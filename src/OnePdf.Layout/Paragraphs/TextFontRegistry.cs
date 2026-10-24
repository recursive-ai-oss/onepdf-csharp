using System.Globalization;
using OneFont;
using OnePdf.Fonts;

namespace OnePdf.Layout;

/// <summary>One face known to a <see cref="TextFontRegistry"/>.</summary>
internal sealed class FontEntry
{
    private readonly Func<byte[]>? _loader;
    private readonly int _faceIndex;
    private byte[]? _data;
    private FontFace? _face;

    public FontEntry(string family, int weight, int width, bool italic, Func<byte[]> loader, int faceIndex)
    {
        Family = family;
        Weight = weight;
        Width = width;
        Italic = italic;
        _loader = loader;
        _faceIndex = faceIndex;
    }

    /// <summary>A standard-14 entry: measured with the metric-compatible substitute face, drawn with the standard font.</summary>
    public FontEntry(string standardName, FontEntry metricFace)
    {
        Family = standardName;
        Weight = metricFace.Weight;
        Width = metricFace.Width;
        Italic = metricFace.Italic;
        StandardName = standardName;
        MetricFace = metricFace;
    }

    public string Family { get; }

    public int Weight { get; }

    public int Width { get; }

    public bool Italic { get; }

    /// <summary>The PDF standard font name for an unshaped entry, else null.</summary>
    public string? StandardName { get; }

    private FontEntry? MetricFace { get; }

    public byte[] Data
    {
        get
        {
            if (MetricFace is not null)
            {
                return MetricFace.Data;
            }

            return _data ??= _loader!();
        }
    }

    public FontFace Face
    {
        get
        {
            if (MetricFace is not null)
            {
                return MetricFace.Face;
            }

            return _face ??= FontFace.Load(Data, _faceIndex);
        }
    }
}

/// <summary>The faces of one resolved style: the entries in preference order and, for shaped text, the OneFont chain over them.</summary>
internal sealed class ResolvedChain(FontEntry[] entries, FontFallbackChain? chain)
{
    public FontEntry[] Entries { get; } = entries;

    public FontFallbackChain? Chain { get; } = chain;

    public bool IsStandard => Chain is null;
}

/// <summary>
/// The fonts a paragraph can use: families built from font files, grouped by family, weight, width and style, plus the standard fonts by name.
/// </summary>
/// <remarks>
/// <para><see cref="CreateDefault"/> registers Liberation Sans, Serif and Mono (regular, bold, italic, bold italic) from the
/// <c>RecursiveAI.OnePdf.Fonts</c> package under their own names and the aliases <c>Sans</c>, <c>Serif</c>, <c>Mono</c> and <c>Monospace</c>.
/// Text set in them is shaped and embedded as a subset of the glyphs used.</para>
/// <para>A family name is looked up among the registered families, then the aliases, then the standard-14 names (<c>Helvetica</c>, <c>Arial</c>,
/// <c>Times-Roman</c>, <c>Courier-Bold</c>, ...), which are unshaped and not embedded. System fonts are never consulted unless
/// <see cref="UseSystemFonts"/> is called, so output does not depend on the machine.</para>
/// <para>Within a family the face with the right style is chosen first (italic or not), then the nearest width class, then the nearest weight;
/// for a requested weight of 500 or more, an equally near heavier face wins a tie. A registry may be shared by documents laid out one after
/// another or on several threads once its fonts are added; adding fonts while another thread lays out text is not supported.</para>
/// </remarks>
public sealed class TextFontRegistry
{
    private readonly object _gate = new();
    private readonly List<FontEntry> _entries = [];
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ResolvedChain> _chains = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FontEntry?> _systemCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FontEntry> _standard = new(StringComparer.Ordinal);
    private IPdfFontProvider? _system;

    /// <summary>Creates an empty registry. The standard-14 fonts still resolve by name.</summary>
    public TextFontRegistry()
    {
    }

    /// <summary>The family used when a style names none. Default <c>Sans</c> (an alias of Liberation Sans in <see cref="CreateDefault"/>).</summary>
    public string DefaultFamily { get; set; } = "Sans";

    /// <summary>Families appended to every style's chain, for characters its own families lack. Add the families of the scripts the document needs.</summary>
    public IList<string> FallbackFamilies { get; } = [];

    /// <summary>A registry of the default faces shared by documents that set no registry of their own. Do not add fonts to it.</summary>
    public static TextFontRegistry Shared { get; } = CreateDefault();

