using OnePdf.FontPrograms;
using OnePdf.FontWriting;

namespace OnePdf.Tests.Creation;

public class FontWritingTests
{
    private static PdfDocument NewDoc(out PdfPage page)
    {
        PdfDocument doc = PdfDocument.CreateEmpty();
        page = doc.InsertBlankPage(0, 600, 300);
        return doc;
    }

    private static byte[] DrawAndSave(byte[] font, string text, PdfEmbedFontOptions? options = null)
    {
        using PdfDocument doc = NewDoc(out PdfPage page);
        PdfCreatedFont f = doc.AddFont(font, options);
        using PdfPageContentBuilder b = page.BeginContent();
        b.SetFont(f, 20);
        b.DrawText(text, 10, 100);
        b.Commit();
        return CreationSupport.Save(doc);
    }

    [Fact]
    public void SubsetKeepsCompositeComponentsAndOutlinesMatch()
    {
        byte[] font = SyntheticFont.Build();
        var original = SfntFont.Parse(font, 0);

        // Request only 'D' (glyph 4): C (3), A (1) and B (2) must follow it as its components.
        byte[] subset = TrueTypeSubsetter.Subset(original, [4]);
        var sub = SfntFont.Parse(subset, 0);
        Assert.Equal(5, sub.GlyphCount); // .notdef, D, then C, A, B in discovery order

        GlyphOutline want = original.GetOutline(4);
        GlyphOutline got = sub.GetOutline(1);
        Assert.Equal(want.Verbs, got.Verbs);
        Assert.Equal(want.Coords, got.Coords);
        Assert.False(want.IsEmpty);
        Assert.True(original.TryGetAdvance(4, out float adv));
        Assert.True(sub.TryGetAdvance(1, out float subAdv));
        Assert.Equal(adv, subAdv);
    }

    [Fact]
    public void SubsetIsAValidSfnt()
    {
        byte[] subset = TrueTypeSubsetter.Subset(SfntFont.Parse(SyntheticFont.Build(), 0), [1, 3]);
        Assert.Equal(0, subset.Length % 4);
        // Whole-file checksum rule: the sum of all 32-bit words equals 0xB1B0AFBA (head.checkSumAdjustment makes it so).
        Assert.Equal(0xB1B0AFBAu, SfntWriter.Checksum(subset));
        // Every table directory entry's checksum matches its (padded) data.
        int n = (subset[4] << 8) | subset[5];
        for (int i = 0; i < n; i++)
        {
            int e = 12 + (16 * i);
            uint tag = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(subset.AsSpan(e));
            uint sum = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(subset.AsSpan(e + 4));
            uint off = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(subset.AsSpan(e + 8));
            uint len = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(subset.AsSpan(e + 12));
            Assert.Equal(0u, off % 4);
            if (tag != 0x68656164)
            {
                Assert.Equal(sum, SfntWriter.Checksum(subset.AsSpan((int)off, (int)((len + 3) & ~3u))));
            }
        }
    }

    [Fact]
    public void CyclicCompositeIsATypedErrorAtSave()
    {
        var o = new SyntheticFont.Options();
        o.Glyphs[3] = SyntheticFont.Composite(4, 0, 0); // C -> D
        o.Glyphs[4] = SyntheticFont.Composite(3, 0, 0); // D -> C
        byte[] font = SyntheticFont.Build(o);
        using PdfDocument doc = NewDoc(out PdfPage page);
        PdfCreatedFont f = doc.AddFont(font);
        using PdfPageContentBuilder b = page.BeginContent();
        b.SetFont(f, 12);
        b.DrawText("C", 10, 10);
        b.Commit();
        Assert.Throws<PdfFormatException>(() => CreationSupport.Save(doc));
    }

    [Fact]
    public void SelfReferencingCompositeIsATypedError()
    {
        var o = new SyntheticFont.Options();
        o.Glyphs[3] = SyntheticFont.Composite(3, 0, 0);
        Assert.Throws<FontProgramException>(() => TrueTypeSubsetter.Subset(SfntFont.Parse(SyntheticFont.Build(o), 0), [3]));
    }

