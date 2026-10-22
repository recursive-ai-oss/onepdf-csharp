using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using OnePdf.FontPrograms;

namespace OnePdf.FontData;

/// <summary>One character of an Adobe Font Metrics file.</summary>
internal readonly record struct AfmGlyph(int Code, int Width, string Name, int LlX, int LlY, int UrX, int UrY);

/// <summary>
/// Metrics of one of the 14 standard fonts, read from the converted Adobe AFM data (widths, bounding boxes, ascent, descent;
/// kerning and composite data are not kept). Immutable, thread-safe.
/// </summary>
internal sealed class StandardFontMetrics
{
    private readonly Dictionary<string, int> _byName;

    private StandardFontMetrics(string name, AfmGlyph[] glyphs)
    {
        Name = name;
        Glyphs = glyphs;
        _byName = new Dictionary<string, int>(glyphs.Length, StringComparer.Ordinal);
        BuiltInEncoding = new string?[256];
        for (int i = 0; i < glyphs.Length; i++)
        {
            _byName[glyphs[i].Name] = i;
            if ((uint)glyphs[i].Code < 256)
            {
                BuiltInEncoding[glyphs[i].Code] = glyphs[i].Name;
            }
        }
    }

    /// <summary>PostScript name, such as <c>Helvetica-Bold</c>.</summary>
    public string Name { get; }

    public string FamilyName { get; private set; } = string.Empty;

    public string Weight { get; private set; } = string.Empty;

    public double ItalicAngle { get; private set; }

    public bool IsFixedPitch { get; private set; }

    /// <summary>FontBBox: llx, lly, urx, ury in 1000-unit glyph space.</summary>
    public int[] FontBBox { get; private set; } = new int[4];

    public int Ascender { get; private set; }

    public int Descender { get; private set; }

    public int CapHeight { get; private set; }

    public int XHeight { get; private set; }

    public int UnderlinePosition { get; private set; }

    public int UnderlineThickness { get; private set; }

    /// <summary>All characters in file order (encoded ones first, as in the AFM).</summary>
    public AfmGlyph[] Glyphs { get; }

    /// <summary>The font's built-in encoding: the AFM's <c>C</c> codes; unencoded codes are null. For Symbol and ZapfDingbats this is the encoding PDF uses.</summary>
    public string?[] BuiltInEncoding { get; }

    /// <summary>Looks a glyph up by name.</summary>
    public bool TryGetGlyph(string name, out AfmGlyph glyph)
    {
        if (_byName.TryGetValue(name, out int i))
        {
            glyph = Glyphs[i];
            return true;
        }

        glyph = default;
        return false;
    }

    /// <summary>The advance width of a named glyph in 1000-unit glyph space, or null.</summary>
    public int? GetWidth(string name) => _byName.TryGetValue(name, out int i) ? Glyphs[i].Width : null;

    internal static StandardFontMetrics Parse(string name, string text)
    {
        var glyphs = new List<AfmGlyph>(320);
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        bool inChars = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("StartCharMetrics", StringComparison.Ordinal))
            {
                inChars = true;
                continue;
            }

            if (line.StartsWith("EndCharMetrics", StringComparison.Ordinal))
            {
                inChars = false;
                continue;
            }

            if (inChars)
            {
                glyphs.Add(ParseChar(line));
                continue;
            }

            int sp = line.IndexOf(' ', StringComparison.Ordinal);
            if (sp > 0)
            {
                keys.TryAdd(line[..sp], line[(sp + 1)..].Trim());
            }
        }

        var m = new StandardFontMetrics(name, [.. glyphs])
        {
            FamilyName = keys.GetValueOrDefault("FamilyName", string.Empty),
            Weight = keys.GetValueOrDefault("Weight", string.Empty),
            ItalicAngle = keys.TryGetValue("ItalicAngle", out string? ia) ? double.Parse(ia, CultureInfo.InvariantCulture) : 0,
            IsFixedPitch = keys.GetValueOrDefault("IsFixedPitch") == "true",
            Ascender = Int(keys, "Ascender"),
            Descender = Int(keys, "Descender"),
            CapHeight = Int(keys, "CapHeight"),
            XHeight = Int(keys, "XHeight"),
            UnderlinePosition = Int(keys, "UnderlinePosition"),
            UnderlineThickness = Int(keys, "UnderlineThickness"),
        };
        if (keys.TryGetValue("FontBBox", out string? bb))
        {
            string[] p = bb.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < 4 && i < p.Length; i++)
            {
                m.FontBBox[i] = int.Parse(p[i], CultureInfo.InvariantCulture);
            }
        }

        return m;
    }

    private static int Int(Dictionary<string, string> keys, string key) =>
        keys.TryGetValue(key, out string? v) ? (int)double.Parse(v, CultureInfo.InvariantCulture) : 0;

    private static AfmGlyph ParseChar(string line)
    {
        int code = -1, wx = 0, llx = 0, lly = 0, urx = 0, ury = 0;
        string name = string.Empty;
        foreach (string part in line.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] t = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 2)
            {
                continue;
            }

            switch (t[0])
            {
                case "C":
                    code = int.Parse(t[1], CultureInfo.InvariantCulture);
                    break;
                case "WX":
                    wx = (int)double.Parse(t[1], CultureInfo.InvariantCulture);
                    break;
                case "N":
                    name = t[1];
                    break;
                case "B" when t.Length >= 5:
                    llx = int.Parse(t[1], CultureInfo.InvariantCulture);
                    lly = int.Parse(t[2], CultureInfo.InvariantCulture);
                    urx = int.Parse(t[3], CultureInfo.InvariantCulture);
                    ury = int.Parse(t[4], CultureInfo.InvariantCulture);
                    break;
            }
        }

        return new AfmGlyph(code, wx, name, llx, lly, urx, ury);
    }
}

