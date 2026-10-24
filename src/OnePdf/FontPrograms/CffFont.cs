using System.Globalization;
using OnePdf.FontData;

namespace OnePdf.FontPrograms;

/// <summary>
/// Compact Font Format (Adobe Technical Note #5176) reader with a Type 2 charstring interpreter (TN #5177): name-keyed and
/// CID-keyed fonts, charsets, encodings, local and global subroutines, hints (skipped), flex, arithmetic operators and the
/// <c>endchar</c> accent composition. The interpreter bounds operand stack, subroutine depth and operator count, so a hostile
/// charstring cannot loop or overrun. Unhinted; outlines are in glyph space.
/// </summary>
/// <remarks>
/// Based on the CFF reader in OneImage.Text, extended with charsets, encodings, names, widths, CID mapping and seac-style endchar.
/// When a CID-keyed font's font dictionaries carry their own <c>FontMatrix</c> and the top dictionary has none, the first
/// dictionary's matrix is used for the whole font.
/// </remarks>
internal sealed class CffFont : FontProgram
{
    private static readonly double[] DefaultMatrix = [0.001, 0, 0, 0.001, 0, 0];

    private readonly ReadOnlyMemory<byte> _data;
    private readonly CffIndex _charStrings;
    private readonly CffIndex _globalSubrs;
    private readonly CffIndex _strings;
    private readonly FontDict[] _fontDicts;
    private readonly ReadOnlyMemory<byte> _fdSelect;
    private readonly int[] _charset; // per GID: SID (name-keyed) or CID (CID-keyed); _charset[0] = 0
    private readonly int _encodingOffset;
    private readonly string? _name;
    private readonly double[] _matrix;
    private readonly double[]? _bbox;
    private readonly Lazy<Dictionary<string, int>> _glyphsByName;
    private readonly Lazy<Dictionary<int, int>> _glyphsByCid;
    private readonly Lazy<string?[]> _encoding;

