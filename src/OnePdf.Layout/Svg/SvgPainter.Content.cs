using System.Globalization;
using OnePdf.Layout.Svg.Dom;
using SvgTextStyle = OnePdf.Layout.Svg.Dom.TextStyle;

namespace OnePdf.Layout.Svg;

internal sealed partial class SvgPainter
{
    // ---- markers (SVG 1.1 11.6) ----

    private void DrawMarkers(SvgElement e, in SvgStyle st, Viewport vp, MarkerPath mp)
    {
        MarkerRefs refs = st.Markers!;
        List<MarkerVertex> vertices = mp.Vertices();
        if (vertices.Count == 0)
        {
            return;
        }

        if (vertices.Count > 100_000)
        {
            Note("A path has more than 100000 vertices; its markers are left out.");
            return;
        }

        double strokeWidth = Math.Max(0, Resolve(st.StrokeWidth, Axis.Other, st, vp));
        Paint? savedFill = _ctxFill;
        Paint? savedStroke = _ctxStroke;
        (_ctxFill, _ctxStroke) = ContextPaints(st);
        try
        {
            for (int i = 0; i < vertices.Count; i++)
            {
                bool start = i == 0;
                string? id = start ? refs.Start : i == vertices.Count - 1 ? refs.End : refs.Mid;
                if (id is null || !_doc.Ids.TryGetValue(id, out SvgElement? marker) || marker.Name != "marker")
                {
                    continue;
                }

                DrawMarker(marker, vertices[i], start, strokeWidth, vp);
            }
        }
        finally
        {
            _ctxFill = savedFill;
            _ctxStroke = savedStroke;
        }
    }

    private void DrawMarker(SvgElement marker, in MarkerVertex v, bool isStart, double strokeWidth, Viewport vp)
    {
        if (!_active.Add(marker))
        {
            Note("A marker contains a shape that uses the same marker; the cycle is cut.");
            return;
        }

        try
        {
            if (!Enter())
            {
                return;
            }

            try
            {
                SvgStyle mst = StyleOf(marker);
                double scale = marker.Attr("markerUnits") == "userSpaceOnUse" ? 1 : strokeWidth;
                double mw = marker.Attr("markerWidth") is { } a && Length.TryParse(a, out Length la) ? Resolve(la, Axis.X, mst, vp) : 3;
                double mh = marker.Attr("markerHeight") is { } b && Length.TryParse(b, out Length lb) ? Resolve(lb, Axis.Y, mst, vp) : 3;
                if (!(mw > 0 && mh > 0 && scale > 0))
                {
                    return;
                }

                double refX = Num(marker, "refX", Axis.X, mst, vp);
                double refY = Num(marker, "refY", Axis.Y, mst, vp);
                double angle = 0;
                string? orient = marker.Attr("orient")?.Trim();
                if (orient == "auto")
                {
                    angle = v.Angle;
                }
                else if (orient == "auto-start-reverse")
                {
                    angle = v.Angle + (isStart ? 180 : 0);
                }
                else if (orient is not null)
                {
                    ReadOnlySpan<char> o = orient.EndsWith("deg", StringComparison.Ordinal) ? orient.AsSpan(0, orient.Length - 3) : orient;
                    if (double.TryParse(o, NumberStyles.Float, CultureInfo.InvariantCulture, out double deg) && double.IsFinite(deg))
                    {
                        angle = deg;
                    }
                }

                Affine vb = Affine.Identity;
                var nvp = new Viewport(mw, mh);
                if (SvgValues.TryViewBox(marker.Attr("viewBox"), out double vx, out double vy, out double vw, out double vh))
                {
                    vb = AspectRatio.Parse(marker.Attr("preserveAspectRatio")).Fit(vx, vy, vw, vh, mw, mh);
                    nvp = new Viewport(vw, vh);
                }

                (double rx, double ry) = vb.Apply(refX, refY);
                double rad = angle * Math.PI / 180;
                double cos = Math.Cos(rad);
                double sin = Math.Sin(rad);
                Affine place = Affine.Translate(v.X, v.Y)
                    .Multiply(new Affine(cos, sin, -sin, cos, 0, 0))
                    .Multiply(Affine.Scale(scale, scale))
                    .Multiply(Affine.Translate(-rx, -ry));
                if (!place.IsInvertible)
                {
                    return;
                }

                string? overflow = marker.Prop("overflow");
                if (!Open())
                {
                    return;
                }

                try
                {
                    _b.Transform(place.A, place.B, place.C, place.D, Coord.D(place.E), Coord.D(place.F));
                    if (overflow is not ("visible" or "auto"))
                    {
                        _b.Rectangle(0, 0, mw, mh);
                        _b.Clip();
                        _b.EndPath();
                    }

                    if (!vb.IsIdentity)
                    {
                        _b.Transform(vb.A, vb.B, vb.C, vb.D, Coord.D(vb.E), Coord.D(vb.F));
                    }

                    foreach (SvgElement c in marker.Children)
                    {
                        DrawElement(c, mst, nvp, null, null);
                    }
                }
                finally
                {
                    Close();
                }
            }
            finally
            {
                _depth--;
            }
        }
        finally
        {
            _active.Remove(marker);
        }
    }

