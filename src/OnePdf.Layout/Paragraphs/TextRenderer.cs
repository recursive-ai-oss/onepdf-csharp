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
    public IReadOnlyList<TextLinkArea> Draw(PdfContentBuilder builder, ParagraphLayout layout, double x, double top, int firstLine = 0, int count = -1)
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
        builder.Save();
        try
        {
            for (int l = firstLine; l < firstLine + count; l++)
            {
                ParagraphLine line = layout.Lines[l];
                DrawLine(builder, layout.Paragraph, line, x + line.X, top - (line.Top - origin), ref links);
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

    private void DrawLine(PdfContentBuilder b, ShapedParagraph para, ParagraphLine line, double x0, double lineTop, ref List<TextLinkArea>? links)
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
                AddGlyph(new PdfShapedGlyph(h.Gid, 0, Thousandths(h.Advance, hf.Size)));
                AddPiece(new Piece { Fmt = h.Fmt, GlyphStart = gs, GlyphEnd = _glyphCount, TextLength = 0, Literal = h.Text, Width = h.Advance, IsHyphen = true, FirstItem = -1, LastItem = -1 });
                cur = -1;
                prevLogical = -2;
                continue;
            }

            int idx = line.ItemStart + logical;
            Item it = para.ItemAt(idx);
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
            if (p.IsHyphen)
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
                b.DrawGlyphRun(x, baseline, glyphs, p.Literal is { } literal ? literal.AsSpan() : para.Text.AsSpan(p.TextStart, p.TextLength));
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
            AddGlyph(new PdfShapedGlyph(g.Gid, it.Start, Thousandths(a, size), Thousandths(g.Dx, size), Thousandths(g.Dy, size)));
        }

        return width;
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
