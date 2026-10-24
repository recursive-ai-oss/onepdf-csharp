namespace OnePdf.Layout.Tests;

/// <summary>PDF/UA-1 and PDF/A level A on layout output, checked with the veraPDF validator as a black box.</summary>
public class PdfUATests
{
    private static readonly string[] Words = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "theta", "kappa"];

    private static string Paragraph(int seed, int words)
    {
        var rng = new Random(seed);
        return string.Join(' ', Enumerable.Range(0, words).Select(_ => Words[rng.Next(Words.Length)])) + ".";
    }

    /// <summary>Headings, paragraphs, a table with header cells over several pages, lists, figures with alternate text, links, a header and a footer with page totals.</summary>
    internal static LayoutDocument Sample(int tableRows = 60, Action<Column>? more = null)
    {
        var body = new Column { Spacing = 8 };
        body.Items.Add(new Heading { Level = 1, Child = new Text { Content = "Annual Report", Size = 20, Weight = 700 } });
        body.Items.Add(new Link { Uri = "https://example.com/report", Child = new Text { Content = "Visit the project page", Size = 10 } });
        body.Items.Add(new Link { Destination = "appendix", Child = new Text { Content = "Jump to the appendix", Size = 10 } });
        for (int i = 0; i < 10; i++)
        {
            body.Items.Add(new Text { Content = $"Paragraph {i}. " + Paragraph(i, 70), Size = 10 });
        }

        body.Items.Add(new Heading { Level = 2, Child = new Text { Content = "Figures", Size = 14, Weight = 700 } });
        body.Items.Add(new Image { Jpeg = Support.Jpeg(), Fit = ImageFit.Contain, AltText = "A red square" });
        body.Items.Add(new Image { Jpeg = Support.Jpeg(), Fit = ImageFit.Contain, Artifact = true });
        body.Items.Add(new Heading { Level = 2, Child = new Text { Content = "Items", Size = 14, Weight = 700 } });
        var inner = new ItemList { Kind = ListKind.Numbered, NumberStyle = NumberingStyle.LowerLetters };
        inner.Items.Add(new ListItem(new Text { Content = "Nested one", Size = 10 }));
        inner.Items.Add(new ListItem(new Text { Content = "Nested two", Size = 10 }));
        var list = new ItemList { Kind = ListKind.Bulleted };
        list.Items.Add(new ListItem(new Text { Content = "First entry", Size = 10 }));
        list.Items.Add(new ListItem(new Column { Items = { new Text { Content = "Second entry with a sublist", Size = 10 }, inner } }));
        body.Items.Add(list);
        body.Items.Add(new Anchor { Id = "appendix", Child = new Heading { Level = 2, Child = new Text { Content = "Appendix table", Size = 14, Weight = 700 } } });
        Table t = TableTests.Make(tableRows, 3, (r, c) => c == 0 ? $"R{r}" : $"cell {r}/{c} " + Paragraph(r + c, 2 + ((r + c) % 5)), header: 1, footer: 1);
        t.Rows[3].Cells[0] = new TableCell(new Text { Content = "Row header 3", Size = 10 }) { IsHeader = true };
        t.Rows[5].Cells[1] = new TableCell(new Text { Content = "Spanning two columns", Size = 10 }) { ColumnSpan = 2 };
        t.Rows[5].Cells.RemoveAt(2);
        body.Items.Add(t);
        more?.Invoke(body);

        var doc = new LayoutDocument { Title = "Annual Report", Language = "en-US", Author = "A. Writer", Subject = "Yearly figures", Keywords = "report, annual" };
        doc.Pages.Add(new LayoutPage
        {
            Size = new PageSize(320, 420),
            Margin = Insets.All(30),
            Header = new Box { Padding = new Insets(0, 0, 4, 0), BorderBottom = new Border(0.5, LayoutColor.Black), Child = new Text { Content = "Acme Corp running header", Size = 8 } },
            Footer = new Row { Spacing = 3, Items = { RowItem.Relative(1, new Spacer()), new RowItem(new Text { Content = "Page", Size = 8 }), new RowItem(new PageNumber { Size = 8 }), new RowItem(new Text { Content = "of", Size = 8 }), new RowItem(new PageNumber { Size = 8, Kind = PageNumberKind.DocumentTotal }) } },
            Content = body,
        });
        return doc;
    }

    private static byte[] Save(LayoutDocument layout, PdfSaveOptions options)
    {
        using var ms = new MemoryStream();
        layout.Save(ms, options);
        return ms.ToArray();
    }

    private static void AssertCompliant(byte[] pdf, string flavour)
    {
        List<string> failures = TagSupport.Vera(pdf, flavour);
        Assert.True(failures.Count == 0, flavour + ":\n" + string.Join("\n", failures));
    }

    [Fact]
    public void LayoutOutputPassesUa1()
    {
        byte[] pdf = Save(Sample(), new PdfSaveOptions { DocumentId = new byte[16], PdfUA = new PdfUAOptions() });
        AssertCompliant(pdf, "ua1");
    }

    [Theory]
    [InlineData(PdfAPart.Part2, "2a")]
    [InlineData(PdfAPart.Part3, "3a")]
    public void LayoutOutputPassesPdfALevelA(PdfAPart part, string flavour)
    {
        byte[] pdf = Save(Sample(), new PdfSaveOptions { DocumentId = new byte[16], PdfA = new PdfAOptions { Part = part, Conformance = PdfAConformance.A } });
        AssertCompliant(pdf, flavour);
    }

    [Theory]
    [InlineData(PdfAPart.Part2, "2a")]
    [InlineData(PdfAPart.Part3, "3a")]
    public void LayoutOutputPassesPdfAAndUaTogether(PdfAPart part, string flavour)
    {
        byte[] pdf = Save(Sample(), new PdfSaveOptions
        {
            DocumentId = new byte[16],
            PdfA = new PdfAOptions { Part = part, Conformance = PdfAConformance.A },
            PdfUA = new PdfUAOptions(),
        });
        AssertCompliant(pdf, flavour);
        AssertCompliant(pdf, "ua1");
    }

    [Fact]
    public void FigureWithoutAlternateTextIsRefused()
    {
        LayoutDocument layout = Sample(more: body => body.Items.Add(new Image { Jpeg = Support.Jpeg(), Fit = ImageFit.Contain }));
        using var ms = new MemoryStream();
        PdfUAException ex = Assert.Throws<PdfUAException>(() => layout.Save(ms, new PdfSaveOptions { PdfUA = new PdfUAOptions() }));
        Assert.Contains(ex.Violations, v => v.Checkpoint == "13");
        Assert.Single(ex.Violations);
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void MissingTitleAndLanguageAreRefusedUnlessTheOptionsSupplyTheLanguage()
    {
        LayoutDocument layout = Sample();
        layout.Title = null;
        layout.Language = null;
        using var ms = new MemoryStream();
        PdfUAException ex = Assert.Throws<PdfUAException>(() => layout.Save(ms, new PdfSaveOptions { PdfUA = new PdfUAOptions { Language = "de" } }));
        Assert.Contains(ex.Violations, v => v.Message.Contains("dc:title", StringComparison.Ordinal));
        Assert.DoesNotContain(ex.Violations, v => v.Message.Contains("/Lang", StringComparison.Ordinal));
    }

    [Fact]
    public void AuthorSubjectAndKeywordsReachTheInfoDictionaryAndTheXmpPacket()
    {
        LayoutDocument layout = Sample();
        using var streamed = new MemoryStream();
        layout.Save(streamed);
        using (PdfDocument plain = PdfDocument.Open(streamed.ToArray()))
        {
            Assert.Equal("A. Writer", plain.Info.Author);
            Assert.Equal("Yearly figures", plain.Info.Subject);
            Assert.Equal("report, annual", plain.Info.Keywords);
        }

        byte[] pdf = Save(layout, new PdfSaveOptions { PdfUA = new PdfUAOptions() });
        using PdfDocument back = PdfDocument.Open(pdf);
        Assert.Equal(["A. Writer"], back.Xmp!.Creators);
        Assert.Equal("Yearly figures", back.Xmp.Description);
        Assert.Equal("report, annual", back.Xmp.Keywords);
        Assert.Empty(back.CheckPdfUA());
    }

    [Fact]
    public void FluentComposerSetsTheDocumentProperties()
    {
        LayoutDocument layout = LayoutDocument.Compose(doc =>
        {
            doc.Title("T").Author("A").Subject("S").Keywords("K").Language("en");
            doc.Page(page => page.Content().Text("Hello"));
        });
        Assert.Equal(("A", "S", "K"), (layout.Author, layout.Subject, layout.Keywords));
        byte[] pdf = Save(layout, new PdfSaveOptions { PdfUA = new PdfUAOptions() });
        AssertCompliant(pdf, "ua1");
    }

    [Fact]
    public void SaveIsReproducible()
    {
        var options = new PdfSaveOptions { DocumentId = new byte[16], PdfUA = new PdfUAOptions() };
        Assert.Equal(Save(Sample(), options), Save(Sample(), options));
    }
}