/// <summary>The 14 standard fonts' metrics (ISO 32000-1 9.6.2.2) and the name aliases PDF producers use for them.</summary>
internal static class Core14
{
    /// <summary>The PostScript names of the 14 fonts.</summary>
    public static readonly string[] Names =
    [
        "Courier", "Courier-Bold", "Courier-Oblique", "Courier-BoldOblique",
        "Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique",
        "Times-Roman", "Times-Bold", "Times-Italic", "Times-BoldItalic",
        "Symbol", "ZapfDingbats",
    ];

    private static readonly ConcurrentDictionary<string, StandardFontMetrics> Cache = new(StringComparer.Ordinal);

    /// <summary>Loads the metrics of one of <see cref="Names"/> (exact name).</summary>
    public static StandardFontMetrics Get(string standardName)
    {
        if (Array.IndexOf(Names, standardName) < 0)
        {
            throw new ArgumentException("Not one of the 14 standard fonts.", nameof(standardName));
        }

        return Cache.GetOrAdd(standardName, static n =>
        {
            byte[] raw = FontResources.Read("afm-" + n + ".bin", 1 << 20);
            return StandardFontMetrics.Parse(n, Encoding.Latin1.GetString(raw));
        });
    }

    /// <summary>
    /// Resolves a <c>/BaseFont</c> name to a standard font name: exact names, a subset prefix (<c>ABCDEF+</c>), and the common
    /// aliases (Arial, ArialMT, Arial-BoldMT, TimesNewRoman, TimesNewRomanPS-BoldItalicMT, CourierNew, with <c>,Bold</c> style
    /// suffixes). Returns null when the name is not a standard font or alias.
    /// </summary>
    public static string? Resolve(string baseFont)
    {
        if (string.IsNullOrEmpty(baseFont))
        {
            return null;
        }

        string n = baseFont;
        if (n.Length > 7 && n[6] == '+' && IsSubsetTag(n))
        {
            n = n[7..];
        }

        n = n.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (Array.IndexOf(Names, n) >= 0)
        {
            return n;
        }

        string family;
        string style;
        int comma = n.IndexOf(',', StringComparison.Ordinal);
        int dash = n.IndexOf('-', StringComparison.Ordinal);
        if (comma >= 0)
        {
            family = n[..comma];
            style = n[(comma + 1)..];
        }
        else if (dash >= 0)
        {
            family = n[..dash];
            style = n[(dash + 1)..];
        }
        else
        {
            family = n;
            style = string.Empty;
        }

        // Windows-style suffixes: MT, PS, PSMT.
        string fam = family;
        foreach (string suffix in new[] { "PSMT", "MT", "PS" })
        {
            if (fam.EndsWith(suffix, StringComparison.Ordinal) && fam.Length > suffix.Length)
            {
                fam = fam[..^suffix.Length];
                break;
            }
        }

        string st = style;
        foreach (string suffix in new[] { "PSMT", "MT", "PS" })
        {
            if (st.EndsWith(suffix, StringComparison.Ordinal) && st.Length > suffix.Length)
            {
                st = st[..^suffix.Length];
                break;
            }
        }

        bool bold = st.Contains("Bold", StringComparison.OrdinalIgnoreCase);
        bool italic = st.Contains("Italic", StringComparison.OrdinalIgnoreCase) || st.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
        switch (fam)
        {
            case "Arial":
            case "Helvetica":
                return bold ? (italic ? "Helvetica-BoldOblique" : "Helvetica-Bold") : (italic ? "Helvetica-Oblique" : "Helvetica");
            case "TimesNewRoman":
            case "Times":
                if (st.Length == 0 || st == "Roman" || bold || italic)
                {
                    return bold ? (italic ? "Times-BoldItalic" : "Times-Bold") : (italic ? "Times-Italic" : "Times-Roman");
                }

                return null;
            case "CourierNew":
            case "Courier":
                return bold ? (italic ? "Courier-BoldOblique" : "Courier-Bold") : (italic ? "Courier-Oblique" : "Courier");
            case "Symbol":
                return st.Length == 0 ? "Symbol" : null;
            case "ZapfDingbats":
                return st.Length == 0 ? "ZapfDingbats" : null;
            default:
                return null;
        }
    }

    private static bool IsSubsetTag(string n)
    {
        for (int i = 0; i < 6; i++)
        {
            if (n[i] is < 'A' or > 'Z')
            {
                return false;
            }
        }

        return true;
    }
}
