using OnePdf.Text;

namespace OnePdf.Tests.Extraction;

public class BidiControlOptionTests
{
    // Visual order on the page, as a layout engine leaves it: a Hebrew word between two controls, and a word of controls alone, in a left-to-right line.
    private static List<PdfTextGlyph> Page()
    {
        string line = "abc \u202B" + Syn.Rev("\u05E9\u05DC\u05D5\u05DD") + "\u202C \u200F \u2066x\u2069 \u0645\u06CC\u200C\u062E";
        return Syn.Line(line, 50, 700);
    }

    private static PdfTextOptions Opt(bool remove) => new() { RemoveBidiControls = remove };

    [Fact]
    public void DefaultKeepsTheControls()
    {
        PdfPageText p = Syn.Analyze(Page(), Opt(false));
        Assert.Contains("\u202B", p.Text, StringComparison.Ordinal);
        Assert.Contains("\u200F", p.Text, StringComparison.Ordinal);
        Assert.Contains("\u2066", p.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionRemovesThemFromEveryTextAndNeverTouchesJoiners()
    {
        PdfPageText p = Syn.Analyze(Page(), Opt(true));
        PdfPageText kept = Syn.Analyze(Page(), Opt(false));
        string[] controls = ["\u202A", "\u202B", "\u202C", "\u202D", "\u202E", "\u2066", "\u2067", "\u2068", "\u2069", "\u200E", "\u200F", "\u061C"];
        foreach (string text in new[] { p.Text, p.GetText(PdfTextMode.ContentOrder), p.GetText(PdfTextMode.Layout), PdfExtractionJson.ToJson([p], new PdfExtractionJsonOptions { IncludeGlyphs = false }) })
        {
            foreach (string c in controls)
            {
                Assert.DoesNotContain(c, text, StringComparison.Ordinal);
            }

            Assert.Contains("\u200C", text, StringComparison.Ordinal);
        }

        PdfTextLine line = p.Blocks[0].Lines[0];
        Assert.All(line.Words, w => Assert.DoesNotContain("\u202B", w.Text, StringComparison.Ordinal));
        Assert.Equal(line.Text, string.Join(' ', line.Words.Select(w => w.Text)));
        // The same words as with the controls kept, less the controls (a word of controls alone is gone).
        string controlChars = "\u202A\u202B\u202C\u202D\u202E\u2066\u2067\u2068\u2069\u200E\u200F\u061C";
        string[] expected = [.. kept.Blocks[0].Lines[0].Words.Select(w => string.Concat(w.Text.Where(c => !controlChars.Contains(c)))).Where(t => t.Length > 0)];
        Assert.Equal(expected, line.Words.Select(w => w.Text));
        Assert.Contains("\u05E9\u05DC\u05D5\u05DD", line.Text, StringComparison.Ordinal);
        Assert.Contains("\u200C", line.Text, StringComparison.Ordinal);

        // The glyphs are the same either way, and so is everything else when the text has no such character.
        Assert.Equal(kept.Glyphs.Select(g => g.Text), p.Glyphs.Select(g => g.Text));
        Assert.Equal(Syn.Analyze(Syn.Line("plain words here", 50, 700), Opt(false)).Text, Syn.Analyze(Syn.Line("plain words here", 50, 700), Opt(true)).Text);
    }
}
