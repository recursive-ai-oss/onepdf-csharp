using OnePdf.Tests.Pages;

namespace OnePdf.Tests.PageGeometry;

/// <summary>Per-side bleed and slug, and printer's marks, on pages, documents and the streaming writer.</summary>
public class PrinterMarksTests
{
    private static PdfDocument Edit() =>
        PdfDocument.Open(BookFixture.Book("S", 3, form: false), new PdfOpenOptions { AllowEditing = true });

    private static PdfDocument Reopen(PdfDocument d)
    {
        var ms = new MemoryStream();
        d.Save(ms);
        return PdfDocument.Open(ms.ToArray());
    }

    private sealed class Strokes : PdfDevice
    {
        public List<(double X, double Y)> Points { get; } = [];

        public int Paths { get; private set; }

        public override void StrokePath(in PdfPath path, in PdfGraphicsState state)
        {
            Paths++;
            ReadOnlySpan<double> p = path.Points.Span;
            for (int i = 0; i + 1 < p.Length; i += 2)
            {
                Points.Add((p[i], p[i + 1]));
            }
        }
    }

    // Every stroked point lies in the media box and outside (or on the edge of) the bleed box, with room for half the line width.
    private static int AssertMarksInSlug(PdfPage page, double lineWidth)
    {
        var dev = new Strokes();
        page.Interpret(dev);
        PdfRectangle media = page.MediaBox, bleed = page.BleedBox;
        double h = lineWidth / 2;
        foreach ((double x, double y) in dev.Points)
        {
            Assert.True(x >= media.Left && x <= media.Right && y >= media.Bottom && y <= media.Top, $"({x},{y}) outside the media box");
            bool outside = x + h <= bleed.Left + 1e-9 || x - h >= bleed.Right - 1e-9 || y + h <= bleed.Bottom + 1e-9 || y - h >= bleed.Top - 1e-9;
            Assert.True(outside, $"({x},{y}) reaches into the bleed box");
        }

        return dev.Paths;
    }

    private static string ContentOf(PdfPage page)
    {
        var sb = new System.Text.StringBuilder();
        if (page.Dictionary.Get("Contents") is PdfStream s)
        {
            sb.Append(System.Text.Encoding.Latin1.GetString(s.GetDecodedBytes().Span));
        }
        else if (page.Dictionary.Get("Contents") is PdfArray a)
        {
            for (int i = 0; i < a.Count; i++)
            {
                sb.Append(System.Text.Encoding.Latin1.GetString(((PdfStream)a[i]!).GetDecodedBytes().Span)).Append('\n');
            }
        }

        return sb.ToString();
    }

    [Fact]
    public void PerSideBleedAndSlugGiveTheExpectedBoxesOnAPage()
    {
        using PdfDocument d = Edit();
        var trim = new PdfRectangle(20, 30, 220, 330);
        d.Pages[0].SetPrintGeometry(trim, new PdfPrintMargins(1, 2, 3, 4), new PdfPrintMargins(10, 20, 30, 40));
        PdfPage p = d.Pages[0];
        Assert.Equal(new PdfRectangle(20 - 1 - 10, 30 - 2 - 20, 220 + 3 + 30, 330 + 4 + 40), p.MediaBox);
        Assert.Equal(new PdfRectangle(19, 28, 223, 334), p.BleedBox);
        Assert.Equal(trim, p.TrimBox);
        Assert.False(p.HasExplicitBox(PdfPageBox.CropBox));

        using PdfDocument back = Reopen(d);
        Assert.Equal(p.MediaBox, back.Pages[0].MediaBox);
        Assert.Equal(p.BleedBox, back.Pages[0].BleedBox);
        Assert.Equal(trim, back.Pages[0].TrimBox);
    }

