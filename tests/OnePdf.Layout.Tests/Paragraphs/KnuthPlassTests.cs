using System.Text;

namespace OnePdf.Layout.Tests;

/// <summary>
/// Knuth-Plass line breaking. Courier is monospaced (600 units, so 6 pt per character at 10 pt): a space stretches by 3 pt and shrinks by
/// 2 pt, and expected breaks can be worked out by hand and checked against an exhaustive search over all break sets.
/// </summary>
public class KnuthPlassTests
{
    private const double CharWidth = 6;

    // "Courier" resolves to the mono face whose advance is 1229/2048 em; this size makes a character exactly 6 pt wide.
    private const double ExactSize = 6.0 * 2048 / 1229;

    private static ShapedParagraph Shape(string text, TextAlignment align = TextAlignment.Justify, LineBreakMode mode = LineBreakMode.KnuthPlass, KnuthPlassOptions? options = null, IHyphenator? hyph = null, double indent = 0, ParagraphDirection dir = ParagraphDirection.Auto, TextStyle? style = null, TextFontRegistry? reg = null)
    {
        var p = new TextParagraph(text, style ?? new TextStyle { Families = ["Courier"], Size = ExactSize })
        {
            Alignment = align,
            LineBreakMode = mode,
            KnuthPlass = options,
            Hyphenator = hyph,
            FirstLineIndent = indent,
            Direction = dir,
        };
        return p.Shape(reg ?? ParaKit.Shared);
    }

    private static List<string> Lines(ParagraphLayout l)
    {
        var r = new List<string>();
        for (int i = 0; i < l.Lines.Count; i++)
        {
            r.Add(ParaKit.LineText(l, i));
        }

        return r;
    }

    [Fact]
    public void ChoosesTheKnownOptimumWhereGreedyStretchesALineTooFar()
    {
        // Lengths 1 5 1 4 3 5 2 2 6 in a line of 14 characters. Greedy ends line 2 after "eee fffff gg", two spaces stretched by 6 pt
        // each (badness 800). The optimum squeezes "eee fffff gg hh" (15 characters) by 1 character over 3 spaces: r = -1, badness 100.
        // Demerits: (10+0)^2 + (10+100)^2 + (10+0)^2 = 12300; the greedy breaks are infeasible at tolerance 200.
        const string text = "a bbbbb c dddd eee fffff gg hh iiiiii";
        ParagraphLayout greedy = Shape(text, mode: LineBreakMode.Greedy).Layout(84);
        ParagraphLayout kp = Shape(text).Layout(84);
        Assert.Equal(["a bbbbb c dddd", "eee fffff gg", "hh iiiiii"], Lines(greedy));
        Assert.Equal(["a bbbbb c dddd", "eee fffff gg hh", "iiiiii"], Lines(kp));
        ParagraphLine squeezed = kp.Lines[1];
        Assert.False(squeezed.Overflows);
        Assert.Equal(-2, squeezed.SpaceExtra, 6);
        Assert.Equal(84, squeezed.Width, 6);
        Assert.Empty(kp.Paragraph.Warnings);
    }

    [Fact]
    public void GreedyIsUnchangedAndStaysTheDefault()
    {
        const string text = "a bbbbb c dddd eee fffff gg hh iiiiii";
        var implicitGreedy = new TextParagraph(text, new TextStyle { Families = ["Courier"], Size = ExactSize }) { Alignment = TextAlignment.Justify };
        Assert.Equal(LineBreakMode.Greedy, implicitGreedy.LineBreakMode);
        Assert.Equal(Lines(implicitGreedy.Shape(ParaKit.Shared).Layout(84)), Lines(Shape(text, mode: LineBreakMode.Greedy).Layout(84)));
    }

