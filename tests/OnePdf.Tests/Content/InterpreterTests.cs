using System.Text;
using OnePdf.Content;

namespace OnePdf.Tests.Content;

public class InterpreterTests
{
    private static string T(string content, string pageEntries = "", Action<PdfBuilder>? extra = null, PdfInterpretOptions? options = null, int objects = 5, string mediaBox = "[0 0 200 200]") =>
        TestPdf.Text(content, pageEntries, extra, options, objects, mediaBox);

    private static IReadOnlyList<PdfDiagnostic> DiagsOf(byte[] data, PdfInterpretOptions? options = null)
    {
        using PdfDocument doc = TestPdf.Open(data);
        TestPdf.Record(doc, 0, options);
        return doc.Diagnostics;
    }

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    [Fact]
    public void FilledRectangleIsReportedInUserSpaceWithTheFullMatrix()
    {
        string t = T("1 0 0 rg 10 10 50 50 re f");
        Assert.Contains("page box=0 0 200 200 name=CropBox rotate=0 unit=1 scale=1 size=200 200 base=[1 0 0 -1 0 200]", t);
        Assert.Contains("gs ctm=[1 0 0 -1 0 200] fill=rgb(1 0 0) stroke=gray(0)", t);
        Assert.Contains("fill nonzero M 10 10 L 60 10 L 60 60 L 10 60 Z", t);
        Assert.StartsWith("onepdf-display-list 1\n", t);
    }

    [Fact]
    public void CmComposesInPdfOrder()
    {
        string t = T("2 0 0 2 5 5 cm 1 0 0 1 10 0 cm 0 0 1 1 re f");
        // The later cm applies first: [1 0 0 1 10 0] x [2 0 0 2 5 5] x base.
        Assert.Contains("ctm=[2 0 0 -2 25 195]", t);
    }

    [Fact]
    public void AllPaintingOperatorsMapToFillAndStroke()
    {
        string content = "0 0 m 10 0 l S  0 0 m 10 0 l 10 10 l s  0 0 10 10 re f  0 0 10 10 re F  0 0 10 10 re f*  0 0 10 10 re B  0 0 10 10 re B*  0 0 10 10 re b  0 0 10 10 re b*  0 0 10 10 re n";
        string t = T(content);
        Assert.Equal(7, TestPdf.Lines(t, "fill").Length);
        Assert.Equal(2 + 4, TestPdf.Lines(t, "stroke").Length);
        Assert.Equal(3, TestPdf.Lines(t, "fill").Count(l => l.StartsWith("fill evenodd", StringComparison.Ordinal)));
        // s closes the subpath; S does not.
        string[] strokes = TestPdf.Lines(t, "stroke");
        Assert.DoesNotContain("Z", strokes[0]);
        Assert.EndsWith("Z", strokes[1]);
    }

    [Fact]
    public void CurvesVAndYUseTheCurrentPointAndEndPoint()
    {
        string t = T("0 0 m 1 2 3 4 v 5 6 7 8 y S");
        Assert.Contains("stroke M 0 0 C 0 0 1 2 3 4 C 5 6 7 8 7 8", t);
    }

    [Fact]
    public void SegmentsWithoutCurrentPointAreSkippedWithADiagnostic()
    {
        byte[] data = TestPdf.Build("10 10 l 0 0 m 5 5 l S");
        string t = TestPdf.Text(data);
        Assert.Contains("stroke M 0 0 L 5 5", t);
        Assert.Contains(DiagsOf(data), d => d.Message.Contains("without a current point"));
    }

