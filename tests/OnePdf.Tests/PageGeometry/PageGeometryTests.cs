using OnePdf.Tests.Pages;

namespace OnePdf.Tests.PageGeometry;

public class PageGeometryTests
{
    private static PdfDocument Edit(byte[]? bytes = null) =>
        PdfDocument.Open(bytes ?? BookFixture.Book("G", 3, form: false), new PdfOpenOptions { AllowEditing = true });

    private static PdfDocument Reopen(Action<Stream> save)
    {
        var ms = new MemoryStream();
        save(ms);
        return PdfDocument.Open(ms.ToArray());
    }

    [Fact]
    public void EachBoxIsSetAndCleared()
    {
        using PdfDocument d = Edit();
        PdfPage p = d.Pages[0];
        var r = new PdfRectangle(10, 20, 200, 250);
        p.SetCropBox(r);
        Assert.Equal(r, p.CropBox);
        p.SetBleedBox(new PdfRectangle(5, 5, 250, 280));
        p.SetTrimBox(new PdfRectangle(20, 30, 190, 240));
        p.SetArtBox(new PdfRectangle(40, 50, 150, 200));
        Assert.Equal(new PdfRectangle(5, 5, 250, 280), p.BleedBox);
        Assert.Equal(new PdfRectangle(20, 30, 190, 240), p.TrimBox);
        Assert.Equal(new PdfRectangle(40, 50, 150, 200), p.ArtBox);
        Assert.True(p.HasExplicitBox(PdfPageBox.TrimBox));

        p.ClearBleedBox();
        p.ClearTrimBox();
        p.ClearArtBox();
        Assert.Equal(p.CropBox, p.BleedBox);
        Assert.Equal(p.CropBox, p.TrimBox);
        Assert.Equal(p.CropBox, p.ArtBox);
        Assert.False(p.HasExplicitBox(PdfPageBox.ArtBox));

        p.ClearCropBox();
        Assert.Equal(p.MediaBox, p.CropBox);
        Assert.Same(p, d.Pages[0]);
    }

    [Fact]
    public void MediaBoxOverridesTheInheritedOneWithoutChangingTheTree()
    {
        using PdfDocument d = Edit();
        Assert.Equal(new PdfRectangle(0, 0, 300, 300), d.Pages[1].MediaBox);
        d.Pages[1].SetMediaBox(new PdfRectangle(0, 0, 400, 500));
        Assert.Equal(new PdfRectangle(0, 0, 400, 500), d.Pages[1].MediaBox);
        Assert.Equal(new PdfRectangle(0, 0, 300, 300), d.Pages[0].MediaBox);
        Assert.Equal(new PdfRectangle(0, 0, 300, 300), d.Pages[2].MediaBox);
        Assert.True(d.Pages[1].Dictionary.ContainsKey("MediaBox"));
    }

    [Fact]
    public void ReversedRectangleIsNormalizedOnWrite()
    {
        using PdfDocument d = Edit();
        d.Pages[0].SetTrimBox(new PdfRectangle(200, 250, 10, 20));
        var arr = (PdfArray)d.Pages[0].Dictionary.Get("TrimBox")!;
        Assert.Equal([10, 20, 200, 250], new[] { arr.GetDouble(0), arr.GetDouble(1), arr.GetDouble(2), arr.GetDouble(3) });
    }

    [Fact]
    public void BoxesAreClippedToTheMediaBoxOnRead()
    {
        using PdfDocument d = Edit();
        PdfPage p = d.Pages[0];
        p.SetTrimBox(new PdfRectangle(-50, -50, 100, 100));
        p.SetCropBox(new PdfRectangle(100, 100, 900, 900));
        Assert.Equal(new PdfRectangle(0, 0, 100, 100), p.TrimBox);
        Assert.Equal(new PdfRectangle(100, 100, 300, 300), p.CropBox);
        p.SetArtBox(new PdfRectangle(1000, 1000, 1100, 1100)); // misses the media box: default
        Assert.Equal(p.CropBox, p.ArtBox);
    }

