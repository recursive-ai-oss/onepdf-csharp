using System.Text;

namespace OnePdf.Tests.Writing;

/// <summary>
/// One image, one font program, one form XObject and one appearance stream are shared by several pages. Every operation that keeps the
/// pages together must keep the streams together: each is written once, not once per page.
/// </summary>
public class SharedStreamTests
{
    private const string Image = "IMGMARK-0123456";
    private const string Font = "FONTPROGRAMMARK";
    private const string Form = "FORMMARK";
    private const string Appearance = "APPEARANCEMARK";

    private static readonly PdfOpenOptions Editable = new() { AllowEditing = true };

    /// <summary>Three pages, all showing the same image, font and form; the first two carry a text widget each, with one shared appearance stream.</summary>
    private static byte[] Shared()
    {
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [14 0 R 15 0 R] /DA (/Helv 0 Tf 0 g) >> >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 /MediaBox [0 0 300 300] >>");
        const string Res = "/Resources << /Font << /F1 10 0 R >> /XObject << /Im1 9 0 R /Fm1 13 0 R >> >>";
        b.Obj(3, $"<< /Type /Page /Parent 2 0 R /Contents 6 0 R {Res} /Annots [14 0 R] >>");
        b.Obj(4, $"<< /Type /Page /Parent 2 0 R /Contents 7 0 R {Res} /Annots [15 0 R] >>");
        b.Obj(5, $"<< /Type /Page /Parent 2 0 R /Contents 8 0 R {Res} >>");
        for (int i = 0; i < 3; i++)
        {
            b.Stream(6 + i, string.Empty, Encoding.ASCII.GetBytes($"q 50 0 0 50 200 200 cm /Im1 Do Q /Fm1 Do BT /F1 12 Tf 20 100 Td (secret{i}) Tj ET"));
        }

        b.Stream(9, "/Type /XObject /Subtype /Image /Width 3 /Height 5 /ColorSpace /DeviceGray /BitsPerComponent 8", Encoding.ASCII.GetBytes(Image));
        b.Obj(10, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FontDescriptor 11 0 R >>");
        b.Obj(11, "<< /Type /FontDescriptor /FontName /Helvetica /Flags 32 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 /FontFile 12 0 R >>");
        b.Stream(12, "/Length1 15 /Length2 0 /Length3 0", Encoding.ASCII.GetBytes(Font));
        b.Stream(13, "/Type /XObject /Subtype /Form /BBox [0 0 300 300]", Encoding.ASCII.GetBytes($"0.9 g 250 250 20 20 re f % {Form}"));
        b.Obj(14, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (a) /V (x) /Rect [10 10 100 30] /F 4 /AP << /N 16 0 R >> /P 3 0 R >>");
        b.Obj(15, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (b) /V (x) /Rect [10 10 100 30] /F 4 /AP << /N 16 0 R >> /P 4 0 R >>");
        b.Stream(16, "/Type /XObject /Subtype /Form /BBox [0 0 90 20]", Encoding.ASCII.GetBytes($"0.8 g 0 0 90 20 re f % {Appearance}"));
        long x = b.XrefTable(17);
        b.Trailer("/Size 17 /Root 1 0 R", x);
        return b.ToArray();
    }

    private static int Count(byte[] data, string needle)
    {
        byte[] n = Encoding.ASCII.GetBytes(needle);
        int count = 0;
        for (int i = data.AsSpan().IndexOf(n); i >= 0;)
        {
            count++;
            int next = data.AsSpan(i + n.Length).IndexOf(n);
            i = next < 0 ? -1 : i + n.Length + next;
        }

        return count;
    }

    private static (int Image, int Font, int Form, int Appearance) Counts(byte[] pdf) => (Count(pdf, Image), Count(pdf, Font), Count(pdf, Form), Count(pdf, Appearance));

    private static byte[] Save(PdfDocument d, PdfSaveOptions? o = null)
    {
        using var ms = new MemoryStream();
        d.Save(ms, o);
        return ms.ToArray();
    }

    [Fact]
    public void AStreamDictionaryHasTheIdentityOfItsStream()
    {
        using PdfDocument d = PdfDocument.Open(Shared());
        var image = (PdfStream)d.Resolve(new PdfReference(9, 0))!;
        Assert.Equal(9, image.Reference!.Number);
        Assert.Equal(9, image.Dictionary.Reference!.Number);
        Assert.Equal(image.SourceOffset, image.Dictionary.SourceOffset);
        Assert.True(image.SourceOffset > 0);
        Assert.Equal(13, d.Pages[0].Resources!.GetDictionary("XObject")!.GetStream("Fm1")!.Dictionary.Reference!.Number);
    }

    [Fact]
    public void AStreamAddedToADocumentGivesItsDictionaryTheReference()
    {
        using PdfDocument d = PdfDocument.Open(Shared(), Editable);
        PdfStream s = d.CreateStream([1, 2, 3]);
        PdfReference r = d.AddObject(s);
        Assert.Equal(r.Number, s.Dictionary.Reference!.Number);
    }

    [Fact]
    public void FullSavesKeepSharedStreamsShared()
    {
        byte[] src = Shared();
        Assert.Equal((1, 1, 1, 1), Counts(src));
        using PdfDocument d = PdfDocument.Open(src, Editable);
        Assert.Equal((1, 1, 1, 1), Counts(Save(d)));
        Assert.Equal((1, 1, 1, 1), Counts(Save(d, new PdfSaveOptions { XrefFormat = PdfXrefFormat.Stream })));
        Assert.Equal((1, 1, 1, 1), Counts(Save(d, new PdfSaveOptions { UseObjectStreams = false })));
    }

    [Fact]
    public void IncrementalSavesDoNotCopyThem()
    {
        byte[] src = Shared();
        using PdfDocument d = PdfDocument.Open(src, Editable);
        d.Pages[0].Dictionary.SetEntry("Rotate", new PdfInteger(90));
        using var ms = new MemoryStream();
        d.SaveIncremental(ms);
        Assert.Equal((1, 1, 1, 1), Counts(ms.ToArray()));
    }

    [Fact]
    public void MergingADocumentWithItselfSharesThem()
    {
        using PdfDocument d = PdfDocument.Open(Shared());
        using PdfDocument merged = PdfDocument.Merge([d, d]);
        Assert.Equal(6, merged.Pages.Count);
        (int image, int font, int form, int appearance) = Counts(Save(merged));
        Assert.True(image <= 2 && font <= 2 && form <= 2 && appearance <= 2, $"{image} {font} {form} {appearance}");
        // within each copy the three pages share
        using PdfDocument back = PdfDocument.Open(Save(merged));
        Assert.Equal(back.Pages[0].Resources!.GetDictionary("XObject")!.GetStream("Im1")!.Reference!.Number, back.Pages[2].Resources!.GetDictionary("XObject")!.GetStream("Im1")!.Reference!.Number);
    }

    [Fact]
    public void ImportingPagesCopiesEachSharedStreamOnce()
    {
        using PdfDocument src = PdfDocument.Open(Shared());
        using PdfDocument target = PdfDocument.CreateEmpty(Editable);
        target.ImportPages(src);
        Assert.Equal(3, target.Pages.Count);
        Assert.Equal((1, 1, 1, 1), Counts(Save(target)));
    }

    [Fact]
    public void ImportingIntoADocumentThatHasThemTwiceAddsTwoCopiesAtMost()
    {
        using PdfDocument src = PdfDocument.Open(Shared());
        using PdfDocument target = PdfDocument.Open(Shared(), Editable);
        target.ImportPages(src, [0, 1]);
        target.ImportPages(src, [2]);
        (int image, int font, int form, int appearance) = Counts(Save(target));
        Assert.True(image <= 2 && font <= 2 && form <= 2 && appearance <= 2, $"{image} {font} {form} {appearance}");
    }

    [Fact]
    public void SplitPartsKeepTheirPagesTogether()
    {
        using PdfDocument src = PdfDocument.Open(Shared());
        IReadOnlyList<PdfDocument> parts = PdfDocument.Split(src, [new PdfPageRange(0, 2), new PdfPageRange(2, 1)]);
        try
        {
            Assert.Equal((1, 1, 1, 1), Counts(Save(parts[0])));
            Assert.Equal((1, 1, 1, 0), Counts(Save(parts[1])));
        }
        finally
        {
            foreach (PdfDocument p in parts)
            {
                p.Dispose();
            }
        }
    }

    [Fact]
    public void RedactingOnePageKeepsTheOthersSharing()
    {
        using PdfDocument d = PdfDocument.Open(Shared(), Editable);
        d.Redact([new PdfRedactionRegion(0, new PdfRectangle(15, 95, 120, 115))]);
        (int image, int font, int form, int appearance) = Counts(Save(d));
        Assert.Equal(1, image);
        Assert.Equal(1, form);
        Assert.Equal(1, appearance);
        Assert.Equal(1, font);
    }

    [Fact]
    public void FlatteningKeepsOneCopyOfTheSharedAppearance()
    {
        using PdfDocument d = PdfDocument.Open(Shared(), Editable);
        d.Flatten();
        byte[] saved = Save(d);
        Assert.Equal((1, 1, 1, 1), Counts(saved));
        using PdfDocument back = PdfDocument.Open(saved);
        Assert.Empty(back.Pages[0].Annotations);
    }

    [Fact]
    public void ReopenedSavedFilesKeepTheSharingAndTheirText()
    {
        using PdfDocument d = PdfDocument.Open(Shared(), Editable);
        using PdfDocument back = PdfDocument.Open(Save(d));
        PdfStream a = back.Pages[0].Resources!.GetDictionary("XObject")!.GetStream("Im1")!;
        PdfStream c = back.Pages[2].Resources!.GetDictionary("XObject")!.GetStream("Im1")!;
        Assert.Equal(a.Reference!.Number, c.Reference!.Number);
    }
}
