namespace OnePdf.Layout;

/// <summary>The text engine of the layout: shapes each <see cref="Text"/> element with the paragraph engine, caches the shaping and the layout at the last width, and draws lines through a <see cref="TextRenderer"/>.</summary>
internal sealed class ParagraphTextEngine : ITextEngine
{
    private readonly TextFontRegistry _fonts;
    private readonly TextRenderer _renderer;
    // Weak, so text made on the fly (page numbers, list markers) does not stay for the whole document.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Text, Entry> _cache = [];

    public ParagraphTextEngine(ILayoutTarget target, TextFontRegistry fonts)
    {
        _fonts = fonts;
        _renderer = new TextRenderer(target.AddFont, target.AddStandardFont);
    }

    public ITextBlock Prepare(Text text, double width, LayoutContext ctx)
    {
        LayoutDirection dir = ctx.Direction;
        if (!_cache.TryGetValue(text, out Entry? e) || e.Direction != dir)
        {
            var style = new TextStyle
            {
                Families = text.FontName is null ? [] : [text.FontName],
                Size = text.Size,
                Color = text.Color,
                Weight = text.Weight,
                Italic = text.Italic,
                LineHeight = text.LineSpacing,
                Language = text.Language,
            };
            IEnumerable<TextSpan> spans = text.Spans.Count > 0 ? text.Spans : [new TextSpan(text.Content ?? string.Empty)];
            var paragraph = new TextParagraph(spans)
            {
                DefaultStyle = style,
                Direction = dir == LayoutDirection.RightToLeft ? ParagraphDirection.RightToLeft : ParagraphDirection.LeftToRight,
                Alignment = text.Justify ? TextAlignment.Justify : text.Alignment switch
                {
                    HorizontalAlignment.Center => TextAlignment.Center,
                    HorizontalAlignment.End => TextAlignment.End,
                    HorizontalAlignment.Left => TextAlignment.Left,
                    HorizontalAlignment.Right => TextAlignment.Right,
                    _ => TextAlignment.Start,
                },
                FirstLineIndent = text.FirstLineIndent,
                Hyphenator = text.Hyphenator,
            };
            e = new Entry(dir, paragraph.Shape(_fonts));
            _cache.AddOrUpdate(text, e);
        }

        double w = Math.Max(width, 1);
        if (e.Block is null || e.Width != w)
        {
            e.Width = w;
            e.Block = new Block(e.Shaped.Layout(w), _renderer, e.Shaped.MaxContentWidth, e.Shaped.MinContentWidth);
        }

        return e.Block;
    }

    private sealed class Entry(LayoutDirection direction, ShapedParagraph shaped)
    {
        public LayoutDirection Direction { get; } = direction;

        public ShapedParagraph Shaped { get; } = shaped;

        public double Width { get; set; }

        public Block? Block { get; set; }
    }

    private sealed class Block(ParagraphLayout layout, TextRenderer renderer, double natural, double minContent) : ITextBlock
    {
        public int LineCount => layout.Lines.Count;

        public double NaturalWidth => natural;

        public double MinContentWidth => minContent;

        public double FirstBaseline => layout.Lines.Count > 0 ? layout.Lines[0].Baseline : 0;

        public double LineHeight(int line) => layout.Lines[line].Height;

        public void Draw(in DrawContext d, Placed p, int first, int count) => renderer.Draw(d.B, layout, d.X, d.PdfY(0), first, count);
    }
}
