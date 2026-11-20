using OnePdf.Layout;

namespace OnePdf.Layout.Core.Tests;

public class LayoutCoreTests
{
    private static byte[] TestFont() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fonts", "LiberationSans-Regular.ttf"));

    private static byte[] Save(LayoutDocument doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms, new PdfDocumentWriterOptions { LeaveOpen = true });
        return ms.ToArray();
    }

    [Fact]
    public void RegisteredFontLaysOutAndExtracts()
    {
        byte[] font = TestFont();
        LayoutDocument doc = LayoutDocument.Compose(d =>
        {
            d.Fonts.Add(font, "Acme");
            d.Fonts.DefaultFamily = "Acme";
            d.Page(p => p.Content().Column(c =>
            {
                c.Item().Text("Core without bundled fonts").FontSize(14);
                c.Item().Text("second line").FontFamily("Acme").Bold();
            }));
        });

        using PdfDocument back = PdfDocument.Open(Save(doc));
        string text = string.Concat(back.ExtractText().Select(t => t.Text));
        Assert.Contains("Core without bundled fonts", text, StringComparison.Ordinal);
        Assert.Contains("second line", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WrappersFeaturesAndPageDefaultsWorkWithARegisteredFont()
    {
        byte[] font = TestFont();
        LayoutDocument doc = LayoutDocument.Compose(d =>
        {
            d.Fonts.Add(font, "Acme");
            d.Page(p =>
            {
                p.DefaultTextStyle(t => t.FontFamily("Acme").FontSize(12).Ligatures(false));
                p.Content().Column(c =>
                {
                    c.Item().Width(120).Height(30).ScaleToFit().Text(string.Join(' ', Enumerable.Repeat("scaled words that do not fit", 6)));
                    c.Item().Shrink().Border(1).Padding(3).Text("hugging");
                    c.Item().Text(t => t.Span("office").Feature("smcp"));
                    c.Item().Unconstrained().Width(900).Height(20).Text("overflowing");
                });
            });
        });

        using PdfDocument back = PdfDocument.Open(Save(doc));
        string text = string.Join(' ', back.ExtractText().Select(t => t.Text));
        Assert.Contains("hugging", text, StringComparison.Ordinal);
        Assert.Contains("overflowing", text, StringComparison.Ordinal);
        Assert.Contains("office", text, StringComparison.Ordinal);
        Assert.Contains("scaled words", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoRegisteredFontIsATypedErrorNamingTheFamily()
    {
        LayoutDocument doc = LayoutDocument.Compose(d => d.Page(p => p.Content().Text("hello")));
        var e = Assert.Throws<PdfFontNotRegisteredException>(() => Save(doc));
        Assert.Equal(["Sans"], e.RequestedFamilies);
        Assert.Contains("'Sans'", e.Message, StringComparison.Ordinal);
        Assert.Contains("TextFontRegistry.Add", e.Message, StringComparison.Ordinal);
        Assert.Contains("RecursiveAI.OnePdf.Layout", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AskedForFamilyIsNamed()
    {
        LayoutDocument doc = LayoutDocument.Compose(d =>
        {
            d.Fonts.DefaultFamily = "Brand";
            d.Page(p => p.Content().Text("hello").FontFamily("Garamond"));
        });
        var e = Assert.Throws<PdfFontNotRegisteredException>(() => Save(doc));
        Assert.Equal(["Garamond"], e.RequestedFamilies);
        Assert.Equal("Brand", e.DefaultFamily);
        Assert.Contains("'Garamond'", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StandardFontNamesDoNotResolveWithoutTheFontsPackage()
    {
        LayoutDocument doc = LayoutDocument.Compose(d => d.Page(p => p.Content().Text("hello").FontFamily("Helvetica")));
        var e = Assert.Throws<PdfFontNotRegisteredException>(() => Save(doc));
        Assert.Contains("Helvetica", e.RequestedFamilies);
    }

    [Fact]
    public void UnregisteredFamilyFallsBackToTheRegisteredDefault()
    {
        byte[] font = TestFont();
        LayoutDocument doc = LayoutDocument.Compose(d =>
        {
            d.Fonts.Add(font, "Acme");
            d.Fonts.DefaultFamily = "Acme";
            d.Page(p => p.Content().Text("fallback text").FontFamily("Garamond"));
        });
        using PdfDocument back = PdfDocument.Open(Save(doc));
        Assert.Contains("fallback text", string.Concat(back.ExtractText().Select(t => t.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public void SpanLinksAndPageNumbersInWrappingTextWorkWithARegisteredFont()
    {
        byte[] font = TestFont();
        LayoutDocument doc = LayoutDocument.Compose(d =>
        {
            d.Fonts.Add(font, "Acme");
            d.Fonts.DefaultFamily = "Acme";
            d.Page(p =>
            {
                p.Size(300, 160);
                p.Margin(20);
                p.Content().Text(t =>
                {
                    t.Justify();
                    for (int i = 0; i < 6; i++)
                    {
                        t.Span(string.Join(' ', Enumerable.Range(0, 12).Select(k => $"q{i}_{k}")) + " ");
                        t.Span("a linked stretch of words that wraps").Hyperlink("https://example.com/core");
                        t.Span(" read on page ");
                        t.CurrentPageNumber();
                        t.Span(" of ");
                        t.TotalPages();
                        t.Span(" ");
                    }
                });
            });
        });

        using PdfDocument back = PdfDocument.Open(Save(doc));
        Assert.True(back.Pages.Count >= 2);
        Assert.Contains(back.Pages[0].Links, l => l.Action?.Uri == "https://example.com/core");
        Assert.Contains(back.Pages[1].Links, l => l.Action?.Uri == "https://example.com/core");
        int count = back.Pages.Count;
        var texts = back.ExtractText();
        for (int pg = 0; pg < count; pg++)
        {
            Assert.Matches($@"page\s+{pg + 1}\s+of\s+{count}", texts[pg].Text);
        }
    }

    [Fact]
    public void NoFontsAssemblyIsLoaded()
    {
        byte[] font = TestFont();
        Save(LayoutDocument.Compose(d =>
        {
            d.Fonts.Add(font, "Acme");
            d.Fonts.DefaultFamily = "Acme";
            d.Page(p => p.Content().Text("x"));
        }));
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == "OnePdf.Fonts");
    }
}
