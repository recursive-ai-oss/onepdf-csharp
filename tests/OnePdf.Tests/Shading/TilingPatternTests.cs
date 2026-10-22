using System.Text;
using OnePdf.Tests.Content;
using static OnePdf.Tests.Shading.ShadingKit;

namespace OnePdf.Tests.Shading;

public class TilingPatternTests
{
    private const string Cell = "/Type /Pattern /PatternType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 ";

    private static string Res(string patterns, string spaces = "") =>
        $"/Resources << /Pattern << {patterns} >> /ColorSpace << /Cs [/Pattern /DeviceRGB] {spaces} >> >>";

    private static Action<PdfBuilder> Patterns(params (int N, string Dict, string Content)[] items) => b =>
    {
        foreach ((int n, string dict, string content) in items)
        {
            b.Stream(n, dict, Encoding.Latin1.GetBytes(content));
        }
    };

    private static (PdfDocument Doc, PdfDisplayList List, Collector Dev) Run(string content, string res, params (int, string, string)[] items)
    {
        PdfDocument doc = TestPdf.Open(TestPdf.Build(content, res, Patterns(items), objects: 12));
        PdfDisplayList list = TestPdf.Record(doc);
        var dev = new Collector();
        list.Replay(dev);
        return (doc, list, dev);
    }

    private static PdfTilingPattern Tiling(PdfGraphicsState s) => Assert.IsType<PdfTilingPattern>(s.FillColor.Pattern);

    [Fact]
    public void ColouredPatternIsInterpretedOnceIntoACell()
    {
        var (doc, list, dev) = Run(
            "/Pattern cs /P1 scn 20 20 100 100 re f 0 0 5 5 re f",
            Res("/P1 6 0 R"),
            (6, Cell + "/PaintType 1 /Matrix [2 0 0 2 0 0] /Resources << >>", "1 0 0 rg 0 0 5 5 re f"));
        using (doc)
        {
            Assert.Equal(2, dev.States.Count);
            PdfTilingPattern p = Tiling(dev.States[0]);
            Assert.Same(p, Tiling(dev.States[1]));
            Assert.True(p.IsColored);
            Assert.Equal(1, p.TilingType);
            Assert.Equal(10, p.XStep);
            Assert.Equal(10, p.YStep);
            Assert.Equal(new PdfBounds(0, 0, 10, 10), p.BBox);

            // /Matrix first, then the page's base transform (not the CTM at the painting site).
            Assert.Equal(new PdfMatrix(2, 0, 0, -2, 0, 200), p.Matrix);
            Assert.NotNull(p.Cell);

            string text = list.ToText();
            Assert.Single(TestPdf.Lines(text, "pattern-def"));
            string cell = p.Cell!.ToText();
            Assert.Contains("clip nonzero M 0 0 L 10 0 L 10 10 L 0 10 Z", cell);
            Assert.Contains("gs ctm=[1 0 0 1 0 0] fill=rgb(1 0 0)", cell);
            Assert.Contains("fill nonzero M 0 0 L 5 0 L 5 5 L 0 5 Z", cell);
            Assert.Contains("fill=pattern(#1 /P1)", text);
            Assert.Contains("tiling paint=1 tile=1 bbox=0 0 10 10 step=10 10 matrix=[2 0 0 -2 0 200]", text);
        }
    }

    [Fact]
    public void UncolouredPatternIgnoresColoursInItsCellAndCarriesTheTint()
    {
        var (doc, _, dev) = Run(
            "/Cs cs 0 0.5 1 /P1 scn 20 20 100 100 re f",
            Res("/P1 6 0 R"),
            (6, Cell + "/PaintType 2 /Resources << >>", "1 0 0 rg 0 1 0 RG /Cs cs 1 1 1 /P1 scn 0 0 5 5 re f"));
        using (doc)
        {
            PdfGraphicsState s = Assert.Single(dev.States);
            PdfTilingPattern p = Tiling(s);
            Assert.False(p.IsColored);
            Assert.Equal(PdfColorSpaceKind.Pattern, s.FillColor.Space.Kind);
            Assert.Equal("rgb", s.FillColor.Space.PatternBase!.Name);
            Assert.Equal(0, s.FillColor.C0);
            Assert.Equal(0.5, s.FillColor.C1);
            Assert.Equal(1, s.FillColor.C2);

            // The colour operators in the cell had no effect: everything is still the initial black.
            string cell = p.Cell!.ToText();
            Assert.Contains("fill=gray(0) stroke=gray(0)", cell);
            Assert.DoesNotContain("rgb(", cell);
            Assert.DoesNotContain("pattern(", cell);
        }
    }