    [Fact]
    public void PerSideBleedAndSlugApplyToAWholeDocument()
    {
        using PdfDocument d = Edit();
        PdfRectangle[] trims = [d.Pages[0].TrimBox, d.Pages[1].TrimBox, d.Pages[2].TrimBox];
        d.SetPrintGeometry(new PdfPrintMargins(1, 2, 3, 4), new PdfPrintMargins(5, 6, 7, 8));
        for (int i = 0; i < 3; i++)
        {
            PdfRectangle t = trims[i];
            Assert.Equal(t, d.Pages[i].TrimBox);
            Assert.Equal(new PdfRectangle(t.Left - 1, t.Bottom - 2, t.Right + 3, t.Top + 4), d.Pages[i].BleedBox);
            Assert.Equal(new PdfRectangle(t.Left - 6, t.Bottom - 8, t.Right + 10, t.Top + 12), d.Pages[i].MediaBox);
        }
    }

    [Fact]
    public void UniformMarginsWriteTheSameBoxesAsTheNumberForms()
    {
        using PdfDocument a = Edit();
        using PdfDocument b = Edit();
        var trim = new PdfRectangle(0.1, 0.2, 200.3, 300.7);
        a.Pages[0].SetPrintGeometry(trim, 9.1, 18.3);
        b.Pages[0].SetPrintGeometry(trim, new PdfPrintMargins(9.1), new PdfPrintMargins(18.3));
        Assert.Equal(a.Pages[0].Dictionary.Get("MediaBox")!.ToString(), b.Pages[0].Dictionary.Get("MediaBox")!.ToString());
        Assert.Equal(a.Pages[0].Dictionary.Get("BleedBox")!.ToString(), b.Pages[0].Dictionary.Get("BleedBox")!.ToString());
        Assert.Equal(a.Pages[0].MediaBox, b.Pages[0].MediaBox);
        Assert.Equal(a.Pages[0].BleedBox, b.Pages[0].BleedBox);

        byte[] Write(Action<PdfDocumentWriter> f)
        {
            var ms = new MemoryStream();
            using (var w = new PdfDocumentWriter(ms, new PdfDocumentWriterOptions { DocumentId = new byte[16], LeaveOpen = true }))
            {
                f(w);
                w.Finish();
            }

            return ms.ToArray();
        }

        byte[] one = Write(w => w.BeginPrintPage(200.3, 300.7, 9.1, 18.3, 0).Commit());
        byte[] two = Write(w => w.BeginPrintPage(200.3, 300.7, new PdfPrintMargins(9.1), new PdfPrintMargins(18.3)).Commit());
        Assert.Equal(one, two);
    }

    [Fact]
    public void WriterPerSidePageHasTheExpectedBoxes()
    {
        var ms = new MemoryStream();
        using (var w = new PdfDocumentWriter(ms, new PdfDocumentWriterOptions { LeaveOpen = true }))
        {
            w.BeginPrintPage(200, 300, new PdfPrintMargins(1, 2, 3, 4), new PdfPrintMargins(10, 20, 30, 40)).Commit();
            w.Finish();
        }

        using PdfDocument r = PdfDocument.Open(ms.ToArray());
        PdfPage p = r.Pages[0];
        Assert.Equal(new PdfRectangle(0, 0, 244, 366), p.MediaBox);
        Assert.Equal(new PdfRectangle(10, 20, 214, 326), p.BleedBox);
        Assert.Equal(new PdfRectangle(11, 22, 211, 322), p.TrimBox);
    }

    [Fact]
    public void BadMarginsAreRejectedAndChangeNothing()
    {
        using PdfDocument d = Edit();
        PdfRectangle before = d.Pages[0].MediaBox;
        Assert.Throws<ArgumentException>(() => d.SetPrintGeometry(new PdfPrintMargins(1, 2, -3, 4), new PdfPrintMargins(5)));
        Assert.Throws<ArgumentException>(() => d.SetPrintGeometry(new PdfPrintMargins(1), new PdfPrintMargins(5, double.NaN, 5, 5)));
        Assert.Equal(before, d.Pages[0].MediaBox);
        using var w = new PdfDocumentWriter(new MemoryStream(), new PdfDocumentWriterOptions { LeaveOpen = true });
        Assert.Throws<ArgumentOutOfRangeException>(() => w.BeginPrintPage(200, 300, new PdfPrintMargins(1, 1, -1, 1), new PdfPrintMargins(0)));
    }

