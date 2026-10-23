using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OnePdf.FontData;
using OnePdf.FontPrograms;
using OnePdf.FontWriting;

namespace OnePdf;

/// <summary>Options of <see cref="PdfDocument.AddFont(ReadOnlyMemory{byte}, PdfEmbedFontOptions?)"/>.</summary>
public sealed class PdfEmbedFontOptions
{
    /// <summary>Which face of a TrueType collection (.ttc) to use. Default 0. A collection can only be embedded as a subset.</summary>
    public int FaceIndex { get; init; }

    /// <summary>
    /// Embed only the glyphs the document uses (ISO 32000-2 9.6.4). Default true. False embeds the whole font program. A font whose
    /// <c>OS/2</c> <c>fsType</c> sets the "no subsetting" bit is always embedded whole, whatever this says.
    /// </summary>
    public bool Subset { get; init; } = true;

    /// <summary>
    /// Embed a font whose <c>OS/2</c> <c>fsType</c> does not permit embedding (restricted license, or bitmap-only embedding).
    /// Default false: <see cref="PdfDocument.AddFont(ReadOnlyMemory{byte}, PdfEmbedFontOptions?)"/> throws
    /// <see cref="PdfFontEmbeddingException"/>. Setting it is the caller's declaration that they hold the right to embed the font.
    /// </summary>
    public bool IgnoreEmbeddingRestrictions { get; init; }

    /// <summary>The PostScript name to use instead of the font's own (letters, digits and hyphens are kept; other characters dropped).</summary>
    public string? BaseFontName { get; init; }
}

/// <summary>A font embedding was refused: the font file's license flags forbid it (<c>OS/2</c> <c>fsType</c>).</summary>
public sealed class PdfFontEmbeddingException : PdfException
{
    /// <summary>Creates the exception.</summary>
    public PdfFontEmbeddingException(string message) : base(message) { }

