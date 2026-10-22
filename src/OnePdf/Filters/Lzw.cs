using System;
using System.Buffers;
using System.Buffers.Binary;

namespace OnePdf.Filters;

/// <summary>Constants for the LZW decoder (ISO 32000-2 7.4.4).</summary>
internal static class Lzw
{
    public const int MaxCodes = 4096;
    public const int MaxBits = 12;
}

/// <summary>
/// Incremental LZW decoder for the PDF flavour (ISO 32000-2 7.4.4): 9 to 12 bit codes packed most significant bit
/// first, clear = 256, end of data = 257, optional early code-width change. A decoded string that does not fit the output span is
/// held internally, so output can be pulled in any chunk size. Malformed codes throw
/// <see cref="FilterDataException"/>; exceeding the output cap or expansion ratio throws
/// <see cref="FilterLimitException"/>.
/// </summary>
/// <remarks>
/// <para>
/// When the table is full the decoder keeps decoding with the existing entries until a clear code
/// (the "deferred clear" convention), as GIF and TIFF writers in the wild rely on.
/// </para>
/// <para>
/// Table entries are stored in 8-byte chunks: each entry holds the last 1-8 bytes of its string packed in a
/// <see cref="ulong"/> plus a link to the entry for the string without those bytes (always a multiple of 8 long).
/// Emitting a string then costs one table hop and one 8-byte store per 8 bytes instead of one per byte.
/// The same instance can decode many streams: call <see cref="Reset"/> between them.
/// </para>
/// </remarks>
internal sealed class LzwDecoder
{
    private int _early;
    private long _ratioFloor;
    private const int MinSize = 8;
    private int _clear;
    private int _eoi;
    private int _first;
    private long _maxOutput;
    private double _maxRatio;

    // One 16-byte record per table entry keeps everything a code needs in a single cache line.
    private struct Entry
    {
        public ulong Tail;
        public short ChunkPrev;
        public ushort Length;
        public byte First;
    }

    private readonly Entry[] _t = new Entry[Lzw.MaxCodes];
    private readonly byte[] _scratch = new byte[Lzw.MaxCodes + 16];
    private int _pendPos;
    private int _pendLen;

    private ulong _bitbuf;
    private int _bitcount;
    private int _codeSize;
    private int _next;
    private int _prev;
    private bool _done;
    private long _total;
    private long _inTotal;

    /// <summary>Total decoded bytes produced so far.</summary>
    public long TotalOut => _total;

    /// <summary>Starts a new stream with new limits, reusing the tables.</summary>
    /// <param name="earlyChange">The PDF /EarlyChange value: 1 (the PDF default) widens codes one entry early, 0 does not.</param>
    /// <param name="maxOutputBytes">Maximum total decoded bytes.</param>
    /// <param name="maxExpansionRatio">Maximum decoded bytes per input byte once past <paramref name="ratioAppliesAfter"/>; 0 for none.</param>
    /// <param name="ratioAppliesAfter">The ratio is only enforced for output larger than this.</param>
    public void Reset(int earlyChange, long maxOutputBytes = long.MaxValue, double maxExpansionRatio = 0, long ratioAppliesAfter = 0)
    {
        _early = earlyChange == 0 ? 0 : 1;
        ArgumentOutOfRangeException.ThrowIfNegative(maxOutputBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maxExpansionRatio);
        _ratioFloor = Math.Max(0, ratioAppliesAfter);
        _clear = 1 << MinSize;
        _eoi = _clear + 1;
        _first = _clear + 2;
        _maxOutput = maxOutputBytes;
        _maxRatio = double.IsFinite(maxExpansionRatio) ? maxExpansionRatio : 0;
        for (int i = 0; i < _clear; i++)
        {
            _t[i] = new Entry { Tail = (ulong)i, ChunkPrev = -1, First = (byte)i, Length = 1 };
        }

        _pendPos = 0;
        _pendLen = 0;
        _bitbuf = 0;
        _bitcount = 0;
        _done = false;
        _total = 0;
        _inTotal = 0;
        ResetTable();
    }

    private void ResetTable()
    {
        _codeSize = MinSize + 1;
        _next = _first;
        _prev = -1;
    }

