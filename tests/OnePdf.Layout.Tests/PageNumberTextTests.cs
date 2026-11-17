using OnePdf.Text;

namespace OnePdf.Layout.Tests;

/// <summary><see cref="PageNumberText"/>: one run for "N/M", exact because the whole text is shaped once the counts are known.</summary>
public class PageNumberTextTests
{
    private const double Tolerance = 0.01;

    private static LayoutDocument Pages(int pages, string format, Action<PageNumberTextDescriptor>? style = null, bool tagged = false) =>
        LayoutDocument.Compose(doc =>
        {
            doc.Title("Numbers").Language("en");
            doc.Page(page =>
            {
                page.Size(300, 200);
                page.Margin(20);
                PageNumberTextDescriptor d = page.Footer().PageNumberText(format).FontSize(8).AlignRight();
                style?.Invoke(d);
                page.Content().Column(c =>
                {
                    for (int i = 0; i < pages; i++)
                    {
                        if (i > 0)
                        {
                            c.Item().PageBreak();
                        }

                        c.Item().Text($"Body {i + 1}");
                    }
                });
            });
        });

    private static string[] Texts(byte[] pdf)
    {
        using PdfDocument d = PdfDocument.Open(pdf);
        return [.. d.ExtractText().Select(t => Support.Normalize(t.Text))];
    }

    private static List<PdfTextGlyph> FooterGlyphs(byte[] pdf, int page, double size)
    {
        using PdfDocument d = PdfDocument.Open(pdf);
        PdfPageText t = d.ExtractText(new PdfTextOptions { RetainGlyphs = true })[page];
        return [.. t.ReadGlyphs().Where(g => Math.Abs(g.Size - size) < 0.01)];
    }

    [Fact]
    public void SeventeenPagesReadThreeOverSeventeenAndMatchPlainText()
    {
        byte[] pdf = Support.Save(Pages(17, "{page}/{total}"));
        string[] texts = Texts(pdf);
        Assert.Equal(17, texts.Length);
        Assert.EndsWith("3/17", texts[2], StringComparison.Ordinal);

        // The same footer as ordinary text, in a page of its own.
        byte[] plain = Support.Save(LayoutDocument.Compose(doc => doc.Page(page =>
        {
            page.Size(300, 200);
            page.Margin(20);
            page.Footer().Text("3/17").FontSize(8).AlignRight();
            page.Content().Text("Body");
        })));
        List<PdfTextGlyph> a = FooterGlyphs(pdf, 2, 8);
        List<PdfTextGlyph> b = FooterGlyphs(plain, 0, 8);
        Assert.Equal("3/17", string.Concat(a.Select(g => g.Text)));
        Assert.Equal(b.Count, a.Count);
        double maxDelta = 0;
        for (int i = 0; i < a.Count; i++)
        {
            maxDelta = Math.Max(maxDelta, Math.Max(Math.Abs(a[i].Origin.X - b[i].Origin.X), Math.Abs(a[i].Origin.Y - b[i].Origin.Y)));
        }

        double edge = a[^1].Bounds.Right;
        double plainEdge = b[^1].Bounds.Right;
        TestContext.Current.SendDiagnosticMessage($"max glyph delta {maxDelta:0.#####}; edge {edge:0.#####} vs plain {plainEdge:0.#####}; box edge 180");
        Assert.True(maxDelta <= Tolerance, "glyph delta " + maxDelta);
        Assert.True(Math.Abs(edge - 180) <= Tolerance, $"edge {edge} vs box edge 180");
        Assert.True(Math.Abs(edge - plainEdge) <= Tolerance, $"edge {edge} vs {plainEdge}");
    }

