using OneFont;
using PdfPageText = OnePdf.Text.PdfPageText;
using PdfTextOptions = OnePdf.Text.PdfTextOptions;

namespace OnePdf.Layout.Tests;

/// <summary>Inline objects in paragraphs: alignment against the line, line height, breaking, justification, bidi, tagging, extraction, links.</summary>
public class InlineTests
{
    private const double Size = 11;
    private const double Eps = 0.01;

    // ---- helpers ----

    private static InlineBox Box(double w, double h, InlineAlignment align = InlineAlignment.Baseline, Action<CanvasSurface>? paint = null) =>
        new(w, h) { Alignment = align, Paint = paint ?? (s => { s.Builder.Rectangle(0, 0, s.Width, s.Height); s.Builder.Fill(); }), AltText = "box" };

    private static TextParagraph Para(params object[] parts)
    {
        var spans = new List<TextSpan>();
        foreach (object p in parts)
        {
            spans.Add(p switch
            {
                string s => new TextSpan(s),
                InlineObject o => TextSpan.ForInline(o),
                TextSpan t => t,
                _ => throw new ArgumentException("part"),
            });
        }

        return new TextParagraph(spans) { DefaultStyle = new TextStyle { Size = Size } };
    }

    private static ParagraphLayout Lay(TextParagraph p, double width = 400) => p.Shape(ParaKit.Shared).Layout(width);

    private static (double Asc, double Desc, double FontAsc, double FontDesc) Metrics(double size = Size)
    {
        ResolvedChain chain = ParaKit.Shared.Resolve(new TextStyle { Size = size });
        FontFace face = chain.Entries[0].Face;
        double sc = size / face.UnitsPerEm;
        double a = face.Metrics.Ascender * sc;
        double d = Math.Abs(face.Metrics.Descender) * sc;
        double gap = Math.Max(0, face.Metrics.LineGap) * sc;
        return (a + (gap / 2), d + (gap / 2), a, d);
    }

    /// <summary>Boxes filled by the page, in output space (y down): left, top, right, bottom, in drawing order.</summary>
    private sealed class FillRecorder : PdfDevice
    {
        public List<(double L, double T, double R, double B)> Fills { get; } = [];

        public override void FillPath(in PdfPath path, bool evenOdd, in PdfGraphicsState state)
        {
            PdfBounds b = state.Ctm.TransformBounds(path.ControlBounds());
            Fills.Add((b.Left, Math.Min(b.Top, b.Bottom), b.Right, Math.Max(b.Top, b.Bottom)));
        }
    }

    private static List<(double L, double T, double R, double B)> Fills(byte[] pdf)
    {
        using PdfDocument doc = PdfDocument.Open(pdf);
        var rec = new FillRecorder();
        doc.Pages[0].Interpret(rec);
        return rec.Fills;
    }

    // The page ParaKit.Page makes is 800 tall and the paragraph's top edge is at y = 780 (20 from the top), left edge at x = 20.
    private const double PageTop = 20;
    private const double PageLeft = 20;

    // ---- vertical alignment ----

    [Theory]
    [InlineData(InlineAlignment.Baseline)]
    [InlineData(InlineAlignment.Top)]
    [InlineData(InlineAlignment.Middle)]
    [InlineData(InlineAlignment.Bottom)]
    [InlineData(InlineAlignment.TextTop)]
    [InlineData(InlineAlignment.TextBottom)]
    public void ObjectsSitWhereTheirAlignmentSaysAndTheLineGrowsToHoldThem(InlineAlignment align)
    {
        const double h = 30;
        (double asc, double desc, double fa, double fd) = Metrics();
        ParagraphLayout layout = Lay(Para("Aa ", Box(10, h, align), " bb"));
        ParagraphLine line = Assert.Single(layout.Lines);

        // The expected line box, worked out from the rules: objects against the baseline or the text first, then top, then bottom.
        double bottom;
        double expAsc = asc;
        double expDesc = desc;
        switch (align)
        {
            case InlineAlignment.Baseline:
                bottom = 0;
                break;
            case InlineAlignment.Middle:
                bottom = ((fa - fd) / 2) - (h / 2);
                break;
            case InlineAlignment.TextTop:
                bottom = fa - h;
                break;
            case InlineAlignment.TextBottom:
                bottom = -fd;
                break;
            case InlineAlignment.Top:
                bottom = double.NaN;
                expDesc = h - asc;
                break;
            default:
                bottom = double.NaN;
                expAsc = h - desc;
                break;
        }

        if (!double.IsNaN(bottom))
        {
            expAsc = Math.Max(asc, bottom + h);
            expDesc = Math.Max(desc, -bottom);
        }

        Assert.Equal(expAsc, line.Baseline, 6);
        Assert.Equal(expAsc + expDesc, line.Height, 6);

        // Drawn: the box's top and bottom against the line.
        var fills = Fills(ParaKit.Page([layout]));
        var box = Assert.Single(fills);
        double top = box.T - PageTop;
        double bot = box.B - PageTop;
        double boxBottomAboveBaseline = line.Baseline - bot;
        double expectedBottom = align switch
        {
            InlineAlignment.Top => line.Baseline - h,
            InlineAlignment.Bottom => -(line.Height - line.Baseline),
            _ => bottom,
        };
        Assert.Equal(expectedBottom, boxBottomAboveBaseline, 2);
        Assert.Equal(h, bot - top, 2);
        Assert.InRange(top, -Eps, line.Height + Eps);
        if (align is InlineAlignment.Top)
        {
            Assert.Equal(0, top, 2);
        }

        if (align is InlineAlignment.Bottom)
        {
            Assert.Equal(line.Height, bot, 2);
        }
    }

