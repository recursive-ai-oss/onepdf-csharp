namespace OnePdf.Tests.Creation;

/// <summary>Gradients written by the content builder as axial and radial shadings, and read back by the reader and interpreter.</summary>
public class GradientTests
{
    private sealed class Capture : PdfDevice
    {
        public List<PdfShading> Shadings { get; } = [];

        public List<string> Events { get; } = [];

        public List<double> Alphas { get; } = [];

        public override void PushClip(in PdfPath path, bool evenOdd, in PdfGraphicsState state) => Events.Add("clip");

        public override void PopClip() => Events.Add("pop");

        public override void PaintShading(PdfShading shading, in PdfGraphicsState state)
        {
            Shadings.Add(shading);
            Alphas.Add(state.FillAlpha);
            Events.Add("shading");
        }
    }

    private static PdfGradientStop S(double p, PdfGradientColor c) => new(p, c);

    private static Capture Roundtrip(Action<PdfPageContentBuilder> draw, out byte[] pdf)
    {
        using PdfDocument doc = PdfDocument.CreateEmpty();
        PdfPage page = doc.InsertBlankPage(0, 200, 200);
        using (PdfPageContentBuilder b = page.BeginContent())
        {
            draw(b);
            b.Commit();
        }

        pdf = CreationSupport.Save(doc);
        using PdfDocument back = PdfDocument.Open(pdf);
        var cap = new Capture();
        back.Pages[0].Interpret(cap, null);
        return cap;
    }

    private static double[] At(PdfShading s, double t)
    {
        var c = new double[s.ColorComponentCount];
        s.Evaluate([t], c);
        return c;
    }

    [Fact]
    public void TwoStopAxialIsOneExponentialFunction()
    {
        PdfGradient g = PdfGradient.Axial(10, 20, 110, 20, [S(0, PdfGradientColor.Rgb(1, 0, 0)), S(1, PdfGradientColor.Rgb(0, 0, 1))]);
        Capture cap = Roundtrip(b => b.FillRectangle(0, 0, 200, 100, g), out _);
        PdfShading s = Assert.Single(cap.Shadings);
        Assert.Equal(2, s.ShadingType);
        Assert.Equal([10d, 20, 110, 20], s.Coords);
        Assert.True(s.ExtendStart && s.ExtendEnd);
        Assert.Equal(2, s.Dictionary.GetDictionary("Function")!.GetInt("FunctionType"));
        Assert.Equal([1d, 0, 0], At(s, 0));
        Assert.Equal([0.5, 0, 0.5], At(s, 0.5), new ToleranceComparer());
        Assert.Equal([0d, 0, 1], At(s, 1));
        Assert.Equal(["clip", "shading", "pop"], cap.Events);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(12)]
    public void SeveralStopsAreOneStitchingFunction(int count)
    {
        var stops = new PdfGradientStop[count];
        for (int i = 0; i < count; i++)
        {
            stops[i] = S((double)i / (count - 1), PdfGradientColor.Gray(i % 2 == 0 ? 0 : 1));
        }

        PdfGradient g = PdfGradient.Axial(0, 0, 100, 0, stops);
        Capture cap = Roundtrip(b => b.FillRectangle(0, 0, 100, 100, g), out _);
        PdfShading s = Assert.Single(cap.Shadings);
        PdfDictionary f = s.Dictionary.GetDictionary("Function")!;
        Assert.Equal(3, f.GetInt("FunctionType"));
        Assert.Equal(count - 1, f.GetArray("Functions")!.Count);
        Assert.Equal(count - 2, f.GetArray("Bounds")!.Count);
        Assert.Equal(2 * (count - 1), f.GetArray("Encode")!.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(i % 2 == 0 ? 0 : 1, At(s, (double)i / (count - 1))[0], 6);
        }

        Assert.Equal(0.5, At(s, 0.5 / (count - 1))[0], 6);
    }

    [Fact]
    public void StopsNotCoveringTheRangeHoldTheEndColoursAndEqualPositionsMakeAHardEdge()
    {
        PdfGradient g = PdfGradient.Axial(0, 0, 100, 0,
        [
            S(0.25, PdfGradientColor.Gray(0.2)),
            S(0.5, PdfGradientColor.Gray(0.2)),
            S(0.5, PdfGradientColor.Gray(0.8)),
            S(0.75, PdfGradientColor.Gray(0.8)),
        ]);
        Capture cap = Roundtrip(b => b.FillRectangle(0, 0, 100, 100, g), out _);
        PdfShading s = Assert.Single(cap.Shadings);
        Assert.Equal(0.2, At(s, 0)[0], 6);
        Assert.Equal(0.2, At(s, 0.49)[0], 6);
        Assert.Equal(0.8, At(s, 0.51)[0], 6);
        Assert.Equal(0.8, At(s, 1)[0], 6);
    }