    [Fact]
    public void MarksOnAPageLieInTheSlugOutsideTheBleedAndAreArtifacts()
    {
        using PdfDocument d = Edit();
        var marks = new PdfPrinterMarks();
        var trim = new PdfRectangle(20, 30, 220, 330);
        d.Pages[0].SetPrintGeometry(trim, new PdfPrintMargins(3, 6, 9, 12), new PdfPrintMargins(25, 30, 35, 40), marks);
        PdfPage p = d.Pages[0];
        // 8 crop lines (two at each corner) plus 4 registration marks of a circle and two cross lines.
        Assert.Equal(8 + 12, AssertMarksInSlug(p, marks.LineWidth));
        string c = ContentOf(p);
        Assert.Contains("/Artifact <</Type /Page>> BDC", c);
        Assert.Equal("Separation", ((PdfName)((PdfArray)p.Resources.GetDictionary("ColorSpace")!.Get("CsAll1")!)[0]!).Value);
        Assert.Equal("All", ((PdfName)((PdfArray)p.Resources.GetDictionary("ColorSpace")!.Get("CsAll1")!)[1]!).Value);

        using PdfDocument back = Reopen(d);
        AssertMarksInSlug(back.Pages[0], marks.LineWidth);
        Assert.Equal(trim, back.Pages[0].TrimBox);
        // Other pages are untouched.
        Assert.DoesNotContain("/Artifact", ContentOf(back.Pages[1]));
    }

    [Fact]
    public void DocumentMarksGoOnEveryPage()
    {
        using PdfDocument d = Edit();
        var marks = new PdfPrinterMarks { RegistrationMarks = false, LineWidth = 0.5 };
        d.SetPrintGeometry(6, 20, marks);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(8, AssertMarksInSlug(d.Pages[i], 0.5));
        }

