namespace OnePdf.Content;

/// <summary>
/// Evaluates tint transforms (Separation and DeviceN alternate-space functions). The function evaluators live in their
/// own area; until they are connected, no function is evaluated and the caller passes the tint components through the
/// alternate space with a diagnostic.
/// </summary>
internal static class TintTransform
{
    /// <summary>Evaluates <paramref name="function"/> on <paramref name="input"/>. Returns false when the function cannot be evaluated.</summary>
    public static bool TryEvaluate(PdfObject? function, ReadOnlySpan<double> input, Span<double> output)
    {
        _ = function;
        _ = input;
        _ = output;
        return false;
    }
}

/// <summary>A colour space as the content stream names it, able to turn operands into a colour in a final space.</summary>
internal abstract class ColorSpaceDef
{
    /// <summary>Operands per colour.</summary>
    public abstract int N { get; }

    /// <summary>The initial colour after the space is selected (ISO 32000-2 8.6.3).</summary>
    public abstract PdfColor Initial(ColorContext ctx);

    /// <summary>Converts operands to a colour in a final space.</summary>
    public abstract PdfColor Convert(ReadOnlySpan<double> c, ColorContext ctx);

    /// <summary>The space a stream drawn in this space ends up in (for images this is informational).</summary>
    public abstract PdfColorSpace Final { get; }

    protected static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : (double.IsNaN(v) ? 0 : v));

    protected static PdfColor Make(PdfColorSpace s, ReadOnlySpan<double> c)
    {
        double c0 = c.Length > 0 ? c[0] : 0, c1 = c.Length > 1 ? c[1] : 0, c2 = c.Length > 2 ? c[2] : 0, c3 = c.Length > 3 ? c[3] : 0;
        return new PdfColor(s, c0, c1, c2, c3);
    }
}

/// <summary>Callbacks colour conversion needs from the interpreter.</summary>
internal sealed class ColorContext
{
    public Action<string>? Note { get; init; }
}

internal sealed class DeviceColorSpaceDef(PdfColorSpace space) : ColorSpaceDef
{
    public override int N => space.ComponentCount;

    public override PdfColorSpace Final => space;

    public override PdfColor Initial(ColorContext ctx) =>
        space.Kind == PdfColorSpaceKind.DeviceCmyk ? new PdfColor(space, 0, 0, 0, 1) : new PdfColor(space, 0, 0, 0, 0);

    public override PdfColor Convert(ReadOnlySpan<double> c, ColorContext ctx)
    {
        Span<double> t = stackalloc double[4];
        for (int i = 0; i < space.ComponentCount; i++)
        {
            t[i] = i < c.Length ? Clamp01(c[i]) : 0;
        }

        return Make(space, t);
    }

    public static readonly DeviceColorSpaceDef Gray = new(PdfColorSpace.Gray);
    public static readonly DeviceColorSpaceDef Rgb = new(PdfColorSpace.Rgb);
    public static readonly DeviceColorSpaceDef Cmyk = new(PdfColorSpace.Cmyk);
}

internal sealed class LabColorSpaceDef(PdfColorSpace space) : ColorSpaceDef
{
    public override int N => 3;

    public override PdfColorSpace Final => space;

    public PdfColor Clamped(ReadOnlySpan<double> c)
    {
        ReadOnlySpan<double> r = space.Range.Span;
        double l = Math.Clamp(c.Length > 0 ? c[0] : 0, 0, 100);
        double a = Math.Clamp(c.Length > 1 ? c[1] : 0, r[0], r[1]);
        double b = Math.Clamp(c.Length > 2 ? c[2] : 0, r[2], r[3]);
        return new PdfColor(space, l, a, b, 0);
    }

    public override PdfColor Initial(ColorContext ctx) => Clamped([0, 0, 0]);

    public override PdfColor Convert(ReadOnlySpan<double> c, ColorContext ctx) => Clamped(c);
}

internal sealed class IccColorSpaceDef(PdfColorSpace space) : ColorSpaceDef
{
    public override int N => space.ComponentCount;

    public override PdfColorSpace Final => space;