    [Fact]
    public void ClipNestingPushesAndPopsInOrder()
    {
        string t = T("q 0 0 100 100 re W n q 10 10 50 50 re W* n 0 0 1 1 re f Q 5 5 5 5 re f Q 1 1 1 1 re f");
        string[] lines = t.Split('\n');
        int clip1 = Array.FindIndex(lines, l => l.TrimStart().StartsWith("clip nonzero", StringComparison.Ordinal));
        int clip2 = Array.FindIndex(lines, l => l.TrimStart().StartsWith("clip evenodd", StringComparison.Ordinal));
        int pops = lines.Count(l => l.TrimStart() == "pop-clip");
        Assert.True(clip1 < clip2);
        Assert.Equal(2, pops);
        // Indentation shows nesting: the fill after the first Q sits one level inside the outer clip only.
        string[] fills = lines.Where(l => l.TrimStart().StartsWith("fill ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, fills.Length);
        Assert.True(Indent(fills[0]) > Indent(fills[1]));
        Assert.True(Indent(fills[1]) > Indent(fills[2]));
        var dev = new CheckingDevice();
        using PdfDocument doc = TestPdf.Open(TestPdf.Build("q 0 0 100 100 re W n q 10 10 50 50 re W* n 0 0 1 1 re f Q 5 5 5 5 re f Q"));
        TestPdf.Page(doc).Interpret(dev);
        Assert.Empty(dev.Problems);
        Assert.Equal(2, dev.Clips);
    }

    [Fact]
    public void UnbalancedSaveAndRestoreAreTolerated()
    {
        byte[] data = TestPdf.Build("Q Q 1 0 0 rg q q q 0 0 10 10 re W n 0 0 1 1 re f");
        var dev = new CheckingDevice();
        using PdfDocument doc = TestPdf.Open(data);
        TestPdf.Page(doc).Interpret(dev);
        Assert.Empty(dev.Problems);
        Assert.True(dev.Ended);
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("Q without"));
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("Unbalanced"));
    }

    [Fact]
    public void RestoreDoesNotPopBelowTheStartOfAForm()
    {
        string t = T("q /F Do Q 0 0 1 1 re f", "/Resources << /XObject << /F 5 0 R >> >>", b =>
            b.Stream(5, "/Type /XObject /Subtype /Form /BBox [0 0 100 100]", Encoding.Latin1.GetBytes("Q Q 0 0 5 5 re f")), objects: 6);
        Assert.Equal(2, TestPdf.Lines(t, "fill").Length);
    }