    /// <summary>Creates the exception with the underlying cause.</summary>
    public PdfFontEmbeddingException(string message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>
/// A font added to a document for drawing new text (<see cref="PdfDocument.AddStandardFont"/>, <see cref="PdfDocument.AddFont(ReadOnlyMemory{byte}, PdfEmbedFontOptions?)"/>).
/// One object can be used by any number of builders and pages of its document.
/// </summary>
/// <remarks>
/// Text is placed glyph by glyph from the font's character map and advance widths. There is no shaping: no kerning, no ligature
/// substitution, no contextual forms, no bidirectional reordering. Scripts that need these (Arabic, Indic scripts, Thai stacking)
/// are out of scope; text is drawn in the order of the string.
/// </remarks>
public abstract class PdfCreatedFont
{
    internal PdfCreatedFont(PdfDocument document, string name)
    {
        Document = document;
        BaseFont = name;
    }

    /// <summary>The document the font belongs to.</summary>
    public PdfDocument Document { get; }

    /// <summary>The PostScript name. For an embedded subset it carries the six-letter subset tag (<c>ABCDEF+Name</c>) once glyphs are used.</summary>
    public abstract string BaseFontName { get; }

    internal string BaseFont { get; set; }

    /// <summary>The indirect reference of the font dictionary.</summary>
    public PdfReference Reference { get; internal set; } = null!;

    /// <summary>Typographic ascent as a fraction of the font size (positive).</summary>
    public abstract double Ascent { get; }

    /// <summary>Typographic descent as a fraction of the font size (negative or zero).</summary>
    public abstract double Descent { get; }

    /// <summary>The width of <paramref name="text"/> in points at <paramref name="fontSize"/>, from the font's advance widths. No kerning; characters the font lacks count as its missing glyph.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public double MeasureText(string text, double fontSize)
    {
        ArgumentNullException.ThrowIfNull(text);
        EncodedText e = Encode(text, record: false);
        return e.Width1000 * fontSize / 1000.0;
    }

    internal abstract EncodedText Encode(string text, bool record);

    internal virtual void FinishForSave()
    {
    }
}

/// <summary>Text as the codes of a font, with what the builder needs to place it.</summary>
internal sealed class EncodedText
{
    public required byte[] Codes { get; init; }

    /// <summary>Two bytes per glyph (Identity-H) when true, else one.</summary>
    public required bool TwoByte { get; init; }

    public required int GlyphCount { get; init; }

    /// <summary>Indexes (in glyphs) of the space glyphs, for word spacing.</summary>
    public required int[] Spaces { get; init; }

    /// <summary>Total advance in thousandths of the font size.</summary>
    public required double Width1000 { get; init; }
}

internal sealed class StandardCreatedFont : PdfCreatedFont
{
    private readonly Dictionary<int, (byte Code, int Width)> _map = [];
    private readonly StandardFontMetrics _metrics;
    private readonly HashSet<int> _reported = [];

    public StandardCreatedFont(PdfDocument doc, string standardName)
        : base(doc, standardName)
    {
        _metrics = Core14.Get(standardName);
        string?[] names = standardName is "Symbol" or "ZapfDingbats" ? _metrics.BuiltInEncoding : Encodings.Get(PredefinedEncoding.WinAnsi);
        for (int code = 0; code < 256; code++)
        {
            string? name = names[code];
            if (name is null)
            {
                continue;
            }

            string? text = null;
            bool found = standardName == "ZapfDingbats" ? GlyphList.TryGetZapfDingbats(name, out text) : GlyphList.TryGetUnicode(name, out text);
            if (!found || text is null || text.Length == 0 || !Rune.TryGetRuneAt(text, 0, out Rune r) || r.Utf16SequenceLength != text.Length)
            {
                continue;
            }

            int? w = _metrics.GetWidth(name);
            if (w is null)
            {
                continue;
            }

            _map.TryAdd(r.Value, ((byte)code, w.Value));
        }

        // The no-break space and soft hyphen print as space and hyphen in WinAnsi (ISO 32000-2 Annex D, notes 3 and 4).
        if (standardName is not ("Symbol" or "ZapfDingbats"))
        {
            if (_map.TryGetValue(' ', out (byte Code, int Width) sp))
            {
                _map.TryAdd(0xA0, sp);
            }

            if (_map.TryGetValue('-', out (byte Code, int Width) hy))
            {
                _map.TryAdd(0xAD, hy);
            }
        }
    }

    public override string BaseFontName => BaseFont;

    public override double Ascent => (_metrics.Ascender != 0 ? _metrics.Ascender : 800) / 1000.0;

    public override double Descent => (_metrics.Descender != 0 ? _metrics.Descender : -200) / 1000.0;

    internal override EncodedText Encode(string text, bool record)
    {
        var codes = new List<byte>(text.Length);
        var spaces = new List<int>();
        double width = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (!_map.TryGetValue(rune.Value, out (byte Code, int Width) e))
            {
                if (rune.Value < 32 || rune.Value == 0x7F)
                {
                    continue; // control characters draw nothing
                }

                if (record && _reported.Add(rune.Value))
                {
                    Document.ReportWarning($"U+{rune.Value:X4} is not in the {BaseFont} font's character set; drawn as '?'.", -1, -1);
                }

                if (!_map.TryGetValue('?', out e))
                {
                    continue;
                }
            }

            if (rune.Value == ' ')
            {
                spaces.Add(codes.Count);
            }

            codes.Add(e.Code);
            width += e.Width;
        }

        return new EncodedText { Codes = [.. codes], TwoByte = false, GlyphCount = codes.Count, Spaces = [.. spaces], Width1000 = width };
    }
}

/// <summary>An embedded TrueType or OpenType font written as a Type 0 font with a CIDFontType2 or CIDFontType0 descendant and Identity-H.</summary>
internal sealed class EmbeddedCreatedFont : PdfCreatedFont
{
    private const int MaxFontBytes = 1 << 28;

    private readonly byte[] _data;
    private readonly SfntFont _font;
    private readonly bool _subset;
    private readonly bool _isCff;
    private readonly string _postScriptName;
    private readonly int _faceIndex;
    private readonly List<int> _glyphs = []; // _glyphs[code - 1] = original glyph, subset numbering
    private readonly List<int> _unicodes = [];
    private readonly Dictionary<int, int> _codeOfGlyph = [];
    private readonly HashSet<int> _reported = [];
    private readonly Dictionary<int, int> _widthOfCode = [];
    private readonly PdfDictionary _type0;
    private readonly PdfDictionary _cidFont;
    private readonly PdfDictionary _descriptor;
    private readonly PdfStream _fontFile;
    private readonly PdfStream _toUnicode;
    private readonly int _upem;
    private readonly CmapSubtable? _cmap;
    private readonly CmapSubtable? _symbolCmap;
    private int _finishedGlyphCount = -1;
    private byte[]? _hash;
    private string _currentName;

