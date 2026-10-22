using OnePdf.FontData;
using OnePdf.FontPrograms;

namespace OnePdf.PdfFonts;

/// <summary>Builds the code to glyph-name table of a simple font from <c>/Encoding</c> (ISO 32000-2 9.6.5, 9.6.6).</summary>
internal static class SimpleEncoding
{
    private static readonly Lazy<Dictionary<string, int>> MacRomanCodes = new(() =>
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        string?[] t = Encodings.Get(PredefinedEncoding.MacRoman);
        for (int i = 255; i >= 0; i--)
        {
            if (t[i] is { } n)
            {
                map[n] = i;
            }
        }

        return map;
    });

    /// <summary>The MacRoman code of a glyph name, or -1.</summary>
    public static int MacRomanCode(string name) => MacRomanCodes.Value.TryGetValue(name, out int c) ? c : -1;

    /// <summary>The predefined encoding a name stands for, or null for unknown names.</summary>
    public static PredefinedEncoding? ByName(string? name) => name switch
    {
        "WinAnsiEncoding" => PredefinedEncoding.WinAnsi,
        "MacRomanEncoding" => PredefinedEncoding.MacRoman,
        "MacExpertEncoding" => PredefinedEncoding.MacExpert,
        "StandardEncoding" => PredefinedEncoding.Standard,
        _ => null,
    };

    /// <summary>Reads <c>/Encoding</c>: the base encoding (null when none is named) and the <c>/Differences</c> array.</summary>
    public static void Read(PdfDictionary font, out bool present, out PredefinedEncoding? baseEncoding, out PdfArray? differences)
    {
        baseEncoding = null;
        differences = null;
        PdfObject? enc = font.Get("Encoding");
        present = enc is PdfName or PdfDictionary;
        if (enc is PdfName n)
        {
            baseEncoding = ByName(n.Value);
        }
        else if (enc is PdfDictionary d)
        {
            baseEncoding = ByName(d.GetName("BaseEncoding"));
            differences = d.GetArray("Differences");
        }
    }

    /// <summary>Applies a <c>/Differences</c> array: an integer sets the next code, each following name takes the code and advances.</summary>
    public static void ApplyDifferences(string?[] names, PdfArray? differences)
    {
        if (differences is null)
        {
            return;
        }

        int code = 0;
        for (int i = 0; i < differences.Count; i++)
        {
            switch (differences[i])
            {
                case PdfInteger n:
                    code = (int)Math.Clamp(n.Value, -1, 1 << 20);
                    break;
                case PdfReal r:
                    code = (int)Math.Clamp(r.Value, -1, 1 << 20);
                    break;
                case PdfName name:
                    if ((uint)code < 256)
                    {
                        names[code] = name.Value;
                    }

                    code++;
                    break;
            }
        }
    }
}

/// <summary>
/// A Type1, MMType1 or TrueType font: one byte per character. Glyph selection follows ISO 32000-2 9.6.5.2 (Type 1 names through
/// the encoding) and 9.6.5.4 (TrueType: names to Unicode through a (3,1) cmap, to MacRoman through (1,0), then the post table;
/// symbolic fonts through (3,0) with the 0xF000 offsets, then (1,0)).
/// </summary>
internal sealed class SimpleFont : PdfFont
{
    private readonly DescriptorInfo _desc;
    private readonly string?[] _names = new string?[256];
    private readonly bool _useEncoding;
    private readonly CMap? _toUnicode;
    private readonly double[]? _widths;
    private readonly int _firstChar;
    private readonly StandardFontMetrics? _std;
    private readonly bool _zapf;
    private Func<int, int>? _codePointMap;
    private readonly ProgramUnicode? _programUnicode;
    private readonly FontGlyph?[] _cache = new FontGlyph?[256];
    private readonly CmapSubtable? _cmap31;
    private readonly CmapSubtable? _cmap30;
    private readonly CmapSubtable? _cmap10;
    private readonly CmapSubtable? _cmapUnicode;

