using OnePdf.Tests.Content;
using static OnePdf.Tests.Images.ImageKit;

namespace OnePdf.Tests.Images;

/// <summary>The on-page target size passed to image decoders, and what happens when a decoder returns less than declared.</summary>
public class ImageTargetSizeTests
{
    private sealed class Capture(PdfImageOptions options) : PdfDevice
    {
        public List<PdfImageData?> Images { get; } = [];
        public List<(int W, int H)> Targets { get; } = [];

        public override void DrawImage(in PdfImage image, in PdfGraphicsState state)
        {
            Targets.Add((image.TargetWidth, image.TargetHeight));
            Images.Add(image.GetData(options));
        }
    }

    private static byte[] Page(string content, int width, int height, string imageEntries = "") =>
        TestPdf.Build(content, "/Resources << /XObject << /Im 5 0 R >> >>", b =>
            b.Stream(5, $"/Type /XObject /Subtype /Image /Width {width} /Height {height} /BitsPerComponent 8 /ColorSpace /DeviceGray /Filter /JPXDecode {imageEntries}", [1, 2, 3]),
            objects: 6);

    private static (FakeDecoder Decoder, Capture Device) Run(byte[] pdf, Func<PdfImageDecodeRequest, PdfDecodedImage?> decode)
    {
        var dec = new FakeDecoder(decode);
        var dev = new Capture(With("JPXDecode", dec));
        using PdfDocument doc = PdfDocument.Open(pdf);
        doc.Pages[0].Interpret(dev);
        return (dec, dev);
    }

    private static PdfDecodedImage Gray(int w, int h) => new(w, h, 1, 8, new byte[w * h]);

    [Fact]
    public void ScaledImageOnAPageGetsItsDevicePixelSize()
    {
        // 4000x4000 drawn at one inch (72 points) on a 72-unit-per-inch page space.
        var (dec, dev) = Run(Page("q 72 0 0 72 10 10 cm /Im Do Q", 4000, 4000), r => Gray(72, 72));
        Assert.Equal(1, dec.Calls);
        Assert.Equal(72, dec.Last.TargetWidth);
        Assert.Equal(72, dec.Last.TargetHeight);
        Assert.Equal(4000, dec.Last.DeclaredWidth);
        Assert.Equal((72, 72), dev.Targets[0]);
    }

    [Fact]
    public void ExtentRoundsUpAndFollowsRotation()
    {
        var (dec, _) = Run(Page("q 10.2 0 0 20.5 0 0 cm /Im Do Q", 400, 400), r => Gray(11, 21));
        Assert.Equal(11, dec.Last.TargetWidth);
        Assert.Equal(21, dec.Last.TargetHeight);

        // A quarter turn swaps the axes; 45 degrees widens both to the bounding box of the square.
        (dec, _) = Run(Page("q 0 20 -10 0 50 0 cm /Im Do Q", 400, 400), r => Gray(10, 20));
        Assert.Equal(10, dec.Last.TargetWidth);
        Assert.Equal(20, dec.Last.TargetHeight);
        (dec, _) = Run(Page("q 10 10 -10 10 50 0 cm /Im Do Q", 400, 400), r => Gray(20, 20));
        Assert.Equal(20, dec.Last.TargetWidth);
        Assert.Equal(20, dec.Last.TargetHeight);
    }

    [Fact]
    public void TargetNeverExceedsTheImageItself()
    {
        var (dec, dev) = Run(Page("q 500 0 0 300 0 0 cm /Im Do Q", 40, 30), r => Gray(40, 30));
        Assert.Equal(40, dec.Last.TargetWidth);
        Assert.Equal(30, dec.Last.TargetHeight);
        Assert.Equal((40, 30), dev.Targets[0]);
    }

    [Fact]
    public void ADegenerateMatrixLeavesTheTargetUnknown()
    {
        var (dec, dev) = Run(Page("q 0 0 0 0 5 5 cm /Im Do Q", 40, 30), r => Gray(40, 30));
        Assert.Equal(0, dec.Last.TargetWidth);
        Assert.Equal(0, dec.Last.TargetHeight);
        Assert.Equal((0, 0), dev.Targets[0]);
    }

    [Fact]
    public void DecodingOutsideAPageLeavesTheTargetUnknown()
    {
        using PdfDocument doc = PdfDocument.Open(Page("", 4000, 4000));
        var dec = new FakeDecoder(r => Gray(4000, 1));
        var s = (PdfStream)doc.GetObject(5)!;
        PdfImageData? img = PdfImageData.FromXObject(doc, s, null, With("JPXDecode", dec));
        Assert.NotNull(img);
        Assert.Equal(1, dec.Calls);
        Assert.Equal(0, dec.Last.TargetWidth);
        Assert.Equal(0, dec.Last.TargetHeight);
    }

    [Fact]
    public void AReducedBufferIsPassedThroughWithoutAWarning()
    {
        byte[] pdf = Page("q 72 0 0 72 10 10 cm /Im Do Q", 4000, 4000);
        var dec = new FakeDecoder(r => Gray(r.TargetWidth, r.TargetHeight));
        var dev = new Capture(With("JPXDecode", dec));
        using PdfDocument doc = PdfDocument.Open(pdf);
        doc.Pages[0].Interpret(dev);
        PdfImageData img = dev.Images[0]!;
        Assert.True(img.IsDecoded);
        Assert.Equal(72, img.Width);
        Assert.Equal(72, img.Height);
        Assert.Equal(72 * 72, img.Samples.Length);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Message.Contains("dictionary says", StringComparison.Ordinal));
    }

    [Fact]
    public void ADecoderThatIgnoresTheHintStillWorks()
    {
        var (_, dev) = Run(Page("q 72 0 0 72 10 10 cm /Im Do Q", 64, 64), r => Gray(64, 64));
        Assert.Equal(64, dev.Images[0]!.Width);
        Assert.Equal(64, dev.Images[0]!.Height);
    }
}
