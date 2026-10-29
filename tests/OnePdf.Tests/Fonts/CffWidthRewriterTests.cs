using OnePdf.FontPrograms;
using OnePdf.FontWriting;

namespace OnePdf.Tests.Fonts;

/// <summary>Rewriting the advance widths of CFF programs: widths change, nothing else does, and hostile input only ever raises the font exception.</summary>
public class CffWidthRewriterTests
{
    private static float Advance(CffFont f, int gid)
    {
        Assert.True(f.TryGetAdvance(gid, out float w));
        return w;
    }

    private static void AssertSameGlyphs(CffFont before, CffFont after)
    {
        Assert.Equal(before.GlyphCount, after.GlyphCount);
        for (int g = 0; g < before.GlyphCount; g++)
        {
            Assert.Equal(SyntheticCff.Describe(before, g), SyntheticCff.Describe(after, g));
        }
    }

    [Fact]
    public void TheFixtureHasTheFractionalWidthsItIsBuiltWith()
    {
        var f = new CffFont(SyntheticCff.Standard());
        Assert.Equal(751.42, Advance(f, 1), 2);
        Assert.Equal(234.4962, Advance(f, 2), 3);
        Assert.Equal(331, Advance(f, 3), 3);
        Assert.Equal(500.5, Advance(f, 4), 3);
        Assert.Equal(700.5, Advance(f, 5), 3);
    }

    [Fact]
    public void AskedForWidthsChangeAndEveryOtherGlyphKeepsItsOwnWhileTheDefaultsBecomeWholeNumbers()
    {
        byte[] cff = SyntheticCff.Standard();
        var before = new CffFont(cff);
        byte[] rewritten = CffWidthRewriter.Rewrite(cff, new Dictionary<int, double> { [1] = 751, [3] = 300, [5] = 700 });
        var after = new CffFont(rewritten);

        AssertSameGlyphs(before, after);
        Assert.Equal(751, Advance(after, 1), 4);
        Assert.Equal(300, Advance(after, 3), 4);
        Assert.Equal(700, Advance(after, 5), 4);
        Assert.Equal(Advance(before, 2), Advance(after, 2), 3); // the default width of the old font, still
        Assert.Equal(Advance(before, 4), Advance(after, 4), 3);
        Assert.Equal(Advance(before, 0), Advance(after, 0), 3);

        (double dw, double nw) = SyntheticCff.PrivateWidths(rewritten);
        Assert.Equal(Math.Round(dw), dw);
        Assert.Equal(Math.Round(nw), nw);
    }

    [Fact]
    public void AWidthEqualToTheNewDefaultIsLeftOutOfTheCharstring()
    {
        byte[] cff = SyntheticCff.Standard();
        byte[] rewritten = CffWidthRewriter.Rewrite(cff, new Dictionary<int, double> { [1] = 234 }); // the new defaultWidthX is 234
        var after = new CffFont(rewritten);
        Assert.Equal(234, Advance(after, 1), 4);
        AssertSameGlyphs(new CffFont(cff), after);
    }

    [Fact]
    public void LocalSubroutinesSurviveTheMoveOfThePrivateDict()
    {
        // Glyph 1 draws through local subr 0 (bias 107: operand -107) after its width and move.
        byte[] g1 = SyntheticCff.Cat(SyntheticCff.Fx(751.42 - 655.66064), SyntheticCff.Num(10), SyntheticCff.Num(20), [21], SyntheticCff.Num(-107), [10], [14]);
        byte[] subr = SyntheticCff.Cat(SyntheticCff.Num(100), SyntheticCff.Num(0), [5], SyntheticCff.Num(0), SyntheticCff.Num(100), [5], [11]);
        byte[] cff = SyntheticCff.Standard([subr], g1);
        var before = new CffFont(cff);
        Assert.True(before.GetOutline(1).Verbs.Length >= 3); // the subroutine draws two lines after the move
        byte[] rewritten = CffWidthRewriter.Rewrite(cff, new Dictionary<int, double> { [1] = 600 });
        var after = new CffFont(rewritten);
        AssertSameGlyphs(before, after);
        Assert.Equal(600, Advance(after, 1), 4);
    }

    [Fact]
    public void AWidthReachedThroughASubroutineIsRefused()
    {
        byte[] subr = SyntheticCff.Cat(SyntheticCff.Num(50), [11]);
        byte[] g1 = SyntheticCff.Cat(SyntheticCff.Num(-107), [10], SyntheticCff.Num(10), SyntheticCff.Num(20), [21], [14]);
        byte[] cff = SyntheticCff.Standard([subr], g1);
        Assert.Throws<FontProgramException>(() => CffWidthRewriter.Rewrite(cff, new Dictionary<int, double> { [1] = 600 }));
    }

    [Fact]
    public void AGlyphOutsideTheProgramIsRefused()
    {
        Assert.Throws<FontProgramException>(() => CffWidthRewriter.Rewrite(SyntheticCff.Standard(), new Dictionary<int, double> { [99] = 600 }));
    }

    [Fact]
    public void ACidKeyedProgramIsRewrittenThroughItsFontDictsToo()
    {
        byte[] source = SyntheticCff.Standard();
        byte[] cid = CffSubsetter.Subset(source, [1, 2, 3, 4], "Sub");
        var before = new CffFont(cid);
        Assert.True(before.IsCidKeyed);
        byte[] rewritten = CffWidthRewriter.Rewrite(cid, new Dictionary<int, double> { [1] = 650.5, [2] = 99 });
        var after = new CffFont(rewritten);
        Assert.True(after.IsCidKeyed);
        AssertSameGlyphs(before, after);
        Assert.Equal(650.5, Advance(after, 1), 3);
        Assert.Equal(99, Advance(after, 2), 4);
        Assert.Equal(Advance(before, 3), Advance(after, 3), 3);
        Assert.Equal(Advance(before, 4), Advance(after, 4), 3);
    }

    [Fact]
    public void TruncatedAndDamagedProgramsOnlyEverRaiseTheFontException()
    {
        byte[] cff = SyntheticCff.Standard();
        var widths = new Dictionary<int, double> { [1] = 700, [3] = 10 };
        for (int len = 0; len < cff.Length; len += 3)
        {
            try
            {
                _ = CffWidthRewriter.Rewrite(cff.AsMemory(0, len), widths);
            }
            catch (FontProgramException)
            {
            }
        }

        var rng = new Random(20261029);
        for (int i = 0; i < 400; i++)
        {
            byte[] copy = (byte[])cff.Clone();
            for (int k = 0; k < 1 + (i % 4); k++)
            {
                copy[rng.Next(copy.Length)] = (byte)rng.Next(256);
            }

            try
            {
                byte[] result = CffWidthRewriter.Rewrite(copy, widths);
                Assert.True(result.Length > 0);
            }
            catch (FontProgramException)
            {
            }
        }
    }
}
