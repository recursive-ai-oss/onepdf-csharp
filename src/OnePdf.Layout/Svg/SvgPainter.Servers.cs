using System.Globalization;
using OnePdf.Layout.Svg.Dom;

namespace OnePdf.Layout.Svg;

internal sealed partial class SvgPainter
{
    private const int MaxPeriods = 64;
    private const int MaxTiles = 4096;

    private enum Spread
    {
        Pad,
        Reflect,
        Repeat,
    }

    private sealed class GradientSpec
    {
        public bool Radial;
        public bool BoxUnits;
        public Affine Transform = Affine.Identity;
        public Spread Spread;
        public double X1, Y1, X2, Y2;
        public double Cx, Cy, R, Fx, Fy, Fr;
        public List<(double Offset, Rgba32 Color)> Stops = [];
    }

    private sealed class GradientPlan
    {
        public Affine M;
        public bool Radial;
        public double X1, Y1, X2, Y2, Cx, Cy, R, Fx, Fy, Fr;
        public List<(double Offset, Rgba32 Color)> Stops = [];
        public bool VaryingAlpha;
        public double AlphaScale = 1;
        public Rgba32? Flat;
    }

    private void FillWithServer(SvgPath path, SvgElement server, in SvgStyle st, Viewport vp)
    {
        if (!path.TryBounds(Affine.Identity, out double bx0, out double by0, out double bx1, out double by1))
        {
            return;
        }

        if (server.Name == "pattern")
        {
            FillWithPattern(path, server, st, vp, bx0, by0, bx1, by1);
            return;
        }

        GradientPlan? plan = PlanGradient(server, st, vp, bx0, by0, bx1, by1);
        if (plan is null)
        {
            return;
        }

        if (plan.Flat is { } flat)
        {
            FillSolid(path, st, flat);
            return;
        }

        Affine m = plan.M;
        PdfGradient color = BuildPdfGradient(plan, alpha: false);
        PdfGradient? alpha = plan.VaryingAlpha ? BuildPdfGradient(plan, alpha: true) : null;
        double opacity = st.FillOpacity * plan.AlphaScale;
        if (opacity <= 0 || !Open())
        {
            return;
        }

        try
        {
            EmitPath(path);
            _b.Clip(st.FillEvenOdd);
            _b.EndPath();
            _b.Transform(m.A, m.B, m.C, m.D, Coord.D(m.E), Coord.D(m.F));
            if (alpha is not null)
            {
                SetMaskFromGradient(alpha);
            }

            if (opacity < 1)
            {
                _b.SetFillOpacity(opacity);
            }

            _b.PaintGradient(color);
        }
        finally
        {
            Close();
        }
    }

