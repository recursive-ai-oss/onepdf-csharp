using System;
using System.Buffers;
using System.Collections.Generic;

namespace OnePdf.Filters;

/// <summary>
/// Growable output that refuses to grow past the decoded-size cap.
/// <para>
/// Up to <see cref="SmallMax"/> bytes the working array is rented from the shared pool (small arrays are cheap to keep
/// there) and the result is one exact-size copy. Beyond that the output is written into a few plain arrays of growing
/// size that are never pooled, and joined into one exact-size array at the end, so a huge stream never leaves big
/// buffers behind in a pool and never produces doubling garbage: peak memory is about twice the result, briefly.
/// Always <see cref="Dispose"/> it.
/// </para>
/// </summary>
internal sealed class OutputBuffer : IDisposable
{
    // Largest array that is rented from the shared pool.
    private const int SmallMax = 1 << 20;

    /// <summary>Results up to this size are built in pooled arrays; larger ones are worth sizing exactly.</summary>
    public const int PooledLimit = SmallMax;

    // Segments of the large mode grow from 4 MiB, doubling, up to this size.
    private const int FirstSegment = 4 << 20;
    private const int MaxSegment = 32 << 20;

    private readonly long _cap;
    private byte[] _cur;
    private bool _pooled;
    private int _curLen;
    private List<(byte[] Data, int Used)>? _done;
    private int _lastSegment;
    private readonly bool _exact;

    public OutputBuffer(long cap, long initial)
    {
        _cap = Math.Min(Math.Max(cap, 0), Array.MaxLength);
        _cur = ArrayPool<byte>.Shared.Rent((int)Math.Min(Math.Max(initial, 16), Math.Min(_cap, SmallMax)));
        _pooled = true;
    }

    /// <summary>
    /// A buffer for output whose final size is expected to be <paramref name="exactSize"/> (more than <see cref="SmallMax"/>
    /// and within the cap; the caller has checked it is plausible for the input). The whole result is one plain array
    /// allocated up front, and <see cref="DetachArray"/> hands it over without a copy when the output fills it exactly.
    /// Output that turns out longer continues in further segments; shorter output is copied to an exact-size array as usual.
    /// </summary>
    public OutputBuffer(long cap, long initial, long exactSize)
    {
        _cap = Math.Min(Math.Max(cap, 0), Array.MaxLength);
        if (exactSize <= SmallMax || exactSize > _cap)
        {
            _cur = ArrayPool<byte>.Shared.Rent((int)Math.Min(Math.Max(initial, 16), Math.Min(_cap, SmallMax)));
            _pooled = true;
            return;
        }

        _cur = new byte[exactSize];
        _exact = true;
        _lastSegment = FirstSegment / 2;
    }

    /// <summary>Bytes held.</summary>
    public int Length { get; private set; }

    /// <summary>The most bytes this buffer will hold.</summary>
    public long Cap => _cap;

    /// <summary>True once a write was refused because of the cap.</summary>
    public bool Exceeded { get; private set; }

    // Free bytes in the current array, never past the cap.
    private int Room => (int)Math.Min(_cur.Length - _curLen, _cap - Length);

    /// <summary>Free space at the end, growing (within the cap) so at least <paramref name="want"/> bytes are available if possible.</summary>
    public Span<byte> Free(int want)
    {
        // An exact-size array is used to its end before any further segment is started.
        if (_cur.Length - _curLen < want && Length < _cap && !(_exact && _cur.Length > _curLen))
        {
            Grow(want);
        }

        return _cur.AsSpan(_curLen, Room);
    }

    private void Grow(int want)
    {
        long needed = Math.Min((long)Length + want, _cap);
        if (_pooled && needed <= SmallMax)
        {
            // Still small: rent a bigger pooled array and move over.
            byte[] bigger = ArrayPool<byte>.Shared.Rent((int)Math.Min(Math.Max((long)_cur.Length * 2, needed), SmallMax));
            _cur.AsSpan(0, _curLen).CopyTo(bigger);
            ArrayPool<byte>.Shared.Return(_cur);
            _cur = bigger;
            return;
        }

        _done ??= [];
        if (_pooled)
        {
            // Leaving the pool: what was written so far becomes the first segment, an exact copy.
            _done.Add((_cur.AsSpan(0, _curLen).ToArray(), _curLen));
            ArrayPool<byte>.Shared.Return(_cur);
            _pooled = false;
            _lastSegment = FirstSegment / 2;
        }
        else
        {
            _done.Add((_cur, _curLen));
        }

        long size = Math.Min(Math.Max((long)_lastSegment * 2, want), Math.Max(MaxSegment, want));
        size = Math.Min(size, Math.Max(_cap - Length, 1));
        size = Math.Max(size, 1);
        _lastSegment = (int)Math.Min(size, MaxSegment);
        _cur = new byte[size];
        _curLen = 0;
    }

    public void Advance(int n)
    {
        _curLen += n;
        Length += n;
    }

    /// <summary>Appends one byte; false (and <see cref="Exceeded"/>) when the cap is reached.</summary>
    public bool Put(byte b)
    {
        if (Length >= _cap)
        {
            Exceeded = true;
            return false;
        }

        if (_cur.Length == _curLen)
        {
            Grow(1);
        }

        _cur[_curLen++] = b;
        Length++;
        return true;
    }

    /// <summary>Appends <paramref name="count"/> copies of a byte, all or nothing.</summary>
    public bool PutRun(byte b, int count)
    {
        if (Length + (long)count > _cap)
        {
            Exceeded = true;
            return false;
        }

        Free(count).Slice(0, count).Fill(b);
        Advance(count);
        return true;
    }

    /// <summary>Appends a copy of <paramref name="data"/>, all or nothing.</summary>
    public bool Put(ReadOnlySpan<byte> data)
    {
        if (Length + (long)data.Length > _cap)
        {
            Exceeded = true;
            return false;
        }

        data.CopyTo(Free(data.Length));
        Advance(data.Length);
        return true;
    }

    public void MarkExceeded() => Exceeded = true;

    /// <summary>Copies the bytes into one exact-size array and releases everything else.</summary>
    public byte[] DetachArray()
    {
        byte[] result;
        if (Length == 0)
        {
            result = [];
        }
        else if (_done is null && !_pooled && _curLen == _cur.Length)
        {
            result = _cur;
        }
        else if (_done is null)
        {
            result = _cur.AsSpan(0, _curLen).ToArray();
        }
        else
        {
            result = new byte[Length];
            int at = 0;
            foreach ((byte[] data, int used) in _done)
            {
                data.AsSpan(0, used).CopyTo(result.AsSpan(at));
                at += used;
            }

            _cur.AsSpan(0, _curLen).CopyTo(result.AsSpan(at));
        }

        Dispose();
        return result;
    }

    /// <summary>Like <see cref="DetachArray"/>.</summary>
    public ReadOnlyMemory<byte> Detach() => DetachArray();

    /// <summary>Returns a pooled array to the pool and drops the rest; the buffer is empty afterwards.</summary>
    public void Dispose()
    {
        if (_pooled && _cur.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_cur);
        }

        _cur = [];
        _pooled = false;
        _done = null;
        _curLen = 0;
        Length = 0;
    }
}