    [Fact]
    public void ASmallObjectDoesNotChangeTheLineHeight()
    {
        ParagraphLine plain = Assert.Single(Lay(Para("Aa  bb")).Lines);
        ParagraphLine withBox = Assert.Single(Lay(Para("Aa ", Box(5, 5), " bb")).Lines);
        Assert.Equal(plain.Height, withBox.Height, 6);
        Assert.Equal(plain.Baseline, withBox.Baseline, 6);
    }

    [Fact]
    public void BaselineOffsetRaisesAndLowersTheObject()
    {
        (double asc, double desc, _, _) = Metrics();
        ParagraphLine up = Assert.Single(Lay(Para("Aa ", new InlineBox(10, 30) { BaselineOffset = 5 }, " bb")).Lines);
        Assert.Equal(35, up.Baseline, 6);
        Assert.Equal(35 + desc, up.Height, 6);
        ParagraphLine down = Assert.Single(Lay(Para("Aa ", new InlineBox(10, 30) { BaselineOffset = -8 }, " bb")).Lines);
        Assert.Equal(Math.Max(asc, 22), down.Baseline, 6);
        Assert.Equal(Math.Max(desc, 8), down.Height - down.Baseline, 6);
    }

    [Fact]
    public void InnerBaselineLiftsTheObjectByThatMuch()
    {
        ParagraphLine line = Assert.Single(Lay(Para("Aa ", new InlineBox(10, 30) { InnerBaseline = 12 }, " bb")).Lines);
        Assert.Equal(18, line.Baseline, 6);
    }

    [Fact]
    public void AFixedLineHeightLetsTheObjectStickOut()
    {
        ParagraphLine plain = Assert.Single(Lay(Para("Aa  bb")).Lines);
        ParagraphLayout layout = Lay(Para("Aa ", new InlineBox(10, 40) { GrowsLine = false, Paint = s => { s.Builder.Rectangle(0, 0, s.Width, s.Height); s.Builder.Fill(); } }, " bb"));
        ParagraphLine line = Assert.Single(layout.Lines);
        Assert.Equal(plain.Height, line.Height, 6);
        var box = Assert.Single(Fills(ParaKit.Page([layout])));
        Assert.True(box.T - PageTop < -Eps, "the object extends above the line box");
    }

    [Fact]
    public void ALineHoldingOnlyAnObjectStillHasTheTextLineHeight()
    {
        ParagraphLine plain = Assert.Single(Lay(Para("A")).Lines);
        ParagraphLine line = Assert.Single(Lay(Para(Box(4, 4))).Lines);
        Assert.Equal(plain.Height, line.Height, 6);
    }

    // ---- line breaking ----

    private static IEnumerable<string> Lines(ParagraphLayout l) => Enumerable.Range(0, l.Lines.Count).Select(i => ParaKit.LineText(l, i));

    [Theory]
    [InlineData(LineBreakMode.Greedy)]
    [InlineData(LineBreakMode.KnuthPlass)]
    public void AnObjectIsOneUnbreakableUnitWithBreaksOnBothSides(LineBreakMode mode)
    {
        // "wwwwwwww" is wider than half the line, so the line must end before or after the object; the object stays whole either way.
        TextParagraph p = Para("wwwwwwwwww", Box(30, 10), "wwwwwwwwww", Box(30, 10), "wwwwwwwwww");
        var para = new TextParagraph(p.Spans) { DefaultStyle = p.DefaultStyle, LineBreakMode = mode };
        ParagraphLayout layout = Lay(para, 90);
        Assert.True(layout.Lines.Count > 1);
        int objects = 0;
        foreach (string line in Lines(layout))
        {
            objects += line.Count(c => c == '\uFFFC');
        }

        Assert.Equal(2, objects);
        foreach (ParagraphLine line in layout.Lines)
        {
            Assert.False(line.Overflows);
            Assert.True(line.Width <= 90 + Eps);
        }

        // A break fell next to an object, with no space there.
        Assert.Contains(Lines(layout), s => s.StartsWith('\uFFFC') || s.EndsWith('\uFFFC'));
    }

