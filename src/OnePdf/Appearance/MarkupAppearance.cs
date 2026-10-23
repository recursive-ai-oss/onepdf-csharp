using System.Globalization;

namespace OnePdf.Appearance;

/// <summary>
/// Appearances for the simple markup annotations of ISO 32000-2 12.5.6: Square, Circle, Line, Polygon, PolyLine, Ink, the four
/// text markup types, FreeText and the Text note icon. The drawing is in default user space, so the form's <c>/BBox</c> and the
/// returned rectangle are the same box (ISO 32000-2 12.5.5).
/// </summary>
internal static class MarkupAppearance
{
    private const double MaxExtent = 1e7;

    public static bool Supports(string subtype) => subtype is "Square" or "Circle" or "Line" or "Polygon" or "PolyLine" or "Ink"
        or "Highlight" or "Underline" or "StrikeOut" or "Squiggly" or "FreeText" or "Text";

    private sealed class Geo
    {
        public double L = double.PositiveInfinity, B = double.PositiveInfinity, R = double.NegativeInfinity, T = double.NegativeInfinity;

        public bool Any => L <= R;

        public void Add(double x, double y)
        {
            L = Math.Min(L, x);
            R = Math.Max(R, x);
            B = Math.Min(B, y);
            T = Math.Max(T, y);
        }
    }