    // ---- raster images (data URIs only) ----

    private void DrawImage(SvgElement e, in SvgStyle st, Viewport vp)
    {
        if (!st.Visible || e.Attr("href") is not { } href)
        {
            return;
        }

        if (!href.AsSpan().TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            Note("An image refers to something outside the document (" + Trim(href.Trim()) + "); references to other files or addresses are never followed.");
            return;
        }

        if (!DataUri.TryDecode(href, _lim.MaxImageBytes, out byte[] data))
        {
            Note("An image data URI could not be decoded.");
            return;
        }

        _imageBytes += data.Length;
        if (_imageBytes > _lim.MaxTotalImageBytes)
        {
            throw new PdfLimitException($"The SVG's images are larger than {_lim.MaxTotalImageBytes} bytes together.");
        }

        bool png = data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;
        bool jpeg = data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8;
        if (!png && !jpeg)
        {
            Note("An image is neither PNG nor JPEG (SVG images, GIF and WebP are not supported); it is left out.");
            return;
        }

        PdfCreatedImage image;
        try
        {
            image = _host.AddImage(e, data, png);
        }
        catch (PdfFormatException ex)
        {
            Note("An image could not be read: " + ex.Message);
            return;
        }

        double x = Num(e, "x", Axis.X, st, vp);
        double y = Num(e, "y", Axis.Y, st, vp);
        double w = Num(e, "width", Axis.X, st, vp);
        double h = Num(e, "height", Axis.Y, st, vp);
        double iw = image.Width;
        double ih = image.Height;
        if (!(w > 0))
        {
            w = h > 0 ? h * iw / ih : iw;
        }

        if (!(h > 0))
        {
            h = w * ih / iw;
        }

        AspectRatio par = AspectRatio.Parse(e.Attr("preserveAspectRatio"));
        Affine fit = par.Fit(0, 0, iw, ih, w, h);
        if (!Open())
        {
            return;
        }

        try
        {
            _b.Rectangle(x, y, w, h);
            _b.Clip();
            _b.EndPath();
            if (st.FillOpacity < 1)
            {
                _b.SetOpacity(st.FillOpacity);
            }

            _b.Translate(x, y);
            _b.Transform(fit.A, fit.B, fit.C, fit.D, Coord.D(fit.E), Coord.D(fit.F));
            // The image's space is y up and the document's is y down, so the picture is drawn upside down in a flipped frame.
            _b.Translate(0, ih);
            _b.Scale(1, -1);
            _b.DrawImage(image, 0, 0, iw, ih);
        }
        finally
        {
            Close();
        }
    }

    // ---- text ----

    private sealed class TextRun
    {
        public string Text = string.Empty;
        public readonly System.Text.StringBuilder Sb = new();
        public required int FrameId;
        public required SvgStyle St;
        public required SvgTextStyle Ts;
        public double? AbsX;
        public double? AbsY;
        public double Dx;
        public double Dy;
        public ParagraphLayout? Layout;
        public double Width;
        public double X;
        public double Y;
    }

    private sealed class ListFrame
    {
        public int Start;
        public List<double>? X;
        public List<double>? Y;
        public List<double>? Dx;
        public List<double>? Dy;
    }

    private sealed class TextFrame
    {
        public required SvgElement El;
        public required SvgStyle St;
        public required SvgTextStyle Ts;
        public int Next;
        public required int Id;
        public bool PushedList;
    }

    private List<double>? LengthList(SvgElement e, string name, Axis axis, in SvgStyle st, Viewport vp)
    {
        string? v = e.Attr(name);
        if (v is null)
        {
            return null;
        }

        var list = new List<double>();
        foreach (Range r in v.AsSpan().SplitAny(" ,\t\r\n"))
        {
            ReadOnlySpan<char> part = v.AsSpan(r);
            if (part.IsEmpty)
            {
                continue;
            }

            if (list.Count >= 100_000)
            {
                break;
            }

            list.Add(Length.TryParse(part, out Length l) ? Resolve(l, axis, st, vp) : 0);
        }

        return list.Count > 0 ? list : null;
    }

