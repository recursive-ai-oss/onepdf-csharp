using System.Globalization;
using System.Text;
using OnePdf.FontData;

namespace OnePdf.FontPrograms;

/// <summary>
/// Adobe Type 1 font program reader (Adobe Type 1 Font Format, 1990): PFA with hex or binary eexec, PFB segments, and the raw
/// <c>FontFile</c> stream of a PDF (clear text, encrypted portion, trailer). Reads <c>FontName</c>, <c>FontMatrix</c>, <c>FontBBox</c>,
/// the built-in <c>Encoding</c>, <c>Subrs</c> and <c>CharStrings</c>, decrypting with eexec (key 55665) and charstring encryption
/// (key 4330, <c>lenIV</c> leading bytes). Charstrings run in <see cref="Type1Interpreter"/>.
/// </summary>
/// <remarks>
/// The scan is tolerant: <c>RD</c>/<c>-|</c> and <c>ND</c>/<c>|-</c> may be spelled any way, the eexec section may be binary or
/// hexadecimal, lengths from the PDF stream dictionary are not needed, and a missing trailer is fine. Glyph indexes follow the
/// order of the <c>CharStrings</c> dictionary.
/// </remarks>
internal sealed class Type1Font : FontProgram
{
    private static readonly double[] DefaultMatrix = [0.001, 0, 0, 0.001, 0, 0];

    private readonly string[] _glyphNames;
    private readonly byte[][] _charstrings;
    private readonly byte[]?[] _subrs;
    private readonly Dictionary<string, int> _byName;
    private readonly string?[] _encoding;
    private readonly string? _fontName;
    private readonly double[] _matrix;
    private readonly double[]? _bbox;

    private Type1Font(string? fontName, double[] matrix, double[]? bbox, string?[] encoding, byte[]?[] subrs, string[] names, byte[][] charstrings)
    {
        _fontName = fontName;
        _matrix = matrix;
        _bbox = bbox;
        _encoding = encoding;
        _subrs = subrs;
        _glyphNames = names;
        _charstrings = charstrings;
        _byName = new Dictionary<string, int>(names.Length, StringComparer.Ordinal);
        for (int i = 0; i < names.Length; i++)
        {
            _byName.TryAdd(names[i], i);
        }
    }

    /// <inheritdoc />
    public override FontProgramKind Kind => FontProgramKind.Type1;

    /// <inheritdoc />
    public override int GlyphCount => _charstrings.Length;

    /// <inheritdoc />
    public override double[] FontMatrix => _matrix;

    /// <inheritdoc />
    public override double[]? FontBBox => _bbox;

    /// <inheritdoc />
    public override string? FontName => _fontName;

    /// <inheritdoc />
    public override string?[]? BuiltInEncoding => _encoding;

    /// <inheritdoc />
    public override int FindGlyph(string name) => _byName.TryGetValue(name, out int g) ? g : -1;

    /// <inheritdoc />
    public override string? GetGlyphName(int gid) => (uint)gid < (uint)_glyphNames.Length ? _glyphNames[gid] : null;

    /// <inheritdoc />
    public override bool TryGetAdvance(int gid, out float advance)
    {
        advance = 0;
        if ((uint)gid >= (uint)_charstrings.Length)
        {
            return false;
        }

        Type1Result r = new Type1Interpreter(_subrs, widthOnly: true).Run(_charstrings[gid]);
        advance = r.Width;
        return r.HasMetrics;
    }

    /// <inheritdoc />
    public override GlyphOutline GetOutline(int gid)
    {
        if ((uint)gid >= (uint)_charstrings.Length)
        {
            return GlyphOutline.Empty;
        }

        Type1Result r = new Type1Interpreter(_subrs, widthOnly: false).Run(_charstrings[gid]);
        if (!r.IsAccented)
        {
            return r.Outline;
        }

        // seac (Type 1 Font Format 6.4): base and accent are StandardEncoding characters; the accent's origin is moved so that its
        // left sidebearing point lands at (adx, ady) relative to the composite's left sidebearing point.
        int bg = FindStandard(r.BaseChar);
        int ag = FindStandard(r.AccentChar);
        GlyphOutline b = GlyphOutline.Empty;
        GlyphOutline a = GlyphOutline.Empty;
        if (bg >= 0)
        {
            Type1Result br = new Type1Interpreter(_subrs, widthOnly: false).Run(_charstrings[bg]);
            b = br.IsAccented ? throw new FontProgramException("Accent composition is nested.") : br.Outline;
        }

        if (ag >= 0)
        {
            Type1Result ar = new Type1Interpreter(_subrs, widthOnly: false).Run(_charstrings[ag]);
            a = ar.IsAccented ? throw new FontProgramException("Accent composition is nested.") : ar.Outline;
            a = a.Translated(r.Sbx + r.AccentDx - r.AccentAsb, r.AccentDy);
        }

        return GlyphOutline.Combine(b, a);
    }

