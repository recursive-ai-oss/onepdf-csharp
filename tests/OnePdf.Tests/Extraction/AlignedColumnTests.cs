using OnePdf.Text;

namespace OnePdf.Tests.Extraction;

/// <summary>
/// Columns whose paragraphs end at the same heights leave empty horizontal bands that cross every column. Reading order must still take each
/// section column by column, not slice by slice.
/// </summary>
public class AlignedColumnTests
{
    private static readonly double[] TwoColumns = [50, 330];
    private static readonly double[] ThreeColumns = [40, 230, 420];

    private const string Title = "a long title across the whole page width of the document";

    /// <summary>Paragraph lines of one column: every paragraph is <paramref name="lines"/> lines, with the same gap in each column.</summary>
    private static string[][] Column(string tag, int paras, int lines)
    {
        string[][] r = new string[paras][];
        for (int p = 0; p < paras; p++)
        {
            r[p] = new string[lines];
            for (int l = 0; l < lines; l++)
            {
                r[p][l] = $"{tag} para{p + 1} line{l + 1} alpha beta";
            }
        }

        return r;
    }

    private static double Step(int lines) => lines * 14 + 14;

    /// <summary>Places a section of columns from <paramref name="top"/>; returns the y below it and appends the expected text.</summary>
    private static double Section(List<PdfTextGlyph> g, List<string> expected, string tag, double[] xs, int paras, int lines, double top)
    {
        for (int c = 0; c < xs.Length; c++)
        {
            string[][] col = Column($"{tag}{c}", paras, lines);
            for (int p = 0; p < paras; p++)
            {
                g.AddRange(Syn.Lines(col[p], xs[c], top - p * Step(lines)));
            }

            expected.Add(string.Join("\n\n", col.Select(p => string.Join('\n', p))));
        }

        return top - paras * Step(lines);
    }

    private static void Check(List<PdfTextGlyph> g, List<string> expected)
    {
        PdfPageText p = Syn.Analyze(g);
        string want = string.Join("\n\n", expected);
        Assert.Equal(want, p.Text);

        // The block order (extraction JSON) follows the same order.
        string fromBlocks = string.Join("\n", p.Blocks.SelectMany(b => b.Lines.Select(l => l.Text)));
        Assert.Equal(want.Replace("\n\n", "\n"), fromBlocks);
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 2)]
    [InlineData(3, 4)]
    public void TitleAboveTwoAlignedColumns(int lines, int paras)
    {
        List<PdfTextGlyph> g = [.. Syn.Line(Title, 60, 750, 14)];
        List<string> expected = [Title];
        Section(g, expected, "L", TwoColumns, paras, lines, 700);
        Check(g, expected);
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(2, 3)]
    public void AlignedTwoColumnsWithoutTitle(int lines, int paras)
    {
        List<PdfTextGlyph> g = [];
        List<string> expected = [];
        Section(g, expected, "L", TwoColumns, paras, lines, 700);
        Check(g, expected);
    }

    [Theory]
    [InlineData(2, 3)]
    [InlineData(3, 2)]
    public void TitleAboveThreeAlignedColumns(int lines, int paras)
    {
        List<PdfTextGlyph> g = [.. Syn.Line(Title, 60, 750, 14)];
        List<string> expected = [Title];
        Section(g, expected, "T", ThreeColumns, paras, lines, 700);
        Check(g, expected);
    }

    [Theory]
    [InlineData(2, 3)]
    [InlineData(3, 2)]
    public void FullWidthElementBetweenSectionsStillSplitsSections(int lines, int paras)
    {
        List<PdfTextGlyph> g = [.. Syn.Line(Title, 60, 750, 14)];
        List<string> expected = [Title];
        double y = Section(g, expected, "A", TwoColumns, paras, lines, 700);
        string rule = "a note that runs across the full width of both columns on this page";
        g.AddRange(Syn.Line(rule, 50, y));
        expected.Add(rule);
        Section(g, expected, "B", TwoColumns, paras, lines, y - 28);
        Check(g, expected);
    }

    [Fact(Skip = "Known limitation: when single-line paragraphs sit in the aligned gaps, the bands read across. Joining them would also join form rows (label and value cells), which read across.")]
    public void ParagraphsOfDifferentLengthsWithAlignedGapsReadByColumn()
    {
        int[] lines = [1, 3, 2, 1];
        List<PdfTextGlyph> g = [.. Syn.Line(Title, 60, 750, 14)];
        List<string> expected = [Title];
        for (int c = 0; c < TwoColumns.Length; c++)
        {
            double y = 700;
            List<string> paras = [];
            for (int p = 0; p < lines.Length; p++)
            {
                string[] para = Column($"M{c}", 1, lines[p])[0].Select(l => l.Replace("para1", "para" + (p + 1), StringComparison.Ordinal)).ToArray();
                g.AddRange(Syn.Lines(para, TwoColumns[c], y));
                paras.Add(string.Join('\n', para));
                y -= Step(lines[p]);
            }

            expected.Add(string.Join("\n\n", paras));
        }

        Check(g, expected);
    }

    [Fact]
    public void SingleLineRowsInTwoColumnsReadAcrossLikeATableOrForm()
    {
        // Cells that share a baseline and are one line each are indistinguishable from label and value rows; they read across, deliberately.
        List<PdfTextGlyph> g = [.. Syn.Line(Title, 60, 750, 14)];
        Section(g, [], "L", TwoColumns, 3, 1, 700);
        string text = Syn.Analyze(g).Text;
        Assert.Contains("L0 para1 line1 alpha beta\tL1 para1 line1 alpha beta", text, StringComparison.Ordinal);
    }
}
