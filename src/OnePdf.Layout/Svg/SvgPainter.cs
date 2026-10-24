using System.Globalization;
using OnePdf.Layout.Svg.Dom;

namespace OnePdf.Layout.Svg;

internal enum Axis
{
    X,
    Y,
    Other,
}

/// <summary>The size percentages refer to.</summary>
internal readonly record struct Viewport(double Width, double Height, double X = 0, double Y = 0);

/// <summary>
/// Draws a parsed SVG document into a content builder as PDF vector content. It walks the element tree with a depth counter (never deeper
/// than <see cref="MaxDepth"/> elements at once), keeps at most <see cref="MaxSaved"/> graphics states open (the builder allows 28), and
/// counts every instantiated element against <see cref="SvgLimits.MaxInstances"/>, so a document of nested <c>use</c> elements cannot run away.
/// The user space of the SVG (y down, CSS pixels) is the builder's user space after one initial matrix, so coordinates are written as they are
/// in the document and the content builder's matrix does the rest.
/// </summary>
internal sealed partial class SvgPainter
{
    private const int MaxDepth = 80;
    private const int MaxSaved = 22;
    private const int MaxGroups = 20_000;
    private const double Huge = 1e7;

    private readonly SvgImage _svg;
    private readonly SvgDocument _doc;
    private readonly SvgLimits _lim;
    private readonly ISvgHost _host;
    private readonly CancellationToken _ct;
    private readonly HashSet<SvgElement> _active = [];
    private long _textTotal;
    private long _boundsWork;
    private readonly HashSet<string> _noted = new(StringComparer.Ordinal);
    private readonly List<string> _notes = [];
    private readonly Dictionary<SvgElement, SvgStyle> _styles = [];
    private readonly PathData.PathBudget _paths;
    private PdfContentBuilder _b = null!;
    private int _saved;
    private int _depth;
    private int _instances;
    private int _groups;
    private long _imageBytes;
    private double _rootFont = SvgStyle.DefaultFontSize;
    private bool _clipMode;
    private Paint? _ctxFill;
    private Paint? _ctxStroke;

    public SvgPainter(SvgImage svg, ISvgHost host, CancellationToken ct)
    {
        _svg = svg;
        _doc = svg.Document;
        _lim = svg.Limits;
        _host = host;
        _ct = ct;
        _paths = new PathData.PathBudget(_lim.MaxPathSegments);
    }

    public IReadOnlyList<string> Notes => _notes;

    /// <summary>Draws the picture so that its natural box maps to the given box (x, y = lower left corner, PDF coordinates).</summary>
    public void Render(PdfContentBuilder builder, double x, double y, double width, double height, (double X, double Y, double W, double H)? clipBox)
    {
        _b = builder;
        _saved = 0;
        SvgElement root = _doc.Root;
        SvgStyle first = SvgStyle.Compute(root, SvgStyle.WithMediumFontSize(SvgStyle.DefaultFontSize), SvgStyle.DefaultFontSize);
        _rootFont = first.FontSize;
        SvgStyle initial = SvgStyle.WithMediumFontSize(SvgStyle.DefaultFontSize);
        Open();
        try
        {
            if (clipBox is { } c)
            {
                _b.Rectangle(c.X, c.Y, c.W, c.H);
                _b.Clip();
                _b.EndPath();
            }

            // CSS pixels, y down, to the box: the picture's top edge is the box's top edge.
            _b.Transform(width / _svg.WidthPx, 0, 0, -height / _svg.HeightPx, x, y + height);
            _b.Rectangle(0, 0, _svg.WidthPx, _svg.HeightPx);
            _b.Clip();
            _b.EndPath();
            if (root.Prop("background-color") is { } bg && CssColor.TryParse(bg, out Rgba32 bgColor) && bgColor.A > 0)
            {
                var back = new SvgPath();
                back.MoveTo(0, 0);
                back.LineTo(_svg.WidthPx, 0);
                back.LineTo(_svg.WidthPx, _svg.HeightPx);
                back.LineTo(0, _svg.HeightPx);
                back.Close();
                FillSolid(back, SvgStyle.Initial, bgColor);
            }

            DrawElement(root, initial, new Viewport(_svg.WidthPx, _svg.HeightPx), null, null, true);
        }
        finally
        {
            Close();
        }
    }

    // ---- bookkeeping ----

    private void Note(string message)
    {
        if (_noted.Add(message))
        {
            _notes.Add(message);
            _host.Note(message);
        }
    }

    private bool Open()
    {
        if (_saved >= MaxSaved)
        {
            Note("Elements are nested too deeply to draw: more than " + MaxSaved + " levels of transform, clip, opacity or viewport.");
            return false;
        }

        _b.Save();
        _saved++;
        return true;
    }

    private void Close()
    {
        _b.Restore();
        _saved--;
    }

    private bool Enter()
    {
        if ((_instances & 0xFF) == 0)
        {
            _ct.ThrowIfCancellationRequested();
        }

        if (++_instances > _lim.MaxInstances)
        {
            throw new PdfLimitException($"The SVG instantiates more than {_lim.MaxInstances} elements (check use references that multiply).");
        }

        if (_depth >= MaxDepth)
        {
            Note("Elements are nested more than " + MaxDepth + " levels deep (counting use references); the rest is left out.");
            return false;
        }

        _depth++;
        return true;
    }

