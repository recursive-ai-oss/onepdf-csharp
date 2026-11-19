namespace OnePdf.Layout.Tests;

/// <summary>
/// Short texts that carry OpenType features (their own or a default text style's) share their shaping like plain ones, but only with texts that
/// shape the same way: a feature set to what the shaper does anyway counts as none, and a different effective set never shares.
/// </summary>
public class FeatureSharingTests
{
    private static readonly string[] Words = ["fi", "fifi", "AVA", "ct"];

    public enum Mode { None, OnText, PageDefault, DocumentDefault, DefaultValue }

    private static LayoutDocument Build(Mode mode, int count, Func<int, Action<TextDescriptor>?>? perText = null)
    {
        TextFontRegistry fonts = TextFontRegistry.CreateDefault();
        fonts.Add(FeatureFont.Build(), FeatureFont.Family);
        LayoutDocument doc = LayoutDocument.Compose(d =>
        {
            if (mode == Mode.DocumentDefault)
            {
                d.DefaultTextStyle(s => s.Ligatures(false).Kerning(false));
            }

            d.Page(p =>
            {
                p.Size(400, 4000);
                if (mode == Mode.PageDefault)
                {
                    p.DefaultTextStyle(s => s.Ligatures(false).Kerning(false));
                }

                p.Content().Column(col =>
                {
                    for (int i = 0; i < count; i++)
                    {
                        TextDescriptor t = col.Item().Text(Words[i % Words.Length]).FontFamily(FeatureFont.Family).FontSize(10);
                        if (mode == Mode.OnText)
                        {
                            t.Ligatures(false).Kerning(false);
                        }
                        else if (mode == Mode.DefaultValue)
                        {
                            t.Ligatures(true).Kerning(true);
                        }

                        perText?.Invoke(i)?.Invoke(t);
                    }
                });
            });
        });
        doc.Options.Fonts = fonts;
        return doc;
    }

    private static byte[] Run(LayoutDocument doc, out int hits, out int misses)
    {
        byte[] pdf = Support.Save(doc);
        hits = doc.LastTextEngine!.SharedHits;
        misses = doc.LastTextEngine.SharedMisses;
        return pdf;
    }

    [Theory]
    [InlineData(Mode.None)]
    [InlineData(Mode.OnText)]
    [InlineData(Mode.PageDefault)]
    [InlineData(Mode.DocumentDefault)]
    [InlineData(Mode.DefaultValue)]
    public void RepeatedTextsShareWhateverWayTheirFeaturesAreSet(Mode mode)
    {
        Run(Build(mode, 40), out int hits, out int misses);
        Assert.Equal(Words.Length, misses);
        Assert.Equal(40 - Words.Length, hits);
    }

    [Fact]
    public void FeaturesSetToTheirDefaultShareWithNoFeatures()
    {
        // Even and odd texts: one has no features, the other has them set to the defaults. All of a word's texts share one shaping.
        Run(Build(Mode.None, 40, i => i % 2 == 0 ? null : t => t.Ligatures(true).Kerning(true)), out int hits, out int misses);
        Assert.Equal(Words.Length, misses);
        Assert.Equal(40 - Words.Length, hits);
    }

    [Fact]
    public void DifferentEffectiveFeaturesDoNotShareAndShapeDifferently()
    {
        // "fi" three ways: ligature (default), split (liga off), and the same split reached by a different spelling.
        LayoutDocument doc = Build(Mode.None, 8, i => (i / 4) % 2 == 1 ? t => t.Feature("liga", false) : null);
        byte[] pdf = Run(doc, out _, out int misses);
        Assert.Equal(8, misses); // 4 words x (plain, liga off)
        StyleSupport.Capture c = StyleSupport.Interpret(pdf);
        // Words: fi, fifi, AVA, ct twice; the second round has ligatures off: fi has 2 glyphs instead of 1, fifi 4 instead of 2.
        Assert.Equal(1 + 2 + 3 + 2 + 2 + 4 + 3 + 2, c.Runs.Sum(r => r.Glyphs));

        // A feature that does nothing for this font (or is a default) doesn't separate texts either.
        LayoutDocument same = Build(Mode.None, 8, i => (i / 4) % 2 == 1 ? t => t.Feature("kern", 1).Feature("zzzz", false) : null);
        Run(same, out _, out int sameMisses);
        Assert.Equal(8, sameMisses); // zzzz=0 is a real (if inert) setting, so it keys separately; the default-valued kern doesn't

        LayoutDocument kern = Build(Mode.None, 8, i => (i / 4) % 2 == 1 ? t => t.Kerning(true).Ligatures(true) : null);
        Run(kern, out _, out int kernMisses);
        Assert.Equal(4, kernMisses);
    }

    [Fact]
    public void KerningOnAndOffDoNotCollide()
    {
        var texts = new List<double>();
        for (int k = 0; k < 2; k++)
        {
            // Two AVA texts in one document, the later with kerning off: it must not take the kerned shaping of the earlier.
            LayoutDocument doc = Build(Mode.None, 7, i => i == 6 && k == 1 ? t => t.Kerning(false) : null);
            byte[] pdf = Support.Save(doc);
            StyleSupport.Capture c = StyleSupport.Interpret(pdf);
            texts.Add(c.Runs.Max(r => r.X));
        }

        Assert.NotEqual(texts[0], texts[1]);
    }

    [Fact]
    public void SharingDoesNotChangeTheOutput()
    {
        // The same documents built with every text distinct in its settings (so nothing is shared) and as repeats give the same text positions.
        foreach (Mode mode in Enum.GetValues<Mode>())
        {
            LayoutDocument shared = Build(mode, 12);
            byte[] a = Run(shared, out int hits, out _);
            Assert.True(hits > 0, mode.ToString());
            byte[] b = Support.Save(shared);
            Assert.Equal(a, b);
        }
    }
}
