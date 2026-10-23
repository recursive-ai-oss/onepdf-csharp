using System.Diagnostics;
using UglyToad.PdfPig;

namespace OnePdf.Tests.Creation;

public class ContentBuilderTests
{
    private static PdfDocument NewDoc(out PdfPage page, double w = 612, double h = 792)
    {
        PdfDocument doc = PdfDocument.CreateEmpty();
        page = doc.InsertBlankPage(0, w, h);
        return doc;
    }

    private static byte[] BuildPageWithContent(string content)
    {
        using PdfDocument doc = NewDoc(out PdfPage page);
        page.Dictionary.SetEntry("Contents", doc.AddObject(doc.CreateStream(System.Text.Encoding.ASCII.GetBytes(content), false)));
        return CreationSupport.Save(doc);
    }

    private static CreationSupport.Collector Interpret(byte[] pdf, int page = 0) => Interpret(pdf, page, out _);

    private static CreationSupport.Collector Interpret(byte[] pdf, int page, out PdfDocument keep)
    {
        keep = PdfDocument.Open(pdf);
        var c = new CreationSupport.Collector();
        keep.Pages[page].Interpret(c, null);
        return c;
    }

    [Fact]
    public void PathsFillStrokeClipAndStateArriveAtTheDevice()
    {
        using PdfDocument doc = NewDoc(out PdfPage page);
        using (PdfPageContentBuilder b = page.BeginContent())
        {
            b.SetFillRgb(1, 0, 0);
            b.Rectangle(100, 200, 50, 30);
            b.Fill();

            b.Save();
            b.SetStrokeCmyk(0, 1, 1, 0);
            b.SetLineWidth(3);
            b.SetLineCap(PdfLineCap.Round);
            b.SetLineJoin(PdfLineJoin.Bevel);
            b.SetMiterLimit(5);
            b.SetDash([4, 2], 1);
            b.MoveTo(10, 10);
            b.LineTo(110, 10);
            b.CurveTo(120, 20, 120, 40, 110, 50);
            b.ClosePath();
            b.Stroke();
            b.Restore();

            b.SetFillGray(0.5);
            b.Ellipse(300, 300, 100, 50);
            b.Fill(evenOdd: true);

            b.Save();
            b.Rectangle(0, 0, 10, 10);
            b.Clip();
            b.Rectangle(5, 5, 20, 20);
            b.FillAndStroke();
            b.Restore();
            b.Commit();
        }

        byte[] pdf = CreationSupport.Save(doc);
        CreationSupport.Collector c = Interpret(pdf);
        var fill = c.Paths[0];
        Assert.True(fill.Fill);
        Assert.Equal(100, fill.Bounds.Left, 0.01);
        Assert.Equal(792 - 230, fill.Bounds.Bottom, 0.01);
        Assert.Equal(150, fill.Bounds.Right, 0.01);
        Assert.Equal(792 - 200, fill.Bounds.Top, 0.01);
        Assert.Equal(1, fill.State.FillColor.C0, 1e-9);

        var stroke = c.Paths.First(p => p.Stroke);
        Assert.Equal(3, stroke.State.LineWidth, 1e-9);
        Assert.Equal(1, stroke.State.LineCap);
        Assert.Equal(2, stroke.State.LineJoin);
        Assert.Equal(5, stroke.State.MiterLimit, 1e-9);
        Assert.Equal([4.0, 2.0], stroke.State.Dash.ToArray());
        Assert.Equal(1, stroke.State.DashPhase, 1e-9);

        var ellipse = c.Paths.First(p => Math.Abs(p.Bounds.Width - 100) < 0.01);
        Assert.Equal(300, ellipse.Bounds.Left, 0.01);
        Assert.Equal(50, ellipse.Bounds.Height, 0.01);
    }

