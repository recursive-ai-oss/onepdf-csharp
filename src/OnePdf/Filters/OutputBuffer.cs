using System;
using System.Buffers;

namespace OnePdf.Filters;

/// <summary>
/// Growable output that refuses to grow past the decoded-size cap. The working array is rented from the shared pool
/// and growth rents a bigger one, so a decode allocates only the final exact-size result (<see cref="Detach"/>), never
/// the doubling garbage. Always <see cref="Dispose"/> it.
/// </summary>
internal sealed class OutputBuffer : IDisposable
{
    private byte[] _a;
    private readonly long _cap;

    public OutputBuffer(long cap, long initial)
    {
        _cap = Math.Min(Math.Max(cap, 0), Array.MaxLength);
        _a = ArrayPool<byte>.Shared.Rent((int)Math.Min(Math.Max(initial, 16), Math.Min(_cap, InitialMax)));
    }

    // Starting bigger than this only wastes memory on streams that turn out small; growth doubles from here.
    private const long InitialMax = 4L << 20;

    /// <summary>Bytes that may be held right now without growing: the rented array, but never past the cap.</summary>
    private int Room => (int)Math.Min(_a.Length, _cap);

    public int Length { get; private set; }

    /// <summary>The most bytes this buffer will hold.</summary>
    public long Cap => _cap;

    /// <summary>True once a write was refused because of the cap.</summary>
    public bool Exceeded { get; private set; }

    public ReadOnlySpan<byte> Written => _a.AsSpan(0, Length);

    /// <summary>Free space at the end, growing (within the cap) so at least <paramref name="want"/> bytes are available if possible.</summary>
    public Span<byte> Free(int want)
    {
        if (Room - Length < want && Room < _cap)
        {
            long target = Math.Max((long)_a.Length * 2, (long)Length + want);
            target = Math.Min(target, _cap);
            byte[] bigger = ArrayPool<byte>.Shared.Rent((int)target);
            _a.AsSpan(0, Length).CopyTo(bigger);
            ArrayPool<byte>.Shared.Return(_a);
            _a = bigger;
        }

        return _a.AsSpan(Length, Room - Length);
    }

    public void Advance(int n) => Length += n;

    /// <summary>Appends one byte; false (and <see cref="Exceeded"/>) when the cap is reached.</summary>
    public bool Put(byte b)
    {
        if (Length == Room)
        {
            if (Room >= _cap)
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

    /// <summary>Copies the bytes into an exact-size array and gives the working array back to the pool.</summary>
    public ReadOnlyMemory<byte> Detach()
    {
        ReadOnlyMemory<byte> result = Length == 0 ? ReadOnlyMemory<byte>.Empty : _a.AsSpan(0, Length).ToArray();
        Dispose();
        return result;
    }

    /// <summary>Returns the working array to the pool; the buffer is empty afterwards.</summary>
    public void Dispose()
    {
        byte[] a = _a;
        _a = [];
        Length = 0;
        if (a.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(a);
        }
    }

    /// <summary>The raw array and in-place edit access for predictors.</summary>
    public byte[] Buffer => _a;

    public void SetLength(int n) => Length = n;
}
