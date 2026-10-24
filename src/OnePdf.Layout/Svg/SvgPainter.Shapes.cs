using System.Globalization;
using OnePdf.Layout.Svg.Dom;

namespace OnePdf.Layout.Svg;

internal sealed partial class SvgPainter
{
    // ---- shape geometry ----

    private SvgPath? ShapePath(SvgElement e, in SvgStyle st, Viewport vp, MarkerPath? markers)
    {
        switch (e.Name)
        {
            case "path":
            {
                string? d = e.Attr("d");
                if (d is null)
                {
                    return null;
                }

                PathData.Parse(d, out SvgPath path, out _, markers, _paths);
                return path;
            }

            case "rect":
                return RectPath(e, st, vp);
            case "circle":
            {
                double r = Num(e, "r", Axis.Other, st, vp);
                return r > 0 ? EllipsePath(Num(e, "cx", Axis.X, st, vp), Num(e, "cy", Axis.Y, st, vp), r, r) : null;
            }

            case "ellipse":
            {
                // SVG 2 11.5: a missing or negative radius is auto and takes the other one's value.
                double? arx = e.Attr("rx") is { } a && Length.TryParse(a, out Length la) && Resolve(la, Axis.X, st, vp) is var ra and >= 0 ? ra : null;
                double? ary = e.Attr("ry") is { } b && Length.TryParse(b, out Length lb) && Resolve(lb, Axis.Y, st, vp) is var rb and >= 0 ? rb : null;
                double rx = arx ?? ary ?? 0;
                double ry = ary ?? arx ?? 0;
                return rx > 0 && ry > 0 ? EllipsePath(Num(e, "cx", Axis.X, st, vp), Num(e, "cy", Axis.Y, st, vp), rx, ry) : null;
            }

            case "line":
            {
                var p = new SvgPath();
                double x1 = Num(e, "x1", Axis.X, st, vp);
                double y1 = Num(e, "y1", Axis.Y, st, vp);
                double x2 = Num(e, "x2", Axis.X, st, vp);
                double y2 = Num(e, "y2", Axis.Y, st, vp);
                p.MoveTo(x1, y1);
                p.LineTo(x2, y2);
                if (markers is not null)
                {
                    markers.MoveTo(x1, y1);
                    markers.LineTo(x2, y2);
                }

                return p;
            }

            case "polyline" or "polygon":
            {
                List<double> n = SvgValues.NumberList(e.Attr("points") ?? string.Empty, out _);
                int count = n.Count / 2;
                if (count < 2)
                {
                    return null;
                }

                _paths.Spend(count);
                var p = new SvgPath();
                for (int i = 0; i < count; i++)
                {
                    double x = Coord.D(n[2 * i]);
                    double y = Coord.D(n[(2 * i) + 1]);
                    if (i == 0)
                    {
                        p.MoveTo(x, y);
                        markers?.MoveTo(x, y);
                    }
                    else
                    {
                        p.LineTo(x, y);
                        markers?.LineTo(x, y);
                    }
                }

                if (e.Name == "polygon")
                {
                    p.Close();
                    markers?.Close();
                }

                return p;
            }

            default:
                return null;
        }
    }