    private void DrawText(SvgElement text, in SvgStyle st, Viewport vp)
    {
        if (!st.Visible)
        {
            return;
        }

        var runs = new List<TextRun>();
        CollectText(text, st, vp, runs);
        if (runs.Count == 0)
        {
            return;
        }

        // Shape every run once; its width places the following ones.
        foreach (TextRun r in runs)
        {
            double size = r.St.FontSize;
            if (!(size > 0) || !double.IsFinite(size))
            {
                continue;
            }

            size = Math.Min(size, 10_000);
            LayoutColor color = TextColor(r.St, out _);
            var style = new TextStyle
            {
                Families = FamiliesFor(r.Ts),
                Size = size,
                Color = color,
                Weight = r.Ts.Weight,
                Italic = r.Ts.Italic,
                LetterSpacing = Math.Clamp(r.Ts.LetterSpacing, -1000, 1000),
                Underline = r.Ts.Underline,
                Strike = r.Ts.LineThrough,
            };
            ShapedParagraph shaped = new TextParagraph(r.Text, style).Shape(_host.Fonts);
            r.Layout = shaped.Layout(1e7);
            r.Width = r.Layout.Lines.Count > 0 ? r.Layout.Lines[0].Width : 0;
            if (r.Text.EndsWith(' '))
            {
                // A line's width leaves out trailing spaces, but here the next run continues after them: measure with a letter behind them.
                double withMark = LineWidth(r.Text + "x", style);
                r.Width = Math.Max(r.Width, withMark - LineWidth("x", style));
            }
        }

        // Positions along the baseline, then a shift per chunk for text-anchor.
        double penX = 0;
        double penY = 0;
        int chunkStart = 0;
        for (int i = 0; i <= runs.Count; i++)
        {
            if (i == runs.Count || (i > chunkStart && (runs[i].AbsX is not null || runs[i].AbsY is not null)))
            {
                ShiftChunk(runs, chunkStart, i);
                chunkStart = i;
            }

            if (i == runs.Count)
            {
                break;
            }

            TextRun r = runs[i];
            r.X = (r.AbsX ?? penX) + r.Dx;
            r.Y = (r.AbsY ?? penY) + r.Dy;
            penX = r.X + r.Width;
            penY = r.Y;
        }

        foreach (TextRun r in runs)
        {
            DrawRun(r);
        }
    }

    private double LineWidth(string text, TextStyle style)
    {
        ParagraphLayout l = new TextParagraph(text, style).Shape(_host.Fonts).Layout(1e7);
        return l.Lines.Count > 0 ? l.Lines[0].Width : 0;
    }

    private static void ShiftChunk(List<TextRun> runs, int from, int to)
    {
        if (to <= from)
        {
            return;
        }

        double start = runs[from].X;
        double end = start;
        for (int i = from; i < to; i++)
        {
            end = Math.Max(end, runs[i].X + runs[i].Width);
        }

        double shift = runs[from].Ts.Anchor switch
        {
            TextAnchor.Middle => -(end - start) / 2,
            TextAnchor.End => -(end - start),
            _ => 0,
        };
        if (shift != 0)
        {
            for (int i = from; i < to; i++)
            {
                runs[i].X += shift;
            }
        }
    }

    // The positions of runs are filled in a second pass, so the pen state used by ShiftChunk must already hold the unshifted X and Y.
    private void DrawRun(TextRun r)
    {
        if (r.Layout is null || r.Layout.Lines.Count == 0 || r.Text.Length == 0)
        {
            return;
        }

        Resolved fill = _clipMode ? s_clipWhite : ResolvePaint(r.St.Fill, r.St);
        double alpha;
        if (fill.Kind == ResolvedKind.None)
        {
            Resolved stroke = ResolvePaint(r.St.Stroke, r.St);
            if (stroke.Kind != ResolvedKind.Solid)
            {
                return;
            }

            Note("Stroked text with no fill is drawn filled with the stroke colour.");
        }

        _ = TextColor(r.St, out alpha);
        alpha *= _clipMode ? 1 : r.St.FillOpacity;
        if (alpha <= 0)
        {
            return;
        }

        if (r.Ts.Vertical)
        {
            Note("Vertical writing modes are drawn horizontally.");
        }

        double shift = BaselineShift(r) ;
        if (!Open())
        {
            return;
        }

        try
        {
            if (alpha < 1)
            {
                _b.SetFillOpacity(alpha);
            }

            _b.Translate(Coord.D(r.X), Coord.D(r.Y + shift));
            _b.Scale(1, -1);
            _host.Renderer.Draw(_b, r.Layout, 0, r.Layout.Lines[0].Baseline);
        }
        finally
        {
            Close();
        }
    }

