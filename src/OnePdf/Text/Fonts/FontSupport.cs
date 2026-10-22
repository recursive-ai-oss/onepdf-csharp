using System.Collections.Concurrent;
using System.Globalization;
using OnePdf.FontData;
using OnePdf.FontPrograms;

namespace OnePdf.PdfFonts;

/// <summary>The parts of a font descriptor the font layer uses.</summary>
internal sealed class DescriptorInfo
{
    public PdfDictionary? Dictionary { get; init; }

    public int Flags { get; init; }

    public bool HasFlags { get; init; }

    public bool HasMissingWidth { get; init; }

    public double MissingWidth { get; init; }

    public double ItalicAngle { get; init; }

    public double Weight { get; init; } = 400;

    public string? FontName { get; init; }

    public double[]? BBox { get; init; }

    public static DescriptorInfo Read(PdfDictionary? d)
    {
        if (d is null)
        {
            return new DescriptorInfo();
        }

        double[]? bbox = null;
        if (d.GetArray("FontBBox") is { Count: 4 } a)
        {
            bbox = [a.GetDouble(0), a.GetDouble(1), a.GetDouble(2), a.GetDouble(3)];
        }

        return new DescriptorInfo
        {
            Dictionary = d,
            Flags = (int)Math.Clamp(d.GetInt("Flags"), int.MinValue, int.MaxValue),
            HasFlags = d.ContainsKey("Flags"),
            HasMissingWidth = d.ContainsKey("MissingWidth"),
            MissingWidth = d.GetDouble("MissingWidth"),
            ItalicAngle = d.GetDouble("ItalicAngle"),
            Weight = d.GetDouble("FontWeight", 400),
            FontName = d.GetName("FontName"),
            BBox = bbox,
        };
    }
}

/// <summary>Shared pieces of font loading: embedded programs, substitutes, ToUnicode maps, name and Unicode lookups.</summary>
internal static class FontSupport
{
    /// <summary>Largest font program accepted from a file.</summary>
    public const int MaxProgramBytes = 64 << 20;

    /// <summary>Largest CMap or ToUnicode stream accepted.</summary>
    public const int MaxCMapBytes = 8 << 20;

    private const int MaxSharedPrograms = 32;
    private static readonly ConcurrentDictionary<string, FontProgram> SharedPrograms = new(StringComparer.Ordinal);

    /// <summary>Decodes a stream within a byte bound; null (with a diagnostic) when it cannot be decoded or is too large.</summary>
    public static byte[]? ReadStream(FontContext ctx, PdfStream stream, int maxBytes, string what)
    {
        try
        {
            ReadOnlyMemory<byte> m = stream.GetDecodedBytes();
            if (m.Length > maxBytes)
            {
                ctx.Warn($"The {what} is larger than {maxBytes} bytes; ignored.");
                return null;
            }

            return m.ToArray();
        }
        catch (Exception ex) when (ex is PdfFormatException or PdfLimitException)
        {
            ctx.Warn($"The {what} could not be decoded ({ex.Message}); ignored.");
            return null;
        }
    }

    /// <summary>Opens the embedded program named by a descriptor (<c>FontFile</c>, <c>FontFile2</c>, <c>FontFile3</c>); null with a diagnostic when damaged, null silently when there is none.</summary>
    public static FontProgram? LoadEmbedded(FontContext ctx, DescriptorInfo desc, string fontName)
    {
        PdfDictionary? d = desc.Dictionary;
        if (d is null)
        {
            return null;
        }

        foreach (string key in (ReadOnlySpan<string>)["FontFile", "FontFile2", "FontFile3"])
        {
            if (d.Get(key) is not PdfStream s)
            {
                continue;
            }

            byte[]? bytes = ReadStream(ctx, s, MaxProgramBytes, $"{key} of font '{fontName}'");
            if (bytes is null)
            {
                continue;
            }

            try
            {
                return FontProgramLoader.Open(bytes);
            }
            catch (FontProgramException ex)
            {
                ctx.Warn($"Font '{fontName}': the embedded font program ({key}) could not be read ({ex.Message}); a substitute is used.");
            }
        }

        return null;
    }

    /// <summary>True when the descriptor has a font file stream under any of the three keys.</summary>
    public static bool HasEmbeddedStream(DescriptorInfo desc) =>
        desc.Dictionary is { } d && (d.Get("FontFile") is PdfStream || d.Get("FontFile2") is PdfStream || d.Get("FontFile3") is PdfStream);

    /// <summary>Asks the provider for a substitute and opens it; null when there is none or it does not parse.</summary>
    public static FontProgram? LoadSubstitute(FontContext ctx, PdfFontRequest request) =>
        LoadSubstitute(ctx, request, out _, out _);