    private int FindStandard(int code)
    {
        string? n = (uint)code < 256 ? Encodings.GetName(PredefinedEncoding.Standard, code) : null;
        return n is null ? -1 : FindGlyph(n);
    }

    /// <summary>Parses a Type 1 font from PFA, PFB or PDF <c>FontFile</c> bytes.</summary>
    /// <exception cref="FontProgramException">No charstrings could be found.</exception>
    public static Type1Font Parse(ReadOnlySpan<byte> data)
    {
        byte[] flat = data.Length > 0 && data[0] == 0x80 ? FlattenPfb(data) : data.ToArray();
        int eexec = FindEexec(flat);
        ReadOnlySpan<byte> clear = eexec >= 0 ? flat.AsSpan(0, eexec) : flat;
        byte[] priv = eexec >= 0 ? DecryptEexec(flat.AsSpan(eexec + 5)) : flat;

        var clearScan = new PsScanner(clear);
        string? fontName = null;
        double[] matrix = DefaultMatrix;
        double[]? bbox = null;
        string?[] encoding = new string?[256];
        bool haveEncoding = false;
        while (clearScan.Next(out PsToken t))
        {
            if (t.Kind != PsKind.Name || t.IsExecutable)
            {
                continue;
            }

            switch (t.Text)
            {
                case "FontName":
                    if (clearScan.Next(out PsToken n) && n.Kind == PsKind.Name)
                    {
                        fontName = n.Text;
                    }

                    break;
                case "FontMatrix":
                    double[]? m = ReadNumberArray(ref clearScan, 6);
                    if (m is not null && m[0] != 0 && m[3] != 0)
                    {
                        matrix = m;
                    }

                    break;
                case "FontBBox":
                    bbox = ReadNumberArray(ref clearScan, 4);
                    break;
                case "Encoding":
                    haveEncoding = ReadEncoding(ref clearScan, encoding);
                    break;
            }
        }

        // Some fonts keep Subrs and CharStrings in clear text; scanning the private part covers both layouts.
        ReadPrivate(priv, out byte[]?[] subrs, out string[] names, out byte[][] charstrings);
        if (charstrings.Length == 0)
        {
            throw new FontProgramException("The Type 1 font has no CharStrings.");
        }

        if (!haveEncoding)
        {
            Array.Copy(Encodings.Get(PredefinedEncoding.Standard), encoding, 256);
        }

        return new Type1Font(fontName, matrix, bbox, encoding, subrs, names, charstrings);
    }

    // PFB (Adobe Type 1 Font Format, Appendix: PFB): segments of 0x80, type (1 ASCII, 2 binary, 3 EOF) and a little-endian length.
    private static byte[] FlattenPfb(ReadOnlySpan<byte> d)
    {
        using var ms = new MemoryStream();
        int p = 0;
        while (p + 6 <= d.Length && d[p] == 0x80)
        {
            int type = d[p + 1];
            if (type == 3)
            {
                break;
            }

            if (type is not (1 or 2))
            {
                throw new FontProgramException("Invalid PFB segment type.");
            }

            long len = d[p + 2] | ((long)d[p + 3] << 8) | ((long)d[p + 4] << 16) | ((long)d[p + 5] << 24);
            p += 6;
            len = Math.Min(len, d.Length - p);
            if (ms.Length + len > FontLimits.MaxType1DecryptedBytes)
            {
                throw new FontProgramException("The Type 1 font is too large.");
            }

            ms.Write(d.Slice(p, (int)len));
            p += (int)len;
        }

        return ms.ToArray();
    }