        using PdfDocument e = Edit();
        e.SetPrintGeometry(new PdfPrintMargins(6), new PdfPrintMargins(20, 21, 22, 23), marks);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(8, AssertMarksInSlug(e.Pages[i], 0.5));
        }
    }

    [Fact]
    public void WriterMarksLieInTheSlugOutsideTheBleed()
    {
        var ms = new MemoryStream();
        var marks = new PdfPrinterMarks();
        using (var w = new PdfDocumentWriter(ms, new PdfDocumentWriterOptions { LeaveOpen = true }))
        {
            w.BeginPrintPage(200, 300, 9, 24, 0, marks).Commit();
            w.BeginPrintPage(200, 300, new PdfPrintMargins(2, 3, 4, 5), new PdfPrintMargins(20, 22, 24, 26), 0, marks).Commit();
            w.Finish();
        }

        using PdfDocument r = PdfDocument.Open(ms.ToArray());
        Assert.Equal(20, AssertMarksInSlug(r.Pages[0], marks.LineWidth));
        Assert.Equal(20, AssertMarksInSlug(r.Pages[1], marks.LineWidth));
        Assert.Contains("/Artifact <</Type /Page>> BDC", ContentOf(r.Pages[0]));
    }

    [Fact]
    public void ASlugTooSmallForTheMarksIsRefusedAndChangesNothing()
    {
        var marks = new PdfPrinterMarks();
        double need = marks.MinimumSlug;
        Assert.True(need > 18 && need < 19);

        using PdfDocument d = Edit();
        PdfRectangle before = d.Pages[0].MediaBox;
        Assert.Throws<ArgumentException>(() => d.Pages[0].SetPrintGeometry(new PdfRectangle(0, 0, 100, 100), 3, need - 0.5, marks));
        Assert.Throws<ArgumentException>(() => d.Pages[0].SetPrintGeometry(new PdfRectangle(0, 0, 100, 100), new PdfPrintMargins(3), new PdfPrintMargins(need, need, need, need - 1), marks));
        Assert.Throws<ArgumentException>(() => d.SetPrintGeometry(3, 0, marks));
        Assert.Equal(before, d.Pages[0].MediaBox);
        Assert.DoesNotContain("/Artifact", ContentOf(d.Pages[0]));

        using var w = new PdfDocumentWriter(new MemoryStream(), new PdfDocumentWriterOptions { LeaveOpen = true });
        Assert.Throws<ArgumentException>(() => w.BeginPrintPage(200, 300, 3, need - 1, 0, marks));

        // Exactly enough is fine.
        d.Pages[0].SetPrintGeometry(new PdfRectangle(0, 0, 100, 100), 3, need, marks);
        AssertMarksInSlug(d.Pages[0], marks.LineWidth);
    }

    [Fact]
    public void BadMarkOptionsAreRefused()
    {
        using PdfDocument d = Edit();
        var trim = new PdfRectangle(0, 0, 100, 100);
        Assert.Throws<ArgumentException>(() => d.Pages[0].SetPrintGeometry(trim, 0, 40, new PdfPrinterMarks { LineWidth = 0 }));
        Assert.Throws<ArgumentException>(() => d.Pages[0].SetPrintGeometry(trim, 0, 40, new PdfPrinterMarks { Offset = 0, LineWidth = 1 }));
        Assert.Throws<ArgumentException>(() => d.Pages[0].SetPrintGeometry(trim, 0, 40, new PdfPrinterMarks { Length = double.NaN }));
        Assert.Throws<ArgumentNullException>(() => d.Pages[0].SetPrintGeometry(trim, 0, 40, null!));
        // No kind selected: nothing to draw, and no slug needed.
        d.Pages[0].SetPrintGeometry(trim, 0, 0, new PdfPrinterMarks { CropMarks = false, RegistrationMarks = false });
        Assert.DoesNotContain("/Artifact", ContentOf(d.Pages[0]));
    }

    [Theory]
    [InlineData("2b")]
    [InlineData("3b")]
    public void PageWithMarksSavesAsPdfA(string flavour)
    {
        OnePdf.Tests.PdfA.VeraPdf.Require();
        using PdfDocument d = PdfDocument.CreateEmpty();
        PdfPage p = d.InsertBlankPage(0, 200, 300);
        p.SetPrintGeometry(new PdfRectangle(30, 30, 170, 270), new PdfPrintMargins(3, 4, 5, 6), new PdfPrintMargins(10), new PdfPrinterMarks { Offset = 1, Length = 5, RegistrationSize = 5 });
        var ms = new MemoryStream();
        d.Save(ms, new PdfSaveOptions { DocumentId = new byte[16], PdfA = new PdfAOptions { Part = flavour == "2b" ? PdfAPart.Part2 : PdfAPart.Part3, Conformance = PdfAConformance.B } });
        Assert.Empty(OnePdf.Tests.PdfA.VeraPdf.Validate(ms.ToArray(), flavour));
    }

    // Writes marks pages where external tools can look at them, when asked to.
    [Fact]
    public void WritesMarksFilesForExternalTools()
    {
        string? dir = Environment.GetEnvironmentVariable("ONEPDF_GEOMETRY_OUT");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        using (var f = File.Create(Path.Combine(dir, "writer-marks.pdf")))
        using (var w = new PdfDocumentWriter(f, new PdfDocumentWriterOptions { DocumentId = new byte[16], LeaveOpen = true }))
        {
            PdfStreamPageBuilder p = w.BeginPrintPage(200, 300, new PdfPrintMargins(4, 6, 8, 10), new PdfPrintMargins(24, 26, 28, 30), 0, new PdfPrinterMarks());
            p.SetFillRgb(0.9, 0.9, 0.6);
            p.Rectangle(24, 26, p.Width - 52, p.Height - 56);
            p.Fill();
            p.Commit();
            w.Finish();
        }

        using PdfDocument d = Edit();
        d.SetPrintGeometry(6, 24, new PdfPrinterMarks());
        d.Save(Path.Combine(dir, "doc-marks.pdf"));
    }
}
