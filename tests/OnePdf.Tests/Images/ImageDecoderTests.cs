using static OnePdf.Tests.Images.ImageKit;

namespace OnePdf.Tests.Images;

/// <summary>The decoder registry end to end: real JPEG files through a OneImage-backed decoder, and fake decoders for JPX and JBIG2.</summary>
public class ImageDecoderTests
{
    private static string ImagePath(string name, [System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (string? d = Path.GetDirectoryName(source); d is not null; d = Path.GetDirectoryName(d))
        {
            string c = Path.Combine(d, "testdata", "generated", "images", name);
            if (File.Exists(c))
            {
                return c;
            }
        }

        throw new FileNotFoundException(name);
    }

    private static readonly PdfImageOptions Dct = With("DCTDecode", new OneImageDctDecoder());

    [Fact]
    public void NoDecoderGivesEncodedBytesAndADiagnostic()
    {
        byte[] jpg = File.ReadAllBytes(ImagePath("rgb16.jpg"));
        using Result r = Make("/Width 16 /Height 16 /BitsPerComponent 8 /ColorSpace /DeviceRGB /Filter /DCTDecode", jpg, new PdfImageOptions { Decoders = new PdfImageDecoderRegistry() });
        Assert.False(r.Img.IsDecoded);
        Assert.Equal("DCTDecode", r.Img.EncodedFilter);
        Assert.Equal(jpg, r.Img.EncodedData.ToArray());
        Assert.Equal(16, r.Img.Width);
        Assert.Contains(r.Warnings, w => w.Contains("No decoder"));
    }

    [Fact]
    public void RgbJpegDecodesNearTheGradient()
    {
        byte[] jpg = File.ReadAllBytes(ImagePath("rgb16.jpg"));
        using Result r = Make("/Width 16 /Height 16 /BitsPerComponent 8 /ColorSpace /DeviceRGB /Filter /DCTDecode", jpg, Dct);
        Assert.True(r.Img.IsDecoded);
        Assert.Equal(PdfColorSpaceKind.DeviceRgb, r.Img.ColorSpace!.Kind);
        byte[] s = Samples(r.Img);
        Assert.Equal(16 * 16 * 3, s.Length);
        AssertNear(s, (x, y) => [x * 16, y * 16, 128], 16, 3, 12);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void GrayJpegWithoutColorSpaceInfersFromComponents()
    {
        byte[] jpg = File.ReadAllBytes(ImagePath("gray16.jpg"));
        using Result r = Make("/Width 16 /Height 16 /BitsPerComponent 8 /Filter /DCTDecode", jpg, Dct);
        Assert.Equal(PdfColorSpaceKind.DeviceGray, r.Img.ColorSpace!.Kind);
        AssertNear(Samples(r.Img), (x, y) => [(x * 16 + y * 8) & 255], 16, 1, 12);
    }

    [Fact]
    public void AdobeCmykJpegIsInvertedByDecodeNotByTheLibrary()
    {
        byte[] jpg = File.ReadAllBytes(ImagePath("cmyk16-adobe.jpg"));
        Assert.Equal(0, OneImageDctDecoder.AdobeTransform(jpg));
        // Writers of Adobe CMYK JPEGs add /Decode [1 0 ...]; the stored values are inverted ink.
        using Result r = Make("/Width 16 /Height 16 /BitsPerComponent 8 /ColorSpace /DeviceCMYK /Decode [1 0 1 0 1 0 1 0] /Filter /DCTDecode", jpg, Dct);
        Assert.Equal(PdfColorSpaceKind.DeviceCmyk, r.Img.ColorSpace!.Kind);
        AssertNear(Samples(r.Img), (x, y) => [x * 16, y * 16, 0, 64], 16, 4, 14);

        // Without /Decode the stored (inverted) values pass through untouched: no hidden second inversion.
        using Result raw = Make("/Width 16 /Height 16 /BitsPerComponent 8 /ColorSpace /DeviceCMYK /Filter /DCTDecode", jpg, Dct);
        AssertNear(Samples(raw.Img), (x, y) => [255 - x * 16, 255 - y * 16, 255, 255 - 64], 16, 4, 14);
    }

    [Fact]
    public void FlateThenDctRunsFlateFirst()
    {
        byte[] jpg = File.ReadAllBytes(ImagePath("gray16.jpg"));
        using Result r = Make("/Width 16 /Height 16 /BitsPerComponent 8 /ColorSpace /DeviceGray /Filter [/FlateDecode /DCTDecode]", PdfBuilder.Deflate(jpg), Dct);
        Assert.True(r.Img.IsDecoded);
        Assert.Equal("DCTDecode", r.Img.EncodedFilter);
        Assert.Equal(jpg, r.Img.EncodedData.ToArray());
    }

    [Fact]
    public void PreferEncodedReturnsTheOriginalJpegBytesAndSemantics()
    {
        byte[] jpg = File.ReadAllBytes(ImagePath("cmyk16-adobe.jpg"));
        var counting = new FakeDecoder(_ => throw new InvalidOperationException("must not decode"));
        using Result r = Make("/Width 16 /Height 16 /BitsPerComponent 8 /ColorSpace /DeviceCMYK /Decode [1 0 1 0 1 0 1 0] /Mask [0 1 0 1 0 1 0 1] /Filter /DCTDecode", jpg, With("DCTDecode", counting, preferEncoded: true));
        Assert.Equal(0, counting.Calls);
        Assert.False(r.Img.IsDecoded);
        Assert.Equal(jpg, r.Img.EncodedData.ToArray());
        Assert.Equal("DCTDecode", r.Img.EncodedFilter);
        Assert.Equal(PdfColorSpaceKind.DeviceCmyk, r.Img.Semantics.ColorSpace!.Kind);
        Assert.Equal([1.0, 0, 1, 0, 1, 0, 1, 0], r.Img.Semantics.Decode);
        Assert.Equal(8, r.Img.Semantics.ColorKeyMask!.Count);
        Assert.Equal(8, r.Img.Semantics.BitsPerComponent);
    }

    [Fact]
    public void ColorTransformParameterReachesTheDecoder()
    {
        var dec = new FakeDecoder(q => new PdfDecodedImage(2, 1, 3, 8, new byte[6]));
        using Result r = Make("/Width 2 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceRGB /Filter /DCTDecode /DecodeParms << /ColorTransform 0 >>", [1, 2, 3], With("DCTDecode", dec));
        Assert.Equal(0, dec.Last.ColorTransform);
        Assert.Equal("DCTDecode", dec.Last.Filter);
        Assert.Equal(2, dec.Last.DeclaredWidth);
    }

    [Fact]
    public void CorruptJpegFallsBackToEncodedWithADiagnostic()
    {
        using Result r = Make("/Width 16 /Height 16 /BitsPerComponent 8 /ColorSpace /DeviceRGB /Filter /DCTDecode", [0xFF, 0xD8, 1, 2, 3, 4], Dct);
        Assert.False(r.Img.IsDecoded);
        Assert.Contains(r.Warnings, w => w.Contains("DCTDecode"));
    }

    [Fact]
    public void JpxUsesCodestreamColourWhenTheDictionaryHasNone()
    {
        var lab = PdfColorSpace.Rgb;
        var dec = new FakeDecoder(q => new PdfDecodedImage(2, 1, 3, 8, new byte[] {10, 20, 30, 40, 50, 60}) { ColorSpace = lab });
        using Result r = Make("/Width 2 /Height 1 /Filter /JPXDecode", [1], With("JPXDecode", dec));
        Assert.Equal(PdfColorSpaceKind.DeviceRgb, r.Img.ColorSpace!.Kind);
        Assert.Equal(new byte[] { 10, 20, 30, 40, 50, 60 }, Samples(r.Img));
    }

    [Fact]
    public void JpxIgnoresDecodeAndDictionaryColourSpaceOverridesTheCodestream()
    {
        var dec = new FakeDecoder(q => new PdfDecodedImage(1, 1, 1, 8, new byte[] {100}) { ColorSpace = PdfColorSpace.Rgb });
        using Result r = Make("/Width 1 /Height 1 /ColorSpace /DeviceGray /Decode [1 0] /Filter /JPXDecode", [1], With("JPXDecode", dec));
        Assert.Equal(PdfColorSpaceKind.DeviceGray, r.Img.ColorSpace!.Kind);
        Assert.Equal(new byte[] { 100 }, Samples(r.Img));
    }

    [Fact]
    public void JpxSMaskInDataTakesTheOpacityChannel()
    {
        var dec = new FakeDecoder(q => new PdfDecodedImage(2, 1, 1, 8, new byte[] {200, 255, 100, 0}) { HasAlpha = true });
        using Result r = Make("/Width 2 /Height 1 /SMaskInData 1 /Filter /JPXDecode", [1], With("JPXDecode", dec));
        Assert.Equal(new byte[] { 200, 100 }, Samples(r.Img));
        Assert.Equal(new byte[] { 255, 0 }, Alpha(r.Img));

        using Result pre = Make("/Width 2 /Height 1 /SMaskInData 2 /Filter /JPXDecode", [1], With("JPXDecode", new FakeDecoder(q => new PdfDecodedImage(2, 1, 1, 8, new byte[] {50, 128, 100, 255}) { HasAlpha = true })));
        // Premultiplied: 50 at alpha 128 un-premultiplies to 50 * 255 / 128 = 99.
        Assert.Equal(99, Samples(pre.Img)[0]);

        using Result off = Make("/Width 2 /Height 1 /Filter /JPXDecode", [1], With("JPXDecode", dec));
        Assert.Null(off.Img.Alpha);
    }

    [Fact]
    public void Jbig2GetsGlobalsAndIsInvertedToPdfPolarity()
    {
        var dec = new FakeDecoder(q => new PdfDecodedImage(8, 1, 1, 1, new byte[] {0b1100_0000}));
        using Result r = Make("/Width 8 /Height 1 /BitsPerComponent 1 /ColorSpace /DeviceGray /Filter /JBIG2Decode /DecodeParms << /JBIG2Globals 11 0 R >>", [9, 9], With("JBIG2Decode", dec), null,
            new Obj(11, "", [5, 6, 7]));
        Assert.Equal([5, 6, 7], dec.Last.Globals.ToArray());
        Assert.Equal([5, 6, 7], r.Img.EncodedGlobals.ToArray());
        // JBIG2 1 = black becomes DeviceGray 0 = black.
        Assert.Equal(new byte[] { 0, 0, 255, 255, 255, 255, 255, 255 }, Samples(r.Img));
    }

    [Fact]
    public void Jbig2StencilMaskPaintsTheBlackPixels()
    {
        var dec = new FakeDecoder(q => new PdfDecodedImage(4, 1, 1, 1, new byte[] {0b1010_0000}));
        using Result r = Make("/Width 4 /Height 1 /ImageMask true /Filter /JBIG2Decode", [9], With("JBIG2Decode", dec));
        Assert.True(r.Img.IsStencil);
        Assert.Equal(new byte[] { 255, 0, 255, 0 }, Alpha(r.Img));
    }

    [Fact]
    public void DecoderFailureAndMismatchesAreRecorded()
    {
        var bad = new FakeDecoder(_ => throw new PdfFormatException("truncated"));
        using Result a = Make("/Width 1 /Height 1 /Filter /JPXDecode", [1], With("JPXDecode", bad));
        Assert.False(a.Img.IsDecoded);
        Assert.Contains(a.Warnings, w => w.Contains("truncated"));

        var size = new FakeDecoder(_ => new PdfDecodedImage(3, 1, 1, 8, new byte[] {1, 2, 3}));
        using Result b = Make("/Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /JPXDecode", [1], With("JPXDecode", size));
        Assert.Equal(3, b.Img.Width);
        Assert.Contains(b.Warnings, w => w.Contains("data size"));

        var comps = new FakeDecoder(_ => new PdfDecodedImage(1, 1, 3, 8, new byte[] {1, 2, 3}));
        using Result c = Make("/Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /DCTDecode", [1], With("DCTDecode", comps));
        Assert.Equal(PdfColorSpaceKind.DeviceRgb, c.Img.ColorSpace!.Kind);
        Assert.Contains(c.Warnings, w => w.Contains("components"));
    }

    [Fact]
    public void OversizeDecoderOutputIsRefused()
    {
        var dec = new FakeDecoder(_ => new PdfDecodedImage(4096, 4096, 1, 8, new byte[16]));
        var limits = PdfLimits.Default with { MaxImagePixels = 1000 };
        Assert.Throws<PdfLimitException>(() => Make("/Width 10 /Height 10 /Filter /JPXDecode", [1], With("JPXDecode", dec), limits));
        Assert.Throws<PdfLimitException>(() => Make("/Width 10000 /Height 10000 /Filter /JPXDecode", [1], With("JPXDecode", dec), limits));
        Assert.Equal(1, dec.Calls);
    }

    [Fact]
    public void UnknownImageFilterKeepsEncodedData()
    {
        using Result r = Make("/Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray /Filter /FooDecode", [1, 2, 3]);
        Assert.False(r.Img.IsDecoded);
        Assert.Equal("FooDecode", r.Img.EncodedFilter);
        Assert.Contains(r.Warnings, w => w.Contains("FooDecode"));
    }

    [Fact]
    public void RegistryRegistersReplacesAndClones()
    {
        var reg = new PdfImageDecoderRegistry();
        var a = new FakeDecoder(_ => null);
        reg.Register("DCTDecode", a);
        PdfImageDecoderRegistry copy = reg.Clone();
        Assert.True(copy.TryGet("DCTDecode", out IPdfImageDecoder? got));
        Assert.Same(a, got);
        Assert.True(reg.Unregister("DCTDecode"));
        Assert.False(reg.TryGet("DCTDecode", out _));
        Assert.True(copy.TryGet("DCTDecode", out _));
        Assert.Throws<ArgumentException>(() => reg.Register("FlateDecode", a));
    }

    private static void AssertNear(byte[] s, Func<int, int, int[]> expect, int size, int comps, int tolerance)
    {
        Assert.Equal(size * size * comps, s.Length);
        int worst = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int[] e = expect(x, y);
                for (int c = 0; c < comps; c++)
                {
                    worst = Math.Max(worst, Math.Abs(s[(y * size + x) * comps + c] - Math.Clamp(e[c], 0, 255)));
                }
            }
        }

        Assert.True(worst <= tolerance, $"worst component error {worst} exceeds {tolerance}");
    }
}