    public SimpleFont(PdfDictionary dictionary, FontContext context)
        : base(dictionary, context)
    {
        _desc = DescriptorInfo.Read(dictionary.GetDictionary("FontDescriptor"));
        string baseName = StripSubsetTag(Name);
        string? stdName = Core14.Resolve(baseName);
        if (stdName is not null)
        {
            _std = Core14.Get(stdName);
        }

        _zapf = stdName == "ZapfDingbats";
        Flags = _desc.HasFlags ? _desc.Flags : stdName is "Symbol" or "ZapfDingbats" ? FontFlags.Symbolic : FontFlags.Nonsymbolic;
        FontBBox = _desc.BBox;

        Program = FontSupport.LoadEmbedded(context, _desc, Name);
        IsEmbedded = Program is not null;
        EmbeddedFailed = Program is null && FontSupport.HasEmbeddedStream(_desc);
        if (Program is null)
        {
            PdfFontRequest req = FontSupport.BuildRequest(Name, _desc, null, false);
            Program = FontSupport.LoadSubstitute(context, req, out FontProgram[] fallbacks, out _codePointMap);
            Fallbacks = fallbacks;
            IsSubstituted = Program is not null;
            context.Document.ReportInfo(Program is null
                ? $"No font program for '{Name}': glyph outlines are unavailable (metrics and text still work)."
                : $"Font '{Name}' is not embedded; a substitute is used.", context.ObjectNumber, -1);
        }

        _toUnicode = FontSupport.LoadToUnicode(context, dictionary);
        if (Program is not null && !IsSubstituted)
        {
            _programUnicode = new ProgramUnicode(Program);
        }

        if (Program is SfntFont sf)
        {
            _cmap31 = sf.FindCmap(3, 1);
            _cmap30 = sf.FindCmap(3, 0);
            _cmap10 = sf.FindCmap(1, 0);
            _cmapUnicode = sf.FindUnicodeCmap();
        }

        SimpleEncoding.Read(dictionary, out bool present, out PredefinedEncoding? baseEnc, out PdfArray? diffs);
        bool symbolic = (Flags & FontFlags.Symbolic) != 0;
        bool nonsymbolic = (Flags & FontFlags.Nonsymbolic) != 0;
        _useEncoding = present && (nonsymbolic || !symbolic);

        string?[]? start = null;
        if (baseEnc is { } be)
        {
            start = Encodings.Get(be);
        }
        else
        {
            if (IsEmbedded)
            {
                start = Program is SfntFont { Cff: { } cff } ? cff.BuiltInEncoding : Program!.BuiltInEncoding;
            }
            else if (_std is not null)
            {
                start = _std.BuiltInEncoding;
            }

            // Without a built-in encoding a nonsymbolic font starts from StandardEncoding (9.6.5.4); a symbolic TrueType relies on its cmap.
            if (start is null && (!symbolic || nonsymbolic || IsSubstituted))
            {
                start = Encodings.Get(PredefinedEncoding.Standard);
            }
        }

        if (start is not null)
        {
            Array.Copy(start, _names, 256);
        }

        SimpleEncoding.ApplyDifferences(_names, diffs);

        _firstChar = (int)Math.Clamp(dictionary.GetInt("FirstChar"), 0, 255);
        if (dictionary.GetArray("Widths") is { } w)
        {
            int n = Math.Min(w.Count, 256);
            _widths = new double[n];
            for (int i = 0; i < n; i++)
            {
                _widths[i] = w.GetDouble(i);
            }
        }
    }

    /// <inheritdoc />
    public override int Next(ReadOnlySpan<byte> text, int position, out FontGlyph? glyph)
    {
        if ((uint)position >= (uint)text.Length)
        {
            glyph = null;
            return position;
        }

        int code = text[position];
        // Racing threads may both build a glyph; exactly one instance is published and shared.
        FontGlyph? g = Volatile.Read(ref _cache[code]);
        if (g is null)
        {
            FontGlyph built = Build(code);
            g = Interlocked.CompareExchange(ref _cache[code], built, null) ?? built;
        }

        glyph = g;
        return position + 1;
    }

    private FontGlyph Build(int code)
    {
        string? name = _names[code];
        bool hasUnicode = FontSupport.TryToUnicode(_toUnicode, (uint)code, 1, out string text) || FontSupport.NameToUnicode(name, _zapf, out text);

        int gid = -1;
        int face = 0;
        bool exists = false;
        FontProgram? p = Program;
        if (p is not null)
        {
            if (IsSubstituted)
            {
                gid = FontSupport.FindGlyphByNameOrUnicode(p, name, hasUnicode ? text : null, _codePointMap);
                for (int i = 0; gid <= 0 && i < Fallbacks.Length; i++)
                {
                    int g2 = FontSupport.FindGlyphByNameOrUnicode(Fallbacks[i], name, hasUnicode ? text : null, _codePointMap);
                    if (g2 > 0)
                    {
                        gid = g2;
                        face = i + 1;
                    }
                }

                exists = gid > 0;
            }
            else if (p is SfntFont { Cff: null } sf)
            {
                gid = TrueTypeGid(sf, code, name, hasUnicode ? text : null);
                exists = gid > 0;
            }
            else
            {
                gid = NameGid(p, code, name, hasUnicode ? text : null);
                // FindGlyph returns -1 for a missing name; a hit on ".notdef" itself is not a real character.
                exists = gid >= 0 && name != ".notdef" && !(gid == 0 && p is SfntFont or CffFont);
            }

            if (gid < 0)
            {
                gid = Math.Max(0, p.FindGlyph(".notdef"));
            }

            if (!hasUnicode && exists && _programUnicode is not null && _programUnicode.TryGet(gid, out string pu))
            {
                text = pu;
                hasUnicode = true;
            }
        }

        // A subset TrueType font with no /Encoding and only a (1,0) or (3,0) cmap has no source of text for its codes; the printable
        // ASCII range means the same character in every Latin encoding, so the code stands for itself (ISO 32000-2 9.6.5.4 leaves
        // these codes to the font's cmap, and producers of such subsets use ASCII codes for ASCII text).
        if (!hasUnicode && code is >= 0x20 and <= 0x7E && Subtype == "TrueType")
        {
            text = ((char)code).ToString();
            hasUnicode = true;
        }

        double width = Width(code, name, gid);
        FontProgram? gp = face > 0 ? Fallbacks[face - 1] : p;
        double[]? matrix = gp?.FontMatrix;
        if (IsSubstituted && gp is not null && exists && width > 0)
        {
            matrix = ScaleToWidth(gp, gid, width);
        }

        return new FontGlyph(this)
        {
            Code = (uint)code,
            CodeLength = 1,
            Cid = code,
            Gid = p is null ? -1 : gid,
            Unicode = hasUnicode ? text : "\uFFFD",
            HasUnicode = hasUnicode,
            Exists = exists,
            Name = name,
            Face = face,
            Advance = width / 1000.0,
            OutlineMatrix = matrix,
        };
    }

