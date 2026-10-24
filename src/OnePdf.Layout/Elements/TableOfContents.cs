using OnePdf.Layout.Pagination;

namespace OnePdf.Layout;

/// <summary>
/// An explicit entry of the table of contents: the child gets an entry with <see cref="Title"/> at <see cref="Level"/> (counted from 1, like
/// heading levels), pointing at the top of the child's first piece. Use it for things that are not <see cref="Heading"/> elements.
/// </summary>
public sealed class TocEntry : SingleChildElement
{
    /// <summary>The text of the entry. When null or empty, the text of the child is used; no entry is made when that is empty too.</summary>
    public string? Title { get; set; }

    /// <summary>Nesting depth, 1 or more (values below 1 count as 1, above 9 as 9). Default 1.</summary>
    public int Level { get; set; } = 1;

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume) => Wrap(LayoutChild(ctx, c, resume), null);

    internal override void DrawCore(in DrawContext d, Placed p)
    {
        TocState.DefineTarget(d, p);
        DrawChildren(d, p);
    }
}

/// <summary>
/// How the entries of one level of a <see cref="TableOfContents"/> look. Every setting that is null falls back to the table's
/// <see cref="TableOfContents.Style"/>, and from there to the defaults of <see cref="Text"/>. The page number takes the style of its entry.
/// </summary>
public sealed class TocLevelStyle
{
    /// <summary>Font family, as for <see cref="Text.FontName"/>.</summary>
    public string? FontName { get; set; }

    /// <summary>Weight, 400 regular or 700 bold. Default 400.</summary>
    public int? Weight { get; set; }

    /// <summary>Whether to use an italic face. Default false.</summary>
    public bool? Italic { get; set; }

    /// <summary>Font size in points. Default 11.</summary>
    public double? Size { get; set; }

    /// <summary>Text colour. Default black.</summary>
    public LayoutColor? Color { get; set; }

    /// <summary>Line distance as a multiple of the font size. Default 1.2.</summary>
    public double? LineSpacing { get; set; }

    /// <summary>Extra space above each entry of the level, in points, except at the top of a page. Default 0.</summary>
    public double? SpaceBefore { get; set; }
}

/// <summary>
/// A table of contents made from the headings and <see cref="TocEntry"/> elements of the document: each entry shows its title, a leader
/// and the page number of its target, and links to it. Place it anywhere, usually before the content it lists.
/// </summary>
/// <remarks>
/// <para><b>Page numbers.</b> The numbers are not known when the table is laid out, and the table's own length moves everything after it.
/// When a document contains a table of contents, the pages are first laid out without drawing, as many times as it takes for the numbers
/// the table shows to equal the pages the headings land on (usually three runs; at most six), and then once more for real. The number column
/// has the width of the widest label seen, and never gets narrower between runs, so the runs settle. Nothing is held beyond the list of
/// entries, so memory stays independent of the page count; a document without a table of contents pays nothing. If the runs do not settle,
/// the last result is used and a note says so.</para>
/// <para>The number is the page label of the target page: with <see cref="LayoutPage.NumberStyle"/> roman, <see cref="LayoutPage.StartNumber"/>
/// or <see cref="LayoutPage.LabelPrefix"/> the table shows "iv" or "A-12" as a viewer does. The table finds headings and entries in the
/// flowing content of every section (not inside table cells, repeated header rows or headers and footers). A table inside a component or
/// custom container is found before layout unless the container is a type this package does not know; set
/// <see cref="LayoutOptions.TableOfContents"/> to true then.</para>
/// <para>Long titles wrap, and the number sits on the last line. Entries are tagged <c>TOC</c> / <c>TOCI</c> holding a <c>Reference</c> that
/// holds a <c>Link</c> (ISO 32000-2 14.8.4.3.3, 14.8.4.4.2); leader dots are artifacts. In right-to-left content the numbers move to the left.</para>
/// </remarks>
public sealed class TableOfContents : Element
{
    /// <summary>The shallowest level listed. Headings are levels 1 to 6. Default 1.</summary>
    public int MinLevel { get; set; } = 1;

    /// <summary>The deepest level listed. Default 3.</summary>
    public int MaxLevel { get; set; } = 3;

    /// <summary>
    /// The leader between the title and the number, repeated with a space between and lined up from entry to entry. Null or empty for none.
    /// Default ".".
    /// </summary>
    public string? Leader { get; set; } = ".";

    /// <summary>How far each level is moved in from the one above, in points. Default 14.</summary>
    public double Indent { get; set; } = 14;

    /// <summary>The least space between a title and its leader or number, in points. Default 6.</summary>
    public double Gap { get; set; } = 6;

    /// <summary>Space between neighbouring entries on one page, in points. Default 3.</summary>
    public double Spacing { get; set; } = 3;

    /// <summary>The style every level starts from.</summary>
    public TocLevelStyle Style { get; } = new();

