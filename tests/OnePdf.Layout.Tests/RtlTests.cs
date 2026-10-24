namespace OnePdf.Layout.Tests;

public class RtlTests
{
    private const string Hello = "\u0645\u0631\u062D\u0628\u0627";          // marhaba
    private const string World = "\u0628\u0627\u0644\u0639\u0627\u0644\u0645"; // bil-alam
    private const string This = "\u0647\u0630\u0627";                         // hatha
    private const string Text = "\u0646\u0635";                               // nass
    private const string Arabic = "\u0639\u0631\u0628\u064A";                 // arabi

    private static LayoutDocument ArabicDocument()
    {
        var body = new Column { Spacing = 8 };
        body.Items.Add(new Heading { Level = 1, Child = new Text { Content = Hello + " " + World, Size = 16 } });
        body.Items.Add(new Text { Content = This + " " + Text + " " + Arabic, Size = 11, Name = "Start aligned" });
        body.Items.Add(new Text { Content = "left to right", Size = 11, Direction = LayoutDirection.LeftToRight, Name = "Marked LTR" });

        var boxes = new Row { Spacing = 6, Name = "Boxes" };
        foreach (string w in new[] { Hello, World, This })
        {
            boxes.Items.Add(RowItem.Constant(60, new Box { Background = new LayoutColor(0.9, 0.9, 1), Padding = Insets.All(4), Child = new Text { Content = w, Size = 11 } }));
        }

        body.Items.Add(boxes);
        body.Items.Add(new Row { Spacing = 6, Direction = LayoutDirection.LeftToRight, Name = "LTR boxes", Items = { RowItem.Constant(60, new Text { Content = "one", Size = 11 }), RowItem.Constant(60, new Text { Content = "two", Size = 11 }) } });

        var table = new Table { CellPadding = Insets.All(3), CellBorder = new Border(0.5, LayoutColor.Black), Name = "Table" };
        table.Columns.Add(TableColumn.Constant(70));
        table.Columns.Add(TableColumn.Constant(70));
        table.Columns.Add(TableColumn.Constant(70));
        var header = new TableRow();
        foreach (string w in new[] { Hello, World, This })
        {
            header.Cells.Add(new TableCell(new Text { Content = w, Size = 11 }));
        }

        table.HeaderRows.Add(header);
        for (int r = 0; r < 2; r++)
        {
            var row = new TableRow();
            foreach (string w in new[] { Text, Arabic, Hello })
            {
                row.Cells.Add(new TableCell(new Text { Content = w, Size = 11 }));
            }

            table.Rows.Add(row);
        }

        body.Items.Add(table);

        var list = new ItemList { Kind = ListKind.Bulleted, Name = "List" };
        list.Items.Add(new ListItem(new Text { Content = Hello + " " + World, Size = 11 }));
        list.Items.Add(new ListItem(new Text { Content = This + " " + Text, Size = 11 }));
        body.Items.Add(list);
        var numbered = new ItemList { Kind = ListKind.Numbered, Name = "Numbered" };
        for (int i = 0; i < 11; i++)
        {
            numbered.Items.Add(new ListItem(new Text { Content = Arabic, Size = 11 }));
        }

        body.Items.Add(numbered);

        var doc = new LayoutDocument { Direction = LayoutDirection.RightToLeft, Language = "ar", Title = "Arabic" };
        doc.Options.Fonts = ParaKit.Shared;
        doc.Pages.Add(new LayoutPage { Size = new PageSize(320, 560), Margin = Insets.All(30), Content = body });
        return doc;
    }

    private static Placed Find(Placed root, string name) => Support.Walk(root).First(p => p.Element.Name == name);