    // How far below the given y the glyphs' baseline goes: dominant-baseline and baseline-shift against typical Liberation metrics
    // (ascent 0.905 em, descent 0.212 em, x-height 0.519 em). The values are the usual approximations, not the font's own tables.
    private double BaselineShift(TextRun r)
    {
        double em = r.St.FontSize;
        BaselineKind kind = r.Ts.Alignment != BaselineKind.Auto ? r.Ts.Alignment : r.Ts.Dominant;
        double down = kind switch
        {
            BaselineKind.Central => 0.3465 * em,
            BaselineKind.Middle => 0.2595 * em,
            BaselineKind.Mathematical => 0.3 * em,
            BaselineKind.Hanging => 0.72 * em,
            BaselineKind.TextBefore => 0.905 * em,
            BaselineKind.TextAfter => -0.212 * em,
            BaselineKind.Ideographic => -0.1 * em,
            _ => 0,
        };
        double rise = r.Ts.Shift switch
        {
            ShiftKind.Sub => -0.2 * em,
            ShiftKind.Super => 0.4 * em,
            ShiftKind.Length => r.Ts.ShiftLength.Unit == LengthUnit.Percent ? r.Ts.ShiftLength.Value / 100 * em : Resolve(r.Ts.ShiftLength, Axis.Other, r.St, new Viewport(em, em)),
            _ => 0,
        };
        return down - rise;
    }

    private LayoutColor TextColor(in SvgStyle st, out double alpha)
    {
        if (_clipMode)
        {
            alpha = 1;
            return LayoutColor.White;
        }

        Resolved fill = ResolvePaint(st.Fill, st);
        Rgba32 c;
        if (fill.Kind == ResolvedKind.Solid)
        {
            c = fill.Color;
        }
        else if (fill.Kind == ResolvedKind.Server && TryRepresentativeColor(fill.Server!, st, new Viewport(100, 100), out c))
        {
            Note("Gradients and patterns on text are drawn as one solid colour.");
        }
        else if (ResolvePaint(st.Stroke, st) is { Kind: ResolvedKind.Solid } stroke)
        {
            c = stroke.Color;
        }
        else
        {
            c = new Rgba32(0, 0, 0, 0);
        }

        alpha = c.A / 255.0;
        return LayoutColor.FromBytes(c.R, c.G, c.B);
    }

    private List<string> FamiliesFor(SvgTextStyle ts)
    {
        var list = new List<string>();
        foreach (string name in ts.Families)
        {
            string n = name.Trim();
            if (n.Length == 0)
            {
                continue;
            }

            if (_host.Fonts.HasFamily(n))
            {
                list.Add(n);
                continue;
            }

            string? generic = GenericFamily(n);
            if (generic is not null && !list.Contains(generic))
            {
                list.Add(generic);
            }
        }

        if (list.Count == 0)
        {
            list.Add("Serif");
        }

        return list;
    }

    // Web fonts that the metric-compatible Liberation faces stand in for, and the CSS generic families.
    private static string? GenericFamily(string name) => name.ToLowerInvariant() switch
    {
        "sans-serif" or "arial" or "helvetica" or "helvetica neue" or "verdana" or "tahoma" or "trebuchet ms" or "calibri" or "segoe ui" or "system-ui"
            or "ui-sans-serif" or "-apple-system" or "blinkmacsystemfont" or "roboto" or "open sans" or "lucida grande" or "dejavu sans" => "Sans",
        "serif" or "times" or "times new roman" or "georgia" or "garamond" or "palatino" or "cambria" or "ui-serif" or "cursive" or "fantasy" or "dejavu serif" => "Serif",
        "monospace" or "courier" or "courier new" or "consolas" or "monaco" or "menlo" or "ui-monospace" or "lucida console" or "dejavu sans mono" => "Mono",
        _ => null,
    };

