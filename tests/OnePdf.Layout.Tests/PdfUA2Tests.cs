namespace OnePdf.Layout.Tests;

/// <summary>PDF/UA-2 on layout output, checked with the veraPDF validator's ua2 flavour as a black box.</summary>
public class PdfUA2Tests
{
    private static readonly PdfUAOptions Part2 = new() { Part = PdfUAPart.Part2 };

    private static byte[] Save(LayoutDocument layout, PdfSaveOptions options)
    {
        using var ms = new MemoryStream();
        layout.Save(ms, options);
        return ms.ToArray();
    }

    private static void AssertCompliant(byte[] pdf)
    {
        List<string> failures = TagSupport.Vera(pdf, "ua2");
        Assert.True(failures.Count == 0, "ua2:\n" + string.Join("\n", failures));
    }

    private static string Para(int n) => string.Concat(Enumerable.Repeat("Layout turns a tree of boxes, rows and paragraphs into pages. ", n)).Trim();

    /// <summary>Headings with bookmarks and anchors, a title, an aside, a footnote, lists, a table with column and row headers over pages, figures, links, inline objects and a table of contents.</summary>
    internal static LayoutDocument Rich()
    {
        return LayoutDocument.Compose(doc =>
        {
            doc.Title("Field guide").Author("A. Writer").Language("en-US");
            doc.Page(page =>
            {
                page.Size(new PageSize(320, 420));
                page.Margin(28);
                page.Header().Text("Field guide").FontSize(8);
                page.Footer().AlignEnd().Text(t => { t.FontSize(8); t.Span("Page "); t.CurrentPageNumber(); t.Span(" of "); t.TotalPages(); });
                page.Content().Column(col =>
                {
                    col.Spacing(6);
                    col.Item().Title().Text("The field guide").FontSize(22).Bold();
                    col.Item().TableOfContents(toc => toc.Levels(1, 2));
                    col.Item().PageBreak();
                    col.Item().Bookmark("Introduction").Anchor("intro").Heading(1).Text("Introduction").FontSize(16).Bold();
                    col.Item().Text(Para(6)).FontSize(10);
                    col.Item().Aside().Text("Aside: a sidebar that stands apart from the main text.").FontSize(9).Italic();
                    col.Item().Footnote().Text("1. A footnote with its own paragraph.").FontSize(8);
                    col.Item().Bookmark("Plants", 1).Heading(2).Text("Plants").FontSize(13).Bold();
                    col.Item().Text(t =>
                    {
                        t.FontSize(10);
                        t.Span("A picture ");
                        t.InlineImage(Support.Jpeg(), 12, 12).AltText("a red square").AlignTextBottom();
                        t.Span(", a badge ");
                        t.Inline(s => s.Background(LayoutColor.FromHex("#dddddd")).Padding(3, 0).Text("NEW").FontSize(8)).AltText("new").AlignMiddle();
                        t.Span(" and a link ");
                        t.Inline(s => s.Image(Support.Jpeg())).Width(10).Height(10).Hyperlink("https://example.com/").AltText("example link");
                        t.Span(" inside one paragraph.");
                    });
                    col.Item().AltText("A red square").Image(Support.Jpeg());
                    col.Item().Bookmark("Lists", 1).Heading(2).Text("Lists").FontSize(13).Bold();
                    col.Item().List(list =>
                    {
                        list.Numbered();
                        list.Item().Text("First");
                        list.Item().Column(c =>
                        {
                            c.Item().Text("Second, with a sublist");
                            c.Item().List(inner => { inner.Item().Text("Nested one"); inner.Item().Text("Nested two"); });
                        });
                    });
                    col.Item().LinkToAnchor("intro").Text("Back to the introduction").FontSize(10).Underline();
                    col.Item().Hyperlink("https://example.com/guide").Text("The guide on the web").FontSize(10).Underline();
                    col.Item().Bookmark("Species").Heading(1).Text("Species").FontSize(16).Bold();
                    col.Item().Table(table =>
                    {
                        table.Columns(c => { c.Relative(2); c.Relative(1); c.Relative(1); });
                        table.HeaderRow(r => { r.Cell().Text("Species").Bold(); r.Cell().Text("Height").Bold(); r.Cell().Text("Leaves").Bold(); });
                        for (int i = 0; i < 40; i++)
                        {
                            int n = i;
                            table.Row(r =>
                            {
                                r.Cell().RowHeader().Text($"Species {n}");
                                r.Cell().Text($"{n * 3} cm");
                                r.Cell().Text(n % 2 == 0 ? "simple" : "compound");
                            });
                        }
                    });
                });
            });
        });
    }

