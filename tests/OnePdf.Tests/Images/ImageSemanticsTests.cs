using static OnePdf.Tests.Images.ImageKit;

namespace OnePdf.Tests.Images;

/// <summary>Each PDF image rule on a hand-built image with the pixels worked out by hand.</summary>
public class ImageSemanticsTests
{
    [Fact]
    public void Gray8PassesThroughAndDecodeInverts()
    {
        using Result a = Make("/Width 2 /Height 2 /BitsPerComponent 8 /ColorSpace /DeviceGray", [0, 64, 128, 255]);
        Assert.True(a.Img.IsDecoded);
        Assert.Equal(PdfColorSpaceKind.DeviceGray, a.Img.ColorSpace!.Kind);
        Assert.Equal(new byte[] { 0, 64, 128, 255 }, Samples(a.Img));
        Assert.Null(a.Img.Alpha);

        using Result b = Make("/Width 2 /Height 2 /BitsPerComponent 8 /ColorSpace /DeviceGray /Decode [1 0]", [0, 64, 128, 255]);
        Assert.Equal(new byte[] { 255, 191, 127, 0 }, Samples(b.Img));
    }

    [Fact]
    public void OneBitRowsArePaddedToBytes()
    {
        // 10 pixels per row: 1010101010 (0xAA 0x80) then 0101010101 (0x55 0x40).
        using Result r = Make("/Width 10 /Height 2 /BitsPerComponent 1 /ColorSpace /DeviceGray", [0xAA, 0x80, 0x55, 0x40]);
        byte[] s = Samples(r.Img);
        Assert.Equal(20, s.Length);
        Assert.Equal(new byte[] { 255, 0, 255, 0, 255, 0, 255, 0, 255, 0 }, s[..10]);
        Assert.Equal(new byte[] { 0, 255, 0, 255, 0, 255, 0, 255, 0, 255 }, s[10..]);
    }

    [Fact]
    public void TwoAndFourBitSamplesScaleToFullRange()
    {
        using Result two = Make("/Width 4 /Height 1 /BitsPerComponent 2 /ColorSpace /DeviceGray", [0b00_01_10_11]);
        Assert.Equal(new byte[] { 0, 85, 170, 255 }, Samples(two.Img));

        using Result four = Make("/Width 3 /Height 1 /BitsPerComponent 4 /ColorSpace /DeviceGray", [0x0F, 0x80]);
        Assert.Equal(new byte[] { 0, 255, 136 }, Samples(four.Img));
    }

    [Fact]
    public void SixteenBitSamplesKeepTheHighByte()
    {
        using Result r = Make("/Width 2 /Height 1 /BitsPerComponent 16 /ColorSpace /DeviceGray", [0x12, 0x34, 0xFF, 0xFF]);
        Assert.Equal(new byte[] { 0x12, 0xFF }, Samples(r.Img));
        using Result inv = Make("/Width 2 /Height 1 /BitsPerComponent 16 /ColorSpace /DeviceGray /Decode [1 0]", [0x12, 0x34, 0xFF, 0xFF]);
        Assert.Equal(new byte[] { 255 - 0x12, 0 }, Samples(inv.Img));
    }

