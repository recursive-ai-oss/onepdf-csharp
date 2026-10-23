using System.Text;

namespace OnePdf.Tests.Redaction;

public class RedactionDocumentTests
{
    private const string Secret = "BT /F1 12 Tf 20 100 Td (Hello secret world) Tj ET";

    private static PdfRectangle Over(byte[] pdf, string word) => RedactionSupport.WordRect(pdf, word);

    [Fact]
    public void AnnotationsUnderTheAreaGoWithTheirTargets()
    {
        byte[] pdf = RedactionSupport.Page(Secret, RedactionSupport.Font + " /Annots [7 0 R 8 0 R 9 0 R]", b =>
        {
            b.Obj(7, "<< /Type /Annot /Subtype /Link /Rect [60 98 100 115] /A << /S /URI /URI (http://example.test/hidden-target) >> /Popup 9 0 R >>");
            b.Obj(8, "<< /Type /Annot /Subtype /Text /Rect [150 10 170 30] /Contents (kept note) >>");
            b.Obj(9, "<< /Type /Annot /Subtype /Popup /Rect [0 0 10 10] /Parent 7 0 R >>");
        });
        using PdfDocument d = RedactionSupport.Edit(pdf);
        PdfRedactionResult r = d.Redact([new PdfRedactionRegion(0, Over(pdf, "secret"))]);
        Assert.Equal(2, r.AnnotationsRemoved);
        byte[] after = RedactionSupport.Save(d);
        using PdfDocument reopened = PdfDocument.Open(after);
        Assert.Single(reopened.Pages[0].Annotations);
        Assert.Equal("Text", reopened.Pages[0].Annotations[0].Subtype);
        Assert.False(RedactionSupport.Contains(RedactionSupport.Everything(after), "hidden-target"));
    }

    [Fact]
    public void KeptAnnotationsHaveTheirAppearanceRedacted()
    {
        byte[] pdf = RedactionSupport.Page("0 g 0 0 1 1 re f", RedactionSupport.Font + " /Annots [7 0 R]", b =>
        {
            b.Obj(7, "<< /Type /Annot /Subtype /FreeText /Rect [20 20 120 40] /Contents (secret words) /AP << /N 8 0 R >> >>");
            b.Stream(8, "/Type /XObject /Subtype /Form /BBox [0 0 100 20] /Resources << /Font << /F1 5 0 R >> >>", Encoding.Latin1.GetBytes("BT /F1 12 Tf 2 5 Td (secret words) Tj ET"));
        });
        using PdfDocument d = RedactionSupport.Edit(pdf);
        PdfRedactionResult r = d.Redact([new PdfRedactionRegion(0, new PdfRectangle(20, 20, 70, 40))], new PdfRedactionOptions { Annotations = PdfRedactionAnnotationMode.KeepRedacted });
        Assert.True(r.GlyphsRemoved > 0);
        byte[] after = RedactionSupport.Save(d);
        using PdfDocument reopened = PdfDocument.Open(after);
        PdfAnnotation a = Assert.Single(reopened.Pages[0].Annotations);
        Assert.Null(a.Contents);
        byte[] all = RedactionSupport.Everything(after);
        Assert.False(RedactionSupport.Contains(all, "secret"));
    }