    public override PdfColor Initial(ColorContext ctx)
    {
        // All zeros clamped to the range; for four components the conventional initial colour is black.
        return space.ComponentCount == 4 ? new PdfColor(space, 0, 0, 0, 1) : new PdfColor(space, 0, 0, 0, 0);
    }

    public override PdfColor Convert(ReadOnlySpan<double> c, ColorContext ctx)
    {
        ReadOnlySpan<double> r = space.Range.Span;
        Span<double> t = stackalloc double[4];
        for (int i = 0; i < space.ComponentCount && i < 4; i++)
        {
            double lo = 2 * i + 1 < r.Length ? r[2 * i] : 0;
            double hi = 2 * i + 1 < r.Length ? r[(2 * i) + 1] : 1;
            double v = i < c.Length ? c[i] : 0;
            t[i] = double.IsNaN(v) ? lo : Math.Clamp(v, lo, hi);
        }

        return Make(space, t);
    }
}

internal sealed class IndexedColorSpaceDef(ColorSpaceDef baseSpace, int hival, byte[] table) : ColorSpaceDef
{
    public override int N => 1;

    public override PdfColorSpace Final => baseSpace.Final;

    public override PdfColor Initial(ColorContext ctx) => Lookup(0, ctx);

    public override PdfColor Convert(ReadOnlySpan<double> c, ColorContext ctx) =>
        Lookup(c.Length > 0 && !double.IsNaN(c[0]) ? (int)Math.Clamp(Math.Round(c[0]), 0, hival) : 0, ctx);

    private PdfColor Lookup(int index, ColorContext ctx)
    {
        int n = baseSpace.N;
        Span<double> comps = stackalloc double[Math.Min(n, 32)];
        for (int j = 0; j < comps.Length; j++)
        {
            int at = (index * n) + j;
            double v = at < table.Length ? table[at] / 255.0 : 0;
            comps[j] = baseSpace is LabColorSpaceDef lab ? DecodeLab(lab, j, v) : v;
        }

        return baseSpace.Convert(comps, ctx);
    }

    private static double DecodeLab(LabColorSpaceDef lab, int j, double v)
    {
        ReadOnlySpan<double> r = lab.Final.Range.Span;
        return j switch
        {
            0 => v * 100,
            1 => r[0] + (v * (r[1] - r[0])),
            _ => r[2] + (v * (r[3] - r[2])),
        };
    }
}

/// <summary>Separation and DeviceN: converted to the alternate space through the tint transform.</summary>
internal sealed class TintColorSpaceDef(int n, ColorSpaceDef alternate, PdfObject? function) : ColorSpaceDef
{
    public override int N => n;

    public override PdfColorSpace Final => alternate.Final;

    public override PdfColor Initial(ColorContext ctx)
    {
        Span<double> ones = stackalloc double[Math.Min(n, 32)];
        ones.Fill(1);
        return Convert(ones, ctx);
    }

    public override PdfColor Convert(ReadOnlySpan<double> c, ColorContext ctx)
    {
        int an = alternate.N;
        Span<double> alt = stackalloc double[Math.Min(Math.Max(an, 1), 32)];
        Span<double> input = stackalloc double[Math.Min(Math.Max(n, 1), 32)];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = i < c.Length ? Clamp01(c[i]) : 0;
        }

        if (!TintTransform.TryEvaluate(function, input, alt))
        {
            ctx.Note?.Invoke("A tint transform was not evaluated; the tint was passed through the alternate colour space.");
            alt.Clear();
            for (int i = 0; i < alt.Length && i < input.Length; i++)
            {
                alt[i] = input[i];
            }
        }

        return alternate.Convert(alt, ctx);
    }
}

internal sealed class PatternColorSpaceDef(ColorSpaceDef? baseSpace, PdfColorSpace space) : ColorSpaceDef
{
    public ColorSpaceDef? Base => baseSpace;

    public override int N => baseSpace?.N ?? 0;

    public override PdfColorSpace Final => space;

    public override PdfColor Initial(ColorContext ctx) => new(space, 0, 0, 0, 0);