    public static PdfGeneratedAppearance? Generate(GenContext c, string subtype, PdfRectangle annotRect)
    {
        var cb = new ContentBuilder();
        var geo = new Geo();
        (double bw, char style, double[] dash) = c.Border();
        double[]? stroke = GenContext.Components(c.Annotation.Get("C"));
        double[]? fill = GenContext.Components(c.Annotation.Get("IC"));
        double opacity = c.Annotation.ContainsKey("CA") ? Math.Clamp(c.Annotation.GetDouble("CA", 1), 0, 1) : 1;
        string? blend = subtype == "Highlight" ? "Multiply" : null;
        FontChain? chain = null;
        var used = new HashSet<int>();
        int items = c.Limits.MaxAppearanceItems;
        PdfRectangle rect = annotRect;
        bool haveRect = !annotRect.IsEmpty;
        string description = subtype;

        cb.Raw("q\n");
        if (opacity < 1 || blend is not null)
        {
            cb.Raw("/GS gs\n");
        }

        switch (subtype)
        {
            case "Square":
            case "Circle":
            {
                if (!haveRect)
                {
                    c.Warn("The " + subtype + " annotation has an empty rectangle; no appearance was generated");
                    return null;
                }

                PdfRectangle inner = Inset(c, rect);
                double hw = bw / 2;
                double x = inner.Left + hw, y = inner.Bottom + hw, w = inner.Width - bw, h = inner.Height - bw;
                if (!(w > 0 && h > 0))
                {
                    c.Warn("The " + subtype + " annotation's rectangle is too small for its border; no appearance was generated");
                    return null;
                }

                StrokeSetup(cb, stroke ?? [0], bw, style, dash, out bool stroked);
                bool filled = cb.Color(fill, false);
                if (subtype == "Square")
                {
                    cb.Rect(x, y, w, h);
                }
                else
                {
                    cb.Ellipse(x, y, w, h);
                }

                cb.Raw(PaintOp(filled, stroked));
                geo.Add(rect.Left, rect.Bottom);
                geo.Add(rect.Right, rect.Top);
                break;
            }

            case "Line":
            {
                if (c.Annotation.GetArray("L") is not { Count: >= 4 } l)
                {
                    c.Warn("The Line annotation has no /L; no appearance was generated");
                    return null;
                }

                double x1 = l.GetDouble(0), y1 = l.GetDouble(1), x2 = l.GetDouble(2), y2 = l.GetDouble(3);
                if (!double.IsFinite(x1 + y1 + x2 + y2))
                {
                    return null;
                }

                StrokeSetup(cb, stroke ?? [0], bw, style, dash, out bool stroked);
                cb.Color(fill, false);
                LineBody(c, cb, geo, x1, y1, x2, y2, bw, fill is not null, stroked);
                break;
            }

            case "Polygon":
            case "PolyLine":
            {
                List<(double X, double Y)>? pts = Points(c.Annotation.GetArray("Vertices"), items, c);
                if (pts is null || pts.Count < 2)
                {
                    c.Warn("The " + subtype + " annotation has fewer than two /Vertices; no appearance was generated");
                    return null;
                }

                StrokeSetup(cb, stroke ?? [0], bw, style, dash, out bool stroked);
                bool closed = subtype == "Polygon";
                bool filled = closed && cb.Color(fill, false);
                cb.Move(pts[0].X, pts[0].Y);
                geo.Add(pts[0].X, pts[0].Y);
                for (int i = 1; i < pts.Count; i++)
                {
                    cb.Line(pts[i].X, pts[i].Y);
                    geo.Add(pts[i].X, pts[i].Y);
                }

                if (closed)
                {
                    cb.Raw("h\n");
                }

                cb.Raw(PaintOp(filled, stroked));
                if (!closed)
                {
                    cb.Color(fill, false);
                    (string start, string end) = LineEndings(c.Annotation);
                    DrawEnding(cb, geo, start, pts[0], pts[1], bw, fill is not null, stroked);
                    DrawEnding(cb, geo, end, pts[^1], pts[^2], bw, fill is not null, stroked);
                }

                break;
            }

            case "Ink":
            {
                if (c.Annotation.GetArray("InkList") is not { Count: > 0 } ink)
                {
                    c.Warn("The Ink annotation has no /InkList; no appearance was generated");
                    return null;
                }

                StrokeSetup(cb, stroke ?? [0], bw, style, dash, out _);
                cb.Raw("1 J 1 j\n");
                int budget = items;
                for (int s = 0; s < ink.Count && budget > 0; s++)
                {
                    List<(double X, double Y)>? pts = ink[s] is PdfArray a ? Points(a, budget, c) : null;
                    if (pts is null || pts.Count == 0)
                    {
                        continue;
                    }

                    budget -= pts.Count;
                    cb.Move(pts[0].X, pts[0].Y);
                    geo.Add(pts[0].X, pts[0].Y);
                    if (pts.Count == 1)
                    {
                        cb.Line(pts[0].X, pts[0].Y);
                    }

                    for (int i = 1; i < pts.Count; i++)
                    {
                        cb.Line(pts[i].X, pts[i].Y);
                        geo.Add(pts[i].X, pts[i].Y);
                    }

                    cb.Raw("S\n");
                }

                break;
            }

            case "Highlight":
            case "Underline":
            case "StrikeOut":
            case "Squiggly":
                if (!TextMarkup(c, cb, geo, subtype, stroke, bw, haveRect ? rect : null))
                {
                    return null;
                }

                break;

            case "FreeText":
                if (!haveRect)
                {
                    c.Warn("The FreeText annotation has an empty rectangle; no appearance was generated");
                    return null;
                }

                FreeText(c, cb, geo, rect, stroke, bw, style, dash, ref chain, used);
                break;

            case "Text":
                return TextNote(c, rect, stroke);

            default:
                return null;
        }

        cb.Raw("Q\n");
        if (!geo.Any)
        {
            return null;
        }

        double pad = subtype is "Highlight" or "FreeText" ? 0 : Math.Max(bw, 0) / 2 + (subtype is "Line" or "Polygon" or "PolyLine" ? Math.Max(8, bw * 4) : 0);
        double l0 = geo.L - pad, b0 = geo.B - pad, r0 = geo.R + pad, t0 = geo.T + pad;
        if (haveRect)
        {
            l0 = Math.Min(l0, rect.Left);
            b0 = Math.Min(b0, rect.Bottom);
            r0 = Math.Max(r0, rect.Right);
            t0 = Math.Max(t0, rect.Top);
        }

        if (!double.IsFinite(l0 + b0 + r0 + t0) || r0 - l0 > MaxExtent || t0 - b0 > MaxExtent || Math.Abs(l0) > MaxExtent || Math.Abs(b0) > MaxExtent)
        {
            c.Warn("The generated geometry is out of range; no appearance was generated");
            return null;
        }

        if (!(r0 > l0)) { r0 = l0 + 1; }
        if (!(t0 > b0)) { t0 = b0 + 1; }
        PdfDictionary? gs = null;
        if (opacity < 1 || blend is not null)
        {
            PdfDictionary g = Cos.Dict().With("Type", Cos.Name("ExtGState")).With("CA", Cos.Real(opacity)).With("ca", Cos.Real(opacity));
            if (blend is not null)
            {
                g.Set("BM", Cos.Name(blend));
            }

            gs = Cos.Dict().With("GS", g);
        }

        PdfDictionary form = GenContext.Form([l0, b0, r0, t0], GenContext.Resources(chain, used, gs));
        return new PdfGeneratedAppearance(form, cb.ToBytes(), new PdfRectangle(l0, b0, r0, t0), description);
    }

