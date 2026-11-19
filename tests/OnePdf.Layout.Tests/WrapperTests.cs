namespace OnePdf.Layout.Tests;

/// <summary>ScaleToFit, Shrink and Unconstrained wrappers.</summary>
public class WrapperTests
{
    private const string Long = "The quick brown fox jumps over the lazy dog and keeps running through the field until the sun goes down behind the hills, " +
        "where the river bends twice before it reaches the sea. Nobody saw it return, though some say the barking carried all night.";

    private static LayoutDocument Doc(Action<ColumnDescriptor> col, Action<PageComposer>? page = null) =>
        LayoutDocument.Compose(doc => doc.Page(p =>
        {
            p.Size(300, 400);
            p.Margin(20);
            page?.Invoke(p);
            p.Content().Column(col);
        }));

    private static string Rep(string s, int n) => string.Join(' ', Enumerable.Repeat(s, n));

    [Fact]
    public void ScaleToFitShrinksATextBlockAndKeepsItReal()
    {
        string text = Rep(Long, 4);
        byte[] scaled = StyleSupport.Save(Doc(c => c.Item().Width(200).Height(60).ScaleToFit().Text(text)));
        StyleSupport.Capture cap = StyleSupport.Interpret(scaled);
        Assert.NotEmpty(cap.Runs);
        // Everything is inside the 200 x 60 box at the top left of the content area (page y runs up from the bottom).
        Assert.All(cap.Runs, r =>
        {
            Assert.InRange(r.X, 20 - 1e-6, 220 + 1e-6);
            Assert.InRange(r.Y, 400 - 20 - 60 - 1e-6, 400 - 20 + 1e-6);
            Assert.True(r.Size < 11 && r.Size > 0.5, "size " + r.Size);
        });
        Assert.Single(PdfPages(scaled));
        string plain = StyleSupport.Text(StyleSupport.Save(Doc(c => c.Item().Width(200).Text(text))));
        Assert.Equal(plain, StyleSupport.Text(scaled));
    }

    private static int PdfPagesCount(byte[] pdf)
    {
        using PdfDocument d = PdfDocument.Open(pdf);
        return d.Pages.Count;
    }

    private static int[] PdfPages(byte[] pdf) => [PdfPagesCount(pdf)];

    [Fact]
    public void ScaleToFitLeavesFittingContentAlone()
    {
        byte[] with = StyleSupport.Save(Doc(c => c.Item().Width(200).Height(60).ScaleToFit().Text("Short")));
        byte[] without = StyleSupport.Save(Doc(c => c.Item().Width(200).Height(60).Text("Short")));
        Assert.Equal(without, with);
        StyleSupport.Capture cap = StyleSupport.Interpret(with);
        Assert.All(cap.Runs, r => Assert.Equal(11, r.Size, 6));
    }

    [Fact]
    public void ScaleToFitNeverScalesUp()
    {
        StyleSupport.Capture cap = StyleSupport.Interpret(StyleSupport.Save(Doc(c => c.Item().Width(250).Height(300).ScaleToFit().Text("Tiny"))));
        Assert.All(cap.Runs, r => Assert.Equal(11, r.Size, 6));
    }

