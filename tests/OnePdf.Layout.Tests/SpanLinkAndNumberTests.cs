using OnePdf.Text;

namespace OnePdf.Layout.Tests;

/// <summary>Links on spans of a text, and page numbers inside paragraphs that wrap and split across pages.</summary>
public class SpanLinkAndNumberTests
{
    private const string External = "an external link that is long enough to wrap over several lines of the paragraph";
    private const string Internal = "an internal one that also wraps around the end of a line";

    private static LayoutDocument LinkDoc(bool justify = true) => LayoutDocument.Compose(doc =>
    {
        doc.Title("Span links").Language("en");
        doc.Page(page =>
        {
            page.Size(300, 300);
            page.Margin(20);
            page.Content().Column(c =>
            {
                c.Item().Text(t =>
                {
                    if (justify)
                    {
                        t.Justify();
                    }

                    t.Span("Before the first link there is some ordinary text and then ");
                    t.Span(External).Hyperlink("https://example.com/a?b=1");
                    t.Span(" and after it more text, then ");
                    t.Span(Internal).LinkToAnchor("target");
                    t.Span(" and a long closing sentence so that the paragraph ends with a few lines of plain text.");
                });
                c.Item().PageBreak();
                c.Item().Anchor("target").Text("Target of the internal link");
            });
        });
    });

    private static byte[] SaveUa(LayoutDocument layout)
    {
        using var ms = new MemoryStream();
        layout.Save(ms, new PdfSaveOptions { DocumentId = new byte[16], PdfUA = new PdfUAOptions() });
        return ms.ToArray();
    }

    // The glyphs of one span: found by their text (without spaces) in the page's glyph sequence, with the glyph just before and just after.
    private static (List<PdfTextGlyph> Span, PdfTextGlyph Before, PdfTextGlyph After) GlyphsOf(PdfDocument doc, int page, string span)
    {
        List<PdfTextGlyph> all = [.. doc.ExtractText(new PdfTextOptions { RetainGlyphs = true })[page].ReadGlyphs().Where(g => !string.IsNullOrWhiteSpace(g.Text))];
        string flat = string.Concat(all.Select(g => g.Text));
        string needle = span.Replace(" ", string.Empty, StringComparison.Ordinal);
        int at = flat.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(at > 0, "the span is on the page");
        return (all.GetRange(at, needle.Length), all[at - 1], all[at + needle.Length]);
    }