    private static int FindEexec(byte[] d)
    {
        ReadOnlySpan<byte> s = d;
        int from = 0;
        while (from < s.Length)
        {
            int i = s[from..].IndexOf("eexec"u8);
            if (i < 0)
            {
                return -1;
            }

            i += from;
            bool startOk = i == 0 || IsSpace(s[i - 1]);
            bool endOk = i + 5 >= s.Length || IsSpace(s[i + 5]);
            if (startOk && endOk)
            {
                return i;
            }

            from = i + 5;
        }

        return -1;
    }

    private static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private static byte[] DecryptEexec(ReadOnlySpan<byte> afterKeyword)
    {
        int p = 0;
        while (p < afterKeyword.Length && IsSpace(afterKeyword[p]))
        {
            p++;
        }

        ReadOnlySpan<byte> cipher = afterKeyword[p..];
        if (cipher.Length > FontLimits.MaxType1DecryptedBytes)
        {
            throw new FontProgramException("The Type 1 font is too large.");
        }

        byte[] bytes;
        bool hex = cipher.Length >= 4;
        for (int i = 0; hex && i < 4; i++)
        {
            hex = Uri.IsHexDigit((char)cipher[i]);
        }

        if (hex)
        {
            // ASCII form: pairs of hex digits, white space ignored; stops at the first other byte.
            var list = new List<byte>(cipher.Length / 2);
            int hi = -1;
            foreach (byte c in cipher)
            {
                int v = HexValue(c);
                if (v < 0)
                {
                    if (IsSpace(c))
                    {
                        continue;
                    }

                    break;
                }

                if (hi < 0)
                {
                    hi = v;
                }
                else
                {
                    list.Add((byte)((hi << 4) | v));
                    hi = -1;
                }
            }

            bytes = [.. list];
        }
        else
        {
            bytes = cipher.ToArray();
        }

        Decrypt(bytes, 55665, bytes);
        return bytes.Length >= 4 ? bytes[4..] : [];
    }

    private static int HexValue(byte c) => c switch
    {
        >= (byte)'0' and <= (byte)'9' => c - '0',
        >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
        _ => -1,
    };

    // Type 1 Font Format 7.2: plain = cipher XOR (r >> 8); r = (cipher + r) * 52845 + 22719, 16-bit. Works in place.
    private static void Decrypt(ReadOnlySpan<byte> input, ushort key, Span<byte> output)
    {
        uint r = key;
        for (int i = 0; i < input.Length; i++)
        {
            byte c = input[i];
            output[i] = (byte)(c ^ (r >> 8));
            r = (((c + r) * 52845u) + 22719u) & 0xFFFF;
        }
    }

    private static double[]? ReadNumberArray(ref PsScanner s, int count)
    {
        if (!s.Next(out PsToken open) || open.Kind != PsKind.Open)
        {
            return null;
        }

        var v = new List<double>(count);
        while (s.Next(out PsToken t))
        {
            if (t.Kind == PsKind.Close)
            {
                break;
            }

            if (t.Kind != PsKind.Number)
            {
                return null;
            }

            v.Add(t.Number);
            if (v.Count > 32)
            {
                return null;
            }
        }

        return v.Count >= count ? [.. v.GetRange(0, count)] : null;
    }

    private static bool ReadEncoding(ref PsScanner s, string?[] encoding)
    {
        if (!s.Next(out PsToken first))
        {
            return false;
        }

        if (first.Kind == PsKind.Name && first.Text == "StandardEncoding")
        {
            Array.Copy(Encodings.Get(PredefinedEncoding.Standard), encoding, 256);
            return true;
        }

        if (first.Kind != PsKind.Number)
        {
            return false;
        }

        // "256 array 0 1 255 {1 index exch /.notdef put} for  dup 32 /space put ... readonly def"
        int guard = 0;
        while (s.Next(out PsToken t) && guard++ < 4096)
        {
            if (t.Kind == PsKind.Name && (t.Text == "def" || t.Text == "readonly") && t.IsExecutable)
            {
                // "for" loops contain no def; the first def ends the table.
                break;
            }

            if (t.Kind == PsKind.Name && t.Text == "dup" && t.IsExecutable)
            {
                if (s.Next(out PsToken code) && code.Kind == PsKind.Number && s.Next(out PsToken name) && name.Kind == PsKind.Name && !name.IsExecutable)
                {
                    int c = (int)code.Number;
                    if ((uint)c < 256)
                    {
                        encoding[c] = name.Text;
                    }
                }
            }
        }

        return true;
    }