    [Fact]
    public void RichDocumentPassesUa2()
    {
        byte[] pdf = Save(Rich(), new PdfSaveOptions { DocumentId = new byte[16], PdfUA = Part2 });
        AssertCompliant(pdf);
        using PdfDocument back = PdfDocument.Open(pdf);
        Assert.Empty(back.CheckPdfUA(PdfUAPart.Part2));
        // Anchors, bookmarks and the table of contents go to structure elements, not to pages.
        Assert.NotEmpty(back.Outline);
        Assert.All(back.Outline, o => Assert.True(o.Destination is { PageIndex: -1 }, "a bookmark goes to a page"));
    }

    [Fact]
    public void PartOneRefusesTheTypesOnlyPartTwoHas()
    {
        LayoutDocument layout = LayoutDocument.Compose(doc =>
        {
            doc.Title("T").Language("en");
            doc.Page(page => page.Content().Column(c => { c.Item().Aside().Text("aside"); }));
        });
        using var ms = new MemoryStream();
        PdfUAException ex = Assert.Throws<PdfUAException>(() => layout.Save(ms, new PdfSaveOptions { PdfUA = new PdfUAOptions() }));
        Assert.Contains(ex.Violations, v => v.Message.Contains("Aside", StringComparison.Ordinal));
        AssertCompliant(Save(layout, new PdfSaveOptions { PdfUA = Part2 }));
    }

    [Fact]
    public void MissingTitleAndLanguageAreRefusedUnlessTheOptionsSupplyTheLanguage()
    {
        LayoutDocument layout = PdfUATests.Sample();
        layout.Title = null;
        layout.Language = null;
        using var ms = new MemoryStream();
        PdfUAException ex = Assert.Throws<PdfUAException>(() => layout.Save(ms, new PdfSaveOptions { PdfUA = new PdfUAOptions { Part = PdfUAPart.Part2 } }));
        Assert.Contains(ex.Violations, v => v.Message.Contains("dc:title", StringComparison.Ordinal) && v.Clause == "8.11.1");
        Assert.Contains(ex.Violations, v => v.Message.Contains("/Lang", StringComparison.Ordinal) && v.Clause == "8.4.4");
        Assert.All(ex.Violations, v => Assert.Equal(2, v.Part));
        Assert.Equal(0, ms.Length);

        PdfUAException withLanguage = Assert.Throws<PdfUAException>(() => layout.Save(ms, new PdfSaveOptions { PdfUA = new PdfUAOptions { Part = PdfUAPart.Part2, Language = "de" } }));
        Assert.DoesNotContain(withLanguage.Violations, v => v.Message.Contains("/Lang", StringComparison.Ordinal));
    }

    [Fact]
    public void FigureWithoutAlternateTextIsRefused()
    {
        LayoutDocument layout = PdfUATests.Sample(more: body => body.Items.Add(new Image { Jpeg = Support.Jpeg(), Fit = ImageFit.Contain }));
        using var ms = new MemoryStream();
        PdfUAException ex = Assert.Throws<PdfUAException>(() => layout.Save(ms, new PdfSaveOptions { PdfUA = Part2 }));
        PdfUAViolation only = Assert.Single(ex.Violations);
        Assert.Equal("8.2.5.28.2", only.Clause);
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void PdfAAndUa2CannotBeCombined()
    {
        using var ms = new MemoryStream();
        PdfAException ex = Assert.Throws<PdfAException>(() => PdfUATests.Sample().Save(ms, new PdfSaveOptions
        {
            PdfA = new PdfAOptions { Part = PdfAPart.Part2, Conformance = PdfAConformance.A },
            PdfUA = Part2,
        }));
        Assert.Contains(ex.Violations, v => v.Message.Contains("PDF 2.0", StringComparison.Ordinal));
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void OutputIsReproducibleAndPartOneIsUnchanged()
    {
        var options = new PdfSaveOptions { DocumentId = new byte[16], PdfUA = Part2 };
        Assert.Equal(Save(Rich(), options), Save(Rich(), options));

        // Part 1 output has no namespaces, no revision and no structure destinations.
        byte[] one = Save(PdfUATests.Sample(), new PdfSaveOptions { DocumentId = new byte[16], PdfUA = new PdfUAOptions() });
        using PdfDocument back = PdfDocument.Open(one);
        Assert.Equal("1.7", back.Version);
        Assert.Null(back.StructureTreeRoot!.GetArray("Namespaces"));
        Assert.Null(back.Xmp!.PdfUARevision);
    }

    [Fact]
    public void TheCommittedReferenceDocumentIsPdfUA2()
    {
        byte[] pdf = File.ReadAllBytes(Path.Combine(Support.Root(), "testdata", "layout-references", "documents", "accessible-ua2.pdf"));
        using PdfDocument doc = PdfDocument.Open(pdf);
        Assert.Equal("2.0", doc.Version);
        Assert.Empty(doc.CheckPdfUA(PdfUAPart.Part2));
        AssertCompliant(pdf);
    }

    [Fact]
    public void SampleDocumentPassesUa2()
    {
        byte[] pdf = Save(PdfUATests.Sample(), new PdfSaveOptions { DocumentId = new byte[16], PdfUA = Part2 });
        AssertCompliant(pdf);
    }
}