    /// <summary>Works out what a gradient paints on a shape with the given box: its matrix, geometry and stops, with the spread method unrolled into the stops.</summary>
    private GradientPlan? PlanGradient(SvgElement server, in SvgStyle st, Viewport vp, double bx0, double by0, double bx1, double by1)
    {
        GradientSpec? g = ReadGradient(server, st, vp);
        if (g is null || g.Stops.Count == 0)
        {
            return null;
        }

        if (g.Stops.Count == 1)
        {
            return new GradientPlan { Flat = g.Stops[0].Color };
        }

        Affine m = g.Transform;
        if (g.BoxUnits)
        {
            if (!(bx1 - bx0 > 0 && by1 - by0 > 0))
            {
                return null;
            }

            m = new Affine(bx1 - bx0, 0, 0, by1 - by0, bx0, by0).Multiply(g.Transform);
        }

        if (!m.IsInvertible)
        {
            return null;
        }

        // A gradient with no length is a flat fill with the last stop (SVG 1.1 13.2.2/13.2.3).
        if ((!g.Radial && g.X1 == g.X2 && g.Y1 == g.Y2) || (g.Radial && !(g.R > 0)))
        {
            return new GradientPlan { Flat = g.Stops[^1].Color };
        }

        Affine inv = Inverse(m);
        (double gx0, double gy0, double gx1, double gy1) = inv.TransformRect(bx0, by0, bx1, by1);
        var stops = new List<(double Offset, Rgba32 Color)>(g.Stops);
        if (stops[0].Offset > 0)
        {
            stops.Insert(0, (0, stops[0].Color));
        }

        if (stops[^1].Offset < 1)
        {
            stops.Add((1, stops[^1].Color));
        }

        var plan = new GradientPlan { M = m, Radial = g.Radial, X1 = g.X1, Y1 = g.Y1, X2 = g.X2, Y2 = g.Y2, Cx = g.Cx, Cy = g.Cy, R = g.R, Fx = g.Fx, Fy = g.Fy, Fr = g.Fr };
        Spread spread = g.Spread;
        int kmin = 0;
        int kmax = 1;
        if (spread != Spread.Pad)
        {
            if (!g.Radial)
            {
                double dx = g.X2 - g.X1;
                double dy = g.Y2 - g.Y1;
                double len2 = (dx * dx) + (dy * dy);
                double tmin = double.MaxValue;
                double tmax = double.MinValue;
                foreach ((double px, double py) in (ReadOnlySpan<(double, double)>)[(gx0, gy0), (gx1, gy0), (gx1, gy1), (gx0, gy1)])
                {
                    double t = (((px - g.X1) * dx) + ((py - g.Y1) * dy)) / len2;
                    tmin = Math.Min(tmin, t);
                    tmax = Math.Max(tmax, t);
                }

                kmin = (int)Math.Floor(Math.Clamp(tmin, -1e6, 1e6));
                kmax = (int)Math.Ceiling(Math.Clamp(tmax, -1e6, 1e6));
                if (kmax <= kmin)
                {
                    kmax = kmin + 1;
                }
            }
            else
            {
                double far = 0;
                foreach ((double px, double py) in (ReadOnlySpan<(double, double)>)[(gx0, gy0), (gx1, gy0), (gx1, gy1), (gx0, gy1)])
                {
                    far = Math.Max(far, Math.Sqrt(((px - g.Cx) * (px - g.Cx)) + ((py - g.Cy) * (py - g.Cy))));
                }

                far += Math.Sqrt(((g.Fx - g.Cx) * (g.Fx - g.Cx)) + ((g.Fy - g.Cy) * (g.Fy - g.Cy)));
                kmax = (int)Math.Max(1, Math.Ceiling(Math.Clamp(far / g.R, 0, 1e6)));
            }

            int periods = kmax - kmin;
            if (periods > MaxPeriods || periods * stops.Count > 250)
            {
                Note("A repeating or reflecting gradient needs more colour bands than a PDF shading holds here; it is drawn with its last band extended.");
                kmin = 0;
                kmax = 1;
                spread = Spread.Pad;
            }
        }

        if (spread == Spread.Pad)
        {
            plan.Stops = stops;
        }
        else
        {
            int periods = kmax - kmin;
            for (int k = 0; k < periods; k++)
            {
                bool flip = spread == Spread.Reflect && ((((kmin + k) % 2) + 2) % 2) == 1;
                for (int i = 0; i < stops.Count; i++)
                {
                    (double off, Rgba32 col) = flip ? (1 - stops[stops.Count - 1 - i].Offset, stops[stops.Count - 1 - i].Color) : stops[i];
                    plan.Stops.Add(((k + off) / periods, col));
                }
            }

            if (!g.Radial)
            {
                double dx = g.X2 - g.X1;
                double dy = g.Y2 - g.Y1;
                plan.X1 = g.X1 + (kmin * dx);
                plan.Y1 = g.Y1 + (kmin * dy);
                plan.X2 = g.X1 + (kmax * dx);
                plan.Y2 = g.Y1 + (kmax * dy);
            }
            else
            {
                plan.Cx = g.Fx + (periods * (g.Cx - g.Fx));
                plan.Cy = g.Fy + (periods * (g.Cy - g.Fy));
                plan.R = g.Fr + (periods * (g.R - g.Fr));
            }
        }

        for (int i = 1; i < plan.Stops.Count; i++)
        {
            plan.VaryingAlpha |= plan.Stops[i].Color.A != plan.Stops[0].Color.A;
        }

        plan.AlphaScale = plan.VaryingAlpha ? 1 : plan.Stops[0].Color.A / 255.0;
        return plan;
    }

    private void SetMaskFromGradient(PdfGradient gray)
    {
        PdfFormContentBuilder fb = _host.BeginForm(new PdfRectangle(-Huge, -Huge, Huge, Huge));
        PdfFormXObject form;
        try
        {
            fb.SetTransparencyGroup(true, false);
            fb.PaintGradient(gray);
            form = fb.Commit();
        }
        finally
        {
            fb.Dispose();
        }

        _b.SetSoftMask(form, PdfSoftMaskKind.Luminosity);
    }