    [Theory]
    [InlineData(LineBreakMode.Greedy)]
    [InlineData(LineBreakMode.KnuthPlass)]
    public void AnObjectThatKeepsWithItsNeighboursNeverStartsOrEndsALine(LineBreakMode mode)
    {
        var para = new TextParagraph([new TextSpan("wwwwwwwwww"), TextSpan.ForInline(new InlineBox(30, 10) { KeepWithNeighbours = true }), new TextSpan("wwwwwwwwww wwwwwwwwww")])
        {
            DefaultStyle = new TextStyle { Size = Size },
            LineBreakMode = mode,
        };
        ParagraphLayout layout = Lay(para, 120);
        foreach (string line in Lines(layout))
        {
            Assert.False(line.StartsWith('\uFFFC') || line.EndsWith('\uFFFC'), line);
        }

        // The glued unit "w...w[obj]w...w" is wider than 120, so it sits whole on an overflowing line.
        ParagraphLine unit = Assert.Single(layout.Lines, l => ParaKit.LineText(layout, l.Index).Contains('\uFFFC'));
        Assert.True(unit.Overflows);
    }

    [Fact]
    public void ALineWithOnlyAnObjectWiderThanTheLineOverflowsAndWarns()
    {
        ParagraphLayout layout = Lay(Para("ab ", Box(200, 10), " cd"), 100);
        ParagraphLine wide = Assert.Single(layout.Lines, l => l.Overflows);
        Assert.Equal(200, wide.Width, 6);
        Assert.Contains(layout.Paragraph.Warnings, w => w.Contains("wider than the line", StringComparison.Ordinal));
        Assert.Equal(3, layout.Lines.Count);
        Assert.Equal(200, layout.Paragraph.MinContentWidth, 6);
    }

    [Fact]
    public void ContentWidthsCountTheObject()
    {
        ShapedParagraph shaped = Para("ab ", Box(50, 10), " cd").Shape(ParaKit.Shared);
        Assert.True(shaped.MinContentWidth >= 50);
        ShapedParagraph plain = Para("ab  cd").Shape(ParaKit.Shared);
        Assert.Equal(plain.MaxContentWidth + 50, shaped.MaxContentWidth, 3);
    }

    // ---- justification ----

    [Fact]
    public void JustificationSpreadsTheSpacesAndLeavesTheObjectAlone()
    {
        var para = new TextParagraph([new TextSpan("one two three four five six "), TextSpan.ForInline(Box(20, 10)), new TextSpan(" seven eight nine ten eleven twelve thirteen fourteen")])
        {
            DefaultStyle = new TextStyle { Size = Size },
            Alignment = TextAlignment.Justify,
        };
        ParagraphLayout layout = Lay(para, 200);
        Assert.True(layout.Lines.Count > 1);
        ParagraphLine withBox = layout.Lines.First(l => ParaKit.LineText(layout, l.Index).Contains('\uFFFC'));
        if (!withBox.IsParagraphEnd)
        {
            Assert.Equal(200, withBox.Width, 3);
        }

        var box = Assert.Single(Fills(ParaKit.Page([layout])));
        Assert.Equal(20, box.R - box.L, 3);
    }

    [Fact]
    public void LetterSpacedJustificationOfALineWithAnObjectKeepsTheObjectWidth()
    {
        var para = new TextParagraph([new TextSpan("abcdef"), TextSpan.ForInline(Box(20, 10)), new TextSpan("ghijkl abcdefghijklmnopqrstuvwxyz")])
        {
            DefaultStyle = new TextStyle { Size = Size },
            Alignment = TextAlignment.Justify,
            JustifyWithLetterSpacing = true,
        };
        ParagraphLayout layout = Lay(para, 120);
        foreach (var f in Fills(ParaKit.Page([layout])))
        {
            Assert.Equal(20, f.R - f.L, 3);
        }
    }

    // ---- bidi ----

