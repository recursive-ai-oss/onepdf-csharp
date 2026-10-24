namespace OnePdf.Layout.Tests;

public class ListTests
{
    private static ListItem Item(string text) => new(new Text { Content = text, Size = 11 });

    [Fact]
    public void NumberFormatsAndHangingIndent()
    {
        var list = new ItemList { Kind = ListKind.Numbered, Start = 8, NumberStyle = NumberingStyle.Arabic };
        for (int i = 0; i < 4; i++)
        {
            list.Items.Add(Item($"Entry {i} with enough words to wrap onto a second line when the column is narrow, so the hanging indent shows"));
        }

        LayoutDocument doc = Support.Single(list, size: new PageSize(250, 300));
        List<Placed> pages = Support.Pages(doc, out _);
        Placed node = pages[0].Children[0];
        // Every entry's text starts at the same x, right of the widest marker ("11." is wider than "8.").
        double[] contentX = node.Children.Select(e => e.Children[^1].X).Distinct().ToArray();
        Assert.Single(contentX);
        Assert.True(contentX[0] > 10);
        // Markers end where the content column starts less the gap, whatever their own width.
        double[] markerEnd = node.Children.Select(e => e.Children[0].X + e.Children[0].Width).Distinct().ToArray();
        Assert.Single(markerEnd);
    }

    [Theory]
    [InlineData(NumberingStyle.LowerLetters, 1, "a")]
    [InlineData(NumberingStyle.LowerLetters, 27, "aa")]
    [InlineData(NumberingStyle.UpperLetters, 52, "AZ")]
    [InlineData(NumberingStyle.LowerRoman, 4, "iv")]
    [InlineData(NumberingStyle.UpperRoman, 1994, "MCMXCIV")]
    [InlineData(NumberingStyle.UpperRoman, 4000, "4000")]
    [InlineData(NumberingStyle.Arabic, 0, "0")]
    [InlineData(NumberingStyle.LowerRoman, -3, "-3")]
    public void NumbersAreWrittenInEachStyle(NumberingStyle style, int n, string expected) =>
        Assert.Equal(expected, Numbering.Format(n, style));

    [Fact]
    public void NestedListsCustomMarkersAndRoundTrip()
    {
        var inner = new ItemList { Kind = ListKind.Numbered, NumberStyle = NumberingStyle.LowerLetters, Suffix = ")" };
        inner.Items.Add(Item("sub one"));
        inner.Items.Add(Item("sub two"));
        var deeper = new ItemList();
        deeper.Items.Add(Item("deep bullet"));
        var outer = new ItemList();
        outer.Items.Add(Item("first"));
        outer.Items.Add(new ListItem(new Column { Items = { new Text { Content = "second with children", Size = 11 }, inner, deeper } }));
        outer.Items.Add(new ListItem(new Text { Content = "custom", Size = 11 }) { MarkerText = "\u2192" });
        var roman = new ItemList { Kind = ListKind.Numbered, NumberStyle = NumberingStyle.UpperRoman, Start = 3, MarkerFormat = n => $"[{n}]" };
        roman.Items.Add(Item("formatted"));
        LayoutDocument doc = Support.Single(new Column { Spacing = 6, Items = { outer, roman } });
        byte[] pdf = Support.Save(doc, "lists");
        using PdfDocument back = PdfDocument.Open(pdf);
        string text = Support.Normalize(back.ExtractText().Single().Text);
        Assert.Contains("second with children", text, StringComparison.Ordinal);
        Assert.Contains("a) sub one", text, StringComparison.Ordinal);
        Assert.Contains("b) sub two", text, StringComparison.Ordinal);
        Assert.Contains("[3] formatted", text, StringComparison.Ordinal);
        Assert.Contains("\u2022 first", text, StringComparison.Ordinal);
        Assert.Contains("\u2013 deep bullet", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LongListSplitsWithoutRepeatingMarkers()
    {
        var list = new ItemList { Kind = ListKind.Numbered };
        for (int i = 0; i < 120; i++)
        {
            list.Items.Add(Item($"Item number {i} " + string.Join(' ', Enumerable.Repeat("word", i % 17))));
        }

        LayoutDocument doc = Support.Single(list);
        byte[] pdf = Support.Save(doc);
        using PdfDocument back = PdfDocument.Open(pdf);
        Assert.True(back.Pages.Count > 2);
        string all = " " + string.Join(" ", back.ExtractText().Select(p => Support.Normalize(p.Text)));
        for (int i = 1; i <= 120; i++)
        {
            Assert.True(CountOf(all, $" {i}. Item number {i - 1}") == 1, $"entry {i}: {all[..Math.Min(300, all.Length)]}");
        }
    }

    private static int CountOf(string haystack, string needle)
    {
        int n = 0;
        int at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            n++;
            at += needle.Length;
        }

        return n;
    }
}