    [Fact]
    public void ShadingsAreSkippedInUncolouredCells()
    {
        var (doc, _, dev) = Run(
            "/Cs cs 0 0 0 /P1 scn 0 0 50 50 re f",
            Res("/P1 6 0 R"),
            (6, Cell + "/PaintType 2 /Resources << /Shading << /S 7 0 R >> >>", "/S sh 0 0 5 5 re f"));
        using (doc)
        {
            // The shading object does not exist here; the point is that the cell holds no sh call.
            Assert.DoesNotContain("shading", Tiling(dev.States[0]).Cell!.ToText());
        }
    }

    [Fact]
    public void NestedPatternsAreAnchoredToTheirParentCell()
    {
        var (doc, list, dev) = Run(
            "/Pattern cs /P1 scn 0 0 100 100 re f",
            Res("/P1 6 0 R"),
            (6, Cell + "/Matrix [1 0 0 1 3 4] /Resources << /Pattern << /P2 7 0 R >> >>", "/Pattern cs /P2 scn 0 0 10 10 re f"),
            (7, Cell + "/Matrix [4 0 0 4 0 0] /BBox [0 0 5 5] /XStep 5 /YStep 5 /Resources << >>", "0 0 1 rg 0 0 2 2 re f"));
        using (doc)
        {
            PdfTilingPattern outer = Tiling(dev.States[0]);
            Assert.NotNull(outer.Cell);
            PdfTilingPattern inner = Assert.IsType<PdfTilingPattern>(FirstFillColor(outer.Cell!));
            Assert.NotNull(inner.Cell);

            // Inside a cell the base space is the cell's own, so the inner pattern's matrix is just its /Matrix.
            Assert.Equal(new PdfMatrix(4, 0, 0, 4, 0, 0), inner.Matrix);
            Assert.Equal(new PdfMatrix(1, 0, 0, -1, 3, 196), outer.Matrix);
            string text = list.ToText();
            Assert.Equal(2, TestPdf.Lines(text, "pattern-def").Length);
            Assert.Contains("fill=rgb(0 0 1)", text);
        }
    }

    private static PdfPattern? FirstFillColor(PdfDisplayList cell)
    {
        var c = new Collector();
        cell.Replay(c);
        return c.States.Select(s => s.FillColor.Pattern).FirstOrDefault(p => p is not null);
    }

    [Fact]
    public void APatternThatPaintsWithItselfStopsAtTheInnerUse()
    {
        var (doc, list, dev) = Run(
            "/Pattern cs /P1 scn 0 0 100 100 re f",
            Res("/P1 6 0 R"),
            (6, Cell + "/Resources << /Pattern << /P1 6 0 R >> >>", "/Pattern cs /P1 scn 0 0 10 10 re f"));
        using (doc)
        {
            PdfTilingPattern p = Tiling(dev.States[0]);
            Assert.NotNull(p.Cell);
            Assert.Contains(doc.Diagnostics, d => d.Message.Contains("paints with itself"));
            PdfTilingPattern? inner = FirstFillColor(p.Cell!) as PdfTilingPattern;
            Assert.NotNull(inner);
            Assert.Null(inner!.Cell);
            _ = list.ToText();
        }
    }