    private static void ReadPrivate(byte[] priv, out byte[]?[] subrs, out string[] names, out byte[][] charstrings)
    {
        int lenIV = 4;
        subrs = [];
        var nameList = new List<string>();
        var csList = new List<byte[]>();
        var s = new PsScanner(priv);
        while (s.Next(out PsToken t))
        {
            if (t.Kind == PsKind.Number || t.Kind != PsKind.Name || t.IsExecutable)
            {
                continue;
            }

            if (t.Text == "lenIV" && s.Next(out PsToken v) && v.Kind == PsKind.Number)
            {
                lenIV = (int)v.Number;
            }
            else if (t.Text == "Subrs" && s.Next(out PsToken count) && count.Kind == PsKind.Number)
            {
                int n = (int)Math.Clamp(count.Number, 0, 65536);
                subrs = new byte[]?[n];
                ReadSubrs(ref s, subrs, lenIV);
            }
            else if (t.Text == "CharStrings" && csList.Count == 0)
            {
                // Subrs may follow CharStrings in odd fonts, so the scan goes on to the end of the private part.
                ReadCharStrings(ref s, nameList, csList, lenIV);
            }
        }

        names = [.. nameList];
        charstrings = [.. csList];
    }

    private static void ReadSubrs(ref PsScanner s, byte[]?[] subrs, int lenIV)
    {
        while (true)
        {
            int mark = s.Position;
            if (!s.Next(out PsToken t))
            {
                return;
            }

            if (t.Kind == PsKind.Name && !t.IsExecutable)
            {
                // The next dictionary entry (/CharStrings, /OtherSubrs) ends the array.
                s.Position = mark;
                return;
            }

            if (t.Kind == PsKind.Name && t.Text == "dup" && t.IsExecutable)
            {
                if (s.Next(out PsToken idx) && idx.Kind == PsKind.Number && s.Next(out PsToken len) && len.Kind == PsKind.Number && s.Next(out PsToken rd) && rd.Kind == PsKind.Name)
                {
                    byte[]? data = s.ReadBinary((int)len.Number);
                    if (data is null)
                    {
                        return;
                    }

                    int i = (int)idx.Number;
                    if ((uint)i < (uint)subrs.Length)
                    {
                        subrs[i] = DecryptCharstring(data, lenIV);
                    }
                }
            }
        }
    }

    private static void ReadCharStrings(ref PsScanner s, List<string> names, List<byte[]> data, int lenIV)
    {
        while (s.Next(out PsToken t))
        {
            if (t.Kind == PsKind.Name && t.IsExecutable && t.Text == "end")
            {
                if (data.Count > 0)
                {
                    return;
                }

                continue;
            }

            if (t.Kind != PsKind.Name || t.IsExecutable)
            {
                continue;
            }

            if (s.Next(out PsToken len) && len.Kind == PsKind.Number && s.Next(out PsToken rd) && rd.Kind == PsKind.Name && rd.IsExecutable)
            {
                byte[]? bytes = s.ReadBinary((int)len.Number);
                if (bytes is null)
                {
                    return;
                }

                if (data.Count >= FontLimits.MaxGlyphs)
                {
                    throw new FontProgramException("The Type 1 font has too many glyphs.");
                }

                names.Add(t.Text);
                data.Add(DecryptCharstring(bytes, lenIV));
            }
        }
    }

    private static byte[] DecryptCharstring(byte[] cipher, int lenIV)
    {
        if (lenIV < 0)
        {
            return cipher;
        }

        var plain = new byte[cipher.Length];
        Decrypt(cipher, 4330, plain);
        return lenIV >= plain.Length ? [] : plain[lenIV..];
    }
}

internal enum PsKind
{
    Name,
    Number,
    Open,
    Close,
    Other,
}

internal readonly record struct PsToken(PsKind Kind, string Text, double Number, bool IsExecutable);