    public EmbeddedCreatedFont(PdfDocument doc, byte[] copy, PdfEmbedFontOptions options)
        : base(doc, "Font")
    {
        _data = copy;
        _faceIndex = options.FaceIndex;
        // The sfnt version (OpenType spec, "Organization of an OpenType Font"): 1.0, 'true' (Apple), 'OTTO' (CFF) or a collection.
        if (_data.Length < 12 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(_data) is not (0x00010000 or 0x74727565 or 0x4F54544F or 0x74746366))
        {
            throw new PdfFormatException("The data is not a TrueType or OpenType font (unknown sfnt version).");
        }

        try
        {
            _font = SfntFont.Parse(_data, options.FaceIndex, GlyphPrograms.Never);
        }
        catch (FontProgramException ex)
        {
            throw new PdfFormatException("The font data is not a usable TrueType or OpenType font: " + ex.Message, ex);
        }

        if (_font.GlyphCount < 1)
        {
            throw new PdfFormatException("The font has no glyphs.");
        }

        _isCff = _font.Kind == FontProgramKind.OpenTypeCff;
        _upem = _font.UnitsPerEm;
        _cmap = _font.FindUnicodeCmap();
        _symbolCmap = _font.FindCmap(3, 0);
        if (_cmap is null && _symbolCmap is null)
        {
            throw new PdfFormatException("The font has no character map, so text cannot be mapped to glyphs.");
        }

        int fsType = EmbeddingFlags(_font);
        bool noSubsetting = (fsType & 0x0100) != 0;
        bool restricted = ((fsType & 0x0002) != 0 && (fsType & 0x000C) == 0) || (fsType & 0x0200) != 0;
        if (restricted && !options.IgnoreEmbeddingRestrictions)
        {
            throw new PdfFontEmbeddingException($"The font's embedding flags (OS/2 fsType 0x{fsType:X4}) do not permit embedding it. Set PdfEmbedFontOptions.IgnoreEmbeddingRestrictions only if you hold the right to embed it.");
        }

        if (SfntFont.FaceCount(_data) > 1 && !options.Subset)
        {
            throw new ArgumentException("A font collection can only be embedded as a subset.", nameof(options));
        }

        _subset = options.Subset && !noSubsetting;
        if (!_subset && SfntFont.FaceCount(_data) > 1)
        {
            throw new PdfFormatException("The font is a collection that forbids subsetting; it cannot be embedded.");
        }

        _postScriptName = SanitizeName(options.BaseFontName ?? _font.FontName ?? "EmbeddedFont");
        _currentName = _postScriptName;
        BaseFont = _postScriptName;

        _fontFile = doc.CreateStream([], false);
        _toUnicode = doc.CreateStream([], false);
        _descriptor = new PdfDictionary();
        _cidFont = new PdfDictionary();
        _type0 = new PdfDictionary();
        PdfReference fontFileRef = doc.AddObject(_fontFile);
        PdfReference toUnicodeRef = doc.AddObject(_toUnicode);

        _descriptor.SetEntry("Type", new PdfName("FontDescriptor"));
        _descriptor.SetEntry("Flags", new PdfInteger(4));
        _descriptor.SetEntry(_isCff ? "FontFile3" : "FontFile2", fontFileRef);
        PdfReference descriptorRef = doc.AddObject(_descriptor);

        _cidFont.SetEntry("Type", new PdfName("Font"));
        _cidFont.SetEntry("Subtype", new PdfName(_isCff ? "CIDFontType0" : "CIDFontType2"));
        var sysInfo = new PdfDictionary();
        sysInfo.SetEntry("Registry", PdfString.FromText("Adobe"));
        sysInfo.SetEntry("Ordering", PdfString.FromText("Identity"));
        sysInfo.SetEntry("Supplement", new PdfInteger(0));
        _cidFont.SetEntry("CIDSystemInfo", sysInfo);
        _cidFont.SetEntry("FontDescriptor", descriptorRef);
        if (!_isCff)
        {
            _cidFont.SetEntry("CIDToGIDMap", new PdfName("Identity"));
        }

        PdfReference cidRef = doc.AddObject(_cidFont);

        _type0.SetEntry("Type", new PdfName("Font"));
        _type0.SetEntry("Subtype", new PdfName("Type0"));
        _type0.SetEntry("Encoding", new PdfName("Identity-H"));
        var descendants = new PdfArray();
        descendants.Append(cidRef);
        _type0.SetEntry("DescendantFonts", descendants);
        _type0.SetEntry("ToUnicode", toUnicodeRef);
        Reference = doc.AddObject(_type0);
        Describe();
        FinishForSave();
    }

    public override string BaseFontName => _currentName;

    public override double Ascent => AscentValue;

    public override double Descent => DescentValue;

    private static int EmbeddingFlags(SfntFont f)
    {
        ReadOnlyMemory<byte> os2 = f.GetTable("OS/2");
        return os2.Length >= 10 ? Be.U16(os2.Span, 8) : 0;
    }

