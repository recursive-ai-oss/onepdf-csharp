using System.Globalization;

namespace OnePdf.Writing;

/// <summary>A buffered writer that counts bytes, so offsets are known without seeking in the output.</summary>
internal sealed class ByteSink
{
    private readonly Stream _out;
    private readonly byte[] _buf;
    private int _n;
    private long _flushed;

    public ByteSink(Stream output, long startPosition = 0, int bufferSize = 1 << 16)
    {
        _out = output;
        _buf = new byte[bufferSize];
        _flushed = startPosition;
    }

    /// <summary>Number of bytes written so far, counting from the start position.</summary>
    public long Position => _flushed + _n;

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.Length >= _buf.Length)
        {
            FlushBuffer();
            _out.Write(data);
            _flushed += data.Length;
            return;
        }

        if (data.Length > _buf.Length - _n)
        {
            FlushBuffer();
        }

        data.CopyTo(_buf.AsSpan(_n));
        _n += data.Length;
    }

    public void Byte(byte b)
    {
        if (_n == _buf.Length)
        {
            FlushBuffer();
        }

        _buf[_n++] = b;
    }

    /// <summary>Writes the characters of an ASCII-only string.</summary>
    public void Ascii(string s)
    {
        foreach (char c in s)
        {
            Byte((byte)c);
        }
    }

    public void Long(long v)
    {
        Span<byte> tmp = stackalloc byte[24];
        v.TryFormat(tmp, out int len, default, CultureInfo.InvariantCulture);
        Write(tmp[..len]);
    }

    /// <summary>A non-negative number as exactly <paramref name="width"/> decimal digits (an xref table field).</summary>
    public void Padded(long v, int width)
    {
        Span<byte> tmp = stackalloc byte[24];
        for (int i = width - 1; i >= 0; i--)
        {
            tmp[i] = (byte)('0' + (int)(v % 10));
            v /= 10;
        }

        Write(tmp[..width]);
    }

    public void Flush()
    {
        FlushBuffer();
        _out.Flush();
    }

    private void FlushBuffer()
    {
        if (_n > 0)
        {
            _out.Write(_buf, 0, _n);
            _flushed += _n;
            _n = 0;
        }
    }
}
