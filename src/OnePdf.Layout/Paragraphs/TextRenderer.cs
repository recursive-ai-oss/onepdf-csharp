using System.Globalization;
using OneFont;

namespace OnePdf.Layout;

/// <summary>
/// Draws laid-out paragraphs into content builders of one document, and owns that document's font objects. Create one per document (or
/// per streaming writer); fonts used by many paragraphs are added once and shared. Not thread-safe.
/// </summary>
public sealed class TextRenderer
{
    private readonly Func<PdfFontSource, PdfEmbedFontOptions?, PdfCreatedFont> _addFont;
    private readonly Func<string, PdfCreatedFont> _addStandard;
    private readonly Dictionary<FontEntry, PdfCreatedFont> _embedded = [];

    /// <summary>Creates a renderer that adds its fonts to <paramref name="document"/>.</summary>
    public TextRenderer(PdfDocument document)
        : this(Check(document).AddFont, document.AddStandardFont)
    {
    }

    /// <summary>Creates a renderer that adds its fonts to a streaming writer.</summary>
    public TextRenderer(PdfDocumentWriter writer)
        : this(Check(writer).AddFont, writer.AddStandardFont)
    {
    }

    internal TextRenderer(Func<PdfFontSource, PdfEmbedFontOptions?, PdfCreatedFont> addFont, Func<string, PdfCreatedFont> addStandard)
    {
        _addFont = addFont;
        _addStandard = addStandard;
    }