    private static string PaintOp(bool filled, bool stroked) => filled ? (stroked ? "B\n" : "f\n") : (stroked ? "S\n" : "n\n");

    /// <summary>Sets the stroke colour, width and dash; <paramref name="stroked"/> is false when the width is zero.</summary>
    private static void StrokeSetup(ContentBuilder cb, double[] colour, double bw, char style, double[] dash, out bool stroked)
    {
        stroked = bw > 0 && cb.Color(colour, true);
        if (!stroked)
        {
            return;
        }

        cb.Op("w", bw);
        if (style == 'D')
        {
            cb.Raw(GenContext.DashOp(dash));
        }
    }

    /// <summary>The annotation rectangle reduced by <c>/RD</c> (left, top, right, bottom differences, ISO 32000-2 Table 180).</summary>
    private static PdfRectangle Inset(GenContext c, PdfRectangle r)
    {
        if (c.Annotation.GetArray("RD") is not { Count: >= 4 } rd)
        {
            return r;
        }

        double l = rd.GetDouble(0), t = rd.GetDouble(1), rr = rd.GetDouble(2), b = rd.GetDouble(3);
        if (!double.IsFinite(l + t + rr + b) || l < 0 || t < 0 || rr < 0 || b < 0 || l + rr >= r.Width || t + b >= r.Height)
        {
            return r;
        }

        return new PdfRectangle(r.Left + l, r.Bottom + b, r.Right - rr, r.Top - t);
    }

    private static List<(double X, double Y)>? Points(PdfArray? a, int maxPoints, GenContext c)
    {
        if (a is null)
        {
            return null;
        }

        int n = Math.Min(a.Count / 2, maxPoints);
        if (a.Count / 2 > maxPoints)
        {
            c.Warn("An annotation has more than " + maxPoints.ToString(CultureInfo.InvariantCulture) + " points; the rest were left out");
        }

        var list = new List<(double, double)>(n);
        for (int i = 0; i < n; i++)
        {
            double x = a.GetDouble(2 * i), y = a.GetDouble((2 * i) + 1);
            if (!double.IsFinite(x + y) || Math.Abs(x) > MaxExtent || Math.Abs(y) > MaxExtent)
            {
                continue;
            }

            list.Add((x, y));
        }

        return list;
    }

    // ---- lines and their ends ----

    private static (string Start, string End) LineEndings(PdfDictionary a)
    {
        if (a.Get("LE") is PdfArray le)
        {
            return (le.Count > 0 ? le.GetName(0) ?? "None" : "None", le.Count > 1 ? le.GetName(1) ?? "None" : "None");
        }

        return a.GetName("LE") is { } one ? (one, "None") : ("None", "None");
    }