    [Fact]
    public void ShrinkingTheMediaBoxClipsStoredBoxes()
    {
        using PdfDocument d = Edit();
        PdfPage p = d.Pages[0];
        p.SetTrimBox(new PdfRectangle(0, 0, 250, 250));
        p.SetArtBox(new PdfRectangle(200, 200, 300, 300));
        p.SetMediaBox(new PdfRectangle(0, 0, 100, 100));
        var trim = (PdfArray)p.Dictionary.Get("TrimBox")!;
        Assert.Equal(100, trim.GetDouble(2));
        Assert.False(p.Dictionary.ContainsKey("ArtBox"));
    }

    [Fact]
    public void InvalidValuesAreRejected()
    {
        using PdfDocument d = Edit();
        PdfPage p = d.Pages[0];
        Assert.Throws<ArgumentException>(() => p.SetTrimBox(new PdfRectangle(0, 0, double.NaN, 10)));
        Assert.Throws<ArgumentException>(() => p.SetTrimBox(new PdfRectangle(0, 0, double.PositiveInfinity, 10)));
        Assert.Throws<ArgumentException>(() => p.SetTrimBox(new PdfRectangle(5, 5, 5, 50)));
        Assert.Throws<ArgumentException>(() => p.SetMediaBox(new PdfRectangle(0, 0, 0, 0)));
        Assert.Throws<ArgumentException>(() => p.SetRotate(45));
        Assert.Throws<ArgumentException>(() => p.SetUserUnit(0));
        Assert.Throws<ArgumentException>(() => p.SetUserUnit(-1));
        Assert.Throws<ArgumentException>(() => p.SetUserUnit(double.NaN));
        Assert.Throws<ArgumentException>(() => p.SetPrintGeometry(new PdfRectangle(0, 0, 10, 10), -1));
        Assert.Throws<ArgumentException>(() => d.SetPrintGeometry(double.NaN));
        Assert.Throws<ArgumentException>(() => p.SetBoxColorInfo(PdfPageBox.MediaBox, new PdfBoxColorInfo()));
        Assert.Throws<ArgumentException>(() => p.SetBoxColorInfo(PdfPageBox.TrimBox, new PdfBoxColorInfo { Red = 2 }));
        Assert.Throws<ArgumentException>(() => p.SetBoxColorInfo(PdfPageBox.TrimBox, new PdfBoxColorInfo { Style = PdfBoxLineStyle.Dashed, Dash = [] }));
        Assert.Equal(new PdfRectangle(0, 0, 300, 300), p.TrimBox);
    }

    [Fact]
    public void ReadOnlyDocumentsRefuseEdits()
    {
        using PdfDocument d = PdfDocument.Open(BookFixture.Book("G", 1, form: false));
        Assert.Throws<InvalidOperationException>(() => d.Pages[0].SetTrimBox(new PdfRectangle(0, 0, 10, 10)));
    }

    [Fact]
    public void RotateIsSetNormalizedAndCleared()
    {
        using PdfDocument d = Edit();
        PdfPage p = d.Pages[0];
        p.SetRotate(-90);
        Assert.Equal(270, p.Rotate);
        p.SetRotate(450);
        Assert.Equal(90, p.Rotate);
        p.ClearRotate();
        Assert.Equal(0, p.Rotate);
        Assert.False(p.Dictionary.ContainsKey("Rotate"));
    }

    [Fact]
    public void ClearingOverridesAnInheritedRotateAndCropBox()
    {
        byte[] bytes = BookFixture.Book("G", 1, form: false);
        bytes = PdfBuilder.Replace(bytes, "/MediaBox [0 0 300 300]", "/MediaBox [0 0 300 300] /CropBox [10 10 100 100] /Rotate 90");
        using PdfDocument d = Edit(bytes);
        PdfPage p = d.Pages[0];
        Assert.Equal(90, p.Rotate);
        Assert.Equal(new PdfRectangle(10, 10, 100, 100), p.CropBox);
        p.ClearRotate();
        p.ClearCropBox();
        Assert.Equal(0, p.Rotate);
        Assert.Equal(p.MediaBox, p.CropBox);
    }