    [Fact]
    public void ARightToLeftParagraphPlacesTheObjectBetweenItsNeighboursFromTheRight()
    {
        // Logical order: first word, object, second word. Right to left puts the first word at the right edge.
        const string first = "\u05D0\u05D1\u05D2";
        const string second = "\u05D3\u05D4\u05D5";
        var para = new TextParagraph([new TextSpan(first + " "), TextSpan.ForInline(Box(20, 10)), new TextSpan(" " + second)])
        {
            DefaultStyle = new TextStyle { Size = Size },
            Direction = ParagraphDirection.RightToLeft,
        };
        ParagraphLayout layout = Lay(para, 300);
        byte[] pdf = ParaKit.Page([layout]);
        var box = Assert.Single(Fills(pdf));
        using PdfDocument doc = PdfDocument.Open(pdf);
        PdfPageText text = doc.Pages[0].ExtractText(new PdfTextOptions { RetainGlyphs = true });
        double firstX = text.Glyphs.First(g => g.Text == "\u05D0").Matrix.E;
        double secondX = text.Glyphs.First(g => g.Text == "\u05D3").Matrix.E;
        double boxX = box.L + 0;
        Assert.True(firstX > boxX, "the first word is to the right of the object");
        Assert.True(secondX < boxX, "the second word is to the left of the object");
        Assert.Equal(300 + PageLeft, layout.Lines[0].X + layout.Lines[0].Width + PageLeft, 3);
    }

    [Fact]
    public void AnObjectBetweenRightToLeftWordsInALeftToRightParagraphTakesTheirDirection()
    {
        const string first = "\u05D0\u05D1\u05D2";
        const string second = "\u05D3\u05D4\u05D5";
        var para = new TextParagraph([new TextSpan("a " + first + " "), TextSpan.ForInline(Box(20, 10)), new TextSpan(" " + second + " b")])
        {
            DefaultStyle = new TextStyle { Size = Size },
            Direction = ParagraphDirection.LeftToRight,
        };
        ParagraphLayout layout = Lay(para, 300);
        byte[] pdf = ParaKit.Page([layout]);
        var box = Assert.Single(Fills(pdf));
        using PdfDocument doc = PdfDocument.Open(pdf);
        PdfPageText text = doc.Pages[0].ExtractText(new PdfTextOptions { RetainGlyphs = true });
        double firstX = text.Glyphs.First(g => g.Text == "\u05D0").Matrix.E;
        double secondX = text.Glyphs.First(g => g.Text == "\u05D3").Matrix.E;
        // Inside the right-to-left run the first Hebrew word is on the right and the second on the left, the object between them.
        Assert.True(firstX > box.L);
        Assert.True(secondX < box.L);
        Assert.Contains("a", ParaKit.Flat(text.Text));
    }

    // ---- standalone drawing, links, extraction ----

    [Fact]
    public void TheRendererReportsALinkAreaForAnObject()
    {
        ParagraphLayout layout = Lay(Para("Click ", new InlineBox(12, 14) { Link = "https://example.com/x", Paint = s => { s.Builder.Rectangle(0, 0, s.Width, s.Height); s.Builder.Fill(); } }, " now"));
        using PdfDocument doc = PdfDocument.CreateEmpty();
        PdfPage page = doc.InsertBlankPage(0, 300, 200);
        using PdfPageContentBuilder b = page.BeginContent();
        IReadOnlyList<TextLinkArea> links = new TextRenderer(doc).Draw(b, layout, 10, 150);
        TextLinkArea area = Assert.Single(links);
        Assert.Equal("https://example.com/x", area.Target);
        Assert.Equal(12, area.Width, 6);
        Assert.Equal(14, area.Height, 6);
        double baseline = 150 - layout.Lines[0].Baseline;
        Assert.Equal(baseline, area.Y, 6);
        Assert.True(area.X > 10);
    }

    [Fact]
    public void ExtractionGivesTheAlternateTextOrNothingAndNeverTheReplacementCharacter()
    {
        var para = Para("Price: ", new InlineBox(10, 10) { AltText = "euro sign", Paint = s => { s.Builder.Rectangle(0, 0, 5, 5); s.Builder.Fill(); } }, " 10 and ", new InlineBox(10, 10) { Artifact = true, Paint = s => { s.Builder.Rectangle(0, 0, 5, 5); s.Builder.Fill(); } }, " done");
        string text = ParaKit.Flat(ParaKit.Extract(ParaKit.Page([Lay(para)])));
        Assert.DoesNotContain('\uFFFC', text);
        Assert.Contains("Price:", text);
        Assert.Contains("euro sign", text);
        Assert.Contains("10 and", text);
        Assert.Contains("done", text);
        Assert.Equal(1, text.Split("euro sign").Length - 1);
    }