    private static void LineBody(GenContext c, ContentBuilder cb, Geo geo, double x1, double y1, double x2, double y2, double bw, bool filled, bool stroked)
    {
        double dx = x2 - x1, dy = y2 - y1, len = Math.Sqrt((dx * dx) + (dy * dy));
        double ux = len > 0 ? dx / len : 1, uy = len > 0 ? dy / len : 0;

        // ISO 32000-2 12.5.6.7: leader lines of length /LL run perpendicular to the line (clockwise from start to end when
        // positive), extended by /LLE; the line itself moves out by /LL, leaving a gap /LLO from the endpoints.
        double ll = c.Annotation.GetDouble("LL"), lle = Math.Abs(c.Annotation.GetDouble("LLE")), llo = Math.Abs(c.Annotation.GetDouble("LLO"));
        if (!double.IsFinite(ll) || Math.Abs(ll) > MaxExtent)
        {
            ll = 0;
        }

        double nx = uy, ny = -ux;
        if (ll != 0 && len > 0)
        {
            double sign = Math.Sign(ll);
            double ex = (Math.Abs(ll) + (double.IsFinite(lle) ? lle : 0)) * sign;
            double off = double.IsFinite(llo) ? llo * sign : 0;
            cb.Move(x1 + (nx * off), y1 + (ny * off)).Line(x1 + (nx * ex), y1 + (ny * ex));
            cb.Move(x2 + (nx * off), y2 + (ny * off)).Line(x2 + (nx * ex), y2 + (ny * ex));
            if (stroked)
            {
                cb.Raw("S\n");
            }
            else
            {
                cb.Raw("n\n");
            }

            geo.Add(x1 + (nx * ex), y1 + (ny * ex));
            geo.Add(x2 + (nx * ex), y2 + (ny * ex));
            x1 += nx * ll;
            y1 += ny * ll;
            x2 += nx * ll;
            y2 += ny * ll;
        }

        cb.Move(x1, y1).Line(x2, y2).Raw(stroked ? "S\n" : "n\n");
        geo.Add(x1, y1);
        geo.Add(x2, y2);
        (string start, string end) = LineEndings(c.Annotation);
        DrawEnding(cb, geo, start, (x1, y1), (x2, y2), bw, filled, stroked);
        DrawEnding(cb, geo, end, (x2, y2), (x1, y1), bw, filled, stroked);
    }

    /// <summary>Draws a line ending at <paramref name="p"/>; <paramref name="toward"/> is the next point along the line, so the ending points away from it.</summary>
    private static void DrawEnding(ContentBuilder cb, Geo geo, string name, (double X, double Y) p, (double X, double Y) toward, double bw, bool filled, bool stroked)
    {
        if (name == "None")
        {
            return;
        }

        double dx = toward.X - p.X, dy = toward.Y - p.Y, len = Math.Sqrt((dx * dx) + (dy * dy));
        if (!(len > 0))
        {
            return;
        }

        double ux = dx / len, uy = dy / len;   // into the line
        double nx = -uy, ny = ux;
        double size = Math.Max(6, bw * 4);
        string paint = PaintOp(filled, stroked);
        void Pt(double x, double y) => geo.Add(x, y);
        switch (name)
        {
            case "Square":
                cb.Move(p.X + (nx * size / 2) + (ux * size / 2), p.Y + (ny * size / 2) + (uy * size / 2))
                    .Line(p.X - (nx * size / 2) + (ux * size / 2), p.Y - (ny * size / 2) + (uy * size / 2))
                    .Line(p.X - (nx * size / 2) - (ux * size / 2), p.Y - (ny * size / 2) - (uy * size / 2))
                    .Line(p.X + (nx * size / 2) - (ux * size / 2), p.Y + (ny * size / 2) - (uy * size / 2))
                    .Raw("h\n").Raw(paint);
                Pt(p.X + size, p.Y + size);
                Pt(p.X - size, p.Y - size);
                break;
            case "Circle":
                cb.Ellipse(p.X - (size / 2), p.Y - (size / 2), size, size).Raw(paint);
                Pt(p.X + size, p.Y + size);
                Pt(p.X - size, p.Y - size);
                break;
            case "Diamond":
                cb.Move(p.X + (ux * size / 2), p.Y + (uy * size / 2)).Line(p.X + (nx * size / 2), p.Y + (ny * size / 2))
                    .Line(p.X - (ux * size / 2), p.Y - (uy * size / 2)).Line(p.X - (nx * size / 2), p.Y - (ny * size / 2)).Raw("h\n").Raw(paint);
                Pt(p.X + size, p.Y + size);
                Pt(p.X - size, p.Y - size);
                break;
            case "OpenArrow":
            case "ClosedArrow":
            case "ROpenArrow":
            case "RClosedArrow":
            {
                double dir = name[0] == 'R' ? -1 : 1;
                double ax = ux * dir, ay = uy * dir;
                double arm = size * 1.5;
                double w1x = p.X + (arm * ((ax * 0.866) + (nx * 0.5))), w1y = p.Y + (arm * ((ay * 0.866) + (ny * 0.5)));
                double w2x = p.X + (arm * ((ax * 0.866) - (nx * 0.5))), w2y = p.Y + (arm * ((ay * 0.866) - (ny * 0.5)));
                cb.Move(w1x, w1y).Line(p.X, p.Y).Line(w2x, w2y);
                if (name.EndsWith("ClosedArrow", StringComparison.Ordinal))
                {
                    cb.Raw("h\n").Raw(paint);
                }
                else
                {
                    cb.Raw(stroked ? "S\n" : "n\n");
                }

                Pt(w1x, w1y);
                Pt(w2x, w2y);
                Pt(p.X, p.Y);
                break;
            }

            case "Butt":
                cb.Move(p.X + (nx * size / 2), p.Y + (ny * size / 2)).Line(p.X - (nx * size / 2), p.Y - (ny * size / 2)).Raw(stroked ? "S\n" : "n\n");
                Pt(p.X + size, p.Y + size);
                Pt(p.X - size, p.Y - size);
                break;
            case "Slash":
            {
                double sx = (ux * 0.5) + (nx * 0.866), sy = (uy * 0.5) + (ny * 0.866);
                cb.Move(p.X + (sx * size / 2), p.Y + (sy * size / 2)).Line(p.X - (sx * size / 2), p.Y - (sy * size / 2)).Raw(stroked ? "S\n" : "n\n");
                Pt(p.X + size, p.Y + size);
                Pt(p.X - size, p.Y - size);
                break;
            }
        }
    }