    /// <summary>
    /// The substitute's matrix with its x axis scaled so the glyph's advance equals the PDF's width, limited to a plausible range
    /// so one odd width cannot flatten or stretch a glyph beyond recognition.
    /// </summary>
    internal static double[] ScaleToWidth(FontProgram p, int gid, double width)
    {
        double[] m = p.FontMatrix;
        try
        {
            if (p.TryGetAdvance(gid, out float adv))
            {
                double natural = adv * m[0] * 1000.0;
                if (natural > 0)
                {
                    double s = Math.Clamp(width / natural, 0.4, 2.5);
                    if (Math.Abs(s - 1) > 0.01)
                    {
                        return [m[0] * s, m[1], m[2] * s, m[3], m[4] * s, m[5]];
                    }
                }
            }
        }
        catch (FontProgramException)
        {
            // A damaged charstring: draw unscaled.
        }

        return m;
    }

    private double Width(int code, string? name, int gid)
    {
        int idx = code - _firstChar;
        if (_widths is not null)
        {
            return (uint)idx < (uint)_widths.Length ? _widths[idx] : _desc.MissingWidth;
        }

        if (_std is not null && name is not null && _std.GetWidth(name) is { } sw)
        {
            return sw;
        }

        if (Program is { } p && gid >= 0 && !IsSubstituted)
        {
            try
            {
                if (p.TryGetAdvance(gid, out float adv))
                {
                    return adv * p.FontMatrix[0] * 1000.0;
                }
            }
            catch (FontProgramException)
            {
                // Fall through to the missing width.
            }
        }

        return _desc.MissingWidth;
    }

    private static int NameGid(FontProgram p, int code, string? name, string? text)
    {
        if (p.IsCidKeyed)
        {
            // A CID-keyed program in a simple font: the code is taken as the CID.
            return p.GlyphForCid(code);
        }

        int g = -1;
        if (name is not null)
        {
            g = p.FindGlyph(name);
            int dot = name.IndexOf('.', StringComparison.Ordinal);
            if (g < 0 && dot > 0)
            {
                g = p.FindGlyph(name[..dot]);
            }
        }

        if (g < 0)
        {
            string?[]? builtin = p is SfntFont { Cff: { } cff } ? cff.BuiltInEncoding : p.BuiltInEncoding;
            if (builtin?[code] is { } bn)
            {
                g = p.FindGlyph(bn);
            }
        }

        if (g < 0 && text is not null)
        {
            g = FontSupport.FindGlyphByNameOrUnicode(p, null, text);
        }

        return g;
    }

    private int TrueTypeGid(SfntFont sf, int code, string? name, string? text)
    {
        int g = 0;
        if (_useEncoding && name is not null)
        {
            CmapSubtable? uni = _cmap31 ?? _cmapUnicode;
            if (uni is not null && FontSupport.NameToUnicode(name, false, out string u))
            {
                g = uni.Lookup(FontSupport.FirstRune(u));
            }

            if (g == 0 && _cmap10 is not null)
            {
                int mac = SimpleEncoding.MacRomanCode(name);
                if (mac >= 0)
                {
                    g = _cmap10.Lookup(mac);
                }
            }

            if (g == 0)
            {
                g = Math.Max(0, sf.FindGlyph(name));
            }

            if (g == 0 && text is not null && uni is not null)
            {
                g = uni.Lookup(FontSupport.FirstRune(text));
            }
        }

        if (g == 0 && _cmap30 is not null)
        {
            g = _cmap30.LookupSymbolic(code);
        }

        if (g == 0 && _cmap10 is not null)
        {
            g = _cmap10.Lookup(code);
        }

        if (g == 0 && _cmap31 is not null)
        {
            // A symbolic font with only a Unicode cmap: the code read as a WinAnsi character, then as a raw value or in the private-use block.
            if (Encodings.GetName(PredefinedEncoding.WinAnsi, code) is { } wn && FontSupport.NameToUnicode(wn, false, out string wu))
            {
                g = _cmap31.Lookup(FontSupport.FirstRune(wu));
            }

            if (g == 0)
            {
                g = _cmap31.LookupSymbolic(code);
            }
        }

        if (g == 0 && !_useEncoding && name is not null)
        {
            g = Math.Max(0, sf.FindGlyph(name));
        }

        return g;
    }
}
