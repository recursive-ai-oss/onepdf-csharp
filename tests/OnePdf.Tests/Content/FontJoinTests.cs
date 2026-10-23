using OnePdf.Text;

namespace OnePdf.Tests.Content;

/// <summary>The PDF fonts joined to the interpreter and to text extraction.</summary>
public class FontJoinTests
{
    private const string Helv = "/Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>";

    // A Type 3 font with two glyphs: a (d1, shape only) square and b (d0, own colour) triangle.
    private static byte[] Type3File(string show, string extraCharProc = "", string mode = "") =>
        TestPdf.Build($"BT /T 10 Tf {mode} 20 100 Td {show} Tj ET", "/Resources << /Font << /T 5 0 R >> >>", b =>
        {
            b.Obj(5, "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << /a 6 0 R /b 7 0 R /c 8 0 R >> "
                + "/Encoding << /Type /Encoding /Differences [97 /a /b /c] >> /FirstChar 97 /LastChar 99 /Widths [1000 1000 500] >>");
            b.Stream(6, string.Empty, System.Text.Encoding.Latin1.GetBytes("1000 0 0 0 1000 1000 d1 1 0 0 rg 0 0 800 800 re f"));
            b.Stream(7, string.Empty, System.Text.Encoding.Latin1.GetBytes("1000 0 d0 0 0 1 rg 0 0 m 800 0 l 400 800 l f " + extraCharProc));
            b.Stream(8, string.Empty, System.Text.Encoding.Latin1.GetBytes("500 0 d0 BT /T 10 Tf (c) Tj ET"));
        }, objects: 9);

    [Fact]
    public void Type3GlyphsRunAsContentWithTheFontMatrixInTheCtm()
    {
        string t = TestPdf.Text(Type3File("(ab)"));
        Assert.Single(TestPdf.Lines(t, "text"));
        Assert.Equal(2, TestPdf.Lines(t, "begin-glyph").Length);
        Assert.Equal(2, TestPdf.Lines(t, "end-glyph").Length);

        // Size 10 with a 0.001 font matrix: 0.01 per glyph unit; the second glyph sits one advance (1000 -> 10 units) to the right.
        Assert.Contains("gs ctm=[0.01 0 0 -0.01 20 100]", t);
        Assert.Contains("gs ctm=[0.01 0 0 -0.01 30 100]", t);
    }

    [Fact]
    public void D1GlyphsIgnoreColourOperatorsAndD0GlyphsKeepTheirs()
    {
        string t = TestPdf.Text(Type3File("(ab)"));
        string[] lines = t.Split('\n');
        // The square (d1) paints in the text colour although it tried to set red; the triangle (d0) sets blue.
        int a = Array.FindIndex(lines, l => l.Contains("code=97", StringComparison.Ordinal));
        int b = Array.FindIndex(lines, l => l.Contains("code=98", StringComparison.Ordinal));
        Assert.True(lines[a - 1].Contains("fill=gray(0)", StringComparison.Ordinal));
        Assert.Contains(lines.Skip(a).Take(3), l => l.Contains("fill nonzero", StringComparison.Ordinal));
        Assert.Contains(lines.Skip(b).Take(4), l => l.Contains("fill=rgb(0 0 1)", StringComparison.Ordinal));
    }

    [Fact]
    public void Type3InvisibleTextAndMissingGlyphsPaintNothing()
    {
        string invisible = TestPdf.Text(Type3File("(ab)", mode: "3 Tr"));
        Assert.Empty(TestPdf.Lines(invisible, "begin-glyph"));
        Assert.Single(TestPdf.Lines(invisible, "text"));

        // Code 100 has no glyph procedure: it advances (width 0) but nothing is drawn.
        string missing = TestPdf.Text(Type3File("(da)"));
        Assert.Single(TestPdf.Lines(missing, "begin-glyph"));
    }

