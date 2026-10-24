using System.Globalization;
using System.Text;
using OnePdf.Layout.Svg.Dom;

namespace OnePdf.Layout.Svg;

internal sealed partial class SvgPainter
{
    // A stroke is an outline the content builder cannot clip to, so strokes painted with a gradient or a pattern are written as a small form
    // of their own: PDF paints a stroke with a pattern colour directly (ISO 32000-2 8.7.3), a shading pattern for a gradient and a tiling
    // pattern for a pattern element. The form is drawn in the current user space, which is the pattern space of what it holds.

    /// <summary>Strokes the path with a gradient or pattern; false when nothing of the kind could be made and the caller should use a solid colour instead.</summary>
    private bool TryStrokeWithServer(SvgPath path, SvgElement server, in SvgStyle st, Viewport vp, double width, bool fromContext, Rgba32? fallback, out Rgba32 flat)
    {
        flat = default;
        if (!ServerBox(path, fromContext, out double bx0, out double by0, out double bx1, out double by1, out Affine space))
        {
            return true;
        }

        PdfDocument doc = _b.Document;
        PdfReference? pattern;
        double alpha = 1;
        if (server.Name == "pattern")
        {
            if (!TryTilingPattern(server, st, vp, bx0, by0, bx1, by1, space, out pattern))
            {
                return UseFallback(fallback, out flat);
            }
        }
        else
        {
            GradientPlan? plan = PlanGradient(server, st, vp, bx0, by0, bx1, by1);
            if (plan is null)
            {
                return UseFallback(fallback, out flat);
            }

            if (plan.Flat is { } f)
            {
                flat = f;
                return false;
            }

            if (plan.VaryingAlpha)
            {
                Note("Colour stops with different opacities on a stroke are drawn opaque.");
            }

            alpha = plan.AlphaScale;
            var pd = new PdfDictionary();
            pd.SetEntry("Type", new PdfName("Pattern"));
            pd.SetEntry("PatternType", new PdfInteger(2));
            Affine pm = space.IsIdentity ? plan.M : space.Multiply(plan.M);
            pd.SetEntry("Matrix", Numbers(pm.A, pm.B, pm.C, pm.D, pm.E, pm.F));
            pd.SetEntry("Shading", ShadingOf(plan));
            pattern = doc.AddObject(pd);
        }

        var sb = new StringBuilder();
        sb.Append("/G gs\n/Pattern CS /P SCN\n");
        sb.Append(F(width)).Append(" w ").Append(st.LineCap switch { 1 => 1, 2 => 2, _ => 0 }).Append(" J ").Append(st.LineJoin switch { 1 => 1, 2 => 2, _ => 0 }).Append(" j ");
        sb.Append(F(Math.Max(1, st.MiterLimit))).Append(" M\n");
        AppendDash(sb, st, vp);
        AppendPath(sb, path);
        sb.Append("S\n");

        var gs = new PdfDictionary();
        gs.SetEntry("Type", new PdfName("ExtGState"));
        gs.SetEntry("CA", new PdfReal(Math.Clamp(st.StrokeOpacity * alpha, 0, 1)));
        var gsMap = new PdfDictionary();
        gsMap.SetEntry("G", gs);
        var pats = new PdfDictionary();
        pats.SetEntry("P", pattern!);
        var res = new PdfDictionary();
        res.SetEntry("ExtGState", gsMap);
        res.SetEntry("Pattern", pats);
        PdfStream form = doc.CreateStream(Encoding.ASCII.GetBytes(sb.ToString()));
        form.Dictionary.SetEntry("Type", new PdfName("XObject"));
        form.Dictionary.SetEntry("Subtype", new PdfName("Form"));
        form.Dictionary.SetEntry("BBox", Numbers(-Huge, -Huge, Huge, Huge));
        form.Dictionary.SetEntry("Resources", res);
        doc.AddObject(form);
        _b.DrawForm(form);
        return true;
    }

    // A server that cannot paint this shape (a zero-size box for objectBoundingBox units) gives way to the fallback colour, when there is one.
    private static bool UseFallback(Rgba32? fallback, out Rgba32 flat)
    {
        flat = fallback ?? default;
        return fallback is null;
    }

    private bool TryTilingPattern(SvgElement pattern, in SvgStyle st, Viewport vp, double bx0, double by0, double bx1, double by1, in Affine space, out PdfReference? reference)
    {
        reference = null;
        if (!_active.Add(pattern))
        {
            Note("patterns refer to each other in a cycle; the pattern is not drawn there.");
            return false;
        }

        try
        {
            if (!ReadPatternGeometry(pattern, vp, bx0, by0, bx1, by1, space, null, out PatternGeometry pg))
            {
                return false;
            }

            PdfFormXObject? tile = BuildTile(pg.Holder, StyleOf(pattern), pg.Tw, pg.Th, pg.Content, vp);
            if (tile is null)
            {
                return false;
            }

            PdfDocument doc = _b.Document;
            var tiles = new PdfDictionary();
            tiles.SetEntry("T", tile.Reference);
            var res = new PdfDictionary();
            res.SetEntry("XObject", tiles);
            PdfStream ps = doc.CreateStream("/T Do\n"u8);
            PdfDictionary d = ps.Dictionary;
            d.SetEntry("Type", new PdfName("Pattern"));
            d.SetEntry("PatternType", new PdfInteger(1));
            d.SetEntry("PaintType", new PdfInteger(1));
            d.SetEntry("TilingType", new PdfInteger(1));
            d.SetEntry("BBox", Numbers(0, 0, pg.Tw, pg.Th));
            d.SetEntry("XStep", new PdfReal(pg.Tw));
            d.SetEntry("YStep", new PdfReal(pg.Th));
            Affine m = pg.Transform.Multiply(Affine.Translate(pg.Tx, pg.Ty));
            d.SetEntry("Matrix", Numbers(m.A, m.B, m.C, m.D, m.E, m.F));
            d.SetEntry("Resources", res);
            reference = doc.AddObject(ps);
            return true;
        }
        finally
        {
            _active.Remove(pattern);
        }
    }

