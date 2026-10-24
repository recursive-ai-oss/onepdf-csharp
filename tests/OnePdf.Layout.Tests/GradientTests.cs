namespace OnePdf.Layout.Tests;

/// <summary>Gradient backgrounds: the PDF shading they become, read back by the reader and interpreter, and how they combine with the rest of a box.</summary>
public class GradientTests
{
    private static readonly LayoutColor Red = new(1, 0, 0);
    private static readonly LayoutColor Blue = new(0, 0, 1);

    private sealed record Seen(string What, PdfShading? Shading = null, PdfGraphicsState? State = null, PdfPath? Path = null);

    private sealed class Capture : PdfDevice
    {
        public List<Seen> Events { get; } = [];

        public IEnumerable<PdfShading> Shadings => Events.Where(e => e.Shading is not null).Select(e => e.Shading!);

        public override void PushClip(in PdfPath path, bool evenOdd, in PdfGraphicsState state) => Events.Add(new Seen("clip", Path: path.ToOwned()));

        public override void PopClip() => Events.Add(new Seen("pop"));

        public override void StrokePath(in PdfPath path, in PdfGraphicsState state) => Events.Add(new Seen("stroke", State: state));

        public override void FillPath(in PdfPath path, bool evenOdd, in PdfGraphicsState state) => Events.Add(new Seen("fill", State: state));

        public override void PaintShading(PdfShading shading, in PdfGraphicsState state) => Events.Add(new Seen("shading", shading, state));
    }

    private static byte[] Doc(Element content, PageSize? size = null)
    {
        var col = new Column { Items = { content } };
        return Support.Save(Support.Single(col, size));
    }

    private static Capture Interpret(byte[] pdf, int page = 0)
    {
        using PdfDocument d = PdfDocument.Open(pdf);
        var cap = new Capture();
        d.Pages[page].Interpret(cap);
        return cap;
    }

    private static LayoutGradientStop[] TwoStops => [new(0, Red), new(1, Blue)];

    private static Box Sized(LayoutGradient g, double w = 200, double h = 100) => new() { Width = w, Height = h, BackgroundGradient = g };

    // The box sits at x 20..220, y (from the top) 20..120 on a 300x400 page, so in PDF space its centre is (120, 330).
    private static void AssertCoords(PdfShading s, double x0, double y0, double x1, double y1)
    {
        Assert.Equal(2, s.ShadingType);
        Assert.Equal(x0, s.Coords[0], 4);
        Assert.Equal(y0, s.Coords[1], 4);
        Assert.Equal(x1, s.Coords[2], 4);
        Assert.Equal(y1, s.Coords[3], 4);
    }

    [Fact]
    public void LeftToRightRunsAcrossTheBoxAtMidHeight()
    {
        PdfShading s = Interpret(Doc(Sized(LayoutGradient.Linear(90, TwoStops)))).Shadings.Single();
        AssertCoords(s, 20, 330, 220, 330);
        Assert.True(s.ExtendStart && s.ExtendEnd);
    }

    [Fact]
    public void BottomToTopAndTopToBottom()
    {
        PdfShading up = Interpret(Doc(Sized(LayoutGradient.Linear(0, TwoStops)))).Shadings.Single();
        AssertCoords(up, 120, 280, 120, 380);
        PdfShading down = Interpret(Doc(Sized(LayoutGradient.Vertical(TwoStops)))).Shadings.Single();
        AssertCoords(down, 120, 380, 120, 280);
        PdfShading left = Interpret(Doc(Sized(LayoutGradient.Linear(270, TwoStops)))).Shadings.Single();
        AssertCoords(left, 220, 330, 20, 330);
    }

    [Fact]
    public void ADiagonalReachesTheCornersExactly()
    {
        // At 45 degrees the line is |w sin| + |h cos| long, so its ends are on lines through the corners perpendicular to it.
        PdfShading s = Interpret(Doc(Sized(LayoutGradient.Linear(45, TwoStops)))).Shadings.Single();
        double half = (200 + 100) * Math.Sqrt(0.5) / 2;
        double d = half * Math.Sqrt(0.5);
        AssertCoords(s, 120 - d, 330 - d, 120 + d, 330 + d);
        // Bottom-left corner (20, 280) projects onto the start, top-right (220, 380) onto the end.
        double Project(double x, double y) => (((x - (120 - d)) * Math.Sqrt(0.5)) + ((y - (330 - d)) * Math.Sqrt(0.5))) / (2 * half);
        Assert.Equal(0, Project(20, 280), 6);
        Assert.Equal(1, Project(220, 380), 6);
    }