    [Fact]
    public void DeeplyNestedCompositesAreRefused()
    {
        var o = new SyntheticFont.Options { Glyphs = [[], SyntheticFont.Simple()] };
        for (int i = 2; i < 40; i++)
        {
            o.Glyphs.Add(SyntheticFont.Composite(i - 1, 0, 0));
        }

        Assert.Throws<FontProgramException>(() => TrueTypeSubsetter.Subset(SfntFont.Parse(SyntheticFont.Build(o), 0), [39]));
    }

    [Fact]
    public void BadLocaIsATypedErrorAndBounded()
    {
        var o = new SyntheticFont.Options { LocaEdit = l => { l[2] = l[3] + 100; return l; } };
        byte[] font = SyntheticFont.Build(o);
        using PdfDocument doc = NewDoc(out _);
        Assert.Throws<PdfFormatException>(() => doc.AddFont(font));

        var beyond = new SyntheticFont.Options { LocaEdit = l => { l[l.Length - 1] = 1_000_000; return l; } };
        using PdfDocument doc2 = NewDoc(out _);
        Assert.Throws<PdfFormatException>(() => doc2.AddFont(SyntheticFont.Build(beyond)));
    }

    [Fact]
    public void HugeGlyphCountWithShortLocaIsATypedError()
    {
        var o = new SyntheticFont.Options { MaxpGlyphsOverride = 65535 };
        using PdfDocument doc = NewDoc(out _);
        Assert.Throws<PdfFormatException>(() => doc.AddFont(SyntheticFont.Build(o)));
    }

    [Fact]
    public void TruncatedAndNonFontDataAreTypedErrors()
    {
        byte[] font = SyntheticFont.Build();
        using PdfDocument doc = NewDoc(out _);
        for (int cut = 0; cut < font.Length; cut += 37)
        {
            byte[] part = font[..cut];
            try
            {
                PdfCreatedFont f = doc.AddFont(part);
                Assert.NotNull(f); // a lenient cut that still holds everything needed is allowed
            }
            catch (PdfFormatException)
            {
            }
        }

        Assert.Throws<PdfFormatException>(() => doc.AddFont(new byte[100]));
        Assert.Throws<PdfFormatException>(() => doc.AddFont("not a font at all, really"u8.ToArray()));
    }

    [Fact]
    public void EmbeddingFlagsAreHonoured()
    {
        byte[] restricted = SyntheticFont.Build(new SyntheticFont.Options { FsType = 0x0002 });
        using PdfDocument doc = NewDoc(out PdfPage page);
        Assert.Throws<PdfFontEmbeddingException>(() => doc.AddFont(restricted));
        Assert.Throws<PdfFontEmbeddingException>(() => doc.AddFont(SyntheticFont.Build(new SyntheticFont.Options { FsType = 0x0200 })));
        PdfCreatedFont ok = doc.AddFont(restricted, new PdfEmbedFontOptions { IgnoreEmbeddingRestrictions = true });
        Assert.NotNull(ok);

        // Editable (0x8) and preview/print (0x4) are fine; they also override a stray restricted bit.
        doc.AddFont(SyntheticFont.Build(new SyntheticFont.Options { FsType = 0x0008 }));
        doc.AddFont(SyntheticFont.Build(new SyntheticFont.Options { FsType = 0x0006 }));
    }