    private static PdfArray Numbers(params ReadOnlySpan<double> values)
    {
        var a = new PdfArray();
        foreach (double v in values)
        {
            a.Append(new PdfReal(Coord.D(v)));
        }

        return a;
    }

    private static PdfDictionary ShadingOf(GradientPlan g)
    {
        var d = new PdfDictionary();
        d.SetEntry("ShadingType", new PdfInteger(g.Radial ? 3 : 2));
        d.SetEntry("ColorSpace", new PdfName("DeviceRGB"));
        d.SetEntry("Coords", g.Radial ? Numbers(g.Fx, g.Fy, Math.Max(0, g.Fr), g.Cx, g.Cy, Math.Max(0, g.R)) : Numbers(g.X1, g.Y1, g.X2, g.Y2));
        var extend = new PdfArray();
        extend.Append(new PdfBoolean(true));
        extend.Append(new PdfBoolean(true));
        d.SetEntry("Extend", extend);

        // One exponential function per segment between stops, stitched; a segment between stops at the same offset is a hard edge and is left out.
        var segments = new List<PdfDictionary>();
        var bounds = new List<double>();
        for (int i = 0; i + 1 < g.Stops.Count; i++)
        {
            (double o0, Rgba32 c0) = g.Stops[i];
            (double o1, Rgba32 c1) = g.Stops[i + 1];
            if (!(o1 > o0))
            {
                continue;
            }

            var f = new PdfDictionary();
            f.SetEntry("FunctionType", new PdfInteger(2));
            f.SetEntry("Domain", Numbers(0, 1));
            f.SetEntry("C0", Numbers(c0.R / 255.0, c0.G / 255.0, c0.B / 255.0));
            f.SetEntry("C1", Numbers(c1.R / 255.0, c1.G / 255.0, c1.B / 255.0));
            f.SetEntry("N", new PdfInteger(1));
            segments.Add(f);
            bounds.Add(o1);
        }

        if (segments.Count == 0)
        {
            Rgba32 c = g.Stops[^1].Color;
            var flat = new PdfDictionary();
            flat.SetEntry("FunctionType", new PdfInteger(2));
            flat.SetEntry("Domain", Numbers(0, 1));
            flat.SetEntry("C0", Numbers(c.R / 255.0, c.G / 255.0, c.B / 255.0));
            flat.SetEntry("C1", Numbers(c.R / 255.0, c.G / 255.0, c.B / 255.0));
            flat.SetEntry("N", new PdfInteger(1));
            d.SetEntry("Function", flat);
            return d;
        }

        if (segments.Count == 1)
        {
            d.SetEntry("Function", segments[0]);
            return d;
        }

        var stitch = new PdfDictionary();
        stitch.SetEntry("FunctionType", new PdfInteger(3));
        stitch.SetEntry("Domain", Numbers(0, 1));
        var fns = new PdfArray();
        var enc = new PdfArray();
        foreach (PdfDictionary s in segments)
        {
            fns.Append(s);
            enc.Append(new PdfReal(0));
            enc.Append(new PdfReal(1));
        }

        stitch.SetEntry("Functions", fns);
        var b = new PdfArray();
        for (int i = 0; i < bounds.Count - 1; i++)
        {
            b.Append(new PdfReal(bounds[i]));
        }

        stitch.SetEntry("Bounds", b);
        stitch.SetEntry("Encode", enc);
        d.SetEntry("Function", stitch);
        return d;
    }

    private static string F(double v)
    {
        double r = Math.Round(Coord.D(v), 4, MidpointRounding.AwayFromZero);
        return r == 0 ? "0" : r.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static void AppendPath(StringBuilder sb, SvgPath p)
    {
        IReadOnlyList<SvgPath.Op> ops = p.Ops;
        IReadOnlyList<double> pts = p.Points;
        int k = 0;
        foreach (SvgPath.Op op in ops)
        {
            switch (op)
            {
                case SvgPath.Op.Move:
                    sb.Append(F(pts[k])).Append(' ').Append(F(pts[k + 1])).Append(" m\n");
                    k += 2;
                    break;
                case SvgPath.Op.Line:
                    sb.Append(F(pts[k])).Append(' ').Append(F(pts[k + 1])).Append(" l\n");
                    k += 2;
                    break;
                case SvgPath.Op.Cubic:
                    for (int i = 0; i < 6; i++)
                    {
                        sb.Append(F(pts[k + i])).Append(' ');
                    }

                    sb.Append("c\n");
                    k += 6;
                    break;
                default:
                    sb.Append("h\n");
                    break;
            }
        }
    }

    private void AppendDash(StringBuilder sb, in SvgStyle st, Viewport vp)
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
        phase = ((phase % sum) + sum) % sum;
        sb.Append('[');
        for (int i = 0; i < lengths.Length; i++)
        {
            sb.Append(i > 0 ? " " : string.Empty).Append(F(lengths[i]));
        }

        sb.Append("] ").Append(F(phase)).Append(" d\n");
    }
}
