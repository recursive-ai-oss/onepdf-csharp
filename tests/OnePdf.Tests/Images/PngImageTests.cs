using OnePdf.Tests.Creation;

namespace OnePdf.Tests.Images;

public class PngImageTests
{
    private static (PdfStream Image, PdfDocument Doc, PdfCreatedImage Created) Add(byte[] png)
    {
        PdfDocument doc = PdfDocument.CreateEmpty();
        PdfCreatedImage img = doc.AddPngImage(png);
        return ((PdfStream)doc.Resolve(img.Reference)!, doc, img);
    }

    private static readonly byte[] Palette16 = [.. Enumerable.Range(0, 48).Select(i => (byte)((i * 37) + 5))];

    public static IEnumerable<object[]> PassThroughCases()
    {
        // color type, depths
        foreach (int d in (int[])[1, 2, 4, 8, 16])
        {
            yield return [0, d];
        }

        foreach (int d in (int[])[8, 16])
        {
            yield return [2, d];
        }

        foreach (int d in (int[])[1, 2, 4, 8])
        {
            yield return [3, d];
        }
    }

    private static PngKit.Spec SpecFor(int type, int depth, int w, int h, bool interlaced)
    {
        var s = new PngKit.Spec(w, h, type, depth) { Interlaced = interlaced };
        if (type == 3)
        {
            int n = Math.Min(1 << depth, 16);
            s = s with { Palette = Palette16[..(n * 3)] };
        }

        return s;
    }

    [Theory]
    [MemberData(nameof(PassThroughCases))]
    public void NonInterlacedWithoutAlphaPassesTheIdatThroughUnchanged(int type, int depth)
    {
        PngKit.Spec spec = SpecFor(type, depth, 13, 9, false) with { SplitIdat = true };
        byte[] rows = PngKit.RandomRows(spec, type * 100 + depth);
        byte[] png = PngKit.Encode(spec, rows);
        (PdfStream image, PdfDocument doc, _) = Add(png);
        using (doc)
        {
            Assert.Equal("FlateDecode", image.Dictionary.GetName("Filter"));
            PdfDictionary parms = image.Dictionary.GetDictionary("DecodeParms")!;
            Assert.Equal(15, parms.GetInt("Predictor"));
            Assert.Equal(13, parms.GetInt("Columns"));
            Assert.Equal(depth, parms.GetInt("BitsPerComponent"));
            Assert.Equal(type == 2 ? 3 : 1, parms.GetInt("Colors"));
            Assert.True(PngKit.IdatPayload(png).AsSpan().SequenceEqual(image.GetStoredBytes(image.FilterNames).Span));
            Assert.Equal(rows, image.GetDecodedBytes().ToArray());
            Assert.Null(image.Dictionary.GetRaw("SMask"));
            if (type == 3)
            {
                PdfArray cs = image.Dictionary.GetArray("ColorSpace")!;
                Assert.Equal("Indexed", cs.GetName(0));
                Assert.Equal((1 << depth) - 1, cs.GetInt(2));
                Assert.Equal(3 * (1 << depth), ((PdfString)cs[3]!).Bytes.Length);
            }
        }
    }

    public static IEnumerable<object[]> DecodedCases()
    {
        foreach (int interlaced in (int[])[0, 1])
        {
            foreach (int d in (int[])[1, 2, 4, 8, 16])
            {
                yield return [0, d, interlaced];
            }

            foreach (int d in (int[])[8, 16])
            {
                yield return [2, d, interlaced];
                yield return [4, d, interlaced];
                yield return [6, d, interlaced];
            }

            foreach (int d in (int[])[1, 2, 4, 8])
            {
                yield return [3, d, interlaced];
            }
        }
    }