    private SvgPath? RectPath(SvgElement e, in SvgStyle st, Viewport vp)
    {
        double w = Num(e, "width", Axis.X, st, vp);
        double h = Num(e, "height", Axis.Y, st, vp);
        if (!(w > 0 && h > 0))
        {
            return null;
        }

        double x = Num(e, "x", Axis.X, st, vp);
        double y = Num(e, "y", Axis.Y, st, vp);
        double? rx = e.Attr("rx") is { } a && Length.TryParse(a, out Length la) && Resolve(la, Axis.X, st, vp) is var ra and >= 0 ? ra : null;
        double? ry = e.Attr("ry") is { } b && Length.TryParse(b, out Length lb) && Resolve(lb, Axis.Y, st, vp) is var rb and >= 0 ? rb : null;
        double rxx = Math.Min(rx ?? ry ?? 0, w / 2);
        double ryy = Math.Min(ry ?? rx ?? 0, h / 2);
        var p = new SvgPath();
        if (rxx <= 0 || ryy <= 0)
        {
            p.MoveTo(x, y);
            p.LineTo(x + w, y);
            p.LineTo(x + w, y + h);
            p.LineTo(x, y + h);
            p.Close();
            return p;
        }

        p.MoveTo(x + rxx, y);
        p.LineTo(x + w - rxx, y);
        p.ArcTo(rxx, ryy, 0, false, true, x + w, y + ryy);
        p.LineTo(x + w, y + h - ryy);
        p.ArcTo(rxx, ryy, 0, false, true, x + w - rxx, y + h);
        p.LineTo(x + rxx, y + h);
        p.ArcTo(rxx, ryy, 0, false, true, x, y + h - ryy);
        p.LineTo(x, y + ryy);
        p.ArcTo(rxx, ryy, 0, false, true, x + rxx, y);
        p.Close();
        return p;
    }

    private static SvgPath EllipsePath(double cx, double cy, double rx, double ry)
    {
        var p = new SvgPath();
        p.MoveTo(cx + rx, cy);
        p.ArcTo(rx, ry, 0, false, true, cx, cy + ry);
        p.ArcTo(rx, ry, 0, false, true, cx - rx, cy);
        p.ArcTo(rx, ry, 0, false, true, cx, cy - ry);
        p.ArcTo(rx, ry, 0, false, true, cx + rx, cy);
        p.Close();
        return p;
    }

    private void EmitPath(SvgPath p) => EmitPath(p, Affine.Identity);

    /// <summary>Writes the path through a matrix, so several paths with different matrices can form one clip.</summary>
    private void EmitPath(SvgPath p, in Affine m)
    {
        IReadOnlyList<SvgPath.Op> ops = p.Ops;
        IReadOnlyList<double> pts = p.Points;
        int k = 0;
        bool id = m.IsIdentity;
        for (int i = 0; i < ops.Count; i++)
        {
            switch (ops[i])
            {
                case SvgPath.Op.Move:
                {
                    (double x, double y) = id ? (pts[k], pts[k + 1]) : m.Apply(pts[k], pts[k + 1]);
                    _b.MoveTo(Coord.D(x), Coord.D(y));
                    k += 2;
                    break;
                }

                case SvgPath.Op.Line:
                {
                    (double x, double y) = id ? (pts[k], pts[k + 1]) : m.Apply(pts[k], pts[k + 1]);
                    _b.LineTo(Coord.D(x), Coord.D(y));
                    k += 2;
                    break;
                }

                case SvgPath.Op.Cubic:
                {
                    (double x1, double y1) = id ? (pts[k], pts[k + 1]) : m.Apply(pts[k], pts[k + 1]);
                    (double x2, double y2) = id ? (pts[k + 2], pts[k + 3]) : m.Apply(pts[k + 2], pts[k + 3]);
                    (double x3, double y3) = id ? (pts[k + 4], pts[k + 5]) : m.Apply(pts[k + 4], pts[k + 5]);
                    _b.CurveTo(Coord.D(x1), Coord.D(y1), Coord.D(x2), Coord.D(y2), Coord.D(x3), Coord.D(y3));
                    k += 6;
                    break;
                }

                default:
                    _b.ClosePath();
                    break;
            }
        }
    }

    // ---- painting shapes ----