    /// <summary>As the one-argument form, also opening the substitute's fallback fonts (those that fail to parse are left out) and returning its code point map.</summary>
    public static FontProgram? LoadSubstitute(FontContext ctx, PdfFontRequest request, out FontProgram[] fallbacks, out Func<int, int>? map)
    {
        fallbacks = [];
        map = null;
        PdfSubstituteFont? sub;
        try
        {
            sub = ctx.Provider.FindFont(request);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            sub = null;
        }

        if (sub is null)
        {
            return null;
        }

        FontProgram? primary = OpenSubstitute(ctx, sub);
        if (primary is not null)
        {
            map = sub.MapCodePoint;
            if (sub.Fallbacks.Count > 0)
            {
                var list = new List<FontProgram>(sub.Fallbacks.Count);
                foreach (PdfSubstituteFont f in sub.Fallbacks)
                {
                    if (OpenSubstitute(ctx, f) is { } fp)
                    {
                        list.Add(fp);
                    }
                }

                fallbacks = [.. list];
            }
        }

        return primary;
    }

    private static FontProgram? OpenSubstitute(FontContext ctx, PdfSubstituteFont sub)
    {
        try
        {
            if (sub.CacheKey is not null && SharedPrograms.TryGetValue(sub.CacheKey, out FontProgram? shared))
            {
                return shared;
            }

            ReadOnlyMemory<byte> data = sub.Data;
            FontProgram p = FontProgramLoader.Sniff(data.Span) is FontProgramKind.TrueType or FontProgramKind.OpenTypeCff
                ? SfntFont.Parse(data, sub.FaceIndex)
                : FontProgramLoader.Open(data);
            if (sub.CacheKey is not null && SharedPrograms.Count < MaxSharedPrograms)
            {
                SharedPrograms.TryAdd(sub.CacheKey, p);
            }

            return p;
        }
        catch (FontProgramException ex)
        {
            ctx.Warn($"The substitute font '{sub.Name}' could not be read ({ex.Message}).");
            return null;
        }
    }

    public static PdfFontRequest BuildRequest(string baseFont, DescriptorInfo desc, string? ordering, bool vertical)
    {
        string name = baseFont;
        if (name.Length > 7 && name[6] == '+')
        {
            bool tag = true;
            for (int i = 0; i < 6; i++)
            {
                tag &= name[i] is >= 'A' and <= 'Z';
            }

            if (tag)
            {
                name = name[7..];
            }
        }

        string? std = Core14.Resolve(name);
        int cut = name.IndexOfAny([',', '-']);
        string family = cut > 0 ? name[..cut] : name;
        foreach (string suffix in (ReadOnlySpan<string>)["PSMT", "MT", "PS"])
        {
            if (family.Length > suffix.Length && family.EndsWith(suffix, StringComparison.Ordinal))
            {
                family = family[..^suffix.Length];
                break;
            }
        }

        bool bold = name.Contains("Bold", StringComparison.OrdinalIgnoreCase) || name.Contains("Black", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Heavy", StringComparison.OrdinalIgnoreCase) || (desc.Flags & FontFlags.ForceBold) != 0 || desc.Weight >= 700;
        bool italic = name.Contains("Italic", StringComparison.OrdinalIgnoreCase) || name.Contains("Oblique", StringComparison.OrdinalIgnoreCase) ||
            (desc.Flags & FontFlags.Italic) != 0 || desc.ItalicAngle != 0;
        bool symbolic = std is "Symbol" or "ZapfDingbats" || (name.Contains("Symbol", StringComparison.OrdinalIgnoreCase) && (desc.Flags & FontFlags.Symbolic) != 0);
        return new PdfFontRequest
        {
            FontName = name,
            FamilyName = family,
            StandardName = std,
            IsBold = bold,
            IsItalic = italic,
            IsSerif = (desc.Flags & FontFlags.Serif) != 0 || (std is not null && std.StartsWith("Times", StringComparison.Ordinal)),
            IsFixedPitch = (desc.Flags & FontFlags.FixedPitch) != 0 || (std is not null && std.StartsWith("Courier", StringComparison.Ordinal)),
            IsSymbolic = symbolic,
            CidOrdering = ordering,
            IsVertical = vertical,
        };
    }

    /// <summary>Parses a <c>/ToUnicode</c> stream; null when absent or unusable.</summary>
    public static CMap? LoadToUnicode(FontContext ctx, PdfDictionary font)
    {
        if (font.Get("ToUnicode") is not PdfStream s)
        {
            return null;
        }

        byte[]? bytes = ReadStream(ctx, s, MaxCMapBytes, "ToUnicode CMap");
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return CMapParser.Parse(bytes);
        }
        catch (FontProgramException ex)
        {
            ctx.Warn($"The ToUnicode CMap could not be parsed ({ex.Message}); ignored.");
            return null;
        }
    }