    [Fact]
    public void ContainersAreMirroredInPlacement()
    {
        LayoutDocument doc = ArabicDocument();
        List<Placed> pages = Support.Pages(doc, out _);
        Placed body = pages[0];
        double width = 260; // 320 - 2 * 30
        // A row runs right to left: the first item is rightmost. A row marked left to right inside runs the other way.
        Placed[] boxes = [.. Find(body, "Boxes").Children];
        Assert.Equal(3, boxes.Length);
        Assert.True(boxes[0].X > boxes[1].X && boxes[1].X > boxes[2].X, string.Join(",", boxes.Select(b => b.X)));
        Assert.Equal(width, boxes[0].X + boxes[0].Width, 0.01);
        Placed[] ltrBoxes = [.. Find(body, "LTR boxes").Children];
        Assert.True(ltrBoxes[0].X < ltrBoxes[1].X);

        // The table's first column is at the right.
        var piece = (Table.Piece)Find(body, "Table").Data!;
        Table.CellGeometry[] header = [.. piece.Cells.Take(3)];
        Assert.True(header[0].X > header[1].X && header[1].X > header[2].X);
        Assert.Equal(210, header[0].X + header[0].Width, 0.01);

        // List markers hang on the right, the content to their left.
        Placed list = Find(body, "List");
        foreach (Placed entry in list.Children)
        {
            Placed marker = entry.Children[0];
            Placed content = entry.Children[1];
            Assert.Equal(list.Width, marker.X + marker.Width, 0.01);
            Assert.True(content.X + content.Width <= marker.X + 0.01, "content ends before the marker column");
            Assert.Equal(0, content.X, 0.01);
        }
    }

    [Fact]
    public void WordsLandWhereRightToLeftReadersExpectThem()
    {
        LayoutDocument doc = ArabicDocument();
        byte[] bytes = Support.Save(doc, "rtl");
        using PdfDocument back = PdfDocument.Open(bytes);
        OnePdf.Text.PdfPageText page = back.Pages[0].ExtractText();
        OnePdf.Text.PdfTextWord[] words = [.. page.Words];
        OnePdf.Text.PdfTextWord W(string text) => words.First(w => w.Text == text);

        // Page is 320 wide with 30 margins: content spans 30..290.
        Assert.True(W(Hello).Bounds.Right > 280, "heading is flush right");
        Assert.True(W("left").Bounds.Left < 40, "text marked left to right is flush left");

        // The first column of the table (header row) is the rightmost word of the header row; the second box is left of the first.
        OnePdf.Text.PdfTextWord start = words.First(w => w.Text == This);
        Assert.True(start.Bounds.Right > 280, "start-aligned text is flush right");
        OnePdf.Text.PdfTextWord[] firstRow = [.. words.Where(w => w.Text == Hello)];
        Assert.True(firstRow.Length >= 3, "the word appears in heading, boxes, table and list");

        // Logical order survives in the extracted text.
        string text = Support.Normalize(page.Text);
        Assert.Contains(Hello + " " + World, text, StringComparison.Ordinal);
        Assert.Contains(This + " " + Text + " " + Arabic, text, StringComparison.Ordinal);
        Assert.Contains("left to right", text, StringComparison.Ordinal);

        // The tree follows the element tree and the language is Arabic.
        Assert.Equal("ar", back.Catalog!.GetString("Lang")?.ToString());
        Assert.Empty(TagSupport.Untagged(back));
    }

    [Fact]
    public void BoxInsetsBordersAndAlignmentFlip()
    {
        Element Make(LayoutDirection d) => new Box
        {
            Direction = d,
            PaddingStart = 20,
            PaddingEnd = 2,
            BorderStart = new Border(3, LayoutColor.Black),
            Child = new Text { Content = "x", Size = 11, Alignment = HorizontalAlignment.End },
        };

        LayoutDocument Doc(LayoutDirection d) => Support.Single(new Column { Items = { Make(d) } });
        Placed ltr = Support.Walk(Support.Pages(Doc(LayoutDirection.LeftToRight), out _)[0]).First(p => p.Element is Box);
        Placed rtl = Support.Walk(Support.Pages(Doc(LayoutDirection.RightToLeft), out _)[0]).First(p => p.Element is Box);
        double wl = ltr.Width;
        double wr = rtl.Width;
        Assert.Equal(wl, wr, 0.01);
        Placed childL = ltr.Children[0];
        Placed childR = rtl.Children[0];
        // 20 + 3 on the start side: left in LTR, right in RTL.
        Assert.Equal(23, childL.X, 0.01);
        Assert.Equal(2, childR.X, 0.01);
        Assert.Equal(childL.Width, childR.Width, 0.01);
    }
}
