using System.Text;
using OnePdf.Content;

namespace OnePdf.Tests.Content;

public class TextFormsAndLayersTests
{
    private const string Font = "/Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FirstChar 65 /LastChar 67 /Widths [500 600 700] >> >> >>";

    private static string T(string content, string pageEntries = "", Action<PdfBuilder>? extra = null, PdfInterpretOptions? options = null, int objects = 5) =>
        TestPdf.Text(content, pageEntries, extra, options, objects);

    private static byte[] Bytes(string s) => Encoding.Latin1.GetBytes(s);

    // ---- text ----

    [Fact]
    public void ShowTextAdvancesByWidthsAndSpacing()
    {
        // Tfs 10, Tc 1, Tz 50: ((0.5*10)+1)*0.5 = 3 for A; ((0.6*10)+1)*0.5 = 3.5 for B.
        string t = T("BT /F1 10 Tf 1 Tc 50 Tz 20 100 Td (AB) Tj (C) Tj ET", Font);
        string[] runs = TestPdf.Lines(t, "text");
        Assert.Equal(2, runs.Length);
        Assert.Contains("size=10 mode=0 tc=1 tw=0 th=0.5 rise=0 tm=[1 0 0 1 20 100] vert=0 adv=6.5 0 codes=4142 glyphs=[65@0,0,0.5 66@3,0,0.6]", runs[0]);
        // The second string starts where the first ended.
        Assert.Contains("tm=[1 0 0 1 26.5 100]", runs[1]);
    }

    [Fact]
    public void WordSpacingAppliesToSingleByteSpaceOnly()
    {
        string t = T("BT /F1 10 Tf 5 Tw (A A) Tj ET", Font);
        // A=5, space=0 width + Tw 5, A=5.
        Assert.Contains("adv=15 0", TestPdf.Lines(t, "text")[0]);
    }

    [Fact]
    public void TjArrayAdjustsPositionAndSplitsRuns()
    {
        string t = T("BT /F1 10 Tf [(A) -1000 (B) 500 (C)] TJ ET", Font);
        string[] runs = TestPdf.Lines(t, "text");
        Assert.Equal(3, runs.Length);
        Assert.Contains("tm=[1 0 0 1 0 0]", runs[0]);
        // A advances 5, then -1000 moves right 10: B starts at 15; B advances 6, then +500 moves left 5: C at 16.
        Assert.Contains("tm=[1 0 0 1 15 0]", runs[1]);
        Assert.Contains("tm=[1 0 0 1 16 0]", runs[2]);
    }

    [Fact]
    public void TextMatrixOperatorsAndLeading()
    {
        string t = T("BT /F1 12 Tf 14 TL 2 0 0 2 10 10 Tm (A) Tj T* (B) Tj 5 -3 TD (C) Tj 0 0 (x) \" ET", Font);
        string[] runs = TestPdf.Lines(t, "text");
        Assert.Equal(4, runs.Length);
        Assert.Contains("tm=[2 0 0 2 10 10]", runs[0]);
        // T* moves down by the leading in text space: (0,-14) transformed by the scale-2 line matrix.
        Assert.Contains("tm=[2 0 0 2 10 -18]", runs[1]);
        Assert.Contains("tm=[2 0 0 2 20 -24]", runs[2]);
    }

    [Fact]
    public void TextStateIsSavedWithQ()
    {
        string t = T("BT /F1 10 Tf 3 Tc q 7 Tc (A) Tj Q (A) Tj ET", Font);
        string[] runs = TestPdf.Lines(t, "text");
        Assert.Contains("tc=7", runs[0]);
        Assert.Contains("tc=3", runs[1]);
    }

    [Fact]
    public void TextRenderModeAndRiseTravelWithTheRun()
    {
        string t = T("BT /F1 10 Tf 3 Tr 4 Ts (A) Tj ET", Font);
        Assert.Contains("mode=3", t);
        Assert.Contains("rise=4", t);
    }

    [Fact]
    public void ClippingTextModesPushATextClipAtET()
    {
        string t = T("q BT /F1 10 Tf 7 Tr (A) Tj ET 0 0 5 5 re f Q", Font);
        Assert.Contains("text-clip", t);
        var dev = new CheckingDevice();
        using PdfDocument doc = TestPdf.Open(TestPdf.Build("q BT /F1 10 Tf 7 Tr (A) Tj ET Q", pageEntries: Font));
        TestPdf.Page(doc).Interpret(dev);
        Assert.Empty(dev.Problems);
    }