    private void DrawShape(SvgElement e, in SvgStyle st, Viewport vp)
    {
        bool wantMarkers = e.Name is "path" or "line" or "polyline" or "polygon" or "rect" or "circle" or "ellipse" && st.Markers is { } mk && (mk.Start is not null || mk.Mid is not null || mk.End is not null);
        MarkerPath? mp = wantMarkers ? new MarkerPath() : null;
        SvgPath? path = ShapePath(e, st, vp, mp);
        if (path is not null && mp is not null && e.Name is "rect" or "circle" or "ellipse")
        {
            // SVG 2 11.6: markers sit on the vertices of every basic shape's path.
            ReplayInto(mp, path);
        }

        if (path is null || path.IsEmpty || !st.Visible)
        {
            return;
        }

        for (int pos = 0; pos < 3; pos++)
        {
            int what = st.PaintIndex(0) == pos ? 0 : st.PaintIndex(1) == pos ? 1 : 2;
            switch (what)
            {
                case 0:
                    FillPath(path, st, vp);
                    break;
                case 1:
                    StrokePath(path, st, vp);
                    break;
                default:
                    if (mp is not null)
                    {
                        DrawMarkers(e, st, vp, mp, path);
                    }

                    break;
            }
        }
    }

    // ---- paint resolution ----

    private enum ResolvedKind
    {
        None,
        Solid,
        Server,
    }

    private readonly record struct Resolved(ResolvedKind Kind, Rgba32 Color = default, SvgElement? Server = null, bool FromContext = false, Rgba32? Fallback = null);

    // The paints content drawn for this element sees as context-fill and context-stroke; a context paint on the element itself passes the outer one on.
    private (Paint?, Paint?) ContextPaints(in SvgStyle st)
    {
        Paint fill = st.Fill.Kind switch { PaintKind.ContextFill => _ctxFill ?? Paint.None, PaintKind.ContextStroke => _ctxStroke ?? Paint.None, _ => st.Fill };
        Paint stroke = st.Stroke.Kind switch { PaintKind.ContextFill => _ctxFill ?? Paint.None, PaintKind.ContextStroke => _ctxStroke ?? Paint.None, _ => st.Stroke };
        return (fill, stroke);
    }

    private Resolved ResolvePaint(in Paint p, in SvgStyle st)
    {
        switch (p.Kind)
        {
            case PaintKind.None:
                return default;
            case PaintKind.Color:
                return new Resolved(ResolvedKind.Solid, p.Color);
            case PaintKind.CurrentColor:
                return new Resolved(ResolvedKind.Solid, st.Color);
            case PaintKind.ContextFill:
                return _ctxFill is { Kind: not (PaintKind.ContextFill or PaintKind.ContextStroke) } cf ? ResolvePaint(cf, st) with { FromContext = true } : default;
            case PaintKind.ContextStroke:
                return _ctxStroke is { Kind: not (PaintKind.ContextFill or PaintKind.ContextStroke) } cs ? ResolvePaint(cs, st) with { FromContext = true } : default;
            default:
            {
                // A gradient with no stops is not a usable paint server, so the fallback applies (as for a reference to nothing).
                if (p.ServerId is { } id && _doc.Ids.TryGetValue(id, out SvgElement? s) && (s.Name == "pattern" || (s.Name is "linearGradient" or "radialGradient" && ChainWithChildren(s, e => e.Children.Exists(c => c.Name == "stop")) is not null)))
                {
                    // The fallback after the url() is used when the server cannot paint this shape (a zero-size box for objectBoundingBox units).
                    Rgba32? fallback = p.Fallback switch { PaintKind.Color => p.FallbackColor, PaintKind.CurrentColor => st.Color, _ => null };
                    return new Resolved(ResolvedKind.Server, default, s, false, fallback);
                }

                return p.Fallback switch
                {
                    PaintKind.Color => new Resolved(ResolvedKind.Solid, p.FallbackColor),
                    PaintKind.CurrentColor => new Resolved(ResolvedKind.Solid, st.Color),
                    _ => default,
                };
            }
        }
    }

    private void SetColor(Rgba32 c, bool stroke)
    {
        double r = c.R / 255.0;
        double g = c.G / 255.0;
        double b = c.B / 255.0;
        if (stroke)
        {
            _b.SetStrokeRgb(r, g, b);
        }
        else
        {
            _b.SetFillRgb(r, g, b);
        }
    }

    private static readonly Resolved s_clipWhite = new(ResolvedKind.Solid, new Rgba32(255, 255, 255, 255));