    // The text of a <text> element as styled runs, in document order. Walks with an explicit stack; x, y, dx and dy lists are consumed per character.
    private void CollectText(SvgElement text, in SvgStyle st, Viewport vp, List<TextRun> runs)
    {
        SvgTextStyle rootTs = SvgTextStyle.Compute(text, SvgTextStyle.Initial, st);
        var stack = new Stack<TextFrame>();
        var lists = new List<ListFrame>();
        int nextId = 0;
        int chars = 0;
        bool prevSpace = true;
        TextRun? cur = null;

        void Push(SvgElement el, SvgStyle s, SvgTextStyle t)
        {
            var f = new TextFrame { El = el, St = s, Ts = t, Id = nextId++ };
            var lf = new ListFrame { Start = chars, X = LengthList(el, "x", Axis.X, s, vp), Y = LengthList(el, "y", Axis.Y, s, vp), Dx = LengthList(el, "dx", Axis.X, s, vp), Dy = LengthList(el, "dy", Axis.Y, s, vp) };
            if (lf.X is not null || lf.Y is not null || lf.Dx is not null || lf.Dy is not null)
            {
                lists.Add(lf);
                f.PushedList = true;
            }

            if (el.Attr("rotate") is not null || el.Attr("textLength") is not null)
            {
                Note("Per-glyph rotate and textLength on text are not supported; the text is drawn without them.");
            }

            stack.Push(f);
        }

        Push(text, st, rootTs);
        while (stack.Count > 0)
        {
            TextFrame f = stack.Peek();
            List<TextNode>? content = f.El.TextContent;
            if (content is null || f.Next >= content.Count)
            {
                if (f.PushedList)
                {
                    lists.RemoveAt(lists.Count - 1);
                }

                stack.Pop();
                continue;
            }

            TextNode node = content[f.Next++];
            if (node.Element is { } child)
            {
                if (child.Name is "tspan" or "a")
                {
                    if (!Conditions(child))
                    {
                        continue;
                    }

                    SvgStyle cs = SvgStyle.Compute(child, f.St, _rootFont);
                    if (!cs.Displayed)
                    {
                        continue;
                    }

                    Push(child, cs, SvgTextStyle.Compute(child, f.Ts, cs));
                }
                else if (child.Name is "textPath" or "tref")
                {
                    Note("textPath and tref are not supported; their text is left out.");
                }

                continue;
            }

            if (node.Text is not { } raw)
            {
                continue;
            }

            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '\r')
                {
                    continue;
                }

                string piece;
                if (char.IsHighSurrogate(c) && i + 1 < raw.Length && char.IsLowSurrogate(raw[i + 1]))
                {
                    piece = raw.Substring(i, 2);
                    i++;
                    prevSpace = false;
                }
                else if (f.Ts.PreserveSpace)
                {
                    piece = c is '\n' or '\t' ? " " : c.ToString();
                    prevSpace = false;
                }
                else if (c is ' ' or '\t' or '\n')
                {
                    // XML white space is collapsed to one space and dropped at the start (SVG 1.1 10.15).
                    if (prevSpace)
                    {
                        continue;
                    }

                    piece = " ";
                    prevSpace = true;
                }
                else
                {
                    piece = c.ToString();
                    prevSpace = false;
                }

                if (++chars > _lim.MaxTextCharacters || ++_textTotal > _lim.MaxTextCharacters)
                {
                    throw new PdfLimitException($"The SVG has more than {_lim.MaxTextCharacters} text characters.");
                }

                int index = chars - 1;
                double? ax = null;
                double? ay = null;
                double dx = 0;
                double dy = 0;
                for (int k = lists.Count - 1; k >= 0; k--)
                {
                    ListFrame lf = lists[k];
                    int at = index - lf.Start;
                    if (at < 0)
                    {
                        continue;
                    }

                    if (ax is null && lf.X is { } xs && at < xs.Count)
                    {
                        ax = xs[at];
                    }

                    if (ay is null && lf.Y is { } ys && at < ys.Count)
                    {
                        ay = ys[at];
                    }

                    if (dx == 0 && lf.Dx is { } dxs && at < dxs.Count)
                    {
                        dx = dxs[at];
                    }

                    if (dy == 0 && lf.Dy is { } dys && at < dys.Count)
                    {
                        dy = dys[at];
                    }
                }

                bool positioned = ax is not null || ay is not null || dx != 0 || dy != 0;
                if (cur is null || positioned || cur.FrameId != f.Id)
                {
                    cur = new TextRun { FrameId = f.Id, St = f.St, Ts = f.Ts, AbsX = ax, AbsY = ay, Dx = dx, Dy = dy };
                    runs.Add(cur);
                }

                cur.Sb.Append(piece);
            }
        }

        foreach (TextRun r in runs)
        {
            r.Text = r.Sb.ToString();
        }

        if (!rootTs.PreserveSpace && runs.Count > 0 && runs[^1].Text.EndsWith(' '))
        {
            TextRun last = runs[^1];
            last.Text = last.Text[..^1];
            if (last.Text.Length == 0)
            {
                runs.RemoveAt(runs.Count - 1);
            }
        }
    }
}
