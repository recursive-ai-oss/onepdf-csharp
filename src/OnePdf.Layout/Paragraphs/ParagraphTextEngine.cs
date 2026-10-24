using OnePdf.Layout.Pagination;

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

    internal TextFontRegistry Fonts => _fonts;

    internal TextRenderer Renderer => _renderer;

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
            Palette = text.Palette,
            Weight = text.Weight,
            Italic = text.Italic,
            LineHeight = text.LineSpacing,
            Language = text.Language,
        };
        IEnumerable<TextSpan> spans = text.Spans.Count > 0 ? ResolveInlines(text.Spans, ctx) : [new TextSpan(text.Content ?? string.Empty)];
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

    // Inline elements are laid out now, so the paragraph knows their sizes; the paragraph engine sees them as sized objects.
    private static IEnumerable<TextSpan> ResolveInlines(IList<TextSpan> spans, LayoutContext ctx)
    {
        List<TextSpan>? resolved = null;
        for (int i = 0; i < spans.Count; i++)
        {
            if (spans[i].Inline is InlineElement element)
            {
                resolved ??= [.. spans];
                resolved[i] = spans[i] with { Inline = Resolve(element, ctx) };
            }
        }

        return resolved ?? (IEnumerable<TextSpan>)spans;
    }

    private static ResolvedInline Resolve(InlineElement ie, LayoutContext ctx)
    {
        ie.Validate();
        Element child = ie.Child;
        ctx.Push("Inline");
        if (ie.Artifact)
        {
            ctx.LayoutArtifactDepth++;
        }

        try
        {
            double width;
            double offered;
            if (ie.Width is { } fixedWidth)
            {
                width = fixedWidth;
                offered = fixedWidth;
            }
            else
            {
                width = child.MeasureWidths(ctx).Max;
                if (ie.MaxWidth is { } cap && cap < width)
                {
                    width = cap;
                    offered = cap;
                }
                else
                {
                    // Text is laid out at exactly its natural width; a little room keeps rounding from wrapping its last word.
                    offered = width + 0.01;
                }
            }

            FlowFit fit = child.Layout(ctx, new Constraint(offered, double.PositiveInfinity, true), null);
            if (fit.Kind == FlowKind.Partial)
            {
                throw new PdfLayoutException(ctx.PathText, offered, double.PositiveInfinity, double.NaN, double.NaN, "An inline element cannot contain a page break");
            }

            if (fit.Placed is not Placed placed)
            {
                throw new PdfLayoutException(ctx.PathText, offered, double.PositiveInfinity, offered, double.NaN, "An inline element could not be laid out");
            }

            double height = ie.Height ?? placed.Height;
            double inner = ie.InnerBaseline ?? (FirstBaseline(placed) is { } fb ? Math.Max(0, height - fb) : 0);
            return new ResolvedInline(placed, width, height, inner, ie.AltText, ie.Link, ie.Alignment, ie.BaselineOffset, ie.GrowsLine, ie.KeepWithNeighbours, ie.Artifact);
        }
        finally
        {
            if (ie.Artifact)
            {
                ctx.LayoutArtifactDepth--;
            }

            ctx.Pop();
        }
    }

    // Distance from the top of the placed element to the baseline of its first line of text, or null when it has none.
    private static double? FirstBaseline(Placed root)
    {
        var stack = new Stack<(Placed Node, double Y)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            (Placed node, double y) = stack.Pop();
            if (node.Data is Text.Fragment { Count: > 0 } fragment)
            {
                return y + node.Y + fragment.Block.FirstBaseline;
            }

            for (int i = node.Children.Count - 1; i >= 0; i--)
            {
                stack.Push((node.Children[i], y + node.Y));
            }
        }

        return null;
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

        public bool HasInline => layout.Paragraph.HasInline;

        public void Draw(in DrawContext d, Placed p, int first, int count)
        {
            renderer.Context = d.Ctx;
            renderer.DefaultPalette = d.Ctx.Options.ColorPalette;
            renderer.Draw(d.B, layout, d.X, d.PdfY(0), first, count, HasInline ? new Host(d) : null);
            if (first + count >= layout.Lines.Count)
            {
                entry.Release();
            }
        }
    }
}


/// <summary>Carries out what the text renderer asks for inline objects: structure, links and the drawing of element subtrees, at positions in the paragraph's frame.</summary>
internal sealed class Host(DrawContext frame) : IInlineHost
{
    private sealed class Token
    {
        public TagScope Link;
        public TagScope Figure;
        public bool Artifact;
    }

    private Tagger Tag => frame.Ctx.Tag;

    public bool BeginContent() => Tag.BeginContent();

    public void EndContent(bool began) => Tag.EndContent(began);

    public object? Begin(InlineObject item, PdfContentBuilder b, double x, double y)
    {
        Tagger tag = Tag;
        var token = new Token();
        if (item.Link is { Length: > 0 } && !item.IsDecoration)
        {
            token.Link = tag.Open(item, false, false, "Link");
        }

        if (!tag.Enabled)
        {
            return token;
        }

        if (item.IsDecoration)
        {
            token.Artifact = tag.BeginArtifact(b);
        }
        else if (item.AltText is not null)
        {
            token.Figure = tag.Open(item, false, false, "Figure", item.AltText);
        }
        else if (item is InlineBox { Image: not null })
        {
            frame.Ctx.NoteOnce(item, "An inline image has no alternate text (AltText); assistive technology cannot describe it. Set AltText, or Artifact for a decorative image.");
            token.Figure = tag.Open(item, false, false, "Figure");
        }

        return token;
    }

    public void DrawElement(ResolvedInline item, double x, double y)
    {
        DrawContext at = At(x, y + item.ResolvedHeight);
        item.Placed.Element.Draw(at, item.Placed);
    }

    public void End(object? token, InlineObject item, PdfContentBuilder b, double x, double y)
    {
        var t = (Token)token!;
        Tagger tag = Tag;
        tag.Close(t.Figure);
        if (t.Artifact)
        {
            tag.EndArtifact(b, true);
        }

        if (item.Link is { Length: > 0 } link && !item.IsDecoration)
        {
            DrawContext at = At(x, y + item.ResolvedHeight);
            if (link[0] == '#')
            {
                frame.Ctx.AddLink(at, item.ResolvedWidth, item.ResolvedHeight, null, link[1..], t.Link.Node?.Element, item.AltText);
            }
            else
            {
                frame.Ctx.AddLink(at, item.ResolvedWidth, item.ResolvedHeight, link, null, t.Link.Node?.Element, item.AltText);
            }
        }

        tag.Close(t.Link);
    }

    // A frame whose origin is the top-left corner of a box at (x, top) in the builder's coordinates.
    private DrawContext At(double x, double top) => frame.At(x - frame.X, frame.PdfY(0) - top);
}