    // ---- text markup ----

    private static bool TextMarkup(GenContext c, ContentBuilder cb, Geo geo, string subtype, double[]? colour, double bw, PdfRectangle? rect)
    {
        var quads = new List<double[]>();
        if (c.Annotation.GetArray("QuadPoints") is { Count: >= 8 } qp)
        {
            int groups = Math.Min(qp.Count / 8, c.Limits.MaxAppearanceItems);
            if (qp.Count / 8 > groups)
            {
                c.Warn("A text markup annotation has more than " + groups.ToString(CultureInfo.InvariantCulture) + " quadrilaterals; the rest were left out");
            }

            for (int g = 0; g < groups; g++)
            {
                var q = new double[8];
                bool ok = true;
                for (int i = 0; i < 8; i++)
                {
                    q[i] = qp.GetDouble((g * 8) + i);
                    ok &= double.IsFinite(q[i]) && Math.Abs(q[i]) <= MaxExtent;
                }

                if (ok)
                {
                    quads.Add(q);
                }
            }
        }
        else if (rect is { } r)
        {
            quads.Add([r.Left, r.Top, r.Right, r.Top, r.Left, r.Bottom, r.Right, r.Bottom]);
        }

        if (quads.Count == 0)
        {
            c.Warn("The " + subtype + " annotation has no /QuadPoints and no rectangle; no appearance was generated");
            return false;
        }

        colour ??= subtype == "Highlight" ? [1, 1, 0] : [0, 0, 0];
        bool highlight = subtype == "Highlight";
        cb.Color(colour, !highlight);
        if (!highlight)
        {
            cb.Op("w", 1);
        }

        // The point order that viewers write is upper left, upper right, lower left, lower right (a Z), not the
        // counterclockwise order the standard's text suggests; the Z order is what files in the wild contain.
        foreach (double[] q in quads)
        {
            double tlx = q[0], tly = q[1], trx = q[2], try_ = q[3], blx = q[4], bly = q[5], brx = q[6], bry = q[7];
            for (int i = 0; i < 8; i += 2)
            {
                geo.Add(q[i], q[i + 1]);
            }

            switch (subtype)
            {
                case "Highlight":
                    cb.Move(blx, bly).Line(brx, bry).Line(trx, try_).Line(tlx, tly).Raw("h f\n");
                    break;
                case "Underline":
                    cb.Move(blx, bly).Line(brx, bry).Raw("S\n");
                    break;
                case "StrikeOut":
                    cb.Move((tlx + blx) / 2, (tly + bly) / 2).Line((trx + brx) / 2, (try_ + bry) / 2).Raw("S\n");
                    break;
                default:
                {
                    double vx = brx - blx, vy = bry - bly, len = Math.Sqrt((vx * vx) + (vy * vy));
                    double ux = tlx - blx, uy = tly - bly, ul = Math.Sqrt((ux * ux) + (uy * uy));
                    if (!(len > 0) || !(ul > 0))
                    {
                        break;
                    }

                    int steps = (int)Math.Min(Math.Ceiling(len / 2), c.Limits.MaxAppearanceItems);
                    ux /= ul;
                    uy /= ul;
                    cb.Move(blx, bly);
                    for (int i = 1; i <= steps; i++)
                    {
                        double t = (double)i / steps, h = (i & 1) == 1 ? 1.5 : 0;
                        cb.Line(blx + (vx * t) + (ux * h), bly + (vy * t) + (uy * h));
                    }

                    cb.Raw("S\n");
                    break;
                }
            }
        }

        _ = bw;
        return true;
    }