    /// <summary>
    /// Decodes input into output. Status semantics match <see cref="Inflater.Inflate"/>: with
    /// <see cref="InflateStatus.Done"/> (end-of-information code read) bytes not reported consumed follow the data.
    /// </summary>
    public InflateStatus Decode(ReadOnlySpan<byte> input, Span<byte> output, out int consumed, out int written)
    {
        int ip = 0;
        int op = 0;
        consumed = 0;
        written = 0;
        long limit = Limit(0);
        try
        {
            while (true)
            {
                if (_pendPos < _pendLen)
                {
                    int n = Math.Min(_pendLen - _pendPos, output.Length - op);
                    _scratch.AsSpan(_pendPos, n).CopyTo(output.Slice(op));
                    _pendPos += n;
                    op += n;
                    if (_pendPos < _pendLen)
                    {
                        _inTotal += ip;
                        consumed = ip;
                        written = op;
                        return InflateStatus.NeedOutput;
                    }
                }

                if (_done)
                {
                    break;
                }

                // Make sure a whole code is buffered.
                if (_bitcount < _codeSize)
                {
                    if (ip <= input.Length - 8)
                    {
                        _bitbuf |= BinaryPrimitives.ReadUInt64BigEndian(input.Slice(ip)) >> _bitcount;

                        ip += (63 - _bitcount) >> 3;
                        _bitcount |= 56;
                    }
                    else
                    {
                        while (_bitcount < _codeSize && ip < input.Length)
                        {
                            ulong b = input[ip++];
                            _bitbuf |= b << (56 - _bitcount);
                            _bitcount += 8;
                        }

                        if (_bitcount < _codeSize)
                        {
                            ClearGarbage();
                            _inTotal += ip;
                            consumed = ip;
                            written = op;
                            return InflateStatus.NeedInput;
                        }
                    }

                    limit = Limit(ip);
                }

                int code = (int)(_bitbuf >> (64 - _codeSize));
                _bitbuf <<= _codeSize;

                _bitcount -= _codeSize;

                if (code == _clear)
                {
                    ResetTable();
                    continue;
                }

                if (code == _eoi)
                {
                    _done = true;
                    continue;
                }

                if (_prev < 0)
                {
                    if (code >= _clear)
                    {
                        throw new FilterDataException("LZW stream does not start with a literal after a clear code.");
                    }
                }
                else
                {
                    if (code > _next || (code == _next && _next >= Lzw.MaxCodes))
                    {
                        throw new FilterDataException("LZW code refers to an entry that does not exist yet.");
                    }

                    if (_next < Lzw.MaxCodes)
                    {
                        // New entry: the previous string plus the first byte of the current one.
                        int e = _next++;
                        ref Entry p = ref _t[_prev];
                        int lp = p.Length;
                        ulong ch = code < e ? _t[code].First : p.First;
                        ref Entry n = ref _t[e];
                        n.First = p.First;
                        n.Length = (ushort)(lp + 1);
                        if ((lp & 7) == 0)
                        {
                            n.ChunkPrev = (short)_prev;
                            n.Tail = ch;
                        }
                        else
                        {
                            n.ChunkPrev = p.ChunkPrev;
                            n.Tail = p.Tail | (ch << (8 * (((lp - 1) & 7) + 1)));
                        }

                        if (_next + _early == (1 << _codeSize) && _codeSize < Lzw.MaxBits)
                        {
                            _codeSize++;
                        }
                    }
                }

                _prev = code;
                int len = _t[code].Length;
                _total += len;
                if (_total > limit)
                {
                    ThrowLimit();
                }

                if (output.Length - op >= len + 8)
                {
                    Emit(code, len, output.Slice(op));
                    op += len;
                }
                else if (output.Length - op >= len)
                {
                    // Too close to the end of the output for the 8-byte stores: build in scratch, then copy.
                    Emit(code, len, _scratch);
                    _scratch.AsSpan(0, len).CopyTo(output.Slice(op));
                    op += len;
                }
                else
                {
                    Emit(code, len, _scratch);
                    _pendPos = 0;
                    _pendLen = len;
                }
            }

        }
        catch (FilterException)
        {
            // Everything emitted before the failure stays valid for the caller.
            consumed = ip;
            written = op;
            throw;
        }

        // End of information: give back whole bytes that were buffered but not needed.
        int back = Math.Min(_bitcount >> 3, ip);
        ip -= back;
        _bitbuf = 0;
        _bitcount = 0;
        _inTotal += ip;
        consumed = ip;
        written = op;
        return InflateStatus.Done;
    }

    // Writes the string for 'code' so that it ends at dst[len]. The first (partial) chunk stores 8 bytes and so
    // scribbles up to 7 bytes past the string; callers pass a span with that much room.
    private void Emit(int code, int len, Span<byte> dst)
    {
        int pos = len - (((len - 1) & 7) + 1);
        Entry[] t = _t;
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(pos), t[code].Tail);
        int c = t[code].ChunkPrev;
        while (c >= 0)
        {
            pos -= 8;
            BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(pos), t[c].Tail);
            c = t[c].ChunkPrev;
        }
    }

    // The most output allowed so far: the output cap, or the ratio budget for the input consumed.
    private long Limit(int ip)
    {
        if (_maxRatio <= 0)
        {
            return _maxOutput;
        }

        double budget = _maxRatio * (_inTotal + ip);
        if (budget >= _maxOutput)
        {
            return _maxOutput;
        }

        return Math.Max((long)budget, Math.Min(_ratioFloor, _maxOutput));
    }

    private void ThrowLimit()
    {
        if (_total > _maxOutput)
        {
            throw new FilterLimitException("Decompressed data exceeds the configured output limit.");
        }

        throw new FilterLimitException("Decompressed data exceeds the configured expansion ratio.");
    }

    private void ClearGarbage()
    {
        // Bits beyond the valid count are copies of upcoming input; drop them so they cannot be confused
        // with data from a later call.
        if (_bitcount == 0)
        {
            _bitbuf = 0;
        }
        else
        {
            _bitbuf &= ~(ulong.MaxValue >> _bitcount);
        }
    }
}