    [Theory]
    [InlineData(RadialExtent.FarthestCorner, 111.8034)]
    [InlineData(RadialExtent.ClosestSide, 50)]
    [InlineData(RadialExtent.FarthestSide, 100)]
    [InlineData(RadialExtent.ClosestCorner, 111.8034)]
    public void RadialRadiusFollowsTheExtent(RadialExtent extent, double radius)
    {
        PdfShading s = Interpret(Doc(Sized(LayoutGradient.Circle(TwoStops, extent: extent)))).Shadings.Single();
        Assert.Equal(3, s.ShadingType);
        Assert.Equal([120d, 330, 0, 120, 330, radius], s.Coords.Select(v => Math.Round(v, 4)).ToArray());
    }

    [Fact]
    public void RadialCentreIsAFractionOfTheBox()
    {
        PdfShading s = Interpret(Doc(Sized(LayoutGradient.Circle(TwoStops, 0, 0)))).Shadings.Single();
        Assert.Equal(20, s.Coords[0], 4);
        Assert.Equal(380, s.Coords[1], 4);
        Assert.Equal(Math.Sqrt((200 * 200) + (100 * 100)), s.Coords[5], 4);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    public void StopCountsAndPositionsSurviveTheRoundTrip(int count)
    {
        var stops = new LayoutGradientStop[count];
        for (int i = 0; i < count; i++)
        {
            double p = (double)i / (count - 1);
            stops[i] = new LayoutGradientStop(p, new LayoutColor(p, 1 - p, i % 2));
        }

        PdfShading s = Interpret(Doc(Sized(LayoutGradient.Horizontal(stops)))).Shadings.Single();
        Assert.Equal(count == 2 ? 2 : 3, s.Dictionary.GetDictionary("Function")!.GetInt("FunctionType"));
        for (int i = 0; i < count; i++)
        {
            var c = new double[3];
            s.Evaluate([(double)i / (count - 1)], c);
            Assert.Equal(stops[i].Color.C0, c[0], 5);
            Assert.Equal(stops[i].Color.C1, c[1], 5);
            Assert.Equal(stops[i].Color.C2, c[2], 5);
        }
    }

    [Fact]
    public void UnevenStopsAndGrayAndCmykUseTheirOwnSpaces()
    {
        LayoutGradient gray = LayoutGradient.Horizontal(new LayoutGradientStop(0, PdfGradientColor.Gray(0)), new LayoutGradientStop(0.2, PdfGradientColor.Gray(0.5)), new LayoutGradientStop(1, PdfGradientColor.Gray(1)));
        PdfShading g = Interpret(Doc(Sized(gray))).Shadings.Single();
        Assert.Equal("DeviceGray", g.Dictionary.GetName("ColorSpace"));
        Assert.Equal(0.2, g.Dictionary.GetDictionary("Function")!.GetArray("Bounds")![0] is PdfReal r ? r.Value : double.NaN, 6);
        var c = new double[1];
        g.Evaluate([0.2], c);
        Assert.Equal(0.5, c[0], 6);

        LayoutGradient cmyk = LayoutGradient.Linear(90, new LayoutGradientStop(0, PdfGradientColor.Cmyk(0, 0, 0, 0)), new LayoutGradientStop(1, PdfGradientColor.Cmyk(1, 0, 0, 0.5)));
        PdfShading k = Interpret(Doc(Sized(cmyk))).Shadings.Single();
        Assert.Equal("DeviceCMYK", k.Dictionary.GetName("ColorSpace"));
        Assert.Equal(4, k.ColorSpace.ComponentCount);
    }

    [Fact]
    public void RoundedCornersClipTheShadingAndTheBorderPaintsOverIt()
    {
        var box = Sized(LayoutGradient.Horizontal(TwoStops));
        box.CornerRadius = 12;
        box.SetBorders(new Border(2, LayoutColor.Black));
        Capture cap = Interpret(Doc(box));
        int clip = cap.Events.FindIndex(e => e.What == "clip");
        int shade = cap.Events.FindIndex(e => e.What == "shading");
        Assert.True(clip >= 0 && shade > clip);
        Assert.Equal("pop", cap.Events[shade + 1].What);
        Assert.Contains((byte)PdfPathVerb.Curve, cap.Events[clip].Path!.Value.VerbBytes.ToArray());
        Assert.Contains(cap.Events.Skip(shade + 2), e => e.What == "stroke");
    }

    [Fact]
    public void OpacityFadesTheGradientButNotTheContent()
    {
        var box = Sized(LayoutGradient.Horizontal(TwoStops));
        box.Opacity = 0.4;
        box.Child = new Box { Background = Red, Width = 20, Height = 20 };
        Capture cap = Interpret(Doc(box));
        Assert.Equal(0.4, cap.Events.Single(e => e.What == "shading").State!.Value.FillAlpha, 6);
        Assert.Equal(1, cap.Events.Last(e => e.What == "fill").State!.Value.FillAlpha, 6);
    }

    [Fact]
    public void GradientTurnsWithARotatedBoxAndScalesWithAScaledOne()
    {
        var inner = Sized(LayoutGradient.Horizontal(TwoStops), 100, 40);
        Capture rot = Interpret(Doc(new Rotate { Degrees = 90, Child = inner }));
        PdfShading s = rot.Shadings.Single();
        PdfMatrix ctm = rot.Events.Single(e => e.What == "shading").State!.Value.Ctm;
        Assert.NotEqual(PdfMatrix.Identity, ctm);
        Assert.True(Math.Abs(ctm.A) < 1e-9 && Math.Abs(ctm.B) > 0.99, "the frame is turned a quarter");
        // In the box's own frame the gradient still runs along its width.
        Assert.Equal(s.Coords[1], s.Coords[3], 4);
        Assert.Equal(100, Math.Abs(s.Coords[2] - s.Coords[0]), 4);

        Capture scaled = Interpret(Doc(new Scale { X = 2, Y = 0.5, Child = inner }));
        Assert.Equal(2, scaled.Events.Single(e => e.What == "shading").State!.Value.Ctm.A, 6);
        Assert.Single(scaled.Shadings);
    }

    [Fact]
    public void ABoxSplitOverPagesPaintsTheWholeGradientInEachPiece()
    {
        var lines = new Column();
        for (int i = 0; i < 60; i++)
        {
            lines.Items.Add(new Text { Content = "Line " + i, Size = 12 });
        }

        var box = new Box { BackgroundGradient = LayoutGradient.Vertical(TwoStops), Padding = Insets.All(4), Child = lines };
        byte[] pdf = Doc(box);
        using PdfDocument d = PdfDocument.Open(pdf);
        Assert.True(d.Pages.Count >= 2);
        var spans = new List<double>();
        for (int p = 0; p < d.Pages.Count; p++)
        {
            PdfShading s = Interpret(pdf, p).Shadings.Single();
            spans.Add(Math.Abs(s.Coords[3] - s.Coords[1]));
            // Each piece starts at the first colour and ends at the last, whatever its height.
            var c = new double[3];
            s.Evaluate([0], c);
            Assert.Equal(1, c[0], 6);
            s.Evaluate([1], c);
            Assert.Equal(1, c[2], 6);
        }

        Assert.True(spans[0] > spans[^1], "the last piece is shorter than the first");
        Assert.All(spans, v => Assert.True(v <= 360 + 1e-6));
    }

    [Fact]
    public void GradientsWorkInLayersTablesAndPageBackgrounds()
    {
        LayoutGradient g = LayoutGradient.Linear(135, new LayoutGradientStop(0, Red), new LayoutGradientStop(0.5, LayoutColor.White), new LayoutGradientStop(1, Blue));
        LayoutDocument layout = LayoutDocument.Compose(doc => doc.Page(page =>
        {
            page.Size(300, 400);
            page.Background().Background(g).ExpandHeight().Text("");
            page.Content().Column(col =>
            {
                col.Item().Layers(l =>
                {
                    l.Layer().Background(g).ExpandHeight().Text("");
                    l.PrimaryLayer().Padding(4).Text("Primary layer");
                });
                col.Item().Table(t =>
                {
                    t.Columns(c => { c.Relative(); c.Relative(); });
                    t.Row(r =>
                    {
                        r.Background(g);
                        r.Cell().Text("a");
                        r.Cell().CellBackground(g).Text("b");
                    });
                });
            });
        }));
        byte[] pdf = Support.Save(layout);
        Capture cap = Interpret(pdf);
        Assert.Equal(4, cap.Shadings.Count());
        using PdfDocument back = PdfDocument.Open(pdf);
        Assert.Contains("Primary layer", string.Join(" ", back.ExtractText().Select(t => t.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public void TaggedOutputKeepsGradientsOutOfTheStructure()
    {
        var col = new Column { Items = { Sized(LayoutGradient.Horizontal(TwoStops)), new Text { Content = "After", Size = 12 } } };
        using PdfDocument d = PdfDocument.Open(Support.Save(Support.Single(col)));
        Assert.Empty(TagSupport.Untagged(d));
    }

    [Fact]
    public void FacadeAndElementsBuildTheSameBoxAndColourAndGradientDoNotMix()
    {
        LayoutGradient g = LayoutGradient.Horizontal(TwoStops);
        Element? root = null;
        new Slot(e => root = e).Rounded(8).Background(g).Padding(6).Text("a");
        var box = Assert.IsType<Box>(root);
        Assert.Same(g, box.BackgroundGradient);
        Assert.Null(box.Background);
        Assert.Equal(8, box.CornerRadius);

        // A second background cannot join the same box, so it wraps in another one that paints behind.
        new Slot(e => root = e).Background(Red).Background(g).Text("a");
        var outer = Assert.IsType<Box>(root);
        Assert.NotNull(outer.Background);
        Assert.Same(g, Assert.IsType<Box>(outer.Child).BackgroundGradient);
    }

    [Fact]
    public void BadGradientsFailWhereTheyAreWritten()
    {
        Assert.Throws<ArgumentException>(() => LayoutGradient.Horizontal(new LayoutGradientStop(0, Red)));
        Assert.Throws<ArgumentException>(() => LayoutGradient.Horizontal(new LayoutGradientStop(0.8, Red), new LayoutGradientStop(0.2, Blue)));
        Assert.Throws<ArgumentException>(() => LayoutGradient.Horizontal(new LayoutGradientStop(0, Red), new LayoutGradientStop(1, PdfGradientColor.Gray(1))));
        Assert.Throws<ArgumentException>(() => LayoutGradient.Linear(double.NaN, Red, Blue));
        Assert.Throws<ArgumentNullException>(() => new Slot(_ => { }).Background((LayoutGradient)null!));
    }

    [Fact]
    public void OutputIsTheSameEveryTime()
    {
        byte[] Make() => Doc(Sized(LayoutGradient.Circle([new LayoutGradientStop(0, Red), new LayoutGradientStop(0.3, LayoutColor.White), new LayoutGradientStop(1, Blue)])));
        Assert.Equal(Make(), Make());
    }

    private static LayoutDocument WithGradients() => PdfUATests.Sample(more: body =>
    {
        body.Items.Add(new Box
        {
            Height = 40, CornerRadius = 8, Opacity = 0.8, BackgroundGradient = LayoutGradient.Horizontal(new LayoutGradientStop(0, Red), new LayoutGradientStop(0.5, LayoutColor.White), new LayoutGradientStop(1, Blue)),
            BorderTop = new Border(1, LayoutColor.Black), Child = new Text { Content = "Over a gradient", Size = 10 },
        });
        body.Items.Add(new Box
        {
            Height = 40, BackgroundGradient = LayoutGradient.Circle([new LayoutGradientStop(0, PdfGradientColor.Gray(1)), new LayoutGradientStop(1, PdfGradientColor.Gray(0.6))]),
            Child = new Text { Content = "Radial in gray", Size = 10 },
        });
    });

    private static byte[] SaveWith(PdfSaveOptions options)
    {
        using var ms = new MemoryStream();
        WithGradients().Save(ms, options);
        return ms.ToArray();
    }

    [Fact]
    public void PdfA2bWithGradientsIsCompliant()
    {
        byte[] pdf = SaveWith(new PdfSaveOptions { DocumentId = new byte[16], PdfA = new PdfAOptions { Part = PdfAPart.Part2, Conformance = PdfAConformance.B } });
        List<string> failures = TagSupport.Vera(pdf, "2b");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void PdfUA1WithGradientsIsCompliant()
    {
        byte[] pdf = SaveWith(new PdfSaveOptions { DocumentId = new byte[16], PdfUA = new PdfUAOptions() });
        List<string> failures = TagSupport.Vera(pdf, "ua1");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void PdfA2aAndUaTogetherWithGradientsIsCompliant()
    {
        byte[] pdf = SaveWith(new PdfSaveOptions
        {
            DocumentId = new byte[16],
            PdfA = new PdfAOptions { Part = PdfAPart.Part2, Conformance = PdfAConformance.A },
            PdfUA = new PdfUAOptions(),
        });
        Assert.True(TagSupport.Vera(pdf, "2a").Count == 0);
        Assert.True(TagSupport.Vera(pdf, "ua1").Count == 0);
    }
}