    [Fact]
    public void CompositeFontUsesTwoByteCodesAndWArray()
    {
        string res = "/Resources << /Font << /F2 5 0 R >> >>";
        byte[] data = TestPdf.Build("BT /F2 10 Tf <00410042004300ff> Tj ET", res, b =>
            b.Obj(5, "<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /DescendantFonts [<< /Type /Font /Subtype /CIDFontType2 /DW 500 /W [65 [600] 66 67 800] >>] >>"), objects: 6);
        string t = TestPdf.Text(data);
        Assert.Contains("glyphs=[65@0,0,0.6 66@6,0,0.8 67@14,0,0.8 255@22,0,0.5]", t);
        Assert.Contains("adv=27 0", t);
    }

    [Fact]
    public void VerticalFontAdvancesDownward()
    {
        string res = "/Resources << /Font << /F2 5 0 R >> >>";
        byte[] data = TestPdf.Build("BT /F2 10 Tf <00410042> Tj ET", res, b =>
            b.Obj(5, "<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-V /DescendantFonts [<< /Type /Font /Subtype /CIDFontType2 /DW 1000 >>] >>"), objects: 6);
        string t = T0(data);
        Assert.Contains("vert=1 adv=0 -20", t);
    }

    private static string T0(byte[] data) => TestPdf.Text(data);

