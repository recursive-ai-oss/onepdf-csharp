using OnePdf.Content;
using OnePdf.Text;

namespace OnePdf.Tests.Extraction;

public class RetainGlyphsTests
{
    [Theory]
    [InlineData("lo-report", PdfTextMode.ReadingOrder, 1)]
    [InlineData("lo-report", PdfTextMode.ReadingOrder, 4)]
    [InlineData("lo-report-tagged", PdfTextMode.ReadingOrder, 1)]
    [InlineData("lo-report", PdfTextMode.ContentOrder, 1)]
    [InlineData("lo-report", PdfTextMode.Layout, 1)]
    public void WithoutGlyphsTheTextAndWordsAreTheSame(string name, PdfTextMode mode, int degree)
    {
        using PdfDocument doc = PdfDocument.Open(TestData.Generated(name));
        var keep = new PdfTextOptions { Mode = mode, MaxDegreeOfParallelism = degree, RetainGlyphs = true };
        var drop = new PdfTextOptions { Mode = mode, MaxDegreeOfParallelism = degree, RetainGlyphs = false };
        IReadOnlyList<PdfPageText> a = doc.ExtractText(keep);
        IReadOnlyList<PdfPageText> b = doc.ExtractText(drop);
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Text, b[i].Text);
            Assert.Equal(a[i].Words.Select(w => (w.Text, w.Bounds, w.IsVisible)), b[i].Words.Select(w => (w.Text, w.Bounds, w.IsVisible)));
            Assert.Equal(
                a[i].Blocks.SelectMany(x => x.Lines).Select(l => (l.Text, l.Bounds, l.Direction, l.IsVertical, string.Join(',', l.CellStarts))),
                b[i].Blocks.SelectMany(x => x.Lines).Select(l => (l.Text, l.Bounds, l.Direction, l.IsVertical, string.Join(',', l.CellStarts))));
            Assert.NotEmpty(a[i].Glyphs);
            Assert.Empty(b[i].Glyphs);
            Assert.All(b[i].Words, w => Assert.Empty(w.GlyphIndices));
        }
    }

    [Fact]
    public void WordsOfAPageWithoutGlyphsAreMadeOnceAndSharedByConcurrentReaders()
    {
        using PdfDocument doc = PdfDocument.Open(TestData.Generated("lo-report"));
        PdfPageText page = doc.ExtractText(new PdfTextOptions { RetainGlyphs = false })[0];
        PdfTextLine[] lines = page.Blocks.SelectMany(b => b.Lines).ToArray();
        Assert.NotEmpty(lines);
        IReadOnlyList<PdfTextWord>[][] seen = new IReadOnlyList<PdfTextWord>[8][];
        Parallel.For(0, seen.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, t => seen[t] = lines.Select(l => l.Words).ToArray());
        for (int t = 1; t < seen.Length; t++)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                Assert.Same(seen[0][i], seen[t][i]);
            }
        }

        // A word's text is the slice of the line's text it stands for.
        foreach (PdfTextLine l in lines)
        {
            Assert.Equal(l.Text.Replace('\t', ' '), string.Join(' ', l.Words.Select(w => w.Text)));
        }
    }

    [Fact]
    public void AllThreeModesWorkOnADefaultDocumentResult()
    {
        using PdfDocument doc = PdfDocument.Open(TestData.Generated("lo-report"));
        IReadOnlyList<PdfPageText> dflt = doc.ExtractText();
        IReadOnlyList<PdfPageText> kept = doc.ExtractText(new PdfTextOptions { RetainGlyphs = true });
        Assert.Empty(dflt[0].Glyphs);
        foreach (PdfTextMode mode in new[] { PdfTextMode.ReadingOrder, PdfTextMode.ContentOrder, PdfTextMode.Layout })
        {
            for (int i = 0; i < kept.Count; i++)
            {
                Assert.Equal(kept[i].GetText(mode), dflt[i].GetText(mode));
            }
        }

        Assert.Equal(kept[0].Glyphs.Count, dflt[0].ReadGlyphs().Count);
        Assert.NotEmpty(doc.Pages[0].ExtractText().Glyphs);
    }

    [Fact]
    public void AResultAnalyzedFromBareGlyphsCannotRecomputeOtherModes()
    {
        var g = new PdfPageGlyphs(0, 600, 800, []);
        PdfPageText page = PdfTextExtractor.Analyze(g, new PdfTextOptions { RetainGlyphs = false });
        Assert.Equal("", page.Text);
    }

    [Fact]
    public void HeadersAndFootersStillExcludedWithoutGlyphs()
    {
        using PdfDocument doc = PdfDocument.Open(TestData.Generated("lo-report"));
        var keep = new PdfTextOptions { ExcludeHeadersAndFooters = true, RetainGlyphs = true };
        var drop = new PdfTextOptions { ExcludeHeadersAndFooters = true, RetainGlyphs = false };
        Assert.Equal(doc.ExtractText(keep).Select(p => p.Text), doc.ExtractText(drop).Select(p => p.Text));
    }

    [Fact]
    public void ASavedStateCopiedIntoASpareObjectEqualsItsClone()
    {
        // Every field of the graphics state must be copied when the interpreter reuses a state object for q.
        var source = new GState
        {
            Ctm = new PdfMatrix(2, 3, 4, 5, 6, 7), LineWidth = 3, LineCap = 1, LineJoin = 2, MiterLimit = 4, DashPhase = 5, Flatness = 6,
            Intent = "Saturation", FillAlpha = 0.5, StrokeAlpha = 0.25, Blend = "Multiply", SoftMask = 9, Ais = true, FillOverprint = true,
            StrokeOverprint = true, OverprintMode = 1, CharSpace = 1.5, WordSpace = 2.5, HorizScale = 0.9, Leading = 11, RenderMode = 2,
            Rise = 3, FontSize = 12, ClipCount = 7,
        };
        var copy = new GState { LineWidth = 99, Intent = "x", ClipCount = 4 };
        copy.CopyFrom(source);
        GState clone = source.Clone();
        foreach (System.Reflection.FieldInfo f in typeof(GState).GetFields())
        {
            Assert.True(Equals(f.GetValue(clone), f.GetValue(copy)), f.Name);
        }
    }
}