    [Fact]
    public void GrayRgbAndCmykUseTheirDeviceSpaces()
    {
        foreach ((PdfGradientColor a, PdfGradientColor c, string space, int n) in new[]
        {
            (PdfGradientColor.Gray(0), PdfGradientColor.Gray(1), "DeviceGray", 1),
            (PdfGradientColor.Rgb(0, 0, 0), PdfGradientColor.Rgb(1, 1, 1), "DeviceRGB", 3),
            (PdfGradientColor.Cmyk(0, 0, 0, 0), PdfGradientColor.Cmyk(1, 0.5, 0, 0.2), "DeviceCMYK", 4),
        })
        {
            PdfGradient g = PdfGradient.Axial(0, 0, 10, 0, [S(0, a), S(1, c)]);
            Capture cap = Roundtrip(b => b.FillRectangle(0, 0, 10, 10, g), out _);
            PdfShading s = Assert.Single(cap.Shadings);
            Assert.Equal(space, s.Dictionary.GetName("ColorSpace"));
            Assert.Equal(n, s.Dictionary.GetDictionary("Function")!.GetArray("C0")!.Count);
        }
    }

    [Fact]
    public void RadialKeepsCirclesAndExtend()
    {
        PdfGradient g = PdfGradient.Radial(50, 60, 5, 55, 60, 40, [S(0, PdfGradientColor.Gray(1)), S(0.5, PdfGradientColor.Gray(0.5)), S(1, PdfGradientColor.Gray(0))], extendStart: false);
        Capture cap = Roundtrip(b => b.FillRectangle(0, 0, 100, 100, g), out _);
        PdfShading s = Assert.Single(cap.Shadings);
        Assert.Equal(3, s.ShadingType);
        Assert.Equal([50d, 60, 5, 55, 60, 40], s.Coords);
        Assert.False(s.ExtendStart);
        Assert.True(s.ExtendEnd);
        Assert.Equal(0.5, At(s, 0.5)[0], 6);
    }

    [Fact]
    public void OpacityAndTransformsApplyToTheShading()
    {
        PdfGradient g = PdfGradient.Axial(0, 0, 10, 0, [S(0, PdfGradientColor.Gray(0)), S(1, PdfGradientColor.Gray(1))]);
        Capture cap = Roundtrip(b =>
        {
            b.Save();
            b.SetFillOpacity(0.4);
            b.Rotate(30);
            b.FillRectangle(0, 0, 50, 50, g);
            b.Restore();
        }, out _);
        Assert.Equal(0.4, Assert.Single(cap.Alphas), 6);
    }

    [Fact]
    public void SameGradientTwiceGivesTwoResourcesAndOutputIsDeterministic()
    {
        PdfGradient g = PdfGradient.Axial(0, 0, 10, 0, [S(0, PdfGradientColor.Gray(0)), S(1, PdfGradientColor.Gray(1))]);
        Capture cap = Roundtrip(b => { b.FillRectangle(0, 0, 10, 10, g); b.FillRectangle(20, 0, 10, 10, g); }, out byte[] one);
        Assert.Equal(2, cap.Shadings.Count);
        Roundtrip(b => { b.FillRectangle(0, 0, 10, 10, g); b.FillRectangle(20, 0, 10, 10, g); }, out byte[] two);
        Assert.Equal(one, two);
    }

    [Fact]
    public void BadInputIsRefused()
    {
        PdfGradientStop a = S(0, PdfGradientColor.Gray(0));
        PdfGradientStop b = S(1, PdfGradientColor.Gray(1));
        Assert.Throws<ArgumentException>(() => PdfGradient.Axial(0, 0, 1, 0, [a]));
        Assert.Throws<ArgumentException>(() => PdfGradient.Axial(0, 0, 1, 0, [b, a]));
        Assert.Throws<ArgumentException>(() => PdfGradient.Axial(0, 0, 1, 0, [a, S(1.5, PdfGradientColor.Gray(1))]));
        Assert.Throws<ArgumentException>(() => PdfGradient.Axial(0, 0, 1, 0, [a, S(1, PdfGradientColor.Rgb(1, 1, 1))]));
        Assert.Throws<ArgumentException>(() => PdfGradient.Axial(1, 1, 1, 1, [a, b]));
        Assert.Throws<ArgumentException>(() => PdfGradient.Axial(double.NaN, 0, 1, 0, [a, b]));
        Assert.Throws<ArgumentException>(() => PdfGradient.Radial(0, 0, 0, 1, 1, 0, [a, b]));
        Assert.Throws<ArgumentException>(() => PdfGradient.Radial(0, 0, -1, 1, 1, 2, [a, b]));
    }

    private sealed class ToleranceComparer : IEqualityComparer<double[]>
    {
        public bool Equals(double[]? x, double[]? y) => x!.Length == y!.Length && x.Zip(y).All(p => Math.Abs(p.First - p.Second) < 1e-6);

        public int GetHashCode(double[] obj) => obj.Length;
    }
}