    private static string SanitizeName(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name)
        {
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_')
            {
                sb.Append(c);
            }
        }

        return sb.Length == 0 ? "EmbeddedFont" : sb.ToString(0, Math.Min(sb.Length, 100));
    }

    // Descriptor values that do not change with the glyphs used.
    private void Describe()
    {
        double scale = 1000.0 / _upem;
        if (_font.FontBBox is { Length: 4 } bb)
        {
            var box = new PdfArray();
            foreach (double v in bb)
            {
                box.Append(new PdfInteger((long)Math.Round(v * scale)));
            }

            _descriptor.SetEntry("FontBBox", box);
        }
        else
        {
            var box = new PdfArray();
            foreach (int v in (int[])[0, -200, 1000, 800])
            {
                box.Append(new PdfInteger(v));
            }

            _descriptor.SetEntry("FontBBox", box);
        }

        double ascent = 800;
        double descent = -200;
        ReadOnlyMemory<byte> hhea = _font.GetTable("hhea");
        if (hhea.Length >= 8)
        {
            ascent = Be.I16(hhea.Span, 4) * scale;
            descent = Be.I16(hhea.Span, 6) * scale;
        }

        double capHeight = ascent;
        ReadOnlyMemory<byte> os2 = _font.GetTable("OS/2");
        if (os2.Length >= 70)
        {
            int typoAscender = Be.I16(os2.Span, 68);
            int typoDescender = Be.I16(os2.Span, 70 <= os2.Length - 2 ? 70 : 68);
            if (ascent == 0 && typoAscender != 0)
            {
                ascent = typoAscender * scale;
                descent = typoDescender * scale;
            }
        }

        if (os2.Length >= 90 && Be.U16(os2.Span, 0) >= 2)
        {
            int cap = Be.I16(os2.Span, 88);
            if (cap > 0)
            {
                capHeight = cap * scale;
            }
        }

        double italic = 0;
        ReadOnlyMemory<byte> post = _font.GetTable("post");
        if (post.Length >= 8)
        {
            italic = Be.I32(post.Span, 4) / 65536.0;
        }

        _descriptor.SetEntry("Ascent", new PdfInteger((long)Math.Round(ascent)));
        _descriptor.SetEntry("Descent", new PdfInteger((long)Math.Round(descent)));
        _descriptor.SetEntry("CapHeight", new PdfInteger((long)Math.Round(capHeight)));
        _descriptor.SetEntry("ItalicAngle", new PdfInteger((long)Math.Round(italic)));
        _descriptor.SetEntry("StemV", new PdfInteger(80));
        AscentValue = ascent / 1000.0;
        DescentValue = descent / 1000.0;
    }

    private double AscentValue { get; set; } = 0.8;

    private double DescentValue { get; set; } = -0.2;

    private int LookupGlyph(int rune)
    {
        int g = _cmap?.Lookup(rune) ?? 0;
        if (g == 0 && _symbolCmap is not null)
        {
            g = _symbolCmap.LookupSymbolic(rune);
        }

        return g < _font.GlyphCount ? g : 0;
    }

    internal override EncodedText Encode(string text, bool record)
    {
        var list = new List<byte>(record ? text.Length * 2 : 0);
        var spaces = new List<int>();
        double width = 0;
        int count = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (rune.Value < 32 || rune.Value == 0x7F || (rune.Value >= 0x80 && rune.Value < 0xA0))
            {
                continue; // control characters draw nothing
            }

            int gid = LookupGlyph(rune.Value);
            int code = 0;
            if (gid == 0)
            {
                if (record && _reported.Add(rune.Value))
                {
                    Document.ReportWarning($"The font {_postScriptName} has no glyph for U+{rune.Value:X4}; drawn as its missing-glyph box (.notdef).", -1, -1);
                }
            }
            else if (record)
            {
                code = CodeFor(gid, rune.Value);
            }

            if (record)
            {
                list.Add((byte)(code >> 8));
                list.Add((byte)code);
            }

            if (rune.Value == ' ')
            {
                spaces.Add(count);
            }

            width += Width1000Of(gid);
            count++;
        }