    // An independent exhaustive search: every set of breaks between words, the same line model written out again.
    private static (double Demerits, List<int> Cuts)? Oracle(string[] words, int chars, double firstIndentChars, KnuthPlassOptions o)
    {
        int n = words.Length;
        (double Demerits, List<int> Cuts)? best = null;
        for (long mask = 0; mask < (1L << (n - 1)); mask++)
        {
            var cuts = new List<int>();
            for (int k = 0; k < n - 1; k++)
            {
                if ((mask >> k & 1) != 0)
                {
                    cuts.Add(k + 1);
                }
            }

            cuts.Add(n);
            double? d = Score(words, cuts, chars, firstIndentChars, o);
            if (d is { } v && (best is null || v < best.Value.Demerits - 1e-9))
            {
                best = (v, cuts);
            }
        }

        return best;
    }

    private static double? Score(string[] words, List<int> cuts, int chars, double firstIndentChars, KnuthPlassOptions o)
    {
        int start = 0;
        double total = 0;
        int prevFit = 2;
        for (int li = 0; li < cuts.Count; li++)
        {
            int end = cuts[li];
            bool last = end == words.Length;
            int sp = end - start - 1;
            int len = sp;
            for (int k = start; k < end; k++)
            {
                len += words[k].Length;
            }

            double avail = (chars - (li == 0 ? firstIndentChars : 0)) * CharWidth;
            double slack = avail - (len * CharWidth);
            double r;
            if (last)
            {
                if (slack < -1e-9)
                {
                    return null;
                }

                r = 0;
            }
            else if (sp > 0)
            {
                r = slack >= 0 ? slack / (sp * 3) : slack / (sp * 2);
            }
            else
            {
                r = slack <= 1e-9 ? (slack >= -1e-9 ? 0 : -2) : 100;
            }

            if (r < -1)
            {
                return null;
            }

            double bad = last ? 0 : Math.Min(10000, 100 * Math.Abs(r) * Math.Abs(r) * Math.Abs(r));
            if (bad > o.Tolerance)
            {
                return null;
            }

            int fit = r < 0 ? (bad > 12 ? 3 : 2) : (bad > 99 ? 0 : bad > 12 ? 1 : 2);
            total += (o.LinePenalty + bad) * (o.LinePenalty + bad);
            if (Math.Abs(fit - prevFit) > 1)
            {
                total += o.AdjacentFitnessDemerits;
            }

            prevFit = fit;
            start = end;
        }

        return total;
    }

    [Fact]
    public void MatchesAnExhaustiveSearchOnRandomParagraphs()
    {
        var rng = new Random(20260524);
        var options = new KnuthPlassOptions { PreTolerance = -1 };
        int compared = 0;
        int differsFromGreedy = 0;
        for (int t = 0; t < 400; t++)
        {
            int n = rng.Next(4, 12);
            var words = new string[n];
            for (int i = 0; i < n; i++)
            {
                words[i] = new string((char)('a' + rng.Next(26)), rng.Next(1, 8));
            }

            int chars = rng.Next(12, 24);
            double indent = rng.Next(0, 3) == 0 ? 2 : 0;
            (double Demerits, List<int> Cuts)? expect = Oracle(words, chars, indent, options);
            if (expect is null)
            {
                continue;
            }

            string text = string.Join(' ', words);
            ParagraphLayout l = Shape(text, options: options, indent: indent * CharWidth).Layout(chars * CharWidth);
            // The lines as word counts.
            var cuts = new List<int>();
            int wordsSoFar = 0;
            foreach (string line in Lines(l))
            {
                wordsSoFar += line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                cuts.Add(wordsSoFar);
            }

            double? got = Score(words, cuts, chars, indent, options);
            Assert.True(got is not null, $"infeasible result for '{text}' at {chars}");
            Assert.Equal(expect.Value.Demerits, got!.Value, 6);
            compared++;
            ParagraphLayout g = Shape(text, mode: LineBreakMode.Greedy, indent: indent * CharWidth).Layout(chars * CharWidth);
            if (!Lines(g).SequenceEqual(Lines(l)))
            {
                differsFromGreedy++;
            }
        }

        Assert.True(compared > 100, "compared " + compared);
        Assert.True(differsFromGreedy > 10, "differs " + differsFromGreedy);
    }

