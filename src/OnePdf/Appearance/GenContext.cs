using System.Linq;
using System.Globalization;

namespace OnePdf.Appearance;

/// <summary>Everything one generation needs: the document, the annotation and a sink for diagnostics.</summary>
internal sealed class GenContext
{
    public GenContext(PdfDocument doc, PdfDictionary annotation, Action<PdfSeverity, string> report)
    {
        Doc = doc;
        Annotation = annotation;
        Report = report;
        Cache = AppearanceCache.For(doc);
        Limits = doc.Limits;
        ObjectNumber = annotation.Reference?.Number ?? -1;
    }

    public PdfDocument Doc { get; }

    public PdfDictionary Annotation { get; }

    public Action<PdfSeverity, string> Report { get; }

    public AppearanceCache Cache { get; }

    public PdfLimits Limits { get; }

    public int ObjectNumber { get; }

    public void Warn(string message) => Report(PdfSeverity.Warning, message + Where());

    public void Info(string message) => Report(PdfSeverity.Info, message + Where());

    private string Where() => ObjectNumber >= 0 ? " (annotation " + ObjectNumber.ToString(CultureInfo.InvariantCulture) + " 0 R)" : string.Empty;

    /// <summary>A value of the dictionary or, following <c>/Parent</c> up to the recursion limit, of an ancestor (ISO 32000-2 12.7.4.1 inheritable field attributes).</summary>
    public PdfObject? Inherited(PdfDictionary d, string key)
    {
        PdfDictionary? cur = d;
        for (int depth = 0; cur is not null && depth <= Limits.MaxRecursionDepth; depth++)
        {
            if (cur.Get(key) is { } v and not PdfNull)
            {
                return v;
            }

            cur = cur.GetDictionary("Parent");
        }

        return null;
    }

    public static double[]? Components(PdfObject? o)
    {
        if (o is not PdfArray a || a.Count is not (1 or 3 or 4))
        {
            return null;
        }

        var c = new double[a.Count];
        for (int i = 0; i < c.Length; i++)
        {
            c[i] = a.GetDouble(i);
        }

        return c;
    }

    public static (double R, double G, double B) Rgb(double[] c) => c.Length switch
    {
        1 => (c[0], c[0], c[0]),
        3 => (c[0], c[1], c[2]),
        _ => (1 - Math.Min(1, c[0] + c[3]), 1 - Math.Min(1, c[1] + c[3]), 1 - Math.Min(1, c[2] + c[3])),
    };

    /// <summary>Text from a string, name, or text stream value.</summary>
    public static string? TextOf(PdfObject? o) => o switch
    {
        PdfString s => s.GetText(),
        PdfName n => n.Value,
        PdfStream st => PdfDocument.TextOf(st),
        _ => null,
    };

    /// <summary>The border width, style letter and dash array of an annotation (ISO 32000-2 12.5.4): <c>/BS</c>, else <c>/Border</c>, else a solid 1-unit line.</summary>
    public (double Width, char Style, double[] Dash) Border()
    {
        PdfDictionary? bs = Annotation.GetDictionary("BS");
        if (bs is not null)
        {
            double w = bs.ContainsKey("W") ? bs.GetDouble("W") : 1;
            char style = bs.GetName("S") is { Length: > 0 } s ? s[0] : 'S';
            return (SafeWidth(w), style, DashOf(bs.GetArray("D")));
        }

        if (Annotation.GetArray("Border") is { Count: >= 3 } b)
        {
            return (SafeWidth(b.GetDouble(2)), 'S', b.Count > 3 && b[3] is PdfArray d ? DashOf(d) : []);
        }

        return (1, 'S', []);
    }

    private static double SafeWidth(double w) => double.IsFinite(w) && w > 0 ? Math.Min(w, 1000) : 0;

    private static double[] DashOf(PdfArray? d)
    {
        if (d is null || d.Count == 0)
        {
            return [3];
        }

        var list = new List<double>();
        for (int i = 0; i < Math.Min(d.Count, 8); i++)
        {
            double v = d.GetDouble(i);
            if (double.IsFinite(v) && v >= 0)
            {
                list.Add(v);
            }
        }

        return list.Count == 0 || list.All(static x => x == 0) ? [3] : [.. list];
    }

    public static string DashOp(double[] dash) => dash.Length == 0 ? "[] 0 d\n" : "[" + string.Join(' ', dash.Select(ContentBuilder.Num)) + "] 0 d\n";

    /// <summary>Resolves the font chain for a <c>/DA</c>: the named font from the field's or the form's <c>/DR</c>, else the matching standard 14 face.</summary>
    public FontChain Chain(DefaultAppearance da)
    {
        PdfDictionary? fontDict = null;
        if (da.FontName is { } name)
        {
            fontDict = Inherited(Annotation, "DR") is PdfDictionary dr ? dr.GetDictionary("Font")?.GetDictionary(name) : null;
            fontDict ??= Doc.Catalog.GetDictionary("AcroForm")?.GetDictionary("DR")?.GetDictionary("Font")?.GetDictionary(name);
            if (fontDict is null)
            {
                Info("The font /" + name + " is not in the form's default resources; a standard font was used.");
            }
        }

        AppearanceFont? primary = null;
        if (fontDict is not null)
        {
            primary = Cache.Get(Doc, fontDict);
            if (!primary.Usable)
            {
                Info("The font /" + da.FontName + " is not a simple font; a standard font was used.");
                primary = null;
            }
        }

        primary ??= Cache.Get(Doc, Cache.Standard(AppearanceCache.StandardNameFor(da.FontName)));
        return new FontChain(primary, [Cache.Get(Doc, Cache.Standard("Helvetica")), Cache.Get(Doc, Cache.Standard("Symbol"))]);
    }

    public void NoteDropped(FontChain chain)
    {
        if (chain.Dropped > 0)
        {
            Warn(chain.Dropped.ToString(CultureInfo.InvariantCulture) + " character(s) could not be encoded in any available font and were left out");
        }
    }

    /// <summary>The form XObject dictionary around generated content.</summary>
    public static PdfDictionary Form(double[] bbox, PdfDictionary resources) =>
        Cos.Dict()
            .With("Type", Cos.Name("XObject"))
            .With("Subtype", Cos.Name("Form"))
            .With("BBox", Cos.Array(bbox))
            .With("Resources", resources);

    public static PdfDictionary Resources(FontChain? chain, HashSet<int>? used, PdfDictionary? extGState)
    {
        PdfDictionary r = Cos.Dict();
        if (chain is not null && used is { Count: > 0 })
        {
            PdfDictionary fonts = Cos.Dict();
            foreach (int i in used.Order())
            {
                fonts.Set(chain.Alias(i), chain[i].Dictionary);
            }

            r.Set("Font", fonts);
        }

        if (extGState is not null)
        {
            r.Set("ExtGState", extGState);
        }

        return r;
    }
}
