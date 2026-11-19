namespace OnePdf.Layout.Tests;

/// <summary>OpenType feature switches on text styles: ligatures, kerning, discretionary ligatures, small capitals and figures, through the shaper, with extraction intact.</summary>
public class OpenTypeFeatureTests
{
    private static LayoutDocument Doc(Action<TextDescriptor> text, Action<PageComposer>? page = null)
    {
        LayoutDocument d = LayoutDocument.Compose(doc => doc.Page(p =>
        {
            p.Size(400, 200);
            page?.Invoke(p);
            p.Content().Text(t =>
            {
                t.FontFamily(FeatureFont.Family).FontSize(20);
                text(t);
            });
        }));
        d.Options.Fonts = Fonts();
        return d;
    }

    private static TextFontRegistry Fonts()
    {
        TextFontRegistry fonts = TextFontRegistry.CreateDefault();
        fonts.Add(FeatureFont.Build(), FeatureFont.Family);
        return fonts;
    }

    private static (int Glyphs, double Right, string Text) Measure(byte[] pdf)
    {
        StyleSupport.Capture c = StyleSupport.Interpret(pdf);
        // The right edge of the last glyph's origin tells where the text ends.
        double last = c.Runs.Count == 0 ? 0 : c.Runs.Max(r => r.X);
        return (c.Runs.Sum(r => r.Glyphs), last, string.Concat(c.Runs.Select(r => r.Text)));
    }

    [Fact]
    public void LigaturesAreOnByDefaultAndSwitchOff()
    {
        (int on, _, string onText) = Measure(StyleSupport.Save(Doc(t => t.Span("fi"))));
        (int off, _, string offText) = Measure(StyleSupport.Save(Doc(t => t.Span("fi").Ligatures(false))));
        (int off2, _, _) = Measure(StyleSupport.Save(Doc(t => t.Span("fi").Feature("liga", false))));
        Assert.Equal(1, on);
        Assert.Equal(2, off);
        Assert.Equal(2, off2);
        Assert.Equal("fi", onText);
        Assert.Equal("fi", offText);
    }

    [Fact]
    public void ExtractionIsTheSameWithLigaturesOnOrOff()
    {
        string on = StyleSupport.Text(StyleSupport.Save(Doc(t => t.Span("fifi fit cat"))));
        string off = StyleSupport.Text(StyleSupport.Save(Doc(t => t.Ligatures(false).Span("fifi fit cat"))));
        Assert.Equal("fifi fit cat", on);
        Assert.Equal(on, off);
    }

    [Fact]
    public void FeaturesApplyToTheSpanThatSetsThem()
    {
        (int glyphs, _, string text) = Measure(StyleSupport.Save(Doc(t =>
        {
            t.Span("fi ");
            t.Span("fi ").Ligatures(false);
            t.Span("fi");
        })));
        // Two of the three "fi" are ligatures, one is split, plus two spaces.
        Assert.Equal(2 + 3 + 1, glyphs);
        Assert.Equal("fi fi fi", text.Replace("  ", " ", StringComparison.Ordinal));
    }

    [Fact]
    public void KerningSwitchesOffAndChangesAdvances()
    {
        (_, double kerned, _) = Measure(StyleSupport.Save(Doc(t => t.Span("AVA"))));
        (_, double plain, _) = Measure(StyleSupport.Save(Doc(t => t.Span("AVA").Kerning(false))));
        (_, double plain2, _) = Measure(StyleSupport.Save(Doc(t => t.Span("AVA").Feature("kern", false))));
        // 150 units per em at 20 pt is 3 pt.
        Assert.Equal(3, plain - kerned, 3);
        Assert.Equal(plain, plain2, 6);
    }

    [Fact]
    public void DiscretionaryLigaturesAreOffUntilSwitchedOn()
    {
        (int off, _, _) = Measure(StyleSupport.Save(Doc(t => t.Span("ct"))));
        (int on, _, string text) = Measure(StyleSupport.Save(Doc(t => t.Span("ct").Feature("dlig"))));
        Assert.Equal(2, off);
        Assert.Equal(1, on);
        Assert.Equal("ct", text);
        Assert.Equal("act ct", StyleSupport.Text(StyleSupport.Save(Doc(t => t.Feature("dlig").Span("act ct")))));
    }

    [Fact]
    public void SmallCapitalsAndTabularFiguresChangeTheGlyphs()
    {
        // The small capital 'a' is 480 units wide, the plain one 560; the tabular '1' is 640, the plain one 500.
        (_, double a, _) = Measure(StyleSupport.Save(Doc(t => t.Span("aaa"))));
        (_, double sc, _) = Measure(StyleSupport.Save(Doc(t => t.Span("aaa").Feature("smcp"))));
        Assert.Equal(2 * 80 / 1000.0 * 20, a - sc, 3);
        (_, double one, _) = Measure(StyleSupport.Save(Doc(t => t.Span("111"))));
        (_, double tab, _) = Measure(StyleSupport.Save(Doc(t => t.Span("111").Feature("tnum", true))));
        Assert.Equal(2 * 140 / 1000.0 * 20, tab - one, 3);
        Assert.Equal("111", StyleSupport.Text(StyleSupport.Save(Doc(t => t.Span("111").Feature("tnum")))));
    }

    [Fact]
    public void UnknownTagsAreIgnoredAndBadTagsRejected()
    {
        (int glyphs, _, _) = Measure(StyleSupport.Save(Doc(t => t.Span("fi").Feature("zzzz"))));
        Assert.Equal(1, glyphs);
        Assert.Throws<ArgumentException>(() => new TextFeature("lig"));
        Assert.Throws<ArgumentException>(() => TextFeature.On("liga\u00e9"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextFeature("liga", -1));
    }

    [Fact]
    public void TextStyleFeaturesWorkOnTheParagraphEngine()
    {
        TextFontRegistry fonts = Fonts();
        TextStyle off = new() { Families = [FeatureFont.Family], Size = 20, Features = [TextFeature.Off("liga")] };
        ShapedParagraph withLiga = new TextParagraph("fi", new TextStyle { Families = [FeatureFont.Family], Size = 20 }).Shape(fonts);
        ShapedParagraph without = new TextParagraph("fi", off).Shape(fonts);
        // The ligature glyph is 520 units wide; the two letters are 300 + 250.
        Assert.Equal(520 / 1000.0 * 20, withLiga.MaxContentWidth, 3);
        Assert.Equal(550 / 1000.0 * 20, without.MaxContentWidth, 3);
    }

    [Fact]
    public void ThePageDefaultCanSwitchFeaturesAndASpanOverridesIt()
    {
        (int glyphs, _, _) = Measure(StyleSupport.Save(Doc(
            t => t.Span("fi fi").Ligatures(true),
            p => p.DefaultTextStyle(s => s.Ligatures(false)))));
        Assert.Equal(1 + 1 + 1, glyphs);
        (int plain, _, _) = Measure(StyleSupport.Save(Doc(
            t => t.Span("fi fi"),
            p => p.DefaultTextStyle(s => s.Ligatures(false)))));
        Assert.Equal(2 + 1 + 2, plain);
    }
}