    [Fact]
    public void DeeplyNestedSavesAreCappedAndStayBalanced()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < ContentInterpreter.MaxSaveDepth + 50; i++)
        {
            sb.Append("q 0 0 1 1 re W n ");
        }

        var dev = new CheckingDevice();
        using PdfDocument doc = TestPdf.Open(TestPdf.Build(sb.ToString()));
        TestPdf.Page(doc).Interpret(dev);
        Assert.Empty(dev.Problems);
    }

    [Fact]
    public void GeneralGraphicsStateAndExtGState()
    {
        string t = T("3 w 1 J 2 j 4 M [3 2] 1 d /Perceptual ri 0.5 i /G1 gs 0 0 m 5 5 l S", "/Resources << /ExtGState << /G1 << /LW 7 /CA 0.5 /ca 0.25 /BM /Multiply /OP true /OPM 1 /AIS true >> >> >>");
        Assert.Contains("lw=7 cap=1 join=2 miter=4 dash=[3 2] phase=1 flat=0.5 ri=Perceptual fa=0.25 sa=0.5 bm=Multiply sm=0 ais=1 op=111", t);
    }

    [Fact]
    public void InvalidDashAndUnknownBlendFallBack()
    {
        string t = T("[-1 2] 0 d [0 0] 0 d 0 0 m 1 1 l S", "/Resources << /ExtGState << /G1 << /BM /Bogus >> >> >>");
        Assert.Contains("dash=[]", t);
        // An array picks the first standard mode it names.
        Assert.Contains("bm=Screen", T("/G1 gs 0 0 m 1 1 l S", "/Resources << /ExtGState << /G1 << /BM [/Bogus /Screen] >> >> >>"));
    }

    [Fact]
    public void ColorOperatorsAndSpaces()
    {
        string res = "/Resources << /ColorSpace << /Cal [/CalRGB << /WhitePoint [1 1 1] >>] /L [/Lab << /WhitePoint [0.95 1 1.09] /Range [-50 50 -50 50] >>] /I [/Indexed /DeviceRGB 1 <FF0000 0000FF>] /S [/Separation /Spot /DeviceCMYK 6 0 R] /ICC [/ICCBased 5 0 R] >> >>";
        string t = T("0.25 g 1 0 0 RG 0 1 0 0 k /Cal cs 0.5 0.5 0.5 sc /L cs 50 80 -90 sc /I cs 1 sc /ICC cs 0.1 0.2 0.3 sc 0 0 m 1 1 l B", res,
            b => b.Stream(5, "/N 3", [1, 2, 3, 4]).Obj(6, "<< /FunctionType 2 /Domain [0 1] /N 1 >>"), objects: 7);
        Assert.Contains("fill=icc3:4:", t);
        Assert.Contains("(0.1 0.2 0.3)", t);
        // Lab: a clamped to its range.
        string t2 = T("/L cs 50 80 -90 sc 0 0 m 1 1 l f", res);
        Assert.Contains("fill=lab(50 50 -50)", t2);
        string t3 = T("/I cs 1 sc 0 0 m 1 1 l f", res);
        Assert.Contains("fill=rgb(0 0 1)", t3);
        string t4 = T("0 1 0 0 k 0 0 m 1 1 l f");
        Assert.Contains("fill=cmyk(0 1 0 0)", t4);
        string t5 = T("/Cal cs 0 0 m 1 1 l f", res);
        Assert.Contains("fill=rgb(0 0 0)", t5);
    }

    [Fact]
    public void SeparationPassesThroughAlternateWithDiagnosticUntilFunctionsAreWired()
    {
        byte[] data = TestPdf.Build("/S cs 0.5 sc 0 0 m 1 1 l f", "/Resources << /ColorSpace << /S [/Separation /Spot /DeviceGray 5 0 R] >> >>",
            b => b.Obj(5, "<< /FunctionType 2 /Domain [0 1] /N 1 >>"), objects: 6);
        string t = TestPdf.Text(data);
        Assert.Contains("fill=gray(0.5)", t);
        Assert.Contains(DiagsOf(data), d => d.Message.Contains("tint transform"));
    }

    [Fact]
    public void PatternColourIsPassedAsAPlaceholder()
    {
        byte[] data = TestPdf.Build("/Pattern cs /P1 scn 0 0 10 10 re f", "/Resources << /Pattern << /P1 5 0 R >> >>",
            b => b.Obj(5, "<< /PatternType 2 >>"), objects: 6);
        Assert.Contains("fill=pattern(/P1)", TestPdf.Text(data));
    }

    [Fact]
    public void BadOperandsAndUnknownOperatorsAreSkippedAndRecordedOnce()
    {
        byte[] data = TestPdf.Build("1 2 re 1 2 re foo foo 5 w (x) w 0 0 10 10 re f");
        string t = TestPdf.Text(data);
        Assert.Single(TestPdf.Lines(t, "fill"));
        IReadOnlyList<PdfDiagnostic> d = DiagsOf(data);
        Assert.Single(d, x => x.Message.Contains("Operator Rect"));
        Assert.Single(d, x => x.Message.Contains("'foo'"));
    }

    [Fact]
    public void CompatibilitySectionsHideUnknownOperators()
    {
        byte[] data = TestPdf.Build("BX zork EX");
        Assert.DoesNotContain(DiagsOf(data), d => d.Message.Contains("zork"));
    }

    [Fact]
    public void OperatorFloodIsStoppedByTheLimitAndStaysBalanced()
    {
        var sb = new StringBuilder("q 0 0 5 5 re W n BT ");
        for (int i = 0; i < 5000; i++)
        {
            sb.Append("0 0 m ");
        }

        byte[] data = TestPdf.Build(sb.ToString());
        var opts = new PdfOpenOptions { Limits = PdfLimits.Default with { MaxOperatorsPerPage = 1000 } };
        using PdfDocument doc = TestPdf.Open(data, opts);
        var dev = new CheckingDevice();
        TestPdf.Page(doc).Interpret(dev);
        Assert.Empty(dev.Problems);
        Assert.True(dev.Ended);
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("more content operators than the limit"));
    }

    [Fact]
    public void HugeOperandRunsDoNotOverflowTheOperandStack()
    {
        string content = string.Concat(Enumerable.Repeat("1 ", 100_000)) + "re 0 0 1 1 re f";
        Assert.Single(TestPdf.Lines(T(content), "fill"));
    }

    // ---- rendering geometry ----

    [Fact]
    public void RotatedAndScaledPagesMapTheBoxToOutputSpace()
    {
        // 100 x 50 box at (10, 20), rotated 90 degrees clockwise, 2x.
        string t = T("0 0 1 1 re f", "/Rotate 90 /CropBox [10 20 110 70]", options: new PdfInterpretOptions { Scale = 2 });
        Assert.Contains("rotate=90 unit=1 scale=2 size=100 200 base=[0 2 2 0 -40 -20]", t);
        // Origin (10,20) is the box's bottom-left: after a clockwise turn it lands at the top-left of the output.
        string t180 = T("0 0 1 1 re f", "/Rotate 180 /CropBox [10 20 110 70]");
        Assert.Contains("rotate=180 unit=1 scale=1 size=100 50 base=[-1 0 0 1 110 -20]", t180);
        string t270 = T("0 0 1 1 re f", "/Rotate -90 /CropBox [10 20 110 70]");
        Assert.Contains("rotate=270 unit=1 scale=1 size=50 100 base=[0 -1 -1 0 70 110]", t270);
    }

    [Fact]
    public void BoxSelectionUserUnitAndDpi()
    {
        string pageEntries = "/CropBox [0 0 100 100] /TrimBox [10 10 90 90] /UserUnit 2";
        string crop = T("0 0 1 1 re f", pageEntries);
        Assert.Contains("box=0 0 100 100 name=CropBox rotate=0 unit=2 scale=2 size=200 200", crop);
        string trim = T("0 0 1 1 re f", pageEntries, options: new PdfInterpretOptions { Box = PdfPageBox.TrimBox, Dpi = 144, ApplyUserUnit = false });
        Assert.Contains("box=10 10 90 90 name=TrimBox rotate=0 unit=1 scale=2 size=160 160 base=[2 0 0 -2 -20 180]", trim);
        string media = T("0 0 1 1 re f", pageEntries, options: new PdfInterpretOptions { Box = PdfPageBox.MediaBox, ApplyRotation = false, ApplyUserUnit = false });
        Assert.Contains("box=0 0 200 200 name=MediaBox", media);
        // An art box that is missing falls back to the crop box.
        Assert.Contains("name=ArtBox", T("0 0 1 1 re f", pageEntries, options: new PdfInterpretOptions { Box = PdfPageBox.ArtBox }));
    }

    [Fact]
    public void InheritedPageAttributesAreFound()
    {
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 100] /Rotate 90 /Resources << /ExtGState << /G << /LW 9 >> >> >> >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>");
        b.Stream(4, string.Empty, "/G gs 0 0 m 1 1 l S"u8.ToArray());
        long x = b.XrefTable(5);
        b.Trailer("/Size 5 /Root 1 0 R", x);
        string t = TestPdf.Text(b.ToArray());
        Assert.Contains("rotate=90", t);
        Assert.Contains("size=100 300", t);
        Assert.Contains("lw=9", t);
    }
}

