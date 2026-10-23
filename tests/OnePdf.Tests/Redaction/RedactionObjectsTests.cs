using System.Text;
using OnePdf.Tests.Images;

namespace OnePdf.Tests.Redaction;

/// <summary>Collects what a page paints (device space, y down) and the images with their samples.</summary>
internal sealed class PaintLog : PdfDevice
{
    public List<PdfBounds> Paths { get; } = [];
    public List<PdfImageData?> Images { get; } = [];
    public int Clips { get; private set; }

    public override void FillPath(in PdfPath path, bool evenOdd, in PdfGraphicsState state) => Paths.Add(state.Ctm.TransformBounds(path.ControlBounds()));

    public override void StrokePath(in PdfPath path, in PdfGraphicsState state) => Paths.Add(state.Ctm.TransformBounds(path.ControlBounds()));

    public override void PushClip(in PdfPath path, bool evenOdd, in PdfGraphicsState state) => Clips++;

    public override void DrawImage(in PdfImage image, in PdfGraphicsState state) => Images.Add(image.GetData(new PdfImageOptions { Decoders = Decoders }));

    public PdfImageDecoderRegistry Decoders { get; init; } = PdfImageDecoderRegistry.Default;

    public static PaintLog Of(byte[] pdf, PdfImageDecoderRegistry? decoders = null)
    {
        using PdfDocument d = PdfDocument.Open(pdf);
        var log = new PaintLog { Decoders = decoders ?? PdfImageDecoderRegistry.Default };
        d.Pages[0].Interpret(log, new PdfInterpretOptions { Annotations = PdfAnnotationMode.None });
        return log;
    }
}

public class VectorAndImageRedactionTests
{
    private static PdfRectangle R(double l, double b, double r, double t) => new(l, b, r, t);