    private static PdfGradient BuildPdfGradient(GradientPlan g, bool alpha)
    {
        var list = new PdfGradientStop[g.Stops.Count];
        double prev = 0;
        for (int i = 0; i < list.Length; i++)
        {
            double off = Math.Clamp(g.Stops[i].Offset, prev, 1);
            prev = off;
            Rgba32 c = g.Stops[i].Color;
            list[i] = new PdfGradientStop(off, alpha ? PdfGradientColor.Gray(c.A / 255.0) : PdfGradientColor.Rgb(c.R / 255.0, c.G / 255.0, c.B / 255.0));
        }

        return g.Radial
            ? PdfGradient.Radial(g.Fx, g.Fy, Math.Max(0, g.Fr), g.Cx, g.Cy, Math.Max(0, g.R), list)
            : PdfGradient.Axial(g.X1, g.Y1, g.X2, g.Y2, list);
    }

    private void FillSolid(SvgPath path, in SvgStyle st, Rgba32 c)
    {
        double opacity = st.FillOpacity * c.A / 255.0;
        if (opacity <= 0 || !Open())
        {
            return;
        }

        SetColor(c, false);
        if (opacity < 1)
        {
            _b.SetFillOpacity(opacity);
        }

        EmitPath(path);
        _b.Fill(st.FillEvenOdd);
        Close();
    }

    private bool TryRepresentativeColor(SvgElement server, in SvgStyle st, Viewport vp, out Rgba32 color)
    {
        color = default;
        if (server.Name == "pattern")
        {
            SvgElement? holder = PatternWithChildren(server);
            if (holder is null)
            {
                return false;
            }

            foreach (SvgElement c in holder.Children)
            {
                SvgStyle cs = StyleOf(c);
                Resolved r = ResolvePaint(cs.Fill.Kind == PaintKind.None ? cs.Stroke : cs.Fill, cs);
                if (r.Kind == ResolvedKind.Solid)
                {
                    color = r.Color;
                    return true;
                }
            }

            return false;
        }

        GradientSpec? g = ReadGradient(server, st, vp);
        if (g is null || g.Stops.Count == 0)
        {
            return false;
        }

        color = g.Stops[g.Stops.Count / 2].Color;
        return true;
    }

    // ---- reading gradient elements ----

    private static bool IsServer(SvgElement e) => e.Name is "linearGradient" or "radialGradient" or "pattern";

    // Attributes and children not given on a gradient or pattern come from the one its href names, and so on up the chain.
    private string? ChainAttr(SvgElement start, string name, bool sameKindOnly)
    {
        SvgElement e = start;
        for (int hops = 0; hops < 32; hops++)
        {
            if ((!sameKindOnly || e.Name == start.Name) && e.Attrs.TryGetValue(name, out string? v))
            {
                return v;
            }

            string? id = Href(e, out _);
            if (id is null || !_doc.Ids.TryGetValue(id, out SvgElement? next) || !IsServer(next) || (next.Name == "pattern") != (start.Name == "pattern"))
            {
                return null;
            }

            e = next;
        }

        return null;
    }

    private SvgElement? ChainWithChildren(SvgElement start, Func<SvgElement, bool> has)
    {
        SvgElement e = start;
        for (int hops = 0; hops < 32; hops++)
        {
            if (has(e))
            {
                return e;
            }

            string? id = Href(e, out _);
            if (id is null || !_doc.Ids.TryGetValue(id, out SvgElement? next) || !IsServer(next) || (next.Name == "pattern") != (start.Name == "pattern"))
            {
                return null;
            }

            e = next;
        }

        return null;
    }

    private SvgElement? PatternWithChildren(SvgElement pattern) => ChainWithChildren(pattern, e => e.Children.Count > 0);