    /// <summary>Creates a registry holding the Liberation faces of the fonts package.</summary>
    public static TextFontRegistry CreateDefault()
    {
        var r = new TextFontRegistry();
        foreach (SubstituteFace f in SubstituteFonts.AllFaces)
        {
            if (!f.Family.StartsWith("Liberation", StringComparison.Ordinal))
            {
                continue;
            }

            SubstituteFace face = f;
            r._entries.Add(new FontEntry(face.Family, face.IsBold ? 700 : 400, 5, face.IsItalic, () => face.GetData().ToArray(), 0));
        }

        r.AddAlias("Sans", "Liberation Sans");
        r.AddAlias("Sans-Serif", "Liberation Sans");
        r.AddAlias("Serif", "Liberation Serif");
        r.AddAlias("Mono", "Liberation Mono");
        r.AddAlias("Monospace", "Liberation Mono");
        return r;
    }

    /// <summary>Registers a face from the bytes of a TrueType, OpenType or TrueType Collection file.</summary>
    /// <param name="fontData">The file. It is kept, not copied; do not change it afterwards.</param>
    /// <param name="family">The family name to register it under; the font's own family name when null.</param>
    /// <param name="weight">The weight class; the font's own when null.</param>
    /// <param name="italic">Whether it is italic; the font's own when null.</param>
    /// <param name="width">The width class, 1 to 9. Default 5.</param>
    /// <param name="faceIndex">Which face of a collection. Default 0.</param>
    /// <exception cref="FontFormatException">The font cannot be read.</exception>
    public void Add(byte[] fontData, string? family = null, int? weight = null, bool? italic = null, int width = 5, int faceIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(fontData);
        FontFace face = FontFace.Load(fontData, faceIndex);
        var entry = new FontEntry(family ?? face.FamilyName, weight ?? face.Weight, Math.Clamp(width, 1, 9), italic ?? face.IsItalic, () => fontData, faceIndex);
        lock (_gate)
        {
            _entries.Add(entry);
            _chains.Clear();
        }
    }

