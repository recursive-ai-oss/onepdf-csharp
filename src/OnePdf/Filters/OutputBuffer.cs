using System;

namespace OnePdf.Filters;

/// <summary>Growable output that refuses to grow past the decoded-size cap.</summary>
internal sealed class OutputBuffer
{
    private byte[] _a;
    private readonly long _cap;

    public OutputBuffer(long cap, long initial)
    {
        _cap = Math.Min(Math.Max(cap, 0), Array.MaxLength);
        _a = new byte[Math.Min(Math.Max(initial, 16), Math.Min(_cap, 1L << 30))];
    }

    public int Length { get; private set; }

    /// <summary>True once a write was refused because of the cap.</summary>
    public bool Exceeded { get; private set; }

    public ReadOnlySpan<byte> Written => _a.AsSpan(0, Length);

    /// <summary>Free space at the end, growing (within the cap) so at least <paramref name="want"/> bytes are available if possible.</summary>
    public Span<byte> Free(int want)
    {
        if (_a.Length - Length < want && _a.Length < _cap)
        {
            long target = Math.Max((long)_a.Length * 2, (long)Length + want);
            target = Math.Min(target, _cap);
            Array.Resize(ref _a, (int)target);
        }

        return _a.AsSpan(Length);
    }

    public void Advance(int n) => Length += n;

    /// <summary>Appends one byte; false (and <see cref="Exceeded"/>) when the cap is reached.</summary>
    public bool Put(byte b)
    {
        if (Length == _a.Length)
        {
            if (_a.Length >= _cap)
            {
                Exceeded = true;
                return false;
            }

            Free(1);
        }

        _a[Length++] = b;
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
        Length += count;
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
        Length += data.Length;
        return true;
    }

    public void MarkExceeded() => Exceeded = true;

    /// <summary>
    /// Hands out the bytes. The array is trimmed only when more than an eighth would be wasted, so large results are not copied
    /// just to drop a little slack.
    /// </summary>
    public ReadOnlyMemory<byte> Detach()
    {
        if (_a.Length - Length > (Length >> 3) + 4096)
        {
            Array.Resize(ref _a, Length);
        }

        return new ReadOnlyMemory<byte>(_a, 0, Length);
    }

    /// <summary>The raw array and in-place edit access for predictors.</summary>
    public byte[] Buffer => _a;

    public void SetLength(int n) => Length = n;
}