    /// <summary>The ToUnicode text for a code, ignoring empty and NUL-only mappings that some producers write for unknown glyphs.</summary>
    public static bool TryToUnicode(CMap? map, uint code, int length, out string text)
    {
        text = string.Empty;
        if (map is null || !map.TryGetUnicode(code, length, out string t) || t.Length == 0 || (t.Length == 1 && t[0] == '\0'))
        {
            return false;
        }

        text = t;
        return true;
    }

    /// <summary>Glyph name to Unicode through the Adobe Glyph List (ZapfDingbats names first for that font).</summary>
    public static bool NameToUnicode(string? name, bool zapf, out string text)
    {
        text = string.Empty;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (zapf && GlyphList.TryGetZapfDingbats(name, out text))
        {
            return true;
        }

        return GlyphList.TryGetUnicode(name, out text);
    }

    public static int FirstRune(string text)
    {
        if (text.Length == 0)
        {
            return -1;
        }

        return char.IsHighSurrogate(text[0]) && text.Length > 1 && char.IsLowSurrogate(text[1]) ? char.ConvertToUtf32(text[0], text[1]) : text[0];
    }

    /// <summary>
    /// Finds a glyph in a program by name, then (for programs with a Unicode cmap) by the Unicode value of the text. Used for
    /// substitutes, whose glyph order and names are the provider's, not the PDF's. -1 when nothing matches.
    /// </summary>
    public static int FindGlyphByNameOrUnicode(FontProgram program, string? name, string? text, Func<int, int>? map = null)
    {
        int gid;
        if (name is not null)
        {
            gid = program.FindGlyph(name);
            if (gid > 0)
            {
                return gid;
            }
        }

        int cp = FirstRune(text ?? string.Empty);
        if (cp < 0)
        {
            return -1;
        }

        if (map is not null)
        {
            int mapped = map(cp);
            if (mapped != cp && mapped >= 0)
            {
                gid = FindByCodePoint(program, mapped);
                if (gid > 0)
                {
                    return gid;
                }
            }
        }

        return FindByCodePoint(program, cp);
    }

    private static int FindByCodePoint(FontProgram program, int cp)
    {
        if (program is SfntFont sfnt)
        {
            CmapSubtable? cmap = sfnt.FindUnicodeCmap();
            if (cmap is not null)
            {
                int g = cmap.Lookup(cp);
                if (g > 0)
                {
                    return g;
                }
            }
        }

        // Programs with glyph names but no cmap (Type 1, CFF): the AGL-style name of the character.
        string uni = cp <= 0xFFFF ? "uni" + cp.ToString("X4", CultureInfo.InvariantCulture) : "u" + cp.ToString("X", CultureInfo.InvariantCulture);
        int gid = program.FindGlyph(uni);
        return gid > 0 ? gid : -1;
    }
}

/// <summary>Glyph index to Unicode for an embedded program: from a Unicode cmap (lowest code wins) or glyph names. Built on first use.</summary>
internal sealed class ProgramUnicode
{
    private readonly FontProgram _program;
    private readonly Lazy<Dictionary<int, int>> _fromCmap;

    public ProgramUnicode(FontProgram program)
    {
        _program = program;
        _fromCmap = new Lazy<Dictionary<int, int>>(Build, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool TryGet(int gid, out string text)
    {
        if (_fromCmap.Value.TryGetValue(gid, out int cp) && cp > 0 && cp <= 0x10FFFF && cp is not (>= 0xD800 and <= 0xDFFF))
        {
            text = char.ConvertFromUtf32(cp);
            return true;
        }

        string? name = _program.GetGlyphName(gid);
        return FontSupport.NameToUnicode(name, false, out text);
    }

    private Dictionary<int, int> Build()
    {
        var map = new Dictionary<int, int>();
        if (_program is not SfntFont sfnt)
        {
            return map;
        }

        CmapSubtable? cmap = sfnt.FindUnicodeCmap();
        if (cmap is null)
        {
            return map;
        }

        try
        {
            foreach ((int code, int glyph) in cmap.Enumerate())
            {
                if (!map.TryGetValue(glyph, out int old) || code < old)
                {
                    map[glyph] = code;
                }
            }
        }
        catch (FontProgramException)
        {
            // A cmap damaged part-way: keep what was read.
        }

        return map;
    }
}