    [Fact]
    public void TransformStackPlacesContent()
    {
        using PdfDocument doc = NewDoc(out PdfPage page, 200, 200);
        PdfCreatedFont f;
        using (PdfPageContentBuilder b = page.BeginContent())
        {
            f = doc.AddStandardFont("Helvetica");
            b.SetFont(f, 10);
            b.Save();
            b.Translate(100, 50);
            b.Rotate(90);
            b.Scale(2, 2);
            b.Rectangle(0, 0, 10, 5);
            b.Fill();
            b.DrawText("Hi", 0, 0);
            b.Restore();
            b.Commit();
        }

        CreationSupport.Collector c = Interpret(CreationSupport.Save(doc));
        // Rotated 90 degrees counter-clockwise: the 10x5 rectangle becomes 10 tall and 20 wide... scaled by 2: 20 wide x 10... check extents.
        var r = c.Paths[0].Bounds;
        Assert.Equal(10, r.Width, 0.01);  // 5 * 2
        Assert.Equal(20, r.Height, 0.01); // 10 * 2
        Assert.Equal(100 - 10, r.Left, 0.01);
        Assert.Equal(200 - 50 - 20, r.Bottom, 0.01);
        Assert.Equal("Hi", c.Runs[0].Text);
        Assert.Equal(100, c.Runs[0].X, 0.01);
        Assert.Equal(150, c.Runs[0].Y, 0.01);
    }