    private GradientSpec? ReadGradient(SvgElement server, in SvgStyle st, Viewport vp)
    {
        var g = new GradientSpec { Radial = server.Name == "radialGradient" };
        g.BoxUnits = ChainAttr(server, "gradientUnits", false) != "userSpaceOnUse";
        if (ChainAttr(server, "gradientTransform", false) is { } gt)
        {
            if (SvgValues.TryTransform(gt, out Affine a) && a.IsFinite)
            {
                g.Transform = a;
            }
        }

        g.Spread = ChainAttr(server, "spreadMethod", false) switch { "reflect" => Spread.Reflect, "repeat" => Spread.Repeat, _ => Spread.Pad };
        SvgStyle ss = StyleOf(server);

        double C(string name, Axis axis, double defaultFraction)
        {
            string? v = ChainAttr(server, name, true);
            Length l = v is not null && Length.TryParse(v, out Length parsed) ? parsed : new Length(defaultFraction * 100, LengthUnit.Percent);
            if (g.BoxUnits)
            {
                return l.Unit == LengthUnit.Percent ? l.Value / 100 : l.Value;
            }

            return Resolve(l, axis, ss, vp);
        }

        if (g.Radial)
        {
            g.Cx = C("cx", Axis.X, 0.5);
            g.Cy = C("cy", Axis.Y, 0.5);
            g.R = C("r", Axis.Other, 0.5);
            g.Fx = ChainAttr(server, "fx", true) is not null ? C("fx", Axis.X, 0.5) : g.Cx;
            g.Fy = ChainAttr(server, "fy", true) is not null ? C("fy", Axis.Y, 0.5) : g.Cy;
            g.Fr = ChainAttr(server, "fr", true) is not null ? C("fr", Axis.Other, 0) : 0;
        }
        else
        {
            g.X1 = C("x1", Axis.X, 0);
            g.Y1 = C("y1", Axis.Y, 0);
            g.X2 = C("x2", Axis.X, 1);
            g.Y2 = C("y2", Axis.Y, 0);
        }

        SvgElement? holder = ChainWithChildren(server, e => e.Children.Exists(c => c.Name == "stop"));
        if (holder is null)
        {
            return g;
        }

        double last = 0;
        foreach (SvgElement stop in holder.Children)
        {
            if (stop.Name != "stop" || g.Stops.Count >= 256)
            {
                continue;
            }

            string? o = stop.Attr("offset");
            double off = 0;
            if (o is not null && Length.TryParse(o, out Length ol))
            {
                off = ol.Unit == LengthUnit.Percent ? ol.Value / 100 : ol.Value;
            }

            off = Math.Clamp(double.IsNaN(off) ? 0 : off, last, 1);
            last = off;
            SvgStyle stopStyle = StyleOf(stop);
            Rgba32 color = new(0, 0, 0, 255);
            if (stop.Prop("stop-color") is { } sc)
            {
                if (sc.Trim().Equals("currentColor", StringComparison.OrdinalIgnoreCase))
                {
                    color = stopStyle.Color;
                }
                else if (CssColor.TryParse(sc, out Rgba32 parsed))
                {
                    color = parsed;
                }
            }

            double so = 1;
            if (stop.Prop("stop-opacity") is { } sop)
            {
                ReadOnlySpan<char> t = sop.AsSpan().Trim();
                bool pct = t.EndsWith("%");
                if (double.TryParse(pct ? t[..^1] : t, NumberStyles.Float, CultureInfo.InvariantCulture, out double sv))
                {
                    so = Math.Clamp(pct ? sv / 100 : sv, 0, 1);
                }
            }

            g.Stops.Add((off, color with { A = (byte)Math.Round(color.A * so) }));
        }

        return g;
    }

    // ---- patterns ----

    private sealed record PatternGeometry(double Tw, double Th, double Tx, double Ty, Affine Transform, Affine Content, SvgElement Holder, long I0, long I1, long J0, long J1);