    [Fact]
    public void NoSubsettingBitEmbedsTheWholeFont()
    {
        byte[] font = SyntheticFont.Build(new SyntheticFont.Options { FsType = 0x0100 });
        byte[] pdf = DrawAndSave(font, "AC");
        using PdfDocument back = PdfDocument.Open(pdf);
        Assert.Equal("AC", CreationSupport.Normalize(CreationSupport.AllText(pdf)));
        PdfDictionary fonts = back.Pages[0].Resources.GetDictionary("Font")!;
        PdfDictionary type0 = fonts.GetDictionary(fonts.Keys.First())!;
        Assert.DoesNotContain("+", type0.GetName("BaseFont"));
        PdfDictionary cid = (PdfDictionary)((PdfArray)type0.Get("DescendantFonts")!)[0]!;
        byte[] program = cid.GetDictionary("FontDescriptor")!.GetStream("FontFile2")!.GetDecodedBytes().ToArray();
        Assert.Equal(font, program);

        // The same flag with Subset = true asked for still embeds whole; Subset = false does so by request.
        byte[] pdf2 = DrawAndSave(SyntheticFont.Build(), "AC", new PdfEmbedFontOptions { Subset = false });
        Assert.Equal("AC", CreationSupport.Normalize(CreationSupport.AllText(pdf2)));
    }

    [Fact]
    public void SubsetAndExtractionRoundTripOnSyntheticFont()
    {
        byte[] pdf = DrawAndSave(SyntheticFont.Build(), "ABCDDCBA");
        Assert.Equal("ABCDDCBA", CreationSupport.Normalize(CreationSupport.AllText(pdf)));
    }

    [Fact]
    public void MissingGlyphsAreNotdefWithADiagnostic()
    {
        using PdfDocument doc = NewDoc(out PdfPage page);
        PdfCreatedFont f = doc.AddFont(SyntheticFont.Build());
        using PdfPageContentBuilder b = page.BeginContent();
        b.SetFont(f, 12);
        b.DrawText("AZ", 10, 10);
        b.Commit();
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("U+005A", StringComparison.Ordinal));
    }

    [Fact]
    public void SameInputsGiveIdenticalBytes()
    {
        byte[] font = SyntheticFont.Build();
        Assert.Equal(DrawAndSave(font, "ABCD"), DrawAndSave(font, "ABCD"));
        byte[]? lib = CreationSupport.Font("LiberationSerif-Regular.ttf") ?? CreationSupport.Font("LiberationSans-Regular.ttf");
        if (lib is not null)
        {
            Assert.Equal(DrawAndSave(lib, "Hello \u0391\u0392 \u041f"), DrawAndSave(lib, "Hello \u0391\u0392 \u041f"));
        }
    }

    [Fact]
    public void CffSubsetOfJapaneseFontRoundTrips()
    {
        byte[] font = CreationSupport.RequireFont("NotoSansJP-Regular.otf");
        const string text = "\u65e5\u672c\u8a9e\u306e\u30c6\u30ad\u30b9\u30c8\u3092\u8868\u793a\u3057\u307e\u3059\u3002 Hello";
        byte[] pdf = DrawAndSave(font, text);
        Assert.Equal(text, CreationSupport.Normalize(CreationSupport.AllText(pdf)));
        using PdfDocument back = PdfDocument.Open(pdf);
        PdfDictionary fonts = back.Pages[0].Resources.GetDictionary("Font")!;
        PdfDictionary type0 = fonts.GetDictionary(fonts.Keys.First())!;
        PdfDictionary cid = (PdfDictionary)((PdfArray)type0.Get("DescendantFonts")!)[0]!;
        Assert.Equal("CIDFontType0", cid.GetName("Subtype"));
        byte[] cff = cid.GetDictionary("FontDescriptor")!.GetStream("FontFile3")!.GetDecodedBytes().ToArray();
        var parsed = new CffFont(cff);
        Assert.True(parsed.IsCidKeyed);
        Assert.True(parsed.GlyphCount < 30);
        Assert.True(cff.Length < font.Length / 50, $"{cff.Length} of {font.Length}");
        // Each kept glyph still draws something.
        int drawn = 0;
        for (int g = 1; g < parsed.GlyphCount; g++)
        {
            if (!parsed.GetOutline(g).IsEmpty)
            {
                drawn++;
            }
        }

        Assert.True(drawn >= parsed.GlyphCount - 3, $"{drawn} of {parsed.GlyphCount}");
    }
}
