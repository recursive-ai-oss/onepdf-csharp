using OnePdf.Layout.Pagination;

namespace OnePdf.Layout;

/// <summary>
/// Stacks its items top to bottom and splits between them when the page ends. An item that does not fit moves to the next page; an item
/// that can split (a column, a box around one, text) continues there. Width is the offered width, or the widest item when unbounded.
/// </summary>
public sealed class Column : Element
{
    /// <summary>The items, first at the top.</summary>
    public List<Element> Items { get; } = [];

    /// <summary>Space between neighbouring items on one page, in points. Default 0. Not added at the top of a page.</summary>
    public double Spacing { get; set; }

    /// <summary>Where an item narrower than the column sits. Default <see cref="HorizontalAlignment.Start"/>.</summary>
    public HorizontalAlignment Alignment { get; set; }

    internal override WidthRange WidthsCore(LayoutContext ctx)
    {
        double lo = 0;
        double hi = 0;
        for (int i = 0; i < Items.Count; i++)
        {
            ctx.PushItem(i);
            try
            {
                WidthRange w = Items[i].MeasureWidths(ctx);
                lo = Math.Max(lo, w.Min);
                hi = Math.Max(hi, w.Max);
            }
            finally
            {
                ctx.Pop();
            }
        }

        return new WidthRange(lo, hi);
    }

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        var start = resume is null ? new FlowCursor(0, null) : (FlowCursor)resume;
        var source = new Source(this, ctx, c.Width);
        FlowPage page = FlowPaginator.Fill(source, Spacing, c.Height, c.AtTop, start, ctx.Rules);
        if (page.NoFit)
        {
            return ctx.NoFit(c, c.Width, page.NoFitRequested);
        }

        double width = c.WidthBounded ? c.Width : 0;
        if (!c.WidthBounded)
        {
            foreach (FlowPlacement fp in page.Placements)
            {
                width = Math.Max(width, ((Placed)fp.Placed!).Width);
            }
        }

        var node = new Placed(this, 0, 0, width, page.Height);
        foreach (FlowPlacement fp in page.Placements)
        {
            var child = (Placed)fp.Placed!;
            child.X = AlignX(Alignment, ctx.Direction, width - child.Width);
            child.Y = fp.Y;
            node.Children.Add(child);
        }

        return page.Complete ? FlowFit.Complete(page.Height, node) : FlowFit.Partial(page.Height, node, page.Next);
    }

    private sealed class Source(Column column, LayoutContext ctx, double width) : IFlowSource
    {
        public int Count => column.Items.Count;

        public bool KeepWithNext(int index) => column.Items[index].EffectiveKeepWithNext;

        public FlowFit Fit(int index, double available, bool atTop, object? resume, FlowRules rules)
        {
            ctx.PushItem(index);
            try
            {
                return column.Items[index].Layout(ctx, new Constraint(width, available, atTop), resume);
            }
            finally
            {
                ctx.Pop();
            }
        }
    }
}

/// <summary>How a <see cref="RowItem"/> gets its width.</summary>
public enum RowItemWidth
{
    /// <summary>The item's natural width, measured with unbounded width.</summary>
    Auto = 0,

    /// <summary>A fixed width in points.</summary>
    Constant,

    /// <summary>A share of what is left, in proportion to the weight.</summary>
    Relative,
}

/// <summary>One cell of a <see cref="Row"/>.</summary>
public sealed class RowItem
{
    /// <summary>Creates an item of automatic width.</summary>
    public RowItem(Element content) => Content = content ?? throw new ArgumentNullException(nameof(content));

    /// <summary>The element in the cell.</summary>
    public Element Content { get; }

    /// <summary>How the width is decided. Default <see cref="RowItemWidth.Auto"/>.</summary>
    public RowItemWidth WidthKind { get; set; }

    /// <summary>Points for <see cref="RowItemWidth.Constant"/>, weight for <see cref="RowItemWidth.Relative"/>.</summary>
    public double Value { get; set; }

    /// <summary>An item of a fixed width.</summary>
    public static RowItem Constant(double width, Element content) => new(content) { WidthKind = RowItemWidth.Constant, Value = width };

    /// <summary>An item sharing the free width by weight.</summary>
    public static RowItem Relative(double weight, Element content) => new(content) { WidthKind = RowItemWidth.Relative, Value = weight };
}

/// <summary>
/// Places its items side by side. Constant and automatic items take their width first and relative items share the rest. The row is as
/// tall as its tallest item; when the page ends, items that can split continue on the next page next to each other. In a right-to-left
/// direction the first item sits at the right.
/// </summary>
public sealed class Row : Element
{
    /// <summary>The cells in logical order.</summary>
    public List<RowItem> Items { get; } = [];

    /// <summary>Space between cells in points. Default 0.</summary>
    public double Spacing { get; set; }

    /// <summary>Where a cell shorter than the row sits (only once the row is on one page). Default <see cref="VerticalAlignment.Top"/>.</summary>
    public VerticalAlignment Alignment { get; set; }

    private sealed record RowResume(object?[] Inner, bool[] Done);