    private static T Check<T>(T value)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        return value;
    }

    /// <summary>
    /// Draws lines of a layout. Each styled stretch of a line becomes one shaped glyph run (<see cref="PdfContentBuilder.DrawGlyphRun"/>) in
    /// display order, so right-to-left text is drawn as it looks and still extracts in logical order; a soft or hyphenation hyphen is drawn
    /// as the font's hyphen and mapped to U+00AD, as tagged PDF asks for (ISO 32000-2 14.8.2.2.3). Underlines and strike-throughs are
    /// filled rectangles in the text colour. The builder's graphics state is restored afterwards.
    /// </summary>
    /// <param name="builder">The target. It must belong to the document or writer this renderer was made for.</param>
    /// <param name="layout">The layout.</param>
    /// <param name="x">Left edge of the paragraph box in the builder's coordinates.</param>
    /// <param name="top">Top edge of the first drawn line in the builder's coordinates (y up, so lines go downward from here).</param>
    /// <param name="firstLine">The first line to draw. Default 0.</param>
    /// <param name="count">How many lines, or -1 for all from <paramref name="firstLine"/>.</param>
    /// <returns>The link areas of spans that have a <see cref="TextStyle.Link"/>.</returns>
    public IReadOnlyList<TextLinkArea> Draw(PdfContentBuilder builder, ParagraphLayout layout, double x, double top, int firstLine = 0, int count = -1) =>
        Draw(builder, layout, x, top, firstLine, count, null);

    internal IReadOnlyList<TextLinkArea> Draw(PdfContentBuilder builder, ParagraphLayout layout, double x, double top, int firstLine, int count, IInlineHost? host)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(layout);
        int lines = layout.Lines.Count;
        if (count < 0)
        {
            count = lines - firstLine;
        }

        if (firstLine < 0 || firstLine > lines || count < 0 || firstLine + count > lines)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "The line range is outside the layout.");
        }

        List<TextLinkArea>? links = null;
        if (count == 0)
        {
            return Array.Empty<TextLinkArea>();
        }

        double origin = layout.Lines[firstLine].Top;
        if (layout.Paragraph.HasInline)
        {
            // With inline objects the graphics state is saved around each stretch of text instead of around the whole paragraph, so that
            // an object (which may open marked content of its own) is never drawn inside a state saved before its marked content began.
            var state = new DrawState(host);
            try
            {
                for (int l = firstLine; l < firstLine + count; l++)
                {
                    ParagraphLine line = layout.Lines[l];
                    DrawLine(builder, layout.Paragraph, line, x + line.X, top - (line.Top - origin), ref links, state);
                }
            }
            finally
            {
                CloseText(builder, state);
            }

            return links ?? (IReadOnlyList<TextLinkArea>)Array.Empty<TextLinkArea>();
        }

        builder.Save();
        try
        {
            for (int l = firstLine; l < firstLine + count; l++)
            {
                ParagraphLine line = layout.Lines[l];
                DrawLine(builder, layout.Paragraph, line, x + line.X, top - (line.Top - origin), ref links, null);
            }
        }
        finally
        {
            builder.Restore();
        }

        return links ?? (IReadOnlyList<TextLinkArea>)Array.Empty<TextLinkArea>();
    }

    private PdfCreatedFont FontFor(FontEntry entry)
    {
        if (entry.StandardName is { } std)
        {
            return _addStandard(std);
        }

        if (!_embedded.TryGetValue(entry, out PdfCreatedFont? font))
        {
            font = _addFont(entry.Source, new PdfEmbedFontOptions());
            _embedded[entry] = font;
        }

        return font;
    }

    // One stretch of a line in one format. Its glyphs are Glyphs[GlyphStart .. GlyphEnd) of the renderer's buffer and its text is
    // para.Text[TextStart .. TextStart + TextLength); the buffers are reused from line to line, so drawing allocates nothing per line.
    private struct Piece
    {
        public int Fmt;
        public int GlyphStart;
        public int GlyphEnd;
        public int TextStart;
        public int TextLength;
        public double Width;
        public bool IsHyphen;

        /// <summary>The text of a closing hyphen (which is not part of the paragraph text); null for other pieces.</summary>
        public string? Literal;
        public int FirstItem;
        public int LastItem;

        /// <summary>True for an inline object; <see cref="FirstItem"/> is its item.</summary>
        public bool IsInline;
    }

    // What one Draw call with inline objects keeps between lines: the host and whether a stretch of text (graphics state and tagged
    // content) is open.
    private sealed class DrawState(IInlineHost? host)
    {
        public IInlineHost? Host { get; } = host;

        public bool TextOpen { get; set; }

        public bool Began { get; set; }
    }

    private static void OpenText(PdfContentBuilder b, DrawState st)
    {
        st.Began = st.Host?.BeginContent() ?? false;
        b.Save();
        st.TextOpen = true;
    }

    private static void CloseText(PdfContentBuilder b, DrawState st)
    {
        if (st.TextOpen)
        {
            st.TextOpen = false;
            b.Restore();
            st.Host?.EndContent(st.Began);
        }
    }

    private PdfShapedGlyph[] _glyphs = new PdfShapedGlyph[128];
    private int _glyphCount;
    private Piece[] _pieces = new Piece[8];
    private int _pieceCount;

    private void AddGlyph(in PdfShapedGlyph g)
    {
        if (_glyphCount == _glyphs.Length)
        {
            Array.Resize(ref _glyphs, _glyphs.Length * 2);
        }

        _glyphs[_glyphCount++] = g;
    }

    private int AddPiece(in Piece p)
    {
        if (_pieceCount == _pieces.Length)
        {
            Array.Resize(ref _pieces, _pieces.Length * 2);
        }

        _pieces[_pieceCount] = p;
        return _pieceCount++;
    }

    private void DrawLine(PdfContentBuilder b, ShapedParagraph para, ParagraphLine line, double x0, double lineTop, ref List<TextLinkArea>? links, DrawState? st)
    {
        double baseline = lineTop - line.Baseline;
        int count = line.ItemEnd - line.ItemStart;
        bool hyphen = line.EndsWithHyphen;
        int total = count + (hyphen ? 1 : 0);
        if (total == 0)
        {
            return;
        }

        // Display order of the line's items; a closing hyphen sits at the end of the line in the paragraph's direction. A line whose items
        // are all at level 0 is already in display order.
        int[]? order = null;
        bool allLevelZero = !hyphen;
        for (int k = 0; allLevelZero && k < count; k++)
        {
            allLevelZero = para.ItemAt(line.ItemStart + k).Level == 0;
        }

        if (!allLevelZero)
        {
            var levels = new byte[total];
            for (int k = 0; k < count; k++)
            {
                levels[k] = para.ItemAt(line.ItemStart + k).Level;
            }

            if (hyphen)
            {
                levels[count] = line.Segment.ParaLevel;
            }

            order = TextShaping.GetVisualOrder(levels);
        }

        _glyphCount = 0;
        _pieceCount = 0;
        int cur = -1;
        int prevLogical = -2;
        for (int oi = 0; oi < total; oi++)
        {
            int logical = order is null ? oi : order[oi];
            if (logical == count)
            {
                Hyph h = para.HyphenFor(line.ItemEnd - 1);
                Fmt hf = para.FmtAt(h.Fmt);
                int gs = _glyphCount;
                AddGlyph(new PdfShapedGlyph((uint)h.Gid < (uint)hf.Face.GlyphCount ? h.Gid : 0, 0, Thousandths(h.Advance, hf.Size)));
                AddPiece(new Piece { Fmt = h.Fmt, GlyphStart = gs, GlyphEnd = _glyphCount, TextLength = 0, Literal = h.Text, Width = h.Advance, IsHyphen = true, FirstItem = -1, LastItem = -1 });
                cur = -1;
                prevLogical = -2;
                continue;
            }

            int idx = line.ItemStart + logical;
            Item it = para.ItemAt(idx);
            if (it.Inline != 0)
            {
                // The gap letter-spaced justification leaves after an item goes after the object, in display order.
                double iw = it.Advance + ((idx != line.ItemEnd - 1 || hyphen) ? line.LetterExtra : 0);
                AddPiece(new Piece { Fmt = it.Fmt, GlyphStart = _glyphCount, GlyphEnd = _glyphCount, FirstItem = idx, LastItem = idx, Width = iw, IsInline = true });
                cur = -1;
                prevLogical = -2;
                continue;
            }

            Fmt f = para.FmtAt(it.Fmt);
            bool extend = cur >= 0 && _pieces[cur].Fmt == it.Fmt
                && para.ItemAt(_pieces[cur].LastItem).Level == it.Level && Math.Abs(logical - prevLogical) == 1;
            if (!extend)
            {
                cur = AddPiece(new Piece { Fmt = it.Fmt, GlyphStart = _glyphCount, FirstItem = idx, LastItem = idx });
            }

            _pieces[cur].LastItem = idx;
            prevLogical = logical;
            double w = AppendItem(para, line, it, f, (idx != line.ItemEnd - 1 || hyphen) ? 1 : 0);
            _pieces[cur].Width += w;
            _pieces[cur].GlyphEnd = _glyphCount;
        }

        // Logical text of each shaped piece, with cluster indexes relative to it.
        for (int pi = 0; pi < _pieceCount; pi++)
        {
            ref Piece p = ref _pieces[pi];
            if (p.IsHyphen || p.IsInline)
            {
                continue;
            }

            int lo = Math.Min(p.FirstItem, p.LastItem);
            int hi = Math.Max(p.FirstItem, p.LastItem);
            int textStart = para.ItemAt(lo).Start;
            Item last = para.ItemAt(hi);
            p.TextStart = textStart;
            p.TextLength = last.Start + last.Length - textStart;
            for (int g = p.GlyphStart; g < p.GlyphEnd; g++)
            {
                _glyphs[g] = _glyphs[g] with { Cluster = _glyphs[g].Cluster - textStart };
            }
        }

        double x = x0;
        double lineBottom = baseline - (line.Height - line.Baseline);
        TextStyle? lastColorStyle = null;
        for (int pi = 0; pi < _pieceCount; pi++)
        {
            Piece p = _pieces[pi];
            Fmt f = para.FmtAt(p.Fmt);
            if (p.IsInline)
            {
                CloseText(b, st!);
                DrawInline(b, para, line, p, f, x, baseline, st!.Host, ref links);
                x += p.Width;
                continue;
            }

            if (st is not null && !st.TextOpen)
            {
                OpenText(b, st);
                lastColorStyle = null;
            }

            PdfCreatedFont font = FontFor(f.Entry);
            LayoutColor color = f.Style.Color;
            if (lastColorStyle is null || lastColorStyle.Color != color)
            {
                b.SetFillRgb(color.R, color.G, color.B);
                lastColorStyle = f.Style;
            }

            b.SetFont(font, f.Size);
            var glyphs = new ReadOnlySpan<PdfShapedGlyph>(_glyphs, p.GlyphStart, p.GlyphEnd - p.GlyphStart);
            if (f.IsStandard)
            {
                DrawStandard(b, para, p, glyphs, f, x, baseline);
            }
            else
            {
                ReadOnlySpan<char> runText = p.Literal is { } literal ? literal.AsSpan() : para.Text.AsSpan(p.TextStart, p.TextLength);
                ColorGlyphs? colour = ColourOf(f, glyphs);
                if (colour is null)
                {
                    b.DrawGlyphRun(x, baseline, glyphs, runText);
                }
                else
                {
                    DrawColourRun(b, colour, f, glyphs, runText, x, baseline);
                }
            }

            if (f.Style.Underline || f.Style.Strike)
            {
                FontMetrics m = f.Face.Metrics;
                double sc = f.Size / f.Face.UnitsPerEm;
                if (f.Style.Underline)
                {
                    double th = Math.Max(m.UnderlineThickness * sc, 0.25);
                    b.Rectangle(x, baseline + (m.UnderlinePosition * sc) - th, p.Width, th);
                    b.Fill();
                }

                if (f.Style.Strike)
                {
                    double th = Math.Max(m.StrikeoutSize * sc, 0.25);
                    b.Rectangle(x, baseline + (m.StrikeoutPosition * sc) - th, p.Width, th);
                    b.Fill();
                }
            }

            if (f.Style.Link is { } target)
            {
                links ??= [];
                int n = links.Count;
                if (n > 0 && links[n - 1].Target == target && Math.Abs(links[n - 1].X + links[n - 1].Width - x) < 1e-6 && Math.Abs(links[n - 1].Y - lineBottom) < 1e-6)
                {
                    TextLinkArea prev = links[n - 1];
                    links[n - 1] = prev with { Width = prev.Width + p.Width };
                }
                else
                {
                    links.Add(new TextLinkArea(target, x, lineBottom, p.Width, line.Height));
                }
            }

            x += p.Width;
        }
    }

    // Draws one inline object whose left edge is at x. The text before it has been closed. Wording of the order: the host opens the
    // structure the object needs (a link, a figure, an artifact), the alternate text goes in as an invisible glyph inside a span that
    // replaces whatever text the object contains with it, then the object draws.
    private void DrawInline(PdfContentBuilder b, ShapedParagraph para, ParagraphLine line, in Piece p, Fmt f, double x, double baseline, IInlineHost? host, ref List<TextLinkArea>? links)
    {
        InlineObject o = para.InlineOf(para.ItemAt(p.FirstItem));
        double w = o.ResolvedWidth;
        double h = o.ResolvedHeight;
        double bottom = baseline + ShapedParagraph.InlineBottom(o, f, line.Baseline, line.Height - line.Baseline);
        object? token = host?.Begin(o, b, x, bottom);
        bool alt = o.AltText is { Length: > 0 } && !o.IsDecoration;
        try
        {
            if (alt)
            {
                b.BeginSpan(o.AltText);
                DrawAltGlyph(b, f, x, baseline, host);
            }

            if (o is InlineBox box)
            {
                bool began = !o.IsDecoration && (host?.BeginContent() ?? false);
                box.Draw(b, x, bottom);
                host?.EndContent(began);
            }
            else if (o is ResolvedInline resolved && host is not null)
            {
                var savedGlyphs = _glyphs;
                var savedPieces = _pieces;
                int savedGlyphCount = _glyphCount;
                int savedPieceCount = _pieceCount;
                (_glyphs, _pieces) = _spare.Count > 0 ? _spare.Pop() : (new PdfShapedGlyph[128], new Piece[8]);
                try
                {
                    host.DrawElement(resolved, x, bottom);
                }
                finally
                {
                    _spare.Push((_glyphs, _pieces));
                    (_glyphs, _pieces, _glyphCount, _pieceCount) = (savedGlyphs, savedPieces, savedGlyphCount, savedPieceCount);
                }
            }

            if (alt)
            {
                b.EndMarkedContent();
            }
        }
        finally
        {
            host?.End(token, o, b, x, bottom);
        }

        if (o.Link is { } target)
        {
            (links ??= []).Add(new TextLinkArea(target, x, bottom, w, h));
        }
    }

    // Pairs of buffers for text drawn while an outer line is still being drawn (the text inside an inline element).
    private readonly Stack<(PdfShapedGlyph[], Piece[])> _spare = new();

    // The alternate text of an object, as an invisible space with the text the object stands for: extraction reads the text, nothing shows.
    private void DrawAltGlyph(PdfContentBuilder b, Fmt f, double x, double baseline, IInlineHost? host)
    {
        bool began = host?.BeginContent() ?? false;
        PdfCreatedFont font = FontFor(f.Entry);
        b.SetFont(font, f.Size);
        b.SetTextRenderMode(PdfTextRenderMode.Invisible);
        try
        {
            if (f.IsStandard)
            {
                b.DrawText(" ", x, baseline);
            }
            else
            {
                b.DrawGlyphRun(x, baseline, [new PdfShapedGlyph(f.Face.GetGlyphIndex(' '), 0, 0)], " ");
            }
        }
        finally
        {
            b.SetTextRenderMode(PdfTextRenderMode.Fill);
            host?.EndContent(began);
        }
    }

    // Adds the item's glyphs in display order. The gap after the item (letter spacing, justification) goes onto its last displayed glyph.
    private double AppendItem(ShapedParagraph para, ParagraphLine line, Item it, Fmt f, int notLast)
    {
        double extra = it.Spacing;
        if (it.Stretch)
        {
            extra += line.SpaceExtra;
        }

        if (notLast == 1)
        {
            extra += line.LetterExtra;
        }

        double width = 0;
        bool rtl = (it.Level & 1) == 1;
        int n = it.GlyphCount;
        double size = f.Size;
        if (f.IsStandard)
        {
            double a = para.GlyphAt(it.GlyphStart).Advance + extra;
            AddGlyph(new PdfShapedGlyph(0, it.Start, Thousandths(a, size)));
            return a;
        }

        int glyphCount = f.Face.GlyphCount;
        for (int k = 0; k < n; k++)
        {
            int gi = it.GlyphStart + (rtl ? n - 1 - k : k);
            GlyphRec g = para.GlyphAt(gi);
            double a = g.Advance;
            if (k == n - 1)
            {
                a += extra;
            }

            width += a;
            // A character map can name a glyph the font does not have; it draws as the missing-glyph box instead of failing the document.
            int gid = (uint)g.Gid < (uint)glyphCount ? g.Gid : 0;
            AddGlyph(new PdfShapedGlyph(gid, it.Start, Thousandths(a, size), Thousandths(g.Dx, size), Thousandths(g.Dy, size)));
        }

        return width;
    }

    /// <summary>The palette colour glyphs use when a style does not choose; set from the document's options.</summary>
    internal ColorPalette DefaultPalette { get; set; }

    /// <summary>Where notes about colour fonts go (once per font and kind); null when drawing outside a layout.</summary>
    internal LayoutContext? Context { get; set; }

    // The colour tables of the run's font when the run has at least one glyph to draw in colour; null for the usual case, which draws as before.
    // A font whose tables are unusable, and glyphs that only have a version 1 description, draw as plain outlines with one note per font.
    private ColorGlyphs? ColourOf(Fmt f, ReadOnlySpan<PdfShapedGlyph> glyphs)
    {
        ColorGlyphs? c = f.Entry.ColorGlyphs;
        if (c is null)
        {
            return null;
        }

        if (!c.IsUsable)
        {
            Context?.NoteOnce(f.Entry, $"The colour tables of font '{f.Entry.Family}' are not valid ({c.Problem}); its glyphs are drawn in one colour.");
            return null;
        }

        bool any = false;
        foreach (PdfShapedGlyph g in glyphs)
        {
            if (c.TryGetLayers(g.GlyphId, out _, out _))
            {
                any = true;
            }
            else if (c.IsVersion1Only(g.GlyphId))
            {
                Context?.NoteOnce((f.Entry, 1), $"Font '{f.Entry.Family}' describes some glyphs only as COLR version 1 paint graphs, which are not drawn; they appear in one colour.");
            }
        }

        return any ? c : null;
    }

    // Draws a run in which some glyphs have colour layers. Plain stretches go out as ordinary glyph runs; a colour glyph is its layers, each
    // a one-glyph run in the layer's palette colour, inside one /Span with /ActualText of the glyph's text (ISO 32000-2 14.9.4), so
    // extraction returns the character once and not once per layer. The layers share the cluster's text for their /ToUnicode entries.
    private void DrawColourRun(PdfContentBuilder b, ColorGlyphs c, Fmt f, ReadOnlySpan<PdfShapedGlyph> glyphs, ReadOnlySpan<char> text, double x, double baseline)
    {
        double size = f.Size;
        int palette = c.ResolvePalette(f.Style.Palette ?? DefaultPalette);
        LayoutColor foreground = f.Style.Color;
        double pen = x;
        int n = glyphs.Length;

        // A cluster's text runs from its value to the next higher cluster value in the run, or to the end of the text. Looking that up in
        // the whole run (not in a piece of it) keeps the text of a glyph from swallowing a neighbour that is drawn separately.
        int[] sorted = new int[n];
        for (int k = 0; k < n; k++)
        {
            sorted[k] = glyphs[k].Cluster;
        }

        Array.Sort(sorted);
        int textLength = text.Length;
        int EndOf(int cluster)
        {
            int at = Array.BinarySearch(sorted, cluster);
            while (at >= 0 && at < n && sorted[at] == cluster)
            {
                at++;
            }

            return at < n && at >= 0 ? sorted[at] : textLength;
        }

        PdfShapedGlyph[] one = new PdfShapedGlyph[1];
        int plain = 0;
        int i = 0;
        while (i < n)
        {
            int cluster = glyphs[i].Cluster;
            int j = i + 1;
            while (j < n && glyphs[j].Cluster == cluster)
            {
                j++;
            }

            bool coloured = false;
            for (int k = i; k < j && !coloured; k++)
            {
                coloured = c.TryGetLayers(glyphs[k].GlyphId, out _, out _);
            }

            if (!coloured)
            {
                i = j;
                continue;
            }

            pen += DrawPlain(b, glyphs[plain..i], text, pen, baseline, EndOf);
            ReadOnlySpan<char> groupText = text[cluster..EndOf(cluster)];
            b.BeginSpan(actualText: groupText.ToString());
            double fillR = foreground.R;
            double fillG = foreground.G;
            double fillB = foreground.B;
            for (int k = i; k < j; k++)
            {
                PdfShapedGlyph g = glyphs[k];
                double gx = pen + (g.OffsetX * size / 1000.0);
                double gy = baseline + (g.OffsetY * size / 1000.0);
                if (c.TryGetLayers(g.GlyphId, out int first, out int count))
                {
                    for (int l = 0; l < count; l++)
                    {
                        c.GetLayer(first + l, out int layerGlyph, out int entry);
                        (double r, double gr, double bl) = ColorGlyphs.IsForeground(entry) ? (foreground.R, foreground.G, foreground.B) : c.GetColor(palette, entry);
                        if (r != fillR || gr != fillG || bl != fillB)
                        {
                            b.SetFillRgb(r, gr, bl);
                            (fillR, fillG, fillB) = (r, gr, bl);
                        }

                        one[0] = new PdfShapedGlyph(layerGlyph, 0, 0);
                        b.DrawGlyphRun(gx, gy, one, groupText);
                    }
                }
                else
                {
                    if (fillR != foreground.R || fillG != foreground.G || fillB != foreground.B)
                    {
                        b.SetFillRgb(foreground.R, foreground.G, foreground.B);
                        (fillR, fillG, fillB) = (foreground.R, foreground.G, foreground.B);
                    }

                    one[0] = new PdfShapedGlyph(g.GlyphId, 0, 0);
                    b.DrawGlyphRun(gx, gy, one, groupText);
                }

                pen += g.Advance * size / 1000.0;
            }

            b.EndMarkedContent();
            if (fillR != foreground.R || fillG != foreground.G || fillB != foreground.B)
            {
                b.SetFillRgb(foreground.R, foreground.G, foreground.B);
            }

            plain = j;
            i = j;
        }

        DrawPlain(b, glyphs[plain..n], text, pen, baseline, EndOf);
    }

    // The stretch between colour glyphs. One call when its clusters only go up (the usual left-to-right case), with the text cut after the
    // last cluster so that the last glyph does not claim the rest of the run; otherwise one call per cluster, each with its own text.
    private static double DrawPlain(PdfContentBuilder b, ReadOnlySpan<PdfShapedGlyph> glyphs, ReadOnlySpan<char> text, double pen, double baseline, Func<int, int> endOf)
    {
        if (glyphs.Length == 0)
        {
            return 0;
        }

        bool ascending = true;
        for (int k = 1; k < glyphs.Length && ascending; k++)
        {
            ascending = glyphs[k].Cluster >= glyphs[k - 1].Cluster;
        }

        if (ascending)
        {
            return b.DrawGlyphRun(pen, baseline, glyphs, text[..endOf(glyphs[^1].Cluster)]);
        }

        double moved = 0;
        var one = new PdfShapedGlyph[1];
        int i = 0;
        while (i < glyphs.Length)
        {
            int cluster = glyphs[i].Cluster;
            int j = i + 1;
            while (j < glyphs.Length && glyphs[j].Cluster == cluster)
            {
                j++;
            }

            ReadOnlySpan<char> part = text[cluster..endOf(cluster)];
            if (j - i == 1)
            {
                one[0] = glyphs[i] with { Cluster = 0 };
                moved += b.DrawGlyphRun(pen + moved, baseline, one, part);
            }
            else
            {
                var group = new PdfShapedGlyph[j - i];
                for (int k = i; k < j; k++)
                {
                    group[k - i] = glyphs[k] with { Cluster = 0 };
                }

                moved += b.DrawGlyphRun(pen + moved, baseline, group, part);
            }

            i = j;
        }

        return moved;
    }


    private static double Thousandths(double points, double size) => points / size * 1000.0;

    // Standard fonts are drawn word by word with the standard font's own text path; positions come from the layout.
    private static void DrawStandard(PdfContentBuilder b, ShapedParagraph para, in Piece p, ReadOnlySpan<PdfShapedGlyph> glyphs, Fmt f, double x, double baseline)
    {
        if (p.IsHyphen)
        {
            b.DrawText("-", x, baseline);
            return;
        }

        b.SetCharacterSpacing(f.Style.LetterSpacing);
        int dir = p.LastItem >= p.FirstItem ? 1 : -1;
        int n = Math.Abs(p.LastItem - p.FirstItem) + 1;
        double pen = x;
        double wordX = x;
        var word = new System.Text.StringBuilder();
        for (int k = 0; k < n; k++)
        {
            Item it = para.ItemAt(p.FirstItem + (dir * k));
            if (it.TrimSpace || it.Stretch)
            {
                if (word.Length > 0)
                {
                    b.DrawText(word.ToString(), wordX, baseline);
                    word.Clear();
                }
            }
            else
            {
                if (word.Length == 0)
                {
                    wordX = pen;
                }

                word.Append(para.Text, it.Start, it.Length);
            }

            pen += glyphs[k].Advance * f.Size / 1000.0;
        }

        if (word.Length > 0)
        {
            b.DrawText(word.ToString(), wordX, baseline);
        }

        b.SetCharacterSpacing(0);
    }
}


/// <summary>What <see cref="TextRenderer"/> asks of the layout that owns the page when a paragraph has inline objects: tagged content for text, structure around objects, and drawing of element subtrees.</summary>
internal interface IInlineHost
{
    /// <summary>Begins tagged content for the paragraph's text; returns whether it did, to pass to <see cref="EndContent"/>.</summary>
    bool BeginContent();

    void EndContent(bool began);

    /// <summary>Opens the structure an object needs before it draws (link, figure, artifact); the result goes to <see cref="End"/>.</summary>
    object? Begin(InlineObject item, PdfContentBuilder b, double x, double y);

    /// <summary>Draws an element subtree with its lower-left corner at (x, y) in the builder's coordinates.</summary>
    void DrawElement(ResolvedInline item, double x, double y);

    /// <summary>Closes what <see cref="Begin"/> opened, and adds the link annotation of the object.</summary>
    void End(object? token, InlineObject item, PdfContentBuilder b, double x, double y);
}