        return new EncodedText { Codes = [.. list], TwoByte = true, GlyphCount = count, Spaces = [.. spaces], Width1000 = width };
    }

    private int CodeFor(int gid, int rune)
    {
        if (_codeOfGlyph.TryGetValue(gid, out int existing))
        {
            return existing;
        }

        // Subset: codes count up from 1 in order of first use. Whole font: the code is the glyph index.
        int code = _subset ? _glyphs.Count + 1 : (_isCff && _font.Cff is { IsCidKeyed: true } cff ? cff.CidOfGlyph(gid) : gid);
        if (_subset && code > 0xFFFF)
        {
            throw new PdfLimitException("A font subset can hold at most 65535 different glyphs.");
        }

        _glyphs.Add(gid);
        _unicodes.Add(rune);
        _codeOfGlyph[gid] = code;
        _widthOfCode[code] = (int)Math.Round(AdvanceUnits(gid));
        return code;
    }

    private double AdvanceUnits(int gid) => _font.TryGetAdvance(gid, out float a) ? a * 1000.0 / _upem : 0;

    private double Width1000Of(int gid) => Math.Round(AdvanceUnits(gid));

    internal override void FinishForSave()
    {
        if (_glyphs.Count == _finishedGlyphCount)
        {
            return;
        }

        _finishedGlyphCount = _glyphs.Count;

        // ---- font program ----
        string tag = _subset ? SubsetTag() : string.Empty;
        _currentName = tag.Length > 0 ? tag + "+" + _postScriptName : _postScriptName;
        BaseFont = _currentName;
        byte[] program;
        if (_subset)
        {
            try
            {
                program = _isCff
                    ? CffSubsetter.Subset(_font.GetTable("CFF "), _glyphs, _currentName)
                    : TrueTypeSubsetter.Subset(_font, _glyphs);
            }
            catch (FontProgramException ex)
            {
                throw new PdfFormatException("The font cannot be subset: " + ex.Message, ex);
            }
        }
        else
        {
            program = _data;
        }

        _fontFile.SetData(program, compress: true);
        if (_isCff)
        {
            _fontFile.Dictionary.SetEntry("Subtype", new PdfName(_subset ? "CIDFontType0C" : "OpenType"));
        }
        else
        {
            _fontFile.Dictionary.SetEntry("Length1", new PdfInteger(program.Length));
        }

        _descriptor.SetEntry("FontName", new PdfName(_currentName));
        _cidFont.SetEntry("BaseFont", new PdfName(_currentName));
        _type0.SetEntry("BaseFont", new PdfName(_currentName + "-Identity-H"));
        _cidFont.SetEntry("DW", new PdfInteger(1000));
        _cidFont.SetEntry("W", BuildWidths());
        _toUnicode.SetData(BuildToUnicode(), compress: true);
    }

    private string SubsetTag()
    {
        // Six capital letters from a hash of the font and the glyphs, so the same inputs always give the same name (ISO 32000-2 9.6.4).
        _hash ??= SHA256.HashData(_data);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(_hash);
        sha.AppendData(BitConverter.GetBytes(_faceIndex));
        foreach (int g in _glyphs)
        {
            sha.AppendData(BitConverter.GetBytes(g));
        }

        byte[] h = sha.GetHashAndReset();
        var chars = new char[6];
        for (int i = 0; i < 6; i++)
        {
            chars[i] = (char)('A' + (h[i] % 26));
        }

        return new string(chars);
    }

    private PdfArray BuildWidths()
    {
        var w = new PdfArray();
        var codes = new List<int>(_widthOfCode.Keys);
        codes.Sort();
        PdfArray? run = null;
        int previous = -2;
        foreach (int c in codes)
        {
            if (c != previous + 1 || run is null)
            {
                run = new PdfArray();
                w.Append(new PdfInteger(c));
                w.Append(run);
            }

            run.Append(new PdfInteger(_widthOfCode[c]));
            previous = c;
        }

        return w;
    }

    private byte[] BuildToUnicode()
    {
        var entries = new List<(int Code, int Rune)>();
        for (int i = 0; i < _glyphs.Count; i++)
        {
            int code = _subset ? i + 1 : _codeOfGlyph[_glyphs[i]];
            entries.Add((code, _unicodes[i]));
        }

        entries.Sort(static (a, b) => a.Code.CompareTo(b.Code));
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        sb.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        sb.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        for (int i = 0; i < entries.Count; i += 100)
        {
            int n = Math.Min(100, entries.Count - i);
            sb.Append(n.ToString(CultureInfo.InvariantCulture)).Append(" beginbfchar\n");
            for (int j = 0; j < n; j++)
            {
                (int code, int rune) = entries[i + j];
                sb.Append('<').Append(code.ToString("X4", CultureInfo.InvariantCulture)).Append("> <");
                foreach (char ch in new Rune(rune).ToString())
                {
                    sb.Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
                }

                sb.Append(">\n");
            }

            sb.Append("endbfchar\n");
        }

        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
