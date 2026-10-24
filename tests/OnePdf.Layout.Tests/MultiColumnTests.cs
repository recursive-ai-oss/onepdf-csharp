using OnePdf.Layout.Pagination;

namespace OnePdf.Layout.Tests;

public class MultiColumnTests
{
    private static readonly string[] Words = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "theta", "kappa", "lambda", "sigma"];

    private static string Words_(int seed, int words)
    {
        var rng = new Random(seed);
        return string.Join(' ', Enumerable.Range(0, words).Select(_ => Words[rng.Next(Words.Length)]));
    }

    private static Text Para(string label, int words, string? name = null) => new() { Content = label + " " + Words_(label.Sum(ch => ch), words), Size = 10, Name = name ?? label };

    private static MultiColumn Flow(int count = 2, ColumnFill fill = ColumnFill.Sequential, double gap = 20) => new() { Count = count, Gap = gap, Fill = fill };

    private static (List<Placed> Pages, Placed Node) One(Element flow, out double lineHeight)
    {
        List<Placed> pages = Support.Pages(Support.Single(flow), out _);
        lineHeight = Support.Walk(Support.Pages(Support.Single(new Text { Content = "x", Size = 10 }), out _)[0]).First(n => n.Element is Text).Height;
        return (pages, Support.Walk(pages[0]).First(n => ReferenceEquals(n.Element, flow)));
    }

    private static List<Placed> PiecesOf(List<Placed> pages, Element e) =>
        [.. pages.SelectMany(p => Support.Walk(p)).Where(n => ReferenceEquals(n.Element, e))];

    // The columns of a placed flow as lists of children in flow order: a new column starts when x moves.
    private static List<List<Placed>> Columns(Placed node)
    {
        var result = new List<List<Placed>>();
        double lastX = double.NaN;
        foreach (Placed child in node.Children)
        {
            if (result.Count == 0 || Math.Abs(child.X - lastX) > 1e-6)
            {
                result.Add([]);
                lastX = child.X;
            }

            result[^1].Add(child);
        }

        return result;
    }

    [Fact]
    public void SequentialFillGoesDownEachColumnThenAcross()
    {
        var flow = Flow();
        for (int i = 0; i < 12; i++)
        {
            flow.Items.Add(Para("P" + i, 40));
        }

        (List<Placed> pages, Placed node) = One(flow, out _);
        List<List<Placed>> cols = Columns(node);
        Assert.Equal(2, cols.Count);
        Assert.True(cols[1][0].X > cols[0][0].X);
        // Column width is (260 - 20) / 2.
        Assert.All(node.Children, c => Assert.True(c.Width <= 120 + 1e-6));
        // Order of the items follows the flow and each column is filled right to the bottom before the next starts.
        var names = node.Children.Select(c => c.Element.Name).Where(n => n is not null).ToList();
        Assert.Equal(names.OrderBy(n => int.Parse(n![1..], System.Globalization.CultureInfo.InvariantCulture)), names);
        Assert.True(cols[0][^1].Y + cols[0][^1].Height > 360 - 12 * 2, "the first column is full");
        Assert.True(pages.Count >= 1);
    }

    [Fact]
    public void FlowContinuesOnTheNextPageAfterTheLastColumn()
    {
        var flow = Flow();
        for (int i = 0; i < 40; i++)
        {
            flow.Items.Add(Para("P" + i, 40));
        }

        List<Placed> pages = Support.Pages(Support.Single(flow), out _);
        Assert.True(pages.Count >= 2);
        List<Placed> pieces = PiecesOf(pages, flow);
        Assert.Equal(pages.Count, pieces.Count);
        Assert.True(pieces[0].Continues);
        Assert.True(pieces[1].Continued);
        Assert.Equal(2, Columns(pieces[0]).Count);
        // Every paragraph is somewhere exactly once (a split one has pieces on neighbouring columns).
        for (int i = 0; i < 40; i++)
        {
            Assert.Contains(pages.SelectMany(p => Support.Walk(p)), n => n.Element.Name == "P" + i);
        }
    }

    [Fact]
    public void BalancedColumnsEndAtAboutTheSameHeight()
    {
        var flow = Flow(3, ColumnFill.Balanced);
        for (int i = 0; i < 9; i++)
        {
            flow.Items.Add(Para("P" + i, 12));
        }

        (_, Placed node) = One(flow, out double line);
        List<List<Placed>> cols = Columns(node);
        Assert.Equal(3, cols.Count);
        double[] bottoms = [.. cols.Select(c => c[^1].Y + c[^1].Height)];
        Assert.True(bottoms.Max() - bottoms.Min() <= line * 5 + 0.5, string.Join(",", bottoms));
        Assert.True(node.Height < 360 * 0.75, "balanced region is shorter than the page: " + node.Height);

        var sequential = Flow(3);
        sequential.Items.AddRange(flow.Items);
        (_, Placed seq) = One(sequential, out _);
        Assert.Equal(2, Columns(seq).Count);
        Assert.True(node.Height < seq.Height);
    }

    [Fact]
    public void BalancingIsBoundedAndNeverBreaksTheRulesToBalance()
    {
        // Three lines cannot be split (2 orphans + 2 widows): they stay in one column.
        var flow = Flow(2, ColumnFill.Balanced);
        flow.Items.Add(new Text { Content = "alpha beta gamma delta epsilon zeta theta kappa lambda sigma", Size = 10 });
        (_, Placed node) = One(flow, out double line);
        Assert.InRange(node.Height / line, 2.5, 3.5);
        Assert.Single(Columns(node));

        // One tall atomic item and a short one: the columns are as tall as the tall item.
        var tall = Flow(2, ColumnFill.Balanced);
        tall.Items.Add(new KeepTogether { Child = new Spacer { Height = 100 } });
        tall.Items.Add(new Text { Content = "short", Size = 10 });
        (_, Placed n2) = One(tall, out _);
        Assert.InRange(n2.Height, 100 - 0.5, 100 + line + 1);

        // Trial layouts are limited: count Fit calls with a content element that records its layouts.
        int layouts = 0;
        var counted = Flow(2, ColumnFill.Balanced);
        for (int i = 0; i < 30; i++)
        {
            counted.Items.Add(new CountingSpacer { Height = 10, Counter = () => layouts++ });
        }

        _ = One(counted, out _);
        Assert.True(layouts <= 30 * (1 + 1 + 12) * 2 + 60, "layouts: " + layouts);
    }

    private sealed class CountingSpacer : Element
    {
        public double Height { get; init; }

        public required Action Counter { get; init; }

        internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
        {
            Counter();
            return Height > c.Height ? ctx.NoFit(c, c.Width, Height) : FlowFit.Complete(Height, new Placed(this, 0, 0, c.Width, Height));
        }
    }

    [Fact]
    public void ParagraphsSplitBetweenColumnsWithWidowsAndOrphans()
    {
        // Sweep the filler so the paragraph meets the column bottom at every line offset.
        for (double fill = 280; fill < 350; fill += 3)
        {
            var flow = Flow();
            flow.Items.Add(new Spacer { Height = fill });
            var p = Para("Split", 70);
            flow.Items.Add(p);
            (List<Placed> pages, _) = One(flow, out double line);
            List<Placed> pieces = PiecesOf(pages, p);
            Assert.NotEmpty(pieces);
            if (pieces.Count > 1)
            {
                Assert.All(pieces, x => Assert.True(x.Height >= (2 * line) - 0.5, $"fill {fill}: piece of {x.Height / line:0.0} lines"));
            }
        }
    }

    [Fact]
    public void KeepTogetherMovesToTheNextColumnAndHeadingsNeverEndOne()
    {
        for (double fill = 250; fill < 345; fill += 5)
        {
            var flow = Flow();
            flow.Items.Add(new Spacer { Height = fill });
            var inner = new Column { Name = "K", Items = { Para("K1", 8), Para("K2", 8) } };
            flow.Items.Add(new KeepTogether { Child = inner });
            flow.Items.Add(new Heading { Level = 2, Child = new Text { Content = "Heading", Size = 14, Name = "H" } });
            flow.Items.Add(Para("After", 40, "A"));
            (List<Placed> pages, Placed node) = One(flow, out _);
            // Whole in one column.
            Assert.Single(PiecesOf(pages, inner));
            // The heading shares its column with the start of the paragraph.
            List<List<Placed>> cols = Columns(node);
            int hCol = cols.FindIndex(c => c.Any(n => Support.Walk(n).Any(x => x.Element.Name == "H")));
            int aCol = cols.FindIndex(c => c.Any(n => Support.Walk(n).Any(x => x.Element.Name == "A")));
            Assert.Equal(aCol, hCol);
        }
    }

    [Fact]
    public void ColumnBreakStartsTheNextColumnAndIsIgnoredAtTheTop()
    {
        var flow = Flow();
        flow.Items.Add(new ColumnBreak());
        flow.Items.Add(Para("A", 6));
        flow.Items.Add(new ColumnBreak());
        flow.Items.Add(Para("B", 6));
        flow.Items.Add(new ColumnBreak());
        (List<Placed> pages, Placed node) = One(flow, out _);
        Assert.Single(pages);
        List<List<Placed>> cols = Columns(node);
        Assert.Equal(2, cols.Count);
        Assert.Equal(0, cols[0][0].Y);
        Assert.Equal(0, cols[1][0].Y);
        Assert.Equal("A", cols[0][0].Element.Name);
        Assert.Equal("B", cols[1][0].Element.Name);

        // A break in the last column goes to the next page.
        var flow2 = Flow();
        flow2.Items.Add(Para("A", 6));
        flow2.Items.Add(new ColumnBreak());
        flow2.Items.Add(Para("B", 6));
        flow2.Items.Add(new ColumnBreak());
        flow2.Items.Add(Para("C", 6));
        Assert.Equal(2, Support.Pages(Support.Single(flow2), out _).Count);
    }

    [Fact]
    public void RightToLeftFillsFromTheRight()
    {
        var flow = Flow(3);
        flow.Direction = LayoutDirection.RightToLeft;
        flow.Items.Add(Para("A", 6));
        flow.Items.Add(new ColumnBreak());
        flow.Items.Add(Para("B", 6));
        flow.Items.Add(new ColumnBreak());
        flow.Items.Add(Para("C", 6));
        (_, Placed node) = One(flow, out _);
        Assert.Equal(["A", "B", "C"], node.Children.Select(c => c.Element.Name));
        Assert.True(node.Children[0].X > node.Children[1].X && node.Children[1].X > node.Children[2].X);
        double colW = (260 - 40) / 3.0;
        Assert.Equal(260 - colW, node.Children[0].X, 6);
    }

    [Fact]
    public void FullWidthNeighboursSitAboveAndBelowInTheSameFlow()
    {
        var flow = Flow(2, ColumnFill.Balanced);
        for (int i = 0; i < 6; i++)
        {
            flow.Items.Add(Para("P" + i, 15));
        }

        var body = new Column { Spacing = 6 };
        var top = new Text { Content = "Title across", Size = 16, Name = "Top" };
        var bottom = new Text { Content = "Footnote across", Size = 8, Name = "Bottom" };
        body.Items.Add(top);
        body.Items.Add(flow);
        body.Items.Add(bottom);
        List<Placed> pages = Support.Pages(Support.Single(body), out _);
        Assert.Single(pages);
        Placed t = Support.Walk(pages[0]).First(n => n.Element.Name == "Top");
        Placed f = Support.Walk(pages[0]).First(n => ReferenceEquals(n.Element, flow));
        Placed b = Support.Walk(pages[0]).First(n => n.Element.Name == "Bottom");
        Assert.True(f.Y >= t.Y + t.Height);
        Assert.True(b.Y >= f.Y + f.Height - 1e-6);
        Assert.Equal(260, f.Width);
        Assert.True(f.Height < 190, "balanced, not filling the page: " + f.Height);
    }

    [Fact]
    public void FlowStartedLowOnAPageFillsTheRestThenContinuesAtTheTop()
    {
        var flow = Flow();
        for (int i = 0; i < 14; i++)
        {
            flow.Items.Add(Para("P" + i, 40));
        }

        var body = new Column { Items = { new Spacer { Height = 200 }, flow } };
        List<Placed> pages = Support.Pages(Support.Single(body), out _);
        List<Placed> pieces = PiecesOf(pages, flow);
        Assert.True(pieces.Count >= 2);
        Assert.Equal(200, pieces[0].Y);
        Assert.Equal(0, pieces[1].Y);
        Assert.True(pieces[0].Height <= 160 + 1e-6);
    }

    [Fact]
    public void NestedInASectionOfItsOwnAndInABox()
    {
        var doc = new LayoutDocument();
        for (int s = 0; s < 2; s++)
        {
            var flow = Flow(s + 2, ColumnFill.Balanced, 10);
            for (int i = 0; i < 8; i++)
            {
                flow.Items.Add(Para($"S{s}P{i}", 20));
            }

            doc.Pages.Add(new LayoutPage { Size = new PageSize(300, 400), Margin = Insets.All(20), Content = new Box { Padding = Insets.All(5), Child = flow } });
        }

        List<Placed> pages = Support.Pages(doc, out _);
        Assert.Equal(2, pages.Count);
        Assert.Equal(2, Columns(Support.Walk(pages[0]).First(n => n.Element is MultiColumn)).Count);
        Assert.Equal(3, Columns(Support.Walk(pages[1]).First(n => n.Element is MultiColumn)).Count);
    }

    [Fact]
    public void TableSpansColumnsAndPagesWithRepeatedHeaders()
    {
        var flow = Flow(2, ColumnFill.Sequential, 16);
        flow.Items.Add(Para("Intro", 10));
        Table t = TableTests.Make(90, 2, header: 1);
        flow.Items.Add(t);
        flow.Items.Add(Para("Outro", 10));
        List<Placed> pages = Support.Pages(Support.Single(flow), out _);
        Assert.True(pages.Count >= 2);
        List<Placed> pieces = PiecesOf(pages, t);
        Assert.True(pieces.Count >= 4, "pieces: " + pieces.Count);
        int next = 0;
        foreach (Placed piece in pieces)
        {
            var data = (Table.Piece)piece.Data!;
            Assert.Equal(0, data.Rows[0].Row); // the header repeats
            int firstBody = data.Rows[1].Row;
            Assert.True(firstBody == 1 + next || firstBody == next + 1, $"body rows continue: {firstBody} after {next}");
            next = data.Rows[^1].Row;
        }

        Assert.Equal(90, next);
    }

    [Fact]
    public void RuleIsDrawnBetweenColumnsWithContentOnly()
    {
        var flow = Flow(3);
        flow.RuleWidth = 0.75;
        flow.Items.Add(Para("A", 6));
        flow.Items.Add(new ColumnBreak());
        flow.Items.Add(Para("B", 6));
        byte[] with = Support.Save(Support.Single(flow));
        flow.RuleWidth = 0;
        byte[] without = Support.Save(Support.Single(flow));
        Assert.NotEqual(with.Length, without.Length);
        using PdfDocument doc = PdfDocument.Open(with);
        Assert.Empty(doc.Diagnostics);
    }

    [Fact]
    public void UnboundedWidthFallsBackToOneColumn()
    {
        var flow = Flow();
        flow.Items.Add(Para("A", 6));
        flow.Items.Add(Para("B", 6));
        var row = new Row { Items = { new RowItem(flow), new RowItem(new Text { Content = "x", Size = 10 }) } };
        List<Placed> pages = Support.Pages(Support.Single(row), out List<LayoutDiagnostic> notes);
        Assert.Single(pages);
        Assert.Contains("single column", string.Join(" ", notes.Select(n => n.Message)), StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnCountIsValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MultiColumn { Count = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new MultiColumn { Count = MultiColumn.MaxColumns + 1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Slot().MultiColumn(c => c.Count(0)));
    }

    [Fact]
    public void OutputIsTheSameEveryTime()
    {
        byte[] Make()
        {
            var flow = Flow(3, ColumnFill.Balanced);
            flow.RuleWidth = 0.5;
            for (int i = 0; i < 25; i++)
            {
                flow.Items.Add(Para("P" + i, 30));
            }

            return Support.Save(Support.Single(flow));
        }

        Assert.Equal(Make(), Make());
    }

    // ---- Reading order ----

    private static LayoutDocument Numbered(int paragraphs, bool rtl = false)
    {
        var doc = new LayoutDocument { Title = "Columns", Language = "en-US" };
        var flow = Flow(2, ColumnFill.Balanced, 24);
        for (int i = 0; i < paragraphs; i++)
        {
            flow.Items.Add(new Text { Content = $"Para{i:00} " + Words_(i, 24), Size = 10 });
        }

        doc.Pages.Add(new LayoutPage { Size = new PageSize(320, 420), Margin = Insets.All(30), Content = flow });
        return doc;
    }

    [Fact]
    public void ExtractionAndStructureTreeFollowTheFlow()
    {
        byte[] pdf = Support.Save(Numbered(30));
        using PdfDocument doc = PdfDocument.Open(pdf);
        Assert.True(doc.Pages.Count >= 2);
        string all = string.Join(" ", doc.ExtractText().Select(t => Support.Normalize(t.Text)));
        int at = -1;
        for (int i = 0; i < 30; i++)
        {
            int found = all.IndexOf($"Para{i:00}", at + 1, StringComparison.Ordinal);
            Assert.True(found > at, $"Para{i:00} missing or out of order");
            at = found;
        }

        // Structure tree: the paragraphs in order, one P per paragraph.
        SNode tree = TagSupport.ReadTree(doc);
        List<SNode> ps = [.. tree.Descendants().Where(n => n.Type == "P")];
        Assert.Equal(30, ps.Count);
        var pageOf = ps.Select(p => p.Descendants().SelectMany(d => d.Content).First().Page).ToList();
        Assert.Equal(pageOf.OrderBy(x => x), pageOf);
    }

    [Fact]
    public void ParagraphSplitOverColumnsIsOneStructureElement()
    {
        var doc = new LayoutDocument { Title = "Columns", Language = "en-US" };
        var flow = Flow(2, ColumnFill.Sequential, 24);
        flow.Items.Add(new Spacer { Height = 300 });
        flow.Items.Add(new Text { Content = "Split " + Words_(1, 90), Size = 10 });
        doc.Pages.Add(new LayoutPage { Size = new PageSize(320, 420), Margin = Insets.All(30), Content = flow });
        using PdfDocument pdf = PdfDocument.Open(Support.Save(doc));
        SNode tree = TagSupport.ReadTree(pdf);
        SNode p = Assert.Single(tree.Descendants(), n => n.Type == "P");
        Assert.True(p.Content.Count >= 2, "content items: " + p.Content.Count);
    }

    [Fact]
    public void RightToLeftExtractionFollowsTheFlowRightToLeft()
    {
        var doc = new LayoutDocument { Title = "Columns", Language = "ar", Direction = LayoutDirection.RightToLeft };
        var flow = Flow(2, ColumnFill.Sequential, 24);
        for (int i = 0; i < 14; i++)
        {
            flow.Items.Add(new Text { Content = $"Item{i:00} " + Words_(i, 20), Size = 10 });
        }

        doc.Pages.Add(new LayoutPage { Size = new PageSize(320, 420), Margin = Insets.All(30), Content = flow });
        List<Placed> pages = Support.Pages(doc, out _);
        Placed node = Support.Walk(pages[0]).First(n => n.Element is MultiColumn);
        List<List<Placed>> cols = Columns(node);
        Assert.Equal(2, cols.Count);
        Assert.True(cols[0][0].X > cols[1][0].X);
        using PdfDocument pdf = PdfDocument.Open(Support.Save(doc));
        string all = string.Join(" ", pdf.ExtractText().Select(t => Support.Normalize(t.Text)));
        int at = -1;
        for (int i = 0; i < 14; i++)
        {
            int found = all.IndexOf($"Item{i:00}", at + 1, StringComparison.Ordinal);
            Assert.True(found > at, $"Item{i:00} missing or out of order");
            at = found;
        }
    }

    // ---- Conformance ----

    private static LayoutDocument Article() => PdfUATests.Sample(40, body =>
    {
        var flow = Flow(2, ColumnFill.Balanced, 18);
        flow.RuleWidth = 0.5;
        flow.Items.Add(new Heading { Level = 2, Child = new Text { Content = "In two columns", Size = 14, Weight = 700 } });
        for (int i = 0; i < 6; i++)
        {
            flow.Items.Add(new Text { Content = $"Column paragraph {i}. " + Words_(i, 60), Size = 10 });
        }

        flow.Items.Add(new Image { Jpeg = Support.Jpeg(), Fit = ImageFit.Contain, AltText = "A red square" });
        flow.Items.Add(TableTests.Make(12, 2, header: 1));
        body.Items.Add(flow);
    });

    [Fact]
    public void PdfA2bAndUa1StayCompliant()
    {
        using var a = new MemoryStream();
        Article().Save(a, new PdfSaveOptions { DocumentId = new byte[16], PdfA = new PdfAOptions { Part = PdfAPart.Part2, Conformance = PdfAConformance.B } });
        List<string> failures = TagSupport.Vera(a.ToArray(), "2b");
        Assert.True(failures.Count == 0, string.Join("\n", failures));

        using var u = new MemoryStream();
        Article().Save(u, new PdfSaveOptions { DocumentId = new byte[16], PdfUA = new PdfUAOptions() });
        failures = TagSupport.Vera(u.ToArray(), "ua1");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void EveryMarkIsTaggedOrAnArtifact()
    {
        byte[] pdf = Support.Save(Article());
        using PdfDocument doc = PdfDocument.Open(pdf);
        Assert.Empty(doc.Diagnostics);
        Assert.Empty(TagSupport.Untagged(doc));
    }
}