    [Fact]
    public void RestoreWithoutSaveAndNonFiniteNumbersAreRefused()
    {
        using PdfDocument doc = NewDoc(out PdfPage page);
        using PdfPageContentBuilder b = page.BeginContent();
        Assert.Throws<InvalidOperationException>(() => b.Restore());
        Assert.Throws<ArgumentOutOfRangeException>(() => b.MoveTo(double.NaN, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => b.SetLineWidth(-1));
        Assert.Throws<InvalidOperationException>(() => b.DrawText("x", 0, 0));
        for (int i = 0; i < 28; i++)
        {
            b.Save();
        }

        Assert.Throws<InvalidOperationException>(() => b.Save());
        b.Commit(); // closes the open levels
        using PdfDocument back = PdfDocument.Open(CreationSupport.Save(doc));
        Assert.Equal(1, back.PageCount);
    }

    [Fact]
    public void UncommittedBuilderChangesNothingAndReadOnlyDocumentsRefuse()
    {
        using PdfDocument doc = NewDoc(out PdfPage page);
        byte[] before = CreationSupport.Save(doc);
        using (PdfPageContentBuilder b = page.BeginContent())
        {
            b.Rectangle(1, 1, 5, 5);
            b.Fill();
        }

        Assert.Equal(before, CreationSupport.Save(doc));
        using PdfDocument ro = PdfDocument.Open(before);
        Assert.Throws<InvalidOperationException>(() => ro.Pages[0].BeginContent());
        Assert.Throws<InvalidOperationException>(() => ro.AddStandardFont("Helvetica"));
    }

    [Fact]
    public void OpacityAndBlendModeUseExtGStateWithDeduplication()
    {
        using PdfDocument doc = NewDoc(out PdfPage page);
        using (PdfPageContentBuilder b = page.BeginContent())
        {
            b.SetFillOpacity(0.5);
            b.Rectangle(0, 0, 10, 10);
            b.Fill();
            b.SetFillOpacity(0.5);
            b.SetStrokeOpacity(0.25);
            b.SetBlendMode(PdfBlendMode.Multiply);
            b.Rectangle(20, 0, 10, 10);
            b.FillAndStroke();
            b.Commit();
        }

        using PdfDocument back = PdfDocument.Open(CreationSupport.Save(doc));
        PdfDictionary gs = back.Pages[0].Resources.GetDictionary("ExtGState")!;
        Assert.Equal(3, gs.Count); // ca .5 (once), CA .25, BM Multiply
        var c = new CreationSupport.Collector();
        back.Pages[0].Interpret(c, null);
        Assert.Equal(0.5, c.Paths[0].State.FillAlpha, 1e-9);
        Assert.Equal(0.25, c.Paths[1].State.StrokeAlpha, 1e-9);
        Assert.Equal("Multiply", c.Paths[1].State.BlendMode);
    }

    [Fact]
    public void OverlayWrapsExistingContentSoItsLeftoversCannotLeak()
    {
        byte[] src = BuildPageWithContent("1 0 0 rg 2 0 0 2 0 0 cm q 0 0 10 10 re f");
        using PdfDocument doc = PdfDocument.Open(src, new PdfOpenOptions { AllowEditing = true });
        using (PdfPageContentBuilder b = doc.Pages[0].BeginContent())
        {
            b.Rectangle(0, 0, 10, 10);
            b.Fill();
            b.Commit();
        }

        CreationSupport.Collector c = Interpret(CreationSupport.Save(doc));
        Assert.Equal(2, c.Paths.Count);
        Assert.Equal(20, c.Paths[0].Bounds.Width, 0.01); // the page's own square, doubled by its cm
        Assert.Equal(10, c.Paths[1].Bounds.Width, 0.01); // ours: unaffected by the leftover cm and red colour
        Assert.Equal(0, c.Paths[1].State.FillColor.C0, 1e-9);
    }

    [Fact]
    public void UnderlayComesFirstAndOtherPagesKeepTheirSharedResources()
    {
        // Two pages share one resource dictionary through the page tree; adding to one must not touch the other.
        using PdfDocument doc = NewDoc(out PdfPage page);
        doc.InsertBlankPage(1, 612, 792);
        PdfCreatedFont f = doc.AddStandardFont("Courier");
        using (PdfPageContentBuilder b = doc.Pages[0].BeginContent(PdfContentPlacement.Underlay))
        {
            b.SetFont(f, 9);
            b.DrawText("under", 10, 10);
            b.Commit();
        }

        Assert.Equal("under", CreationSupport.Normalize(CreationSupport.AllText(CreationSupport.Save(doc))));
        using PdfDocument back = PdfDocument.Open(CreationSupport.Save(doc));
        Assert.Equal(0, back.Pages[1].Resources.Count);
    }

    [Fact]
    public void ImagesJpegRawAndAlphaAndRePlacement()
    {
        string dir = Path.Combine(CreationSupport.Root(), "testdata", "generated", "images");
        byte[] rgb = File.ReadAllBytes(Path.Combine(dir, "rgb16.jpg"));
        byte[] gray = File.ReadAllBytes(Path.Combine(dir, "gray16.jpg"));
        byte[] cmyk = File.ReadAllBytes(Path.Combine(dir, "cmyk16-adobe.jpg"));
        using PdfDocument doc = NewDoc(out PdfPage page);
        PdfCreatedImage j = doc.AddJpegImage(rgb);
        Assert.Equal(16, j.Width);
        PdfCreatedImage g = doc.AddJpegImage(gray);
        PdfCreatedImage k = doc.AddJpegImage(cmyk);
        byte[] samples = new byte[4 * 3 * 3];
        byte[] alpha = new byte[4 * 3];
        for (int i = 0; i < alpha.Length; i++)
        {
            alpha[i] = (byte)(i * 20);
        }

        PdfCreatedImage raw = doc.AddRawImage(4, 3, PdfImageColor.Rgb, samples, alpha);
        Assert.True(raw.HasAlpha);
        using (PdfPageContentBuilder b = page.BeginContent())
        {
            b.DrawImage(j, 10, 20, 100, 80);
            b.DrawImage(g, 120, 20, 50, 50);
            b.DrawImage(k, 180, 20, 50, 50);
            b.DrawImage(raw, 240, 20, 40, 30);
            b.DrawImage(j, 300, 20, 100, 80);
            b.Commit();
        }

        byte[] pdf = CreationSupport.Save(doc);
        CreationSupport.Collector c = Interpret(pdf, 0, out PdfDocument keep);
        using PdfDocument keepAlive = keep;
        Assert.Equal(5, c.Images.Count);
        Assert.Equal(100, c.Images[0].Ctm.A, 0.01);
        Assert.Equal(10, c.Images[0].Ctm.E, 0.01);
        Assert.Equal(16, (int)c.Images[0].Image.Dictionary.GetInt("Width"));
        Assert.Equal("DCTDecode", c.Images[0].Image.Dictionary.GetName("Filter"));
        Assert.NotNull(c.Images[3].Image.Dictionary.Get("SMask"));
        // The JPEG bytes are embedded unchanged.
        Assert.Equal(rgb, c.Images[0].Image.GetEncodedData().ToArray());

        // Re-placing an image that was read back from the page.
        using PdfDocument ed = PdfDocument.Open(pdf, new PdfOpenOptions { AllowEditing = true });
        PdfImage existing = c.Images[0].Image;
        PdfImage fromEd = default;
        var grab = new ImageGrabber();
        ed.Pages[0].Interpret(grab, null);
        fromEd = grab.First;
        PdfPage second = ed.InsertBlankPage(1, 200, 200);
        using (PdfPageContentBuilder b = second.BeginContent())
        {
            b.DrawImage(fromEd, 0, 0, 100, 100);
            b.Commit();
        }

        _ = existing;
        CreationSupport.Collector c2 = Interpret(CreationSupport.Save(ed), 1, out PdfDocument keep2);
        using PdfDocument keepAlive2 = keep2;
        Assert.Single(c2.Images);
        Assert.Equal(rgb, c2.Images[0].Image.GetEncodedData().ToArray());
        Assert.Throws<ArgumentException>(() => ed.Pages[0].BeginContent().DrawImage(existing, 0, 0, 1, 1));
    }

    private static int XObjectNumber(PdfPage page, string name) =>
        page.Resources.GetDictionary("XObject")!.GetRaw(name) is PdfReference r ? r.Number : throw new InvalidOperationException(name);

    private sealed class ImageGrabber : PdfDevice
    {
        public PdfImage First { get; private set; }

        public override void DrawImage(in PdfImage image, in PdfGraphicsState state)
        {
            if (First.Stream is null)
            {
                First = image;
            }
        }
    }

    [Fact]
    public void BadImagesAreTypedErrors()
    {
        using PdfDocument doc = NewDoc(out _);
        Assert.Throws<PdfFormatException>(() => doc.AddJpegImage(new byte[] { 1, 2, 3, 4, 5 }));
        Assert.Throws<PdfFormatException>(() => doc.AddJpegImage(new byte[] { 0xFF, 0xD8, 0xFF, 0xDA, 0, 2 }));
        Assert.Throws<ArgumentException>(() => doc.AddRawImage(2, 2, PdfImageColor.Rgb, new byte[5]));
        Assert.Throws<ArgumentException>(() => doc.AddRawImage(0, 2, PdfImageColor.Gray, Array.Empty<byte>()));
        Assert.Throws<PdfLimitException>(() => doc.AddRawImage(1 << 20, 1 << 20, PdfImageColor.Gray, new byte[1]));
    }

    [Fact]
    public void FormXObjectIsDrawnManyTimesAndSharedAcrossPages()
    {
        using PdfDocument doc = NewDoc(out PdfPage page);
        doc.InsertBlankPage(1, 612, 792);
        PdfCreatedFont f = doc.AddStandardFont("Helvetica-Bold");
        PdfFormContentBuilder fb = doc.BeginForm(new PdfRectangle(0, 0, 100, 30));
        fb.SetFont(f, 12);
        fb.DrawText("Stamp", 5, 8);
        fb.Rectangle(0, 0, 100, 30);
        fb.Stroke();
        PdfFormXObject form = fb.Commit();
        for (int p = 0; p < 2; p++)
        {
            using PdfPageContentBuilder b = doc.Pages[p].BeginContent();
            b.Save();
            b.Translate(50, 50);
            b.DrawForm(form);
            b.Translate(0, 100);
            b.DrawForm(form);
            b.Restore();
            b.Commit();
        }

        byte[] pdf = CreationSupport.Save(doc);
        CreationSupport.Collector c = Interpret(pdf, 1);
        Assert.Equal(2, c.Runs.Count);
        Assert.Equal(55, c.Runs[0].X, 0.01);
        Assert.Equal(792 - 58, c.Runs[0].Y, 0.01);
        Assert.Equal(792 - 158, c.Runs[1].Y, 0.01);
        using PdfDocument back = PdfDocument.Open(pdf);
        Assert.Equal(XObjectNumber(back.Pages[0], "OpFm1"), XObjectNumber(back.Pages[1], "OpFm1"));
    }

    [Fact]
    public void TextStateSpacingRenderModeAndWordSpacingForEmbeddedFonts()
    {
        using PdfDocument doc = NewDoc(out PdfPage page);
        PdfCreatedFont helv = doc.AddStandardFont("Helvetica");
        PdfCreatedFont emb = doc.AddFont(SyntheticFont.Build(new SyntheticFont.Options { Characters = "AB " }));
        using (PdfPageContentBuilder b = page.BeginContent())
        {
            b.SetFont(helv, 10);
            b.SetCharacterSpacing(1);
            b.SetWordSpacing(5);
            double w = b.MeasureText("a b");
            Assert.Equal(((556 + 278 + 556) * 10 / 1000.0) + 3 + 5, w, 1e-9);
            b.SetTextRenderMode(PdfTextRenderMode.Stroke);
            b.DrawText("a b", 10, 100);

            b.SetTextRenderMode(PdfTextRenderMode.Fill);
            b.SetFont(emb, 10);
            double we = b.MeasureText("A B");
            b.DrawText("A B", 10, 80);
            Assert.True(we > 0);
            b.Commit();
        }

        byte[] pdf = CreationSupport.Save(doc);
        Assert.Contains("A B", CreationSupport.AllText(pdf));
        CreationSupport.Collector c = Interpret(pdf);
        Assert.Equal(3, c.Runs.Count); // the embedded font's line is split after the space for word spacing
        Assert.Equal(10 + ((510 + 530) * 10 / 1000.0) + 2 + 5, c.Runs[2].X, 0.01);
    }

    [Fact]
    public void TextBoxWrapsAlignsAndReturnsWhatDidNotFit()
    {
        using PdfDocument doc = NewDoc(out PdfPage page);
        PdfCreatedFont helv = doc.AddStandardFont("Helvetica");
        using PdfPageContentBuilder b = page.BeginContent();
        b.SetFont(helv, 10);
        IReadOnlyList<string> lines = b.WrapText("the quick brown fox jumps over the lazy dog\nsecond paragraph", 80);
        Assert.True(lines.Count >= 4);
        foreach (string l in lines)
        {
            Assert.True(b.MeasureText(l) <= 80 + 1e-9, l);
        }

        Assert.EndsWith("paragraph", lines[^1]);
        string rest = b.DrawTextBox("the quick brown fox jumps over the lazy dog", 50, 500, 80, 30, PdfTextAlignment.Justify);
        Assert.NotEmpty(rest);
        Assert.EndsWith("lazy dog", rest);
        Assert.Equal(string.Empty, b.DrawTextBox("short", 50, 400, 200, 100, PdfTextAlignment.Right));
        double wide = b.MeasureText("Supercalifragilisticexpialidocious");
        Assert.All(b.WrapText("Supercalifragilisticexpialidocious", wide / 3), l => Assert.True(b.MeasureText(l) <= wide / 3 + 1e-9));
        b.Commit();
        string text = CreationSupport.AllText(CreationSupport.Save(doc));
        Assert.Contains("short", text);
        Assert.Contains("quick", text);
    }

    [Fact]
    public void StampsGoOnSelectedPagesRotatedAndShareOneForm()
    {
        using PdfDocument doc = NewDoc(out _);
        doc.InsertBlankPage(1, 612, 792);
        doc.InsertBlankPage(2, 612, 792);
        doc.SetPageRotation(2, 90);
        PdfCreatedFont f = doc.AddStandardFont("Helvetica-Bold");
        doc.AddTextStamp("CONFIDENTIAL", f, 48, new PdfStampOptions { Opacity = 0.3, RotationDegrees = 45, Color = (1, 0, 0), Placement = PdfContentPlacement.Underlay }, [0, 2]);
        byte[] pdf = CreationSupport.Save(doc);
        using PdfDocument back = PdfDocument.Open(pdf);
        Assert.Empty(back.Pages[1].Resources.GetDictionary("XObject")?.Keys ?? []);
        Assert.Equal(XObjectNumber(back.Pages[0], "OpFm1"), XObjectNumber(back.Pages[2], "OpFm1"));
        Assert.Contains("CONFIDENTIAL", Interpret(pdf, 0).Runs[0].Text);
        Assert.Contains("CONFIDENTIAL", Interpret(pdf, 2).Runs[0].Text);
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.AddTextStamp("x", f, 10, null, [9]));
    }

