using OnePdf.Layout.Pagination;

namespace OnePdf.Layout;

/// <summary>How a <see cref="MultiColumn"/> fills the columns of the last region of its content.</summary>
public enum ColumnFill
{
    /// <summary>
    /// Where the content ends inside a region, the columns are shortened to the least height that still holds everything, so they end at
    /// about the same level. Regions that are filled to the bottom (the content goes on) are unaffected. This is the default.
    /// </summary>
    Balanced = 0,

    /// <summary>Each column is filled to the bottom of the space offered before the next one is started; the last column ends where the content does.</summary>
    Sequential,
}

/// <summary>
/// Pours its items through a number of equal columns: down the first column, then down the next, and on to the next page when the last
/// column is full. The items are laid out exactly as a <see cref="Column"/> lays them out on pages — paragraphs split with widows and
/// orphans, tables split between rows and repeat their header rows, keep-together, keep-with-next and ensure-space apply, and a heading
/// never ends a column — with the column height playing the part of the page height.
/// </summary>
/// <remarks>
/// <para>Order: the flow reads column 1, column 2, ... and then the next page. In a right-to-left direction column 1 is the rightmost, so
/// the flow reads right to left. The structure tree and text extraction follow the flow.</para>
/// <para>Balancing (<see cref="Fill"/> = <see cref="ColumnFill.Balanced"/>): once the content is known to end inside the region, the least
/// column height that still holds it is searched for by bisection. The search lays the content out at most 12 more times and stops at a
/// resolution of a quarter of a point, whatever the content. Content that cannot be divided (one paragraph of fewer lines than the widow and
/// orphan rules need, one tall image) ends up in as few columns as it needs; the other columns stay empty.</para>
/// <para>A region is as tall as its tallest column. With an unbounded height the content is balanced over all columns. With an unbounded
/// width there is a single column, and a note is recorded. A <see cref="PageBreak"/> directly in the flow ends the column like a
/// <see cref="ColumnBreak"/>.</para>
/// </remarks>
public sealed class MultiColumn : Element
{
    /// <summary>The most columns allowed.</summary>
    public const int MaxColumns = 32;

    private int _count = 2;

    /// <summary>The items, poured in order.</summary>
    public List<Element> Items { get; } = [];

    internal override void PushChildren(Stack<Element> into)
    {
        foreach (Element e in Items)
        {
            into.Push(e);
        }
    }

    /// <summary>How many columns. Default 2. From 1 to <see cref="MaxColumns"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside that range.</exception>
    public int Count
    {
        get => _count;
        set => _count = value is >= 1 and <= MaxColumns ? value : throw new ArgumentOutOfRangeException(nameof(value), "A multi-column flow has 1 to " + MaxColumns + " columns.");
    }

    /// <summary>Space between neighbouring columns in points. Default 12.</summary>
    public double Gap { get; set; } = 12;

    /// <summary>Width of the rule drawn in the middle of each gap, in points; 0, the default, draws none. The rule is decoration and spans the height of the region. It is drawn only between columns that have content.</summary>
    public double RuleWidth { get; set; }

    /// <summary>Colour of the rule. Default black.</summary>
    public LayoutColor RuleColor { get; set; } = LayoutColor.Black;

    /// <summary>Whether the last region is balanced. Default <see cref="ColumnFill.Balanced"/>.</summary>
    public ColumnFill Fill { get; set; }

    /// <summary>Space between neighbouring items within a column, in points. Default 0. Not added at the top of a column.</summary>
    public double Spacing { get; set; }

    private const int BalanceProbes = 12;
    private const double BalanceResolution = 0.25;

    private sealed record Geometry(double[] Xs, double ColumnWidth, double Gap, int Used);

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

