namespace OnePdf.Layout.Pagination;

/// <summary>What happened when a flow item was asked to place itself in the space left on a page.</summary>
internal enum FlowKind
{
    /// <summary>All of the item was placed.</summary>
    Complete,

    /// <summary>Part of the item was placed; the rest continues on the next page from <see cref="FlowFit.Resume"/>.</summary>
    Partial,

    /// <summary>Nothing was placed: the item needs more room than is left.</summary>
    NoFit,

    /// <summary>The item is a forced page break. Nothing is placed and the page ends after the previous item.</summary>
    Break,
}

/// <summary>The outcome of placing one flow item. <see cref="Placed"/> is whatever the item wants drawn, opaque to the paginator.</summary>
internal readonly struct FlowFit
{
    private FlowFit(FlowKind kind, double height, object? placed, object? resume, double requested)
    {
        Kind = kind;
        Height = height;
        Placed = placed;
        Resume = resume;
        Requested = requested;
    }

    public FlowKind Kind { get; }

    /// <summary>Height consumed in points.</summary>
    public double Height { get; }

    public object? Placed { get; }

    /// <summary>Where the next page continues; only meaningful for <see cref="FlowKind.Partial"/>.</summary>
    public object? Resume { get; }

    /// <summary>For <see cref="FlowKind.NoFit"/>: the height the item wanted, or NaN.</summary>
    public double Requested { get; }

    public static FlowFit Complete(double height, object? placed) => new(FlowKind.Complete, height, placed, null, 0);

    public static FlowFit Partial(double height, object? placed, object? resume) => new(FlowKind.Partial, height, placed, resume, 0);

    public static FlowFit NoFit(double requested) => new(FlowKind.NoFit, 0, null, null, requested);

    public static FlowFit Break() => new(FlowKind.Break, 0, null, null, 0);
}

/// <summary>Widow and orphan control for items that are made of countable units (lines, table rows).</summary>
/// <param name="Orphans">Fewest units left at the bottom of a page when an item splits.</param>
/// <param name="Widows">Fewest units carried to the next page when an item splits.</param>
internal readonly record struct FlowRules(int Orphans, int Widows)
{
    public static FlowRules Default => new(2, 2);
}

/// <summary>An ordered list of things placed top to bottom, as the paginator sees it. Implemented by containers over their children.</summary>
internal interface IFlowSource
{
    int Count { get; }

    /// <summary>True when item <paramref name="index"/> must share a page with the start of the next item.</summary>
    bool KeepWithNext(int index);

    /// <summary>
    /// Places item <paramref name="index"/> (or its continuation <paramref name="resume"/>) in <paramref name="available"/> points.
    /// <paramref name="atTop"/> is true when nothing sits above it on the page, so a <see cref="FlowKind.NoFit"/> could not be cured
    /// by moving to a fresh page; an item that cannot fit then reports the problem itself.
    /// </summary>
    FlowFit Fit(int index, double available, bool atTop, object? resume, FlowRules rules);
}

/// <summary>Where a flow continues.</summary>
/// <param name="Index">The item.</param>
/// <param name="Resume">The item's own continuation, or null to start it.</param>
internal readonly record struct FlowCursor(int Index, object? Resume);

/// <summary>One item placed on a page.</summary>
internal readonly record struct FlowPlacement(int Item, double Y, double Height, object? Placed);

/// <summary>The items placed on one page and where to continue.</summary>
internal sealed class FlowPage
{
    public List<FlowPlacement> Placements { get; } = [];

    public double Height { get; set; }

    /// <summary>True when every remaining item was placed.</summary>
    public bool Complete { get; set; }

    /// <summary>True when nothing could be placed here (only possible when not at the top).</summary>
    public bool NoFit { get; set; }

    public double NoFitRequested { get; set; }

    public FlowCursor Next { get; set; }
}

