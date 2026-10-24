using System.Text;

namespace OnePdf.Tests.PdfA;

/// <summary>
/// A PDF/A or PDF/UA save must never drop content. When a part the document needs cannot be read, the checks and the saves report it by
/// object number and refuse; a normal save keeps behaving as before. The files are built here byte by byte so each kind of damage is
/// exact.
/// </summary>
public class ConformanceNoLossTests
{
    // Object numbers of the parts of the test document.
    private const int FontFile = 10;
    private const int Image = 7;
    private const int Icc = 8;
    private const int Content = 5;
    private const int Xmp = 9;

    private static readonly byte[] BadFlate = [0x78, 0x9C, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    private static readonly byte[] RawContent = Encoding.Latin1.GetBytes("BT /F1 12 Tf 20 100 Td (Hello) Tj ET /Im1 Do");

    public enum Damage
    {
        None,
        FontProgramMissing,
        FontProgramCorrupt,
        ImageCorrupt,
        ContentCorrupt,
        IccProfileCorrupt,
        IccProfileMissing,
        MetadataCorrupt,
        AnnotationMissing,
        FontProgramTruncated,
    }

    private static byte[] Build(Damage damage)
    {
        byte[] data(Damage bad, byte[] plain) => damage == bad ? BadFlate : PdfBuilder.Deflate(plain);
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R" + (damage == Damage.None || damage == Damage.MetadataCorrupt ? " /Metadata 9 0 R" : string.Empty) + " >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 5 0 R /Resources << /Font << /F1 4 0 R >> /XObject << /Im1 7 0 R >> /ColorSpace << /CS1 [/ICCBased 8 0 R] >> >>"
            + (damage == Damage.AnnotationMissing ? " /Annots [20 0 R]" : string.Empty) + " >>");
        b.Obj(4, "<< /Type /Font /Subtype /TrueType /BaseFont /AAAAAA+Test /FirstChar 32 /LastChar 32 /Widths [500] /FontDescriptor 6 0 R /Encoding /WinAnsiEncoding >>");
        b.Stream(Content, "/Filter /FlateDecode", data(Damage.ContentCorrupt, RawContent));
        b.Obj(6, "<< /Type /FontDescriptor /FontName /AAAAAA+Test /Flags 32 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 /FontFile2 10 0 R >>");
        b.Stream(Image, "/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode", data(Damage.ImageCorrupt, [1, 2, 3, 4]));
        if (damage != Damage.IccProfileMissing)
        {
            b.Stream(Icc, "/N 3 /Filter /FlateDecode", data(Damage.IccProfileCorrupt, new byte[128]));
        }

        b.Stream(Xmp, "/Type /Metadata /Subtype /XML /Filter /FlateDecode", data(Damage.MetadataCorrupt, Encoding.UTF8.GetBytes("<?xpacket begin='' id='W5M0MpCehiHzreSzNTczkc9d'?><x:xmpmeta xmlns:x='adobe:ns:meta/'/><?xpacket end='w'?>")));
        if (damage != Damage.FontProgramMissing)
        {
            byte[] font = new byte[300];
            new Random(1).NextBytes(font);
            byte[] stored = damage == Damage.FontProgramCorrupt ? BadFlate : PdfBuilder.Deflate(font);
            if (damage == Damage.FontProgramTruncated)
            {
                stored = stored[..(stored.Length / 2)];
            }

            b.Stream(FontFile, "/Length1 300 /Filter /FlateDecode", stored);
        }

        long x = b.XrefTable(21);
        b.Trailer("/Size 21 /Root 1 0 R", x);
        return b.ToArray();
    }

    private static PdfDocument Open(byte[] pdf) => PdfDocument.Open(pdf, new PdfOpenOptions { AllowEditing = true });

    public static IEnumerable<object[]> Cases()
    {
        yield return [Damage.FontProgramMissing, 10, "embedded font program"];
        yield return [Damage.FontProgramCorrupt, 10, "embedded font program"];
        yield return [Damage.FontProgramTruncated, 10, "embedded font program"];
        yield return [Damage.ImageCorrupt, 7, "image"];
        yield return [Damage.ContentCorrupt, 5, "page content stream"];
        yield return [Damage.IccProfileCorrupt, 8, "ICC profile"];
        yield return [Damage.IccProfileMissing, 8, "ICC profile"];
        yield return [Damage.MetadataCorrupt, 9, "XMP metadata stream"];
        yield return [Damage.AnnotationMissing, 20, "annotation"];
    }

    private static bool Names(string message, int number, string what) =>
        message.Contains(what, StringComparison.Ordinal) && message.Contains($"(object {number} 0)", StringComparison.Ordinal);