    // ---- lengths and styles ----

    private double Resolve(Length l, Axis axis, in SvgStyle st, Viewport vp) => Coord.D(l.Unit switch
    {
        LengthUnit.Px => l.Value,
        LengthUnit.Em => l.Value * st.FontSize,
        LengthUnit.Ex => l.Value * st.FontSize / 2,
        LengthUnit.Rem => l.Value * _rootFont,
        _ => l.Value / 100 * (axis == Axis.X ? vp.Width : axis == Axis.Y ? vp.Height : Math.Sqrt(((vp.Width * vp.Width) + (vp.Height * vp.Height)) / 2)),
    });

    private static Length ParseLength(string? v) => v is not null && Length.TryParse(v, out Length l) ? l : Length.Zero;

    private double Num(SvgElement e, string name, Axis axis, in SvgStyle st, Viewport vp) => Resolve(ParseLength(e.Attr(name)), axis, st, vp);

    private double NumOr(SvgElement e, string name, Axis axis, in SvgStyle st, Viewport vp, double fallbackPercent)
    {
        string? v = e.Attr(name);
        return v is not null && Length.TryParse(v, out Length l) ? Resolve(l, axis, st, vp) : Resolve(new Length(fallbackPercent, LengthUnit.Percent), axis, st, vp);
    }

    /// <summary>The computed style of an element reached by reference (a gradient, marker or mask child): inherited from its own ancestors.</summary>
    private SvgStyle StyleOf(SvgElement e)
    {
        if (_styles.TryGetValue(e, out SvgStyle cached))
        {
            return cached;
        }

        var chain = new Stack<SvgElement>();
        for (SvgElement? p = e; p is not null; p = p.Parent)
        {
            chain.Push(p);
            if (chain.Count > 300)
            {
                break;
            }
        }

        SvgStyle s = SvgStyle.WithMediumFontSize(SvgStyle.DefaultFontSize);
        while (chain.Count > 0)
        {
            s = SvgStyle.Compute(chain.Pop(), s, _rootFont);
        }

        _styles[e] = s;
        return s;
    }