    /// <summary>Reads a pattern's tile size, grid origin, matrices and the range of tiles a box touches; false when it paints nothing.</summary>
    private bool ReadPatternGeometry(SvgElement pattern, Viewport vp, double bx0, double by0, double bx1, double by1, out PatternGeometry geometry)
    {
        geometry = null!;
        SvgStyle ps = StyleOf(pattern);
        bool unitsBox = ChainAttr(pattern, "patternUnits", false) != "userSpaceOnUse";
        bool contentBox = ChainAttr(pattern, "patternContentUnits", false) == "objectBoundingBox";
        double bw = bx1 - bx0;
        double bh = by1 - by0;
        if ((unitsBox || contentBox) && !(bw > 0 && bh > 0))
        {
            return false;
        }

        double PatternLength(string name, Axis axis)
        {
            string? v = ChainAttr(pattern, name, false);
            Length l = v is not null && Length.TryParse(v, out Length parsed) ? parsed : Length.Zero;
            if (unitsBox)
            {
                return l.Unit == LengthUnit.Percent ? l.Value / 100 : l.Value;
            }

            return Resolve(l, axis, ps, vp);
        }

        double tx = PatternLength("x", Axis.X);
        double ty = PatternLength("y", Axis.Y);
        double tw = PatternLength("width", Axis.X);
        double th = PatternLength("height", Axis.Y);
        if (unitsBox)
        {
            tx = bx0 + (tx * bw);
            ty = by0 + (ty * bh);
            tw *= bw;
            th *= bh;
        }

        if (!(tw > 0 && th > 0) || !double.IsFinite(tx + ty + tw + th))
        {
            return false;
        }

        Affine pt = Affine.Identity;
        if (ChainAttr(pattern, "patternTransform", false) is { } ptt && (!SvgValues.TryTransform(ptt, out pt) || !pt.IsInvertible))
        {
            pt = Affine.Identity;
        }

        SvgElement? holder = PatternWithChildren(pattern);
        if (holder is null)
        {
            return false;
        }

        Affine content = Affine.Identity;
        if (SvgValues.TryViewBox(ChainAttr(pattern, "viewBox", false), out double vx, out double vy, out double vw, out double vh))
        {
            content = AspectRatio.Parse(ChainAttr(pattern, "preserveAspectRatio", false)).Fit(vx, vy, vw, vh, tw, th);
        }
        else if (contentBox)
        {
            content = Affine.Scale(bw, bh);
        }

        // The tiles the shape's box touches, in pattern space (the grid starts at tx,ty).
        Affine inv = Inverse(pt);
        (double px0, double py0, double px1, double py1) = inv.TransformRect(bx0, by0, bx1, by1);
        long i0 = (long)Math.Floor(Math.Clamp((px0 - tx) / tw, -1e9, 1e9));
        long i1 = (long)Math.Ceiling(Math.Clamp((px1 - tx) / tw, -1e9, 1e9));
        long j0 = (long)Math.Floor(Math.Clamp((py0 - ty) / th, -1e9, 1e9));
        long j1 = (long)Math.Ceiling(Math.Clamp((py1 - ty) / th, -1e9, 1e9));
        geometry = new PatternGeometry(tw, th, tx, ty, pt, content, holder, i0, i1, j0, j1);
        return true;
    }

    private void FillWithPattern(SvgPath path, SvgElement pattern, in SvgStyle st, Viewport vp, double bx0, double by0, double bx1, double by1)
    {
        if (!_active.Add(pattern))
        {
            Note("patterns refer to each other in a cycle; the pattern is not drawn there.");
            return;
        }

        try
        {
            if (!ReadPatternGeometry(pattern, vp, bx0, by0, bx1, by1, out PatternGeometry pg))
            {
                return;
            }

            (double tw, double th, double tx, double ty, Affine pt, Affine content, SvgElement holder, long i0, long i1, long j0, long j1) = pg;
            SvgStyle ps = StyleOf(pattern);
            if (i1 - i0 > MaxTiles || j1 - j0 > MaxTiles || (i1 - i0) * (j1 - j0) > MaxTiles)
            {
                Note("A pattern would need more than " + MaxTiles + " tiles for one shape; the shape is not filled.");
                return;
            }

            if (!Open())
            {
                return;
            }

            try
            {
                PdfFormXObject? tile = BuildTile(holder, ps, tw, th, content, vp);
                if (tile is null)
                {
                    return;
                }

                EmitPath(path);
                _b.Clip(st.FillEvenOdd);
                _b.EndPath();
                if (st.FillOpacity < 1)
                {
                    _b.SetOpacity(st.FillOpacity);
                }

                if (!pt.IsIdentity)
                {
                    _b.Transform(pt.A, pt.B, pt.C, pt.D, Coord.D(pt.E), Coord.D(pt.F));
                }

                for (long j = j0; j < j1; j++)
                {
                    for (long i = i0; i < i1; i++)
                    {
                        _b.Save();
                        _b.Translate(Coord.D(tx + (i * tw)), Coord.D(ty + (j * th)));
                        _b.DrawForm(tile);
                        _b.Restore();
                    }
                }
            }
            finally
            {
                Close();
            }
        }
        finally
        {
            _active.Remove(pattern);
        }
    }

    private PdfFormXObject? BuildTile(SvgElement holder, SvgStyle ps, double tw, double th, Affine content, Viewport vp)
    {
        if (++_groups > MaxGroups)
        {
            Note("More than " + MaxGroups + " forms; further patterns are not drawn.");
            return null;
        }

        PdfFormContentBuilder fb = _host.BeginForm(new PdfRectangle(0, 0, tw, th));
        PdfContentBuilder outer = _b;
        int outerSaved = _saved;
        try
        {
            _b = fb;
            _saved = 0;
            if (!content.IsIdentity)
            {
                _b.Transform(content.A, content.B, content.C, content.D, Coord.D(content.E), Coord.D(content.F));
            }

            foreach (SvgElement c in holder.Children)
            {
                DrawElement(c, ps, vp, null, null);
            }

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
}