    private static bool Inside(PdfRectangle r, PdfTextGlyph g)
    {
        double x = (g.Bounds.Left + g.Bounds.Right) / 2;
        double y = (g.Bounds.Bottom + g.Bounds.Top) / 2;
        return x >= r.Left && x <= r.Right && y >= r.Bottom && y <= r.Top;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnExternalAndAnInternalLinkCoverTheirSpansOnEveryLine(bool justify)
    {
        byte[] pdf = Support.Save(LinkDoc(justify));
        using PdfDocument doc = PdfDocument.Open(pdf);
        var links = doc.Pages.SelectMany(p => p.Links).ToList();
        var external = links.Where(l => l.Action?.Uri == "https://example.com/a?b=1").ToList();
        var inner = links.Where(l => l.Action?.Destination is not null).ToList();
        Assert.True(external.Count >= 2, "the external span wraps");
        Assert.True(inner.Count >= 2, "the internal span wraps");
        Assert.All(inner, l => Assert.Equal(1, l.Action!.Destination!.PageIndex));
        Assert.Equal(links.Count, external.Count + inner.Count);

        foreach ((string span, List<PdfAnnotation> mine, List<PdfAnnotation> other) in new[] { (External, external, inner), (Internal, inner, external) })
        {
            (List<PdfTextGlyph> glyphs, PdfTextGlyph before, PdfTextGlyph after) = GlyphsOf(doc, 0, span);
            Assert.All(glyphs, g => Assert.Contains(mine, l => Inside(l.Rect, g)));
            Assert.DoesNotContain(mine, l => Inside(l.Rect, before) || Inside(l.Rect, after));

            // One rectangle per line, none wider than the column.
            Assert.Equal(glyphs.Select(g => Math.Round(g.Origin.Y, 1)).Distinct().Count(), mine.Count);
            Assert.All(mine, l => Assert.True(l.Rect.Width <= 260.01));
            Assert.All(mine, l => Assert.Contains(l.Contents!, span, StringComparison.Ordinal));
            Assert.DoesNotContain(other, l => glyphs.Any(g => Inside(l.Rect, g)));
        }
    }

    [Fact]
    public void SpanLinksAreLinkElementsWithObjectReferencesAndDescriptions()
    {
        byte[] pdf = Support.Save(LinkDoc());
        using PdfDocument doc = PdfDocument.Open(pdf);
        SNode root = TagSupport.ReadTree(doc);
        var linkNodes = root.Descendants().Where(n => n.Type == "Link").ToList();
        int annots = doc.Pages.SelectMany(p => p.Links).Count();
        Assert.Equal(annots, linkNodes.Count);
        Assert.All(linkNodes, n =>
        {
            Assert.Single(n.Objects);
            Assert.NotEmpty(n.Content);
            Assert.Equal("P", n.Parent!.Type);
        });
        Assert.Empty(TagSupport.Untagged(doc));
        Assert.All(doc.Pages.SelectMany(p => p.Links), l => Assert.False(string.IsNullOrWhiteSpace(l.Contents)));
        Assert.Equal(pdf, Support.Save(LinkDoc()));
    }

    [Fact]
    public void SpanLinksPassVeraPdfUA1()
    {
        byte[] pdf = SaveUa(LinkDoc());
        Assert.Empty(TagSupport.Vera(pdf, "ua1"));
    }

    [Fact]
    public void ALinkedSpanBrokenByAPageBreakHasRectanglesOnBothPages()
    {
        LayoutDocument layout = LayoutDocument.Compose(doc =>
        {
            doc.Title("Split").Language("en");
            doc.Page(page =>
            {
                page.Size(300, 140);
                page.Margin(20);
                page.Content().Text(t =>
                {
                    t.Span(string.Join(' ', Enumerable.Repeat("ordinary words fill the paragraph", 6)) + " ");
                    t.Span(string.Join(' ', Enumerable.Repeat("linked words that run on", 14))).Hyperlink("https://example.com/long");
                    t.Span(" " + string.Join(' ', Enumerable.Repeat("trailing words end it", 4)));
                });
            });
        });
        byte[] pdf = SaveUa(layout);
        using PdfDocument doc = PdfDocument.Open(pdf);
        Assert.True(doc.Pages.Count >= 2);
        var pagesWithLinks = doc.Pages.Where(p => p.Links.Any(l => l.Action?.Uri == "https://example.com/long")).Select(p => p.Index).ToList();
        Assert.True(pagesWithLinks.Count >= 2, "links on at least two pages");

        // Every glyph of the linked words is inside a rectangle on its own page, and nothing else is.
        int covered = 0;
        IReadOnlyList<PdfPageText> texts = doc.ExtractText(new PdfTextOptions { RetainGlyphs = true });
        for (int p = 0; p < doc.Pages.Count; p++)
        {
            var links = doc.Pages[p].Links.ToList();
            covered += texts[p].ReadGlyphs().Count(g => !string.IsNullOrWhiteSpace(g.Text) && links.Any(l => Inside(l.Rect, g)));
        }

        Assert.Equal(14 * "linkedwordsthatrunon".Length, covered);
        Assert.Empty(TagSupport.Vera(pdf, "ua1"));
    }

    [Fact]
    public void ALinkInsideALinkElementKeepsOneStructureLink()
    {
        LayoutDocument layout = LayoutDocument.Compose(doc =>
        {
            doc.Title("Nested").Language("en");
            doc.Page(page =>
            {
                page.Size(300, 200);
                page.Margin(20);
                page.Content().Hyperlink("https://example.com/outer").Text(t =>
                {
                    t.Span("outer text ");
                    t.Span("with an inner span").Hyperlink("https://example.com/inner");
                });
            });
        });
        byte[] pdf = SaveUa(layout);
        using PdfDocument doc = PdfDocument.Open(pdf);
        SNode root = TagSupport.ReadTree(doc);
        Assert.Equal(2, doc.Pages[0].Links.Count);
        Assert.Equal(1, root.Descendants().Count(n => n.Type == "Link" && n.Parent!.Type != "Link"));
        Assert.Empty(TagSupport.Vera(pdf, "ua1"));
    }

    // ---- page numbers in wrapping paragraphs ----

    private static string Words(int count, string prefix = "w") => string.Join(' ', Enumerable.Range(0, count).Select(i => prefix + i));

    private static LayoutDocument NumberDoc(Action<TextDescriptor> compose, bool justify = true, double height = 160) => LayoutDocument.Compose(doc =>
    {
        doc.Title("Numbers").Language("en");
        doc.Page(page =>
        {
            page.Size(300, height).Portrait();
            page.Margin(20);
            page.Content().Text(t =>
            {
                if (justify)
                {
                    t.Justify();
                }

                compose(t);
            });
        });
    });

    [Fact]
    public void AParagraphWithPageNumbersWrapsSplitsAndShowsTheNumberOfEachPage()
    {
        // The marker "see page N of M" sits once per about four lines, so every page has some.
        LayoutDocument layout = NumberDoc(t =>
        {
            for (int i = 0; i < 12; i++)
            {
                t.Span(Words(14, "x" + i + "_") + " see page ");
                t.CurrentPageNumber();
                t.Span(" of ");
                t.TotalPages();
                t.Span(" ");
            }
        });
        byte[] pdf = Support.Save(layout);
        using PdfDocument doc = PdfDocument.Open(pdf);
        int pages = doc.Pages.Count;
        Assert.True(pages >= 4, "pages: " + pages);
        int seen = 0;
        for (int p = 0; p < pages; p++)
        {
            string text = Support.Normalize(doc.ExtractText()[p].Text);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"see page (\d+) of (\d+)"))
            {
                Assert.Equal((p + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), m.Groups[1].Value);
                Assert.Equal(pages.ToString(System.Globalization.CultureInfo.InvariantCulture), m.Groups[2].Value);
                seen++;
            }
        }