    public override PdfColor Convert(ReadOnlySpan<double> c, ColorContext ctx)
    {
        if (baseSpace is null)
        {
            return new PdfColor(space, 0, 0, 0, 0);
        }

        PdfColor tint = baseSpace.Convert(c, ctx);
        return new PdfColor(space, tint.C0, tint.C1, tint.C2, tint.C3);
    }
}

/// <summary>Parses colour space objects, caching per document object.</summary>
internal sealed class ColorSpaceParser(PdfDocument doc, Action<string> warn)
{
    private const int MaxDepth = 8;
    private readonly Dictionary<int, ColorSpaceDef> _byObject = [];
    private readonly Dictionary<string, (PdfDictionary Res, ColorSpaceDef Def)> _byName = new(StringComparer.Ordinal);
    private readonly PdfColorSpace _pattern = new(PdfColorSpaceKind.Pattern, 0);

    public ColorSpaceDef? ParseNamed(string name, PdfDictionary? resources)
    {
        switch (name)
        {
            case "DeviceGray":
            case "G":
            case "CalGray":
                return DeviceColorSpaceDef.Gray;
            case "DeviceRGB":
            case "RGB":
            case "CalRGB":
                return DeviceColorSpaceDef.Rgb;
            case "DeviceCMYK":
            case "CMYK":
                return DeviceColorSpaceDef.Cmyk;
            case "Pattern":
                return new PatternColorSpaceDef(null, _pattern);
            default:
                break;
        }

        PdfDictionary? spaces = resources?.GetDictionary("ColorSpace");
        if (spaces is null || !spaces.ContainsKey(name))
        {
            return null;
        }

        if (_byName.TryGetValue(name, out var hit) && ReferenceEquals(hit.Res, spaces))
        {
            return hit.Def;
        }

        ColorSpaceDef? def = Parse(spaces.GetRaw(name), resources, 0);
        if (def is not null)
        {
            _byName[name] = (spaces, def);
        }

        return def;
    }

    public ColorSpaceDef? Parse(PdfObject? raw, PdfDictionary? resources, int depth)
    {
        if (depth > MaxDepth)
        {
            warn("Colour spaces are nested too deeply.");
            return null;
        }

        int refNumber = raw is PdfReference rr ? rr.Number : -1;
        if (refNumber >= 0 && _byObject.TryGetValue(refNumber, out ColorSpaceDef? cached))
        {
            return cached;
        }

        PdfObject? o = doc.Resolve(raw);
        ColorSpaceDef? def = null;
        switch (o)
        {
            case PdfName n:
                // A name that is a key in the resources would loop back to itself; only the built-in names are followed here.
                def = depth == 0 || n.Value is "DeviceGray" or "DeviceRGB" or "DeviceCMYK" or "Pattern" or "G" or "RGB" or "CMYK"
                    ? ParseNamed(n.Value, resources)
                    : null;
                break;
            case PdfArray a when a.Count > 0 && a[0] is PdfName kind:
                def = ParseArray(kind.Value, a, resources, depth);
                break;
            default:
                break;
        }

        if (def is not null && refNumber >= 0)
        {
            _byObject[refNumber] = def;
        }

        return def;
    }

