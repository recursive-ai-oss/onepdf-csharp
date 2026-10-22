using System.Text.Json;
using OnePdf.Text;

namespace OnePdf.Tests.Extraction;

public class LayoutTests
{
    private static readonly string[] Left =
    [
        "the harbor town woke slowly", "fishing boats rocked against", "their moorings while gulls", "argued over the morning catch", "by nine the market stalls", "were open and the smell",
    ];

    private static readonly string[] Right =
    [
        "visitors who arrived early", "could watch the ferry load", "crates of lemons bicycles", "a piano wrapped in blankets", "at noon the bell on the", "clock tower rang twelve times",
    ];

    [Fact]
    public void TwoColumnsReadLeftThenRight()
    {
        // Content order interleaves the columns line by line; reading order must not.
        List<PdfTextGlyph> g = [];
        for (int i = 0; i < Left.Length; i++)
        {
            g.AddRange(Syn.Line(Left[i], 50, 700 - i * 14));
            g.AddRange(Syn.Line(Right[i], 330, 700 - i * 14));
        }

        PdfPageText p = Syn.Analyze(g);
        Assert.Equal(2, p.Blocks.Count);
        Assert.Equal(string.Join('\n', Left) + "\n\n" + string.Join('\n', Right), p.Text);
    }

    [Fact]
    public void ThreeColumnsReadInOrder()
    {
        string[][] cols = [Left, Right, ["one two three", "four five six", "seven eight nine", "ten eleven twelve", "thirteen fourteen", "fifteen sixteen"]];
        List<PdfTextGlyph> g = [];
        for (int c = 0; c < 3; c++)
        {
            g.AddRange(Syn.Lines(cols[c], 40 + c * 190, 700));
        }

        string text = Syn.Analyze(g).Text;
        Assert.Equal(string.Join("\n\n", cols.Select(c => string.Join('\n', c))), text);
    }

