using OnePdf.Tests.Signatures;
using System.Diagnostics;
using System.Text;
using static OnePdf.Tests.Signatures.SigningFixture;

namespace OnePdf.Tests.Redaction;

/// <summary>Content that tries to keep text, hide it from the rewrite or make the rewrite run away.</summary>
public class RedactionHostileTests
{
    private static readonly PdfRectangle Everything = new(0, 0, 200, 200);

    [Fact]
    public void CommentsInTheContentAreDroppedButPercentSignsInStringsStay()
    {
        const string content = "% secret heading\nBT /F1 12 Tf 20 100 Td (Hello secret world) Tj ET % secret note\n0 g % another secret\n10 10 5 5 re f\nBT /F1 12 Tf 20 50 Td (50% done) Tj ET";
        byte[] pdf = RedactionSupport.Page(content);
        PdfRectangle secret = RedactionSupport.WordRect(pdf, "secret");
        byte[] after = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, secret)]);
        byte[] all = RedactionSupport.Everything(after);
        Assert.False(RedactionSupport.Contains(all, "secret"));
        Assert.Contains("50% done", string.Join(" ", RedactionSupport.Words(after).Select(w => w.Text)));
        Assert.Single(PaintLog.Of(after).Paths);
    }

    [Fact]
    public void ANewObjectNeverTakesTheNumberOfADanglingReference()
    {
        // Page 2 points at object 8, which the file does not define. The streams redaction writes must not become object 8,
        // or page 2 would suddenly draw page 1's text without having been checked.
        const string file = "%PDF-1.7\n" +
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>\nendobj\n" +
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 5 0 R /Resources << /Font << /F1 6 0 R >> >> >>\nendobj\n" +
            "4 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 8 0 R /Resources << /Font << /F1 6 0 R >> >> >>\nendobj\n" +
            "5 0 obj\n<< /Length 52 >>\nstream\nBT /F1 12 Tf 20 100 Td (Hello secret world) Tj ET\nendstream\nendobj\n" +
            "6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n" +
            "trailer\n<< /Root 1 0 R >>\n";
        byte[] pdf = Encoding.ASCII.GetBytes(file);
        PdfRectangle secret = RedactionSupport.WordRect(pdf, "secret");
        byte[] after = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, secret)]);
        using PdfDocument back = PdfDocument.Open(after);
        Assert.Equal(2, back.PageCount);
        Assert.Equal(string.Empty, back.Pages[1].ExtractText().Text.Trim());
        Assert.False(RedactionSupport.Contains(RedactionSupport.Everything(after), "secret"));
    }

    [Fact]
    public void FormsNestedBeyondTheLimitFailTheCallInsteadOfLeavingText()
    {
        // Form 10 draws form 11, which draws form 12, ... form 49 holds the text.
        byte[] pdf = RedactionSupport.Page("/F10 Do", "/Resources << /XObject << /F10 10 0 R >> >>", b =>
        {
            for (int i = 10; i < 50; i++)
            {
                string body = i < 49 ? $"/F{i + 1} Do" : "BT /F1 12 Tf 20 100 Td (Hello secret world) Tj ET";
                string res = i < 49 ? $"/XObject << /F{i + 1} {i + 1} 0 R >>" : "/Font << /F1 5 0 R >>";
                b.Stream(i, $"/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources << {res} >>", Encoding.Latin1.GetBytes(body));
            }
        }, objects: 60);
        using PdfDocument d = RedactionSupport.Edit(pdf);
        PdfRedactionException e = Assert.Throws<PdfRedactionException>(() => d.Redact([new PdfRedactionRegion(0, Everything)]));
        Assert.Equal(0, e.PageIndex);
        Assert.False(d.IsRedacted);
    }

    [Fact]
    public void NestedFormsWithinTheLimitAreRedactedAtEveryLevel()
    {
        byte[] pdf = RedactionSupport.Page("/F10 Do", "/Resources << /XObject << /F10 10 0 R >> >>", b =>
        {
            for (int i = 10; i < 15; i++)
            {
                string body = i < 14 ? $"/F{i + 1} Do" : "BT /F1 12 Tf 20 100 Td (Hello secret world) Tj ET";
                string res = i < 14 ? $"/XObject << /F{i + 1} {i + 1} 0 R >>" : "/Font << /F1 5 0 R >>";
                b.Stream(i, $"/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources << {res} >>", Encoding.Latin1.GetBytes(body));
            }
        }, objects: 20);
        PdfRectangle secret = RedactionSupport.WordRect(pdf, "secret");
        byte[] after = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, secret)]);
        Assert.False(RedactionSupport.Contains(RedactionSupport.Everything(after), "secret"));
        Assert.Contains("Hello", string.Join(" ", RedactionSupport.Words(after).Select(w => w.Text)));
    }

    [Fact]
    public void AFormThatDrawsItselfDoesNotHangTheRewrite()
    {
        byte[] pdf = RedactionSupport.Page("/F10 Do", "/Resources << /XObject << /F10 10 0 R >> >>", b =>
            b.Stream(10, "/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources << /XObject << /F10 10 0 R >> /Font << /F1 5 0 R >> >>", Encoding.Latin1.GetBytes("BT /F1 12 Tf 20 100 Td (Hello secret world) Tj ET /F10 Do")));
        PdfRectangle secret = RedactionSupport.WordRect(pdf, "secret");
        // The rewritten copy would still reach the original through the form's own resources, so the call refuses and changes nothing.
        using PdfDocument d = RedactionSupport.Edit(pdf);
        Assert.Throws<PdfRedactionException>(() => d.Redact([new PdfRedactionRegion(0, secret)]));
        Assert.False(d.IsRedacted);
    }

    [Fact]
    public void HugeTjArraysStayBounded()
    {
        var sb = new StringBuilder("BT /F1 4 Tf 10 100 Td [");
        for (int i = 0; i < 60000; i++)
        {
            sb.Append("(ab) -3 ");
        }

        sb.Append("] TJ ET");
        byte[] pdf = RedactionSupport.Page(sb.ToString());
        var clock = Stopwatch.StartNew();
        using PdfDocument d = RedactionSupport.Edit(pdf);
        PdfRedactionResult r = d.Redact([new PdfRedactionRegion(0, new PdfRectangle(0, 90, 60, 120))]);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), clock.Elapsed.ToString());
        Assert.True(r.GlyphsRemoved > 0);
        byte[] saved = RedactionSupport.Save(d);
        Assert.NotEmpty(saved);
    }

    [Fact]
    public void TextRunsLongerThanTheRunLimitAreRewrittenAcrossRuns()
    {
        string text = new('x', 9000);
        byte[] pdf = RedactionSupport.Page($"BT /F1 1 Tf 0 100 Td ({text}) Tj ET");
        using PdfDocument d = RedactionSupport.Edit(pdf);
        PdfRedactionResult r = d.Redact([new PdfRedactionRegion(0, new PdfRectangle(100, 90, 200, 110))]);
        Assert.True(r.GlyphsRemoved > 0);
        byte[] saved = RedactionSupport.Save(d);
        // The glyphs before the area keep their place: the first one is still at the start.
        Assert.Equal(0, RedactionSupport.Words(saved).Count(w => w.Bounds.Left > 100 && w.Bounds.Left < 199 && w.Bounds.Width > 1));
    }

    [Fact]
    public void ContentCutOffInsideAStringIsHandled()
    {
        byte[] pdf = RedactionSupport.Page("BT /F1 12 Tf 20 100 Td (Hello secret wor");
        using PdfDocument d = RedactionSupport.Edit(pdf);
        try
        {
            d.Redact([new PdfRedactionRegion(0, Everything)]);
        }
        catch (PdfRedactionException)
        {
        }

        byte[] saved = RedactionSupport.Save(d);
        Assert.False(RedactionSupport.Contains(RedactionSupport.Everything(saved), "secret"));
    }

    [Fact]
    public void AnInlineImageWithDataThatLooksLikeAnEndMarkerIsRemovedWhole()
    {
        byte[] pdf = RedactionSupport.Page("q 100 0 0 100 50 50 cm BI /W 4 /H 1 /BPC 8 /CS /G ID EI Q EI\n0 g 150 150 10 10 re f Q");
        byte[] after = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, new PdfRectangle(60, 60, 90, 90))]);
        Assert.Empty(PaintLog.Of(after).Images);
    }

    [Fact]
    public void SignaturesAreReportedAsInvalidated()
    {
        byte[] signed = SignToBytes(Editable(UnsignedPdf()), Credential(TestPki.Shared.RsaSigner));
        using PdfDocument d = Editable(signed);
        PdfRedactionResult r = d.Redact([new PdfRedactionRegion(0, new PdfRectangle(0, 0, 1, 1))]);
        Assert.True(r.SignaturesInvalidated);
        Assert.Contains(r.Warnings, w => w.Contains("signature", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ASecondRedactionOfTheSameDocumentWorks()
    {
        byte[] pdf = RedactionSupport.Page("BT /F1 12 Tf 20 100 Td (Hello secret world) Tj ET");
        using PdfDocument d = RedactionSupport.Edit(pdf);
        d.Redact([new PdfRedactionRegion(0, RedactionSupport.WordRect(pdf, "secret"))]);
        d.Redact([new PdfRedactionRegion(0, RedactionSupport.WordRect(pdf, "Hello"))]);
        string text = string.Join(" ", RedactionSupport.Words(RedactionSupport.Save(d)).Select(w => w.Text));
        Assert.Equal("world", text);
    }

    [Fact]
    public void RedactionIsDeterministic()
    {
        byte[] pdf = RedactionSupport.Page("BT /F1 12 Tf 20 100 Td (Hello secret world) Tj ET");
        IReadOnlyList<PdfRedactionRegion> regions = [new PdfRedactionRegion(0, RedactionSupport.WordRect(pdf, "secret"))];
        Assert.Equal(RedactionSupport.Redact(pdf, regions), RedactionSupport.Redact(pdf, regions));
    }

}