    [Theory]
    [InlineData(9)]
    [InlineData(100)]
    public void DigitCountChangesWithoutAGap(int n)
    {
        string[] texts = Texts(Support.Save(Pages(n, "Page {page} of {total}")));
        Assert.Equal(n, texts.Length);
        for (int i = 0; i < n; i++)
        {
            Assert.EndsWith($"Page {i + 1} of {n}", texts[i], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LiteralBracesAndOnlyPageAreOrdinaryText()
    {
        string[] texts = Texts(Support.Save(Pages(2, "{{{page}}}")));
        Assert.EndsWith("{2}", texts[1], StringComparison.Ordinal);
    }

    [Fact]
    public void SectionTotalUsesTheSectionStyle()
    {
        LayoutDocument layout = LayoutDocument.Compose(doc =>
        {
            doc.Page(page =>
            {
                page.Size(300, 200);
                page.Margin(20);
                page.NumberStyle(NumberingStyle.LowerRoman);
                page.Footer().PageNumberText("{page} / {sectionTotal} ({total})").FontSize(8);
                page.Content().Column(c => { for (int i = 0; i < 3; i++) { if (i > 0) { c.Item().PageBreak(); } c.Item().Text("Front"); } });
            });
            doc.Page(page =>
            {
                page.Size(300, 200);
                page.Margin(20);
                page.StartNumber(1);
                page.Footer().PageNumberText("{page} / {sectionTotal}").FontSize(8);
                page.Content().Column(c => { for (int i = 0; i < 5; i++) { if (i > 0) { c.Item().PageBreak(); } c.Item().Text("Main"); } });
            });
        });
        string[] t = Texts(Support.Save(layout));
        Assert.Equal(8, t.Length);
        Assert.EndsWith("i / iii (8)", t[0], StringComparison.Ordinal);
        Assert.EndsWith("iii / iii (8)", t[2], StringComparison.Ordinal);
        Assert.EndsWith("1 / 5", t[3], StringComparison.Ordinal);
        Assert.EndsWith("5 / 5", t[7], StringComparison.Ordinal);
    }

    [Fact]
    public void StartAndCenterAlignment()
    {
        double Left(Action<PageNumberTextDescriptor> style)
        {
            byte[] pdf = Support.Save(Pages(12, "{page}/{total}", style));
            return FooterGlyphs(pdf, 2, 8)[0].Bounds.Left;
        }

        double start = Left(d => d.AlignStart());
        double center = Left(d => d.AlignCenter());
        double end = Left(d => d.AlignEnd());
        TestContext.Current.SendDiagnosticMessage($"start {start} center {center} end {end}");
        Assert.True(Math.Abs(start - 20) < 0.2, $"start {start} center {center} end {end}");
        Assert.True(start < center && center < end);
        byte[] pdf = Support.Save(Pages(12, "{page}/{total}", d => d.AlignCenter()));
        List<PdfTextGlyph> g = FooterGlyphs(pdf, 2, 8);
        Assert.Equal(100, (g.Min(x => x.Bounds.Left) + g.Max(x => x.Bounds.Right)) / 2, 0.3);
    }

    [Fact]
    public void AutoWidthSlotUsesReservedDigits()
    {
        double Width(int digits)
        {
            LayoutDocument layout = LayoutDocument.Compose(doc => doc.Page(page =>
            {
                page.Size(300, 200);
                page.Margin(20);
                page.Content().Row(r =>
                {
                    r.AutoItem().PageNumberText("{page}/{total}").ReservedDigits(digits);
                    r.RelativeItem().Text("rest");
                });
            }));
            var bodies = Support.Pages(layout, out _);
            return Support.Walk(bodies[0]).First(p => p.Element is PageNumberText).Width;
        }

        double w2 = Width(2);
        double w4 = Width(4);
        Assert.True(w4 > w2);
        Assert.Equal(w4 - w2, 2 * (w2 - Width(1)), 0.01); // each digit adds the same width, for each of the two placeholders
    }

    [Fact]
    public void TextWiderThanItsBoxIsCondensedWithANote()
    {
        LayoutDocument layout = LayoutDocument.Compose(doc => doc.Page(page =>
        {
            page.Size(300, 200);
            page.Margin(20);
            page.Footer().Width(30).PageNumberText("Page {page} of {total}").FontSize(8);
            page.Content().Text("Body");
        }));
        byte[] pdf = Support.Save(layout);
        Assert.Contains(layout.Diagnostics, d => d.Message.Contains("condensed", StringComparison.Ordinal));
        Assert.EndsWith("Page 1 of 1", Texts(pdf)[0], StringComparison.Ordinal);
    }

    [Fact]
    public void BadFormatsAreRefusedWhenCreated()
    {
        LayoutDocument.Compose(doc => doc.Page(page =>
        {
            Assert.Throws<ArgumentException>(() => page.Footer().PageNumberText("{pages}"));
            Assert.Throws<ArgumentException>(() => page.Footer().PageNumberText("{page"));
            Assert.Throws<ArgumentException>(() => page.Footer().PageNumberText("page}"));
            Assert.Throws<ArgumentException>(() => new PageNumberText { Format = "{}" });
            Assert.Throws<ArgumentNullException>(() => new PageNumberText { Format = null! });
            page.Content().Text("x");
        }));
    }

    [Fact]
    public void DocumentIsLaidOutOnce()
    {
        int draws = 0;
        LayoutDocument layout = LayoutDocument.Compose(doc => doc.Page(page =>
        {
            page.Size(300, 200);
            page.Footer().PageNumberText("{page}/{total}");
            page.Content().Canvas(1, _ => draws++);
        }));
        Support.Save(layout);
        Assert.Equal(1, draws);
    }

    [Fact]
    public void TaggedOutputPassesPdfUA1()
    {
        LayoutDocument layout = LayoutDocument.Compose(doc =>
        {
            doc.Title("Numbers").Language("en");
            doc.Page(page =>
            {
                page.Size(300, 200);
                page.Margin(20);
                page.Footer().PageNumberText("{page}/{total}").FontSize(8);
                page.Content().Column(c =>
                {
                    c.Item().Heading(1).Text("Title");
                    c.Item().PageBreak();
                    c.Item().Text("Second");
                    c.Item().PageBreak();
                    c.Item().PageNumberText("body {page}/{total}");
                });
            });
        });
        using var ms = new MemoryStream();
        layout.Save(ms, new PdfSaveOptions { DocumentId = new byte[16], PdfUA = new PdfUAOptions() });
        byte[] pdf = ms.ToArray();
        List<string> failures = TagSupport.Vera(pdf, "ua1");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.Contains("body 3/3", Texts(pdf)[2], StringComparison.Ordinal);
    }
}