    // SVG 1.1 5.8: an element with requiredExtensions, requiredFeatures or systemLanguage renders only when all are true.
    private static bool Conditions(SvgElement e)
    {
        if (e.Attrs.Count == 0)
        {
            return true;
        }

        if (e.Attrs.ContainsKey("requiredExtensions"))
        {
            return false;
        }

        if (e.Attrs.TryGetValue("requiredFeatures", out string? features) && features.Trim().Length == 0)
        {
            return false;
        }

        if (e.Attrs.TryGetValue("systemLanguage", out string? languages))
        {
            foreach (Range r in languages.AsSpan().Split(','))
            {
                ReadOnlySpan<char> tag = languages.AsSpan(r).Trim();
                if (tag.Equals("en", StringComparison.OrdinalIgnoreCase) || (tag.Length > 2 && tag[2] == '-' && tag[..2].Equals("en", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }

        return true;
    }

    private static Affine TransformOf(SvgElement e)
    {
        string? t = e.Prop("transform")?.Replace("px", string.Empty, StringComparison.Ordinal).Replace("deg", string.Empty, StringComparison.Ordinal) ?? e.Attr("transform");
        return t is not null && SvgValues.TryTransform(t, out Affine a) && a.IsFinite ? a : Affine.Identity;
    }

    /// <summary>The transform of an element acting around its <c>transform-origin</c>, whose reference box is the nearest viewBox (CSS Transforms 2). A root with a transform and no origin turns about its centre.</summary>
    private Affine TransformOf(SvgElement e, in SvgStyle st, Viewport vp, bool isRoot)
    {
        Affine t = TransformOf(e);
        if (t.IsIdentity)
        {
            return t;
        }

        if (e.Prop("transform-origin") is { } origin)
        {
            if (TryOrigin(origin, vp.X, vp.Y, vp.Width, vp.Height, st, out double ox, out double oy) && (ox != 0 || oy != 0))
            {
                return Affine.Translate(ox, oy).Multiply(t).Multiply(Affine.Translate(-ox, -oy));
            }
        }
        else if (isRoot)
        {
            return Affine.Translate(vp.Width / 2, vp.Height / 2).Multiply(t).Multiply(Affine.Translate(-vp.Width / 2, -vp.Height / 2));
        }

        return t;
    }

    private enum OriginKind
    {
        Length,
        Left,
        Right,
        Top,
        Bottom,
        Center,
    }

    private static OriginKind OriginWord(string t) => t switch
    {
        "left" => OriginKind.Left,
        "right" => OriginKind.Right,
        "top" => OriginKind.Top,
        "bottom" => OriginKind.Bottom,
        "center" => OriginKind.Center,
        _ => OriginKind.Length,
    };

    // CSS Transforms 2: one to three values (a third is a length along z and is ignored); keywords and lengths or percentages of the reference
    // box. A single value is the horizontal one, unless it is top or bottom.
    private bool TryOrigin(string value, double rx, double ry, double rw, double rh, in SvgStyle st, out double ox, out double oy)
    {
        ox = 0;
        oy = 0;
        string[] t = value.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        if (t.Length is 0 or > 3)
        {
            return false;
        }

        OriginKind ka = OriginWord(t[0]);
        OriginKind kb = t.Length >= 2 ? OriginWord(t[1]) : OriginKind.Center;
        string sa = t[0];
        string sb = t.Length >= 2 ? t[1] : "center";
        if (t.Length == 1 && ka is OriginKind.Top or OriginKind.Bottom)
        {
            (ka, kb) = (OriginKind.Center, ka);
            (sa, sb) = ("center", t[0]);
        }
        else if (ka is OriginKind.Top or OriginKind.Bottom || kb is OriginKind.Left or OriginKind.Right)
        {
            (ka, kb) = (kb, ka);
            (sa, sb) = (sb, sa);
        }

        if (ka is OriginKind.Top or OriginKind.Bottom || kb is OriginKind.Left or OriginKind.Right)
        {
            return false;
        }

        if (!OriginOffset(ka, sa, rw, st, out double dx) || !OriginOffset(kb, sb, rh, st, out double dy))
        {
            return false;
        }

        ox = rx + dx;
        oy = ry + dy;
        return double.IsFinite(ox) && double.IsFinite(oy);
    }

    private bool OriginOffset(OriginKind kind, string text, double size, in SvgStyle st, out double offset)
    {
        offset = 0;
        switch (kind)
        {
            case OriginKind.Left or OriginKind.Top:
                return true;
            case OriginKind.Right or OriginKind.Bottom:
                offset = size;
                return true;
            case OriginKind.Center:
                offset = size / 2;
                return true;
        }

        if (!Length.TryParse(text, out Length l))
        {
            return false;
        }

        offset = l.Unit == LengthUnit.Percent ? l.Value / 100 * size : Resolve(l, Axis.Other, st, new Viewport(0, 0));
        return true;
    }

    internal static Affine Inverse(in Affine m)
    {
        double det = m.Determinant;
        double a = m.D / det;
        double b = -m.B / det;
        double c = -m.C / det;
        double d = m.A / det;
        return new Affine(a, b, c, d, -((a * m.E) + (c * m.F)), -((b * m.E) + (d * m.F)));
    }

    /// <summary>The id in <c>url(#id)</c>, or null for <c>none</c>, a missing value or a reference to something outside the document.</summary>
    private string? RefId(string? value, string what)
    {
        if (value is null || value.AsSpan().Trim().SequenceEqual("none"))
        {
            return null;
        }

        if (SvgStyle.TryMarker(value, out string? id) && id is not null)
        {
            return id;
        }

        Note($"The {what} value '{Trim(value)}' is not a reference to an element of this document; it is ignored.");
        return null;
    }

    private static string Trim(string s) => s.Length <= 60 ? s : s[..60] + "...";

    private static string? Href(SvgElement e, out bool external)
    {
        external = false;
        string? href = e.Attr("href");
        if (href is null)
        {
            return null;
        }

        href = href.Trim();
        if (href.Length > 1 && href[0] == '#')
        {
            return href[1..];
        }

        if (href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        external = true;
        return null;
    }

    // ---- elements ----

    private void DrawElement(SvgElement e, in SvgStyle parent, Viewport vp, double? useW, double? useH, bool isRoot = false, bool viaUse = false)
    {
        switch (e.Name)
        {
            case "g" or "a" or "switch" or "use" or "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon" or "path" or "image" or "text":
                break;
            case "svg":
                break;
            case "symbol" when viaUse:
                break;
            case "script":
                Note("Scripts are never run; the script element is ignored.");
                return;
            case "foreignObject":
                Note("foreignObject (embedded HTML) is not drawn.");
                return;
            case "animate" or "animateTransform" or "animateMotion" or "animateColor" or "set":
                Note("Animation is not supported; the document is drawn in its initial state.");
                return;
            default:
                return;
        }

        if (!Conditions(e) || !Enter())
        {
            return;
        }

        try
        {
            SvgStyle st = SvgStyle.Compute(e, parent, _rootFont);
            if (!st.Displayed)
            {
                return;
            }

            DrawStyled(e, st, vp, useW, useH, isRoot);
        }
        finally
        {
            _depth--;
        }
    }

    private void DrawStyled(SvgElement e, SvgStyle st, Viewport vp, double? useW, double? useH, bool isRoot)
    {
        // SVG 1.1 gives symbol no transform; the references this follows ignore it.
        Affine t = e.Name == "symbol" ? Affine.Identity : TransformOf(e, st, vp, isRoot);
        if (!t.IsInvertible)
        {
            return;
        }

        SvgElement? clipEl = LookupServer(RefId(e.Prop("clip-path"), "clip-path"), "clipPath");
        SvgElement? maskEl = LookupServer(RefId(e.Prop("mask"), "mask"), "mask");
        if (e.Prop("filter") is { } filter && !filter.AsSpan().Trim().SequenceEqual("none"))
        {
            Note("Filters are not supported; elements with a filter are drawn unfiltered.");
        }

        if (st.Blend != BlendMode.Normal && BlendFor(st.Blend) == PdfBlendMode.Normal)
        {
            Note("The blend mode " + st.Blend + " has no PDF equivalent in this library; the content is drawn normally.");
        }

        bool group = (st.Opacity < 1 && !CanFoldOpacity(e, st)) || maskEl is not null || BlendFor(st.Blend) != PdfBlendMode.Normal || st.Plus || (st.Isolate && e.Name is "g" or "a" or "svg" or "use" or "switch");
        if (!group && t.IsIdentity && clipEl is null && st.Opacity >= 1)
        {
            DrawContent(e, st, vp, useW, useH, isRoot);
            return;
        }

        if (!Open())
        {
            return;
        }

        try
        {
            if (!t.IsIdentity)
            {
                _b.Transform(t.A, t.B, t.C, t.D, Coord.D(t.E), Coord.D(t.F));
            }

            if (clipEl is not null)
            {
                ApplyClip(clipEl, e, vp, 0);
            }

            if (!group)
            {
                if (st.Opacity < 1)
                {
                    st.FillOpacity *= st.Opacity;
                    st.StrokeOpacity *= st.Opacity;
                    st.Opacity = 1;
                }

                DrawContent(e, st, vp, useW, useH, isRoot);
                return;
            }

            if (++_groups > MaxGroups)
            {
                Note("More than " + MaxGroups + " transparency groups; further groups are drawn without their opacity.");
                DrawContent(e, st, vp, useW, useH, isRoot);
                return;
            }

            PdfFormContentBuilder fb = _host.BeginForm(new PdfRectangle(-Huge, -Huge, Huge, Huge));
            PdfFormXObject form;
            PdfContentBuilder outer = _b;
            int outerSaved = _saved;
            try
            {
                fb.SetTransparencyGroup(true, false);
                _b = fb;
                _saved = 0;
                SvgStyle inner = st;
                inner.Opacity = 1;
                DrawContent(e, inner, vp, useW, useH, isRoot);
                _b = outer;
                _saved = outerSaved;
                form = fb.Commit();
            }
            finally
            {
                _b = outer;
                _saved = outerSaved;
                fb.Dispose();
            }

            if (maskEl is not null)
            {
                ApplyMask(maskEl, e, vp);
            }

            if (st.Opacity < 1)
            {
                _b.SetOpacity(st.Opacity);
            }

            if (BlendFor(st.Blend) is { } blend and not PdfBlendMode.Normal)
            {
                _b.SetBlendMode(blend);
            }

            _b.DrawForm(form);
        }
        finally
        {
            Close();
        }
    }

    private static PdfBlendMode BlendFor(BlendMode m) => m switch
    {
        BlendMode.Multiply => PdfBlendMode.Multiply,
        BlendMode.Screen => PdfBlendMode.Screen,
        BlendMode.Overlay => PdfBlendMode.Overlay,
        BlendMode.Darken => PdfBlendMode.Darken,
        BlendMode.Lighten => PdfBlendMode.Lighten,
        BlendMode.ColorDodge => PdfBlendMode.ColorDodge,
        BlendMode.ColorBurn => PdfBlendMode.ColorBurn,
        BlendMode.HardLight => PdfBlendMode.HardLight,
        BlendMode.SoftLight => PdfBlendMode.SoftLight,
        BlendMode.Difference => PdfBlendMode.Difference,
        BlendMode.Exclusion => PdfBlendMode.Exclusion,
        _ => PdfBlendMode.Normal,
    };

    // A shape with only a fill or only a stroke, an image and a text can take the group's opacity into its own, which is how the group would
    // composite anyway and saves a form.
    private static bool CanFoldOpacity(SvgElement e, in SvgStyle st) => e.Name switch
    {
        "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon" or "path" =>
            st.Markers is null && (st.Fill.Kind == PaintKind.None || st.Stroke.Kind == PaintKind.None),
        "image" or "text" => true,
        _ => false,
    };

    private SvgElement? LookupServer(string? id, string name)
    {
        if (id is null)
        {
            return null;
        }

        if (_doc.Ids.TryGetValue(id, out SvgElement? s) && s.Name == name)
        {
            return s;
        }

        Note($"The reference '#{id}' does not name a {name} element; it is ignored.");
        return null;
    }

    private void DrawContent(SvgElement e, in SvgStyle st, Viewport vp, double? useW, double? useH, bool isRoot)
    {
        switch (e.Name)
        {
            case "svg" or "symbol":
                DrawViewport(e, st, vp, useW, useH, isRoot);
                break;
            case "g" or "a":
                DrawChildren(e, st, vp);
                break;
            case "switch":
                foreach (SvgElement c in e.Children)
                {
                    if (c.Name is "g" or "a" or "svg" or "use" or "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon" or "path" or "image" or "text" && Conditions(c))
                    {
                        DrawElement(c, st, vp, null, null);
                        break;
                    }
                }

                break;
            case "use":
                DrawUse(e, st, vp);
                break;
            case "image":
                DrawImage(e, st, vp);
                break;
            case "text":
                DrawText(e, st, vp);
                break;
            default:
                DrawShape(e, st, vp);
                break;
        }
    }

    private void DrawChildren(SvgElement e, in SvgStyle st, Viewport vp)
    {
        foreach (SvgElement c in e.Children)
        {
            DrawElement(c, st, vp, null, null);
        }
    }

    private void DrawViewport(SvgElement e, in SvgStyle st, Viewport vp, double? useW, double? useH, bool isRoot)
    {
        double x = 0;
        double y = 0;
        double w;
        double h;
        if (isRoot)
        {
            w = _svg.WidthPx;
            h = _svg.HeightPx;
        }
        else
        {
            if (e.Name == "svg")
            {
                x = Num(e, "x", Axis.X, st, vp);
                y = Num(e, "y", Axis.Y, st, vp);
            }

            w = useW ?? NumOr(e, "width", Axis.X, st, vp, 100);
            h = useH ?? NumOr(e, "height", Axis.Y, st, vp, 100);
        }

        if (!(w > 0 && h > 0))
        {
            return;
        }

        Affine inner = Affine.Translate(x, y);
        var nvp = new Viewport(w, h);
        if (SvgValues.TryViewBox(e.Attr("viewBox"), out double vx, out double vy, out double vw, out double vh))
        {
            inner = inner.Multiply(AspectRatio.Parse(e.Attr("preserveAspectRatio")).Fit(vx, vy, vw, vh, w, h));
            nvp = new Viewport(vw, vh, vx, vy);
        }

        string? overflow = e.Prop("overflow");
        bool clip = !isRoot && overflow is not ("visible" or "auto");
        if (!Open())
        {
            return;
        }

        try
        {
            if (clip)
            {
                _b.Rectangle(x, y, w, h);
                _b.Clip();
                _b.EndPath();
            }

            if (!inner.IsIdentity)
            {
                _b.Transform(inner.A, inner.B, inner.C, inner.D, Coord.D(inner.E), Coord.D(inner.F));
            }

            DrawChildren(e, st, nvp);
        }
        finally
        {
            Close();
        }
    }

    private void DrawUse(SvgElement e, in SvgStyle st, Viewport vp)
    {
        string? id = Href(e, out bool external);
        if (external)
        {
            Note("A use element refers to something outside the document (" + Trim(e.Attr("href") ?? string.Empty) + "); references to other files or addresses are never followed.");
            return;
        }

        if (id is null || !_doc.Ids.TryGetValue(id, out SvgElement? target))
        {
            return;
        }

        for (SvgElement? p = e; p is not null; p = p.Parent)
        {
            if (p == target)
            {
                Note("A use element refers to one of its own ancestors; it is not drawn.");
                return;
            }
        }

        if (_active.Count >= _lim.MaxReferenceDepth)
        {
            Note("use references are chained more than " + _lim.MaxReferenceDepth + " deep; the rest is left out.");
            return;
        }

        if (!_active.Add(target))
        {
            Note("use elements refer to each other in a cycle; the cycle is cut.");
            return;
        }

        Paint? savedFill = _ctxFill;
        Paint? savedStroke = _ctxStroke;
        // context-fill and context-stroke inside the referenced content mean the use element's own paints (SVG 2 13.5).
        (_ctxFill, _ctxStroke) = ContextPaints(st);
        try
        {
            double x = Num(e, "x", Axis.X, st, vp);
            double y = Num(e, "y", Axis.Y, st, vp);
            double? w = e.Attr("width") is { } ws && Length.TryParse(ws, out Length wl) ? Resolve(wl, Axis.X, st, vp) : null;
            double? h = e.Attr("height") is { } hs && Length.TryParse(hs, out Length hl) ? Resolve(hl, Axis.Y, st, vp) : null;
            if (x == 0 && y == 0)
            {
                DrawElement(target, st, vp, w, h, false, true);
                return;
            }

            if (!Open())
            {
                return;
            }

            try
            {
                _b.Translate(x, y);
                DrawElement(target, st, vp, w, h, false, true);
            }
            finally
            {
                Close();
            }
        }
        finally
        {
            _active.Remove(target);
            _ctxFill = savedFill;
            _ctxStroke = savedStroke;
        }
    }

    // ---- clipping and masking ----

    private void EmitEmptyClip()
    {
        _b.Rectangle(0, 0, 0, 0);
        _b.Clip();
        _b.EndPath();
    }

    /// <summary>Intersects the clip with a clipPath element, in the user space of <paramref name="target"/>. The clip is the union of the element's shapes.</summary>
    private void ApplyClip(SvgElement clipEl, SvgElement target, Viewport vp, int depth)
    {
        if (depth > 8 || !_active.Add(clipEl))
        {
            Note("clipPath elements refer to each other in a cycle or too deeply; the clip is ignored there.");
            return;
        }

        try
        {
            Affine m = TransformOf(clipEl, StyleOf(clipEl), vp, false);
            if (clipEl.Attr("clipPathUnits") == "objectBoundingBox")
            {
                if (!TryBoundsOf(target, vp, 0, out double bx0, out double by0, out double bx1, out double by1) || !(bx1 > bx0) || !(by1 > by0))
                {
                    EmitEmptyClip();
                    return;
                }

                // The clipPath's own transform acts in the user space of the clipped element, after the box mapping (as viewers do).
                m = m.Multiply(new Affine(bx1 - bx0, 0, 0, by1 - by0, bx0, by0));
            }

            if (NeedsMaskClip(clipEl))
            {
                ApplyClipAsMask(clipEl, target, vp, m);
                if (clipEl.Prop("clip-path") is { } nestedMask && LookupServer(RefId(nestedMask, "clip-path"), "clipPath") is { } innerMask)
                {
                    ApplyClip(innerMask, target, vp, depth + 1);
                }

                return;
            }

            bool any = false;
            bool evenOdd = false;
            bool first = true;
            foreach (SvgElement c in clipEl.Children)
            {
                if (!AddClipShape(c, m, vp, ref any, ref evenOdd, ref first, 0))
                {
                    continue;
                }
            }

            if (!any)
            {
                EmitEmptyClip();
            }
            else
            {
                _b.Clip(evenOdd);
                _b.EndPath();
            }

            if (clipEl.Prop("clip-path") is { } nested && LookupServer(RefId(nested, "clip-path"), "clipPath") is { } inner)
            {
                ApplyClip(inner, target, vp, depth + 1);
            }
        }
        finally
        {
            _active.Remove(clipEl);
        }
    }

    // Text and children with a clip-path of their own cannot be a union of paths, so such a clip is made as a mask.
    private bool NeedsMaskClip(SvgElement clipEl)
    {
        // The clip is the union of its shapes, each filled by its own clip-rule. One combined path with even-odd would cancel
        // the overlaps (and one rule cannot serve mixed rules), so such a clip is made as a mask too.
        int shapes = 0;
        bool evenOdd = false;
        foreach (SvgElement c in clipEl.Children)
        {
            if (c.Name is "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon" or "path" or "use")
            {
                shapes++;
                evenOdd |= StyleOf(c).ClipEvenOdd;
            }
        }

        if (shapes > 1 && evenOdd)
        {
            return true;
        }

        foreach (SvgElement c in clipEl.Children)
        {
            if (c.Name == "text" || (c.Prop("clip-path") is { } cp && !cp.AsSpan().Trim().SequenceEqual("none")))
            {
                return true;
            }

            if (c.Name == "use" && Href(c, out _) is { } id && _doc.Ids.TryGetValue(id, out SvgElement? t) && (t.Name == "text" || (t.Prop("clip-path") is { } tcp && !tcp.AsSpan().Trim().SequenceEqual("none"))))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A clip made as a luminosity mask of the clip's children drawn in white (what a viewer shows for a clip with text or nested clips).</summary>
    private void ApplyClipAsMask(SvgElement clipEl, SvgElement target, Viewport vp, Affine m)
    {
        if (++_groups > MaxGroups)
        {
            Note("More than " + MaxGroups + " transparency groups; further clips are ignored.");
            return;
        }

        PdfFormXObject form = BuildForm(() =>
        {
            if (!Open())
            {
                return;
            }

            _clipMode = true;
            try
            {
                _b.Transform(m.A, m.B, m.C, m.D, Coord.D(m.E), Coord.D(m.F));
                foreach (SvgElement c in clipEl.Children)
                {
                    DrawClipChild(c, target, vp, 0);
                }
            }
            finally
            {
                _clipMode = false;
                Close();
            }
        });
        _b.SetSoftMask(form, PdfSoftMaskKind.Luminosity);
    }

    private void DrawClipChild(SvgElement c, SvgElement target, Viewport vp, int depth)
    {
        if (c.Name == "use" && depth < 3 && Href(c, out _) is { } id && _doc.Ids.TryGetValue(id, out SvgElement? t) && t.Name is not ("g" or "svg" or "symbol" or "use"))
        {
            if (Open())
            {
                try
                {
                    Affine ut = TransformOf(c);
                    _b.Transform(ut.A, ut.B, ut.C, ut.D, Coord.D(ut.E), Coord.D(ut.F));
                    SvgStyle us = StyleOf(c);
                    _b.Translate(Num(c, "x", Axis.X, us, vp), Num(c, "y", Axis.Y, us, vp));
                    DrawClipChild(t, target, vp, depth + 1);
                }
                finally
                {
                    Close();
                }
            }

            return;
        }

        if (c.Name is not ("rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon" or "path" or "text"))
        {
            return;
        }

        // A child's own clip-path applies in the child's user space, after its transform.
        SvgElement? own = LookupServer(RefId(c.Prop("clip-path"), "clip-path"), "clipPath");
        if (own is null || !Open())
        {
            DrawElement(c, StyleOf(c.Parent!), vp, null, null);
            return;
        }

        try
        {
            Affine ct = TransformOf(c);
            _b.Transform(ct.A, ct.B, ct.C, ct.D, Coord.D(ct.E), Coord.D(ct.F));
            ApplyClip(own, c, vp, 1);
            DrawContentOnly(c, vp);
        }
        finally
        {
            Close();
        }
    }

    // Draws a shape or text without its own transform and clip, which the caller has applied.
    private void DrawContentOnly(SvgElement c, Viewport vp)
    {
        SvgStyle st = StyleOf(c);
        if (!st.Displayed)
        {
            return;
        }

        if (c.Name == "text")
        {
            DrawText(c, st, vp);
        }
        else
        {
            DrawShape(c, st, vp);
        }
    }

    // Runs <paramref name="draw"/> against a new transparency group form and returns the committed form.
    private PdfFormXObject BuildForm(Action draw)
    {
        PdfFormContentBuilder fb = _host.BeginForm(new PdfRectangle(-Huge, -Huge, Huge, Huge));
        PdfContentBuilder outer = _b;
        int outerSaved = _saved;
        try
        {
            fb.SetTransparencyGroup(true, false);
            _b = fb;
            _saved = 0;
            draw();
            _b = outer;
            _saved = outerSaved;
            return fb.Commit();
        }
        finally
        {
            _b = outer;
            _saved = outerSaved;
            fb.Dispose();
        }
    }

    private bool AddClipShape(SvgElement c, in Affine parentM, Viewport vp, ref bool any, ref bool evenOdd, ref bool first, int depth)
    {
        if (depth > 4 || !Conditions(c))
        {
            return false;
        }

        SvgStyle st = StyleOf(c);
        if (!st.Displayed || !st.Visible)
        {
            return false;
        }

        Affine m = parentM.Multiply(TransformOf(c));
        if (c.Name == "use")
        {
            string? id = Href(c, out _);
            if (id is null || !_doc.Ids.TryGetValue(id, out SvgElement? target) || target.Name is "g" or "svg" or "symbol" or "use")
            {
                return false;
            }

            m = m.Multiply(Affine.Translate(Num(c, "x", Axis.X, st, vp), Num(c, "y", Axis.Y, st, vp)));
            return AddClipShape(target, m, vp, ref any, ref evenOdd, ref first, depth + 1);
        }

        if (c.Name == "text")
        {
            Note("Text inside a clipPath is not supported; that part of the clip is left out.");
            return false;
        }

        if (c.Prop("clip-path") is { } own && !own.AsSpan().Trim().SequenceEqual("none"))
        {
            Note("A clip-path on a clipPath child is not supported; it is ignored.");
        }

        SvgPath? path = ShapePath(c, st, vp, null);
        if (path is null || path.IsEmpty)
        {
            return false;
        }

        if (first)
        {
            evenOdd = st.ClipEvenOdd;
            first = false;
        }
        else if (evenOdd != st.ClipEvenOdd)
        {
            Note("clipPath children with different clip-rule values are clipped with the first one's rule.");
        }

        EmitPath(path, m);
        any = true;
        return true;
    }

    /// <summary>Makes the soft mask of a mask element current. The mask is drawn in the user space of <paramref name="target"/>, which is the current space.</summary>
    private void ApplyMask(SvgElement maskEl, SvgElement target, Viewport vp)
    {
        if (!_active.Add(maskEl))
        {
            Note("mask elements refer to each other in a cycle; the mask is ignored there.");
            return;
        }

        try
        {
            SvgStyle mst = StyleOf(maskEl);
            bool unitsBox = maskEl.Attr("maskUnits") != "userSpaceOnUse";
            bool contentBox = maskEl.Attr("maskContentUnits") == "objectBoundingBox";
            double bx0 = 0;
            double by0 = 0;
            double bx1 = 0;
            double by1 = 0;
            if ((unitsBox || contentBox) && (!TryBoundsOf(target, vp, 0, out bx0, out by0, out bx1, out by1) || !(bx1 > bx0) || !(by1 > by0)))
            {
                EmitEmptyClip();
                return;
            }

            double rx;
            double ry;
            double rw;
            double rh;
            if (unitsBox)
            {
                rx = bx0 + (BoxFraction(maskEl, "x", -0.1) * (bx1 - bx0));
                ry = by0 + (BoxFraction(maskEl, "y", -0.1) * (by1 - by0));
                rw = BoxFraction(maskEl, "width", 1.2) * (bx1 - bx0);
                rh = BoxFraction(maskEl, "height", 1.2) * (by1 - by0);
            }
            else
            {
                rx = NumOr(maskEl, "x", Axis.X, mst, vp, -10);
                ry = NumOr(maskEl, "y", Axis.Y, mst, vp, -10);
                rw = NumOr(maskEl, "width", Axis.X, mst, vp, 120);
                rh = NumOr(maskEl, "height", Axis.Y, mst, vp, 120);
            }

            if (!(rw > 0 && rh > 0))
            {
                EmitEmptyClip();
                return;
            }

            if (++_groups > MaxGroups)
            {
                Note("More than " + MaxGroups + " transparency groups; further masks are ignored.");
                return;
            }

            PdfFormContentBuilder fb = _host.BeginForm(new PdfRectangle(-Huge, -Huge, Huge, Huge));
            PdfFormXObject form;
            PdfContentBuilder outer = _b;
            int outerSaved = _saved;
            try
            {
                fb.SetTransparencyGroup(true, false);
                _b = fb;
                _saved = 0;
                Open();
                _b.Rectangle(rx, ry, rw, rh);
                _b.Clip();
                _b.EndPath();
                // A mask on the mask element multiplies into it (SVG 1.1 14.4), in the same user space as the outer mask.
                if (LookupServer(RefId(maskEl.Prop("mask"), "mask"), "mask") is { } nestedMask)
                {
                    ApplyMask(nestedMask, target, vp);
                }

                if (contentBox)
                {
                    _b.Transform(bx1 - bx0, 0, 0, by1 - by0, bx0, by0);
                }

                foreach (SvgElement c in maskEl.Children)
                {
                    DrawElement(c, mst, vp, null, null);
                }

                Close();
                _b = outer;
                _saved = outerSaved;
                form = fb.Commit();
            }
            finally
            {
                _b = outer;
                _saved = outerSaved;
                fb.Dispose();
            }

            Note("Masks are luminosity masks with the PDF luminosity weights (a very small difference from the SVG weights).");
            _b.SetSoftMask(form, maskEl.Prop("mask-type") == "alpha" ? PdfSoftMaskKind.Alpha : PdfSoftMaskKind.Luminosity);
        }
        finally
        {
            _active.Remove(maskEl);
        }
    }

    private static double BoxFraction(SvgElement e, string name, double fallback)
    {
        string? v = e.Attr(name);
        if (v is null || !Length.TryParse(v, out Length l))
        {
            return fallback;
        }

        return l.Unit == LengthUnit.Percent ? l.Value / 100 : l.Value;
    }

    // ---- bounding boxes (for objectBoundingBox units) ----

    /// <summary>The bounding box of an element's geometry in its own user space (its transform not applied), without strokes.</summary>
    private bool TryBoundsOf(SvgElement e, Viewport vp, int depth, out double x0, out double y0, out double x1, out double y1)
    {
        x0 = y0 = double.MaxValue;
        x1 = y1 = double.MinValue;
        if (depth > 24)
        {
            return false;
        }

        // Boxes of use targets are measured again at every use, so a fan-out of references would multiply the work; it shares the instance bound.
        if (++_boundsWork > _lim.MaxInstances)
        {
            throw new PdfLimitException($"The SVG needs more than {_lim.MaxInstances} element visits to measure its bounding boxes.");
        }

        SvgStyle st = StyleOf(e);
        if (!st.Displayed || (!st.Visible && e.Name is not ("g" or "a" or "switch" or "svg" or "symbol" or "use")))
        {
            return false;
        }

        switch (e.Name)
        {
            case "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon" or "path":
            {
                SvgPath? p = ShapePath(e, st, vp, null);
                return p is not null && p.TryBounds(Affine.Identity, out x0, out y0, out x1, out y1);
            }

            case "image":
            {
                double x = Num(e, "x", Axis.X, st, vp);
                double y = Num(e, "y", Axis.Y, st, vp);
                x0 = x;
                y0 = y;
                x1 = x + Num(e, "width", Axis.X, st, vp);
                y1 = y + Num(e, "height", Axis.Y, st, vp);
                return x1 > x0 && y1 > y0;
            }

            case "text":
            {
                // No shaping here: a box from the first position, the character count and the font size.
                double x = FirstNumber(e.Attr("x"), 0);
                double y = FirstNumber(e.Attr("y"), 0);
                int chars = Math.Min(TextLength(e), 10_000);
                x0 = x;
                x1 = x + (chars * st.FontSize * 0.5);
                y0 = y - st.FontSize;
                y1 = y + (st.FontSize * 0.25);
                return chars > 0;
            }

            case "use":
            {
                string? id = Href(e, out _);
                if (id is null || !_doc.Ids.TryGetValue(id, out SvgElement? target) || target == e || !TryBoundsOf(target, vp, depth + 1, out double a, out double b, out double c, out double d))
                {
                    return false;
                }

                Affine m = Affine.Translate(Num(e, "x", Axis.X, st, vp), Num(e, "y", Axis.Y, st, vp)).Multiply(TransformOf(target));
                (x0, y0, x1, y1) = m.TransformRect(a, b, c, d);
                return true;
            }

            case "g" or "a" or "switch" or "svg" or "symbol":
            {
                bool any = false;
                foreach (SvgElement c in e.Children)
                {
                    if (!TryBoundsOf(c, vp, depth + 1, out double a, out double b, out double cc, out double d))
                    {
                        continue;
                    }

                    (double ax, double ay, double bx, double by) = TransformOf(c).TransformRect(a, b, cc, d);
                    x0 = Math.Min(x0, ax);
                    y0 = Math.Min(y0, ay);
                    x1 = Math.Max(x1, bx);
                    y1 = Math.Max(y1, by);
                    any = true;
                }

                return any;
            }

            default:
                return false;
        }
    }

    private static double FirstNumber(string? list, double fallback)
    {
        if (list is null)
        {
            return fallback;
        }

        ReadOnlySpan<char> s = list;
        return SvgValues.NextNumber(ref s, out double v) ? v : fallback;
    }

    private static int TextLength(SvgElement e)
    {
        int n = 0;
        if (e.TextContent is { } parts)
        {
            foreach (TextNode t in parts)
            {
                if (t.Text is { } s)
                {
                    n += s.Length;
                }
                else if (t.Element is { } child && n < 100_000)
                {
                    n += TextLength(child);
                }
            }
        }

        return n;
    }
}