/// <summary>A small PostScript token scanner, enough to walk a Type 1 font's dictionaries and to step over binary charstring data.</summary>
internal ref struct PsScanner
{
    private readonly ReadOnlySpan<byte> _d;

    public PsScanner(ReadOnlySpan<byte> data)
    {
        _d = data;
        Position = 0;
    }

    public int Position { get; set; }

    private static bool IsWhite(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or 0;

    private static bool IsDelim(byte b) => b is (byte)'/' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'%';

    /// <summary>
    /// Reads the next token. Literal names ("/Foo") come back with IsExecutable false and the slash removed; bare words
    /// (keywords) have IsExecutable true. Strings and procedures' braces are returned as Other, Open and Close.
    /// </summary>
    public bool Next(out PsToken token)
    {
        token = default;
        while (Position < _d.Length)
        {
            byte b = _d[Position];
            if (IsWhite(b))
            {
                Position++;
            }
            else if (b == (byte)'%')
            {
                while (Position < _d.Length && _d[Position] is not ((byte)'\n' or (byte)'\r'))
                {
                    Position++;
                }
            }
            else
            {
                break;
            }
        }

        if (Position >= _d.Length)
        {
            return false;
        }

        byte c = _d[Position];
        switch (c)
        {
            case (byte)'[':
            case (byte)'{':
                Position++;
                token = new PsToken(PsKind.Open, ((char)c).ToString(), 0, true);
                return true;
            case (byte)']':
            case (byte)'}':
                Position++;
                token = new PsToken(PsKind.Close, ((char)c).ToString(), 0, true);
                return true;
            case (byte)'(':
            {
                int depth = 0;
                int start = Position;
                while (Position < _d.Length)
                {
                    byte x = _d[Position++];
                    if (x == (byte)'\\')
                    {
                        Position++;
                    }
                    else if (x == (byte)'(')
                    {
                        depth++;
                    }
                    else if (x == (byte)')' && --depth == 0)
                    {
                        break;
                    }
                }

                token = new PsToken(PsKind.Other, Encoding.Latin1.GetString(_d.Slice(start, Math.Min(Position, _d.Length) - start)), 0, false);
                return true;
            }

            case (byte)'<':
            case (byte)'>':
                Position++;
                if (Position < _d.Length && _d[Position] == c)
                {
                    Position++;
                }
                else if (c == (byte)'<')
                {
                    while (Position < _d.Length && _d[Position] != (byte)'>')
                    {
                        Position++;
                    }

                    Position++;
                }

                token = new PsToken(PsKind.Other, "<>", 0, false);
                return true;
            case (byte)')':
                Position++;
                token = new PsToken(PsKind.Other, ")", 0, false);
                return true;
        }

        bool literal = false;
        if (c == (byte)'/')
        {
            literal = true;
            Position++;
        }

        int s = Position;
        while (Position < _d.Length && !IsWhite(_d[Position]) && !IsDelim(_d[Position]))
        {
            Position++;
        }

        string text = Encoding.Latin1.GetString(_d.Slice(s, Position - s));
        if (!literal && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double num) && double.IsFinite(num))
        {
            token = new PsToken(PsKind.Number, text, num, false);
        }
        else if (!literal && text.Contains('#', StringComparison.Ordinal) && TryRadix(text, out double rn))
        {
            token = new PsToken(PsKind.Number, text, rn, false);
        }
        else
        {
            token = new PsToken(PsKind.Name, text, 0, !literal);
        }

        return true;
    }

    // "16#FF": PostScript radix numbers appear in a few Type 1 fonts.
    private static bool TryRadix(string text, out double value)
    {
        value = 0;
        int h = text.IndexOf('#', StringComparison.Ordinal);
        if (!int.TryParse(text.AsSpan(0, h), NumberStyles.None, CultureInfo.InvariantCulture, out int radix) || radix < 2 || radix > 36)
        {
            return false;
        }

        long v = 0;
        for (int i = h + 1; i < text.Length; i++)
        {
            int d = char.IsAsciiDigit(text[i]) ? text[i] - '0' : (char.IsAsciiLetter(text[i]) ? char.ToLowerInvariant(text[i]) - 'a' + 10 : 99);
            if (d >= radix)
            {
                return false;
            }

            v = (v * radix) + d;
            if (v > int.MaxValue)
            {
                return false;
            }
        }

        value = v;
        return true;
    }

    /// <summary>After an <c>RD</c>-style token: skips the single separator byte and returns the next <paramref name="length"/> bytes, or null when they do not fit.</summary>
    public byte[]? ReadBinary(int length)
    {
        if (length < 0 || Position >= _d.Length)
        {
            return null;
        }

        Position++; // exactly one white-space character separates RD from the data
        if (length > _d.Length - Position)
        {
            return null;
        }

        byte[] r = _d.Slice(Position, length).ToArray();
        Position += length;
        return r;
    }
}
