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
        Entry e = Shaped(text, ctx);
        double w = Math.Max(width, 1);
        if (e.Block is null || e.Width != w)
        {
            e.Width = w;
            e.Block = new Block(e, e.Shaped!.Layout(w), _renderer, e.Shaped.MaxContentWidth, e.Shaped.MinContentWidth);
        }

        return e.Block;
    }

    public WidthRange Widths(Text text, LayoutContext ctx)
    {
        ShapedParagraph shaped = Shaped(text, ctx).Shaped!;
        return new WidthRange(shaped.MinContentWidth, shaped.MaxContentWidth);
    }

    // The shaped paragraph of a text, made once and kept until the text has been drawn to its last line (see Block.Draw).
    private Entry Shaped(Text text, LayoutContext ctx)
    {
        LayoutDirection dir = ctx.Direction;
        if (_cache.TryGetValue(text, out Entry? e) && e.Direction == dir && e.Shaped is not null)
        {
            return e;
        }

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
            LineBreakMode = text.LineBreakMode ?? ctx.Options.LineBreakMode,
            KnuthPlass = text.KnuthPlass ?? ctx.Options.KnuthPlass,
        };
        ShapedParagraph shaped = paragraph.Shape(_fonts);
        if (e is null || e.Direction != dir)
        {
            e = new Entry(dir);
            _cache.AddOrUpdate(text, e);
        }
        else if (e.Drawn)
        {
            // Back after being drawn and let go: repeated content (a header on every page). Keep it from now on.
            e.Keep = true;
        }

        e.Shaped = shaped;
        e.Block = null;
        return e;
    }

    private sealed class Entry(LayoutDirection direction)
    {
        public LayoutDirection Direction { get; } = direction;

        // Null once the text was drawn to its last line and nothing asks for it again.
        public ShapedParagraph? Shaped { get; set; }

        public double Width { get; set; }

        public Block? Block { get; set; }

        public bool Drawn { get; set; }

        public bool Keep { get; set; }

        // Shaped paragraphs are the biggest thing a long document holds, and a text is normally drawn once; letting go of the shaping after
        // its last line keeps memory to the pages in flight instead of the whole document.
        public void Release()
        {
            Drawn = true;
            if (!Keep)
            {
                Shaped = null;
                Block = null;
            }
        }
    }

    private sealed class Block(Entry entry, ParagraphLayout layout, TextRenderer renderer, double natural, double minContent) : ITextBlock
    {
        public int LineCount => layout.Lines.Count;

        public double NaturalWidth => natural;

        public double MinContentWidth => minContent;

        public double FirstBaseline => layout.Lines.Count > 0 ? layout.Lines[0].Baseline : 0;

        public double LineHeight(int line) => layout.Lines[line].Height;

        public double LineWidth(int line) => layout.Lines[line].Width;

        public double LineX(int line) => layout.Lines[line].X;

        public void Draw(in DrawContext d, Placed p, int first, int count)
        {
            renderer.Draw(d.B, layout, d.X, d.PdfY(0), first, count);
            if (first + count >= layout.Lines.Count)
            {
                entry.Release();
            }
        }
    }
}