    private ColorSpaceDef? ParseArray(string kind, PdfArray a, PdfDictionary? resources, int depth)
    {
        switch (kind)
        {
            case "DeviceGray":
            case "G":
            case "CalGray":
            case "DeviceRGB":
            case "RGB":
            case "CalRGB":
            case "DeviceCMYK":
            case "CMYK":
                return ParseNamed(kind, resources);
            case "Lab":
                {
                    PdfDictionary? d = a.Count > 1 ? a[1] as PdfDictionary : null;
                    double[] wp = ReadNumbers(d?.GetArray("WhitePoint"), 3, [0.9505, 1, 1.089]);
                    double[] range = ReadNumbers(d?.GetArray("Range"), 4, [-100, 100, -100, 100]);
                    if (range[0] > range[1] || range[2] > range[3])
                    {
                        range = [-100, 100, -100, 100];
                    }

                    return new LabColorSpaceDef(new PdfColorSpace(PdfColorSpaceKind.Lab, 3) { WhitePoint = wp, Range = range });
                }

            case "ICCBased":
                return ParseIcc(a);
            case "Indexed":
            case "I":
                {
                    if (a.Count < 4)
                    {
                        return null;
                    }

                    ColorSpaceDef? b = Parse(a.GetRaw(1), resources, depth + 1);
                    if (b is null or IndexedColorSpaceDef or PatternColorSpaceDef)
                    {
                        return null;
                    }

                    int hival = (int)Math.Clamp(a.GetInt(2), 0, 255);
                    byte[] table = ReadTable(a[3]);
                    return new IndexedColorSpaceDef(b, hival, table);
                }

            case "Separation":
                {
                    if (a.Count < 4)
                    {
                        return null;
                    }

                    ColorSpaceDef? alt = Parse(a.GetRaw(2), resources, depth + 1);
                    return alt is null or PatternColorSpaceDef ? null : new TintColorSpaceDef(1, alt, a[3]);
                }

            case "DeviceN":
                {
                    if (a.Count < 4 || a[1] is not PdfArray names)
                    {
                        return null;
                    }

                    ColorSpaceDef? alt = Parse(a.GetRaw(2), resources, depth + 1);
                    if (alt is null or PatternColorSpaceDef || names.Count < 1 || names.Count > 32)
                    {
                        return null;
                    }

                    return new TintColorSpaceDef(names.Count, alt, a[3]);
                }

            case "Pattern":
                {
                    ColorSpaceDef? b = a.Count > 1 ? Parse(a.GetRaw(1), resources, depth + 1) : null;
                    return new PatternColorSpaceDef(b, new PdfColorSpace(PdfColorSpaceKind.Pattern, b?.N ?? 0) { PatternBase = b?.Final });
                }

            default:
                return null;
        }
    }

    private ColorSpaceDef? ParseIcc(PdfArray a)
    {
        if (a.Count < 2 || a[1] is not PdfStream s)
        {
            return null;
        }

        int n = (int)s.Dictionary.GetInt("N", 0);
        if (n is not (1 or 3 or 4))
        {
            // A wrong N: believe the alternate space when there is one.
            PdfObject? alt = s.Dictionary.Get("Alternate");
            ColorSpaceDef? altDef = alt is null ? null : Parse(alt, null, MaxDepth - 1);
            if (altDef is not null)
            {
                warn("An ICC profile stream has an invalid /N; its alternate space is used.");
                return altDef;
            }

            warn("An ICC profile stream has an invalid /N and no alternate; DeviceGray is used.");
            return DeviceColorSpaceDef.Gray;
        }

        ReadOnlyMemory<byte> profile = default;
        try
        {
            profile = s.GetDecodedBytes();
        }
        catch (PdfException e)
        {
            warn("An ICC profile could not be read (" + e.Message + "); the component-count fallback is used.");
        }

        PdfColorSpace fallback = n == 1 ? PdfColorSpace.Gray : (n == 3 ? PdfColorSpace.Rgb : PdfColorSpace.Cmyk);
        double[] range = ReadNumbers(s.Dictionary.GetArray("Range"), n * 2, DefaultRange(n));
        var space = new PdfColorSpace(PdfColorSpaceKind.IccBased, n) { IccProfile = profile, IccFallback = fallback, Range = range };
        return new IccColorSpaceDef(space);
    }

    private static double[] DefaultRange(int n)
    {
        var r = new double[n * 2];
        for (int i = 0; i < n; i++)
        {
            r[(2 * i) + 1] = 1;
        }

        return r;
    }

    private static double[] ReadNumbers(PdfArray? a, int count, double[] fallback)
    {
        if (a is null || a.Count < count)
        {
            return fallback;
        }

        var r = new double[count];
        for (int i = 0; i < count; i++)
        {
            r[i] = a.GetDouble(i);
        }

        return r;
    }

    private byte[] ReadTable(PdfObject? o)
    {
        try
        {
            return o switch
            {
                PdfString s => s.Bytes.ToArray(),
                PdfStream st => st.GetDecodedBytes().ToArray(),
                _ => [],
            };
        }
        catch (PdfException e)
        {
            warn("An Indexed colour table could not be read (" + e.Message + ").");
            return [];
        }
    }
}