    [Fact]
    public void TextWithoutFontStillPositions()
    {
        byte[] data = TestPdf.Build("BT (AB) Tj ET");
        using PdfDocument doc = TestPdf.Open(data);
        string t = TestPdf.Record(doc).ToText();
        Assert.Single(TestPdf.Lines(t, "text"));
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("before any font"));
    }

    [Fact]
    public void LongStringsAreSplitIntoRunsWithoutLosingPosition()
    {
        string s = new string('A', 10_000);
        string t = T("BT /F1 1 Tf (" + s + ") Tj (B) Tj ET", Font);
        string[] runs = TestPdf.Lines(t, "text");
        Assert.Equal(4, runs.Length);
        Assert.Contains("tm=[1 0 0 1 2048 0]", runs[1]);
        Assert.Contains("tm=[1 0 0 1 5000 0]", runs[3]);
    }

    // ---- forms ----

    private static string FormObj(string body, string extra = "") => body + extra;

    [Fact]
    public void FormXObjectAppliesMatrixBBoxAndOwnResources()
    {
        string t = T("q 1 0 0 rg /F Do Q 0 0 1 1 re f", "/Resources << /XObject << /F 5 0 R >> >>", b =>
            b.Stream(5, "/Type /XObject /Subtype /Form /BBox [0 0 50 50] /Matrix [2 0 0 2 10 10] /Resources << /ExtGState << /G << /LW 4 >> >> >>", Bytes("/G gs 0 0 20 20 re S")), objects: 6);
        // Clip to the box in form space, with the form matrix in the CTM.
        Assert.Contains("clip nonzero M 0 0 L 50 0 L 50 50 L 0 50 Z", t);
        Assert.Contains("ctm=[2 0 0 -2 10 190]", t);
        Assert.Contains("lw=4", t);
        // The form's state does not leak: the last fill is back at the page CTM and default line width.
        string last = t[t.LastIndexOf("gs ctm=", StringComparison.Ordinal)..];
        Assert.StartsWith("gs ctm=[1 0 0 -1 0 200] fill=gray(0)", last);
        Assert.Single(TestPdf.Lines(t, "pop-clip"));
    }

    [Fact]
    public void SelfAndMutuallyReferencingFormsAreCut()
    {
        byte[] data = TestPdf.Build("/A Do", "/Resources << /XObject << /A 5 0 R >> >>", b =>
        {
            b.Stream(5, "/Type /XObject /Subtype /Form /BBox [0 0 9 9] /Resources << /XObject << /B 6 0 R >> >>", Bytes("0 0 1 1 re f /B Do"));
            b.Stream(6, "/Type /XObject /Subtype /Form /BBox [0 0 9 9] /Resources << /XObject << /A 5 0 R >> >>", Bytes("0 0 2 2 re f /A Do"));
        }, objects: 7);
        using PdfDocument doc = TestPdf.Open(data);
        var dev = new CheckingDevice();
        TestPdf.Page(doc).Interpret(dev);
        Assert.Empty(dev.Problems);
        Assert.Equal(2, dev.Paints);
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("uses itself"));
    }

    [Fact]
    public void DeepFormChainsStopAtTheRecursionLimit()
    {
        const int n = 60;
        byte[] data = TestPdf.Build("/F Do", "/Resources << /XObject << /F 5 0 R >> >>", b =>
        {
            for (int i = 0; i < n; i++)
            {
                string next = i + 1 < n ? $"/F {6 + i} 0 R" : string.Empty;
                b.Stream(5 + i, $"/Type /XObject /Subtype /Form /BBox [0 0 9 9] /Resources << /XObject << {next} >> >>", Bytes("0 0 1 1 re f " + (i + 1 < n ? "/F Do" : string.Empty)));
            }
        }, objects: 5 + n);
        using PdfDocument doc = TestPdf.Open(data);
        var dev = new CheckingDevice();
        TestPdf.Page(doc).Interpret(dev);
        Assert.Empty(dev.Problems);
        Assert.Equal(doc.Limits.MaxRecursionDepth, dev.Paints);
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("recursion limit"));
    }

    // ---- optional content ----

    private const string Ocg = " /OCProperties << /OCGs [10 0 R 11 0 R] /D << /OFF [11 0 R] >> >>";

    private static void Layers(PdfBuilder b)
    {
        b.Obj(10, "<< /Type /OCG /Name (on) >>");
        b.Obj(11, "<< /Type /OCG /Name (off) >>");
        b.Stream(12, "/Type /XObject /Subtype /Form /BBox [0 0 9 9] /OC 11 0 R", Bytes("0 0 3 3 re f"));
        b.Obj(13, "<< /Type /OCMD /OCGs [10 0 R 11 0 R] /P /AllOn >>");
    }

    private static byte[] LayerPdf(string content) => TestPdf.Build(content,
        "/Resources << /Properties << /On 10 0 R /Off 11 0 R /Both 13 0 R >> /XObject << /X 12 0 R >> >>", Layers, objects: 14, catalogEntries: Ocg);

    [Fact]
    public void HiddenOptionalContentIsNeverEmitted()
    {
        string t = TestPdf.Text(LayerPdf(
            "/OC /Off BDC 0 0 1 1 re f /OC /On BDC 0 0 2 2 re f EMC EMC /OC /On BDC 0 0 3 3 re f EMC /X Do /OC /Both BDC 0 0 4 4 re f EMC"));
        string[] fills = TestPdf.Lines(t, "fill");
        Assert.Equal(["fill nonzero M 0 0 L 3 0 L 3 3 L 0 3 Z"], fills);
        Assert.Single(TestPdf.Lines(t, "begin-mc"));
        Assert.Single(TestPdf.Lines(t, "end-mc"));
    }

    [Fact]
    public void HiddenContentStillUpdatesStateAndClip()
    {
        string t = TestPdf.Text(LayerPdf("/OC /Off BDC 1 0 0 rg 0 0 5 5 re W n EMC 0 0 9 9 re f"));
        Assert.Contains("clip nonzero M 0 0 L 5 0", t);
        Assert.Contains("fill=rgb(1 0 0)", t);
    }

    [Fact]
    public void OverrideMakesGroupsVisibleOrHidden()
    {
        byte[] data = LayerPdf("/OC /Off BDC 0 0 1 1 re f EMC /OC /On BDC 0 0 2 2 re f EMC");
        var show = new PdfInterpretOptions { OptionalContentOverride = g => g.GetString("Name")?.GetText() == "off" ? true : null };
        Assert.Equal(2, TestPdf.Lines(TestPdf.Text(data, show), "fill").Length);
        var hide = new PdfInterpretOptions { OptionalContentOverride = g => g.GetString("Name")?.GetText() == "on" ? false : null };
        Assert.Empty(TestPdf.Lines(TestPdf.Text(data, hide), "fill"));
    }

    [Fact]
    public void MarkedContentIsBalancedAndPropertiesPassThrough()
    {
        string t = T("/Span <</MCID 3 /Lang (en)>> BDC /Artifact BMC 0 0 1 1 re f EMC EMC EMC");
        Assert.Contains("begin-mc /Span props=<</Lang <656e> /MCID 3>>", t);
        Assert.Contains("begin-mc /Artifact props=null", t);
        Assert.Equal(2, TestPdf.Lines(t, "end-mc").Length);
    }

    [Fact]
    public void UnclosedMarkedContentIsClosedAtTheEnd()
    {
        var dev = new CheckingDevice();
        using PdfDocument doc = TestPdf.Open(TestPdf.Build("/A BMC /B BMC 0 0 1 1 re f"));
        TestPdf.Page(doc).Interpret(dev);
        Assert.Empty(dev.Problems);
    }

    [Fact]
    public void MarkedContentMayStraddleSaveRestore()
    {
        var dev = new CheckingDevice();
        using PdfDocument doc = TestPdf.Open(TestPdf.Build("q 0 0 5 5 re W n /A BMC Q 0 0 1 1 re f EMC"));
        TestPdf.Page(doc).Interpret(dev);
        Assert.Empty(dev.Problems);
    }

    // ---- transparency ----

    [Fact]
    public void TransparencyGroupIsBracketedWhenOpacityApplies()
    {
        string res = "/Resources << /XObject << /G 5 0 R >> /ExtGState << /A << /ca 0.5 >> >> >>";
        void Extra(PdfBuilder b) => b.Stream(5, "/Type /XObject /Subtype /Form /BBox [0 0 20 20] /Group << /S /Transparency /I true >>", Bytes("0 0 5 5 re f"));
        string t = T("/A gs /G Do", res, Extra, objects: 6);
        Assert.Contains("begin-group bbox=0 0 20 20 isolated=1 knockout=0", t);
        Assert.Contains("end-group", t);
        // Inside the group the content is opaque again.
        string inner = t[t.IndexOf("begin-group", StringComparison.Ordinal)..];
        Assert.Contains("fa=1 ", inner[inner.IndexOf("gs ctm", StringComparison.Ordinal)..]);
        // Plain opaque use of a non-isolated group needs no bracket.
        string plain = T("/G Do", "/Resources << /XObject << /G 5 0 R >> >>", b => b.Stream(5, "/Type /XObject /Subtype /Form /BBox [0 0 20 20] /Group << /S /Transparency >>", Bytes("0 0 5 5 re f")), objects: 6);
        Assert.DoesNotContain("begin-group", plain);
    }

    [Fact]
    public void SoftMaskIsDefinedInABracketAndReferencedById()
    {
        string res = "/Resources << /ExtGState << /M << /SMask << /Type /Mask /S /Luminosity /G 5 0 R /BC [0] >> >> /N << /SMask /None >> >> >>";
        void Extra(PdfBuilder b) => b.Stream(5, "/Type /XObject /Subtype /Form /BBox [0 0 100 100] /Group << /S /Transparency /CS /DeviceGray >>", Bytes("1 g 0 0 50 50 re f"));
        string t = T("q /M gs 0 0 80 80 re f Q 0 0 1 1 re f q /M gs 0 0 2 2 re f /N gs 0 0 3 3 re f Q", res, Extra, objects: 6);
        Assert.Equal(2, TestPdf.Lines(t, "begin-softmask").Length);
        Assert.Equal(2, TestPdf.Lines(t, "end-softmask").Length);
        Assert.Contains("begin-softmask id=1 type=luminosity bbox=0 0 100 100 bc=[0] tr=null", t);
        string[] gs = TestPdf.Lines(t, "gs");
        Assert.Contains(gs, l => l.Contains(" sm=1 ", StringComparison.Ordinal));
        Assert.Contains(gs, l => l.Contains(" sm=2 ", StringComparison.Ordinal));
        Assert.Contains(gs, l => l.Contains(" sm=0 ", StringComparison.Ordinal));
        var dev = new CheckingDevice();
        using PdfDocument doc = TestPdf.Open(TestPdf.Build("q /M gs 0 0 80 80 re f Q", res, Extra, objects: 6));
        TestPdf.Page(doc).Interpret(dev);
        Assert.Empty(dev.Problems);
    }

    [Fact]
    public void SoftMaskSelfReferenceIsCut()
    {
        string res = "/Resources << /ExtGState << /M << /SMask << /S /Alpha /G 5 0 R >> >> >> >>";
        byte[] data = TestPdf.Build("/M gs 0 0 8 8 re f", res, b =>
            b.Stream(5, "/Type /XObject /Subtype /Form /BBox [0 0 9 9] /Resources << /ExtGState << /M << /SMask << /S /Alpha /G 5 0 R >> >> >> >>", Bytes("/M gs 0 0 1 1 re f")), objects: 6);
        var dev = new CheckingDevice();
        using PdfDocument doc = TestPdf.Open(data);
        TestPdf.Page(doc).Interpret(dev);
        Assert.Empty(dev.Problems);
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("uses itself"));
    }

    // ---- images and shading ----

    [Fact]
    public void ImageXObjectAndInlineImagesReachTheDevice()
    {
        string res = "/Resources << /XObject << /Im 5 0 R >> /Shading << /S1 6 0 R >> >>";
        string t = T("q 10 0 0 10 5 5 cm /Im Do Q BI /W 2 /H 2 /BPC 8 /CS /G ID \u0001\u0002\u0003\u0004 EI 0 0 1 1 re W n /S1 sh", res, b =>
        {
            b.Stream(5, "/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", [0x7f]);
            b.Obj(6, "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 1 0] >>");
        }, objects: 7);
        string[] images = TestPdf.Lines(t, "image");
        Assert.Equal(2, images.Length);
        Assert.StartsWith("image /Im bytes=1", images[0]);
        Assert.StartsWith("image inline bytes=4", images[1]);
        Assert.Contains("/ColorSpace /DeviceGray", images[1]);
        Assert.Single(TestPdf.Lines(t, "shading"));
    }

    [Fact]
    public void InlineImagesInOddLayouts()
    {
        // No space before EI after known-length data, binary data containing EI, EI at the very end, ASCII85 data, nothing after.
        string[] cases =
        [
            "q BI /W 1 /H 1 /BPC 8 /CS /G ID \u0001EI Q 0 0 1 1 re f",
            "q BI /W 2 /H 1 /BPC 8 /CS /G ID EI\nEI Q 0 0 1 1 re f",
            "q BI /W 1 /H 1 /BPC 8 /CS /G /F /A85 ID !!~> EI Q 0 0 1 1 re f",
            "q BI/W 1/H 1/BPC 8/CS/G ID\r\n\u0009 EI Q 0 0 1 1 re f",
            "0 0 1 1 re f BI /W 1 /H 1 /BPC 8 /CS /G ID \u0007 EI",
            "q BI /W 8 /H 1 /IM true /D [1 0] ID \u00aa EI Q 0 0 1 1 re f",
        ];
        foreach (string c in cases)
        {
            var dev = new CheckingDevice();
            using PdfDocument doc = TestPdf.Open(TestPdf.Build(c));
            TestPdf.Page(doc).Interpret(dev);
            Assert.Empty(dev.Problems);
            Assert.Equal(c.Contains("0 0 1 1 re f", StringComparison.Ordinal) ? 2 : 1, dev.Paints);
        }
    }

    [Fact]
    public void InlineImageUsesNamedColorSpaceFromResources()
    {
        byte[] data = TestPdf.Build("BI /W 2 /H 1 /BPC 8 /CS /Cs1 ID \u0001\u0002\u0003\u0004\u0005\u0006 EI 0 0 1 1 re f",
            "/Resources << /ColorSpace << /Cs1 [/ICCBased 5 0 R] >> >>", b => b.Stream(5, "/N 3", [0]), objects: 6);
        string t = TestPdf.Text(data);
        Assert.Contains("image inline bytes=6", t);
        Assert.Single(TestPdf.Lines(t, "fill"));
    }

    // ---- annotations ----

    private static byte[] AnnotPdf(string annotEntries, string apBody = "0 0 100 50 re f", string apDict = "/BBox [0 0 100 50]")
    {
        return TestPdf.Build("0 0 1 1 re f", "/Annots [10 0 R]", b =>
        {
            b.Obj(10, $"<< /Type /Annot /Subtype /Square /Rect [20 30 70 55] {annotEntries} >>");
            b.Stream(11, $"/Type /XObject /Subtype /Form {apDict}", Bytes(apBody));
        }, objects: 12);
    }

    [Fact]
    public void AnnotationAppearanceIsFittedToTheRect()
    {
        string t = TestPdf.Text(AnnotPdf("/AP << /N 11 0 R >> /F 4"));
        Assert.Contains("begin-annot /Square rect=20 30 70 55 flags=4", t);
        // BBox 100x50 onto Rect 50x25: scale 0.5, then the page's y flip.
        Assert.Contains("ctm=[0.5 0 0 -0.5 20 170]", t);
        Assert.Contains("end-annot", t);
    }

    [Fact]
    public void AnnotationMatrixIsAppliedBeforeFitting()
    {
        // The form matrix rotates the box by 90 degrees; the fitted result must land inside the Rect.
        string t = TestPdf.Text(AnnotPdf("/AP << /N 11 0 R >>", apDict: "/BBox [0 0 100 50] /Matrix [0 1 -1 0 0 0]"));
        Assert.Contains("ctm=", t);
        Assert.Contains("begin-annot", t);
    }

    [Fact]
    public void AnnotationStatesAndFlags()
    {
        byte[] withStates = TestPdf.Build("", "/Annots [10 0 R]", b =>
        {
            b.Obj(10, "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /AS /On /AP << /N << /On 11 0 R /Off 12 0 R >> >> >>");
            b.Stream(11, "/Type /XObject /Subtype /Form /BBox [0 0 10 10]", Bytes("0 0 5 5 re f"));
            b.Stream(12, "/Type /XObject /Subtype /Form /BBox [0 0 10 10]", Bytes("0 0 6 6 re f"));
        }, objects: 13);
        string t = TestPdf.Text(withStates);
        Assert.Contains("M 0 0 L 5 0", t);
        Assert.DoesNotContain("L 6 0", t);

        Assert.DoesNotContain("begin-annot", TestPdf.Text(AnnotPdf("/AP << /N 11 0 R >> /F 2")));
        Assert.DoesNotContain("begin-annot", TestPdf.Text(AnnotPdf("/AP << /N 11 0 R >> /F 32")));
        Assert.Contains("begin-annot", TestPdf.Text(AnnotPdf("/AP << /N 11 0 R >> /F 4"), new PdfInterpretOptions { Annotations = PdfAnnotationMode.Print }));
        Assert.DoesNotContain("begin-annot", TestPdf.Text(AnnotPdf("/AP << /N 11 0 R >>"), new PdfInterpretOptions { Annotations = PdfAnnotationMode.Print }));
        Assert.DoesNotContain("begin-annot", TestPdf.Text(AnnotPdf("/AP << /N 11 0 R >> /F 4"), new PdfInterpretOptions { Annotations = PdfAnnotationMode.None }));
    }

    [Fact]
    public void AnnotationWithoutAppearanceDrawsNothingAndRecordsADiagnostic()
    {
        byte[] data = AnnotPdf(string.Empty);
        using PdfDocument doc = TestPdf.Open(data);
        string t = TestPdf.Record(doc).ToText();
        Assert.DoesNotContain("begin-annot", t);
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("no normal appearance"));
    }

    [Fact]
    public void HiddenOptionalContentHidesAnnotations()
    {
        byte[] data = TestPdf.Build("", "/Annots [14 0 R]", b =>
        {
            Layers(b);
            b.Obj(14, "<< /Type /Annot /Subtype /Square /Rect [0 0 10 10] /OC 11 0 R /AP << /N 15 0 R >> >>");
            b.Stream(15, "/Type /XObject /Subtype /Form /BBox [0 0 10 10]", Bytes("0 0 5 5 re f"));
        }, objects: 16, catalogEntries: Ocg);
        Assert.DoesNotContain("begin-annot", TestPdf.Text(data));
    }

    // ---- cancellation ----

    private sealed class CancelAfter(int calls, CancellationTokenSource cts) : PdfDevice
    {
        private int _n;
        public override void FillPath(in PdfPath path, bool evenOdd, in PdfGraphicsState state)
        {
            if (++_n == calls)
            {
                cts.Cancel();
            }
        }

        public List<string> Log { get; } = [];

        public override void BeginPage(in PdfPageInfo page) => Log.Add("bp");

        public override void EndPage() => Log.Add("ep");

        public override void PushClip(in PdfPath path, bool evenOdd, in PdfGraphicsState state) => Log.Add("push");

        public override void PopClip() => Log.Add("pop");
    }

    [Fact]
    public void CancellationClosesBracketsAndThrows()
    {
        var sb = new StringBuilder("q 0 0 9 9 re W n q 0 0 8 8 re W n ");
        for (int i = 0; i < 2000; i++)
        {
            sb.Append("0 0 1 1 re f ");
        }

        using var cts = new CancellationTokenSource();
        var dev = new CancelAfter(10, cts);
        using PdfDocument doc = TestPdf.Open(TestPdf.Build(sb.ToString()));
        Assert.Throws<OperationCanceledException>(() => TestPdf.Page(doc).Interpret(dev, null, cts.Token));
        Assert.Equal(["bp", "push", "push", "pop", "pop", "ep"], dev.Log);
    }
}