    // ---- FreeText ----

    private static void FreeText(GenContext c, ContentBuilder cb, Geo geo, PdfRectangle rect, double[]? background, double bw, char style, double[] dash, ref FontChain? chain, HashSet<int> used)
    {
        PdfDictionary a = c.Annotation;
        geo.Add(rect.Left, rect.Bottom);
        geo.Add(rect.Right, rect.Top);
        DefaultAppearance da = DefaultAppearance.Parse(GenContext.TextOf(a.Get("DA")) ?? GenContext.TextOf(c.Doc.Catalog.GetDictionary("AcroForm")?.Get("DA")));
        double[] ink = da.Color ?? [0];
        bool hasBorder = a.GetDictionary("BS") is not null || a.GetArray("Border") is not null;
        double width = hasBorder ? bw : 0;
        PdfRectangle box = Inset(c, rect);

        if (cb.Color(background, false))
        {
            cb.Rect(rect.Left, rect.Bottom, rect.Width, rect.Height).Raw("f\n");
        }

        if (width > 0)
        {
            cb.Raw("q\n");
            if (cb.Color(ink, true))
            {
                cb.Op("w", width);
                if (style == 'D')
                {
                    cb.Raw(GenContext.DashOp(dash));
                }

                cb.Rect(box.Left + (width / 2), box.Bottom + (width / 2), box.Width - width, box.Height - width).Raw("S\n");
            }

            cb.Raw("Q\n");
        }

        string text = GenContext.TextOf(a.Get("Contents")) ?? string.Empty;
        if (text.Length > 0)
        {
            chain = c.Chain(da);
            List<ShapedGlyph> glyphs = chain.Shape(text, c.Limits.MaxAppearanceText, true);
            c.NoteDropped(chain);
            double fs = da.Size > 0 ? da.Size : 12;
            double availW = box.Width - (2 * width) - 4, availH = box.Height - (2 * width) - 4;
            if (glyphs.Count > 0 && availW > 0 && availH > 0)
            {
                List<TextLine> lines = TextLayout.Wrap(glyphs, availW, fs, c.Limits.MaxAppearanceItems);
                int q = (int)Math.Clamp(a.GetInt("Q"), 0, 2);
                double lh = fs * TextLayout.LineHeight;
                double y = box.Top - width - 2 - (TextLayout.Ascent * fs);
                int cur = -1;
                cb.Raw("q\n").Rect(box.Left + width, box.Bottom + width, box.Width - (2 * width), box.Height - (2 * width)).Raw("W n\nBT\n");
                var colourBuilder = new ContentBuilder();
                if (!colourBuilder.Color(da.Color, false))
                {
                    colourBuilder.Gray(0, false);
                }

                cb.Raw(colourBuilder.ToString());
                foreach (TextLine ln in lines)
                {
                    if (y < box.Bottom - lh)
                    {
                        break;
                    }

                    if (ln.End > ln.Start)
                    {
                        double tw = ln.Width * fs;
                        double x = box.Left + width + 2;
                        if (tw < availW)
                        {
                            x += q == 1 ? (availW - tw) / 2 : q == 2 ? availW - tw : 0;
                        }

                        TextLayout.EmitLine(cb, chain, glyphs, ln.Start, ln.End, x, y, fs, ref cur, used);
                    }

                    y -= lh;
                }

                cb.Raw("ET\nQ\n");
            }
        }

        // The callout line: two or three points from the text box to the thing it points at, with the /LE ending at the first point.
        if (a.GetArray("CL") is { Count: >= 4 } cl && (cl.Count == 4 || cl.Count >= 6))
        {
            int n = cl.Count >= 6 ? 3 : 2;
            var pts = new List<(double X, double Y)>();
            for (int i = 0; i < n; i++)
            {
                double x = cl.GetDouble(2 * i), y = cl.GetDouble((2 * i) + 1);
                if (double.IsFinite(x + y) && Math.Abs(x) <= MaxExtent && Math.Abs(y) <= MaxExtent)
                {
                    pts.Add((x, y));
                }
            }

            if (pts.Count == n)
            {
                cb.Raw("q\n");
                if (cb.Color(ink, true))
                {
                    cb.Op("w", bw > 0 ? bw : 1);
                    cb.Move(pts[0].X, pts[0].Y);
                    for (int i = 1; i < n; i++)
                    {
                        cb.Line(pts[i].X, pts[i].Y);
                    }

                    cb.Raw("S\n");
                    cb.Color(ink, false);
                    (string start, _) = LineEndings(a);
                    DrawEnding(cb, geo, start, pts[0], pts[1], bw > 0 ? bw : 1, true, true);
                }

                cb.Raw("Q\n");
                foreach ((double x, double y) in pts)
                {
                    geo.Add(x, y);
                }
            }
        }
    }