    private void FillPath(SvgPath path, in SvgStyle st0, Viewport vp)
    {
        // Inside a clip made as a mask everything is filled white with the clip-rule and no opacity.
        SvgStyle st = st0;
        if (_clipMode)
        {
            st.FillOpacity = 1;
            st.FillEvenOdd = st.ClipEvenOdd;
        }

        Resolved r = _clipMode ? s_clipWhite : ResolvePaint(st.Fill, st);
        if (r.Kind == ResolvedKind.None)
        {
            return;
        }

        if (r.Kind == ResolvedKind.Solid)
        {
            double opacity = st.FillOpacity * r.Color.A / 255.0;
            if (opacity <= 0)
            {
                return;
            }

            bool wrap = opacity < 1;
            if (wrap && !Open())
            {
                return;
            }

            SetColor(r.Color, false);
            if (wrap)
            {
                _b.SetFillOpacity(opacity);
            }

            EmitPath(path);
            _b.Fill(st.FillEvenOdd);
            if (wrap)
            {
                Close();
            }

            return;
        }

        FillWithServer(path, r.Server!, st, vp, r.FromContext, r.Fallback);
    }

    private void StrokePath(SvgPath path, in SvgStyle st, Viewport vp)
    {
        Resolved r = _clipMode ? default : ResolvePaint(st.Stroke, st);
        if (r.Kind == ResolvedKind.None)
        {
            return;
        }

        double width = Resolve(st.StrokeWidth, Axis.Other, st, vp);
        if (!(width > 0))
        {
            return;
        }

        Rgba32 color;
        double alpha = 1;
        if (r.Kind == ResolvedKind.Solid)
        {
            color = r.Color;
            alpha = r.Color.A / 255.0;
        }
        else
        {
            if (TryStrokeWithServer(path, r.Server!, st, vp, width, r.FromContext, r.Fallback, out color))
            {
                return;
            }

            alpha = color.A / 255.0;
        }

        double opacity = st.StrokeOpacity * alpha;
        if (opacity <= 0 || !Open())
        {
            return;
        }

        try
        {
            SetColor(color, true);
            if (opacity < 1)
            {
                _b.SetStrokeOpacity(opacity);
            }

            _b.SetLineWidth(width);
            _b.SetLineCap(st.LineCap switch { 1 => PdfLineCap.Round, 2 => PdfLineCap.ProjectingSquare, _ => PdfLineCap.Butt });
            _b.SetLineJoin(st.LineJoin switch { 1 => PdfLineJoin.Round, 2 => PdfLineJoin.Bevel, _ => PdfLineJoin.Miter });
            _b.SetMiterLimit(Math.Max(1, st.MiterLimit));
            ApplyDash(st, vp);
            EmitPath(path);
            _b.Stroke();
            if (st.LineCap != 0 && st.DashArray is not { Length: > 0 })
            {
                DrawCapDots(path, st, color, width, opacity);
            }
        }
        finally
        {
            Close();
        }
    }

    private void ApplyDash(in SvgStyle st, Viewport vp)
    {
        if (st.DashArray is not { Length: > 0 } dash)
        {
            return;
        }

        var lengths = new double[dash.Length % 2 == 1 ? dash.Length * 2 : dash.Length];
        double sum = 0;
        for (int i = 0; i < lengths.Length; i++)
        {
            double v = Resolve(dash[i % dash.Length], Axis.Other, st, vp);
            if (!(v >= 0))
            {
                return;
            }

            lengths[i] = v;
            sum += v;
        }

        if (!(sum > 0))
        {
            return;
        }

        double phase = Resolve(st.DashOffset, Axis.Other, st, vp);
        // A negative offset starts that far before the pattern; the PDF phase is the same offset into it.
        phase = ((phase % sum) + sum) % sum;
        _b.SetDash(lengths, phase);
    }

    // ---- helpers for markers, caps and context paints ----