    [Fact]
    public void FieldValuesUnderTheAreaAreCleared()
    {
        byte[] pdf = RedactionSupport.Page("0 g 0 0 1 1 re f", RedactionSupport.Font + " /Annots [7 0 R]", b =>
        {
            b.Obj(7, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (confidential value) /Rect [20 20 120 40] /AP << /N 8 0 R >> >>");
            b.Stream(8, "/Type /XObject /Subtype /Form /BBox [0 0 100 20] /Resources << /Font << /F1 5 0 R >> >>", Encoding.Latin1.GetBytes("BT /F1 12 Tf 2 5 Td (confidential value) Tj ET"));
            b.Obj(9, "<< /Fields [7 0 R] >>");
        });
        string text = Encoding.Latin1.GetString(pdf).Replace("/Type /Catalog", "/Type /Catalog /AcroForm 9 0 R");
        using PdfDocument d = RedactionSupport.Edit(Encoding.Latin1.GetBytes(text));
        PdfRedactionResult r = d.Redact([new PdfRedactionRegion(0, new PdfRectangle(10, 10, 130, 50))]);
        Assert.True(r.FieldsCleared >= 1);
        byte[] after = RedactionSupport.Save(d);
        Assert.False(RedactionSupport.Contains(RedactionSupport.Everything(after), "confidential"));
    }

    [Fact]
    public void MetadataMatchingTheTermsIsCleared()
    {
        byte[] xmp = Encoding.UTF8.GetBytes("<?xpacket begin='x' id='W5M0MpCehiHzreSzNTczkc9d'?><x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><rdf:Description xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:title>Project Secret plan</dc:title><dc:creator>Bob</dc:creator></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end='w'?>");
        byte[] pdf = TestPdfWithMetadata(xmp);
        using PdfDocument d = RedactionSupport.Edit(pdf);
        PdfRedactionResult r = d.Redact([new PdfRedactionRegion(0, new PdfRectangle(0, 0, 1, 1))], new PdfRedactionOptions { MetadataTerms = ["secret"] });
        Assert.True(r.MetadataCleared >= 2);
        byte[] after = RedactionSupport.Save(d);
        byte[] all = RedactionSupport.Everything(after);
        Assert.False(RedactionSupport.Contains(all, "Secret"));
        Assert.True(RedactionSupport.Contains(all, "Bob"));
        using PdfDocument reopened = PdfDocument.Open(after);
        Assert.Equal("Bob", reopened.Info?.Author);
    }

    private static byte[] TestPdfWithMetadata(byte[] xmp)
    {
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R /Metadata 6 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>");
        b.Stream(4, string.Empty, Encoding.Latin1.GetBytes("0 g 0 0 1 1 re f"));
        b.Obj(5, "<< /Title (Secret strategy) /Author (Bob) >>");
        b.Stream(6, "/Type /Metadata /Subtype /XML", xmp);
        long x = b.XrefTable(7);
        b.Trailer("/Size 7 /Root 1 0 R /Info 5 0 R", x);
        return b.ToArray();
    }

    [Fact]
    public void AlternateTextOfRedactedContentIsRemoved()
    {
        string content = "/P << /MCID 0 /ActualText (secret) >> BDC BT /F1 12 Tf 20 100 Td (Hello secret world) Tj ET EMC /P << /MCID 1 /ActualText (kept) >> BDC 0 g 150 10 10 10 re f EMC";
        byte[] pdf = RedactionSupport.Page(content, RedactionSupport.Font + " /StructParents 0", b =>
        {
            b.Obj(6, "<< /Type /StructTreeRoot /K [7 0 R 8 0 R] >>");
            b.Obj(7, "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 /Alt (secret alt) >>");
            b.Obj(8, "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 1 /Alt (kept alt) >>");
        });
        string text = Encoding.Latin1.GetString(pdf).Replace("/Type /Catalog", "/Type /Catalog /StructTreeRoot 6 0 R");
        byte[] source = Encoding.Latin1.GetBytes(text);
        byte[] after = RedactionSupport.Redact(source, [new PdfRedactionRegion(0, Over(source, "secret"))]);
        byte[] all = RedactionSupport.Everything(after);
        Assert.False(RedactionSupport.Contains(all, "secret"));
        Assert.True(RedactionSupport.Contains(all, "kept alt"));
        Assert.True(RedactionSupport.Contains(all, "(kept)"));
    }

    [Fact]
    public void RedactAnnotationsAreApplied()
    {
        byte[] pdf = RedactionSupport.Page(Secret, RedactionSupport.Font + " /Annots [7 0 R]", b =>
            b.Obj(7, "<< /Type /Annot /Subtype /Redact /Rect [0 0 1 1] /QuadPoints [60 98 100 98 60 115 100 115] /IC [1 0 0] /OverlayText (GONE) >>"));
        using PdfDocument d = RedactionSupport.Edit(pdf);
        // The quad is given in the order the specification shows; it covers the word.
        PdfRedactionResult r = d.ApplyRedactAnnotations();
        Assert.Equal(1, r.RedactAnnotationsApplied);
        byte[] after = RedactionSupport.Save(d);
        string words = string.Join(" ", RedactionSupport.Words(after).Select(w => w.Text));
        Assert.DoesNotContain("secret", words);
        Assert.Contains("GONE", words);
        using PdfDocument reopened = PdfDocument.Open(after);
        Assert.Empty(reopened.Pages[0].Annotations);
        Assert.Contains("1 0 0 rg", Encoding.Latin1.GetString(RedactionSupport.Everything(after)));
    }

    [Fact]
    public void ScriptsAttachmentsAndThumbnailsGoOnRequest()
    {
        byte[] pdf = RedactionSupport.Page(Secret, RedactionSupport.Font + " /Thumb 7 0 R /AA << /O << /S /JavaScript /JS (app.alert('page-script')) >> >>", b =>
        {
            b.Stream(7, "/Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", [0]);
            b.Obj(8, "<< /JavaScript << /Names [(x) << /S /JavaScript /JS (app.alert('doc-script')) >>] >> /EmbeddedFiles << /Names [(a.txt) << /Type /Filespec /F (a.txt) /EF << /F 9 0 R >> >>] >> >>");
            b.Stream(9, "/Type /EmbeddedFile", Encoding.Latin1.GetBytes("attached-secret-bytes"));
        });
        string text = Encoding.Latin1.GetString(pdf).Replace("/Type /Catalog", "/Type /Catalog /Names 8 0 R");
        using PdfDocument d = RedactionSupport.Edit(Encoding.Latin1.GetBytes(text));
        d.Redact([new PdfRedactionRegion(0, new PdfRectangle(0, 0, 1, 1))], new PdfRedactionOptions { RemoveJavaScript = true, RemoveEmbeddedFiles = true, RemoveThumbnails = true });
        byte[] saved = RedactionSupport.Save(d);
        byte[] all = RedactionSupport.Everything(saved);
        Assert.False(RedactionSupport.Contains(all, "doc-script"));
        Assert.False(RedactionSupport.Contains(all, "page-script"));
        Assert.False(RedactionSupport.Contains(all, "attached-secret-bytes"));
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        using PdfDocument d = RedactionSupport.Edit(RedactionSupport.Page(Secret));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.Redact([new PdfRedactionRegion(3, new PdfRectangle(0, 0, 1, 1))]));
        Assert.Throws<ArgumentException>(() => d.Redact([new PdfRedactionRegion(0, new PdfRectangle(5, 5, 5, 9))]));
        using PdfDocument ro = PdfDocument.Open(RedactionSupport.Page(Secret));
        Assert.Throws<InvalidOperationException>(() => ro.Redact([new PdfRedactionRegion(0, new PdfRectangle(0, 0, 1, 1))]));
    }
}
