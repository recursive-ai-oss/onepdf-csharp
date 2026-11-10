using OnePdf.Tests.Pages;

namespace OnePdf.Tests.PageGeometry;

/// <summary>Slug margins for printer's marks, and /BoxColorInfo for pages made by the streaming writer.</summary>
public class SlugTests
{
    private static PdfDocument Edit() =>
        PdfDocument.Open(BookFixture.Book("S", 3, form: false), new PdfOpenOptions { AllowEditing = true });

    private static PdfDocument Reopen(PdfDocument d)
    {
        var ms = new MemoryStream();
        d.Save(ms);
        return PdfDocument.Open(ms.ToArray());
    }

    [Fact]
    public void PageSlugGrowsTheMediaBoxOutsideTheBleedAndShowsTheWholeMedia()
    {
        using PdfDocument d = Edit();
        PdfPage p = d.Pages[0];
        p.SetCropBox(new PdfRectangle(10, 10, 100, 100));
        var trim = new PdfRectangle(20, 30, 220, 330);
        p.SetPrintGeometry(trim, 9, 18);
        Assert.Equal(new PdfRectangle(-7, 3, 247, 357), p.MediaBox);
        Assert.Equal(new PdfRectangle(11, 21, 229, 339), p.BleedBox);
        Assert.Equal(trim, p.TrimBox);
        Assert.False(p.HasExplicitBox(PdfPageBox.CropBox));
        Assert.Equal(p.MediaBox, p.CropBox);

        using PdfDocument back = Reopen(d);
        Assert.Equal(new PdfRectangle(-7, 3, 247, 357), back.Pages[0].MediaBox);
        Assert.Equal(new PdfRectangle(11, 21, 229, 339), back.Pages[0].BleedBox);
        Assert.Equal(trim, back.Pages[0].TrimBox);
    }

    [Fact]
    public void ZeroSlugIsTheSameAsTheTwoArgumentForm()
    {
        using PdfDocument a = Edit();
        using PdfDocument b = Edit();
        var trim = new PdfRectangle(0, 0, 200, 300);
        a.Pages[0].SetPrintGeometry(trim, 6);
        b.Pages[0].SetPrintGeometry(trim, 6, 0);
        Assert.Equal(a.Pages[0].MediaBox, b.Pages[0].MediaBox);
        Assert.Equal(a.Pages[0].BleedBox, b.Pages[0].BleedBox);
        Assert.Equal(b.Pages[0].MediaBox, b.Pages[0].BleedBox);
    }

    [Fact]
    public void DocumentSlugKeepsEachPagesTrimBox()
    {
        using PdfDocument d = Edit();
        PdfRectangle[] trims = [d.Pages[0].TrimBox, d.Pages[1].TrimBox, d.Pages[2].TrimBox];
        d.SetPrintGeometry(4, 10);
        for (int i = 0; i < 3; i++)
        {
            PdfPage p = d.Pages[i];
            PdfRectangle t = trims[i];
            Assert.Equal(t, p.TrimBox);
            Assert.Equal(new PdfRectangle(t.Left - 4, t.Bottom - 4, t.Right + 4, t.Top + 4), p.BleedBox);
            Assert.Equal(new PdfRectangle(t.Left - 14, t.Bottom - 14, t.Right + 14, t.Top + 14), p.MediaBox);
        }
    }

    [Fact]
    public void InvalidSlugIsRejectedAndChangesNothing()
    {
        using PdfDocument d = Edit();
        PdfRectangle before = d.Pages[0].MediaBox;
        Assert.Throws<ArgumentException>(() => d.SetPrintGeometry(3, -1));
        Assert.Throws<ArgumentException>(() => d.SetPrintGeometry(3, double.NaN));
        Assert.Throws<ArgumentException>(() => d.Pages[0].SetPrintGeometry(new PdfRectangle(0, 0, 100, 100), 3, double.PositiveInfinity));
        Assert.Equal(before, d.Pages[0].MediaBox);
    }

    [Fact]
    public void WriterSlugPageHasTheExpectedBoxes()
    {
        var ms = new MemoryStream();
        using (var w = new PdfDocumentWriter(ms, new PdfDocumentWriterOptions { LeaveOpen = true }))
        {
            PdfStreamPageBuilder p = w.BeginPrintPage(200, 300, 9, 18, 0);
            p.Commit();
            PdfStreamPageBuilder q = w.BeginPrintPage(200, 300, 0, 12, 0);
            q.Commit();
            w.Finish();
        }

        using PdfDocument r = PdfDocument.Open(ms.ToArray());
        Assert.Equal(new PdfRectangle(0, 0, 254, 354), r.Pages[0].MediaBox);
        Assert.Equal(new PdfRectangle(18, 18, 236, 336), r.Pages[0].BleedBox);
        Assert.Equal(new PdfRectangle(27, 27, 227, 327), r.Pages[0].TrimBox);
        Assert.False(r.Pages[0].HasExplicitBox(PdfPageBox.CropBox));
        Assert.Equal(r.Pages[0].MediaBox, r.Pages[0].CropBox);
        // Bleed zero: the bleed box is the trim box and the slug surrounds both.
        Assert.Equal(new PdfRectangle(0, 0, 224, 324), r.Pages[1].MediaBox);
        Assert.Equal(new PdfRectangle(12, 12, 212, 312), r.Pages[1].BleedBox);
        Assert.Equal(r.Pages[1].BleedBox, r.Pages[1].TrimBox);
    }