    private static void ReplayInto(MarkerPath markers, SvgPath path)
    {
        IReadOnlyList<SvgPath.Op> ops = path.Ops;
        IReadOnlyList<double> pts = path.Points;
        int k = 0;
        foreach (SvgPath.Op op in ops)
        {
            switch (op)
            {
                case SvgPath.Op.Move:
                    markers.MoveTo(pts[k], pts[k + 1]);
                    k += 2;
                    break;
                case SvgPath.Op.Line:
                    markers.LineTo(pts[k], pts[k + 1]);
                    k += 2;
                    break;
                case SvgPath.Op.Cubic:
                    markers.CubicTo(pts[k], pts[k + 1], pts[k + 2], pts[k + 3], pts[k + 4], pts[k + 5]);
                    k += 6;
                    break;
                default:
                    markers.Close();
                    break;
            }
        }
    }

    /// <summary>
    /// A subpath whose points are all equal is stroked as a dot under round and square caps (SVG 2 painting, 13.4); PDF paints such a
    /// subpath only for round caps, and never a square, so the dots are drawn as filled shapes in the stroke colour.
    /// </summary>
    private void DrawCapDots(SvgPath path, in SvgStyle st, Rgba32 color, double width, double opacity)
    {
        IReadOnlyList<SvgPath.Op> ops = path.Ops;
        IReadOnlyList<double> pts = path.Points;
        int k = 0;
        double sx = 0;
        double sy = 0;
        bool open = false;
        bool degenerate = false;
        bool segments = false;
        var dots = new List<(double, double)>();

        void End()
        {
            if (open && degenerate && segments)
            {
                dots.Add((sx, sy));
            }

            open = false;
        }

        foreach (SvgPath.Op op in ops)
        {
            switch (op)
            {
                case SvgPath.Op.Move:
                    End();
                    sx = pts[k];
                    sy = pts[k + 1];
                    open = true;
                    degenerate = true;
                    segments = false;
                    k += 2;
                    break;
                case SvgPath.Op.Line:
                    segments = true;
                    degenerate &= pts[k] == sx && pts[k + 1] == sy;
                    k += 2;
                    break;
                case SvgPath.Op.Cubic:
                    segments = true;
                    degenerate &= pts[k] == sx && pts[k + 1] == sy && pts[k + 2] == sx && pts[k + 3] == sy && pts[k + 4] == sx && pts[k + 5] == sy;
                    k += 6;
                    break;
                default:
                    // A closed zero-length subpath is a dot as well.
                    segments = true;
                    End();
                    break;
            }

            if (dots.Count > 10_000)
            {
                break;
            }
        }

        End();
        if (dots.Count == 0)
        {
            return;
        }

        SetColor(color, false);
        _b.SetFillOpacity(opacity);
        foreach ((double x, double y) in dots)
        {
            if (st.LineCap == 1)
            {
                EmitPath(EllipsePath(x, y, width / 2, width / 2));
            }
            else
            {
                _b.Rectangle(Coord.D(x - (width / 2)), Coord.D(y - (width / 2)), width, width);
            }

            _b.Fill();
        }
    }

    /// <summary>The box a gradient or pattern refers to, and the matrix that takes its coordinate system into the current user space.</summary>
    private bool ServerBox(SvgPath path, bool fromContext, out double bx0, out double by0, out double bx1, out double by1, out Affine space)
    {
        space = Affine.Identity;
        if (fromContext && _ctxFrame is { } f)
        {
            bool ok;
            if (f.Path is not null)
            {
                ok = f.Path.TryBounds(Affine.Identity, out bx0, out by0, out bx1, out by1);
            }
            else
            {
                ok = TryBoundsOf(f.Element!, f.Viewport, 0, out bx0, out by0, out bx1, out by1);
            }

            if (_rel.IsInvertible)
            {
                space = Inverse(_rel);
            }

            return ok;
        }

        return path.TryBounds(Affine.Identity, out bx0, out by0, out bx1, out by1);
    }
}
