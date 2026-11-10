using OnePdf.Text;

namespace OnePdf.Layout.Tests;

/// <summary>Bleed, slug and print preferences in layout output.</summary>
public class PrintGeometryTests
{
    private static LayoutDocument Doc(Action<LayoutDocument>? setup = null)
    {
        var body = new Column { Spacing = 6 };
        for (int i = 0; i < 40; i++)
        {
            body.Items.Add(new Text { Content = $"Line {i} of the printed report", Size = 12 });
        }

        var doc = new LayoutDocument { Title = "Print", Language = "en-US" };
        doc.Pages.Add(new LayoutPage
        {
            Size = new PageSize(300, 400),
            Margin = Insets.All(30),
            Background = new Box { Background = LayoutColor.Black },
            Content = body,
        });
        doc.Pages.Add(new LayoutPage { Size = new PageSize(200, 250), Margin = Insets.All(20), Content = new Text { Content = "Second section", Size = 12 } });
        setup?.Invoke(doc);
        return doc;
    }

    private static (double X, double Y) FirstGlyph(PdfDocument d, int page)
    {
        PdfPageText t = d.Pages[page].ExtractText(new PdfTextOptions { RetainGlyphs = true });
        PdfTextGlyph g = t.Glyphs.First(x => x.Text == "L" || x.Text == "S");
        return (g.Matrix.E, g.Matrix.F);
    }

    [Fact]
    public void BleedAndSlugGiveTheExpectedBoxesOnEveryPageAndContentStaysRelativeToTrim()
    {
        byte[] plain = Support.Save(Doc());
        byte[] print = Support.Save(Doc(d => d.Print = new PrintSetup { Bleed = 9, Slug = 18 }));
        using PdfDocument a = PdfDocument.Open(plain);
        using PdfDocument b = PdfDocument.Open(print);
        Assert.Equal(a.Pages.Count, b.Pages.Count);
        Assert.True(b.Pages.Count > 2);
        for (int i = 0; i < b.Pages.Count; i++)
        {
            PdfPage p = b.Pages[i];
            double w = a.Pages[i].MediaBox.Width, h = a.Pages[i].MediaBox.Height;
            Assert.Equal(new PdfRectangle(0, 0, w + 54, h + 54), p.MediaBox);
            Assert.Equal(new PdfRectangle(18, 18, w + 36, h + 36), p.BleedBox);
            Assert.Equal(new PdfRectangle(27, 27, w + 27, h + 27), p.TrimBox);
            Assert.False(p.HasExplicitBox(PdfPageBox.CropBox));
        }

        // Page 0 and the last page (second section): text sits at the same place relative to the trim box.
        foreach (int i in new[] { 0, b.Pages.Count - 1 })
        {
            (double ax, double ay) = FirstGlyph(a, i);
            (double bx, double by) = FirstGlyph(b, i);
            Assert.Equal(ax + 27, bx, 3);
            Assert.Equal(ay + 27, by, 3);
        }
    }

    [Fact]
    public void SectionPrintSetupOverridesTheDocumentOneAndExplicitBoxesAreRelativeToTrim()
    {
        byte[] pdf = Support.Save(Doc(d =>
        {
            d.Print = new PrintSetup { Bleed = 3 };
            d.Pages[1].Print = new PrintSetup { Bleed = 6, Slug = 10, CropBox = new PdfRectangle(-6, -6, 206, 256), ArtBox = new PdfRectangle(20, 20, 180, 230) };
        }));
        using PdfDocument r = PdfDocument.Open(pdf);
        PdfPage first = r.Pages[0];
        Assert.Equal(new PdfRectangle(0, 0, 306, 406), first.MediaBox);
        Assert.Equal(new PdfRectangle(3, 3, 303, 403), first.TrimBox);
        PdfPage last = r.Pages[^1];
        Assert.Equal(new PdfRectangle(0, 0, 232, 282), last.MediaBox);
        Assert.Equal(new PdfRectangle(10, 10, 222, 272), last.BleedBox);
        Assert.Equal(new PdfRectangle(16, 16, 216, 266), last.TrimBox);
        Assert.Equal(new PdfRectangle(10, 10, 222, 272), last.CropBox);
        Assert.Equal(new PdfRectangle(36, 36, 196, 246), last.ArtBox);
    }

    [Fact]
    public void BackgroundBleedsByDefaultAndCanBeTurnedOff()
    {
        static double Extent(byte[] pdf)
        {
            using PdfDocument r = PdfDocument.Open(pdf);
            var dev = new LeftEdge();
            r.Pages[0].Interpret(dev);
            return dev.Min;
        }

        double bleeds = Extent(Support.Save(Doc(d => d.Print = new PrintSetup { Bleed = 9, Slug = 18 })));
        double trimOnly = Extent(Support.Save(Doc(d => d.Print = new PrintSetup { Bleed = 9, Slug = 18, BleedBackground = false })));
        Assert.Equal(18, bleeds, 2);
        Assert.Equal(27, trimOnly, 2);
    }

