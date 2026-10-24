using OnePdf.Layout.Pagination;

namespace OnePdf.Layout.Tests;

/// <summary>The pagination core on its own, with items that are just heights and line counts.</summary>
public class PaginationCoreTests
{
    private sealed class Item
    {
        public double Height;
        public bool KeepNext;
        public bool Break;
        public int Lines;
    }

    private sealed class Source(params Item[] items) : IFlowSource
    {
        public int Count => items.Length;

        public bool KeepWithNext(int index) => items[index].KeepNext;

        public FlowFit Fit(int index, double available, bool atTop, object? resume, FlowRules rules)
        {
            Item it = items[index];
            if (it.Break)
            {
                return FlowFit.Break();
            }

            if (it.Lines > 0)
            {
                // Each line is 10 points; resume is the first line still to place.
                int first = resume is int r ? r : 0;
                int left = it.Lines - first;
                int fit = (int)Math.Floor(available / 10);
                if (fit >= left)
                {
                    return FlowFit.Complete(left * 10, index);
                }

                int take = fit;
                if (left - take < rules.Widows)
                {
                    take = left - rules.Widows;
                }

                if (first == 0 && take < rules.Orphans)
                {
                    take = 0;
                }

                return take <= 0 ? FlowFit.NoFit(10) : FlowFit.Partial(take * 10, index, first + take);
            }

            return it.Height <= available ? FlowFit.Complete(it.Height, index) : FlowFit.NoFit(it.Height);
        }
    }

    private static List<FlowPage> Run(Source s, double height, double spacing = 0)
    {
        var pages = new List<FlowPage>();
        var cursor = new FlowCursor(0, null);
        while (true)
        {
            FlowPage p = FlowPaginator.Fill(s, spacing, height, true, cursor, FlowRules.Default);
            Assert.False(p.NoFit);
            pages.Add(p);
            if (p.Complete)
            {
                return pages;
            }

            cursor = p.Next;
            Assert.True(pages.Count < 100);
        }
    }

    private static int[][] Items(List<FlowPage> pages) => [.. pages.Select(p => p.Placements.Select(x => x.Item).ToArray())];

    [Fact]
    public void FillsAndMovesTheItemThatDoesNotFit()
    {
        var pages = Run(new Source(new Item { Height = 60 }, new Item { Height = 60 }, new Item { Height = 30 }), 100);
        Assert.Equal([[0], [1, 2]], Items(pages));
    }

    [Fact]
    public void KeepWithNextChainMovesTogether()
    {
        var s = new Source(new Item { Height = 50 }, new Item { Height = 20, KeepNext = true }, new Item { Height = 20, KeepNext = true }, new Item { Height = 20 });
        var pages = Run(s, 100);
        Assert.Equal([[0], [1, 2, 3]], Items(pages));
    }

    [Fact]
    public void KeepWithNextChainTallerThanThePageIsPlacedAsItComes()
    {
        var s = new Source(new Item { Height = 60, KeepNext = true }, new Item { Height = 60 });
        var pages = Run(s, 100);
        Assert.Equal([[0], [1]], Items(pages));
    }

    [Fact]
    public void BreaksEndThePageAndAreIgnoredAtTheTop()
    {
        var s = new Source(new Item { Break = true }, new Item { Height = 10 }, new Item { Break = true }, new Item { Height = 10 });
        var pages = Run(s, 100);
        Assert.Equal([[1], [3]], Items(pages));
    }

    [Fact]
    public void SpacingSeparatesNeighboursButNotPageTops()
    {
        var pages = Run(new Source(new Item { Height = 40 }, new Item { Height = 40 }, new Item { Height = 40 }), 100, spacing: 10);
        Assert.Equal([[0, 1], [2]], Items(pages));
        Assert.Equal(50, pages[0].Placements[1].Y);
        Assert.Equal(0, pages[1].Placements[0].Y);
    }

    [Fact]
    public void WidowsAndOrphansDecideWhereAParagraphSplits()
    {
        // 25 lines at 10 pt on 100 pt pages: 10 + 10 + 5; the last page keeps at least two lines.
        var pages = Run(new Source(new Item { Lines = 25 }), 100);
        Assert.Equal([10, 10, 5], pages.Select(p => (int)p.Height / 10));
        // 21 lines would end with a single widow line: the second page gives one up.
        pages = Run(new Source(new Item { Lines = 21 }), 100);
        Assert.Equal([10, 9, 2], pages.Select(p => (int)p.Height / 10));
        // Starting with one line of room would leave an orphan, so the paragraph starts on the next page.
        pages = Run(new Source(new Item { Height = 90 }, new Item { Lines = 5 }), 100);
        Assert.Equal([[0], [1]], Items(pages));
    }

    [Fact]
    public void NoFitAtTheTopIsReportedToTheCaller()
    {
        FlowPage p = FlowPaginator.Fill(new Source(new Item { Height = 500 }), 0, 100, false, new FlowCursor(0, null), FlowRules.Default);
        Assert.True(p.NoFit);
        Assert.Equal(500, p.NoFitRequested);
    }

    [Fact]
    public void SequencerFeedsEachPageBeforeTheNextAndGuardsRunaways()
    {
        var s = new Source(new Item { Height = 60 }, new Item { Height = 60 }, new Item { Height = 60 });
        var seen = new List<int>();
        FlowFit Body(double available, bool atTop, object? resume, FlowRules rules)
        {
            FlowPage p = FlowPaginator.Fill(s, 0, available, atTop, resume is null ? new FlowCursor(0, null) : (FlowCursor)resume, rules);
            return p.Complete ? FlowFit.Complete(p.Height, p) : FlowFit.Partial(p.Height, p, p.Next);
        }

        int pages = PageSequencer.Run(Body, FlowRules.Default, 10, n => 100, (n, fit) => seen.Add(n), m => new InvalidOperationException(m));
        Assert.Equal(3, pages);
        Assert.Equal([0, 1, 2], seen);
        Assert.Throws<InvalidOperationException>(() => PageSequencer.Run(Body, FlowRules.Default, 2, n => 100, (n, fit) => { }, m => new InvalidOperationException(m)));
    }
}
