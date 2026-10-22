using static OnePdf.Tests.Shading.ShadingKit;

namespace OnePdf.Tests.Shading;

public class ShadingEvaluatorTests
{
    private const string Gray01 = "/Function << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> ";

    [Fact]
    public void AxialEvaluatesAlongTheAxisAndHonoursExtend()
    {
        using ShadingKit k = Sh(Rgb2 + "/Coords [0 0 100 0] /Extend [false true] " + RedToBlue);
        PdfShading sh = k.Shading!;
        Assert.Equal(2, sh.ShadingType);
        Assert.Equal("rgb", sh.FinalSpace.Name);
        Assert.True(sh.ExtendEnd);
        Assert.False(sh.ExtendStart);
        Near([1, 0, 0], AtPoint(sh, 0, 0));
        Near([0.5, 0, 0.5], AtPoint(sh, 50, 10));
        Near([0, 0, 1], AtPoint(sh, 100, -30));
        Assert.Null(AtPoint(sh, -1, 0));
        Near([0, 0, 1], AtPoint(sh, 500, 7));
        Assert.Single(k.Dev.Shadings);
    }

    [Fact]
    public void AxialMapsThroughTheDomainAndRejectsADegenerateAxis()
    {
        using (ShadingKit k = Sh(Rgb2 + "/Coords [0 0 100 0] /Domain [2 4] " + RedToBlue))
        {
            Assert.True(k.Shading!.TryGetParameter(50, 0, out double t));
            Assert.Equal(3, t, 12);
            Assert.True(k.Shading.TryGetParameter(0, 0, out t));
            Assert.Equal(2, t, 12);
        }

        using ShadingKit zero = Sh(Rgb2 + "/Coords [5 5 5 5] /Extend [true true] " + RedToBlue);
        Assert.Null(AtPoint(zero.Shading!, 5, 5));
        Assert.Null(AtPoint(zero.Shading!, 50, 50));
    }

    [Fact]
    public void RadialConcentricCircles()
    {
        using ShadingKit k = Sh("/ShadingType 3 /ColorSpace /DeviceGray /Coords [50 50 0 50 50 50] /Extend [false true] " + Gray01);
        PdfShading sh = k.Shading!;
        Near([0.5], AtPoint(sh, 75, 50));
        Near([0], AtPoint(sh, 50, 50));
        Near([1], AtPoint(sh, 120, 50));
        using ShadingKit noExt = Sh("/ShadingType 3 /ColorSpace /DeviceGray /Coords [50 50 0 50 50 50] " + Gray01);
        Assert.Null(AtPoint(noExt.Shading!, 120, 50));
        Near([1], AtPoint(noExt.Shading!, 100, 50));
    }

    [Fact]
    public void RadialCirclesThatDoNotContainEachOther()
    {
        using ShadingKit k = Sh("/ShadingType 3 /ColorSpace /DeviceGray /Coords [0 0 10 100 0 20] /Extend [true true] " + Gray01);
        PdfShading sh = k.Shading!;

        // Two roots (s = 0.0556 and s = -0.136): the larger one paints.
        Assert.True(sh.TryGetParameter(-5, 0, out double t));
        Assert.Equal(550.0 / 9900.0, t, 12);

        // Outside the cone that the circles sweep out.
        Assert.False(sh.TryGetParameter(0, 100, out _));
        Assert.Null(AtPoint(sh, 0, 100));
    }

    [Fact]
    public void RadialWhereTheRadiusChangesAsFastAsTheCentreMoves()
    {
        // |c1 - c0| = |r1 - r0|, so the quadratic degenerates to a line.
        using ShadingKit k = Sh("/ShadingType 3 /ColorSpace /DeviceGray /Coords [0 0 0 10 0 10] " + Gray01);
        Assert.True(k.Shading!.TryGetParameter(10, 0, out double t));
        Assert.Equal(0.5, t, 12);
        Assert.False(k.Shading.TryGetParameter(-3, 0, out _));
    }

    [Fact]
    public void RadialNeverPaintsWhereTheExtendedRadiusWouldBeNegative()
    {
        // The cone shrinks to a point at x = 200; beyond it nothing is painted even with Extend.
        using ShadingKit k = Sh("/ShadingType 3 /ColorSpace /DeviceGray /Coords [0 0 20 100 0 10] /Extend [true true] " + Gray01);
        PdfShading sh = k.Shading!;
        Assert.Null(AtPoint(sh, 250, 0));
        Near([1], AtPoint(sh, 190, 0));
    }

    [Fact]
    public void RadialRejectsNegativeRadii()
    {
        using ShadingKit k = Sh("/ShadingType 3 /ColorSpace /DeviceGray /Coords [0 0 -1 10 0 5] " + Gray01);
        Assert.Null(k.Shading);
        Assert.Contains(k.Doc.Diagnostics, d => d.Message.Contains("negative radius"));
    }

