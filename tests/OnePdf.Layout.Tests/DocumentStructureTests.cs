using System.Globalization;

namespace OnePdf.Layout.Tests;

public class DocumentStructureTests
{
    private static readonly string[] Words = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "theta", "kappa", "lambda", "sigma"];

    private static string Paragraph(int seed, int words)
    {
        var rng = new Random(seed);
        return string.Join(' ', Enumerable.Range(0, words).Select(_ => Words[rng.Next(Words.Length)])) + ".";
    }

    private static Box Heading1(string title, string? anchor, int level)
    {
        Element h = new Heading { Level = level + 1, Child = new Text { Content = title, Size = 18 - (level * 3), Weight = 700 } };
        h = new Bookmark { Title = title, Level = level, Child = h };
        if (anchor is not null)
        {
            h = new Anchor { Id = anchor, Child = h };
        }

        return new Box { Margin = new Insets(10, 0, 4, 0), Child = h };
    }

    private static Row Footer(bool sectionTotals)
    {
        var row = new Row { Spacing = 3 };
        row.Items.Add(RowItem.Relative(1, new Spacer()));
        row.Items.Add(new RowItem(new Text { Content = "Page", Size = 9 }));
        row.Items.Add(new RowItem(new PageNumber { Size = 9 }));
        row.Items.Add(new RowItem(new Text { Content = "of", Size = 9 }));
        row.Items.Add(new RowItem(new PageNumber { Size = 9, Kind = sectionTotals ? PageNumberKind.SectionTotal : PageNumberKind.DocumentTotal, ReservedDigits = sectionTotals ? 4 : 3 }));
        return row;
    }

    internal static (LayoutDocument Doc, int Rows) Report()
    {
        var body = new Column { Spacing = 8 };
        body.Items.Add(Heading1("Introduction", "intro", 0));
        body.Items.Add(new Link { Uri = "https://example.com/report?a=1", Child = new Text { Content = "Visit the project page", Size = 10 } });
        body.Items.Add(new Link { Destination = "appendix", Child = new Text { Content = "Jump to the appendix", Size = 10 } });
        for (int i = 0; i < 40; i++)
        {
            body.Items.Add(new Text { Content = Paragraph(i, 90), Size = 10 });
        }

        body.Items.Add(Heading1("Data", "data", 0));
        Table t = TableTests.Make(500, 4, (r, c) => c == 0 ? $"#{r}" : $"row {r} col {c} " + Paragraph(r * 4 + c, 1 + ((r + c) % 6)), header: 2, footer: 1);
        t.Columns[0] = TableColumn.Constant(40);
        // A cell spanning two columns in the first header row and one spanning two rows in the body.
        t.HeaderRows[0].Cells.Clear();
        t.HeaderRows[0].Cells.Add(new TableCell(new Text { Content = "Data table", Size = 10, Weight = 700 }) { ColumnSpan = 2 });
        t.HeaderRows[0].Cells.Add(new TableCell(new Text { Content = "Values", Size = 10, Weight = 700 }) { ColumnSpan = 2 });
        t.Rows[5].Cells.Clear();
        t.Rows[5].Cells.Add(new TableCell(new Text { Content = "tall", Size = 10 }) { RowSpan = 2 });
        for (int c = 1; c < 4; c++)
        {
            t.Rows[5].Cells.Add(new TableCell(new Text { Content = "five-" + c, Size = 10 }));
        }

        t.Rows[6].Cells.RemoveAt(0);
        t.RowBackground = i => i % 2 == 1 ? new LayoutColor(0.94, 0.94, 0.98) : null;
        body.Items.Add(t);

        body.Items.Add(Heading1("Notes", "notes", 0));
        body.Items.Add(Heading1("Lists", null, 1));
        var inner = new ItemList { Kind = ListKind.Numbered, NumberStyle = NumberingStyle.LowerLetters, Suffix = ")" };
        for (int i = 0; i < 6; i++)
        {
            inner.Items.Add(new ListItem(new Text { Content = "inner " + Paragraph(100 + i, 8), Size = 10 }));
        }

        var outer = new ItemList { Kind = ListKind.Numbered };
        for (int i = 0; i < 12; i++)
        {
            outer.Items.Add(new ListItem(new Column { Items = { new Text { Content = "outer " + Paragraph(200 + i, 20), Size = 10 }, inner } }));
        }

        body.Items.Add(outer);
        body.Items.Add(new Link { Destination = "intro", Child = new Text { Content = "Back to the introduction", Size = 10 } });

        var appendix = new Column { Spacing = 8 };
        appendix.Items.Add(Heading1("Appendix", "appendix", 0));
        for (int i = 0; i < 80; i++)
        {
            appendix.Items.Add(new Text { Content = Paragraph(300 + i, 120), Size = 11 });
        }

        appendix.Items.Add(new Link { Destination = "data", Child = new Text { Content = "Back to the data", Size = 10 } });

        var doc = new LayoutDocument();
        doc.Pages.Add(new LayoutPage
        {
            Size = PageSize.Letter,
            Margin = Insets.All(54),
            Content = body,
            FirstHeader = new Spacer(),
            Header = new Text { Content = "Annual report", Size = 9 },
            Footer = Footer(false),
        });
        doc.Pages.Add(new LayoutPage
        {
            Size = PageSize.Letter,
            Orientation = PageOrientation.Landscape,
            Margin = Insets.All(54),
            Content = appendix,
            Header = new Text { Content = "Annual report appendix", Size = 9 },
            Footer = Footer(true),
            NumberStyle = NumberingStyle.LowerRoman,
            StartNumber = 1,
            LabelPrefix = "A-",
        });
        return (doc, 300);
    }

    [Fact]
    public void FiftyPageReportRoundTrips()
    {
        (LayoutDocument layout, _) = Report();
        byte[] pdf = Support.Save(layout, "report");
        using PdfDocument doc = PdfDocument.Open(pdf);
        Assert.Empty(doc.Diagnostics);
        Assert.Empty(layout.Diagnostics);
        int pages = doc.Pages.Count;
        TestContext.Current.SendDiagnosticMessage($"report pages={pages} bytes={pdf.Length}");
        Assert.InRange(pages, 45, 70);

        var texts = doc.ExtractText().Select(p => Support.Normalize(p.Text)).ToList();
        Assert.Equal(pages, texts.Count);

        // Sections: portrait then landscape; page labels read back.
        int firstLandscape = Enumerable.Range(0, pages).First(i => doc.Pages[i].MediaBox.Width > doc.Pages[i].MediaBox.Height);
        Assert.True(firstLandscape > 20);
        for (int i = 0; i < pages; i++)
        {
            bool land = i >= firstLandscape;
            Assert.Equal(land, doc.Pages[i].MediaBox.Width > doc.Pages[i].MediaBox.Height);
        }

        int appendixPages = pages - firstLandscape;
        for (int i = 0; i < pages; i++)
        {
            string expectedLabel = i < firstLandscape ? (i + 1).ToString(CultureInfo.InvariantCulture) : "A-" + Numbering.Format(i - firstLandscape + 1, NumberingStyle.LowerRoman);
            Assert.Equal(expectedLabel, doc.Pages[i].Label);

            // "Page N of M" reads the right numbers: decimal against the document total, then roman against the section total.
            string expected = i < firstLandscape
                ? $"Page {i + 1} of {pages}"
                : $"Page {Numbering.Format(i - firstLandscape + 1, NumberingStyle.LowerRoman)} of {Numbering.Format(appendixPages, NumberingStyle.LowerRoman)}";
            Assert.True(texts[i].Contains(expected, StringComparison.Ordinal), $"page {i}: '{expected}' not in '{texts[i][^Math.Min(80, texts[i].Length)..]}'");
        }

        // The first page has no header (FirstHeader is empty); the others do.
        Assert.DoesNotContain("Annual report", texts[0], StringComparison.Ordinal);
        Assert.Contains("Annual report", texts[1], StringComparison.Ordinal);
        Assert.Contains("Annual report appendix", texts[firstLandscape], StringComparison.Ordinal);

        // Repeated table header and footer on every page of the table.
        var tablePages = Enumerable.Range(0, firstLandscape).Where(i => texts[i].Contains("Data table", StringComparison.Ordinal)).ToList();
        Assert.True(tablePages.Count >= 5, "table pages " + tablePages.Count);
        foreach (int i in tablePages)
        {
            Assert.Contains("Values", texts[i], StringComparison.Ordinal);
            Assert.Contains("F0-3", texts[i], StringComparison.Ordinal);
        }

        // Text comes in document order.
        string all = string.Join(" ", texts);
        int at = -1;
        foreach (string needle in new[] { "Introduction", "Visit the project page", "Data table", "row 0 col 1", "row 499 col 3", "outer", "Back to the introduction", "Appendix", "Back to the data" })
        {
            int found = all.IndexOf(needle, at + 1, StringComparison.Ordinal);
            Assert.True(found > at, $"'{needle}' missing or out of order");
            at = found;
        }

        // Links: the URI, a forward link to the appendix, a backward link to the introduction.
        int PageWith(string s) => texts.FindIndex(t => t.Contains(s, StringComparison.Ordinal));
        var links = doc.Pages.SelectMany(p => p.Links).ToList();
        Assert.Contains(links, l => l.Action?.Uri == "https://example.com/report?a=1");
        PdfAnnotation toAppendix = links.First(l => l.Action?.Destination is not null && l.Page.Index == 0);
        Assert.Equal(PageWith("Appendix") is var a && a >= firstLandscape ? a : -1, toAppendix.Action!.Destination!.PageIndex);
        PdfAnnotation toIntro = links.First(l => (l.Contents?.StartsWith("Back to the introduction", StringComparison.Ordinal) ?? false) && l.Action?.Destination is { } d && d.PageIndex == 0 && l.Page.Index > 0);
        Assert.True(toIntro.Page.Index < firstLandscape);
        PdfAnnotation toData = links.First(l => l.Page.Index >= firstLandscape && l.Action?.Destination is not null);
        Assert.Equal(PageWith("Data table"), toData.Action!.Destination!.PageIndex);

        // Outline: entries in order, "Lists" nested under "Notes", each going to the page of its heading.
        IReadOnlyList<PdfOutlineItem> outline = doc.Outline;
        Assert.Equal(["Introduction", "Data", "Notes", "Appendix"], outline.Select(o => o.Title).ToArray());
        Assert.Equal("Lists", Assert.Single(outline[2].Children).Title);
        Assert.Equal(0, outline[0].Destination!.PageIndex);
        Assert.Equal(PageWith("Appendix"), outline[3].Destination!.PageIndex);
        Assert.Equal(outline[2].Destination!.PageIndex, outline[2].Children[0].Destination!.PageIndex);

        // Other readers accept it.
        using UglyToad.PdfPig.PdfDocument pig = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Assert.Equal(pages, pig.NumberOfPages);
        string scratch = Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath();
        (int code, string output) = Support.Qpdf(pdf, scratch);
        Assert.True(code == 0, output);
    }

    [Fact]
    public void SameReportTwiceGivesTheSameBytes()
    {
        byte[] one = Support.Save(Report().Doc);
        byte[] two = Support.Save(Report().Doc);
        Assert.True(one.AsSpan().SequenceEqual(two));
    }

    [Fact]
    public void EvenPageHeadersAndRestartedNumbering()
    {
        var doc = new LayoutDocument();
        var col = new Column();
        for (int i = 0; i < 6; i++)
        {
            col.Items.Add(new Text { Content = "p" + i, Size = 12 });
            if (i < 5)
            {
                col.Items.Add(new PageBreak());
            }
        }

        doc.Pages.Add(new LayoutPage { Content = col, EvenHeader = new Text { Content = "EVEN", Size = 9 }, Header = new Text { Content = "ODD", Size = 9 }, Footer = new PageNumber { Size = 9 } });
        doc.Pages.Add(new LayoutPage { Content = new Text { Content = "tail" }, StartNumber = 20, Footer = new PageNumber { Size = 9 } });
        byte[] pdf = Support.Save(doc);
        using PdfDocument back = PdfDocument.Open(pdf);
        Assert.Equal(7, back.Pages.Count);  // six pages and the tail
        string[] t = back.ExtractText().Select(p => Support.Normalize(p.Text)).ToArray();
        Assert.StartsWith("ODD", t[0], StringComparison.Ordinal);
        Assert.StartsWith("EVEN", t[1], StringComparison.Ordinal);
        Assert.Contains("tail", t[6], StringComparison.Ordinal);
        Assert.EndsWith("20", t[6], StringComparison.Ordinal);
        Assert.Equal("20", back.Pages[6].Label);
        Assert.Equal("6", back.Pages[5].Label);
    }

    [Fact]
    public void RenderToDocumentKnowsTotalsAndSkipsNavigation()
    {
        (LayoutDocument layout, _) = Report();
        using PdfDocument target = PdfDocument.CreateEmpty();
        int pages = layout.Render(target);
        Assert.Equal(pages, target.Pages.Count);
        Assert.Contains(layout.Diagnostics, d => d.Message.Contains("cannot record links", StringComparison.Ordinal));
        using var saved = new MemoryStream();
        target.Save(saved);
        using PdfDocument back = PdfDocument.Open(saved.ToArray());
        string last = Support.Normalize(back.ExtractText().ToList()[^1].Text);
        Assert.Contains(" of ", last, StringComparison.Ordinal);
    }
}