    [Fact]
    public void EffectiveSizesFollowRotateAndUserUnit()
    {
        using PdfDocument d = Edit();
        PdfPage p = d.Pages[0];
        p.SetMediaBox(new PdfRectangle(0, 0, 200, 100));
        PdfPageSize s = p.GetDisplaySize();
        Assert.Equal(new PdfPageSize(200, 100), s);
        p.SetRotate(90);
        Assert.Equal(new PdfPageSize(100, 200), p.GetDisplaySize());
        Assert.Equal(new PdfPageSize(200, 100), p.GetDisplaySize(applyRotate: false));
        p.SetUserUnit(2);
        PdfPageSize z = p.GetDisplaySize();
        Assert.Equal(200, z.Width);
        Assert.Equal(400, z.Height);
        Assert.Equal(400 / 72.0, z.HeightInches, 9);
        Assert.Equal(400 * 25.4 / 72, z.HeightMillimeters, 9);
        Assert.Equal(2, p.UserUnit);
        p.ClearUserUnit();
        Assert.Equal(1, p.UserUnit);
    }

    [Fact]
    public void PrintGeometrySetsMediaBleedAndTrimTogether()
    {
        using PdfDocument d = Edit();
        PdfPage p = d.Pages[0];
        p.SetCropBox(new PdfRectangle(50, 50, 60, 60));
        p.SetPrintGeometry(new PdfRectangle(10, 10, 110, 210), 9);
        Assert.Equal(new PdfRectangle(1, 1, 119, 219), p.MediaBox);
        Assert.Equal(p.MediaBox, p.BleedBox);
        Assert.Equal(new PdfRectangle(10, 10, 110, 210), p.TrimBox);
        Assert.Equal(p.MediaBox, p.CropBox);

        d.SetPrintGeometry(3);
        Assert.Equal(new PdfRectangle(7, 7, 113, 213), d.Pages[0].MediaBox);
        Assert.Equal(new PdfRectangle(10, 10, 110, 210), d.Pages[0].TrimBox);
        Assert.Equal(new PdfRectangle(-3, -3, 303, 303), d.Pages[2].MediaBox);
        Assert.Equal(new PdfRectangle(0, 0, 300, 300), d.Pages[2].TrimBox);
    }

    [Fact]
    public void BoxColorInfoRoundTrips()
    {
        using PdfDocument d = Edit();
        PdfPage p = d.Pages[0];
        Assert.Null(p.GetBoxColorInfo(PdfPageBox.TrimBox));
        var info = new PdfBoxColorInfo { Red = 1, Green = 0.5, Blue = 0, LineWidth = 2, Style = PdfBoxLineStyle.Dashed, Dash = [4, 2] };
        p.SetBoxColorInfo(PdfPageBox.TrimBox, info);
        p.SetBoxColorInfo(PdfPageBox.BleedBox, new PdfBoxColorInfo());
        PdfBoxColorInfo back = p.GetBoxColorInfo(PdfPageBox.TrimBox)!;
        Assert.Equal((1, 0.5, 0, 2), (back.Red, back.Green, back.Blue, back.LineWidth));
        Assert.Equal(PdfBoxLineStyle.Dashed, back.Style);
        Assert.Equal([4, 2], back.Dash);

        using PdfDocument r = Reopen(ms => d.Save(ms));
        PdfBoxColorInfo again = r.Pages[0].GetBoxColorInfo(PdfPageBox.TrimBox)!;
        Assert.Equal([4, 2], again.Dash);
        Assert.Equal(0.5, again.Green);
        Assert.Equal(PdfBoxLineStyle.Solid, r.Pages[0].GetBoxColorInfo(PdfPageBox.BleedBox)!.Style);

        p.SetBoxColorInfo(PdfPageBox.TrimBox, null);
        p.SetBoxColorInfo(PdfPageBox.BleedBox, null);
        Assert.Null(p.GetBoxColorInfo(PdfPageBox.TrimBox));
        Assert.False(p.Dictionary.ContainsKey("BoxColorInfo"));
    }