        double gaps = Math.Max(0, Gap) * (Count - 1);
        return new WidthRange((lo * Count) + gaps, (hi * Count) + gaps);
    }

    private sealed class Region
    {
        public List<FlowPage> Columns { get; } = [];

        public bool Complete { get; set; }

        public FlowCursor Next { get; set; }

        public bool NoFit { get; set; }

        public double NoFitRequested { get; set; }

        public double Height
        {
            get
            {
                double h = 0;
                foreach (FlowPage p in Columns)
                {
                    h = Math.Max(h, p.Height);
                }

                return h;
            }
        }
    }

    // Fills up to `n` columns of `height` points, the way pages are filled one after the other.
    private Region Pour(Source source, int n, double height, bool atTop, FlowCursor start, FlowRules rules)
    {
        var region = new Region();
        FlowCursor cursor = start;
        for (int j = 0; j < n; j++)
        {
            FlowPage page = FlowPaginator.Fill(source, Spacing, height, atTop, cursor, rules);
            if (page.NoFit)
            {
                // Nothing fits the first column: the region belongs on a fresh page. A later column that cannot take the next item has
                // the same height as the first, so the item would not fit there either: the region ends and the item opens the next one.
                if (j == 0)
                {
                    region.NoFit = true;
                    region.NoFitRequested = page.NoFitRequested;
                    return region;
                }

                break;
            }

            region.Columns.Add(page);
            if (page.Complete)
            {
                region.Complete = true;
                return region;
            }

            cursor = page.Next;
            if (cursor.Resume is null && cursor.Index >= Items.Count)
            {
                // A column break after the last item: nothing is left to pour.
                region.Complete = true;
                return region;
            }
        }

        region.Next = cursor;
        return region;
    }

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        int n = Count;
        double gap = Math.Max(0, Gap);
        bool bounded = c.WidthBounded;
        if (!bounded && n > 1)
        {
            ctx.NoteOnce(this, "A multi-column flow offered unbounded width has a single column.");
            n = 1;
        }

        double colW = bounded ? Math.Max(0, (c.Width - (gap * (n - 1))) / n) : double.PositiveInfinity;
        var start = resume is null ? new FlowCursor(0, null) : (FlowCursor)resume;
        var source = new Source(this, ctx, colW);
        Region region = Pour(source, n, c.Height, c.AtTop, start, ctx.Rules);
        if (region.NoFit)
        {
            return ctx.NoFit(c, c.Width, region.NoFitRequested);
        }

        if (Fill == ColumnFill.Balanced && region.Complete && n > 1 && region.Height > 0)
        {
            region = Balance(source, n, start, region, ctx.Rules);
        }

        double width = bounded ? c.Width : 0;
        if (!bounded)
        {
            foreach (FlowPage page in region.Columns)
            {
                foreach (FlowPlacement fp in page.Placements)
                {
                    width = Math.Max(width, ((Placed)fp.Placed!).Width);
                }
            }
        }

        double height = region.Height;
        var xs = new double[region.Columns.Count];
        var node = new Placed(this, 0, 0, width, height);
        bool rtl = ctx.Direction == LayoutDirection.RightToLeft;
        for (int j = 0; j < region.Columns.Count; j++)
        {
            double x = !bounded ? 0 : rtl ? c.Width - ((j + 1) * colW) - (j * gap) : j * (colW + gap);
            xs[j] = x;
            foreach (FlowPlacement fp in region.Columns[j].Placements)
            {
                var child = (Placed)fp.Placed!;
                child.X = x + (bounded ? AlignX(HorizontalAlignment.Start, ctx.Direction, colW - child.Width) : 0);
                child.Y = fp.Y;
                node.Children.Add(child);
            }
        }

        node.Data = new Geometry(xs, colW, gap, xs.Length);
        return region.Complete ? FlowFit.Complete(height, node) : FlowFit.Partial(height, node, region.Next);
    }

    // The content is known to fit when every column may be `region.Height` tall. Bisect for the least height that still holds it,
    // keeping the best layout seen; a trial at a height that does not hold the content is thrown away.
    private Region Balance(Source source, int n, FlowCursor start, Region full, FlowRules rules)
    {
        Region best = full;
        double hi = full.Height;
        double lo = 0;
        for (int i = 0; i < BalanceProbes && hi - lo > BalanceResolution; i++)
        {
            double mid = (lo + hi) / 2;

            // Not at the top: a trial that cannot place something reports it instead of throwing; the real layout already succeeded.
            Region trial = Pour(source, n, mid, false, start, rules);
            if (!trial.NoFit && trial.Complete)
            {
                best = trial;
                hi = Math.Min(mid, trial.Height);
            }
            else
            {
                lo = mid;
            }
        }

        return best;
    }

    internal override void DrawCore(in DrawContext d, Placed p)
    {
        if (RuleWidth > 0 && p.Data is Geometry g && g.Used > 1 && p.Height > 0)
        {
            Tagger tag = d.Ctx.Tag;
            bool artifact = tag.BeginArtifact(d.B);
            PdfContentBuilder b = d.B;
            b.Save();
            b.SetStrokeRgb(RuleColor.R, RuleColor.G, RuleColor.B);
            b.SetLineWidth(RuleWidth);
            for (int j = 0; j + 1 < g.Used; j++)
            {
                double x = g.Xs[j + 1] > g.Xs[j] ? g.Xs[j] + g.ColumnWidth + (g.Gap / 2) : g.Xs[j] - (g.Gap / 2);
                b.MoveTo(d.X + x, d.PdfY(0));
                b.LineTo(d.X + x, d.PdfY(p.Height));
                b.Stroke();
            }

            b.Restore();
            tag.EndArtifact(b, artifact);
        }

        DrawChildren(d, p);
    }

    private sealed class Source(MultiColumn flow, LayoutContext ctx, double width) : IFlowSource
    {
        public int Count => flow.Items.Count;

        public bool KeepWithNext(int index) => flow.Items[index].EffectiveKeepWithNext;

        public FlowFit Fit(int index, double available, bool atTop, object? resume, FlowRules rules)
        {
            ctx.PushItem(index);
            try
            {
                return flow.Items[index].Layout(ctx, new Constraint(width, available, atTop), resume);
            }
            finally
            {
                ctx.Pop();
            }
        }
    }
}

/// <summary>
/// Ends the current column of the <see cref="MultiColumn"/> it sits in directly: whatever follows starts the next column (the next page after
/// the last column). At the top of a column it does nothing, so no empty column appears. Elsewhere it behaves like a <see cref="PageBreak"/>.
/// </summary>
public sealed class ColumnBreak : Element
{
    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume) => FlowFit.Break();
}