    [Fact]
    public void WriterSlugRejectsBadValues()
    {
        using var w = new PdfDocumentWriter(new MemoryStream(), new PdfDocumentWriterOptions { LeaveOpen = true });
        Assert.Throws<ArgumentOutOfRangeException>(() => w.BeginPrintPage(200, 300, 3, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.BeginPrintPage(200, 300, 3, double.NaN, 0));
    }

    [Fact]
    public void WriterBoxColorInfoRoundTripsAndMatchesTheEditingApi()
    {
        var info = new PdfBoxColorInfo { Red = 1, Green = 0.5, Blue = 0, LineWidth = 2, Style = PdfBoxLineStyle.Dashed, Dash = [4, 2] };
        var plain = new PdfBoxColorInfo { Red = 0, Green = 0.25, Blue = 1 };
        var ms = new MemoryStream();
        using (var w = new PdfDocumentWriter(ms, new PdfDocumentWriterOptions { LeaveOpen = true }))
        {
            PdfStreamPageBuilder p = w.BeginPrintPage(200, 300, 9, 18, 0);
            p.SetBoxColorInfo(PdfPageBox.BleedBox, info);
            p.SetBoxColorInfo(PdfPageBox.TrimBox, plain);
            p.SetBoxColorInfo(PdfPageBox.ArtBox, plain);
            p.SetBoxColorInfo(PdfPageBox.ArtBox, null);
            p.Commit();
            w.BeginPage(100, 100).Commit();
            w.Finish();
        }

        using PdfDocument r = PdfDocument.Open(ms.ToArray());
        Assert.Equal(info.Red, r.Pages[0].GetBoxColorInfo(PdfPageBox.BleedBox)?.Red);
        Assert.Equal(2, r.Pages[0].GetBoxColorInfo(PdfPageBox.BleedBox)!.LineWidth);
        Assert.Equal([4, 2], r.Pages[0].GetBoxColorInfo(PdfPageBox.BleedBox)!.Dash);
        Assert.Equal(PdfBoxLineStyle.Dashed, r.Pages[0].GetBoxColorInfo(PdfPageBox.BleedBox)!.Style);
        Assert.Equal(0.25, r.Pages[0].GetBoxColorInfo(PdfPageBox.TrimBox)?.Green);
        Assert.Null(r.Pages[0].GetBoxColorInfo(PdfPageBox.ArtBox));
        Assert.Null(r.Pages[1].GetBoxColorInfo(PdfPageBox.TrimBox));
        Assert.False(r.Pages[1].Dictionary.ContainsKey("BoxColorInfo"));

        // The same entry written through the editing API is the same object tree.
        using PdfDocument e = PdfDocument.Open(BookFixture.Book("S", 1, form: false), new PdfOpenOptions { AllowEditing = true });
        e.Pages[0].SetBoxColorInfo(PdfPageBox.BleedBox, info);
        var written = (PdfDictionary)((PdfDictionary)r.Pages[0].Dictionary.Get("BoxColorInfo")!).Get("BleedBox")!;
        using PdfDocument e2 = Reopen(e);
        var edited = (PdfDictionary)((PdfDictionary)e2.Pages[0].Dictionary.Get("BoxColorInfo")!).Get("BleedBox")!;
        Assert.Equal(Describe(edited), Describe(written));
    }

    private static string Describe(PdfDictionary d)
    {
        var keys = d.Keys.Select(k => k.ToString()).OrderBy(k => k, StringComparer.Ordinal).ToList();
        return string.Join(";", keys.Select(k => k + "=" + (d.Get(k) is PdfArray a ? string.Join(",", Enumerable.Range(0, a.Count).Select(i => a.GetDouble(i))) : d.Get(k)!.ToString())));
    }

    [Fact]
    public void WriterBoxColorInfoRejectsMediaBoxAndBadValues()
    {
        using var w = new PdfDocumentWriter(new MemoryStream(), new PdfDocumentWriterOptions { LeaveOpen = true });
        PdfStreamPageBuilder p = w.BeginPage(100, 100);
        Assert.Throws<ArgumentException>(() => p.SetBoxColorInfo(PdfPageBox.MediaBox, new PdfBoxColorInfo()));
        Assert.Throws<ArgumentException>(() => p.SetBoxColorInfo(PdfPageBox.TrimBox, new PdfBoxColorInfo { Red = 2 }));
        Assert.Throws<ArgumentException>(() => p.SetBoxColorInfo(PdfPageBox.TrimBox, new PdfBoxColorInfo { Style = PdfBoxLineStyle.Dashed, Dash = [] }));
        p.Commit();
    }

    // Writes a writer-made slug page where external tools (pdfinfo -box, qpdf --check) can look at it, when asked to.
    [Fact]
    public void WritesFileForExternalTools()
    {
        string? dir = Environment.GetEnvironmentVariable("ONEPDF_GEOMETRY_OUT");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        using var f = File.Create(Path.Combine(dir, "writer-slug.pdf"));
        using var w = new PdfDocumentWriter(f, new PdfDocumentWriterOptions { DocumentId = new byte[16], LeaveOpen = true });
        PdfStreamPageBuilder p = w.BeginPrintPage(200, 300, 9, 18, 0);
        p.SetBoxColorInfo(PdfPageBox.BleedBox, new PdfBoxColorInfo { Red = 1, LineWidth = 2, Style = PdfBoxLineStyle.Dashed, Dash = [4, 2] });
        p.Commit();
        w.Finish();
    }
}
