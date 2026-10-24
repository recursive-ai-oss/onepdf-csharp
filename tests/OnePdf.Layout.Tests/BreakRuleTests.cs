namespace OnePdf.Layout.Tests;

public class BreakRuleTests
{
    private static Text Line(string s, double size = 12) => new() { Content = s, Size = size };

    [Fact]
    public void CellThatCannotFitAnyPageNamesItsPath()
    {
        Table t = TableTests.Make(40, 3);
        t.Rows[40 - 1].Cells[2] = new TableCell(new Box { Name = "Tall", Height = 5000 });
        var doc = new LayoutDocument();
        doc.Pages.Add(new LayoutPage { Size = new PageSize(300, 400), Margin = Insets.All(20), Content = t });
        PdfLayoutException ex = Assert.Throws<PdfLayoutException>(() => Support.Save(doc));
        Assert.Matches(@"^Page\[[1-9]\d*\] > Content > Table > Row 39 > Cell 2 > Tall$", ex.ElementPath);
    }

    [Fact]
    public void RowGroupTallerThanAPageRaises()
    {
        var t = new Table();
        t.Columns.Add(TableColumn.Constant(100));
        t.Columns.Add(TableColumn.Constant(100));
        var r1 = new TableRow { Cells = { new TableCell(new Spacer { Height = 300, Name = "A" }) { RowSpan = 2 }, new TableCell(Line("b")) } };
        var r2 = new TableRow { Cells = { new TableCell(new Spacer { Height = 400, Name = "C" }) } };
        t.Rows.Add(r1);
        t.Rows.Add(r2);
        // Page body is 360 tall: the two spanned rows need 300 + 300 only for their own cells, but the group cannot split.
        r2.Cells[0] = new TableCell(new Spacer { Height = 400, Name = "C" });
        PdfLayoutException ex = Assert.Throws<PdfLayoutException>(() => Support.Save(Support.Single(t)));
        Assert.Contains("> Table > Row ", ex.ElementPath, StringComparison.Ordinal);
    }

    [Fact]
    public void HeaderAndFooterThatLeaveNoRoomRaise()
    {
        Table t = TableTests.Make(3, 2, header: 1, footer: 1);
        t.HeaderRows[0].Cells[0] = new TableCell(new Spacer { Height = 200 });
        t.FooterRows[0].Cells[0] = new TableCell(new Spacer { Height = 200 });
        PdfLayoutException ex = Assert.Throws<PdfLayoutException>(() => Support.Save(Support.Single(t)));
        Assert.Contains("Table", ex.ElementPath, StringComparison.Ordinal);
    }

    [Fact]
    public void HeadingNeverEndsAPage()
    {
        // Fill the page so that the heading would be the last thing on it, then check it moves with its paragraph.
        for (double fill = 280; fill < 360; fill += 7)
        {
            var col = new Column();
            col.Items.Add(new Spacer { Height = fill });
            col.Items.Add(new Heading { Child = new Text { Content = "Heading", Size = 14, Name = "H" } });
            col.Items.Add(new Text { Content = "Paragraph " + string.Join(' ', Enumerable.Repeat("word", 60)), Size = 12, Name = "P" });
            List<Placed> pages = Support.Pages(Support.Single(col), out _);
            int hPage = pages.FindIndex(p => Support.Walk(p).Any(n => n.Element.Name == "H"));
            int pPage = pages.FindIndex(p => Support.Walk(p).Any(n => n.Element.Name == "P"));
            Assert.Equal(hPage, pPage);
        }
    }

    [Fact]
    public void KeepTogetherAndEnsureSpaceMoveTablesAndLists()
    {
        Table t = TableTests.Make(4, 2, header: 1);
        var list = new ItemList { Kind = ListKind.Numbered };
        for (int i = 0; i < 4; i++)
        {
            list.Items.Add(new ListItem(Line("entry " + i)));
        }

        foreach (Element e in new Element[] { t, list })
        {
            for (double fill = 200; fill < 330; fill += 9)
            {
                var col = new Column { Items = { new Spacer { Height = fill }, new KeepTogether { Child = e } } };
                List<Placed> pages = Support.Pages(Support.Single(col), out _);
                // The element is whole on one page: only one page holds it.
                Assert.Single(pages, p => Support.Walk(p).Any(n => ReferenceEquals(n.Element, e)));
            }

            var ensured = new Column { Items = { new Spacer { Height = 330 }, new EnsureSpace { Height = 100, Child = e } } };
            List<Placed> after = Support.Pages(Support.Single(ensured), out _);
            Assert.DoesNotContain(Support.Walk(after[0]), n => ReferenceEquals(n.Element, e));
        }
    }

    [Fact]
    public void TableKeepWithNextStaysWithTheParagraphAfterIt()
    {
        Table t = TableTests.Make(3, 2, header: 0);
        t.KeepWithNext = true;
        for (double fill = 200; fill < 340; fill += 11)
        {
            var col = new Column { Items = { new Spacer { Height = fill }, t, new Text { Content = "After the table", Name = "after" } } };
            List<Placed> pages = Support.Pages(Support.Single(col), out _);
            int tPage = pages.FindLastIndex(p => Support.Walk(p).Any(n => ReferenceEquals(n.Element, t)));
            int aPage = pages.FindIndex(p => Support.Walk(p).Any(n => n.Element.Name == "after"));
            Assert.Equal(tPage, aPage);
        }
    }

    [Fact]
    public void MeasuredRowsLimitsWhichRowsSizeAutomaticColumns()
    {
        Table Make(int measured)
        {
            var t = new Table { MeasuredRows = measured };
            t.Columns.Add(TableColumn.Auto());
            t.Rows.Add(new TableRow { Cells = { new TableCell(Line("a")) } });
            t.Rows.Add(new TableRow { Cells = { new TableCell(Line("a much wider cell in the second row")) } });
            return t;
        }

        double Width(int measured)
        {
            List<Placed> pages = Support.Pages(Support.Single(new Row { Items = { new RowItem(Make(measured)) } }), out _);
            return Support.Walk(pages[0]).First(n => n.Element is Table).Width;
        }

        Assert.True(Width(int.MaxValue) > Width(1) + 50);
    }

    [Fact]
    public void RightToLeftTableMirrorsItsColumns()
    {
        Table Make()
        {
            Table t = TableTests.Make(2, 3, header: 0);
            t.Columns.Clear();
            t.Columns.Add(TableColumn.Constant(40));
            t.Columns.Add(TableColumn.Constant(60));
            t.Columns.Add(TableColumn.Constant(80));
            return t;
        }

        Placed Cell(LayoutDirection dir, int col)
        {
            Table t = Make();
            List<Placed> pages = Support.Pages(Support.Single(t, setup: p => p.Direction = dir), out _);
            var piece = (Table.Piece)Support.Walk(pages[0]).First(n => n.Element == t).Data!;
            return piece.Cells[col].Content!;
        }

        double ltr = Cell(LayoutDirection.LeftToRight, 0).X;
        double rtl = Cell(LayoutDirection.RightToLeft, 0).X;
        Assert.True(ltr < 10);
        Assert.True(rtl > 120, "first column should be at the right: " + rtl);
    }
}