    [Fact]
    public void TitleAboveColumnsStaysFirstAndColumnsStayApart()
    {
        List<PdfTextGlyph> g = [];
        g.AddRange(Syn.Line("a long title across the whole page width here", 100, 740, 14));
        g.AddRange(Syn.Lines(Left, 50, 700));
        g.AddRange(Syn.Lines(Right, 330, 700));
        PdfPageText p = Syn.Analyze(g);
        Assert.StartsWith("a long title across the whole page width here\n\nthe harbor", p.Text, StringComparison.Ordinal);
        Assert.EndsWith(Right[^1], p.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TableRowsStayRowsAndCellsAreNotMerged()
    {
        string[][] rows =
        [
            ["Item", "Quantity", "Unit", "Notes"],
            ["Lemons", "120", "crate", "unwaxed"],
            ["Bicycles", "14", "each", "assorted"],
            ["Blankets", "40", "bale", "wool"],
            ["Lamp oil", "9", "drum", "sealed"],
        ];
        List<PdfTextGlyph> g = [];
        double[] xs = [50, 150, 250, 350];
        for (int r = 0; r < rows.Length; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                g.AddRange(Syn.Line(rows[r][c], xs[c], 600 - r * 16));
            }
        }

        PdfPageText p = Syn.Analyze(g);
        PdfTextBlock b = Assert.Single(p.Blocks);
        Assert.True(b.IsTable);
        Assert.Equal(5, b.Lines.Count);
        Assert.Equal("Item\tQuantity\tUnit\tNotes", b.Lines[0].Text);
        Assert.Equal("Lamp oil\t9\tdrum\tsealed", b.Lines[4].Text);
        Assert.Equal([0, 1, 2, 3], b.Lines[1].CellStarts);
    }

    [Fact]
    public void ProseColumnsAreNotATable()
    {
        List<PdfTextGlyph> g = [];
        g.AddRange(Syn.Lines(Left, 50, 700));
        g.AddRange(Syn.Lines(Right, 330, 700));
        Assert.All(Syn.Analyze(g).Blocks, b => Assert.False(b.IsTable));
    }

    [Fact]
    public void SuperscriptJoinsItsLine()
    {
        List<PdfTextGlyph> g = Syn.Line("word", 50, 700);
        g.Add(Syn.Glyph("1", 70, 704, 6));
        g.AddRange(Syn.Line("and more text", 74, 700));
        PdfPageText p = Syn.Analyze(g);
        Assert.Single(p.Blocks[0].Lines);
    }

    [Fact]
    public void HeadersAndFootersAreFlaggedByRepetition()
    {
        PdfPageGlyphs Page(int i)
        {
            List<PdfTextGlyph> g = [];
            g.AddRange(Syn.Line("Quarterly Harbor Report", 50, 760));
            g.AddRange(Syn.Line($"body text of page number {"abcdefg"[i]}", 50, 500));
            g.AddRange(Syn.Line($"Page {i + 1}", 280, 30));
            return Syn.Page(g, i);
        }

        PdfTextExtractor ex = new(new FakeSource(Page(0), Page(1), Page(2)), null, new PdfTextOptions { MaxDegreeOfParallelism = 2 });
        IReadOnlyList<PdfPageText> pages = ex.ExtractDocument();
        foreach (PdfPageText p in pages)
        {
            Assert.Equal(3, p.Blocks.Count);
            Assert.Equal(PdfTextRole.Header, p.Blocks[0].Role);
            Assert.Equal(PdfTextRole.Body, p.Blocks[1].Role);
            Assert.Equal(PdfTextRole.Footer, p.Blocks[2].Role);
        }

        Assert.Equal("Quarterly Harbor Report\n\nbody text of page number b\n\nPage 2", pages[1].Text);
        Assert.Equal("body text of page number b", new PdfTextExtractor(new FakeSource(Page(0), Page(1), Page(2)), null,
            new PdfTextOptions { ExcludeHeadersAndFooters = true }).ExtractDocument()[1].Text);
    }

    [Fact]
    public void VerticalCjkReadsRightColumnFirstTopToBottom()
    {
        List<PdfTextGlyph> g = [];
        g.AddRange(Syn.Column("ふたつめの列です", 300, 700));
        g.AddRange(Syn.Column("ひとつめの列です", 330, 700));
        g.AddRange(Syn.Column("みっつめ", 270, 700));
        PdfPageText p = Syn.Analyze(g);
        Assert.Equal("ひとつめの列です\nふたつめの列です\nみっつめ", p.Text.Replace("\n\n", "\n", StringComparison.Ordinal));
        Assert.All(p.Blocks.SelectMany(b => b.Lines), l => Assert.True(l.IsVertical));
    }

    [Fact]
    public void RotatedParagraphIsReadLast()
    {
        List<PdfTextGlyph> g = [];
        // A paragraph turned 90 degrees clockwise: glyphs run downwards, lines stack leftwards.
        string rot = "rotated words here";
        double y = 600;
        foreach (char c in rot)
        {
            if (c == ' ')
            {
                y -= 2.5;
                continue;
            }

            g.Add(new PdfTextGlyph { Text = c.ToString(), Matrix = new PdfTextMatrix(0, -10, 10, 0, 500, y), Advance = 0.5, FillColor = 0 });
            y -= 5;
        }

        g.AddRange(Syn.Lines(Left, 50, 700));
        string text = Syn.Analyze(g).Text;
        Assert.Equal(string.Join('\n', Left) + "\n\nrotated words here", text);
    }

    [Fact]
    public void ArabicAndHebrewComeOutInLogicalOrder()
    {
        string he = "שלום עולם";
        string visualHe = string.Join(' ', he.Split(' ').Reverse().Select(Syn.Rev));
        PdfPageText p = Syn.Analyze(Syn.Line(visualHe, 400, 700));
        Assert.Equal(he, p.Text);
        Assert.Equal(PdfTextDirection.RightToLeft, p.Blocks[0].Lines[0].Direction);

        string ar = "مرحبا بالعالم";
        string visualAr = string.Join(' ', ar.Split(' ').Reverse().Select(Syn.Rev));
        Assert.Equal(ar, Syn.Analyze(Syn.Line(visualAr, 400, 700)).Text);
    }

    [Fact]
    public void RtlLineWithNumbersKeepsNumbersInPlace()
    {
        // Logical: שלום 2026 עולם   (right-to-left paragraph)
        string logical = "שלום 2026 עולם";
        string visual = Syn.Rev("עולם") + " 2026 " + Syn.Rev("שלום");
        Assert.Equal(logical, Syn.Analyze(Syn.Line(visual, 300, 700)).Text);
    }

    [Fact]
    public void EmbeddedRtlRunInEnglishLine()
    {
        // Logical: the Hebrew שלום עולם appears inside English text
        string visual = "the Hebrew " + Syn.Rev("עולם") + " " + Syn.Rev("שלום") + " appears inside English text";
        Assert.Equal("the Hebrew שלום עולם appears inside English text", Syn.Analyze(Syn.Line(visual, 50, 700)).Text);
    }

    [Fact]
    public void ActualTextReplacesItsSpan()
    {
        List<PdfTextGlyph> g = Syn.Line("pay ", 50, 700);
        List<PdfTextGlyph> span = Syn.Line("xyz", 70, 700);
        for (int i = 0; i < span.Count; i++)
        {
            span[i] = span[i] with { ActualTextSpan = 1, ActualText = i == 0 ? "fee" : null };
        }

        g.AddRange(span);
        g.AddRange(Syn.Line(" now", 85, 700));
        Assert.Equal("pay fee now", Syn.Analyze(g).Text);
    }

    [Fact]
    public void LigaturesAreDecomposedByDefaultAndCanBeKept()
    {
        List<PdfTextGlyph> g = [Syn.Glyph("o", 50, 700), Syn.Glyph("\uFB03", 55, 700), Syn.Glyph("c", 60, 700), Syn.Glyph("e", 65, 700)];
        Assert.Equal("office", Syn.Analyze(g).Text);
        Assert.Equal("o\uFB03ce", Syn.Analyze(g, new PdfTextOptions { DecomposeLigatures = false }).Text);
    }

    [Fact]
    public void HyphenatedWordsAreKeptOrJoined()
    {
        List<PdfTextGlyph> g = Syn.Lines(["an exam-", "ple of text"], 50, 700);
        Assert.Equal("an exam-\nple of text", Syn.Analyze(g).Text);
        Assert.Equal("an example of text", Syn.Analyze(g, new PdfTextOptions { JoinHyphenatedWords = true }).Text);
    }

    [Fact]
    public void FakeBoldDuplicatesCollapse()
    {
        List<PdfTextGlyph> g = Syn.Line("Bold", 50, 700);
        foreach (PdfTextGlyph x in Syn.Line("Bold", 50.3, 700))
        {
            g.Add(x);
        }

        Assert.Equal("Bold", Syn.Analyze(g).Text);
        Assert.Equal(8, Syn.Analyze(g, new PdfTextOptions { CollapseDuplicates = false }).Glyphs.Count);
    }

    [Fact]
    public void RepeatedLettersAreNotDuplicates()
    {
        Assert.Equal("ball", Syn.Analyze(Syn.Line("ball", 50, 700)).Text);
    }

    [Fact]
    public void InvisibleTextIsFlaggedAndClippedAndOffPageAreExcluded()
    {
        List<PdfTextGlyph> g = Syn.Line("seen", 50, 700);
        List<PdfTextGlyph> ocr = Syn.Line("ocr", 50, 600);
        for (int i = 0; i < ocr.Count; i++)
        {
            ocr[i] = ocr[i] with { RenderMode = 3, IsVisible = false };
        }

        g.AddRange(ocr);
        List<PdfTextGlyph> clipped = Syn.Line("gone", 50, 500);
        for (int i = 0; i < clipped.Count; i++)
        {
            clipped[i] = clipped[i] with { IsClipped = true, IsVisible = false };
        }

        g.AddRange(clipped);
        g.AddRange(Syn.Line("away", 900, 300));

        PdfPageText p = Syn.Analyze(g);
        Assert.Equal("seen\n\nocr", p.Text);
        PdfTextWord ocrWord = p.Words.Last();
        Assert.False(ocrWord.IsVisible);
        Assert.True(p.Words.First().IsVisible);
        Assert.Contains("gone", Syn.Analyze(g, new PdfTextOptions { IncludeClippedAndOffPage = true }).Text, StringComparison.Ordinal);
        Assert.Contains("away", Syn.Analyze(g, new PdfTextOptions { IncludeClippedAndOffPage = true }).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void LayoutModeKeepsColumnsAligned()
    {
        List<PdfTextGlyph> g = [];
        string[][] rows = [["Name", "Qty"], ["Apple", "3"], ["Fig", "12"]];
        for (int r = 0; r < rows.Length; r++)
        {
            g.AddRange(Syn.Line(rows[r][0], 50, 700 - r * 14));
            g.AddRange(Syn.Line(rows[r][1], 200, 700 - r * 14));
        }

        string[] lines = Syn.Analyze(g).GetText(PdfTextMode.Layout).Split('\n');
        Assert.Equal(3, lines.Length);
        int col = lines[0].IndexOf("Qty", StringComparison.Ordinal);
        Assert.True(col > 20);
        Assert.Equal(col, lines[1].IndexOf('3', StringComparison.Ordinal));
        Assert.Equal(col, lines[2].IndexOf("12", StringComparison.Ordinal));
        Assert.StartsWith("Apple", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void ContentOrderFollowsTheStream()
    {
        List<PdfTextGlyph> g = [];
        g.AddRange(Syn.Line("second column first", 330, 700));
        g.AddRange(Syn.Line("first column second", 50, 700));
        g.AddRange(Syn.Line("next line", 50, 686));
        Assert.Equal("second column first\nfirst column second\nnext line", Syn.Analyze(g).GetText(PdfTextMode.ContentOrder));
    }

    [Fact]
    public void StructureOrderOverridesGeometryWhenItCoversThePage()
    {
        // Two paragraphs side by side that geometry reads left to right; the tree says right first.
        List<PdfTextGlyph> g = [];
        foreach (PdfTextGlyph x in Syn.Lines(Left, 50, 700))
        {
            g.Add(x with { MarkedContentId = 0 });
        }

        foreach (PdfTextGlyph x in Syn.Lines(Right, 330, 700))
        {
            g.Add(x with { MarkedContentId = 1 });
        }

        PageStructure ps = new();
        ps.Map[1] = (0, 1);
        ps.Map[0] = (1, 2);
        PdfPageText p = PdfTextExtractor.AnalyzeCore(Syn.Page(g), new PdfTextOptions(), ps, true, CancellationToken.None);
        Assert.True(p.UsedStructureTree);
        Assert.StartsWith(Right[0], p.Text, StringComparison.Ordinal);

        PageStructure partial = new();
        partial.Map[1] = (0, 1);
        PdfPageText q = PdfTextExtractor.AnalyzeCore(Syn.Page(g), new PdfTextOptions(), partial, true, CancellationToken.None);
        Assert.False(q.UsedStructureTree);
        Assert.StartsWith(Left[0], q.Text, StringComparison.Ordinal);
        Assert.Single(q.Diagnostics);
    }

    [Fact]
    public void JsonCarriesAVersionAndMatchesTheDocumentedShape()
    {
        PdfPageText p = Syn.Analyze(Syn.Lines(["hello world", "second line"], 50, 700));
        string json = PdfExtractionJson.ToJson([p]);
        using JsonDocument d = JsonDocument.Parse(json);
        JsonElement root = d.RootElement;
        Assert.Equal("onepdf.extraction", root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(1, root.GetProperty("pageCount").GetInt32());
        JsonElement page = root.GetProperty("pages")[0];
        Assert.Equal("hello world\nsecond line", page.GetProperty("text").GetString());
        JsonElement word = page.GetProperty("blocks")[0].GetProperty("lines")[0].GetProperty("words")[0];
        Assert.Equal("hello", word.GetProperty("text").GetString());
        Assert.Equal(4, word.GetProperty("bounds").GetArrayLength());
        int gi = word.GetProperty("glyphs")[0].GetInt32();
        Assert.Equal("h", page.GetProperty("glyphs")[gi].GetProperty("text").GetString());
        Assert.Equal(6, page.GetProperty("glyphs")[0].GetProperty("matrix").GetArrayLength());
        Assert.Equal(json, PdfExtractionJson.ToJson([p]));
        Assert.DoesNotContain("\"glyphs\":[{", PdfExtractionJson.ToJson([p], new PdfExtractionJsonOptions { IncludeGlyphs = false }), StringComparison.Ordinal);
    }

    [Fact]
    public void LoneSurrogatesDoNotBreakJson()
    {
        PdfPageText p = Syn.Analyze([Syn.Glyph("\uD800", 50, 700), Syn.Glyph("a", 55, 700)]);
        using JsonDocument d = JsonDocument.Parse(PdfExtractionJson.ToJson([p]));
        Assert.Equal("\uFFFDa", d.RootElement.GetProperty("pages")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void EmptyAndDegeneratePagesWork()
    {
        Assert.Equal("", Syn.Analyze([]).Text);
        PdfTextGlyph bad = Syn.Glyph("a", double.NaN, 700);
        Assert.Equal("", Syn.Analyze([bad]).Text);
        Assert.Equal("", Syn.Analyze([default(PdfTextGlyph)]).Text);
    }

    [Fact]
    public void CancellationIsHonoured()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();
        PdfTextExtractor ex = new(new FakeSource(Syn.Page(Syn.Line("abc", 50, 700))), null, null);
        Assert.Throws<OperationCanceledException>(() => ex.ExtractDocument(cts.Token));
    }
}

public class ExtractionFuzzSmoke
{
    [Fact]
    public void MutatedGlyphListsNeverThrow()
    {
        Random rng = new(12345);
        byte[] seed = new byte[1 + 10 * 60];
        for (int round = 0; round < 400; round++)
        {
            rng.NextBytes(seed);
            if (round % 3 == 0)
            {
                // structured: a plausible line of glyphs, then noise
                for (int i = 0; i < 40; i++)
                {
                    seed[1 + i * 10 + 6] = 0;
                    seed[1 + i * 10 + 5] = 10;
                    seed[1 + i * 10 + 1] = 0;
                    seed[1 + i * 10 + 2] = (byte)(i * 6);
                }
            }

            OnePdf.Fuzz.ExtractionTarget.Run(seed);
            OnePdf.Fuzz.ExtractionTarget.Run(seed.AsSpan(0, rng.Next(2, seed.Length)));
        }
    }
}