    [Fact]
    public void ScaleToFitFitsATableInsideTheBox()
    {
        var rows = Enumerable.Range(0, 40).Select(i => "Row" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        byte[] pdf = StyleSupport.Save(Doc(c => c.Item().Width(240).Height(150).ScaleToFit().Table(t =>
        {
            t.Columns(cols => { cols.Relative(); cols.Relative(); });
            t.CellBorder(0.5, Colors.Grey);
            foreach (string r in rows)
            {
                t.Row(row =>
                {
                    row.Cell().Text(r);
                    row.Cell().Text(r + "x");
                });
            }
        })));
        Assert.Equal(1, PdfPagesCount(pdf));
        string text = StyleSupport.Text(pdf);
        foreach (string r in rows)
        {
            Assert.Contains(r + " " + r + "x", text, StringComparison.Ordinal);
        }

        StyleSupport.Capture cap = StyleSupport.Interpret(pdf);
        Assert.All(cap.Runs, r => Assert.InRange(r.Y, 400 - 20 - 150 - 1e-6, 400 - 20 + 1e-6));
        Assert.All(cap.Paths, p => Assert.True(p.X1 <= 20 + 240 + 1e-3 && p.Y0 >= 400 - 20 - 150 - 1e-3));
    }

    [Fact]
    public void ScaleToFitScalesAPictureThatFillsTheWidth()
    {
        byte[] pdf = StyleSupport.Save(Doc(c => c.Item().Width(200).Height(50).ScaleToFit().Image(Support.Jpeg()).FitWidth()));
        StyleSupport.Capture cap = StyleSupport.Interpret(pdf);
        PdfMatrix m = Assert.Single(cap.Images);
        Assert.Equal(50, Math.Abs(m.A), 3);
        Assert.Equal(50, Math.Abs(m.D), 3);
    }

    [Fact]
    public void ScaleToFitKeepsLinksAndTags()
    {
        byte[] pdf = StyleSupport.Save(Doc(c => c.Item().Width(200).Height(40).ScaleToFit().Hyperlink("https://example.com/x").Text(Rep("linked words", 20))));
        using PdfDocument d = PdfDocument.Open(pdf);
        PdfAnnotation link = Assert.Single(d.Pages[0].Links);
        Assert.StartsWith("https://example.com/x", link.Action!.Uri, StringComparison.Ordinal);
        PdfRectangle r = link.Rect;
        Assert.InRange(r.Left, 20 - 1e-3, 21);
        Assert.InRange(r.Right, 200, 220 + 1e-3);
        Assert.InRange(r.Top, 400 - 20 - 1, 400 - 20 + 1e-3);
        Assert.InRange(r.Bottom, 400 - 20 - 40 - 1e-3, 400 - 20 - 5);
        Assert.NotNull(d.StructureTree);
        Assert.Contains("linked words", StyleSupport.Text(pdf), StringComparison.Ordinal);
    }

    [Fact]
    public void ShrinkMakesABorderedBoxHugItsText()
    {
        LayoutDocument wide = Doc(c => c.Item().Border(1).Padding(4).Text("Short"));
        LayoutDocument hug = Doc(c => c.Item().Shrink().Border(1).Padding(4).Text("Short"));
        double wideW = Support.Pages(wide, out _)[0].Children[0].Width;
        double hugW = Support.Pages(hug, out _)[0].Children[0].Width;
        Assert.Equal(260, wideW, 3);
        Assert.InRange(hugW, 30, 60);
        // The border rectangle is as wide as the box.
        StyleSupport.Capture cap = StyleSupport.Interpret(StyleSupport.Save(hug));
        double maxRight = cap.Paths.Max(p => p.X1);
        Assert.Equal(20 + hugW, maxRight, 2);
        Assert.Equal("Short", StyleSupport.Text(StyleSupport.Save(hug)));
    }

    [Fact]
    public void ShrinkNeverExceedsTheSpaceOffered()
    {
        LayoutDocument d = Doc(c => c.Item().Shrink().Border(1).Padding(4).Text(Rep(Long, 2)));
        Placed body = Support.Pages(d, out _)[0];
        Assert.Equal(260, body.Children[0].Width, 3);
    }

    [Fact]
    public void ShrinkSplitsAcrossPagesLikeItsContent()
    {
        LayoutDocument d = Doc(c => c.Item().Shrink().Column(col =>
        {
            for (int i = 0; i < 60; i++)
            {
                col.Item().Text("Line " + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }));
        byte[] pdf = StyleSupport.Save(d);
        Assert.True(PdfPagesCount(pdf) >= 2);
        string text = StyleSupport.Text(pdf);
        Assert.Contains("Line 59", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnconstrainedKeepsItsNaturalSizeAndTakesNoRoom()
    {
        static LayoutDocument Make(bool withWide) => Doc(c =>
        {
            c.Item().Text("Above");
            if (withWide)
            {
                c.Item().Unconstrained().Width(1000).Height(500).Background(Colors.LightGrey).Text("Wide");
            }

            c.Item().Text("Below");
        });
        List<Placed> with = Support.Pages(Make(true), out _);
        List<Placed> without = Support.Pages(Make(false), out _);
        Assert.Single(with);
        Placed wrapper = with[0].Children[1];
        Assert.IsType<Unconstrained>(wrapper.Element);
        Placed child = Assert.Single(wrapper.Children);
        Assert.Equal(1000, child.Width, 3);
        Assert.Equal(500, child.Height, 3);
        // The sibling below sits where it would without the wide element.
        Assert.Equal(without[0].Children[1].Y, with[0].Children[2].Y, 6);
        Assert.Equal(0, wrapper.Width);
        Assert.Equal(0, wrapper.Height);
        Assert.Equal("Above Wide Below", StyleSupport.Text(StyleSupport.Save(Make(true))));
    }

    [Fact]
    public void UnconstrainedCanReserveTheOfferedRoom()
    {
        LayoutDocument d = Doc(c =>
        {
            c.Item().Unconstrained(UnconstrainedSpace.Offered).Width(1000).Height(30).Text("Wide");
            c.Item().Text("Below");
        });
        List<Placed> pages = Support.Pages(d, out _);
        Placed wrapper = pages[0].Children[0];
        Assert.Equal(260, wrapper.Width, 3);
        Assert.Equal(30, wrapper.Height, 3);
        Assert.Equal(30, pages[0].Children[1].Y, 3);
    }

    [Fact]
    public void UnconstrainedDoesNotBreakPaginationOfSiblings()
    {
        LayoutDocument d = Doc(c =>
        {
            for (int i = 0; i < 80; i++)
            {
                c.Item().Text("Item " + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (i == 10)
                {
                    c.Item().Unconstrained().Width(5000).Height(5000).Text("Huge");
                }
            }
        });
        byte[] pdf = StyleSupport.Save(d);
        Assert.True(PdfPagesCount(pdf) >= 2);
        Assert.Contains("Item 79", StyleSupport.Text(pdf), StringComparison.Ordinal);
    }
}