    [Fact]
    public void ACycleThroughTwoPatternsIsStopped()
    {
        var (doc, list, _) = Run(
            "/Pattern cs /A scn 0 0 100 100 re f",
            Res("/A 6 0 R"),
            (6, Cell + "/Resources << /Pattern << /B 7 0 R >> >>", "/Pattern cs /B scn 0 0 10 10 re f"),
            (7, Cell + "/Resources << /Pattern << /A 6 0 R >> >>", "/Pattern cs /A scn 0 0 10 10 re f"));
        using (doc)
        {
            Assert.Contains(doc.Diagnostics, d => d.Message.Contains("paints with itself"));
            Assert.Equal(3, TestPdf.Lines(list.ToText(), "pattern-def").Length); // A, B and the inner A without a cell
        }
    }

    [Fact]
    public void PatternNestingBeyondTheRecursionLimitIsCutOff()
    {
        const int chain = 45;
        var items = new List<(int, string, string)>();
        for (int i = 0; i < chain; i++)
        {
            int n = 6 + i;
            string next = i + 1 < chain ? $"/Pattern << /N {n + 1} 0 R >>" : string.Empty;
            string body = i + 1 < chain ? "/Pattern cs /N scn 0 0 10 10 re f" : "0 0 1 1 re f";
            items.Add((n, Cell + $"/Resources << {next} >>", body));
        }

        byte[] file = TestPdf.Build("/Pattern cs /N scn 0 0 100 100 re f", "/Resources << /Pattern << /N 6 0 R >> >>", Patterns([.. items]), objects: 6 + chain);
        using PdfDocument doc = TestPdf.Open(file);
        PdfDisplayList list = TestPdf.Record(doc);
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("recursion limit"));
        string text = list.ToText();
        Assert.InRange(TestPdf.Lines(text, "pattern-def").Length, 2, 40);
    }

    [Fact]
    public void HugeStepsAndBoxesAreJustData()
    {
        var (doc, list, dev) = Run(
            "/Pattern cs /P1 scn 0 0 100 100 re f",
            Res("/P1 6 0 R"),
            (6, "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [-1000000000 -1000000000 1000000000 1000000000] /XStep 100000000000000000 /YStep -100000000000000000 /Resources << >> /Matrix [0.000000001 0 0 0.000000001 0 0]", "0 0 100000000 100000000 re f"));
        using (doc)
        {
            PdfTilingPattern p = Tiling(dev.States[0]);
            Assert.Equal(1e17, p.XStep);
            Assert.Equal(-1e17, p.YStep);
            Assert.NotNull(p.Cell);
            _ = list.ToText();
            list.Replay(new Collector(), new PdfBounds(0, 0, 10, 10));
        }
    }

    [Fact]
    public void UnusableStepsFallBackToTheBoxAndBadPatternsAreSkipped()
    {
        var (doc, _, dev) = Run(
            "/Pattern cs /P1 scn 0 0 100 100 re f /P2 scn 0 0 5 5 re f /P3 scn 0 0 5 5 re f /Missing scn 0 0 5 5 re f",
            Res("/P1 6 0 R /P2 7 0 R /P3 8 0 R"),
            (6, "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 8 4] /XStep 0 /Resources << >>", "0 0 1 1 re f"),
            (7, "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 0 4] /XStep 1 /YStep 1 /Resources << >>", "0 0 1 1 re f"),
            (8, "/Type /Pattern /PatternType 7 /BBox [0 0 8 4] /Resources << >>", "0 0 1 1 re f"));
        using (doc)
        {
            PdfTilingPattern p = Tiling(dev.States[0]);
            Assert.Equal(8, p.XStep);
            Assert.Equal(4, p.YStep);

            // The empty-box, unknown-type and missing patterns left the colour as it was (P1), so the later fills still use it.
            Assert.All(dev.States, s => Assert.Same(p, s.FillColor.Pattern));
            Assert.Contains(doc.Diagnostics, d => d.Message.Contains("empty /BBox"));
            Assert.Contains(doc.Diagnostics, d => d.Message.Contains("/Missing is not defined"));
        }
    }

    [Fact]
    public void PatternsSelectedInAFormAreAnchoredToTheForm()
    {
        PdfDocument doc = TestPdf.Open(TestPdf.Build(
            "q 1 0 0 1 100 0 cm /F Do Q /Pattern cs /P1 scn 0 0 5 5 re f",
            "/Resources << /XObject << /F 7 0 R >> /Pattern << /P1 6 0 R >> >>",
            b =>
            {
                b.Stream(6, Cell + "/Resources << >>", Encoding.Latin1.GetBytes("0 0 1 1 re f"));
                b.Stream(7, "/Type /XObject /Subtype /Form /BBox [0 0 100 100] /Matrix [1 0 0 1 0 50] /Resources << /Pattern << /P1 6 0 R >> >>", Encoding.Latin1.GetBytes("/Pattern cs /P1 scn 0 0 5 5 re f"));
            },
            objects: 12));
        using (doc)
        {
            var dev = new Collector();
            TestPdf.Record(doc).Replay(dev);
            PdfTilingPattern inForm = Tiling(dev.States[0]);
            PdfTilingPattern onPage = Tiling(dev.States[1]);

            // The form's space is the page's base shifted by the form matrix and the cm in force at Do.
            Assert.Equal(new PdfMatrix(1, 0, 0, -1, 100, 150), inForm.Matrix);
            Assert.Equal(new PdfMatrix(1, 0, 0, -1, 0, 200), onPage.Matrix);
            Assert.NotSame(inForm, onPage);
        }
    }

    [Fact]
    public void ShadingPatternsCarryTheShadingAndTheMatrix()
    {
        byte[] file = TestPdf.Build(
            "/Pattern cs /P1 scn 10 10 80 80 re f /Pattern CS /P1 SCN 10 10 80 80 re S",
            "/Resources << /Pattern << /P1 7 0 R >> >>",
            b =>
            {
                b.Obj(6, "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 100 0] /Background [0 1 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >>");
                b.Obj(7, "<< /Type /Pattern /PatternType 2 /Shading 6 0 R /Matrix [1 0 0 1 20 0] >>");
            },
            objects: 12);
        using PdfDocument doc = TestPdf.Open(file);
        PdfDisplayList list = TestPdf.Record(doc);
        var dev = new Collector();
        list.Replay(dev);
        var sp = Assert.IsType<PdfShadingPattern>(dev.States[0].FillColor.Pattern);
        Assert.Equal(2, sp.Shading.ShadingType);
        Assert.Equal(new PdfMatrix(1, 0, 0, -1, 20, 200), sp.Matrix);
        Near([0.5, 0, 0.5], AtPoint(sp.Shading, 50, 0));
        Assert.True(sp.Shading.HasBackground);
        Assert.Same(sp, dev.States[1].StrokeColor.Pattern);
        string text = list.ToText();
        Assert.Contains("pattern-def #1 /P1 shading matrix=[1 0 0 -1 20 200]", text);
        Assert.Contains("shading type=2 cs=rgb fn=1 bbox=none bg=(0 1 0) aa=0 coords=(0 0 100 0) domain=(0 1) extend=00 at-start=(1 0 0) at-mid=(0.5 0 0.5) at-end=(0 0 1)", text);
    }

    [Fact]
    public void RecordingIsDeterministicAndConcurrentInterpretationsAgree()
    {
        byte[] file = TestPdf.Build(
            "/Pattern cs /P1 scn 0 0 100 100 re f /P2 scn 0 0 5 5 re f",
            Res("/P1 6 0 R /P2 8 0 R"),
            Patterns(
                (6, Cell + "/Resources << >>", "0 0 1 1 re f"),
                (8, Cell + "/Resources << /Pattern << /P1 6 0 R >> >>", "/Pattern cs /P1 scn 0 0 3 3 re f")),
            objects: 12);
        using PdfDocument doc = TestPdf.Open(file);
        string reference = TestPdf.Record(doc).ToText();
        var results = new string[16];
        Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => results[i] = TestPdf.Record(doc).ToText());
        Assert.All(results, r => Assert.Equal(reference, r));
    }
}