    [Fact]
    public void HyphenatesWhenThatIsTheOnlyWayToSetTheLineWell()
    {
        // 14 characters. Without the hyphen the first line is "aaaaaaaaa" alone (badness 292, above both tolerances).
        const string text = "aaaaaaaaa bbb\u00ADbbbbbbbbbb ccc";
        ParagraphLayout kp = Shape(text, TextAlignment.Start).Layout(84);
        Assert.True(kp.Lines[0].EndsWithHyphen);
        Assert.Equal("aaaaaaaaa bbb", ParaKit.LineText(kp, 0));
        Assert.Equal("bbbbbbbbbb ccc", ParaKit.LineText(kp, 1));

        // Forbidding hyphens leaves the unhyphenated setting.
        ParagraphLayout no = Shape(text, TextAlignment.Start, options: new KnuthPlassOptions { HyphenPenalty = 10000 }).Layout(84);
        Assert.DoesNotContain(no.Lines, l => l.EndsWithHyphen);
        Assert.Equal(text.Replace("\u00AD", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal), string.Concat(Lines(no)).Replace(" ", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void ConsecutiveHyphensCostMore()
    {
        // Two soft hyphens that both want a line end. With a huge consecutive-hyphen price, no two adjacent lines end in a hyphen.
        const string text = "xx aaaa\u00ADaaaaaaa bbbb\u00ADbbbbbbb cccc\u00ADccccccc dddd\u00ADddddddd eeee\u00ADeeeeeee ffff\u00ADfffffff";
        var o = new KnuthPlassOptions { HyphenPenalty = 0, ConsecutiveHyphenDemerits = 1e6, PreTolerance = -1 };
        ParagraphLayout l = Shape(text, TextAlignment.Start, options: o).Layout(9 * CharWidth);
        for (int i = 0; i + 1 < l.Lines.Count; i++)
        {
            Assert.False(l.Lines[i].EndsWithHyphen && l.Lines[i + 1].EndsWithHyphen, "consecutive hyphens at line " + i);
        }
    }

    [Fact]
    public void UsesHyphenatorPointsAndProseKeepsItsText()
    {
        const string text = "Typesetting paragraphs optimally requires considering every possible line break across the entire paragraph rather than greedily.";
        var hy = new FixedHyphenator();
        ParagraphLayout g = Shape(text, mode: LineBreakMode.Greedy, hyph: hy, style: new TextStyle { Size = 11 }).Layout(130);
        ParagraphLayout k = Shape(text, hyph: hy, style: new TextStyle { Size = 11 }).Layout(130);
        Assert.Equal(Flat(text), Flat(string.Concat(Lines(k))));
        Assert.Equal(Flat(text), Flat(string.Concat(Lines(g))));
        foreach (ParagraphLine line in k.Lines)
        {
            Assert.False(line.Overflows);
            Assert.True(line.Width <= 130 + 1e-6);
        }

        // Justified lines with a space reach the width, except the last.
        for (int i = 0; i + 1 < k.Lines.Count; i++)
        {
            if (ParaKit.LineText(k, i).Contains(' ', StringComparison.Ordinal))
            {
                Assert.Equal(130, k.Lines[i].Width, 6);
            }
        }
    }

    private static string Flat(string s) => s.Replace(" ", string.Empty, StringComparison.Ordinal);

    /// <summary>Allows a break after every 4th letter of words longer than 6.</summary>
    private sealed class FixedHyphenator : IHyphenator
    {
        public IReadOnlyList<int> GetBreaks(string word, string? language)
        {
            var r = new List<int>();
            if (word.Length > 6)
            {
                for (int i = 3; i < word.Length - 2; i += 3)
                {
                    r.Add(i);
                }
            }

            return r;
        }
    }

    [Fact]
    public void WordWiderThanTheLineKeepsTheRestOptimised()
    {
        const string text = "aa bb cc ddddddddddddddddddddd ee ff gg hh ii";
        ParagraphLayout l = Shape(text).Layout(10 * CharWidth);
        Assert.Contains(l.Lines, x => x.Overflows);
        Assert.Contains("ddddddddddddddddddddd", Lines(l));
        Assert.Equal(Flat(text), Flat(string.Concat(Lines(l))));
        Assert.Contains(l.Paragraph.Warnings, w => w.Contains("wider", StringComparison.Ordinal));
        foreach (ParagraphLine line in l.Lines)
        {
            if (!line.Overflows)
            {
                Assert.True(line.Width <= 10 * CharWidth + 1e-6);
            }
        }
    }

    [Fact]
    public void NoFeasibleSettingInToleranceStillBreaksEveryLine()
    {
        // One-letter words in a ragged paragraph with tolerance 0: only exact fits are feasible, so the relaxed last pass must run.
        string text = string.Join(' ', Enumerable.Repeat("w", 30)) + " tail";
        ParagraphLayout l = Shape(text, TextAlignment.Start, options: new KnuthPlassOptions { Tolerance = 0, PreTolerance = -1 }).Layout(10.5 * CharWidth);
        Assert.Equal(Flat(text), Flat(string.Concat(Lines(l))));
        Assert.All(l.Lines, x => Assert.False(x.Overflows));
    }

    [Fact]
    public void TinyWidthGivesOneOverflowingPieceAtATimeInsteadOfFailing()
    {
        ParagraphLayout l = Shape("alpha beta gamma delta").Layout(1);
        Assert.Equal(4, l.Lines.Count);
        Assert.All(l.Lines, x => Assert.True(x.Overflows));
    }

    [Fact]
    public void LoosenessAsksForMoreOrFewerLines()
    {
        string text = ParaKit.Latin;
        int basic = Shape(text, style: new TextStyle { Size = 11 }).Layout(150).Lines.Count;
        int more = Shape(text, options: new KnuthPlassOptions { Looseness = 1 }, style: new TextStyle { Size = 11 }).Layout(150).Lines.Count;
        int fewer = Shape(text, options: new KnuthPlassOptions { Looseness = -1 }, style: new TextStyle { Size = 11 }).Layout(150).Lines.Count;
        Assert.True(more >= basic && more <= basic + 1, $"{more} vs {basic}");
        Assert.True(fewer <= basic && fewer >= basic - 1, $"{fewer} vs {basic}");
        bool moved = false;
        for (double w = 110; w <= 220 && !moved; w += 7)
        {
            int b = Shape(text, style: new TextStyle { Size = 11 }).Layout(w).Lines.Count;
            int m = Shape(text, options: new KnuthPlassOptions { Looseness = 1 }, style: new TextStyle { Size = 11 }).Layout(w).Lines.Count;
            int f = Shape(text, options: new KnuthPlassOptions { Looseness = -1 }, style: new TextStyle { Size = 11 }).Layout(w).Lines.Count;
            Assert.True(m >= b && m <= b + 1 && f <= b && f >= b - 1, $"w={w} b={b} m={m} f={f}");
            moved = m > b || f < b;
        }

        Assert.True(moved);
    }

    [Fact]
    public void HardLineEndsAreSolvedSeparatelyAndNeverJustified()
    {
        ParagraphLayout l = Shape("a bbbbb c dddd eee fffff gg hh iiiiii\nsecond para graph here").Layout(84);
        Assert.Equal("iiiiii", ParaKit.LineText(l, 2));
        Assert.True(l.Lines[2].IsParagraphEnd);
        Assert.Equal(0, l.Lines[2].SpaceExtra);
        Assert.Equal(Flat("second para graph here"), Flat(string.Concat(Lines(l).Skip(3))));
    }

    [Fact]
    public void MixedSizesAndFontsKeepEveryLineWithinTheWidth()
    {
        TextStyle small = new() { Size = 9 };
        TextStyle big = new() { Size = 16, Weight = 700 };
        var p = new TextParagraph([new TextSpan("Mixed sizes within one paragraph make ", small), new TextSpan("uneven", big), new TextSpan(" glyph widths so the best breaks are not obvious from character counts alone, ", small), new TextSpan("sometimes", big), new TextSpan(" quite different.", small)])
        {
            Alignment = TextAlignment.Justify,
            LineBreakMode = LineBreakMode.KnuthPlass,
        };
        ParagraphLayout l = p.Shape(ParaKit.Shared).Layout(140);
        Assert.True(l.Lines.Count >= 4);
        foreach (ParagraphLine line in l.Lines)
        {
            Assert.False(line.Overflows);
            Assert.True(line.Width <= 140 + 1e-6);
        }

        Assert.Equal(Flat(l.Paragraph.Text), Flat(string.Concat(Lines(l))));
    }

    [Fact]
    public void RightToLeftAndMixedScriptParagraphsBreakAndCoverTheirText()
    {
        const string arabic = "\u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645 \u0647\u0630\u0627 \u0646\u0635 \u0639\u0631\u0628\u064A \u0637\u0648\u064A\u0644 \u064A\u062A\u0645 \u0639\u0631\u0636\u0647 \u0645\u0646 \u0627\u0644\u064A\u0645\u064A\u0646 \u0625\u0644\u0649 \u0627\u0644\u064A\u0633\u0627\u0631 \u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645";
        const string mixed = "Hello \u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645 world \u0928\u092E\u0938\u094D\u0924\u0947 and more words to make the line wrap several times over \u0645\u0631\u062D\u0628\u0627 \u0647\u0630\u0627 \u0646\u0635";
        foreach ((string text, ParagraphDirection dir) in new[] { (arabic, ParagraphDirection.Auto), (mixed, ParagraphDirection.LeftToRight), (mixed, ParagraphDirection.RightToLeft) })
        {
            var style = new TextStyle { Size = 12 };
            ParagraphLayout kp = Shape(text, dir: dir, style: style).Layout(170);
            ParagraphLayout gr = Shape(text, mode: LineBreakMode.Greedy, dir: dir, style: style).Layout(170);
            Assert.True(kp.Lines.Count >= 2, "lines " + kp.Lines.Count);
            Assert.Equal(Flat(kp.Paragraph.Text), Flat(string.Concat(Lines(kp))));
            foreach (ParagraphLine line in kp.Lines)
            {
                Assert.False(line.Overflows, $"{dir} overflow line {line.Index} w={line.Width}");
                Assert.True(line.X >= -1e-6 && line.X + line.Width <= 170 + 1e-6, $"{dir} line {line.Index}: x={line.X} w={line.Width} extra={line.SpaceExtra}");
            }

            Assert.True(kp.Lines.Count <= gr.Lines.Count + 2, $"kp {kp.Lines.Count} greedy {gr.Lines.Count}");
        }
    }

    [Fact]
    public void FirstLineIndentIsRespected()
    {
        const string text = "a bbbbb c dddd eee fffff gg hh iiiiii jjjj kk lll";
        ParagraphLayout l = Shape(text, indent: 4 * CharWidth).Layout(84);
        Assert.True(l.Lines[0].Width <= 84 - (4 * CharWidth) + 1e-6);
        Assert.Equal(Flat(text), Flat(string.Concat(Lines(l))));
    }

    [Fact]
    public void ResultIsDeterministic()
    {
        string a = string.Join("|", Lines(Shape(ParaKit.Latin, style: new TextStyle { Size = 11 }).Layout(173.3)));
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(a, string.Join("|", Lines(Shape(ParaKit.Latin, style: new TextStyle { Size = 11 }).Layout(173.3))));
        }
    }

    [Fact]
    public void OptionsAreValidated()
    {
        Assert.Throws<ArgumentException>(() => Shape("x", options: new KnuthPlassOptions { Tolerance = -1 }));
        Assert.Throws<ArgumentException>(() => Shape("x", options: new KnuthPlassOptions { HyphenPenalty = double.NaN }));
        Assert.Throws<ArgumentException>(() => Shape("x", options: new KnuthPlassOptions { HyphenPenalty = -10000 }));
        Assert.Throws<ArgumentException>(() => Shape("x", options: new KnuthPlassOptions { LinePenalty = double.PositiveInfinity }));
    }

    [Fact]
    public void LongParagraphIsQuickAndBounded()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 4000; i++)
        {
            sb.Append(ParaKit.Latin).Append(' ');
        }

        ShapedParagraph shaped = Shape(sb.ToString(), style: new TextStyle { Size = 10 });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ParagraphLayout l = shaped.Layout(300);
        sw.Stop();
        Assert.True(l.Lines.Count > 4000);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), sw.Elapsed.ToString());
    }

    private static LayoutDocument Document(LineBreakMode? perText, LineBreakMode documentDefault)
    {
        var body = new Column { Spacing = 6 };
        for (int i = 0; i < 12; i++)
        {
            body.Items.Add(new Text { Content = $"Paragraph {i}. " + ParaKit.Latin + " " + ParaKit.Latin, Size = 10, Justify = true, LineBreakMode = perText, Hyphenator = new FixedHyphenator() });
        }

        var doc = new LayoutDocument();
        doc.Options.LineBreakMode = documentDefault;
        doc.Pages.Add(new LayoutPage { Size = new PageSize(260, 300), Margin = Insets.All(24), Content = body });
        return doc;
    }

    [Fact]
    public void ParagraphsSplitAcrossPagesKeepTheirTextAndTheDocumentDefaultApplies()
    {
        byte[] kp = Support.Save(Document(null, LineBreakMode.KnuthPlass));
        byte[] perText = Support.Save(Document(LineBreakMode.KnuthPlass, LineBreakMode.Greedy));
        byte[] greedy = Support.Save(Document(null, LineBreakMode.Greedy));
        Assert.Equal(Convert.ToHexString(kp), Convert.ToHexString(perText));
        Assert.NotEqual(Convert.ToHexString(kp), Convert.ToHexString(greedy));
        // A text can opt back out of the document default.
        Assert.Equal(Convert.ToHexString(greedy), Convert.ToHexString(Support.Save(Document(LineBreakMode.Greedy, LineBreakMode.KnuthPlass))));

        using PdfDocument d = PdfDocument.Open(kp);
        Assert.True(d.Pages.Count >= 3);
        string all = string.Join(" ", d.ExtractText().Select(t => Support.Normalize(t.Text))).Replace("\u00AD", string.Empty, StringComparison.Ordinal);
        string flat = Flat(all);
        for (int i = 0; i < 12; i++)
        {
            Assert.Contains(Flat($"Paragraph {i}." + ParaKit.Latin), flat, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TaggedKnuthPlassTextIsPdfACompliant()
    {
        LayoutDocument layout = Document(LineBreakMode.KnuthPlass, LineBreakMode.Greedy);
        using var b = new MemoryStream();
        layout.Save(b, new PdfSaveOptions { PdfA = new PdfAOptions { Part = PdfAPart.Part2, Conformance = PdfAConformance.B } });
        Assert.Empty(TagSupport.Vera(b.ToArray(), "2b"));
    }

    [Fact]
    public void FluentApiSelectsTheBreaker()
    {
        byte[] Make(bool kp) => Support.Save(LayoutDocument.Compose(d =>
        {
            if (kp)
            {
                d.LineBreaking(LineBreakMode.KnuthPlass, new KnuthPlassOptions { Tolerance = 300 });
            }

            d.Page(p => p.Content().Text(t => { t.Justify(); t.Hyphenator(new FixedHyphenator()); t.Span(ParaKit.Latin + " " + ParaKit.Latin); }));
        }));

        byte[] viaDocument = Make(true);
        byte[] viaText = Support.Save(LayoutDocument.Compose(d => d.Page(p => p.Content().Text(t => { t.Justify(); t.Hyphenator(new FixedHyphenator()); t.OptimalLineBreaks(new KnuthPlassOptions { Tolerance = 300 }); t.Span(ParaKit.Latin + " " + ParaKit.Latin); }))));
        Assert.Equal(Convert.ToHexString(viaDocument), Convert.ToHexString(viaText));
        Assert.NotEqual(Convert.ToHexString(viaDocument), Convert.ToHexString(Make(false)));
    }
}