    [Fact]
    public void FunctionBasedShadingMapsTheDomainThroughItsMatrix()
    {
        void Fn(PdfBuilder b) => b.Stream(8, "/FunctionType 4 /Domain [0 1 0 1] /Range [0 1 0 1 0 1]", Latin1("{ 0 }"));
        using (ShadingKit k = Sh("/ShadingType 1 /ColorSpace /DeviceRGB /Domain [0 1 0 1] /Matrix [100 0 0 100 10 10] /Function 8 0 R", null, Fn))
        {
            PdfShading sh = k.Shading!;
            Assert.Equal(1, sh.ShadingType);
            Assert.Equal(2, sh.ParameterCount);
            Near([0.5, 0.5, 0], AtPoint(sh, 60, 60));
            Near([1, 0, 0], AtPoint(sh, 110, 10));
            Assert.Null(AtPoint(sh, 5, 5));
            Assert.Null(AtPoint(sh, 111, 60));
            Near([0.25, 0.75, 0], At(sh, 0.25, 0.75));
        }

        // A rotated matrix: (u, v) -> (-100 v, 100 u).
        using ShadingKit r = Sh("/ShadingType 1 /ColorSpace /DeviceRGB /Matrix [0 100 -100 0 0 0] /Function 8 0 R", null, Fn);
        Near([0.25, 0.75, 0], AtPoint(r.Shading!, -75, 25), 1e-12);
    }

    [Fact]
    public void AnArrayOfOneOutputFunctionsMakesOneColourFunction()
    {
        string fns = "/Function [ << /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >> << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> << /FunctionType 2 /Domain [0 1] /C0 [0.2] /C1 [0.2] /N 1 >> ] ";
        using ShadingKit k = Sh(Rgb2 + "/Coords [0 0 100 0] " + fns);
        Near([0.5, 0.5, 0.2], AtPoint(k.Shading!, 50, 0));

        using ShadingKit bad = Sh(Rgb2 + "/Coords [0 0 100 0] /Function [ << /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >> ] ");
        Assert.Null(bad.Shading);
    }

    [Fact]
    public void SeparationShadingsConvertThroughTheTintTransform()
    {
        string cs = "/ColorSpace [/Separation /Spot /DeviceRGB << /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [1 0 0] /N 1 >>] ";
        using ShadingKit k = Sh("/ShadingType 2 " + cs + "/Coords [0 0 100 0] " + Gray01);
        PdfShading sh = k.Shading!;
        Assert.Equal(1, sh.ColorSpace.ComponentCount);
        Assert.Equal(3, sh.ColorComponentCount);
        Near([1, 0, 0], AtPoint(sh, 100, 0));
        Near([1, 0.5, 0.5], AtPoint(sh, 50, 0));
    }

    [Fact]
    public void BackgroundBoxAndAntiAliasAreData()
    {
        using ShadingKit k = Sh(Rgb2 + "/Coords [0 0 100 0] /Background [0.1 0.2 0.3] /BBox [10 20 50 60] /AntiAlias true " + RedToBlue);
        PdfShading sh = k.Shading!;
        Assert.True(sh.HasBackground);
        var bg = new double[3];
        Assert.True(sh.TryGetBackground(bg));
        Near([0.1, 0.2, 0.3], bg);
        Assert.Equal(new PdfBounds(10, 20, 50, 60), sh.BBox);
        Assert.True(sh.AntiAlias);

        using ShadingKit plain = Sh(Rgb2 + "/Coords [0 0 100 0] " + RedToBlue);
        Assert.False(plain.Shading!.HasBackground);
        Assert.False(plain.Shading.TryGetBackground(bg));
        Assert.Null(plain.Shading.BBox);
        Assert.False(plain.Shading.AntiAlias);
    }

    [Fact]
    public void TheShOperatorDeliversTheShadingWithTheCtm()
    {
        using ShadingKit k = Sh(Rgb2 + "/Coords [0 0 1 0] " + RedToBlue, content: "q 2 0 0 2 5 5 cm /S sh Q /S sh");
        Assert.Equal(2, k.Dev.Shadings.Count);
        Assert.Equal(new PdfMatrix(2, 0, 0, -2, 5, 195), k.Dev.States[0].Ctm);
        Assert.Same(k.Dev.Shadings[0], k.Dev.Shadings[1]);
        Assert.DoesNotContain(k.Doc.Diagnostics, d => d.Message.Contains("unevaluated"));
    }

    [Fact]
    public void UnusableShadingsAreSkippedWithADiagnostic()
    {
        string[] bad =
        [
            "/ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 1] " + RedToBlue,
            "/ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 1 0]",
            "/ShadingType 2 /ColorSpace /Pattern /Coords [0 0 1 0] " + RedToBlue,
            "/ShadingType 9 /ColorSpace /DeviceRGB",
            "/ShadingType 4 /ColorSpace /DeviceRGB /Coords [0 0 1 0] " + RedToBlue,
            "/ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 1 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >> ",
        ];
        foreach (string dict in bad)
        {
            using ShadingKit k = Sh(dict);
            Assert.Null(k.Shading);
            Assert.Contains(k.Doc.Diagnostics, d => d.Severity >= PdfSeverity.Warning && d.Message.Contains("hading"));
        }
    }
}