    private static void Apply(PdfDocument d)
    {
        d.Pages[0].SetPrintGeometry(new PdfRectangle(36, 36, 264, 264), 9);
        d.Pages[0].SetArtBox(new PdfRectangle(100, 100, 200, 200));
        d.Pages[0].SetRotate(90);
        d.Pages[0].SetUserUnit(2);
        d.Pages[0].SetBoxColorInfo(PdfPageBox.TrimBox, new PdfBoxColorInfo { Blue = 1 });
        d.Pages[1].SetCropBox(new PdfRectangle(5, 6, 7, 8));
    }

    private static void Check(PdfDocument r)
    {
        PdfPage p = r.Pages[0];
        Assert.Equal(new PdfRectangle(27, 27, 273, 273), p.MediaBox);
        Assert.Equal(p.MediaBox, p.BleedBox);
        Assert.Equal(new PdfRectangle(36, 36, 264, 264), p.TrimBox);
        Assert.Equal(new PdfRectangle(100, 100, 200, 200), p.ArtBox);
        Assert.Equal(90, p.Rotate);
        Assert.Equal(2, p.UserUnit);
        Assert.Equal(1, p.GetBoxColorInfo(PdfPageBox.TrimBox)!.Blue);
        Assert.Equal(new PdfRectangle(5, 6, 7, 8), r.Pages[1].CropBox);
        Assert.Equal(new PdfRectangle(0, 0, 300, 300), r.Pages[2].MediaBox);
    }

    [Fact]
    public void FullSaveRoundTrips()
    {
        using PdfDocument d = Edit();
        Apply(d);
        using PdfDocument r = Reopen(ms => d.Save(ms));
        Check(r);
        using PdfDocument r2 = Reopen(ms => d.Save(ms, new PdfSaveOptions { XrefFormat = PdfXrefFormat.Stream }));
        Check(r2);
    }

    [Fact]
    public void IncrementalSaveRoundTrips()
    {
        using PdfDocument d = Edit();
        Apply(d);
        using PdfDocument r = Reopen(ms => d.SaveIncremental(ms));
        Check(r);
    }

    [Fact]
    public void WriterWritesBoxesRotateAndUserUnit()
    {
        var ms = new MemoryStream();
        using (var w = new PdfDocumentWriter(ms, new PdfDocumentWriterOptions { LeaveOpen = true }))
        {
            PdfStreamPageBuilder p = w.BeginPage(300, 400, 90);
            p.SetCropBox(new PdfRectangle(250, 350, 10, 20));
            p.SetUserUnit(2);
            p.Commit();
            PdfStreamPageBuilder q = w.BeginPrintPage(200, 300, 9);
            q.Commit();
            w.Finish();
        }

        using PdfDocument r = PdfDocument.Open(ms.ToArray());
        Assert.Equal(new PdfRectangle(10, 20, 250, 350), r.Pages[0].CropBox);
        Assert.Equal(2, r.Pages[0].UserUnit);
        Assert.Equal(90, r.Pages[0].Rotate);
        Assert.Equal(new PdfRectangle(0, 0, 218, 318), r.Pages[1].MediaBox);
        Assert.Equal(new PdfRectangle(9, 9, 209, 309), r.Pages[1].TrimBox);
        Assert.Equal(r.Pages[1].MediaBox, r.Pages[1].BleedBox);
    }

    // Writes the edited file where an external tool can look at it (pdfinfo -box, qpdf --check) when asked to.
    [Fact]
    public void WritesFileForExternalTools()
    {
        string? dir = Environment.GetEnvironmentVariable("ONEPDF_GEOMETRY_OUT");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        using PdfDocument d = Edit();
        Apply(d);
        using (var f = File.Create(Path.Combine(dir, "geometry-full.pdf")))
        {
            d.Save(f);
        }

        using PdfDocument e = Edit();
        Apply(e);
        using var g = File.Create(Path.Combine(dir, "geometry-incremental.pdf"));
        e.SaveIncremental(g);
    }
}