    /// <summary>Parses the CFF data (a FontSet; the first font is used).</summary>
    /// <exception cref="FontProgramException">The data is not a CFF 1 font or is damaged beyond use.</exception>
    public CffFont(ReadOnlyMemory<byte> cff)
    {
        _data = cff;
        ReadOnlySpan<byte> d = cff.Span;
        if (Be.U8(d, 0) != 1)
        {
            throw new FontProgramException("Only CFF version 1 is supported.");
        }

        int hdrSize = Be.U8(d, 2);
        CffIndex nameIndex = CffIndex.Read(d, hdrSize, out int pos);
        CffIndex topIndex = CffIndex.Read(d, pos, out pos);
        _strings = CffIndex.Read(d, pos, out pos);
        _globalSubrs = CffIndex.Read(d, pos, out _);
        if (topIndex.Count < 1)
        {
            throw new FontProgramException("The CFF font has no top dictionary.");
        }

        if (nameIndex.Count >= 1)
        {
            ReadOnlySpan<byte> nm = nameIndex.Get(d, 0);
            _name = System.Text.Encoding.Latin1.GetString(nm);
        }

        Dictionary<int, double[]> top = ParseDict(topIndex.Get(d, 0));
        if (top.TryGetValue(0x0C06, out double[]? ct) && ct.Length > 0 && ct[0] != 2)
        {
            throw new FontProgramException("CFF fonts with Type 1 charstrings are not supported.");
        }

        if (!top.TryGetValue(17, out double[]? cs) || cs.Length < 1)
        {
            throw new FontProgramException("The CFF font has no CharStrings.");
        }

        _charStrings = CffIndex.Read(d, ToOffset(cs[0]), out _);
        GlyphCount = _charStrings.Count;
        if (GlyphCount > FontLimits.MaxGlyphs)
        {
            throw new FontProgramException("The CFF font has too many glyphs.");
        }

        if (top.TryGetValue(5, out double[]? bb) && bb.Length >= 4)
        {
            _bbox = [bb[0], bb[1], bb[2], bb[3]];
        }

        double[]? fdMatrix = null;
        IsCidKeyed = top.ContainsKey(0x0C1E) || top.ContainsKey(0x0C24);
        if (top.TryGetValue(0x0C24, out double[]? fdArray) && fdArray.Length > 0)
        {
            // CID-keyed: one private dictionary per font dict, glyphs choose theirs through FDSelect.
            CffIndex fds = CffIndex.Read(d, ToOffset(fdArray[0]), out _);
            if (fds.Count == 0 || fds.Count > FontLimits.MaxFontDicts)
            {
                throw new FontProgramException("The CFF font dictionary array is invalid.");
            }

            _fontDicts = new FontDict[fds.Count];
            for (int i = 0; i < fds.Count; i++)
            {
                Dictionary<int, double[]> fd = ParseDict(fds.Get(d, i));
                _fontDicts[i] = ReadPrivate(d, fd);
                if (i == 0 && fd.TryGetValue(0x0C07, out double[]? m) && m.Length >= 6)
                {
                    fdMatrix = [m[0], m[1], m[2], m[3], m[4], m[5]];
                }
            }

            if (!top.TryGetValue(0x0C25, out double[]? fdSel) || fdSel.Length < 1)
            {
                throw new FontProgramException("A CID-keyed CFF font has no FDSelect.");
            }

            int selOff = ToOffset(fdSel[0]);
            if (selOff >= cff.Length)
            {
                throw Be.Truncated();
            }

            _fdSelect = cff[selOff..];
            ValidateFdSelect(_fdSelect.Span);
        }
        else
        {
            _fontDicts = [ReadPrivate(d, top)];
            _fdSelect = default;
        }

        if (top.TryGetValue(0x0C07, out double[]? fm) && fm.Length >= 6)
        {
            _matrix = [fm[0], fm[1], fm[2], fm[3], fm[4], fm[5]];
        }
        else
        {
            _matrix = fdMatrix ?? DefaultMatrix;
        }

        int charsetOffset = top.TryGetValue(15, out double[]? csOff) && csOff.Length > 0 ? ToOffset(csOff[0]) : 0;
        _charset = ReadCharset(d, charsetOffset);
        _encodingOffset = top.TryGetValue(16, out double[]? enc) && enc.Length > 0 ? ToOffset(enc[0]) : 0;

        _glyphsByName = new Lazy<Dictionary<string, int>>(BuildNames, LazyThreadSafetyMode.ExecutionAndPublication);
        _glyphsByCid = new Lazy<Dictionary<int, int>>(BuildCids, LazyThreadSafetyMode.ExecutionAndPublication);
        _encoding = new Lazy<string?[]>(BuildEncoding, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public override FontProgramKind Kind => FontProgramKind.Cff;

    /// <inheritdoc />
    public override int GlyphCount { get; }

    /// <inheritdoc />
    public override double[] FontMatrix => _matrix;

    /// <inheritdoc />
    public override double[]? FontBBox => _bbox;

    /// <inheritdoc />
    public override bool IsCidKeyed { get; }

    /// <inheritdoc />
    public override string? FontName => _name;

    /// <inheritdoc />
    public override string?[]? BuiltInEncoding => IsCidKeyed ? null : _encoding.Value;

    /// <inheritdoc />
    public override GlyphOutline GetOutline(int gid) => GetOutline(gid, 0);

    /// <inheritdoc />
    public override bool TryGetAdvance(int gid, out float advance)
    {
        advance = 0;
        if ((uint)gid >= (uint)GlyphCount)
        {
            return false;
        }

        advance = Execute(gid, widthOnly: true).Width;
        return true;
    }

    /// <inheritdoc />
    public override int FindGlyph(string name) => !IsCidKeyed && _glyphsByName.Value.TryGetValue(name, out int g) ? g : -1;

    /// <inheritdoc />
    public override string? GetGlyphName(int gid) => !IsCidKeyed && (uint)gid < (uint)_charset.Length ? SidString(_charset[gid]) : null;

    /// <inheritdoc />
    public override int GlyphForCid(int cid)
    {
        if (!IsCidKeyed)
        {
            return base.GlyphForCid(cid);
        }

        return _glyphsByCid.Value.TryGetValue(cid, out int g) ? g : -1;
    }

    /// <summary>The CID of a glyph in a CID-keyed font (the GID itself otherwise).</summary>
    public int CidOfGlyph(int gid) => IsCidKeyed && (uint)gid < (uint)_charset.Length ? _charset[gid] : gid;

    private GlyphOutline GetOutline(int gid, int seacDepth)
    {
        if ((uint)gid >= (uint)GlyphCount)
        {
            return GlyphOutline.Empty;
        }

        CharstringResult r = Execute(gid, widthOnly: false);
        if (!r.IsAccented)
        {
            return r.Outline;
        }

        // endchar with four arguments composes an accented character from two StandardEncoding characters (TN 5177, Appendix C).
        if (seacDepth > 0)
        {
            throw new FontProgramException("Accent composition is nested.");
        }

        int bg = FindStandard(r.BaseChar);
        int ag = FindStandard(r.AccentChar);
        GlyphOutline b = bg >= 0 ? GetOutline(bg, seacDepth + 1) : GlyphOutline.Empty;
        GlyphOutline a = ag >= 0 ? GetOutline(ag, seacDepth + 1).Translated(r.AccentDx, r.AccentDy) : GlyphOutline.Empty;
        return GlyphOutline.Combine(b, a);
    }

    /// <summary>True when the glyph's charstring ends in <c>endchar</c> with accent arguments (the deprecated form that composes two StandardEncoding characters).</summary>
    internal bool IsAccented(int gid) => (uint)gid < (uint)GlyphCount && Execute(gid, widthOnly: false).IsAccented;

    private int FindStandard(int code)
    {
        string? n = (uint)code < 256 ? Encodings.GetName(PredefinedEncoding.Standard, code) : null;
        return n is null ? -1 : FindGlyph(n);
    }

    private CharstringResult Execute(int gid, bool widthOnly)
    {
        ReadOnlySpan<byte> d = _data.Span;
        ReadOnlyMemory<byte> code = _charStrings.GetMemory(d, _data, gid);
        FontDict fd = _fontDicts[SelectFd(gid)];
        return new Type2Interpreter(_data, _globalSubrs, fd, widthOnly).Run(code);
    }

    private string? SidString(int sid)
    {
        if (sid < CffStandardData.StandardStringCount)
        {
            return sid >= 0 ? CffStandardData.StandardString(sid) : null;
        }

        int i = sid - CffStandardData.StandardStringCount;
        if (i >= _strings.Count)
        {
            return null;
        }

        try
        {
            return System.Text.Encoding.Latin1.GetString(_strings.Get(_data.Span, i));
        }
        catch (FontProgramException)
        {
            return null; // a damaged String INDEX leaves custom glyph names unknown
        }
    }

    private Dictionary<string, int> BuildNames()
    {
        var map = new Dictionary<string, int>(GlyphCount, StringComparer.Ordinal);
        for (int g = 0; g < _charset.Length; g++)
        {
            string? n = g == 0 ? ".notdef" : SidString(_charset[g]);
            if (n is not null)
            {
                map.TryAdd(n, g);
            }
        }

        return map;
    }

    private Dictionary<int, int> BuildCids()
    {
        var map = new Dictionary<int, int>(GlyphCount);
        for (int g = 0; g < _charset.Length; g++)
        {
            map.TryAdd(_charset[g], g);
        }

        return map;
    }

    private string?[] BuildEncoding()
    {
        var result = new string?[256];
        ReadOnlySpan<byte> d = _data.Span;
        if (_encodingOffset == 0 || _encodingOffset == 1)
        {
            for (int c = 0; c < 256; c++)
            {
                string? n;
                if (_encodingOffset == 0)
                {
                    n = Encodings.GetName(PredefinedEncoding.Standard, c);
                }
                else
                {
                    int sid = CffStandardData.ExpertEncoding[c];
                    n = sid == 0 ? null : CffStandardData.StandardString(sid);
                }

                if (n is not null && FindGlyph(n) >= 0)
                {
                    result[c] = n;
                }
            }

            return result;
        }

        try
        {
            int p = _encodingOffset;
            int format = Be.U8(d, p++);
            if ((format & 0x7F) == 0)
            {
                int n = Be.U8(d, p++);
                for (int i = 0; i < n; i++)
                {
                    int code = Be.U8(d, p++);
                    result[code] = GetGlyphName(i + 1);
                }
            }
            else if ((format & 0x7F) == 1)
            {
                int nRanges = Be.U8(d, p++);
                int gid = 1;
                for (int i = 0; i < nRanges; i++)
                {
                    int first = Be.U8(d, p++);
                    int left = Be.U8(d, p++);
                    for (int k = 0; k <= left; k++, gid++)
                    {
                        if (first + k < 256)
                        {
                            result[first + k] = GetGlyphName(gid);
                        }
                    }
                }
            }

            if ((format & 0x80) != 0)
            {
                int nSups = Be.U8(d, p++);
                for (int i = 0; i < nSups; i++)
                {
                    int code = Be.U8(d, p++);
                    int sid = Be.U16(d, p);
                    p += 2;
                    result[code] = SidString(sid);
                }
            }
        }
        catch (FontProgramException)
        {
            // A damaged encoding table leaves the codes read so far; the PDF font's own /Encoding usually supplies the rest.
        }

        return result;
    }

    private int[] ReadCharset(ReadOnlySpan<byte> d, int offset)
    {
        var cs = new int[GlyphCount];
        if (GlyphCount == 0)
        {
            return cs;
        }

        if (offset <= 2 && !IsCidKeyed)
        {
            int[] predefined = CffStandardData.PredefinedCharset(offset);
            for (int g = 1; g < cs.Length; g++)
            {
                cs[g] = g - 1 < predefined.Length ? predefined[g - 1] : 0;
            }

            return cs;
        }

        if (offset <= 2)
        {
            // A CID-keyed font must carry its own charset; fall back to the identity so the glyphs stay reachable.
            for (int g = 1; g < cs.Length; g++)
            {
                cs[g] = g;
            }

            return cs;
        }

        try
        {
            int p = offset;
            int format = Be.U8(d, p++);
            int gid = 1;
            switch (format)
            {
                case 0:
                    for (; gid < cs.Length; gid++)
                    {
                        cs[gid] = Be.U16(d, p);
                        p += 2;
                    }

                    break;
                case 1:
                case 2:
                    while (gid < cs.Length)
                    {
                        int first = Be.U16(d, p);
                        p += 2;
                        int left;
                        if (format == 1)
                        {
                            left = Be.U8(d, p++);
                        }
                        else
                        {
                            left = Be.U16(d, p);
                            p += 2;
                        }

                        for (int k = 0; k <= left && gid < cs.Length; k++, gid++)
                        {
                            cs[gid] = first + k;
                        }
                    }

                    break;
                default:
                    throw new FontProgramException("Unknown CFF charset format.");
            }
        }
        catch (FontProgramException)
        {
            // Subsets sometimes cut the charset short. Glyphs past the break stay unnamed (SID 0) but keep their outlines.
        }

        return cs;
    }

    private int SelectFd(int glyph)
    {
        if (_fdSelect.IsEmpty)
        {
            return 0;
        }

        ReadOnlySpan<byte> s = _fdSelect.Span;
        int fd;
        if (s[0] == 0)
        {
            fd = Be.U8(s, 1 + glyph);
        }
        else
        {
            // Format 3: ranges of {first glyph, fd}, closed by a sentinel glyph.
            int nRanges = Be.U16(s, 1);
            int lo = 0, hi = nRanges - 1;
            fd = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int first = Be.U16(s, 3 + (mid * 3));
                if (first <= glyph)
                {
                    fd = Be.U8(s, 5 + (mid * 3));
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
        }

        return fd < _fontDicts.Length ? fd : 0;
    }

    private void ValidateFdSelect(ReadOnlySpan<byte> s)
    {
        byte fmt = Be.U8(s, 0);
        if (fmt == 0)
        {
            if (s.Length < 1 + GlyphCount)
            {
                throw Be.Truncated();
            }
        }
        else if (fmt == 3)
        {
            int n = Be.U16(s, 1);
            if (s.Length < 5 + (3 * n))
            {
                throw Be.Truncated();
            }
        }
        else
        {
            throw new FontProgramException("Unknown FDSelect format.");
        }
    }

    private static FontDict ReadPrivate(ReadOnlySpan<byte> d, Dictionary<int, double[]> dict)
    {
        var fd = new FontDict();
        if (!dict.TryGetValue(18, out double[]? priv) || priv.Length < 2)
        {
            return fd;
        }

        int size = ToOffset(priv[0]);
        int off = ToOffset(priv[1]);
        ReadOnlySpan<byte> pd = Be.Slice(d, off, size);
        Dictionary<int, double[]> p = ParseDict(pd);
        if (p.TryGetValue(20, out double[]? dw) && dw.Length > 0)
        {
            fd.DefaultWidth = (float)dw[0];
        }

        if (p.TryGetValue(21, out double[]? nw) && nw.Length > 0)
        {
            fd.NominalWidth = (float)nw[0];
        }

        if (p.TryGetValue(19, out double[]? sub) && sub.Length > 0)
        {
            fd.LocalSubrs = CffIndex.Read(d, checked(off + ToOffset(sub[0])), out _);
        }

        return fd;
    }

    private static int ToOffset(double v)
    {
        if (!(v >= 0 && v <= int.MaxValue))
        {
            throw new FontProgramException("A CFF offset is out of range.");
        }

        return (int)v;
    }

    // DICT data (TN 5176 section 4): operands then an operator. Keys are the operator, with escaped ones as 0x0C00 | second byte.
    private static Dictionary<int, double[]> ParseDict(ReadOnlySpan<byte> d)
    {
        var result = new Dictionary<int, double[]>();
        var operands = new List<double>(8);
        int p = 0;
        while (p < d.Length)
        {
            int b = d[p++];
            if (b <= 21)
            {
                int op = b;
                if (b == 12)
                {
                    op = 0x0C00 | Be.U8(d, p++);
                }

                result[op] = [.. operands];
                operands.Clear();
            }
            else if (b == 28)
            {
                operands.Add(Be.I16(d, p));
                p += 2;
            }
            else if (b == 29)
            {
                operands.Add(Be.I32(d, p));
                p += 4;
            }
            else if (b == 30)
            {
                operands.Add(ReadReal(d, ref p));
            }
            else if (b >= 32 && b <= 246)
            {
                operands.Add(b - 139);
            }
            else if (b >= 247 && b <= 250)
            {
                operands.Add(((b - 247) * 256) + Be.U8(d, p++) + 108);
            }
            else if (b >= 251 && b <= 254)
            {
                operands.Add((-(b - 251) * 256) - Be.U8(d, p++) - 108);
            }
            else
            {
                throw new FontProgramException("Invalid CFF dictionary byte.");
            }

            if (operands.Count > 64)
            {
                throw new FontProgramException("A CFF dictionary operand list is too long.");
            }
        }

        return result;
    }

    internal static double ReadReal(ReadOnlySpan<byte> d, ref int p)
    {
        // Packed BCD, two nibbles per byte, ended by an 0xF nibble.
        Span<char> text = stackalloc char[48];
        int n = 0;
        while (true)
        {
            int b = Be.U8(d, p++);
            for (int k = 0; k < 2; k++)
            {
                int nib = k == 0 ? b >> 4 : b & 15;
                if (nib == 15)
                {
                    return double.TryParse(text[..n], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : 0;
                }

                ReadOnlySpan<char> piece = nib switch
                {
                    < 10 => ((char)('0' + nib)).ToString(),
                    10 => ".",
                    11 => "E",
                    12 => "E-",
                    14 => "-",
                    _ => throw new FontProgramException("Invalid CFF real number."),
                };
                if (n + piece.Length > text.Length)
                {
                    throw new FontProgramException("A CFF real number is too long.");
                }

                piece.CopyTo(text[n..]);
                n += piece.Length;
            }
        }
    }

    internal sealed class FontDict
    {
        public CffIndex LocalSubrs;
        public float DefaultWidth;
        public float NominalWidth;
    }
}

/// <summary>A CFF INDEX: validated header, offsets read on demand.</summary>
internal readonly struct CffIndex
{
    private readonly int _offsetsPos;
    private readonly int _offSize;
    private readonly int _dataStart;

    private CffIndex(int count, int offSize, int offsetsPos, int dataStart)
    {
        Count = count;
        _offSize = offSize;
        _offsetsPos = offsetsPos;
        _dataStart = dataStart;
    }

    public int Count { get; }

    public static CffIndex Read(ReadOnlySpan<byte> d, int pos, out int end)
    {
        int count = Be.U16(d, pos);
        if (count == 0)
        {
            end = pos + 2;
            return new CffIndex(0, 1, pos + 2, pos + 2);
        }

        int offSize = Be.U8(d, pos + 2);
        if (offSize < 1 || offSize > 4)
        {
            throw new FontProgramException("Invalid CFF INDEX offset size.");
        }

        long offsetsPos = pos + 3L;
        long dataStart = offsetsPos + ((count + 1L) * offSize) - 1;
        if (dataStart > d.Length)
        {
            throw Be.Truncated();
        }

        uint last = Be.UN(d, (int)(offsetsPos + ((long)count * offSize)), offSize);
        long endPos = dataStart + last;
        if (last < 1 || endPos > d.Length)
        {
            throw Be.Truncated();
        }

        end = (int)endPos;
        return new CffIndex(count, offSize, (int)offsetsPos, (int)dataStart);
    }

    public ReadOnlySpan<byte> Get(ReadOnlySpan<byte> d, int i)
    {
        (int s, int len) = Range(d, i);
        return d.Slice(s, len);
    }

    public ReadOnlyMemory<byte> GetMemory(ReadOnlySpan<byte> d, ReadOnlyMemory<byte> m, int i)
    {
        (int s, int len) = Range(d, i);
        return m.Slice(s, len);
    }

    private (int Start, int Length) Range(ReadOnlySpan<byte> d, int i)
    {
        if ((uint)i >= (uint)Count)
        {
            throw new FontProgramException("CFF INDEX entry out of range.");
        }

        uint a = Be.UN(d, _offsetsPos + (i * _offSize), _offSize);
        uint b = Be.UN(d, _offsetsPos + ((i + 1) * _offSize), _offSize);
        if (a < 1 || b < a || _dataStart + (long)b > d.Length)
        {
            throw new FontProgramException("CFF INDEX offsets are invalid.");
        }

        return (_dataStart + (int)a, (int)(b - a));
    }
}

/// <summary>The result of running one Type 2 charstring.</summary>
internal readonly record struct CharstringResult(GlyphOutline Outline, float Width, bool IsAccented, int BaseChar, int AccentChar, float AccentDx, float AccentDy);