    [Fact]
    public void Type3GlyphThatShowsItsOwnFontIsSkippedNotLooped()
    {
        byte[] data = Type3File("(c)");
        using PdfDocument doc = TestPdf.Open(data);
        var device = new CheckingDevice();
        TestPdf.Page(doc).Interpret(device);
        Assert.Empty(device.Problems);
        Assert.True(device.Ended);
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("uses itself", StringComparison.Ordinal));
    }

    [Fact]
    public void Type3TextExtractsThroughTheGlyphNames()
    {
        // Glyph names a, b: the AGL gives the letters.
        using PdfDocument doc = TestPdf.Open(Type3File("(ab)"));
        Assert.Equal("ab", doc.ExtractText()[0].Text.Trim());
    }

    [Fact]
    public void OutlinesComeFromTheFontAndCanBeSwitchedOff()
    {
        // Helvetica is not embedded: the outline exists only when a substitute font is installed or registered, so only the
        // switch is asserted: with GlyphOutlines false no glyph has an outline.
        using PdfDocument doc = TestPdf.Open(TestPdf.Build("BT /F1 12 Tf 20 100 Td (Hi) Tj ET", Helv));
        var probe = new OutlineProbe();
        TestPdf.Page(doc).Interpret(probe, new PdfInterpretOptions { GlyphOutlines = false });
        Assert.Equal(2, probe.Glyphs);
        Assert.Equal(0, probe.WithOutline);
        Assert.Equal("Hi", probe.Text);
    }

    private sealed class OutlineProbe : PdfDevice
    {
        public int Glyphs { get; private set; }

        public int WithOutline { get; private set; }

        public string Text { get; private set; } = "";

        public override void ShowGlyphRun(in PdfGlyphRun run, in PdfGraphicsState state)
        {
            foreach (PdfGlyph g in run.Glyphs.Span)
            {
                Glyphs++;
                if (!g.Outline.IsEmpty)
                {
                    WithOutline++;
                }

                Text += g.Unicode;
            }
        }
    }

    [Fact]
    public void VerticalWritingUsesW2AndPlacesGlyphsByThePositionVector()
    {
        // Identity-V, DW2 [900 -500]; CID 66 has W2 w1y -250, v (100, 800). Font size 10.
        string content = "BT /V 10 Tf 100 150 Td <00410042> Tj ET";
        byte[] data = TestPdf.Build(content, "/Resources << /Font << /V 5 0 R >> >>", b =>
            b.Obj(5, "<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-V /DescendantFonts [<< /Type /Font /Subtype /CIDFontType2 /DW 1000 /DW2 [900 -500] /W2 [66 [-250 100 800]] >>] >>"), objects: 6);
        string t = TestPdf.Text(data);
        // A: v = (0.5, 0.9) em (default w0/2 and DW2[0]), w1 = -0.5; B: v = (0.1, 0.8), w1 = -0.25. Total advance -7.5 (0.5*10 + 0.25*10).
        Assert.Contains("vert=1 adv=0 -7.5", t);
        Assert.Contains(":v0.5,0.9,-0.5", t);
        Assert.Contains(":v0.1,0.8,-0.25", t);
        // Glyph origins sit at the text position minus v*size; B starts 5 below A.
        Assert.Contains("65@-5,-9,1", t);
        Assert.Contains("66@-1,-13,1", t);

        using PdfDocument doc = TestPdf.Open(data);
        PdfPageText page = doc.Pages[0].ExtractText();
        Assert.All(page.Glyphs, g => Assert.True(g.IsVertical));
        // Origin 1 of the first glyph is the text position (100, 150) in y-up page space; the second is 5 lower.
        Assert.Equal(100, page.Glyphs[0].Matrix.E, 6);
        Assert.Equal(150, page.Glyphs[0].Matrix.F, 6);
        Assert.Equal(100 + 0.0, page.Glyphs[1].Matrix.E, 6);
        Assert.Equal(145, page.Glyphs[1].Matrix.F, 6);
    }

    [Fact]
    public void HorizontalScalingCharacterAndWordSpacingFollowTheFontWidths()
    {
        // Helvetica widths come from the Core14 metrics: space 278, H 722, i 222. Tz 50, Tc 1, Tw 4 on the single-byte space only.
        string t = TestPdf.Text("BT /F1 10 Tf 50 Tz 1 Tc 4 Tw (H i) Tj ET", Helv);
        // ((7.22+1) + (2.78+1+4) + (2.22+1)) * 0.5 = 9.61
        Assert.Contains("adv=9.61 0", t);
    }

    [Fact]
    public void DocumentExtractionReadsTextWithTheDefaultOptions()
    {
        byte[] data = TestPdf.Build("BT /F1 12 Tf 20 150 Td (Hello World) Tj 0 -20 Td (second line) Tj ET", Helv);
        using PdfDocument doc = TestPdf.Open(data);
        IReadOnlyList<PdfPageText> pages = doc.ExtractText(new PdfTextOptions { RetainGlyphs = true });
        Assert.Single(pages);
        Assert.Equal("Hello World\n\nsecond line", pages[0].Text.Trim());
        Assert.Equal(pages[0].Text, doc.Pages[0].ExtractText().Text);
        Assert.All(pages[0].Glyphs, g => Assert.Equal(12, g.Size, 6));
    }

    [Fact]
    public void ActualTextReplacesItsSpanAndInvisibleTextIsFlagged()
    {
        string content = "BT /F1 12 Tf 20 150 Td /Span << /ActualText (fixed) >> BDC (garbled) Tj EMC 3 Tr ( hidden) Tj ET";
        using PdfDocument doc = TestPdf.Open(TestPdf.Build(content, Helv));
        PdfPageText page = doc.Pages[0].ExtractText();
        Assert.StartsWith("fixed", page.Text.Trim(), StringComparison.Ordinal);
        Assert.Contains(page.Glyphs, g => !g.IsVisible);
    }

    [Fact]
    public void GlyphsOutsideTheClipAreFlagged()
    {
        string content = "0 0 50 50 re W n BT /F1 12 Tf 20 20 Td (in) Tj 100 100 Td (out) Tj ET";
        using PdfDocument doc = TestPdf.Open(TestPdf.Build(content, Helv));
        PdfPageText kept = doc.Pages[0].ExtractText();
        Assert.Equal("in", kept.Text.Trim());
        PdfPageText all = doc.Pages[0].ExtractText(new PdfTextOptions { IncludeClippedAndOffPage = true });
        Assert.Contains("out", all.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ContentStreamsSplitInsideAnArrayAreJoined()
    {
        // ISO 32000-2 7.8.2 allows a split only between tokens, but files split inside a TJ array; the pieces are one stream.
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents [4 0 R 5 0 R] " + Helv + " >>");
        b.Stream(4, string.Empty, System.Text.Encoding.Latin1.GetBytes("BT /F1 12 Tf 20 100 Td [(Sub)-10(mit To)"));
        b.Stream(5, string.Empty, System.Text.Encoding.Latin1.GetBytes(" (Agency)] TJ ET"));
        long x = b.XrefTable(6);
        b.Trailer("/Size 6 /Root 1 0 R", x);
        using PdfDocument doc = TestPdf.Open(b.ToArray());
        Assert.Equal("Submit ToAgency", doc.Pages[0].ExtractText().Text.Trim());
    }

    [Fact]
    public void CompatibilityFormsFoldToTheirBaseCharacters()
    {
        Assert.Equal("\u4E00\u4E28", GlyphPrep.FoldCompatibility("\u2F00\u2F01"));
        Assert.Equal("\u0671", GlyphPrep.FoldCompatibility("\uFB50"));
        Assert.Equal("\u064B", GlyphPrep.FoldCompatibility("\uFE70"));
        Assert.Equal("\u8C48", GlyphPrep.FoldCompatibility("\uF900"));
        Assert.Equal("\u4E3D", GlyphPrep.FoldCompatibility("\U0002F800"));
        Assert.Equal("plain \uFB01", GlyphPrep.FoldCompatibility("plain \uFB01"));
    }
}
