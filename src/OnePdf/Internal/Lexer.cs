using System.Buffers;
using System.Text;
using System.Text.Unicode;

namespace OnePdf.Internal;

internal enum TokenKind : byte
{
    Eof,
    Integer,
    Real,
    String,
    Name,
    ArrayOpen,
    ArrayClose,
    DictOpen,
    DictClose,
    Keyword,
    Junk,
}

internal struct Token
{
    public TokenKind Kind;
    public long Start;
    public long Int;
    public double Real;
    public byte[]? Bytes;
    public string? Text;

    public readonly bool IsKeyword(string text) => Kind == TokenKind.Keyword && Text == text;
}

/// <summary>
/// Tokenizer for PDF syntax (ISO 32000-2 7.2) over a positional source, using a small refilled window so that a file is
/// never held whole. Tolerant of the usual real-world damage; it never throws on bad bytes.
/// </summary>
internal sealed class Lexer : IDisposable
{
    private const int WindowSize = 16 * 1024;
    private const int MaxKeywordLength = 256;

    private readonly IPdfSource _source;
    private readonly long _length;
    private byte[]? _buf;
    private long _bufStart;
    private int _bufLen;

    public Lexer(IPdfSource source, long position = 0)
    {
        _source = source;
        _length = source.Length;
        _buf = ArrayPool<byte>.Shared.Rent((int)Math.Min(WindowSize, Math.Max(16, _length)));
        Position = position;
    }

    public long Position { get; set; }

    public long Length => _length;

    public void Dispose()
    {
        byte[]? b = _buf;
        _buf = null;
        if (b is not null)
        {
            ArrayPool<byte>.Shared.Return(b);
        }
    }

    /// <summary>The byte at <paramref name="pos"/>, or -1 outside the data.</summary>
    public int ByteAt(long pos)
    {
        long rel = pos - _bufStart;
        if (rel >= 0 && rel < _bufLen)
        {
            return _buf![(int)rel];
        }

        if (pos < 0 || pos >= _length)
        {
            return -1;
        }

        Fill(pos);
        return _buf![(int)(pos - _bufStart)];
    }

    private void Fill(long pos)
    {
        byte[] buf = _buf ?? throw new ObjectDisposedException(nameof(Lexer));
        long start = Math.Max(0, pos - 256);
        _bufLen = _source.Read(start, buf.AsSpan(0, (int)Math.Min(buf.Length, _length - start)));
        _bufStart = start;
        if (pos - start >= _bufLen)
        {
            // The source returned fewer bytes than its length promised (truncated underneath us).
            _bufLen = 0;
            _source.EnsureUnchanged();
            throw new PdfSourceChangedException("The source returned fewer bytes than it had at open.");
        }
    }