    [Fact]
    public void RgbAndCmykInterleave()
    {
        using Result rgb = Make("/Width 2 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceRGB", [1, 2, 3, 4, 5, 6]);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, Samples(rgb.Img));
        using Result cmyk = Make("/Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceCMYK /Decode [1 0 1 0 1 0 1 0]", [0, 255, 10, 200]);
        Assert.Equal(new byte[] { 255, 0, 245, 55 }, Samples(cmyk.Img));
        Assert.Equal(PdfColorSpaceKind.DeviceCmyk, cmyk.Img.ColorSpace!.Kind);
    }

    [Fact]
    public void IndexedExpandsThroughThePaletteAndClipsToHival()
    {
        // hival 1: two entries. Index 2 and 3 are beyond it and use the last entry (8.6.6.3).
        using Result r = Make("/Width 4 /Height 1 /BitsPerComponent 8 /ColorSpace [/Indexed /DeviceRGB 1 <FF000000FF00>]", [0, 1, 2, 3]);
        Assert.Equal(PdfColorSpaceKind.DeviceRgb, r.Img.ColorSpace!.Kind);
        Assert.Equal(new byte[] { 255, 0, 0, 0, 255, 0, 0, 255, 0, 0, 255, 0 }, Samples(r.Img));
    }

    [Fact]
    public void IndexedWithShortTableReadsMissingEntriesAsZeroAndWarns()
    {
        using Result r = Make("/Width 2 /Height 1 /BitsPerComponent 8 /ColorSpace [/Indexed /DeviceRGB 3 <FFFFFF>]", [0, 2]);
        Assert.Equal(new byte[] { 255, 255, 255, 0, 0, 0 }, Samples(r.Img));
        Assert.Contains(r.Warnings, w => w.Contains("lookup table"));
    }

    [Fact]
    public void IndexedFourBitUsesIndexAsSample()
    {
        using Result r = Make("/Width 2 /Height 1 /BitsPerComponent 4 /ColorSpace [/Indexed /DeviceGray 2 <00 80 FF>]", [0x21]);
        Assert.Equal(new byte[] { 255, 128 }, Samples(r.Img));
    }

    [Fact]
    public void ColorKeyMaskMakesMatchingPixelsTransparent()
    {
        using Result r = Make("/Width 4 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray /Mask [10 20]", [0, 10, 20, 30]);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Alpha(r.Img));
        Assert.Equal(new byte[] { 0, 10, 20, 30 }, Samples(r.Img));
    }

    [Fact]
    public void ColorKeyNeedsEveryComponentInRange()
    {
        using Result r = Make("/Width 2 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceRGB /Mask [0 0 255 255 0 0]", [0, 255, 0, 0, 255, 1]);
        Assert.Equal(new byte[] { 0, 255 }, Alpha(r.Img));
    }

    [Fact]
    public void ColorKeyOnIndexedImagesTestsTheIndex()
    {
        using Result r = Make("/Width 3 /Height 1 /BitsPerComponent 8 /ColorSpace [/Indexed /DeviceGray 2 <00 80 FF>] /Mask [1 1]", [0, 1, 2]);
        Assert.Equal(new byte[] { 255, 0, 255 }, Alpha(r.Img));
    }

    [Fact]
    public void ImageMaskPaintsZeroSamplesByDefault()
    {
        // Row 1: 1010 -> paint where the bit is 0.
        using Result r = Make("/Width 4 /Height 1 /ImageMask true /BitsPerComponent 1", [0b1010_0000]);
        Assert.True(r.Img.IsStencil);
        Assert.True(r.Img.IsDecoded);
        Assert.Null(r.Img.ColorSpace);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Alpha(r.Img));
    }

    [Fact]
    public void ImageMaskDecodeOneZeroPaintsOneSamples()
    {
        using Result r = Make("/Width 4 /Height 1 /ImageMask true /Decode [1 0]", [0b1010_0000]);
        Assert.Equal(new byte[] { 255, 0, 255, 0 }, Alpha(r.Img));
    }

    [Fact]
    public void ExplicitMaskStreamKeepsItsOwnSize()
    {
        using Result r = Make("/Width 2 /Height 2 /BitsPerComponent 8 /ColorSpace /DeviceGray /Mask 11 0 R", [10, 20, 30, 40], null, null,
            new Obj(11, "/Type /XObject /Subtype /Image /Width 4 /Height 1 /ImageMask true /BitsPerComponent 1", [0b0011_0000]));
        Assert.Equal(4, r.Img.Alpha!.Width);
        Assert.Equal(1, r.Img.Alpha.Height);
        // Mask samples 0 paint (opaque), 1 are masked out.
        Assert.Equal(new byte[] { 255, 255, 0, 0 }, Alpha(r.Img));
    }

    [Fact]
    public void SoftMaskGivesAlphaAndMatteIsRemoved()
    {
        // One RGB pixel premultiplied against a white matte: black at alpha 128 stores 255 - 128 = 127.
        using Result r = Make("/Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceRGB /SMask 11 0 R", [127, 127, 127], null, null,
            new Obj(11, "/Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray /Matte [1 1 1]", [128]));
        Assert.Equal(new byte[] { 128 }, Alpha(r.Img));
        Assert.Equal(new byte[] { 0, 0, 0 }, Samples(r.Img));
        Assert.Equal([1.0, 1.0, 1.0], r.Img.Semantics.Matte);
    }

    [Fact]
    public void SoftMaskWithZeroAlphaTakesTheMatte()
    {
        using Result r = Make("/Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceRGB /SMask 11 0 R", [9, 9, 9], null, null,
            new Obj(11, "/Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray /Matte [0 1 0]", [0]));
        Assert.Equal(new byte[] { 0, 255, 0 }, Samples(r.Img));
    }

    [Fact]
    public void SoftMaskWinsOverMaskAndHonoursItsOwnDecode()
    {
        using Result r = Make("/Width 2 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray /SMask 11 0 R /Mask [0 255]", [1, 2], null, null,
            new Obj(11, "/Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray /Decode [1 0]", [0, 200]));
        Assert.Equal(new byte[] { 255, 55 }, Alpha(r.Img));
    }

    [Fact]
    public void LabImageUsesItsRanges()
    {
        using Result r = Make("/Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace [/Lab << /WhitePoint [0.9505 1 1.089] /Range [-128 127 -128 127] >>]", [255, 128, 0]);
        PdfColorSpace cs = r.Img.ColorSpace!;
        Assert.Equal(PdfColorSpaceKind.Lab, cs.Kind);
        // Default decode is [0 100 amin amax bmin bmax], so sample bytes pass through and map onto those ranges.
        Assert.Equal(new byte[] { 255, 128, 0 }, Samples(r.Img));
        Assert.Equal((-128.0, 127.0), cs.GetComponentRange(1));
    }

    [Fact]
    public void SeparationRunsItsTintFunction()
    {
        using Result r = Make("/Width 3 /Height 1 /BitsPerComponent 8 /ColorSpace [/Separation /Spot /DeviceCMYK 12 0 R]", [0, 255, 51], null, null,
            new Obj(12, "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 1 1 0] /N 1 >>"));
        Assert.Equal(PdfColorSpaceKind.DeviceCmyk, r.Img.ColorSpace!.Kind);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 255, 255, 0, 0, 51, 51, 0 }, Samples(r.Img));
    }

    [Fact]
    public void DeviceNRunsAType4Function()
    {
        // Two inks into RGB: r = 1 - a, g = 1 - b, b = 0.5.
        byte[] fn = Latin1("{ 1 exch sub exch 1 exch sub exch 0.5 }");
        using Result r = Make("/Width 2 /Height 1 /BitsPerComponent 8 /ColorSpace [/DeviceN [/A /B] /DeviceRGB 12 0 R]", [255, 0, 0, 255], null, null,
            new Obj(12, "/FunctionType 4 /Domain [0 1 0 1] /Range [0 1 0 1 0 1]", fn));
        Assert.Equal(PdfColorSpaceKind.DeviceRgb, r.Img.ColorSpace!.Kind);
        Assert.Equal(new byte[] { 0, 255, 128, 255, 0, 128 }, Samples(r.Img));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("tint"));
    }

    [Fact]
    public void SeparationWithoutUsableFunctionApproximatesInk()
    {
        using Result r = Make("/Width 2 /Height 1 /BitsPerComponent 8 /ColorSpace [/Separation /Spot /DeviceGray 12 0 R]", [0, 255], null, null,
            new Obj(12, "<< /FunctionType 9 >>"));
        Assert.Equal(PdfColorSpaceKind.DeviceGray, r.Img.ColorSpace!.Kind);
        Assert.Equal(new byte[] { 255, 0 }, Samples(r.Img));
        Assert.Contains(r.Warnings, w => w.Contains("tint transform"));
    }

    [Fact]
    public void SeparationWithWrongOutputCountIsRejected()
    {
        using Result r = Make("/Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace [/Separation /Spot /DeviceRGB 12 0 R]", [255], null, null,
            new Obj(12, "<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>"));
        Assert.Contains(r.Warnings, w => w.Contains("inputs"));
    }

    // 16x4 test pattern, CCITT Group 4 (made with an independent encoder; the first row is a white run of 0 then a
    // black run of 16). With the default BlackIs1 false the rows decode to 0000, FF00, CCCC, 0FF0.
    private static readonly byte[] G4 = Convert.FromHexString("26a0b998a5f08ee778510e002002");

    [Fact]
    public void CcittImageIsDecodedLikeAnyOtherFilter()
    {
        using Result r = Make("/Width 16 /Height 4 /BitsPerComponent 1 /ColorSpace /DeviceGray /Filter /CCITTFaxDecode /DecodeParms << /K -1 /Columns 16 /Rows 4 >>", G4);
        Assert.True(r.Img.IsDecoded);
        Assert.Null(r.Img.EncodedFilter);
        byte[] s = Samples(r.Img);
        Assert.Equal(64, s.Length);
        Assert.All(s[..16], v => Assert.Equal(0, v));
        Assert.Equal(new byte[] { 255, 255, 255, 255, 255, 255, 255, 255, 0, 0, 0, 0, 0, 0, 0, 0 }, s[16..32]);
        Assert.Equal(new byte[] { 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0 }, s[32..48]);
    }

    [Fact]
    public void CcittBlackIs1AndDecodeCompose()
    {
        using Result b1 = Make("/Width 16 /Height 4 /BitsPerComponent 1 /ColorSpace /DeviceGray /Filter /CCITTFaxDecode /DecodeParms << /K -1 /Columns 16 /Rows 4 /BlackIs1 true >>", G4);
        Assert.All(Samples(b1.Img)[..16], v => Assert.Equal(255, v));
        using Result inv = Make("/Width 16 /Height 4 /BitsPerComponent 1 /ColorSpace /DeviceGray /Decode [1 0] /Filter /CCITTFaxDecode /DecodeParms << /K -1 /Columns 16 /Rows 4 >>", G4);
        Assert.All(Samples(inv.Img)[..16], v => Assert.Equal(255, v));
    }

    [Fact]
    public void CcittImageMaskPaintsBlackPixels()
    {
        using Result r = Make("/Width 16 /Height 4 /ImageMask true /Filter /CCITTFaxDecode /DecodeParms << /K -1 /Columns 16 /Rows 4 >>", G4);
        Assert.True(r.Img.IsStencil);
        byte[] a = Alpha(r.Img);
        Assert.All(a[..16], v => Assert.Equal(255, v));
        Assert.Equal(0, a[16]);
        Assert.Equal(255, a[31]);
    }

    [Fact]
    public void InlineCcittImage()
    {
        using Result host = Make("/Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray", [0]);
        var dict = new PdfDictionary(null);
        dict.Set("W", new PdfInteger(16));
        dict.Set("H", new PdfInteger(4));
        dict.Set("BPC", new PdfInteger(1));
        dict.Set("CS", new PdfName("G"));
        dict.Set("F", new PdfName("CCF"));
        var dp = new PdfDictionary(null);
        dp.Set("K", new PdfInteger(-1));
        dp.Set("Columns", new PdfInteger(16));
        dict.Set("DP", dp);
        PdfImageData? img = PdfImageData.FromInline(host.Doc, dict, G4);
        Assert.True(img!.IsDecoded);
        Assert.Equal(0, img.Samples.Span[0]);
        Assert.Equal(255, img.Samples.Span[16]);
    }

    [Fact]
    public void NamedColorSpaceComesFromResources()
    {
        using Result r = Make("/Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /Cs1", [1, 2, 3], null, null,
            new Obj(20, "<< /ColorSpace << /Cs1 /DeviceRGB >> >>"));
        Assert.Equal(PdfColorSpaceKind.DeviceRgb, r.Img.ColorSpace!.Kind);
    }

    [Fact]
    public void TruncatedDataReadsAsZeroWithAWarning()
    {
        using Result r = Make("/Width 2 /Height 2 /BitsPerComponent 8 /ColorSpace /DeviceGray", [9, 9, 9]);
        Assert.Equal(new byte[] { 9, 9, 9, 0 }, Samples(r.Img));
        Assert.Contains(r.Warnings, w => w.Contains("needed"));
    }

    [Fact]
    public void FlateDataIsDecodedBeforeUnpacking()
    {
        using Result r = Make("/Width 2 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray /Filter /FlateDecode", PdfBuilder.Deflate([7, 8]));
        Assert.Equal(new byte[] { 7, 8 }, Samples(r.Img));
        Assert.Null(r.Img.EncodedFilter);
    }

    [Fact]
    public void InterpolateAndIntentPassThrough()
    {
        using Result r = Make("/Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray /Interpolate true /Intent /Perceptual", [1]);
        Assert.True(r.Img.Interpolate);
        Assert.Equal("Perceptual", r.Img.RenderingIntent);
    }

    [Fact]
    public void InlineImageWithAbbreviationsAndFilters()
    {
        using Result host = Make("/Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray", [0]);
        var d = new PdfDictionary(null);
        d.Set("W", new PdfInteger(2));
        d.Set("H", new PdfInteger(1));
        d.Set("BPC", new PdfInteger(8));
        d.Set("CS", new PdfName("RGB"));
        d.Set("F", new PdfName("AHx"));
        d.Set("D", Arr(new PdfInteger(1), new PdfInteger(0), new PdfInteger(0), new PdfInteger(1), new PdfInteger(0), new PdfInteger(1)));
        PdfImageData? img = PdfImageData.FromInline(host.Doc, d, Latin1("00 FF 10 20 30 40>"));
        Assert.NotNull(img);
        Assert.Equal(new byte[] { 255, 255, 16, 223, 48, 64 }, img.Samples.ToArray());
    }

    [Fact]
    public void InlineImageMaskUsesAbbreviatedKeys()
    {
        using Result host = Make("/Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray", [0]);
        var d = new PdfDictionary(null);
        d.Set("W", new PdfInteger(8));
        d.Set("H", new PdfInteger(1));
        d.Set("IM", new PdfBoolean(true));
        PdfImageData? img = PdfImageData.FromInline(host.Doc, d, new byte[] { 0xF0 });
        Assert.True(img!.IsStencil);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 255, 255, 255, 255 }, img.Alpha!.Values.ToArray());
    }

    private static PdfArray Arr(params PdfObject[] items)
    {
        var a = new PdfArray(null);
        foreach (PdfObject o in items)
        {
            a.Add(o);
        }

        return a;
    }

    [Fact]
    public void OversizeImageIsStoppedBeforeAllocation()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Assert.Throws<PdfLimitException>(() => Make("/Width 100000 /Height 100000 /BitsPerComponent 8 /ColorSpace /DeviceRGB", [1, 2, 3]));
        Assert.Contains("pixels", ex.Message);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64 << 20);

        // Under the pixel limit but over the decoded-size limit: also refused before the buffer exists.
        var limits = PdfLimits.Default with { MaxDecodedStreamBytes = 1000 };
        Assert.Throws<PdfLimitException>(() => Make("/Width 20 /Height 20 /BitsPerComponent 8 /ColorSpace /DeviceRGB", [1, 2, 3], null, limits));
    }

    [Fact]
    public void BadSizeOrColorSpaceIsRecordedNotThrown()
    {
        using Result r = Make("/Width 0 /Height 4 /BitsPerComponent 8 /ColorSpace /DeviceGray", [1]);
        Assert.Null(r.Image);
        Assert.Contains(r.Warnings, w => w.Contains("Width"));
        using Result m = Make("/Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /Nope", [200]);
        Assert.True(m.Img.IsDecoded);
        Assert.Contains(m.Warnings, w => w.Contains("Nope"));
    }

    [Fact]
    public void CyclicColorSpaceDoesNotRecurseForever()
    {
        using Result r = Make("/Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace 11 0 R", [1], null, null,
            new Obj(11, "[/Indexed 11 0 R 1 <00>]"));
        Assert.True(r.Img.IsDecoded);
        Assert.Contains(r.Warnings, w => w.Contains("colour space", StringComparison.OrdinalIgnoreCase) || w.Contains("Indexed"));
    }
}
