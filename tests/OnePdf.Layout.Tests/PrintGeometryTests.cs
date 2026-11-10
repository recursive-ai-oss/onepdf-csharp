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

    private static readonly PdfBoxColorInfo Guide = new() { Red = 1, Green = 0.25, LineWidth = 2, Style = PdfBoxLineStyle.Dashed, Dash = [4, 2] };

    [Fact]
    public void PerSideBleedAndSlugGiveTheExpectedBoxesAndKeepContentRelativeToTrim()
    {
        var bleed = new PdfPrintMargins(3, 4, 5, 6);
        var slug = new PdfPrintMargins(10, 20, 30, 40);
        LayoutDocument Make(PrintSetup? print) => Doc(d => d.Print = print);
        byte[] plain = Support.Save(Make(null));
        byte[] print = Support.Save(Make(new PrintSetup { BleedSides = bleed, SlugSides = slug, CropBox = new PdfRectangle(-3, -4, 303, 406) }));
        using PdfDocument a = PdfDocument.Open(plain);
        using PdfDocument b = PdfDocument.Open(print);
        Assert.Equal(a.Pages.Count, b.Pages.Count);
        for (int i = 0; i < b.Pages.Count; i++)
        {
            PdfPage p = b.Pages[i];
            double w = a.Pages[i].MediaBox.Width, h = a.Pages[i].MediaBox.Height;
            Assert.Equal(new PdfRectangle(0, 0, w + 13 + 35, h + 24 + 46), p.MediaBox);
            Assert.Equal(new PdfRectangle(10, 20, w + 13 + 5, h + 24 + 6), p.BleedBox);
            Assert.Equal(new PdfRectangle(13, 24, 13 + w, 24 + h), p.TrimBox);
        }

        Assert.Equal(new PdfRectangle(10, 20, 316, 430), b.Pages[0].CropBox);
        (double ax, double ay) = FirstGlyph(a, 0);
        (double bx, double by) = FirstGlyph(b, 0);
        // Extraction reports positions from the crop box's corner, which is (10, 20) here.
        Assert.Equal(ax + 13 - 10, bx, 3);
        Assert.Equal(ay + 24 - 20, by, 3);
    }

    [Fact]
    public void PerSideSetupMatchesTheUniformOneWhenTheSidesAreEqual()
    {
        byte[] one = Support.Save(Doc(d => d.Print = new PrintSetup { Bleed = 9, Slug = 18 }));
        byte[] two = Support.Save(Doc(d => d.Print = new PrintSetup { BleedSides = new PdfPrintMargins(9), SlugSides = new PdfPrintMargins(18) }));
        Assert.Equal(one, two);
    }

    [Fact]
    public void PerSideBackgroundBleedsByTheSideAmounts()
    {
        var doc = new LayoutDocument();
        doc.Pages.Add(new LayoutPage
        {
            Size = new PageSize(300, 400),
            Margin = Insets.All(30),
            Background = new Box { Background = LayoutColor.Black, ExpandHeight = true },
            Content = new Text { Content = "Body", Size = 12 },
            Print = new PrintSetup { BleedSides = new PdfPrintMargins(2, 3, 4, 5), SlugSides = new PdfPrintMargins(10) },
        });
        var dev = new Extent();
        using PdfDocument r = PdfDocument.Open(Support.Save(doc));
        PdfPage page = r.Pages[0];
        page.Interpret(dev);
        PdfRectangle bleed = page.BleedBox;
        double h = page.MediaBox.Height;
        // The black background fills the bleed box exactly (the interpreter reports y downwards from the top of the page).
        Assert.Equal(bleed.Left, dev.MinX, 2);
        Assert.Equal(bleed.Right, dev.MaxX, 2);
        Assert.Equal(h - bleed.Top, dev.MinY, 2);
        Assert.Equal(h - bleed.Bottom, dev.MaxY, 2);
    }

    [Fact]
    public void FluentPerSideAndMarksReachTheDocument()
    {
        var marks = new PdfPrinterMarks();
        LayoutDocument doc = LayoutDocument.Compose(c => c
            .Print(new PdfPrintMargins(3, 4, 5, 6), new PdfPrintMargins(30), marks)
            .Page(p =>
            {
                p.Size(200, 200);
                p.Content().Text("Hello");
            })
            .Page(p =>
            {
                p.Size(200, 200);
                p.Print(2, 25, marks);
                p.Content().Text("Hello again");
            }));
        using PdfDocument r = PdfDocument.Open(Support.Save(doc));
        Assert.Equal(new PdfRectangle(33, 34, 233, 234), r.Pages[0].TrimBox);
        Assert.Equal(new PdfRectangle(27, 27, 227, 227), r.Pages[1].TrimBox);
        foreach (int i in new[] { 0, 1 })
        {
            var dev = new Extent();
            r.Pages[i].Interpret(dev);
            Assert.True(dev.Strokes >= 20);
        }
    }

    [Fact]
    public void MarksLieInTheSlugOutsideTheBleedOnWriterAndDocumentTargets()
    {
        var marks = new PdfPrinterMarks();
        PrintSetup Setup() => new() { BleedSides = new PdfPrintMargins(3, 4, 5, 6), SlugSides = new PdfPrintMargins(22, 24, 26, 28), Marks = marks };
        using PdfDocument viaWriter = PdfDocument.Open(Support.Save(Doc(d => d.Print = Setup())));
        using PdfDocument viaDoc = PdfDocument.CreateEmpty();
        Doc(d => d.Print = Setup()).Render(viaDoc);
        foreach (PdfDocument r in new[] { viaWriter, viaDoc })
        {
            Assert.True(r.Pages.Count > 2);
            for (int i = 0; i < r.Pages.Count; i++)
            {
                var dev = new Extent();
                r.Pages[i].Interpret(dev);
                Assert.True(dev.Strokes >= 20);
                PdfRectangle media = r.Pages[i].MediaBox, bleed = r.Pages[i].BleedBox;
                foreach ((double x, double y) in dev.StrokePoints)
                {
                    Assert.True(x >= media.Left && x <= media.Right && y >= media.Bottom && y <= media.Top);
                    Assert.True(x <= bleed.Left || x >= bleed.Right || y <= bleed.Bottom || y >= bleed.Top, $"page {i}: ({x},{y}) inside the bleed box");
                }
            }
        }
    }

    [Fact]
    public void MarksNeedEnoughSlug()
    {
        Assert.Throws<ArgumentException>(() => Support.Save(Doc(d => d.Print = new PrintSetup { Bleed = 3, Slug = 5, Marks = new PdfPrinterMarks() })));
        Assert.Throws<ArgumentException>(() => Support.Save(Doc(d => d.Print = new PrintSetup { Bleed = 3, SlugSides = new PdfPrintMargins(30, 30, 30, 5), Marks = new PdfPrinterMarks() })));
        Assert.Throws<ArgumentException>(() => Support.Save(Doc(d => d.Print = new PrintSetup { BleedSides = new PdfPrintMargins(1, 1, -1, 1) })));
    }

    [Fact]
    public void BoxColorInfoRoundTripsAndMatchesTheCoreWriter()
    {
        var setup = new PrintSetup { Bleed = 4, Slug = 6, BleedBoxColor = Guide, TrimBoxColor = new PdfBoxColorInfo { Blue = 1 }, ArtBox = new PdfRectangle(20, 20, 200, 300), ArtBoxColor = new PdfBoxColorInfo { Green = 1, LineWidth = 0.5 } };
        using PdfDocument viaWriter = PdfDocument.Open(Support.Save(Doc(d => d.Print = setup)));
        using PdfDocument viaDoc = PdfDocument.CreateEmpty();
        Doc(d => d.Print = setup).Render(viaDoc);
        foreach (PdfDocument r in new[] { viaWriter, viaDoc })
        {
            for (int i = 0; i < r.Pages.Count; i++)
            {
                PdfPage p = r.Pages[i];
                PdfBoxColorInfo? bleed = p.GetBoxColorInfo(PdfPageBox.BleedBox);
                Assert.NotNull(bleed);
                Assert.Equal(1, bleed.Red);
                Assert.Equal(0.25, bleed.Green);
                Assert.Equal(2, bleed.LineWidth);
                Assert.Equal(PdfBoxLineStyle.Dashed, bleed.Style);
                Assert.Equal([4, 2], bleed.Dash);
                Assert.Equal(1, p.GetBoxColorInfo(PdfPageBox.TrimBox)!.Blue);
                Assert.Equal(0.5, p.GetBoxColorInfo(PdfPageBox.ArtBox)!.LineWidth);
                Assert.Null(p.GetBoxColorInfo(PdfPageBox.CropBox));
            }
        }

        // The same entry as the core writer produces.
        var ms = new MemoryStream();
        using (var w = new PdfDocumentWriter(ms, new PdfDocumentWriterOptions { LeaveOpen = true }))
        {
            PdfStreamPageBuilder b = w.BeginPrintPage(100, 100, 4, 6, 0);
            b.SetBoxColorInfo(PdfPageBox.BleedBox, Guide);
            b.Commit();
            w.Finish();
        }

        using PdfDocument core = PdfDocument.Open(ms.ToArray());
        Assert.Equal(Describe((PdfDictionary)((PdfDictionary)core.Pages[0].Dictionary.Get("BoxColorInfo")!).Get("BleedBox")!), Describe((PdfDictionary)((PdfDictionary)viaWriter.Pages[0].Dictionary.Get("BoxColorInfo")!).Get("BleedBox")!));
    }

    private static string Describe(PdfDictionary d) =>
        string.Join(";", d.Keys.Select(k => k.ToString()).OrderBy(k => k, StringComparer.Ordinal).Select(k => k + "=" + (d.Get(k) is PdfArray a ? string.Join(",", Enumerable.Range(0, a.Count).Select(i => a.GetDouble(i))) : d.Get(k)!.ToString())));

    [Theory]
    [InlineData("ua1")]
    [InlineData("2a")]
    public void PerSideMarksOutputPassesVeraPdf(string flavour)
    {
        LayoutDocument layout = PdfUATests.Sample();
        layout.Print = new PrintSetup
        {
            BleedSides = new PdfPrintMargins(3, 4, 5, 6),
            SlugSides = new PdfPrintMargins(24, 26, 28, 30),
            Marks = new PdfPrinterMarks(),
            BleedBoxColor = Guide,
        };
        var options = flavour == "ua1"
            ? new PdfSaveOptions { DocumentId = new byte[16], PdfUA = new PdfUAOptions() }
            : new PdfSaveOptions { DocumentId = new byte[16], PdfUA = new PdfUAOptions(), PdfA = new PdfAOptions { Part = PdfAPart.Part2, Conformance = PdfAConformance.A } };

        using var ms = new MemoryStream();
        layout.Save(ms, options);
        byte[] pdf = ms.ToArray();
        using (PdfDocument r = PdfDocument.Open(pdf))
        {
            var dev = new Extent();
            r.Pages[0].Interpret(dev);
            Assert.True(dev.Strokes >= 20);
        }

        Assert.Empty(TagSupport.Vera(pdf, flavour));
        if (flavour == "2a")
        {
            Assert.Empty(TagSupport.Vera(pdf, "ua1"));
        }
    }

    private sealed class Extent : PdfDevice
    {
        public double MinX { get; private set; } = double.MaxValue;

        public double MinY { get; private set; } = double.MaxValue;

        public double MaxX { get; private set; } = double.MinValue;

        public double MaxY { get; private set; } = double.MinValue;

        public int Strokes { get; private set; }

        public List<(double X, double Y)> StrokePoints { get; } = [];

        public override void FillPath(in PdfPath path, bool evenOdd, in PdfGraphicsState state)
        {
            ReadOnlySpan<double> pts = path.Points.Span;
            for (int i = 0; i + 1 < pts.Length; i += 2)
            {
                MinX = Math.Min(MinX, pts[i]);
                MaxX = Math.Max(MaxX, pts[i]);
                MinY = Math.Min(MinY, pts[i + 1]);
                MaxY = Math.Max(MaxY, pts[i + 1]);
            }
        }

        public override void StrokePath(in PdfPath path, in PdfGraphicsState state)
        {
            Strokes++;
            ReadOnlySpan<double> pts = path.Points.Span;
            for (int i = 0; i + 1 < pts.Length; i += 2)
            {
                StrokePoints.Add((pts[i], pts[i + 1]));
            }
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
        File.WriteAllBytes(Path.Combine(dir, "layout-sides-marks.pdf"), Support.Save(Doc(d => d.Print = new PrintSetup { BleedSides = new PdfPrintMargins(3, 6, 9, 12), SlugSides = new PdfPrintMargins(24, 26, 28, 30), Marks = new PdfPrinterMarks(), BleedBoxColor = Guide })));
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