    [Theory]
    [MemberData(nameof(Cases))]
    public void ChecksNameTheUnreadablePart(Damage damage, int number, string what)
    {
        using PdfDocument d = Open(Build(damage));
        Assert.Contains(d.CheckPdfUA(), v => v.ObjectNumber == number && Names(v.Message, number, what));
        Assert.Contains(d.CheckPdfUA(PdfUAPart.Part2), v => v.ObjectNumber == number && Names(v.Message, number, what));
        Assert.Contains(d.CheckPdfA(), v => v.ObjectNumber == number && Names(v.Message, number, what));
        Assert.Contains(d.CheckPdfA(new PdfAOptions { Part = PdfAPart.Part3, Conformance = PdfAConformance.U }), v => v.ObjectNumber == number && Names(v.Message, number, what));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void SavesRefuseWithTheSameViolation(Damage damage, int number, string what)
    {
        byte[] pdf = Build(damage);

        using (PdfDocument d = Open(pdf))
        {
            using var ms = new MemoryStream();
            PdfUAException ex = Assert.Throws<PdfUAException>(() => d.Save(ms, new PdfSaveOptions { PdfUA = new PdfUAOptions() }));
            PdfUAViolation hit = Assert.Single(ex.Violations, v => v.ObjectNumber == number && Names(v.Message, number, what));
            Assert.Contains(d.CheckPdfUA(), v => v.Message == hit.Message);
            Assert.Equal(0, ms.Length);
        }

        using (PdfDocument d = Open(pdf))
        {
            using var ms = new MemoryStream();
            PdfAException ex = Assert.Throws<PdfAException>(() => d.Save(ms, new PdfSaveOptions { PdfA = new PdfAOptions() }));
            PdfAViolation hit = Assert.Single(ex.Violations, v => v.ObjectNumber == number && Names(v.Message, number, what));
            Assert.Contains(d.CheckPdfA(), v => v.Message == hit.Message);
            Assert.Equal(0, ms.Length);
        }

        // Both at once list the finding once.
        using (PdfDocument d = Open(pdf))
        {
            using var ms = new MemoryStream();
            PdfAException ex = Assert.Throws<PdfAException>(() => d.Save(ms, new PdfSaveOptions { PdfA = new PdfAOptions { Conformance = PdfAConformance.A }, PdfUA = new PdfUAOptions() }));
            Assert.Single(ex.Violations, v => v.ObjectNumber == number && Names(v.Message, number, what));
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ThePrepareStepsKeepWhatTheyCannotRead(Damage damage, int number, string what)
    {
        // Preparing in memory must not replace the unreadable part, or a later check and save would pass over the loss.
        using PdfDocument d = Open(Build(damage));
        IReadOnlyList<PdfAViolation> a = d.PreparePdfA();
        Assert.Contains(a, v => v.ObjectNumber == number && Names(v.Message, number, what));
        IReadOnlyList<PdfUAViolation> ua = d.PreparePdfUA();
        Assert.Contains(ua, v => v.ObjectNumber == number && Names(v.Message, number, what));
        Assert.Contains(d.CheckPdfA(), v => v.ObjectNumber == number && Names(v.Message, number, what));
        Assert.Contains(d.CheckPdfUA(), v => v.ObjectNumber == number && Names(v.Message, number, what));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void AnOrdinarySaveKeepsBehaving(Damage damage, int number, string what)
    {
        _ = number;
        _ = what;
        using PdfDocument d = Open(Build(damage));
        using var one = new MemoryStream();
        using var two = new MemoryStream();
        var options = new PdfSaveOptions { DocumentId = new byte[16] };
        d.Save(one, options);
        d.Save(two, options);
        Assert.Equal(one.ToArray(), two.ToArray());
        Assert.True(one.Length > 200);
    }

    [Fact]
    public void ASoundDocumentReportsNoLoss()
    {
        using PdfDocument d = Open(Build(Damage.None));
        Assert.DoesNotContain(d.CheckPdfUA(), v => v.Message.Contains("without loss", StringComparison.Ordinal) || v.Message.Contains("write null", StringComparison.Ordinal));
        Assert.DoesNotContain(d.CheckPdfA(), v => v.Message.Contains("without loss", StringComparison.Ordinal) || v.Message.Contains("write null", StringComparison.Ordinal));
    }

    [Fact]
    public void RepairsThatLoseNothingAreNotReported()
    {
        // A wrong /Length is found by scanning for endstream and a missing Flate checksum leaves the data complete: neither loses content.
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>");
        byte[] z = PdfBuilder.Deflate(RawContent);
        b.Stream(4, "/Filter /FlateDecode", z[..^4], lengthEntry: "3");
        long x = b.XrefTable(5);
        b.Trailer("/Size 5 /Root 1 0 R", x);
        using PdfDocument d = Open(b.ToArray());
        Assert.DoesNotContain(d.CheckPdfA(), v => v.Message.Contains("without loss", StringComparison.Ordinal));
    }

    [Fact]
    public void DamagedEncryptedDataIsNamedToo()
    {
        byte[] plain = Build(Damage.None);
        byte[] enc;
        using (PdfDocument d = Open(plain))
        {
            using var ms = new MemoryStream();
            d.Save(ms, new PdfSaveOptions { Encryption = PdfEncryptionAction.Set, NewEncryption = new PdfEncryptionOptions { UserPassword = "pw" } });
            enc = ms.ToArray();
        }

        // Flip a byte of the last cipher block of the font program: the padding no longer checks out.
        byte[] stored;
        using (PdfDocument d = PdfDocument.Open(enc, new PdfOpenOptions { Password = "pw" }))
        {
            PdfStream? fontFile = d.Pages[0].Resources.GetDictionary("Font")?.GetDictionary("F1")?.GetDictionary("FontDescriptor")?.GetStream("FontFile2");
            Assert.NotNull(fontFile);
            stored = fontFile.GetRawBytes().ToArray();
        }

        int at = enc.AsSpan().IndexOf(stored);
        Assert.True(at >= 0);
        enc[at + stored.Length - 3] ^= 0x55;

        using PdfDocument bad = PdfDocument.Open(enc, new PdfOpenOptions { Password = "pw", AllowEditing = true });
        Assert.Contains(bad.CheckPdfUA(), v => v.Message.Contains("embedded font program", StringComparison.Ordinal) && v.Message.Contains("decrypted", StringComparison.Ordinal));
        using var outMs = new MemoryStream();
        Assert.Throws<PdfUAException>(() => bad.Save(outMs, new PdfSaveOptions { PdfUA = new PdfUAOptions() }));
    }
}