    // ---- sticky note ----

    private static PdfGeneratedAppearance? TextNote(GenContext c, PdfRectangle annotRect, double[]? colour)
    {
        // The icon is 20 by 20 units at the upper left of the annotation rectangle (ISO 32000-2 12.5.6.4).
        double left = annotRect.Left, top = annotRect.Top;
        if (!double.IsFinite(left + top) || Math.Abs(left) > MaxExtent || Math.Abs(top) > MaxExtent || (left == 0 && top == 0 && annotRect.IsEmpty && c.Annotation.GetArray("Rect") is null))
        {
            c.Warn("The Text annotation has no valid /Rect; no appearance was generated");
            return null;
        }

        string name = c.Annotation.GetName("Name") ?? "Note";
        var cb = new ContentBuilder();
        cb.Raw("q\n");
        cb.Op("w", 1).Raw("1 J 1 j\n");
        cb.Color(colour ?? [1, 1, 0], false);
        cb.Gray(0, true);
        if (name == "Comment")
        {
            cb.Move(2, 5).Line(2, 18).Line(18, 18).Line(18, 5).Line(11, 5).Line(6, 1).Line(7, 5).Raw("h B\n");
            cb.Op("w", 1).Move(5, 14).Line(15, 14).Move(5, 11).Line(15, 11).Move(5, 8).Line(11, 8).Raw("S\n");
        }
        else if (name == "Help")
        {
            cb.Ellipse(1.5, 1.5, 17, 17).Raw("B\n");
            cb.Op("w", 1.6).Move(7, 13.5).Curve(7, 17, 13, 17, 13, 13.5).Curve(13, 11, 10, 10.5, 10, 8).Raw("S\n");
            cb.Gray(0, false).Ellipse(9.1, 4.3, 1.8, 1.8).Raw("f\n");
        }
        else
        {
            cb.Move(2, 1).Line(2, 19).Line(18, 19).Line(18, 6).Line(13, 1).Raw("h B\n");
            cb.Move(13, 1).Line(13, 6).Line(18, 6).Raw("S\n");
            cb.Op("w", 1).Move(5, 15).Line(15, 15).Move(5, 12).Line(15, 12).Move(5, 9).Line(15, 9).Move(5, 6).Line(10, 6).Raw("S\n");
        }

        cb.Raw("Q\n");
        double b = top - 20;
        PdfDictionary form = GenContext.Form([0, 0, 20, 20], GenContext.Resources(null, null, null));
        form.Set("Matrix", Cos.Array(1, 0, 0, 1, 0, 0));
        return new PdfGeneratedAppearance(form, cb.ToBytes(), new PdfRectangle(left, b, left + 20, top), "Text note icon");
    }
}