/// <summary>
/// The pagination rules, independent of what the items are: fill a page top to bottom, split the item that straddles the bottom,
/// move an item whose keep-with-next chain would be torn, honour forced breaks and spacing. Containers (column, later tables and
/// paragraph blocks) plug in through <see cref="IFlowSource"/>.
/// </summary>
/// <remarks>
/// Rules (all deterministic, no look-back):
/// <list type="bullet">
/// <item>An item is placed in the order given; <c>spacing</c> separates neighbours on a page and is not added at a page top.</item>
/// <item>A partial item ends the page; its continuation is the first thing on the next one.</item>
/// <item>An item with keep-with-next is placed only if every following item of the chain up to the first without it is placed whole,
/// and the first without it at least begins, on the same page. Otherwise the page ends before it. At the top of a page the rule
/// cannot help and the chain is placed as it comes.</item>
/// <item>A break item ends the page; at the top of a page it is ignored so no blank page appears.</item>
/// </list>
/// </remarks>
internal static class FlowPaginator
{
    public static FlowPage Fill(IFlowSource items, double spacing, double available, bool atTop, FlowCursor start, FlowRules rules)
    {
        var page = new FlowPage();
        double y = 0;
        int i = start.Index;
        object? resume = start.Resume;
        while (i < items.Count)
        {
            bool first = page.Placements.Count == 0;
            double gap = first ? 0 : spacing;
            double remaining = available - y - gap;
            bool top = atTop && first;

            // The gap alone can push past the bottom; nothing more is placed here.
            if (!first && remaining < 0)
            {
                page.Next = new FlowCursor(i, resume);
                page.Height = y;
                return page;
            }

            if (resume is null && items.KeepWithNext(i) && !ChainFits(items, i, spacing, remaining, top, rules, out double chainHeight) && !top)
            {
                if (first)
                {
                    page.NoFit = true;
                    page.NoFitRequested = chainHeight;
                    return page;
                }

                page.Next = new FlowCursor(i, null);
                page.Height = y;
                return page;
            }

            FlowFit fit = items.Fit(i, remaining, top, resume, rules);
            switch (fit.Kind)
            {
                case FlowKind.NoFit:
                    if (first)
                    {
                        page.NoFit = true;
                        page.NoFitRequested = fit.Requested;
                        return page;
                    }

                    page.Next = new FlowCursor(i, resume);
                    page.Height = y;
                    return page;

                case FlowKind.Break:
                    resume = null;
                    i++;
                    if (top)
                    {
                        continue;
                    }

                    page.Next = new FlowCursor(i, null);
                    page.Height = y;
                    return page;

                case FlowKind.Complete:
                    page.Placements.Add(new FlowPlacement(i, y + gap, fit.Height, fit.Placed));
                    y += gap + fit.Height;
                    resume = null;
                    i++;
                    break;

                default:
                    page.Placements.Add(new FlowPlacement(i, y + gap, fit.Height, fit.Placed));
                    y += gap + fit.Height;
                    page.Next = new FlowCursor(i, fit.Resume);
                    page.Height = y;
                    return page;
            }
        }

        page.Complete = true;
        page.Height = y;
        return page;
    }

    // Tries the chain that starts at `index` without keeping the results. The layout is done again for real when the chain fits.
    private static bool ChainFits(IFlowSource items, int index, double spacing, double remaining, bool top, FlowRules rules, out double height)
    {
        height = 0;
        double left = remaining;
        for (int j = index; j < items.Count; j++)
        {
            double gap = j == index ? 0 : spacing;
            FlowFit fit = items.Fit(j, left - gap, top && j == index, null, rules);
            if (fit.Kind == FlowKind.NoFit)
            {
                height += double.IsNaN(fit.Requested) ? 0 : fit.Requested;
                return false;
            }

            if (fit.Kind == FlowKind.Break)
            {
                return true;
            }

            height += gap + fit.Height;
            left -= gap + fit.Height;
            if (fit.Kind == FlowKind.Partial)
            {
                // A partial item is the end of what fits; the chain holds if this item was the last one that had to share the page.
                return !items.KeepWithNext(j);
            }

            if (!items.KeepWithNext(j))
            {
                return true;
            }
        }

        return true;
    }
}

/// <summary>Runs a flow over a sequence of pages.</summary>
internal static class PageSequencer
{
    /// <summary>
    /// Fits <paramref name="body"/> page after page. <paramref name="onPage"/> receives each page's result before the next page is laid out,
    /// so a page can be drawn and written, then dropped.
    /// </summary>
    /// <param name="body">The whole flow as one item, resumable.</param>
    /// <param name="rules">Widow and orphan control.</param>
    /// <param name="maxPages">A guard against runaway layouts.</param>
    /// <param name="availableHeight">Height of the body region on page <c>n</c> (after repeated header and footer).</param>
    /// <param name="onPage">Called with the page number and its result.</param>
    /// <param name="onLimit">Builds the exception when <paramref name="maxPages"/> is exceeded or a page makes no progress.</param>
    public static int Run(Func<double, bool, object?, FlowRules, FlowFit> body, FlowRules rules, int maxPages, Func<int, double> availableHeight, Action<int, FlowFit> onPage, Func<string, Exception> onLimit)
    {
        object? resume = null;
        for (int page = 0; ; page++)
        {
            if (page >= maxPages)
            {
                throw onLimit("The document needs more pages than the limit of " + maxPages.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            FlowFit fit = body(availableHeight(page), true, resume, rules);
            if (fit.Kind == FlowKind.NoFit)
            {
                throw onLimit("The content does not fit an empty page");
            }

            onPage(page, fit);
            if (fit.Kind != FlowKind.Partial)
            {
                return page + 1;
            }

            if (fit.Height <= 0 && Equals(fit.Resume, resume))
            {
                throw onLimit("A page made no progress");
            }

            resume = fit.Resume;
        }
    }
}
