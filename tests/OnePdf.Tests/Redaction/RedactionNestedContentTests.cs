using System.Text;
using OnePdf.Tests.Content;

namespace OnePdf.Tests.Redaction;

/// <summary>
/// Content that is drawn by something other than the page's own operators: tiling pattern cells, soft-mask groups and Type 3
/// glyph procedures. What it draws under an area is gone from the saved file, what it draws elsewhere is kept, and the objects
/// other pages share are left alone.
/// </summary>
public class RedactionNestedContentTests
{
    // 48 bytes: a 4 x 4 RGB image whose samples can be searched for as a string.
    private const string ImageBytes = "IMGSECRET-IMGSECRET-IMGSECRET-IMGSECRET-IMGSECRE";

    private static PdfRectangle R(double l, double b, double r, double t) => new(l, b, r, t);

    private static byte[] Ascii(string s) => Encoding.Latin1.GetBytes(s);

    private static void Image(PdfBuilder b, int number) =>
        b.Stream(number, "/Type /XObject /Subtype /Image /Width 4 /Height 4 /ColorSpace /DeviceRGB /BitsPerComponent 8", Ascii(ImageBytes));

    private static string ResourceNames(byte[] pdf, string category, int page = 0)
    {
        using PdfDocument d = PdfDocument.Open(pdf);
        PdfDictionary? c = d.Pages[page].Resources?.GetDictionary(category);
        return c is null ? string.Empty : string.Join(",", c.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    // A pattern the content still selects but nothing is painted with is replaced by an empty cell of the same name.
    private static bool IsEmptyStandIn(byte[] pdf, string name, int page = 0)
    {
        using PdfDocument d = PdfDocument.Open(pdf);
        PdfStream cell = (PdfStream)d.Pages[page].Resources!.GetDictionary("Pattern")!.Get(name)!;
        return cell.GetDecodedBytes().Length == 0 && cell.Dictionary.GetDictionary("Resources")!.Count == 0;
    }

    private static List<string> TextWords(byte[] pdf, int page = 0) => [.. RedactionSupport.Words(pdf, page).Select(w => w.Text)];

    // ---- tiling patterns ----

    private static byte[] PatternPage(string extraContent = "")
    {
        const string cell = "BT /F1 10 Tf 2 25 Td (PATTERNSECRET) Tj ET q 20 0 0 20 2 2 cm /Im1 Do Q";
        return RedactionSupport.Page(
            "/Pattern cs /P1 scn 20 100 80 60 re f 0 g BT /F1 12 Tf 120 20 Td (visible) Tj ET " + extraContent,
            "/Resources << /Font << /F1 5 0 R >> /Pattern << /P1 6 0 R >> >>",
            b =>
            {
                b.Stream(6, "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 40 40] /XStep 40 /YStep 40 /Resources << /Font << /F1 5 0 R >> /XObject << /Im1 7 0 R >> >>", Ascii(cell));
                Image(b, 7);
            });
    }

    [Theory]
    [InlineData(PdfRedactionVectorMode.RemovePaths)]
    [InlineData(PdfRedactionVectorMode.ClipOut)]
    public void APatternCellUnderTheAreaIsGone(PdfRedactionVectorMode mode)
    {
        byte[] pdf = PatternPage();
        Assert.True(RedactionSupport.Contains(RedactionSupport.Everything(pdf), "PATTERNSECRET"));
        byte[] after = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, R(30, 110, 60, 130))], new PdfRedactionOptions { VectorMode = mode });

