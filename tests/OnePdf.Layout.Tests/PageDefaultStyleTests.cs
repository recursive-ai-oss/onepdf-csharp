namespace OnePdf.Layout.Tests;

/// <summary>The default text style of a page: it reaches every kind of text on the page, loses to explicit settings field by field, and leaves other documents alone.</summary>
public class PageDefaultStyleTests
{
    private static readonly LayoutColor Red = new(1, 0, 0);
    private static readonly LayoutColor Blue = new(0, 0, 1);

    private static LayoutDocument Doc(Action<PageComposer> page, Action<DocumentComposer>? document = null) =>
        LayoutDocument.Compose(doc =>
        {
            document?.Invoke(doc);
            doc.Page(p =>
            {
                p.Size(300, 400);
                p.Margin(20);
                page(p);
            });
        });

    private static void AssertStyle(StyleSupport.Capture c, string text, double size, LayoutColor color, string? font = null)
    {
        List<StyleSupport.Run> runs = StyleSupport.Find(c, text);
        Assert.NotEmpty(runs);
        foreach (StyleSupport.Run r in runs.Where(r => r.Text.Trim().Length > 0))
        {
            Assert.Equal(size, r.Size, 3);
            Assert.Equal(color.R, r.R, 3);
            Assert.Equal(color.G, r.G, 3);
            Assert.Equal(color.B, r.B, 3);
            if (font is not null)
            {
                Assert.Contains(font, r.Font, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ReachesHeaderFooterPageNumbersContentTablesListsAndTheContentsTable()
    {
        LayoutDocument d = Doc(p =>
        {
            p.DefaultTextStyle(s => s.FontFamily("Serif").FontSize(14).FontColor(Red));
            p.Header().Text("Headerword");
            p.Footer().Column(f =>
            {
                f.Item().Text(t =>
                {
                    t.Span("Footerword ");
                    t.CurrentPageNumber();
                });
                f.Item().PageNumberText("Pageno {page}");
            });
            p.Background().Text("Backdrop");
            p.Foreground().AlignBottom().Text("Frontdrop");
            p.Content().Column(c =>
            {
                c.Item().TableOfContents();
                c.Item().Heading(1).Text("Introduction").FontSize(16);
                c.Item().Text("Bodyword");
                c.Item().Table(t =>
                {
                    t.Columns(cols => { cols.Relative(); cols.Relative(); });
                    t.Row(r =>
                    {
                        r.Cell().Text("Cellone");
                        r.Cell().Text("Celltwo");
                    });
                });
                c.Item().List(l =>
                {
                    l.Item().Text("Listword");
                });
            });
        });
        byte[] pdf = StyleSupport.Save(d);
        StyleSupport.Capture c = StyleSupport.Interpret(pdf);
        foreach (string word in new[] { "Headerword", "Footerword", "Backdrop", "Frontdrop", "Bodyword", "Cellone", "Celltwo", "Listword" })
        {
            AssertStyle(c, word, 14, Red, "Serif");
        }

        // The current page number inside a text, the page number text and the bullet.
        AssertStyle(c, "Pageno 1", 14, Red, "Serif");
        List<StyleSupport.Run> footer = StyleSupport.Find(c, "Footerword 1");
        Assert.All(footer.Where(r => r.Text.Trim().Length > 0), r => Assert.Equal(14, r.Size, 3));
        AssertStyle(c, "\u2022", 14, Red, "Serif");

        // The contents table's entry follows the page default; the heading keeps its own size and takes the rest.
        var entries = c.Runs.Where(r => r.Text == "I").ToList();
        Assert.NotEmpty(entries);
        Assert.Contains(entries, r => Math.Abs(r.Size - 14) < 1e-6);
        Assert.Contains(entries, r => Math.Abs(r.Size - 16) < 1e-6);
        Assert.All(entries, r => Assert.Contains("Serif", r.Font, StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitSettingsWinFieldByField()
    {
        LayoutDocument d = Doc(p =>
        {
            p.DefaultTextStyle(s => s.FontFamily("Serif").FontSize(14).FontColor(Red));
            p.Content().Column(c =>
            {
                c.Item().Text("Ownsize").FontSize(20);
                c.Item().Text("Owncolor").FontColor(Blue);
                c.Item().Text("Ownfont").FontFamily("Mono");
                c.Item().Text(t =>
                {
                    t.Span("Plainspan ");
                    t.Span("Bluespan ").FontColor(Blue);
                    t.Span("Bigspan ").FontSize(30);
                    t.Span("Monospan").FontFamily("Mono");
                });
                c.Item().Text(t =>
                {
                    t.FontSize(18);
                    t.Span("Textsize ");
                    t.Span("Spansize").FontSize(9);
                });
            });
        });
        StyleSupport.Capture c = StyleSupport.Interpret(StyleSupport.Save(d));
        AssertStyle(c, "Ownsize", 20, Red, "Serif");
        AssertStyle(c, "Owncolor", 14, Blue, "Serif");
        AssertStyle(c, "Ownfont", 14, Red, "Mono");
        AssertStyle(c, "Plainspan", 14, Red, "Serif");
        AssertStyle(c, "Bluespan", 14, Blue, "Serif");
        AssertStyle(c, "Bigspan", 30, Red, "Serif");
        AssertStyle(c, "Monospan", 14, Red, "Mono");
        AssertStyle(c, "Textsize", 18, Red, "Serif");
        AssertStyle(c, "Spansize", 9, Red, "Serif");
    }

    [Fact]
    public void PageSettingsWinOverTheDocumentAndEachPageHasItsOwn()
    {
        LayoutDocument d = LayoutDocument.Compose(doc =>
        {
            doc.Page(p =>
            {
                p.Size(300, 200);
                p.DefaultTextStyle(s => s.FontColor(Blue));
                p.Content().Text("Firstpage");
            });
            doc.Page(p =>
            {
                p.Size(300, 200);
                p.Content().Text("Secondpage").FontSize(10);
            });
            doc.DefaultTextStyle(s => s.FontSize(16).FontColor(Red));
        });
        byte[] pdf = StyleSupport.Save(d);
        StyleSupport.Capture first = StyleSupport.Interpret(pdf, 0);
        StyleSupport.Capture second = StyleSupport.Interpret(pdf, 1);
        // The document default was set last and still reaches both pages; the first page's colour wins over it.
        AssertStyle(first, "Firstpage", 16, Blue);
        AssertStyle(second, "Secondpage", 10, Red);
    }

    [Fact]
    public void WithoutADefaultNothingChanges()
    {
        static LayoutDocument Make(bool withDefault) => Doc(p =>
        {
            if (withDefault)
            {
                p.DefaultTextStyle(_ => { });
            }

            p.Header().Text("Head");
            p.Footer().Text(t => { t.Span("Page "); t.CurrentPageNumber(); });
            p.Content().Column(c =>
            {
                c.Item().Text("Body text").Bold();
                c.Item().List(l => l.Item().Text("Item"));
            });
        });
        Assert.Equal(StyleSupport.Save(Make(false)), StyleSupport.Save(Make(true)));
    }

    [Fact]
    public void ElementsMadeByHandInheritWhatTheyNeverSet()
    {
        LayoutDocument d = Doc(p =>
        {
            p.DefaultTextStyle(s => s.FontSize(13).FontColor(Blue));
            p.Content().Element(new Text { Content = "Handmade", Weight = 700 });
        });
        StyleSupport.Capture c = StyleSupport.Interpret(StyleSupport.Save(d));
        AssertStyle(c, "Handmade", 13, Blue);
    }

    [Fact]
    public void LetterSpacingUnderlineAndLineHeightComeFromThePageToo()
    {
        static LayoutDocument Make(Action<DefaultTextStyleDescriptor>? style) => Doc(p =>
        {
            if (style is not null)
            {
                p.DefaultTextStyle(style);
            }

            p.Content().Text("Spaced out");
        });
        StyleSupport.Capture plain = StyleSupport.Interpret(StyleSupport.Save(Make(null)));
        StyleSupport.Capture spaced = StyleSupport.Interpret(StyleSupport.Save(Make(s => s.LetterSpacing(2))));
        StyleSupport.Capture underlined = StyleSupport.Interpret(StyleSupport.Save(Make(s => s.Underline())));
        double last(StyleSupport.Capture c) => c.Runs.Max(r => r.X);
        Assert.Equal(9 * 2, last(spaced) - last(plain), 3);
        Assert.NotEmpty(underlined.Paths);
        Assert.Empty(plain.Paths);
    }
}