    /// <summary>Makes <paramref name="alias"/> another name for a registered family.</summary>
    public void AddAlias(string alias, string family)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        lock (_gate)
        {
            _aliases[alias.Trim()] = family.Trim();
            _chains.Clear();
        }
    }

    /// <summary>
    /// Lets family names that are not registered be looked up in <paramref name="provider"/> (for example a provider of installed fonts).
    /// Off by default; with it on, output can differ between machines.
    /// </summary>
    public void UseSystemFonts(IPdfFontProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_gate)
        {
            _system = provider;
            _chains.Clear();
        }
    }

    internal ResolvedChain Resolve(TextStyle style)
    {
        string key = ChainKey(style);
        lock (_gate)
        {
            if (_chains.TryGetValue(key, out ResolvedChain? cached))
            {
                return cached;
            }

            ResolvedChain made = Build(style);
            _chains[key] = made;
            return made;
        }
    }

    internal static string ChainKey(TextStyle style)
    {
        string fam = style.Families.Count == 0 ? string.Empty : string.Join('|', style.Families);
        return string.Create(CultureInfo.InvariantCulture, $"{fam}#{style.Weight}#{(style.Italic ? 1 : 0)}#{style.Width}");
    }

    private ResolvedChain Build(TextStyle style)
    {
        var entries = new List<FontEntry>();
        IReadOnlyList<string> names = style.Families.Count == 0 ? [DefaultFamily] : style.Families;
        for (int pass = 0; pass < 2 && entries.Count < FontFallbackChain.MaxFaces; pass++)
        {
            IEnumerable<string> list = pass == 0 ? names : FallbackFamilies;
            foreach (string name in list)
            {
                FontEntry? e = Find(name, style);
                if (e is null)
                {
                    continue;
                }

                if (e.StandardName is not null)
                {
                    if (entries.Count == 0 && pass == 0)
                    {
                        return new ResolvedChain([e], null);
                    }

                    continue;
                }

                if (!entries.Contains(e))
                {
                    entries.Add(e);
                }
            }
        }

        if (entries.Count == 0)
        {
            FontEntry? def = Find(DefaultFamily, style) ?? throw new InvalidOperationException($"No font family could be found for '{string.Join(", ", names)}', and the default family '{DefaultFamily}' is not registered.");
            if (def.StandardName is not null)
            {
                return new ResolvedChain([def], null);
            }

            entries.Add(def);
        }

        var faces = new FontFace[entries.Count];
        for (int i = 0; i < faces.Length; i++)
        {
            faces[i] = entries[i].Face;
        }

        return new ResolvedChain([.. entries], new FontFallbackChain(faces));
    }

    private FontEntry? Find(string name, TextStyle style)
    {
        string family = name.Trim();
        if (_aliases.TryGetValue(family, out string? real))
        {
            family = real;
        }

        FontEntry? best = null;
        long bestCost = long.MaxValue;
        foreach (FontEntry e in _entries)
        {
            if (!string.Equals(e.Family, family, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            long cost = Cost(e, style);
            if (cost < bestCost)
            {
                bestCost = cost;
                best = e;
            }
        }

        if (best is not null)
        {
            return best;
        }

        FontEntry? std = FindStandard(family, style);
        if (std is not null)
        {
            return std;
        }

        if (_system is not null)
        {
            string key = string.Create(CultureInfo.InvariantCulture, $"{family}#{style.Weight}#{(style.Italic ? 1 : 0)}");
            if (!_systemCache.TryGetValue(key, out FontEntry? sys))
            {
                sys = FromSystem(family, style);
                _systemCache[key] = sys;
            }

            return sys;
        }

        return null;
    }

    private static long Cost(FontEntry e, TextStyle style)
    {
        long cost = e.Italic != style.Italic ? 100_000_000L : 0;
        cost += Math.Abs(e.Width - style.Width) * 1_000_000L;
        int dw = e.Weight - style.Weight;
        cost += Math.Abs(dw) * 10L;
        if (dw < 0 && style.Weight >= 500)
        {
            cost += 1;
        }
        else if (dw > 0 && style.Weight < 500)
        {
            cost += 1;
        }

        return cost;
    }

    private FontEntry? FromSystem(string family, TextStyle style)
    {
        PdfSubstituteFont? found = _system!.FindFont(new PdfFontRequest
        {
            FontName = family,
            FamilyName = family,
            IsBold = style.Weight >= 600,
            IsItalic = style.Italic,
        });
        if (found is null)
        {
            return null;
        }

        byte[] bytes = found.Data.ToArray();
        FontFace face = FontFace.Load(bytes, found.FaceIndex);
        var entry = new FontEntry(family, face.Weight, 5, face.IsItalic, () => bytes, found.FaceIndex);
        _entries.Add(entry);
        return entry;
    }

    // Standard fonts: Helvetica/Arial, Times, Courier families, bold and italic from the name or the style. Measured with the
    // metric-compatible Liberation face, drawn with the PDF standard font.
    private FontEntry? FindStandard(string family, TextStyle style)
    {
        if (!SubstituteFonts.TryFind(family, out SubstituteFace face) || face.Family.StartsWith("Noto", StringComparison.Ordinal))
        {
            return null;
        }

        string lower = family.ToLowerInvariant();
        if (lower.Contains("symbol", StringComparison.Ordinal) || lower.Contains("dingbats", StringComparison.Ordinal))
        {
            return null;
        }

        bool bold = face.IsBold || style.Weight >= 600;
        bool italic = face.IsItalic || style.Italic;
        string[] names;
        switch (face.Family)
        {
            case "Liberation Serif":
                names = ["Times-Roman", "Times-Bold", "Times-Italic", "Times-BoldItalic"];
                break;
            case "Liberation Mono":
                names = ["Courier", "Courier-Bold", "Courier-Oblique", "Courier-BoldOblique"];
                break;
            default:
                names = ["Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique"];
                break;
        }

        string std = names[(bold ? 1 : 0) + (italic ? 2 : 0)];
        if (_standard.TryGetValue(std, out FontEntry? known))
        {
            return known;
        }

        SubstituteFace metric = face;
        foreach (SubstituteFace f in SubstituteFonts.AllFaces)
        {
            if (f.Family == face.Family && f.IsBold == bold && f.IsItalic == italic)
            {
                metric = f;
                break;
            }
        }

        SubstituteFace metricFace = metric;
        var backing = new FontEntry(metric.Family, bold ? 700 : 400, 5, italic, () => metricFace.GetData().ToArray(), 0);
        var entry = new FontEntry(std, backing);
        _standard[std] = entry;
        return entry;
    }
}