    [Theory]
    [MemberData(nameof(DecodedCases))]
    public void DecodedSamplesMatchTheKnownPixels(int type, int depth, int interlaced)
    {
        // Sizes that leave Adam7 passes empty (3 x 3) and partial bytes at row ends.
        foreach ((int w, int h) in ((int, int)[])[(3, 3), (17, 11), (1, 1), (8, 8)])
        {
            PngKit.Spec spec = SpecFor(type, depth, w, h, interlaced == 1);
            byte[] rows = PngKit.RandomRows(spec, w * 1000 + h);
            (PdfStream image, PdfDocument doc, PdfCreatedImage created) = Add(PngKit.Encode(spec, rows));
            using (doc)
            {
                bool alpha = type is 4 or 6;
                Assert.Equal(alpha, created.HasAlpha);
                int bytes = depth / 8;
                byte[] expectColor;
                byte[]? expectAlpha = null;
                if (alpha)
                {
                    int ch = spec.Channels;
                    int colorCh = ch - 1;
                    var color = new List<byte>();
                    var a = new List<byte>();
                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            int p = (y * spec.Stride) + (x * ch * bytes);
                            for (int k = 0; k < colorCh * bytes; k++)
                            {
                                color.Add(rows[p + k]);
                            }

                            for (int k = 0; k < bytes; k++)
                            {
                                a.Add(rows[p + (colorCh * bytes) + k]);
                            }
                        }
                    }

                    expectColor = [.. color];
                    expectAlpha = [.. a];
                }
                else
                {
                    expectColor = rows;
                }

                Assert.Equal(expectColor, image.GetDecodedBytes().ToArray());
                Assert.Equal(depth, image.Dictionary.GetInt("BitsPerComponent"));
                if (alpha)
                {
                    var mask = (PdfStream)image.Dictionary.Get("SMask")!;
                    Assert.Equal(depth, mask.Dictionary.GetInt("BitsPerComponent"));
                    Assert.Equal(expectAlpha, mask.GetDecodedBytes().ToArray());
                }
            }
        }
    }

    [Fact]
    public void PaletteTransparencyBecomesAColourKeyWhenItCanAndASoftMaskWhenItCannot()
    {
        PngKit.Spec spec = SpecFor(3, 4, 10, 6, false);
        byte[] rows = PngKit.RandomRows(spec, 5);

        // One run of fully transparent entries: colour key, image data still passed through.
        (PdfStream keyed, PdfDocument d1, PdfCreatedImage c1) = Add(PngKit.Encode(spec with { Trns = [255, 0, 0, 255] }, rows));
        using (d1)
        {
            PdfArray mask = keyed.Dictionary.GetArray("Mask")!;
            Assert.Equal<long>([1L, 2L], mask.Items.Select(o => ((PdfInteger)o!).Value));
            Assert.True(c1.HasAlpha);
            Assert.NotNull(keyed.Dictionary.GetDictionary("DecodeParms"));
        }

        // A partly transparent entry needs an alpha plane built from the indices.
        byte[] trns = [255, 128, 0, 77];
        (PdfStream soft, PdfDocument d2, _) = Add(PngKit.Encode(spec with { Trns = trns }, rows));
        using (d2)
        {
            var sm = (PdfStream)soft.Dictionary.Get("SMask")!;
            byte[] alpha = sm.GetDecodedBytes().ToArray();
            Assert.Equal(60, alpha.Length);
            for (int y = 0; y < 6; y++)
            {
                for (int x = 0; x < 10; x++)
                {
                    int idx = PngKit.GetBits(rows, (y * spec.Stride * 8) + (x * 4), 4);
                    Assert.Equal(idx < trns.Length ? trns[idx] : 255, alpha[(y * 10) + x]);
                }
            }

            Assert.Equal(rows, soft.GetDecodedBytes().ToArray());
        }
    }

    [Fact]
    public void GrayAndRgbTransparencyIsAColourKey()
    {
        PngKit.Spec gray = new PngKit.Spec(7, 5, 0, 8) { Trns = [0, 200] };
        (PdfStream g, PdfDocument d1, _) = Add(PngKit.Encode(gray, PngKit.RandomRows(gray, 1)));
        using (d1)
        {
            Assert.Equal<long>([200L, 200L], g.Dictionary.GetArray("Mask")!.Items.Select(o => ((PdfInteger)o!).Value));
        }

        PngKit.Spec rgb = new PngKit.Spec(7, 5, 2, 16) { Trns = [0, 1, 0, 2, 1, 0] };
        (PdfStream r, PdfDocument d2, _) = Add(PngKit.Encode(rgb, PngKit.RandomRows(rgb, 2)));
        using (d2)
        {
            Assert.Equal<long>([1L, 1L, 2L, 2L, 256L, 256L], r.Dictionary.GetArray("Mask")!.Items.Select(o => ((PdfInteger)o!).Value));
        }
    }

    [Fact]
    public void IccProfileBecomesIccBasedWhenItMatchesAndIsIgnoredWithAWarningWhenItDoesNot()
    {
        PngKit.Spec rgb = new PngKit.Spec(4, 4, 2, 8) { Iccp = PngKit.IccProfileChunk("RGB ") };
        (PdfStream img, PdfDocument d1, _) = Add(PngKit.Encode(rgb, PngKit.RandomRows(rgb, 3)));
        using (d1)
        {
            PdfArray cs = img.Dictionary.GetArray("ColorSpace")!;
            Assert.Equal("ICCBased", cs.GetName(0));
            var profile = (PdfStream)cs[1]!;
            Assert.Equal(3, profile.Dictionary.GetInt("N"));
            Assert.Equal(200, profile.GetDecodedBytes().Length);
        }

        PngKit.Spec wrong = rgb with { Iccp = PngKit.IccProfileChunk("GRAY") };
        (PdfStream img2, PdfDocument d2, _) = Add(PngKit.Encode(wrong, PngKit.RandomRows(wrong, 3)));
        using (d2)
        {
            Assert.Equal("DeviceRGB", img2.Dictionary.GetName("ColorSpace"));
            Assert.Contains(d2.Diagnostics, x => x.Message.Contains("iCCP", StringComparison.Ordinal));
        }

        PngKit.Spec indexed = SpecFor(3, 8, 4, 4, false) with { Iccp = PngKit.IccProfileChunk("RGB ") };
        (PdfStream img3, PdfDocument d3, _) = Add(PngKit.Encode(indexed, PngKit.RandomRows(indexed, 3)));
        using (d3)
        {
            PdfArray cs = img3.Dictionary.GetArray("ColorSpace")!;
            Assert.Equal("Indexed", cs.GetName(0));
            Assert.Equal("ICCBased", ((PdfArray)cs[1]!).GetName(0));
        }
    }

    [Fact]
    public void PngImageDrawsAndSavesThroughTheBuilder()
    {
        PngKit.Spec spec = new PngKit.Spec(8, 8, 6, 8);
        using PdfDocument doc = PdfDocument.CreateEmpty();
        PdfPage page = doc.InsertBlankPage(0, 200, 200);
        PdfCreatedImage img = doc.AddPngImage(PngKit.Encode(spec, PngKit.RandomRows(spec, 9)));
        using (PdfPageContentBuilder b = page.BeginContent())
        {
            b.DrawImage(img, 10, 10, 80, 80);
            b.Commit();
        }

        byte[] pdf = CreationSupport.Save(doc);
        using PdfDocument back = PdfDocument.Open(pdf);
        var c = new CreationSupport.Collector();
        back.Pages[0].Interpret(c, null);
        Assert.Single(c.Images);
    }

    [Fact]
    public void MalformedPngsFailWithATypedError()
    {
        PngKit.Spec spec = new PngKit.Spec(6, 6, 2, 8);
        byte[] good = PngKit.Encode(spec, PngKit.RandomRows(spec, 4));
        using PdfDocument doc = PdfDocument.CreateEmpty();

        Assert.Throws<PdfFormatException>(() => doc.AddPngImage(new byte[] { 1, 2, 3 }));
        Assert.Throws<PdfFormatException>(() => doc.AddPngImage(good.AsMemory(0, 40))); // cut inside the data
        Assert.Throws<PdfFormatException>(() => doc.AddPngImage(PngKit.Encode(spec with { BadCrcOnIdat = true }, PngKit.RandomRows(spec, 4))));

        byte[] badDepth = (byte[])good.Clone();
        badDepth[24] = 7; // IHDR bit depth
        Assert.Throws<PdfFormatException>(() => doc.AddPngImage(badDepth));

        // A truncated zlib stream is caught on the decode path, and a pass-through stream with a bad header at once.
        PngKit.Spec inter = spec with { Interlaced = true };
        byte[] interPng = PngKit.Encode(inter, PngKit.RandomRows(inter, 4));
        Assert.ThrowsAny<PdfException>(() => doc.AddPngImage(interPng.AsMemory(0, IndexOf(interPng, "IDAT") + 8)));
        using (var ms = new MemoryStream())
        {
            using (var zs = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, true))
            {
                zs.Write(new byte[20]); // far fewer rows than the image declares
            }

            Assert.Throws<PdfFormatException>(() => doc.AddPngImage(ReplaceIdat(interPng, ms.ToArray())));
        }

        byte[] noZlib = (byte[])good.Clone();
        int at = IndexOf(noZlib, "IDAT") + 4;
        noZlib[at] = 0xFF;
        Assert.ThrowsAny<PdfException>(() => doc.AddPngImage(noZlib));
    }

    [Fact]
    public void HugeDimensionsAreRefusedBeforeAnythingIsAllocated()
    {
        PngKit.Spec spec = new PngKit.Spec(6, 6, 6, 8);
        byte[] png = PngKit.Encode(spec, PngKit.RandomRows(spec, 4));
        // Declare 2^20 x 2^20: far past MaxImagePixels. The IHDR CRC is rewritten so only the limit can refuse it.
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 1 << 20);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), 1 << 20);
        FixIhdrCrc(png);
        using PdfDocument doc = PdfDocument.CreateEmpty();
        Assert.Throws<PdfLimitException>(() => doc.AddPngImage(png));

        // Within the pixel limit but a decoded size over the stream limit.
        PdfDocument small = PdfDocument.CreateEmpty(new PdfOpenOptions { Limits = PdfLimits.Default with { MaxDecodedStreamBytes = 100 } });
        using (small)
        {
            PngKit.Spec big = new PngKit.Spec(100, 100, 6, 8) { Interlaced = true };
            byte[] png2 = PngKit.Encode(big, PngKit.RandomRows(big, 1));
            Assert.Throws<PdfLimitException>(() => small.AddPngImage(png2));
        }
    }

    [Fact]
    public void DeflateBombInTheImageDataStaysWithinTheDeclaredSize()
    {
        // 1 MiB of zero filter bytes + zeros compresses to ~1 KiB; the image only declares 8x8, so decoding must stop at the declared size.
        byte[] zeros = new byte[1 << 20];
        byte[] z;
        using (var ms = new MemoryStream())
        {
            using (var zs = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.SmallestSize, true))
            {
                zs.Write(zeros);
            }

            z = ms.ToArray();
        }

        PngKit.Spec spec = new PngKit.Spec(8, 8, 6, 8);
        byte[] png = PngKit.Encode(spec, PngKit.RandomRows(spec, 1));
        byte[] bomb = ReplaceIdat(png, z);
        using PdfDocument doc = PdfDocument.CreateEmpty();
        PdfCreatedImage img = doc.AddPngImage(bomb); // data beyond the image is ignored (only the first 8 rows count)
        Assert.Equal(8, img.Width);
    }

    private static int IndexOf(byte[] data, string ascii)
    {
        byte[] t = System.Text.Encoding.ASCII.GetBytes(ascii);
        for (int i = 0; i + t.Length <= data.Length; i++)
        {
            if (data.AsSpan(i, t.Length).SequenceEqual(t))
            {
                return i;
            }
        }

        return -1;
    }

    private static void FixIhdrCrc(byte[] png)
    {
        uint c = 0xFFFFFFFFu;
        for (int i = 12; i < 12 + 4 + 13; i++)
        {
            c ^= png[i];
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
        }

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29), c ^ 0xFFFFFFFFu);
    }

    private static byte[] ReplaceIdat(byte[] png, byte[] zlib)
    {
        int idat = IndexOf(png, "IDAT") - 4;
        int len = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(idat));
        using var ms = new MemoryStream();
        ms.Write(png, 0, idat);
        byte[] head = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(head, (uint)zlib.Length);
        ms.Write(head);
        byte[] body = [.. "IDAT"u8, .. zlib];
        ms.Write(body);
        uint c = 0xFFFFFFFFu;
        foreach (byte b in body)
        {
            c ^= b;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
        }

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(head, c ^ 0xFFFFFFFFu);
        ms.Write(head);
        ms.Write(png, idat + 12 + len, png.Length - (idat + 12 + len));
        return ms.ToArray();
    }
}
