namespace OnePdf.Layout.Tests;

public class TableTests
{
    internal static Table Make(int rows, int cols, Func<int, int, string>? text = null, int header = 1, int footer = 0, bool borders = true)
    {
        text ??= (r, c) => $"r{r}c{c} cell text";
        var t = new Table { CellPadding = Insets.All(3) };
        if (borders)
        {
            t.CellBorder = new Border(0.5, LayoutColor.Black);
        }

        for (int c = 0; c < cols; c++)
        {
            t.Columns.Add(c == 0 ? TableColumn.Constant(50) : TableColumn.Relative(1));
        }

        for (int h = 0; h < header; h++)
        {
            var hr = new TableRow { Background = new LayoutColor(0.85, 0.85, 0.85) };
            for (int c = 0; c < cols; c++)
            {
                hr.Cells.Add(new TableCell(new Text { Content = $"H{h}-{c}", Size = 10 }));
            }

            t.HeaderRows.Add(hr);
        }

        for (int r = 0; r < rows; r++)
        {
            var row = new TableRow();
            for (int c = 0; c < cols; c++)
            {
                row.Cells.Add(new TableCell(new Text { Content = text(r, c), Size = 10 }));
            }

            t.Rows.Add(row);
        }

        for (int f = 0; f < footer; f++)
        {
            var fr = new TableRow();
            for (int c = 0; c < cols; c++)
            {
                fr.Cells.Add(new TableCell(new Text { Content = $"F{f}-{c}", Size = 10 }));
            }

            t.FooterRows.Add(fr);
        }

        return t;
    }

    [Fact]
    public void TableSplitsAcrossPagesAndRepeatsHeaderAndFooter()
    {
        Table t = Make(60, 3, header: 1, footer: 1);
        t.RowBackground = i => i % 2 == 1 ? new LayoutColor(0.95, 0.95, 1) : null;
        LayoutDocument doc = Support.Single(new Column { Items = { new Text { Content = "Before the table", Size = 10 }, t } });
        byte[] pdf = Support.Save(doc);
        using PdfDocument back = PdfDocument.Open(pdf);
        Assert.True(back.Pages.Count >= 3, "pages " + back.Pages.Count);
        Assert.Empty(back.Diagnostics);
        int n = 0;
        foreach (OnePdf.Text.PdfPageText p in back.ExtractText())
        {
            string text = Support.Normalize(p.Text);
            Assert.Contains("H0-0", text, StringComparison.Ordinal);
            Assert.Contains("F0-2", text, StringComparison.Ordinal);
            n++;
        }

        Assert.Equal(back.Pages.Count, n);
        string all = string.Join(" ", back.ExtractText().Select(p => Support.Normalize(p.Text)));
        Assert.True(all.IndexOf("r0c0", StringComparison.Ordinal) < all.IndexOf("r59c2", StringComparison.Ordinal));
        string scratch = Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath();
        (int code, string output) = Support.Qpdf(pdf, scratch);
        Assert.True(code == 0, output);
    }

    [Fact]
    public void SpansBordersAndAlignment()
    {
        var t = new Table { CellPadding = Insets.All(4), CellBorder = new Border(0.5, LayoutColor.Black) };
        t.Columns.Add(TableColumn.Auto());
        t.Columns.Add(TableColumn.Relative(1));
        t.Columns.Add(TableColumn.Relative(2));
        t.Columns.Add(TableColumn.Constant(60));
        TableCell Cell(string s, int cs = 1, int rs = 1, LayoutColor? bg = null) => new(new Text { Content = s, Size = 10 }) { ColumnSpan = cs, RowSpan = rs, Background = bg };
        var h = new TableRow();
        h.Cells.Add(Cell("Name"));
        h.Cells.Add(new TableCell(new Text { Content = "Both middle columns", Size = 10 }) { ColumnSpan = 2, Background = new LayoutColor(0.8, 0.9, 1), BorderBottom = new Border(2, new LayoutColor(0, 0, 0.6)) });
        h.Cells.Add(Cell("End"));
        t.HeaderRows.Add(h);
        var r1 = new TableRow();
        r1.Cells.Add(Cell("Tall cell over two rows", rs: 2, bg: new LayoutColor(1, 0.95, 0.8)));
        r1.Cells.Add(Cell("b1 some longer text that wraps in the narrow relative column"));
        r1.Cells.Add(Cell("c1"));
        r1.Cells.Add(new TableCell(new Text { Content = "d1", Size = 10 }) { BorderStart = new Border(1, LayoutColor.Black, BorderStyle.Dashed) });
        var r2 = new TableRow();
        r2.Cells.Add(Cell("b2"));
        r2.Cells.Add(new TableCell(new Text { Content = "c2 middle", Size = 10 }) { VerticalAlignment = VerticalAlignment.Middle, ColumnSpan = 2 });
        t.Rows.Add(r1);
        t.Rows.Add(r2);
        var r3 = new TableRow { Background = new LayoutColor(0.9, 1, 0.9) };
        for (int i = 0; i < 4; i++)
        {
            r3.Cells.Add(Cell("x" + i));
        }

        t.Rows.Add(r3);
        LayoutDocument doc = Support.Single(t);
        byte[] pdf = Support.Save(doc, "spans");
        using PdfDocument back = PdfDocument.Open(pdf);
        Assert.Single(back.Pages);
        List<Placed> pages = Support.Pages(doc, out _);
        var piece = (Table.Piece)pages[0].Children[0].Data!;
        Assert.Equal(4, piece.Rows.Count);
        // The spanning cell is as tall as its two rows.
        Table.CellGeometry tall = piece.Cells.First(c => c.Slot == piece.State.Grid.RowStart[1]);
        Assert.Equal(piece.Rows[1].Height + piece.Rows[2].Height, tall.Height, 6);
    }
}