    private readonly Dictionary<int, TocLevelStyle> _levels = [];

    /// <summary>The style of one level (1 or more), made on first use. It overrides <see cref="Style"/> for the settings it sets.</summary>
    public TocLevelStyle LevelStyle(int level)
    {
        if (!_levels.TryGetValue(level, out TocLevelStyle? s))
        {
            s = new TocLevelStyle();
            _levels[level] = s;
        }

        return s;
    }

    internal readonly record struct Resolved(string? FontName, int Weight, bool Italic, double Size, LayoutColor Color, double LineSpacing, double SpaceBefore);

    internal Resolved StyleOf(int level)
    {
        _levels.TryGetValue(level, out TocLevelStyle? l);
        return new Resolved(
            l?.FontName ?? Style.FontName,
            l?.Weight ?? Style.Weight ?? 400,
            l?.Italic ?? Style.Italic ?? false,
            l?.Size ?? Style.Size ?? 11,
            l?.Color ?? Style.Color ?? LayoutColor.Black,
            l?.LineSpacing ?? Style.LineSpacing ?? 1.2,
            Math.Max(0, l?.SpaceBefore ?? Style.SpaceBefore ?? 0));
    }

    internal static Text MakeText(string content, in Resolved s, HorizontalAlignment alignment) => new()
    {
        Content = content,
        FontName = s.FontName,
        Weight = s.Weight,
        Italic = s.Italic,
        Size = s.Size,
        Color = s.Color,
        LineSpacing = s.LineSpacing,
        Alignment = alignment,
    };

    internal override string TypeName => "TableOfContents";

    private const double DefaultWidth = 300;

    internal override WidthRange WidthsCore(LayoutContext ctx) => new(0, DefaultWidth);

    private sealed record Cursor(FlowCursor Flow);

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        double width = c.WidthBounded ? c.Width : DefaultWidth;
        TocState? state = ctx.Toc;
        if (state is null)
        {
            ctx.NoteOnce(this, "A table of contents was not found before layout, so it has no page numbers; set LayoutOptions.TableOfContents to true.");
        }

        IReadOnlyList<TocItem> items = state is null ? [] : state.Visible(this);
        var start = resume is Cursor r ? r.Flow : new FlowCursor(0, null);
        var source = new Source(this, ctx, state!, items, width);
        FlowPage page = FlowPaginator.Fill(source, Math.Max(0, Spacing), c.Height, c.AtTop, start, ctx.Rules);
        if (page.NoFit)
        {
            return ctx.NoFit(c, width, page.NoFitRequested);
        }

        var node = new Placed(this, 0, 0, width, page.Height);
        foreach (FlowPlacement fp in page.Placements)
        {
            var entry = (Placed)fp.Placed!;
            entry.X = 0;
            entry.Y = fp.Y;
            node.Children.Add(entry);
        }

        return page.Complete ? FlowFit.Complete(page.Height, node) : FlowFit.Partial(page.Height, node, new Cursor(page.Next));
    }

    internal override void DrawCore(in DrawContext d, Placed p)
    {
        if (p.Children.Count == 0)
        {
            return;
        }

        TagScope scope = d.Ctx.Tag.Open(this, p.Continued, p.Continues, "TOC");
        DrawChildren(d, p);
        d.Ctx.Tag.Close(scope);
    }

    private sealed class Source(TableOfContents toc, LayoutContext ctx, TocState state, IReadOnlyList<TocItem> items, double width) : IFlowSource
    {
        public int Count => items.Count;

        public bool KeepWithNext(int index) => false;

        public FlowFit Fit(int index, double available, bool atTop, object? resume, FlowRules rules)
        {
            TocItem item = items[index];
            ctx.Push("Entry ", index);
            try
            {
                Placed entry = TocRow.Build(toc, ctx, state, item, state.TargetName(toc, index), width, atTop);
                if (entry.Height > available + 1e-9)
                {
                    return FlowFit.NoFit(entry.Height);
                }

                return FlowFit.Complete(entry.Height, entry);
            }
            finally
            {
                ctx.Pop();
            }
        }
    }
}

/// <summary>One line of a table of contents: indented title, leader, number and the link over all of it.</summary>
internal sealed class TocRow : Element
{
    private static readonly TocRow Instance = new();

    private static readonly object ReferenceKey = new();

    private static readonly object LinkKey = new();

    internal sealed record Data(string Target, string Title);