    [Fact]
    public void PathsUnderTheAreaAreRemovedAndOthersStay()
    {
        byte[] pdf = RedactionSupport.Page("0 0 1 rg 20 20 60 60 re f 1 0 0 rg 120 120 50 50 re f 0 0 m 200 200 l S");
        Assert.Equal(3, PaintLog.Of(pdf).Paths.Count);
        byte[] after = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, R(30, 30, 50, 50))]);
        PaintLog log = PaintLog.Of(after);
        // The first square and the diagonal cross the area; the second square does not.
        Assert.Single(log.Paths);
        Assert.InRange(log.Paths[0].Left, 119.9, 120.1);
        Assert.DoesNotContain("20 20 60 60", Encoding.Latin1.GetString(RedactionSupport.Everything(after)));
    }

    [Fact]
    public void ClippingPathsSurviveTheirPaint()
    {
        byte[] pdf = RedactionSupport.Page("q 20 20 60 60 re W n 0 g 0 0 200 200 re f Q 1 0 0 rg 150 150 20 20 re f");
        byte[] after = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, R(30, 30, 50, 50))]);
        PaintLog log = PaintLog.Of(after);
        Assert.Single(log.Paths);
        Assert.Equal(1, log.Clips);
    }

    [Fact]
    public void ClipOutKeepsThePathButNotInsideTheArea()
    {
        byte[] pdf = RedactionSupport.Page("0 0 1 rg 20 20 160 160 re f");
        byte[] after = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, R(30, 30, 50, 50))], new PdfRedactionOptions { VectorMode = PdfRedactionVectorMode.ClipOut });
        PaintLog log = PaintLog.Of(after);
        Assert.Single(log.Paths);
        Assert.True(log.Clips >= 1);
    }

    [Fact]
    public void ShadingsThatReachTheAreaAreRemoved()
    {
        byte[] pdf = RedactionSupport.Page(
            "q 0 0 200 200 re W n /Sh1 sh Q",
            "/Resources << /Shading << /Sh1 6 0 R >> >>",
            b => b.Obj(6, "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 200 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >>"));
        byte[] after = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, R(30, 30, 50, 50))]);
        Assert.Contains("shading", RedactionSupport.DisplayList(pdf));
        Assert.DoesNotContain("shading", RedactionSupport.DisplayList(after));
    }

    private static byte[] RgbImagePdf(string extraObjs = "", string content = "q 100 0 0 100 50 50 cm /Im1 Do Q")
    {
        byte[] px = new byte[48];
        Array.Fill(px, (byte)200);
        return RedactionSupport.Page(content, "/Resources << /XObject << /Im1 6 0 R >> >>", b =>
        {
            b.Stream(6, "/Type /XObject /Subtype /Image /Width 4 /Height 4 /ColorSpace /DeviceRGB /BitsPerComponent 8", px);
            _ = extraObjs;
        });
    }

    [Fact]
    public void ImageSamplesUnderTheAreaAreBlanked()
    {
        byte[] after = RedactionSupport.Redact(RgbImagePdf(), [new PdfRedactionRegion(0, R(50, 100, 100, 150))]);
        PdfImageData img = PaintLog.Of(after).Images.Single()!;
        byte[] s = img.Samples.ToArray();
        // Rows 0 and 1, columns 0 and 1 (upper left quarter) are blank; the rest is as it was.
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                byte expect = x < 2 && y < 2 ? (byte)0 : (byte)200;
                Assert.Equal(expect, s[((y * 4) + x) * 3]);
            }
        }
    }

    [Fact]
    public void ASharedImageIsCopiedNotChanged()
    {
        byte[] pdf = RgbImagePdf(content: "q 100 0 0 100 50 50 cm /Im1 Do Q q 20 0 0 20 0 0 cm /Im1 Do Q");
        byte[] after = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, R(50, 100, 100, 150))]);
        List<PdfImageData?> images = PaintLog.Of(after).Images;
        Assert.Equal(2, images.Count);
        Assert.Equal(0, images[0]!.Samples.Span[0]);
        Assert.Equal(200, images[1]!.Samples.Span[0]);
    }

    [Fact]
    public void ImagesWithoutADecoderAreRemovedWhole()
    {
        byte[] pdf = RedactionSupport.Page("q 100 0 0 100 50 50 cm /Im1 Do Q", "/Resources << /XObject << /Im1 6 0 R >> >>",
            b => b.Stream(6, "/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /DCTDecode", Encoding.Latin1.GetBytes("SECRETJPEGBYTES")));
        using PdfDocument d = RedactionSupport.Edit(pdf);
        PdfRedactionResult r = d.Redact([new PdfRedactionRegion(0, R(50, 100, 100, 150))], new PdfRedactionOptions { Decoders = new PdfImageDecoderRegistry() });
        Assert.Equal(1, r.ImagesRemoved);
        byte[] after = RedactionSupport.Save(d);
        Assert.False(RedactionSupport.Contains(RedactionSupport.Everything(after), "SECRETJPEGBYTES"));
        Assert.Empty(PaintLog.Of(after).Images);
    }

    [Fact]
    public void AJpegGoesThroughTheRegisteredDecoderAndComesOutAsFlate()
    {
        string path = Path.Combine(CreationSupportRoot(), "testdata", "generated", "images", "rgb16.jpg");
        byte[] jpg = File.ReadAllBytes(path);
        byte[] pdf = RedactionSupport.Page("q 160 0 0 160 20 20 cm /Im1 Do Q", "/Resources << /XObject << /Im1 6 0 R >> >>",
            b => b.Stream(6, "/Type /XObject /Subtype /Image /Width 16 /Height 16 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", jpg));
        var reg = new PdfImageDecoderRegistry();
        reg.Register("DCTDecode", new ImageKit.OneImageDctDecoder());
        PdfImageData original = PaintLog.Of(pdf, reg).Images.Single()!;
        using PdfDocument d = RedactionSupport.Edit(pdf);
        d.Redact([new PdfRedactionRegion(0, R(20, 100, 100, 180))], new PdfRedactionOptions { Decoders = reg });
        byte[] after = RedactionSupport.Save(d);
        Assert.False(RedactionSupport.Contains(after, "DCTDecode"));
        PdfImageData img = PaintLog.Of(after).Images.Single()!;
        Assert.Equal(16, img.Width);
        byte[] o = original.Samples.ToArray(), s = img.Samples.ToArray();
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    byte expect = x < 8 && y < 8 ? (byte)0 : o[(((y * 16) + x) * 3) + c];
                    Assert.Equal(expect, s[(((y * 16) + x) * 3) + c]);
                }
            }
        }
    }

    [Fact]
    public void SoftMasksAreBlankedWithTheirImage()
    {
        byte[] px = new byte[48];
        Array.Fill(px, (byte)200);
        byte[] alpha = new byte[16];
        Array.Fill(alpha, (byte)255);
        byte[] pdf = RedactionSupport.Page("q 100 0 0 100 50 50 cm /Im1 Do Q", "/Resources << /XObject << /Im1 6 0 R >> >>", b =>
        {
            b.Stream(6, "/Type /XObject /Subtype /Image /Width 4 /Height 4 /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask 7 0 R", px);
            b.Stream(7, "/Type /XObject /Subtype /Image /Width 4 /Height 4 /ColorSpace /DeviceGray /BitsPerComponent 8", alpha);
        });
        byte[] after = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, R(50, 100, 100, 150))]);
        PdfImageData img = PaintLog.Of(after).Images.Single()!;
        Assert.NotNull(img.Alpha);
        Assert.Equal(0, img.Alpha!.Values.Span[0]);
        Assert.Equal(255, img.Alpha.Values.Span[15]);
    }

    [Fact]
    public void InlineImagesThatReachTheAreaAreRemoved()
    {
        byte[] pdf = RedactionSupport.Page("q 100 0 0 100 50 50 cm BI /W 2 /H 2 /CS /G /BPC 8 ID \u0001\u0002\u0003\u0004 EI Q 0 g 150 150 20 20 re f");
        Assert.Single(PaintLog.Of(pdf).Images);
        byte[] after = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, R(60, 60, 90, 90))]);
        Assert.Empty(PaintLog.Of(after).Images);
        Assert.Single(PaintLog.Of(after).Paths);
    }

    private static string CreationSupportRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (string? d = Path.GetDirectoryName(source); d is not null; d = Path.GetDirectoryName(d))
        {
            if (Directory.Exists(Path.Combine(d, "testdata")))
            {
                return d;
            }
        }

        throw new DirectoryNotFoundException("testdata");
    }
}