        // A marker may be split by a line end ("see page" / "3 of 8"), so only most of them are found whole.
        Assert.True(seen >= 8, "markers read: " + seen);
        Assert.Empty(TagSupport.Untagged(doc));
        Assert.Equal(pdf, Support.Save(layout));
    }

    [Fact]
    public void NumbersInAJustifiedParagraphLeaveLinesFlushAtBothEdges()
    {
        byte[] pdf = Support.Save(NumberDoc(t =>
        {
            t.Span(Words(30) + " on page ");
            t.CurrentPageNumber();
            t.Span(" " + Words(40, "y"));
        }, height: 300));
        using PdfDocument doc = PdfDocument.Open(pdf);
        PdfPageText page = doc.ExtractText(new PdfTextOptions { RetainGlyphs = true })[0];
        var lines = page.ReadGlyphs().Where(g => !string.IsNullOrWhiteSpace(g.Text)).GroupBy(g => Math.Round(g.Origin.Y, 1)).ToList();
        Assert.True(lines.Count >= 4);
        foreach (var line in lines.Take(lines.Count - 1))
        {
            Assert.InRange(line.Min(g => g.Bounds.Left), 19.9, 20.5);
            Assert.InRange(line.Max(g => g.Bounds.Right), 279.5, 280.1);
        }
    }

    [Theory]
    [InlineData(99)]
    [InlineData(100)]
    public void ADigitCountBoundaryOfTheReservedWidthIsHandled(int pages)
    {
        LayoutDocument layout = LayoutDocument.Compose(doc =>
        {
            doc.Title("Boundary").Language("en");
            doc.Page(page =>
            {
                page.Size(200, 100);
                page.Margin(10);
                page.Content().Column(c =>
                {
                    for (int i = 0; i < pages; i++)
                    {
                        if (i > 0)
                        {
                            c.Item().PageBreak();
                        }

                        c.Item().Text(t =>
                        {
                            t.Span(Words(8, "p" + i + "_") + " is page ");
                            t.CurrentPageNumber().ReservedDigits(2);
                            t.Span(" of ");
                            t.TotalPages().ReservedDigits(2);
                            t.Span(" in all.");
                        });
                    }
                });
            });
        });
        byte[] pdf = Support.Save(layout);
        using PdfDocument doc = PdfDocument.Open(pdf);
        Assert.Equal(pages, doc.Pages.Count);
        IReadOnlyList<PdfPageText> texts = doc.ExtractText();
        for (int p = 0; p < pages; p += 11)
        {
            Assert.Contains($"is page {p + 1} of {pages}", Support.Normalize(texts[p].Text), StringComparison.Ordinal);
        }

        Assert.Contains($"is page {pages} of {pages}", Support.Normalize(texts[pages - 1].Text), StringComparison.Ordinal);
        bool noted = layout.Diagnostics.Any(d => d.Message.Contains("more digits", StringComparison.Ordinal));
        Assert.Equal(pages == 100, noted);
    }

    [Fact]
    public void ATextThatFitsOneLineKeepsTheRowLayoutOfBefore()
    {
        // "Page 3 of 12" in a footer: the compact row, no reserved gaps.
        byte[] pdf = Support.Save(LayoutDocument.Compose(doc =>
        {
            doc.Page(page =>
            {
                page.Size(300, 200);
                page.Margin(20);
                page.Footer().AlignCenter().Text(t =>
                {
                    t.FontSize(9);
                    t.Span("Page ");
                    t.CurrentPageNumber();
                    t.Span(" of ");
                    t.TotalPages();
                });
                page.Content().Text("Body");
            });
        }));
        using PdfDocument doc = PdfDocument.Open(pdf);
        Assert.Contains("Page 1 of 1", Support.Normalize(doc.ExtractText()[0].Text), StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkedSpanAndPageNumbersWorkTogether()
    {
        LayoutDocument layout = NumberDoc(t =>
        {
            t.Span(Words(20) + " ");
            t.Span("go to page ").Hyperlink("https://example.com/p");
            t.CurrentPageNumber();
            t.Span(" " + Words(30, "z"));
        }, height: 300);
        byte[] pdf = SaveUa(layout);
        using PdfDocument doc = PdfDocument.Open(pdf);
        Assert.Contains(doc.Pages[0].Links, l => l.Action?.Uri == "https://example.com/p");
        Assert.Empty(TagSupport.Vera(pdf, "ua1"));
    }
}