    internal override string TypeName => "TocEntry";

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume) => throw new InvalidOperationException();

    public static Placed Build(TableOfContents toc, LayoutContext ctx, TocState state, TocItem item, string target, double width, bool atTop)
    {
        bool rtl = ctx.Direction == LayoutDirection.RightToLeft;
        TableOfContents.Resolved style = toc.StyleOf(item.Level);
        double gap = Math.Max(0, toc.Gap);
        double numberColumn = state.NumberColumn(toc, ctx);
        double indent = Math.Clamp((item.Level - toc.MinLevel) * Math.Max(0, toc.Indent), 0, width / 3);

        // The title takes what the indent, the number column and the gap leave; the number column is as wide for every entry, so titles wrap alike.
        double titleWidth = Math.Max(Math.Min(width, 20), width - indent - numberColumn - gap);
        var title = TableOfContents.MakeText(item.Title, style, HorizontalAlignment.Start);
        var titlePlaced = (Placed)title.Layout(ctx, new Constraint(titleWidth, double.PositiveInfinity, false), null).Placed!;
        ITextBlock block = ((Text.Fragment)titlePlaced.Data!).Block;
        int last = block.LineCount - 1;
        double lastTop = 0;
        for (int i = 0; i < last; i++)
        {
            lastTop += block.LineHeight(i);
        }

        double titleX = rtl ? width - indent - titleWidth : indent;
        double lineStart = titleX + block.LineX(last);
        double lineEnd = lineStart + block.LineWidth(last);

        var number = TableOfContents.MakeText(item.Label, style, HorizontalAlignment.End);
        double numberNatural = ctx.Text.Prepare(number, 1e6, ctx).NaturalWidth;
        double box = numberNatural + 1;
        var numberPlaced = (Placed)number.Layout(ctx, new Constraint(box, double.PositiveInfinity, false), null).Placed!;
        double numberX = rtl ? 0 : width - box;
        numberPlaced.X = numberX;
        numberPlaced.Y = lastTop;

        double spaceBefore = atTop ? 0 : style.SpaceBefore;
        titlePlaced.X = titleX;
        titlePlaced.Y = spaceBefore;
        numberPlaced.Y += spaceBefore;
        double height = Math.Max(titlePlaced.Height, lastTop + numberPlaced.Height) + spaceBefore;
        var row = new Placed(Instance, 0, 0, width, height) { Data = new Data(target, item.Title) };
        row.Children.Add(titlePlaced);

        Placed? leader = LayoutLeader(toc, ctx, state, style, rtl, width, lineStart, lineEnd, numberNatural, gap);
        if (leader is not null)
        {
            leader.Y = lastTop + spaceBefore;
            row.Children.Add(leader);
        }

        row.Children.Add(numberPlaced);
        return row;
    }

    private static Placed? LayoutLeader(TableOfContents toc, LayoutContext ctx, TocState state, in TableOfContents.Resolved style, bool rtl, double width, double lineStart, double lineEnd, double numberWidth, double gap)
    {
        if (string.IsNullOrEmpty(toc.Leader))
        {
            return null;
        }

        (double pitch, double mark) = state.LeaderMetrics(toc, ctx, style);
        if (!(pitch > 0))
        {
            return null;
        }

        // Dots sit on a grid anchored at the edge the number is on, so they line up from entry to entry.
        double regionStart = rtl ? numberWidth + gap : lineEnd + gap;
        double regionEnd = rtl ? lineStart - gap : width - numberWidth - gap;
        int first;
        int lastDot;
        double x;
        if (rtl)
        {
            first = (int)Math.Ceiling(regionStart / pitch);
            lastDot = (int)Math.Floor((regionEnd - mark) / pitch);
            x = first * pitch;
        }
        else
        {
            first = (int)Math.Ceiling((width - regionEnd) / pitch);
            lastDot = (int)Math.Floor((width - mark - regionStart) / pitch);
            x = width - lastDot * pitch - mark;
        }

        first = Math.Max(first, 1);
        int count = lastDot - first + 1;
        if (count < 1 || count > 2000)
        {
            return null;
        }

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append(toc.Leader);
        }

        string content = sb.ToString();
        Text text = TableOfContents.MakeText(content, style, HorizontalAlignment.Left);
        text.Artifact = true;
        double natural = ctx.Text.Prepare(text, 1e6, ctx).NaturalWidth;
        var placed = (Placed)text.Layout(ctx, new Constraint(natural + 1, double.PositiveInfinity, false), null).Placed!;
        placed.X = x;
        return placed;
    }

    // The whole row is one link; the leader is decoration. Structure: TOCI > Reference > Link (ISO 32000-2 14.8.4.3.3, 14.8.4.4.2).
    internal override void DrawCore(in DrawContext d, Placed p)
    {
        var data = (Data)p.Data!;
        Tagger tag = d.Ctx.Tag;
        TagScope item = tag.Open(this, false, false, "TOCI");
        TagScope reference = tag.Open(ReferenceKey, false, false, "Reference");
        TagScope link = tag.Open(LinkKey, false, false, "Link");
        DrawChildren(d, p);
        d.Ctx.AddLink(d, p.Width, p.Height, null, data.Target, link.Node?.Element, data.Title);
        tag.Close(link);
        tag.Close(reference);
        tag.Close(item);
    }
}