    [Fact]
    public void ImageStamp()
    {
        using PdfDocument doc = NewDoc(out _);
        PdfCreatedImage img = doc.AddRawImage(2, 2, PdfImageColor.Gray, [0, 80, 160, 255]);
        doc.AddImageStamp(img, 100, 100, new PdfStampOptions { Opacity = 0.5 });
        CreationSupport.Collector c = Interpret(CreationSupport.Save(doc));
        Assert.Single(c.Images);
    }

    [Fact]
    public void QpdfAndPdfPigAcceptCreatedFiles()
    {
        byte[] lib = CreationSupport.Font("LiberationSans-Regular.ttf") ?? SyntheticFont.Build();
        using PdfDocument doc = NewDoc(out PdfPage page);
        PdfCreatedFont emb = doc.AddFont(lib);
        PdfCreatedFont std = doc.AddStandardFont("Times-Roman");
        using (PdfPageContentBuilder b = page.BeginContent())
        {
            b.SetFont(emb, 12);
            b.DrawText(lib.Length > 1000 ? "Embedded text \u0394 \u0416" : "ABCD", 72, 700);
            b.SetFont(std, 12);
            b.DrawText("Standard text", 72, 680);
            b.SetOpacity(0.4);
            b.SetFillRgb(0, 0, 1);
            b.Rectangle(72, 600, 100, 40);
            b.Fill();
            b.Commit();
        }

        foreach (PdfSaveOptions o in new[]
        {
            new PdfSaveOptions { DocumentId = new byte[16] },
            new PdfSaveOptions { DocumentId = new byte[16], XrefFormat = PdfXrefFormat.Stream, CompressStreams = true },
        })
        {
            byte[] pdf = CreationSupport.Save(doc, o);
            (int code, string output) = CreationSupport.Qpdf(pdf);
            Assert.True(code == 0, output);
            string want = lib.Length > 1000 ? "Embedded text \u0394 \u0416 Standard text" : "ABCD Standard text";
            Assert.Equal(want, CreationSupport.Normalize(CreationSupport.AllText(pdf)));
            using PdfDocument pig = PdfDocument.Open(pdf);
            using UglyToad.PdfPig.PdfDocument p = UglyToad.PdfPig.PdfDocument.Open(pdf);
            string pigText = CreationSupport.Normalize(string.Join(" ", p.GetPage(1).GetWords().Select(w => w.Text)));
            Assert.Equal(want, pigText);
        }
    }
}