    [Fact]
    public void FluentFacadeSetsGeometryAndPreferences()
    {
        LayoutDocument doc = LayoutDocument.Compose(c => c
            .Title("Brochure")
            .Language("en")
            .Print(bleed: 9, slug: 18)
            .ViewerPreferences(new PdfViewerPreferences { PrintScaling = PdfPrintScaling.None, NumCopies = 3, Duplex = PdfDuplex.DuplexFlipLongEdge })
            .Page(p =>
            {
                p.Size(PageSize.A5);
                p.Content().Text("Hello");
            })
            .Page(p =>
            {
                p.Size(200, 200);
                p.Print(2);
                p.Content().Text("Hello again");
            }));
        using PdfDocument r = PdfDocument.Open(Support.Save(doc));
        PageSize a5 = PageSize.A5;
        Assert.Equal(new PdfRectangle(0, 0, a5.Width + 54, a5.Height + 54), r.Pages[0].MediaBox);
        Assert.Equal(new PdfRectangle(27, 27, 27 + a5.Width, 27 + a5.Height), r.Pages[0].TrimBox);
        Assert.Equal(new PdfRectangle(2, 2, 202, 202), r.Pages[1].TrimBox);
        PdfViewerPreferences? v = r.ViewerPreferences;
        Assert.NotNull(v);
        Assert.Equal(PdfPrintScaling.None, v.PrintScaling);
        Assert.Equal(3, v.NumCopies);
        Assert.Equal(PdfDuplex.DuplexFlipLongEdge, v.Duplex);
        Assert.True(v.DisplayDocTitle);
    }

    [Fact]
    public void PreferencesReachDocumentTargetsAndExplicitOptionsWin()
    {
        LayoutDocument doc = Doc(d => d.ViewerPreferences = new PdfViewerPreferences { NumCopies = 5 });
        using (PdfDocument target = PdfDocument.CreateEmpty())
        {
            doc.Render(target);
            Assert.Equal(5, target.ViewerPreferences!.NumCopies);
        }

        using var ms = new MemoryStream();
        doc.Save(ms, new PdfDocumentWriterOptions { LeaveOpen = true, ViewerPreferences = new PdfViewerPreferences { NumCopies = 2 } });
        using PdfDocument r = PdfDocument.Open(ms.ToArray());
        Assert.Equal(2, r.ViewerPreferences!.NumCopies);
    }

    [Fact]
    public void InvalidBleedIsRefused()
    {
        LayoutDocument doc = Doc(d => d.Print = new PrintSetup { Bleed = -1 });
        Assert.Throws<ArgumentException>(() => Support.Save(doc));
    }

    [Fact]
    public void RenderingIntoAnEditableDocumentGivesTheSameBoxes()
    {
        LayoutDocument doc = Doc(d => d.Print = new PrintSetup { Bleed = 9, Slug = 18, CropBox = new PdfRectangle(0, 0, 300, 400) });
        using PdfDocument target = PdfDocument.CreateEmpty();
        doc.Render(target);
        Assert.Equal(new PdfRectangle(0, 0, 354, 454), target.Pages[0].MediaBox);
        Assert.Equal(new PdfRectangle(27, 27, 327, 427), target.Pages[0].TrimBox);
        Assert.Equal(new PdfRectangle(27, 27, 327, 427), target.Pages[0].CropBox);
    }

    [Fact]
    public void LinksKeepTheirPlaceOnTheTrimmedPage()
    {
        static PdfRectangle LinkRect(byte[] pdf)
        {
            using PdfDocument r = PdfDocument.Open(pdf);
            return r.Pages[0].Annotations.First(x => x.Subtype == "Link").Rect;
        }

        LayoutDocument Make(bool print)
        {
            LayoutDocument d = Support.Single(new Link { Uri = "https://example.com/", Child = new Text { Content = "click", Size = 12 } }, margin: 20);
            if (print)
            {
                d.Print = new PrintSetup { Bleed = 5, Slug = 7 };
            }

            return d;
        }

        PdfRectangle a = LinkRect(Support.Save(Make(false)));
        PdfRectangle b = LinkRect(Support.Save(Make(true)));
        Assert.Equal(a.Left + 12, b.Left, 3);
        Assert.Equal(a.Bottom + 12, b.Bottom, 3);
    }

    [Theory]
    [InlineData("ua1")]
    [InlineData("2a")]
    public void PrintGeometryOutputPassesVeraPdf(string flavour)
    {
        LayoutDocument layout = PdfUATests.Sample();
        layout.Print = new PrintSetup { Bleed = 9, Slug = 18 };
        layout.ViewerPreferences = new PdfViewerPreferences { PrintScaling = PdfPrintScaling.None, NumCopies = 2 };
        var options = flavour == "ua1"
            ? new PdfSaveOptions { DocumentId = new byte[16], PdfUA = new PdfUAOptions() }
            : new PdfSaveOptions { DocumentId = new byte[16], PdfUA = new PdfUAOptions(), PdfA = new PdfAOptions { Part = PdfAPart.Part2, Conformance = PdfAConformance.A } };

        using var ms = new MemoryStream();
        layout.Save(ms, options);
        byte[] pdf = ms.ToArray();
        using (PdfDocument r = PdfDocument.Open(pdf))
        {
            Assert.Equal(new PdfRectangle(27, 27, 347, 447), r.Pages[0].TrimBox);
            Assert.Equal(2, r.ViewerPreferences!.NumCopies);
        }

        Assert.Empty(TagSupport.Vera(pdf, flavour));
        if (flavour == "2a")
        {
            Assert.Empty(TagSupport.Vera(pdf, "ua1"));
        }
    }

    // Writes a file where external tools (pdfinfo -box, qpdf --check) can look at it, when asked to.
    [Fact]
    public void WritesFileForExternalTools()
    {
        string? dir = Environment.GetEnvironmentVariable("ONEPDF_GEOMETRY_OUT");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "layout-slug.pdf"), Support.Save(Doc(d => d.Print = new PrintSetup { Bleed = 9, Slug = 18, ArtBox = new PdfRectangle(20, 20, 280, 380) })));
    }

    private sealed class LeftEdge : PdfDevice
    {
        public double Min { get; private set; } = double.MaxValue;

        public override void FillPath(in PdfPath path, bool evenOdd, in PdfGraphicsState state)
        {
            ReadOnlySpan<double> pts = path.Points.Span;
            for (int i = 0; i + 1 < pts.Length; i += 2)
            {
                Min = Math.Min(Min, pts[i]);
            }
        }
    }
}