    internal override WidthRange WidthsCore(LayoutContext ctx)
    {
        double lo = Items.Count > 1 ? Spacing * (Items.Count - 1) : 0;
        double hi = lo;
        for (int i = 0; i < Items.Count; i++)
        {
            RowItem it = Items[i];
            if (it.WidthKind == RowItemWidth.Constant)
            {
                lo += Math.Max(0, it.Value);
                hi += Math.Max(0, it.Value);
                continue;
            }

            ctx.PushItem(i);
            try
            {
                WidthRange w = it.Content.MeasureWidths(ctx);
                lo += w.Min;
                hi += w.Max;
            }
            finally
            {
                ctx.Pop();
            }
        }

        return new WidthRange(lo, hi);
    }

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        int n = Items.Count;
        var state = (RowResume?)resume;
        var widths = new double[n];
        double gaps = n > 1 ? Spacing * (n - 1) : 0;
        double fixedTotal = 0;
        double weights = 0;
        for (int i = 0; i < n; i++)
        {
            RowItem it = Items[i];
            if (it.WidthKind == RowItemWidth.Constant)
            {
                widths[i] = Math.Max(0, it.Value);
                fixedTotal += widths[i];
            }
            else if (it.WidthKind == RowItemWidth.Relative && c.WidthBounded)
            {
                weights += Math.Max(0, it.Value);
            }
            else
            {
                // Natural width: lay the cell out unbounded and keep the width it asks for.
                ctx.PushItem(i);
                try
                {
                    FlowFit probe = it.Content.Layout(ctx, new Constraint(double.PositiveInfinity, double.PositiveInfinity, false), null);
                    widths[i] = probe.Placed is Placed pl ? pl.Width : 0;
                }
                finally
                {
                    ctx.Pop();
                }

                fixedTotal += widths[i];
            }
        }

        double free = c.WidthBounded ? Math.Max(0, c.Width - gaps - fixedTotal) : 0;
        for (int i = 0; i < n; i++)
        {
            if (Items[i].WidthKind == RowItemWidth.Relative && c.WidthBounded)
            {
                widths[i] = weights > 0 ? free * Math.Max(0, Items[i].Value) / weights : 0;
            }
        }

        var results = new FlowFit[n];
        bool anyPartial = false;
        double height = 0;
        for (int i = 0; i < n; i++)
        {
            if (state is not null && state.Done[i])
            {
                continue;
            }

            ctx.PushItem(i);
            try
            {
                results[i] = Items[i].Content.Layout(ctx, new Constraint(widths[i], c.Height, c.AtTop), state?.Inner[i]);
            }
            finally
            {
                ctx.Pop();
            }

            if (results[i].Kind == FlowKind.NoFit)
            {
                return ctx.NoFit(c, c.Width, results[i].Requested);
            }

            anyPartial |= results[i].Kind == FlowKind.Partial;
            height = Math.Max(height, results[i].Height);
        }

        var inner = new object?[n];
        var done = new bool[n];
        double totalWidth = gaps;
        foreach (double w in widths)
        {
            totalWidth += w;
        }

        double rowWidth = c.WidthBounded ? c.Width : totalWidth;
        var node = new Placed(this, 0, 0, rowWidth, height);
        double x = 0;
        bool rtl = ctx.Direction == LayoutDirection.RightToLeft;
        for (int i = 0; i < n; i++)
        {
            if (state is not null && state.Done[i])
            {
                done[i] = true;
            }
            else if (results[i].Kind == FlowKind.Partial)
            {
                inner[i] = results[i].Resume;
            }
            else
            {
                done[i] = true;
            }

            if (results[i].Placed is Placed child)
            {
                child.X = rtl ? rowWidth - x - widths[i] : x;
                child.Y = anyPartial ? 0 : AlignY(Alignment, height - child.Height);
                node.Children.Add(child);
            }

            x += widths[i] + Spacing;
        }

        return anyPartial ? FlowFit.Partial(height, node, new RowResume(inner, done)) : FlowFit.Complete(height, node);
    }
}

/// <summary>
/// Overlapping layers drawn in list order, first at the back. One layer, the primary, decides the size and may split across pages; the
/// others (a background, a watermark) are laid out at the primary's size, on every page the primary occupies.
/// </summary>
public sealed class Layers : Element
{
    /// <summary>The layers, back to front.</summary>
    public List<Element> Items { get; } = [];

    /// <summary>The index in <see cref="Items"/> of the layer that sets the size. Default 0.</summary>
    public int PrimaryIndex { get; set; }

    internal override WidthRange WidthsCore(LayoutContext ctx)
    {
        double lo = 0;
        double hi = 0;
        for (int i = 0; i < Items.Count; i++)
        {
            ctx.PushItem(i);
            try
            {
                WidthRange w = Items[i].MeasureWidths(ctx);
                lo = Math.Max(lo, w.Min);
                hi = Math.Max(hi, w.Max);
            }
            finally
            {
                ctx.Pop();
            }
        }

        return new WidthRange(lo, hi);
    }

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        if (PrimaryIndex < 0 || PrimaryIndex >= Items.Count)
        {
            throw new PdfLayoutException(ctx.PathText, c.Width, c.Height, double.NaN, double.NaN, "The primary layer index is out of range");
        }

        ctx.PushItem(PrimaryIndex);
        FlowFit primary;
        try
        {
            primary = Items[PrimaryIndex].Layout(ctx, c, resume);
        }
        finally
        {
            ctx.Pop();
        }

        if (primary.Kind == FlowKind.NoFit)
        {
            return primary;
        }

        if (primary.Placed is not Placed main)
        {
            return primary;
        }

        var node = new Placed(this, 0, 0, main.Width, main.Height);
        for (int i = 0; i < Items.Count; i++)
        {
            if (i == PrimaryIndex)
            {
                node.Children.Add(main);
                continue;
            }

            ctx.PushItem(i);
            try
            {
                FlowFit f = Items[i].Layout(ctx, new Constraint(main.Width, main.Height, true), null);
                if (f.Placed is Placed layer)
                {
                    if (f.Kind == FlowKind.Partial)
                    {
                        ctx.Note("The layer is taller than the primary layer and is cut off.");
                    }

                    node.Children.Add(layer);
                }
            }
            finally
            {
                ctx.Pop();
            }
        }

        return primary.Kind == FlowKind.Partial ? FlowFit.Partial(main.Height, node, primary.Resume) : FlowFit.Complete(main.Height, node);
    }
}