        byte[] all = RedactionSupport.Everything(after);
        Assert.False(RedactionSupport.Contains(all, "PATTERNSECRET"));
        Assert.False(RedactionSupport.Contains(all, ImageBytes));
        Assert.DoesNotContain("PATTERNSECRET", string.Join(" ", TextWords(after)));
        Assert.True(IsEmptyStandIn(after, "P1"));
        Assert.Equal(["visible"], TextWords(after));
        Assert.Equal(RedactionSupport.Words(pdf).Single(w => w.Text == "visible").Bounds, RedactionSupport.Words(after).Single().Bounds);
    }

    [Fact]
    public void APatternUsedOutsideTheAreaKeepsItsCell()
    {
        byte[] pdf = PatternPage("/Pattern cs /P1 scn 120 100 60 60 re f");
        byte[] after = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, R(30, 110, 60, 130))]);
        Assert.Equal("P1", ResourceNames(after, "Pattern"));
        Assert.True(RedactionSupport.Contains(RedactionSupport.Everything(after), "PATTERNSECRET"));
        Assert.Equal(1, RedactionSupport.DisplayList(after).Split('\n').Count(l => l.TrimStart().StartsWith("fill ", StringComparison.Ordinal)));
    }

    [Fact]
    public void AnAreaThatMeetsNothingLeavesThePatternPageAsItWas()
    {
        byte[] pdf = PatternPage();
        byte[] after = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, R(150, 150, 190, 190))]);
        Assert.Equal("P1", ResourceNames(after, "Pattern"));
        Assert.Equal(RedactionSupport.DisplayList(pdf), RedactionSupport.DisplayList(after));
    }

    // ---- soft masks ----

    private const string MaskContent =
        "1 g BT /F1 14 Tf 30 120 Td (MASKSECRET) Tj ET q 30 0 0 20 130 112 cm /Im1 Do Q 0.5 g 150 30 30 30 re f";

    private static byte[] MaskPage(string content = "/GS1 gs 0 0 1 rg 120 20 60 40 re f BT /F1 12 Tf 20 20 Td (visible) Tj ET")
    {
        return RedactionSupport.Page(
            content,
            "/Resources << /Font << /F1 5 0 R /Unused 5 0 R >> /ExtGState << /GS1 6 0 R /GS2 << /ca 0.5 >> >> >>",
            b =>
            {
                b.Obj(6, "<< /Type /ExtGState /SMask << /Type /Mask /S /Luminosity /G 7 0 R >> >>");
                b.Stream(7, "/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /CS /DeviceGray >> /Resources << /Font << /F1 5 0 R >> /XObject << /Im1 8 0 R >> >>", Ascii(MaskContent));
                Image(b, 8);
            });
    }

    [Fact]
    public void ASoftMaskGroupLosesWhatItDrawsUnderTheArea()
    {
        byte[] pdf = MaskPage();
        byte[] after = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, R(25, 112, 145, 135))]);

        byte[] all = RedactionSupport.Everything(after);
        Assert.False(RedactionSupport.Contains(all, "MASKSECRET"));
        Assert.False(RedactionSupport.Contains(all, ImageBytes));

        // The mask is still there, still draws its square and the masked rectangle is still painted; the image's samples under the area are blank.
        string before = RedactionSupport.DisplayList(pdf);
        string list = RedactionSupport.DisplayList(after);
        Assert.Equal(CountLines(before, "begin-softmask"), CountLines(list, "begin-softmask"));
        Assert.Equal(CountLines(before, "fill "), CountLines(list, "fill "));
        PdfImageData img = PaintLog.Of(after).Images.Single()!;
        byte[] s = img.Samples.ToArray();
        Assert.Contains(s, v => v == 0);
        Assert.Contains(s, v => v != 0);
        Assert.Equal(["visible"], TextWords(after));
    }

    private static int CountLines(string text, string keyword) =>
        text.Split('\n').Count(l => l.TrimStart().StartsWith(keyword, StringComparison.Ordinal));

    [Fact]
    public void TheOriginalGraphicsStateIsNotKeptReachable()
    {
        byte[] pdf = MaskPage();
        byte[] after = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, R(25, 112, 145, 135))]);
        // GS1 was replaced by a copy with the rewritten group; GS2 and the unused font are not used by the content.
        string states = ResourceNames(after, "ExtGState");
        Assert.DoesNotContain("GS1", states);
        Assert.DoesNotContain("GS2", states);
        Assert.Equal("F1", ResourceNames(after, "Font"));
    }

    [Fact]
    public void ASoftMaskUsedOutsideTheAreaKeepsItsContentWhereItIsNotUnderTheArea()
    {
        byte[] pdf = MaskPage();
        byte[] after = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, R(145, 25, 185, 65))]);
        // Only the grey square of the mask lies under this area; the text stays in the mask.
        Assert.True(RedactionSupport.Contains(RedactionSupport.Everything(after), "MASKSECRET"));
        Assert.True(RedactionSupport.Contains(RedactionSupport.Everything(after), ImageBytes));
    }

    // ---- Type 3 glyph procedures ----

    private const string Type3Font =
        "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] " +
        "/CharProcs << /a 7 0 R /b 8 0 R /c 10 0 R >> /Encoding << /Type /Encoding /Differences [97 /a /b /c] >> " +
        "/FirstChar 97 /LastChar 99 /Widths [1000 1000 1000] /Resources << /Font << /F1 5 0 R >> /XObject << /Im1 9 0 R >> >> >>";

    private static byte[] Type3Page(string content)
    {
        return RedactionSupport.Page(
            content,
            "/Resources << /Font << /T3 6 0 R /Spare 5 0 R >> >>",
            b =>
            {
                b.Obj(6, Type3Font);
                b.Stream(7, string.Empty, Ascii("1000 0 d0 0 g 100 100 700 700 re f BT /F1 300 Tf 100 400 Td (GLYPHSECRET) Tj ET q 300 0 0 300 300 100 cm /Im1 Do Q"));
                b.Stream(8, string.Empty, Ascii("1000 0 0 0 1000 1000 d1 100 100 800 800 re f"));
                Image(b, 9);
                b.Stream(10, string.Empty, Ascii("1000 0 d0 -6500 100 800 800 re f"));
            },
            objects: 12);
    }

    [Fact]
    public void AType3GlyphUnderTheAreaTakesItsProcedureWithIt()
    {
        byte[] pdf = Type3Page("BT /T3 20 Tf 20 100 Td (ab) Tj ET");
        Assert.True(RedactionSupport.Contains(RedactionSupport.Everything(pdf), "GLYPHSECRET"));
        byte[] after = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, R(20, 100, 38, 120))]);

        byte[] all = RedactionSupport.Everything(after);
        Assert.False(RedactionSupport.Contains(all, "GLYPHSECRET"));
        Assert.False(RedactionSupport.Contains(all, ImageBytes));
        Assert.Equal("T3", ResourceNames(after, "Font"));

        // The second glyph is where it was and still has its procedure.
        string before = RedactionSupport.DisplayList(pdf);
        string list = RedactionSupport.DisplayList(after);
        Assert.Equal(2, CountLines(before, "begin-glyph"));
        Assert.Equal(1, CountLines(list, "begin-glyph"));
        using PdfDocument d = PdfDocument.Open(after);
        PdfDictionary font = d.Pages[0].Resources!.GetDictionary("Font")!.GetDictionary("T3")!;
        Assert.Equal(["b", "c"], font.GetDictionary("CharProcs")!.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(string.Empty, string.Join(",", font.GetDictionary("Resources")!.GetDictionary("Font")?.Keys ?? []));
        Assert.Empty(font.GetDictionary("Resources")!.GetDictionary("XObject")?.Keys ?? []);
    }

    [Fact]
    public void AType3ProcedureThatPaintsIntoTheAreaFromElsewhereIsRemoved()
    {
        // Glyph c sits at x = 150 and draws 130 pt to its left.
        byte[] pdf = Type3Page("BT /T3 20 Tf 150 100 Td (c) Tj ET BT /T3 20 Tf 100 100 Td (b) Tj ET");
        Assert.Equal(2, CountLines(RedactionSupport.DisplayList(pdf), "begin-glyph"));
        byte[] after = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, R(20, 100, 36, 116))]);
        Assert.Equal(1, CountLines(RedactionSupport.DisplayList(after), "begin-glyph"));
    }

    [Fact]
    public void AType3GlyphShownAlsoOutsideTheAreaKeepsItsProcedure()
    {
        byte[] pdf = Type3Page("BT /T3 20 Tf 20 100 Td (a) Tj ET BT /T3 20 Tf 120 100 Td (a) Tj ET");
        byte[] after = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, R(20, 100, 38, 120))]);
        Assert.Equal(1, CountLines(RedactionSupport.DisplayList(after), "begin-glyph"));
        // It is still drawn once, so its procedure stays.
        Assert.True(RedactionSupport.Contains(RedactionSupport.Everything(after), "GLYPHSECRET"));
    }

    // ---- shared objects and unused resources ----

    private static byte[] TwoPages(string content0, string content1, Action<PdfBuilder> extra)
    {
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R 13 0 R] /Count 2 >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources 12 0 R >>");
        b.Stream(4, string.Empty, Ascii(content0));
        b.Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        b.Obj(13, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 14 0 R /Resources 12 0 R >>");
        b.Stream(14, string.Empty, Ascii(content1));
        extra(b);
        long x = b.XrefTable(20);
        b.Trailer("/Size 20 /Root 1 0 R", x);
        return b.ToArray();
    }

    [Fact]
    public void ObjectsSharedWithAnotherPageAreNotChanged()
    {
        const string cell = "BT /F1 10 Tf 2 25 Td (PATTERNSECRET) Tj ET";
        const string form = "BT /F1 12 Tf 10 10 Td (FORMSECRET) Tj ET";
        string content = "/Pattern cs /P1 scn 20 100 80 60 re f q 1 0 0 1 20 20 cm /Fm1 Do Q";
        byte[] pdf = TwoPages(content, content, b =>
        {
            b.Obj(12, "<< /Font << /F1 5 0 R /F9 5 0 R >> /Pattern << /P1 6 0 R >> /XObject << /Fm1 7 0 R /Fm2 7 0 R >> /ExtGState << /GS1 << /ca 0.5 >> >> >>");
            b.Stream(6, "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 40 40] /XStep 40 /YStep 40 /Resources << /Font << /F1 5 0 R >> >>", Ascii(cell));
            b.Stream(7, "/Type /XObject /Subtype /Form /BBox [0 0 100 40] /Resources << /Font << /F1 5 0 R >> >>", Ascii(form));
        });

        using PdfDocument d = RedactionSupport.Edit(pdf);
        d.Redact([new PdfRedactionRegion(0, R(30, 110, 60, 130)), new PdfRedactionRegion(0, R(25, 25, 130, 45))]);
        byte[] after = RedactionSupport.Save(d);

        // Page 1 was redacted: the pattern use is gone and the form's text is gone from what it draws.
        Assert.True(IsEmptyStandIn(after, "P1", 0));
        Assert.DoesNotContain("FORMSECRET", string.Join(" ", TextWords(after, 0)));

        // Page 2 shares every one of those objects and draws exactly what it drew.
        Assert.Equal("P1", ResourceNames(after, "Pattern", 1));
        Assert.Equal("F1,F9", ResourceNames(after, "Font", 1));
        Assert.Equal("Fm1,Fm2", ResourceNames(after, "XObject", 1));
        Assert.Equal("GS1", ResourceNames(after, "ExtGState", 1));
        Assert.Equal(RedactionSupport.DisplayList(pdf, 1), RedactionSupport.DisplayList(after, 1));
        Assert.Contains("FORMSECRET", string.Join(" ", TextWords(after, 1)));
    }

    [Fact]
    public void ResourcesNothingUsesAnyMoreAreGoneFromTheRedactedPage()
    {
        byte[] pdf = TwoPages("BT /F1 12 Tf 20 100 Td (secret) Tj ET BT /F1 12 Tf 20 20 Td (kept) Tj ET", "BT /F1 12 Tf 20 100 Td (secret) Tj ET", b =>
        {
            b.Obj(12, "<< /Font << /F1 5 0 R /F9 5 0 R >> /XObject << /Fm1 7 0 R >> /ExtGState << /GS1 << /ca 0.5 >> >> /ProcSet [/PDF /Text] >>");
            b.Stream(7, "/Type /XObject /Subtype /Form /BBox [0 0 100 40]", Ascii("0 g 0 0 10 10 re f"));
        });
        byte[] after = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, RedactionSupport.WordRect(pdf, "secret"))]);
        Assert.Equal("F1", ResourceNames(after, "Font", 0));
        Assert.Equal(string.Empty, ResourceNames(after, "XObject", 0));
        Assert.Equal(string.Empty, ResourceNames(after, "ExtGState", 0));
        Assert.Equal(["kept"], TextWords(after, 0));
        Assert.Equal("F1,F9", ResourceNames(after, "Font", 1));
        Assert.Equal(["secret"], TextWords(after, 1));
    }

    [Fact]
    public void AFormWithoutResourcesKeepsWhatItNamesInThePagesResources()
    {
        byte[] pdf = RedactionSupport.Page(
            "BT /F1 12 Tf 20 100 Td (secret) Tj ET /Fm1 Do",
            "/Resources << /Font << /F1 5 0 R /F2 5 0 R >> /XObject << /Fm1 6 0 R >> >>",
            b => b.Stream(6, "/Type /XObject /Subtype /Form /BBox [0 0 200 200]", Ascii("BT /F2 12 Tf 20 20 Td (inherits) Tj ET")));
        byte[] after = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, RedactionSupport.WordRect(pdf, "secret"))]);
        Assert.Equal("F1,F2", ResourceNames(after, "Font"));
        Assert.Equal(["inherits"], TextWords(after));
    }

    [Fact]
    public void ResourcesAreLeftAloneWhenAType3ProcedureMayNameThem()
    {
        // The font has no resources of its own, so its procedure looks names up in the page's.
        byte[] pdf = RedactionSupport.Page(
            "BT /F1 12 Tf 20 100 Td (secret) Tj ET BT /T3 20 Tf 100 20 Td (a) Tj ET",
            "/Resources << /Font << /F1 5 0 R /T3 6 0 R >> /XObject << /Im1 8 0 R >> >>",
            b =>
            {
                b.Obj(6, "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << /a 7 0 R >> /Encoding << /Type /Encoding /Differences [97 /a] >> /FirstChar 97 /LastChar 97 /Widths [1000] >>");
                b.Stream(7, string.Empty, Ascii("1000 0 d0 q 800 0 0 800 0 0 cm /Im1 Do Q"));
                Image(b, 8);
            });
        byte[] after = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, RedactionSupport.WordRect(pdf, "secret"))]);
        Assert.Equal("Im1", ResourceNames(after, "XObject"));
        Assert.Single(PaintLog.Of(after).Images);
    }

    [Fact]
    public void AFontOnlyRedactedTextUsedIsNotReachableAnyMore()
    {
        byte[] pdf = RedactionSupport.Page(
            "BT /F2 12 Tf 20 100 Td (secret) Tj ET BT /F3 12 Tf 20 20 Td (kept) Tj ET",
            "/Resources << /Font << /F2 6 0 R /F3 8 0 R >> >>",
            b =>
            {
                b.Obj(6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FontDescriptor 7 0 R >>");
                b.Obj(7, "<< /Type /FontDescriptor /FontName /Helvetica /Flags 32 /FontFile 9 0 R >>");
                b.Obj(8, "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /FontDescriptor 10 0 R >>");
                b.Stream(9, "/Length1 20 /Length2 0 /Length3 0", Ascii("FIRSTFONTPROGRAMMARKER"));
                b.Obj(10, "<< /Type /FontDescriptor /FontName /Times-Roman /Flags 32 /FontFile 11 0 R >>");
                b.Stream(11, "/Length1 20 /Length2 0 /Length3 0", Ascii("SECONDFONTPROGRAMMARKER"));
            },
            objects: 14);
        byte[] after = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, RedactionSupport.WordRect(pdf, "secret"))]);
        byte[] all = RedactionSupport.Everything(after);
        Assert.False(RedactionSupport.Contains(all, "FIRSTFONTPROGRAMMARKER"));
        Assert.True(RedactionSupport.Contains(all, "SECONDFONTPROGRAMMARKER"));
        Assert.Equal(["kept"], TextWords(after));
    }

    [Fact]
    public void TextPaintedWithAPatternTakesThePatternsCellWithIt()
    {
        byte[] pdf = RedactionSupport.Page(
            "/Pattern cs /P1 scn BT /F1 40 Tf 20 100 Td (BIG) Tj ET 0 g BT /F1 12 Tf 20 20 Td (kept) Tj ET",
            "/Resources << /Font << /F1 5 0 R >> /Pattern << /P1 6 0 R >> >>",
            b => b.Stream(6, "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 40 40] /XStep 40 /YStep 40 /Resources << /Font << /F1 5 0 R >> >>", Ascii("BT /F1 10 Tf 2 25 Td (CELLSECRET) Tj ET")));
        byte[] after = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, RedactionSupport.WordRect(pdf, "BIG"))]);
        Assert.False(RedactionSupport.Contains(RedactionSupport.Everything(after), "CELLSECRET"));
        Assert.Equal(["kept"], TextWords(after));
    }

    [Fact]
    public void AFontThatIsAStreamObjectIsStillRecognisedAsInUse()
    {
        // A dictionary read from a stream object has no object number of its own; the font must still be found to be in use.
        byte[] pdf = RedactionSupport.Page(
            "BT /F2 12 Tf 20 100 Td (secret) Tj ET BT /F2 12 Tf 20 20 Td (kept words here) Tj ET",
            "/Resources << /Font << /F2 6 0 R >> >>",
            b => b.Stream(6, "/Type /Font /Subtype /Type1 /BaseFont /Times-Roman", []));
        List<(string Text, PdfRectangle Bounds)> before = RedactionSupport.Words(pdf);
        byte[] after = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, RedactionSupport.WordRect(pdf, "secret"))]);
        List<(string Text, PdfRectangle Bounds)> words = RedactionSupport.Words(after);
        Assert.Equal(["kept", "words", "here"], words.Select(w => w.Text));
        foreach ((string text, PdfRectangle bounds) in words)
        {
            Assert.Equal(before.First(w => w.Text == text).Bounds, bounds);
        }
    }
}
