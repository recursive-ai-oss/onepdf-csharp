using System.Globalization;
using OneFont;

namespace OnePdf.Layout;

/// <summary>
/// Draws laid-out paragraphs into content builders of one document, and owns that document's font objects. Create one per document (or
/// per streaming writer); fonts used by many paragraphs are added once and shared. Not thread-safe.
/// </summary>
public sealed class TextRenderer
{
    private readonly Func<ReadOnlyMemory<byte>, PdfEmbedFontOptions?, PdfCreatedFont> _addFont;
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

    internal TextRenderer(Func<ReadOnlyMemory<byte>, PdfEmbedFontOptions?, PdfCreatedFont> addFont, Func<string, PdfCreatedFont> addStandard)
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

        var links = new List<TextLinkArea>();
        if (count == 0)
        {
            return links;
        }

        double origin = layout.Lines[firstLine].Top;
        builder.Save();
        try
        {
            for (int l = firstLine; l < firstLine + count; l++)
            {
                ParagraphLine line = layout.Lines[l];
                DrawLine(builder, layout.Paragraph, line, x + line.X, top - (line.Top - origin), links);
            }
        }
        finally
        {
            builder.Restore();
        }

        return links;
    }

    private PdfCreatedFont FontFor(FontEntry entry)
    {
        if (entry.StandardName is { } std)
        {
            return _addStandard(std);
        }

        if (!_embedded.TryGetValue(entry, out PdfCreatedFont? font))
        {
            font = _addFont(entry.Data, new PdfEmbedFontOptions());
            _embedded[entry] = font;
        }

        return font;
    }

    private sealed class Piece
    {
        public required int Fmt;
        public required string Text;
        public required List<PdfShapedGlyph> Glyphs;
        public double Width;
        public bool IsHyphen;
        public int FirstItem;
        public int LastItem;
    }

    private void DrawLine(PdfContentBuilder b, ShapedParagraph para, ParagraphLine line, double x0, double lineTop, List<TextLinkArea> links)
    {
        double baseline = lineTop - line.Baseline;
        int count = line.ItemEnd - line.ItemStart;
        bool hyphen = line.EndsWithHyphen;
        int total = count + (hyphen ? 1 : 0);
        if (total == 0)
        {
            return;
        }

        // Display order of the line's items; a closing hyphen sits at the end of the line in the paragraph's direction.
        var levels = new byte[total];
        for (int k = 0; k < count; k++)
        {
            levels[k] = para.ItemAt(line.ItemStart + k).Level;
        }

        if (hyphen)
        {
            levels[count] = line.Segment.ParaLevel;
        }

        int[] order = TextShaping.GetVisualOrder(levels);

        var pieces = new List<Piece>();
        Piece? cur = null;
        int prevLogical = -2;
        foreach (int logical in order)
        {
            if (logical == count)
            {
                Hyph h = para.HyphenFor(line.ItemEnd - 1);
                Fmt hf = para.FmtAt(h.Fmt);
                var hp = new Piece
                {
                    Fmt = h.Fmt,
                    Text = h.Text,
                    Glyphs = [new PdfShapedGlyph(h.Gid, 0, Thousandths(h.Advance, hf.Size))],
                    Width = h.Advance,
                    IsHyphen = true,
                    FirstItem = -1,
                    LastItem = -1,
                };
                pieces.Add(hp);
                cur = null;
                prevLogical = -2;
                continue;
            }

            int idx = line.ItemStart + logical;
            Item it = para.ItemAt(idx);
            Fmt f = para.FmtAt(it.Fmt);
            bool extend = cur is not null && cur.Fmt == it.Fmt
                && para.ItemAt(cur.LastItem).Level == it.Level && Math.Abs(logical - prevLogical) == 1;
            if (!extend)
            {
                cur = new Piece { Fmt = it.Fmt, Text = string.Empty, Glyphs = [], FirstItem = idx, LastItem = idx };
                pieces.Add(cur);
            }

            cur!.LastItem = idx;
            prevLogical = logical;
            cur.Width += AppendItem(para, line, it, f, cur, (idx != line.ItemEnd - 1 || hyphen) ? 1 : 0);
        }

        // Logical text of each shaped piece, with cluster indexes relative to it.
        foreach (Piece p in pieces)
        {
            if (p.IsHyphen)
            {
                continue;
            }

            int lo = Math.Min(p.FirstItem, p.LastItem);
            int hi = Math.Max(p.FirstItem, p.LastItem);
            int textStart = para.ItemAt(lo).Start;
            Item last = para.ItemAt(hi);
            p.Text = para.Text.Substring(textStart, last.Start + last.Length - textStart);
            for (int g = 0; g < p.Glyphs.Count; g++)
            {
                PdfShapedGlyph gl = p.Glyphs[g];
                p.Glyphs[g] = gl with { Cluster = gl.Cluster - textStart };
            }
        }

        double x = x0;
        double lineBottom = baseline - (line.Height - line.Baseline);
        TextStyle? lastColorStyle = null;
        foreach (Piece p in pieces)
        {
            Fmt f = para.FmtAt(p.Fmt);
            PdfCreatedFont font = FontFor(f.Entry);
            LayoutColor color = f.Style.Color;
            if (lastColorStyle is null || lastColorStyle.Color != color)
            {
                b.SetFillRgb(color.R, color.G, color.B);
                lastColorStyle = f.Style;
            }

            b.SetFont(font, f.Size);
            if (f.IsStandard)
            {
                DrawStandard(b, para, p, f, x, baseline);
            }
            else
            {
                b.DrawGlyphRun(x, baseline, p.Glyphs.ToArray(), p.Text);
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
    private static double AppendItem(ShapedParagraph para, ParagraphLine line, Item it, Fmt f, Piece piece, int notLast)
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
            piece.Glyphs.Add(new PdfShapedGlyph(0, it.Start, Thousandths(a, size)));
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
            piece.Glyphs.Add(new PdfShapedGlyph(g.Gid, it.Start, Thousandths(a, size), Thousandths(g.Dx, size), Thousandths(g.Dy, size)));
        }

        return width;
    }

    private static double Thousandths(double points, double size) => points / size * 1000.0;

    // Standard fonts are drawn word by word with the standard font's own text path; positions come from the layout.
    private static void DrawStandard(PdfContentBuilder b, ShapedParagraph para, Piece p, Fmt f, double x, double baseline)
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

            pen += p.Glyphs[k].Advance * f.Size / 1000.0;
        }

        if (word.Length > 0)
        {
            b.DrawText(word.ToString(), wordX, baseline);
        }

        b.SetCharacterSpacing(0);
    }
}