    public static bool IsWhite(int b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    public static bool IsDelimiter(int b) => b is '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';

    public static bool IsRegular(int b) => b >= 0 && !IsWhite(b) && !IsDelimiter(b);

    public void SkipWhiteAndComments()
    {
        long p = Position;
        while (true)
        {
            int b = ByteAt(p);
            if (IsWhite(b))
            {
                p++;
            }
            else if (b == '%')
            {
                while (true)
                {
                    b = ByteAt(++p);
                    if (b is -1 or 10 or 13)
                    {
                        break;
                    }
                }
            }
            else
            {
                break;
            }
        }

        Position = p;
    }

    /// <summary>True when the bytes at <paramref name="pos"/> are exactly <paramref name="ascii"/>.</summary>
    public bool MatchAt(long pos, string ascii)
    {
        for (int i = 0; i < ascii.Length; i++)
        {
            if (ByteAt(pos + i) != ascii[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>First occurrence of <paramref name="pattern"/> in [from, limit), or -1. Scans in chunks without whole-file buffers.</summary>
    public long IndexOf(ReadOnlySpan<byte> pattern, long from, long limit)
    {
        limit = Math.Min(limit, _length);
        if (from < 0)
        {
            from = 0;
        }

        byte[] chunk = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long pos = from;
            while (pos < limit)
            {
                int want = (int)Math.Min(chunk.Length, limit - pos);
                int got = _source.Read(pos, chunk.AsSpan(0, want));
                if (got < pattern.Length)
                {
                    return -1;
                }

                int idx = chunk.AsSpan(0, got).IndexOf(pattern);
                if (idx >= 0)
                {
                    return pos + idx;
                }

                if (pos + got >= limit)
                {
                    return -1;
                }

                pos += got - (pattern.Length - 1);
            }

            return -1;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    public Token NextToken()
    {
        SkipWhiteAndComments();
        long start = Position;
        var t = new Token { Start = start };
        int b = ByteAt(start);
        if (b < 0)
        {
            t.Kind = TokenKind.Eof;
            return t;
        }

        switch (b)
        {
            case '/':
                Position = start + 1;
                t.Kind = TokenKind.Name;
                t.Text = ReadName();
                return t;
            case '(':
                Position = start + 1;
                t.Kind = TokenKind.String;
                t.Bytes = ReadLiteralString();
                return t;
            case '<':
                if (ByteAt(start + 1) == '<')
                {
                    Position = start + 2;
                    t.Kind = TokenKind.DictOpen;
                }
                else
                {
                    Position = start + 1;
                    t.Kind = TokenKind.String;
                    t.Bytes = ReadHexString();
                }

                return t;
            case '>':
                if (ByteAt(start + 1) == '>')
                {
                    Position = start + 2;
                    t.Kind = TokenKind.DictClose;
                }
                else
                {
                    Position = start + 1;
                    t.Kind = TokenKind.Junk;
                }

                return t;
            case '[':
                Position = start + 1;
                t.Kind = TokenKind.ArrayOpen;
                return t;
            case ']':
                Position = start + 1;
                t.Kind = TokenKind.ArrayClose;
                return t;
            case '{':
            case '}':
                Position = start + 1;
                t.Kind = TokenKind.Keyword;
                t.Text = b == '{' ? "{" : "}";
                return t;
            case ')':
                Position = start + 1;
                t.Kind = TokenKind.Junk;
                return t;
            case '+':
            case '-':
            case '.':
            case >= '0' and <= '9':
                ReadNumber(ref t);
                return t;
            default:
                ReadKeyword(ref t);
                return t;
        }
    }

    private void ReadKeyword(ref Token t)
    {
        long p = Position;
        Span<byte> tmp = stackalloc byte[MaxKeywordLength];
        int n = 0;
        while (true)
        {
            int b = ByteAt(p);
            if (!IsRegular(b))
            {
                break;
            }

            if (n < tmp.Length)
            {
                tmp[n++] = (byte)b;
            }

            p++;
        }

        Position = p;
        t.Kind = TokenKind.Keyword;
        t.Text = Intern(tmp[..n]);
    }

    private static string Intern(ReadOnlySpan<byte> s)
    {
        return s.Length switch
        {
            1 when s[0] == 'R' => "R",
            1 when s[0] == 'n' => "n",
            1 when s[0] == 'f' => "f",
            3 when s.SequenceEqual("obj"u8) => "obj",
            4 when s.SequenceEqual("true"u8) => "true",
            4 when s.SequenceEqual("null"u8) => "null",
            4 when s.SequenceEqual("xref"u8) => "xref",
            5 when s.SequenceEqual("false"u8) => "false",
            6 when s.SequenceEqual("endobj"u8) => "endobj",
            6 when s.SequenceEqual("stream"u8) => "stream",
            7 when s.SequenceEqual("trailer"u8) => "trailer",
            9 when s.SequenceEqual("endstream"u8) => "endstream",
            9 when s.SequenceEqual("startxref"u8) => "startxref",
            _ => Encoding.Latin1.GetString(s),
        };
    }

    private void ReadNumber(ref Token t)
    {
        long p = Position;
        bool negative = false;
        bool seenDigit = false;
        bool seenDot = false;
        bool overflow = false;
        ulong intPart = 0;
        double real = 0;
        ulong fracDigits = 0;
        int fracCount = 0;
        double scale = 1;

        while (true)
        {
            int b = ByteAt(p);
            if (b is >= '0' and <= '9')
            {
                seenDigit = true;
                int d = b - '0';
                if (!seenDot)
                {
                    if (intPart > (ulong.MaxValue - 9) / 10)
                    {
                        overflow = true;
                    }
                    else
                    {
                        intPart = (intPart * 10) + (uint)d;
                    }

                    real = (real * 10) + d;
                }
                else if (fracCount < 15)
                {
                    fracDigits = (fracDigits * 10) + (uint)d;
                    fracCount++;
                    scale *= 10;
                }
            }
            else if (b == '.')
            {
                // A second dot ends the useful part; the remaining digits are consumed and ignored.
                seenDot = true;
            }
            else if (b == '-')
            {
                // Only a leading minus means anything ("--5" and "5-3" occur in damaged files).
                if (!seenDigit)
                {
                    negative = true;
                }
            }
            else if (b != '+')
            {
                break;
            }

            p++;
        }

        Position = p;
        if (!seenDot && !overflow && intPart <= long.MaxValue)
        {
            t.Kind = TokenKind.Integer;
            t.Int = negative ? -(long)intPart : (long)intPart;
            return;
        }

        double v = real + (fracDigits / scale);
        t.Kind = TokenKind.Real;
        t.Real = negative ? -v : v;
    }

    private string ReadName()
    {
        long p = Position;
        Span<byte> tmp = stackalloc byte[128];
        byte[]? rented = null;
        int n = 0;
        bool ascii = true;
        while (true)
        {
            int b = ByteAt(p);
            if (!IsRegular(b))
            {
                break;
            }

            p++;
            if (b == '#')
            {
                int h1 = HexVal(ByteAt(p));
                int h2 = h1 >= 0 ? HexVal(ByteAt(p + 1)) : -1;
                if (h2 >= 0)
                {
                    b = (h1 << 4) | h2;
                    p += 2;
                }
            }

            if (n == tmp.Length)
            {
                byte[] bigger = ArrayPool<byte>.Shared.Rent(n * 2);
                tmp.CopyTo(bigger);
                if (rented is not null)
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }

                rented = bigger;
                tmp = bigger;
            }

            tmp[n++] = (byte)b;
            ascii &= b < 0x80;
        }

        Position = p;
        string s = ascii || Utf8.IsValid(tmp[..n]) ? Encoding.UTF8.GetString(tmp[..n]) : Encoding.Latin1.GetString(tmp[..n]);
        if (rented is not null)
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        return s;
    }

    private static int HexVal(int b) => b switch
    {
        >= '0' and <= '9' => b - '0',
        >= 'a' and <= 'f' => b - 'a' + 10,
        >= 'A' and <= 'F' => b - 'A' + 10,
        _ => -1,
    };

    private byte[] ReadLiteralString()
    {
        long p = Position;
        var sb = new ByteBuilder();
        int depth = 1;
        while (true)
        {
            int b = ByteAt(p++);
            if (b < 0)
            {
                break; // unterminated: keep what there is
            }

            if (b == '(')
            {
                depth++;
                sb.Add((byte)b);
            }
            else if (b == ')')
            {
                if (--depth == 0)
                {
                    break;
                }

                sb.Add((byte)b);
            }
            else if (b == '\\')
            {
                int e = ByteAt(p++);
                switch (e)
                {
                    case 'n': sb.Add(10); break;
                    case 'r': sb.Add(13); break;
                    case 't': sb.Add(9); break;
                    case 'b': sb.Add(8); break;
                    case 'f': sb.Add(12); break;
                    case 13:
                        if (ByteAt(p) == 10)
                        {
                            p++;
                        }

                        break;
                    case 10:
                        break;
                    case >= '0' and <= '7':
                        {
                            int v = e - '0';
                            for (int i = 0; i < 2; i++)
                            {
                                int d = ByteAt(p);
                                if (d is >= '0' and <= '7')
                                {
                                    v = (v << 3) | (d - '0');
                                    p++;
                                }
                                else
                                {
                                    break;
                                }
                            }

                            sb.Add((byte)(v & 0xFF));
                            break;
                        }

                    case -1:
                        break;
                    default:
                        sb.Add((byte)e);
                        break;
                }
            }
            else if (b == 13)
            {
                // An end-of-line inside a string reads as a single line feed (7.3.4.2).
                if (ByteAt(p) == 10)
                {
                    p++;
                }

                sb.Add(10);
            }
            else
            {
                sb.Add((byte)b);
            }
        }

        Position = p;
        return sb.ToArray();
    }

    private byte[] ReadHexString()
    {
        long p = Position;
        var sb = new ByteBuilder();
        int hi = -1;
        while (true)
        {
            int b = ByteAt(p++);
            if (b is -1 or '>')
            {
                break;
            }

            int h = HexVal(b);
            if (h < 0)
            {
                continue;
            }

            if (hi < 0)
            {
                hi = h;
            }
            else
            {
                sb.Add((byte)((hi << 4) | h));
                hi = -1;
            }
        }

        if (hi >= 0)
        {
            sb.Add((byte)(hi << 4));
        }

        Position = p;
        return sb.ToArray();
    }
}

/// <summary>A minimal growable byte buffer.</summary>
internal struct ByteBuilder
{
    private byte[]? _buf;
    private int _n;

    public void Add(byte b)
    {
        _buf ??= new byte[16];
        if (_n == _buf.Length)
        {
            Array.Resize(ref _buf, checked(_n * 2));
        }

        _buf[_n++] = b;
    }

    public readonly byte[] ToArray() => _n == 0 ? [] : _buf.AsSpan(0, _n).ToArray();
}