public class ContentRepros
{
    [Fact]
    public void ClipsPushedOutsideAnyQAreClosedAtTheEndOfThePage()
    {
        var dev = new CheckingDevice();
        using PdfDocument doc = TestPdf.Open(TestPdf.Build("0 0 5 5 re W n 1 0 0 1 0 0 cm 0 0 1 1 re W f"));
        TestPdf.Page(doc).Interpret(dev);
        Assert.Empty(dev.Problems);
        Assert.Equal(2, dev.Clips);
    }

    [Fact]
    public void FuzzReproClipOutsideQLeftOpen()
    {
        string? file = FindRepro("content-clip-outside-q-left-open.txt");
        Assert.SkipWhen(file is null, "testdata/repros is not present");
        OnePdf.Fuzz.ContentTarget.Run(File.ReadAllBytes(file!));
        using PdfDocument doc = TestPdf.Open(OnePdf.Fuzz.ContentTarget.Wrap(File.ReadAllBytes(file!)));
        var dev = new CheckingDevice();
        TestPdf.Page(doc).Interpret(dev);
        Assert.Empty(dev.Problems);
    }

    private static string? FindRepro(string name, [System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (string? d = Path.GetDirectoryName(source); d is not null; d = Path.GetDirectoryName(d))
        {
            string c = Path.Combine(d, "testdata", "repros", name);
            if (File.Exists(c))
            {
                return c;
            }
        }

        return null;
    }
}