    [Fact]
    public void ALiteralReplacementCharacterInTextIsDropped()
    {
        ParagraphLayout layout = Lay(Para("a\uFFFCb"));
        Assert.Equal("ab", layout.Paragraph.Text);
    }

    [Fact]
    public void AnInlineElementCannotBeShapedOutsideALayoutText()
    {
        Assert.Throws<ArgumentException>(() => new TextParagraph([TextSpan.ForInline(new InlineElement(new Spacer()))]));
    }

    [Fact]
    public void SizesAreValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InlineBox(-1, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InlineBox(5, double.NaN));
        Assert.Throws<ArgumentException>(() => new TextParagraph([TextSpan.ForInline(new InlineBox(1, 1) { BaselineOffset = double.PositiveInfinity })]));
    }

    [Fact]
    public void LayoutAndOutputAreDeterministic()
    {
        byte[] a = ParaKit.Page([Lay(Para("x ", Box(10, 20, InlineAlignment.Middle), " y z ", Box(8, 8)), 60)]);
        byte[] b = ParaKit.Page([Lay(Para("x ", Box(10, 20, InlineAlignment.Middle), " y z ", Box(8, 8)), 60)]);
        Assert.Equal(a, b);
    }

    // ---- layout elements: Text with inline elements ----

    private static byte[] Doc(Text text, double width = 300, Action<LayoutDocument>? setup = null)
    {
        LayoutDocument doc = Support.Single(text, new PageSize(width, 600));
        doc.Title = "Inline";
        doc.Language = "en";
        setup?.Invoke(doc);
        return Support.Save(doc);
    }

    private static Text TextWith(params object[] parts)
    {
        var t = new Text { Size = 11 };
        foreach (object p in parts)
        {
            t.Spans.Add(p switch
            {
                string s => new TextSpan(s),
                InlineObject o => TextSpan.ForInline(o),
                _ => throw new ArgumentException("part"),
            });
        }

        return t;
    }

    private static Image Picture(string? alt, bool artifact = false) => new() { Jpeg = Support.Jpeg(), Fit = ImageFit.Stretch, AltText = alt, Artifact = artifact };

    [Fact]
    public void AnInlinePictureIsAFigureInsideTheParagraphAndExtractsItsAlternateText()
    {
        Text t = TextWith("See the ", new InlineElement(Picture("company logo")) { Width = 14, Height = 14, AltText = "company logo" }, " above.");
        byte[] pdf = Doc(t);
        using PdfDocument doc = PdfDocument.Open(pdf);
        SNode root = TagSupport.ReadTree(doc);
        SNode p = root.Descendants().First(n => n.Type == "P");
        SNode figure = Assert.Single(p.Kids);
        Assert.Equal("Figure", figure.Type);
        Assert.Equal("company logo", figure.Alt);

        // Content before the picture, the picture, content after: P holds content, then the figure, then content, in that order.
        Assert.Equal("cec", p.Order);
        Assert.Empty(TagSupport.Untagged(doc));
        string text = ParaKit.Flat(string.Concat(doc.ExtractText().Select(x => x.Text)));
        Assert.DoesNotContain('\uFFFC', text);
        Assert.Contains("company logo", text);
        Assert.Contains("See the", text);
        Assert.Contains("above.", text);
    }

    [Fact]
    public void ADecorativeInlineObjectIsAnArtifactAndExtractsNothing()
    {
        Text t = TextWith("Dot ", new InlineElement(Picture(null, artifact: true)) { Width = 8, Height = 8, Artifact = true }, " end");
        byte[] pdf = Doc(t);
        using PdfDocument doc = PdfDocument.Open(pdf);
        SNode root = TagSupport.ReadTree(doc);
        Assert.DoesNotContain(root.Descendants(), n => n.Type == "Figure");
        Assert.Empty(TagSupport.Untagged(doc));
        string text = ParaKit.Flat(string.Concat(doc.ExtractText().Select(x => x.Text)));
        Assert.Equal("Dot end", text);
    }

    [Fact]
    public void ABadgeWithItsOwnTextLinesUpWithTheTextAround()
    {
        var badge = new Box { Background = LayoutColor.FromHex("#dddddd"), Padding = Insets.Symmetric(3, 1), Child = new Text { Content = "NEW", Size = 8 } };
        Text t = TextWith("Status ", new InlineElement(badge), " ok");
        byte[] pdf = Doc(t);
        using PdfDocument doc = PdfDocument.Open(pdf);
        PdfPageText text = doc.Pages[0].ExtractText(new PdfTextOptions { RetainGlyphs = true });
        double baselineOutside = text.Glyphs.First(g => g.Text == "S").Matrix.F;
        double baselineInside = text.Glyphs.First(g => g.Text == "N").Matrix.F;
        Assert.Equal(baselineOutside, baselineInside, 2);
        Assert.Equal("Status NEW ok", ParaKit.Flat(text.Text));
        Assert.Empty(TagSupport.Untagged(doc));
    }

    [Fact]
    public void ABadgeWithAlternateTextExtractsTheAlternateTextOnce()
    {
        var badge = new Box { Padding = Insets.All(2), Child = new Text { Content = "NEW", Size = 8 } };
        Text t = TextWith("Status ", new InlineElement(badge) { AltText = "new item" }, " ok");
        byte[] pdf = Doc(t);
        using PdfDocument doc = PdfDocument.Open(pdf);
        string text = ParaKit.Flat(string.Concat(doc.ExtractText().Select(x => x.Text)));
        Assert.Contains("new item", text);
        Assert.DoesNotContain("NEW", text);
        Assert.Equal("Figure", Assert.Single(TagSupport.ReadTree(doc).Descendants(), n => n.Type == "Figure").Type);
    }

    [Fact]
    public void AnInlineElementCanBeALinkOrAnAnchorLikeAnySpan()
    {
        var icon = new Link { Uri = "https://example.com/doc", Child = new Image { Jpeg = Support.Jpeg(), Fit = ImageFit.Stretch, AltText = "docs" } };
        var target = new Anchor { Id = "here", Child = new Spacer { Width = 6, Height = 6 } };
        var jump = new InlineBox(10, 10) { Link = "#here", AltText = "go", Paint = s => { s.Builder.Rectangle(0, 0, 10, 10); s.Builder.Fill(); } };
        Text t = TextWith("Read ", new InlineElement(icon) { Width = 12, Height = 12 }, " or ", jump, " then ", new InlineElement(target), " here.");
        byte[] pdf = Doc(t);
        using PdfDocument doc = PdfDocument.Open(pdf);
        var annots = doc.Pages[0].Annotations.Where(a => a.Subtype == "Link").ToList();
        Assert.Equal(2, annots.Count);
        Assert.Contains(annots, a => a.Dictionary.GetDictionary("A")?.GetName("S") == "URI");
        Assert.Contains(annots, a => a.Dictionary.GetDictionary("A")?.GetName("S") == "GoTo");
        foreach (PdfAnnotation a in annots)
        {
            PdfRectangle r = a.Rect;
            Assert.True(r.Right - r.Left is > 9 and < 13);
        }

        SNode root = TagSupport.ReadTree(doc);
        Assert.Equal(2, root.Descendants().Count(n => n.Type == "Link"));
        Assert.All(root.Descendants().Where(n => n.Type == "Link"), n => Assert.Single(n.Objects));
    }

    [Fact]
    public void ALongTextWithInlineElementsFlowsOverLinesAndPages()
    {
        var t = new Text { Size = 11 };
        for (int i = 0; i < 80; i++)
        {
            t.Spans.Add(new TextSpan($"word{i} and more words "));
            t.Spans.Add(TextSpan.ForInline(new InlineElement(new Box { Padding = Insets.All(1), Child = new Text { Content = $"#{i}", Size = 8 } })));
            t.Spans.Add(new TextSpan(" "));
        }

        LayoutDocument doc = Support.Single(t, new PageSize(300, 600));
        doc.Title = "Flow";
        doc.Language = "en";
        byte[] pdf = Support.Save(doc);
        using PdfDocument back = PdfDocument.Open(pdf);
        Assert.True(back.Pages.Count > 1);
        Assert.Empty(TagSupport.Untagged(back));
        string text = string.Concat(back.ExtractText().Select(x => x.Text));
        for (int i = 0; i < 80; i += 9)
        {
            Assert.Contains($"#{i}", text);
        }

        Assert.Equal(pdf, Support.Save(doc));
    }

    [Fact]
    public void AnInlineElementWithAPageBreakIsRefusedWithThePath()
    {
        Text t = TextWith("a ", new InlineElement(new Column { Items = { new Spacer { Height = 10 }, new PageBreak(), new Spacer { Height = 10 } } }), " b");
        PdfLayoutException ex = Assert.Throws<PdfLayoutException>(() => Doc(t));
        Assert.Contains("Inline", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMeasuredElementIsAsWideAsItsContentUpToMaxWidth()
    {
        var content = new Text { Content = "a rather long piece of text inside", Size = 9 };
        Text natural = TextWith("x ", new InlineElement(content), " y");
        Text capped = TextWith("x ", new InlineElement(content) { MaxWidth = 40 }, " y");
        double wNatural = WidthOfFirstObject(natural);
        double wCapped = WidthOfFirstObject(capped);
        Assert.True(wNatural > 60);
        Assert.Equal(40, wCapped, 3);
    }

    private static double WidthOfFirstObject(Text t)
    {
        // The width the object takes in the line is the width between the text before it and after it.
        LayoutDocument doc = Support.Single(t, new PageSize(500, 600));
        List<Placed> pages = Support.Pages(doc, out _);
        Placed text = Support.Walk(pages[0]).First(p => p.Element is Text);
        var fragment = (Text.Fragment)text.Data!;
        return fragment.Block.NaturalWidth - TextWidth("x  y");
    }

    private static double TextWidth(string s)
    {
        ShapedParagraph shaped = new TextParagraph(s, new TextStyle { Size = 11 }).Shape(ParaKit.Shared);
        return shaped.MaxContentWidth;
    }

    [Fact]
    public void StandardFontsAndNestedInlineElementsWork()
    {
        // A badge whose own text holds an inline box, in a paragraph in a standard font: the text renderer is entered again while a line is open.
        Text inner = TextWith("in ", new InlineBox(5, 5) { Paint = s => { s.Builder.Rectangle(0, 0, 5, 5); s.Builder.Fill(); } }, " side");
        inner.FontName = "Helvetica";
        Text outer = TextWith("Before ", new InlineElement(new Box { Padding = Insets.All(2), Child = inner }) { AltText = "nested badge" }, " after ", new InlineBox(6, 6) { AltText = "dot", Paint = s => { s.Builder.Rectangle(0, 0, 6, 6); s.Builder.Fill(); } }, " end");
        outer.FontName = "Helvetica";
        byte[] pdf = Doc(outer);
        using PdfDocument doc = PdfDocument.Open(pdf);
        Assert.Empty(TagSupport.Untagged(doc));
        string text = ParaKit.Flat(string.Concat(doc.ExtractText().Select(x => x.Text)));
        Assert.Contains("Before", text);
        Assert.Contains("nested badge", text);
        Assert.Contains("after", text);
        Assert.Contains("dot", text);
        Assert.DoesNotContain('\uFFFC', text);
    }

    [Fact]
    public void UntaggedDocumentsDrawInlineObjectsWithoutMarks()
    {
        Text t = TextWith("x ", new InlineElement(Picture("logo")) { Width = 10, Height = 10, AltText = "logo" }, " y");
        LayoutDocument layout = Support.Single(t, new PageSize(200, 300));
        layout.Options.Tagged = false;
        byte[] pdf = Support.Save(layout);
        using PdfDocument doc = PdfDocument.Open(pdf);
        Assert.Null(doc.StructureTreeRoot);
        Assert.Contains("logo", ParaKit.Flat(string.Concat(doc.ExtractText().Select(x => x.Text))));
    }

    [Fact]
    public void ATableCellSizesItsColumnFromTheInlineElementsInIt()
    {
        var table = new Table();
        table.Columns.Add(TableColumn.Auto());
        table.Columns.Add(TableColumn.Auto());
        var row = new TableRow();
        row.Cells.Add(new TableCell(TextWith("a", new InlineElement(new Spacer { Width = 80, Height = 5 }) { Width = 80, Height = 5 })));
        row.Cells.Add(new TableCell(new Text { Content = "b" }));
        table.Rows.Add(row);
        byte[] pdf = Support.Save(Support.Single(table, new PageSize(300, 400)));
        using PdfDocument doc = PdfDocument.Open(pdf);
        PdfPageText text = doc.Pages[0].ExtractText(new PdfTextOptions { RetainGlyphs = true });
        double a = text.Glyphs.First(g => g.Text == "a").Matrix.E;
        double b = text.Glyphs.First(g => g.Text == "b").Matrix.E;
        Assert.True(b - a > 80, $"column one is at least as wide as its object: {b - a}");
    }

    // ---- fluent ----

    [Fact]
    public void TheFluentApiPutsPicturesBadgesAndCanvasesInTheLine()
    {
        byte[] pdf = LayoutDocument.Compose(doc =>
        {
            doc.Title("Fluent").Language("en");
            doc.Page(page =>
            {
                page.Size(new PageSize(320, 400));
                page.Margin(20);
                page.Content().Text(t =>
                {
                    t.Span("Status: ");
                    t.InlineImage(Support.Jpeg(), 12, 12).AltText("flag").AlignTextBottom();
                    t.Span(" and ");
                    t.Inline(s => s.Background(LayoutColor.FromHex("#cccccc")).Padding(3, 0).Text("NEW").FontSize(8).Bold()).AlignMiddle().AltText("new");
                    t.Span(" and a box ");
                    t.InlineCanvas(8, 8, c =>
                    {
                        c.Builder.Rectangle(0, 0, 8, 8);
                        c.Builder.Stroke();
                    }).Baseline(1).Artifact().KeepWithNeighbours();
                    t.Span(" end.");
                });
            });
        }).ToBytes(new PdfDocumentWriterOptions { DocumentId = new byte[16], LeaveOpen = true });
        using PdfDocument back = PdfDocument.Open(pdf);
        SNode root = TagSupport.ReadTree(back);
        Assert.Equal(2, root.Descendants().Count(n => n.Type == "Figure"));
        Assert.Empty(TagSupport.Untagged(back));
        string text = ParaKit.Flat(string.Concat(back.ExtractText().Select(x => x.Text)));
        Assert.DoesNotContain('\uFFFC', text);
        Assert.Contains("flag", text);
        Assert.Contains("end.", text);
    }

    [Fact]
    public void InlineObjectsAndPageNumbersCannotShareOneText()
    {
        Assert.Throws<InvalidOperationException>(() => LayoutDocument.Compose(doc =>
        {
            doc.Page(page =>
            {
                page.Content().Text(t =>
                {
                    t.Span("Page ");
                    t.CurrentPageNumber();
                    t.InlineCanvas(4, 4, _ => { });
                });
            });
        }));
    }

    // ---- compliance ----

    private static byte[] CompliantDocument(PdfSaveOptions options)
    {
        LayoutDocument layout = LayoutDocument.Compose(doc =>
        {
            doc.Title("Inline figures").Language("en-US");
            doc.Page(page =>
            {
                page.Size(new PageSize(320, 400));
                page.Margin(24);
                page.Content().Column(col =>
                {
                    col.Spacing(6);
                    col.Item().Heading(1).Text("Inline objects").FontSize(16).Bold();
                    col.Item().Text(t =>
                    {
                        t.Span("A line with a picture ");
                        t.InlineImage(Support.Jpeg(), 12, 12).AltText("a red square").AlignTextBottom();
                        t.Span(", a badge ");
                        t.Inline(s => s.Background(LayoutColor.FromHex("#dddddd")).Padding(3, 0).Text("NEW").FontSize(8)).AltText("new").AlignMiddle();
                        t.Span(", a decoration ");
                        t.InlineCanvas(6, 6, c => { c.Builder.Rectangle(0, 0, 6, 6); c.Builder.Fill(); }).Artifact();
                        t.Span(" and a link ");
                        t.Inline(s => s.Image(Support.Jpeg())).Width(10).Height(10).Hyperlink("https://example.com/").AltText("example link");
                        t.Span(" in the middle of a paragraph that goes on for long enough to wrap onto a third line of text.");
                    });
                });
            });
        });
        using var ms = new MemoryStream();
        layout.Save(ms, options);
        return ms.ToArray();
    }

    [Fact]
    public void InlineFiguresPassUa1()
    {
        List<string> failures = TagSupport.Vera(CompliantDocument(new PdfSaveOptions { DocumentId = new byte[16], PdfUA = new PdfUAOptions() }), "ua1");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Theory]
    [InlineData(PdfAPart.Part2, "2a")]
    [InlineData(PdfAPart.Part3, "3a")]
    public void InlineFiguresPassPdfALevelA(PdfAPart part, string flavour)
    {
        byte[] pdf = CompliantDocument(new PdfSaveOptions { DocumentId = new byte[16], PdfA = new PdfAOptions { Part = part, Conformance = PdfAConformance.A }, PdfUA = new PdfUAOptions() });
        List<string> failures = TagSupport.Vera(pdf, flavour);
        Assert.True(failures.Count == 0, flavour + ":\n" + string.Join("\n", failures));
        failures = TagSupport.Vera(pdf, "ua1");
        Assert.True(failures.Count == 0, "ua1:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void TheCompliantSampleExtractsItsFiguresNotTheReplacementCharacter()
    {
        byte[] pdf = CompliantDocument(new PdfSaveOptions { DocumentId = new byte[16] });
        using PdfDocument doc = PdfDocument.Open(pdf);
        string text = string.Concat(doc.ExtractText().Select(x => x.Text));
        Assert.DoesNotContain('\uFFFC', text);
        Assert.Contains("a red square", text);
        Assert.Contains("example link", text);
    }
}
