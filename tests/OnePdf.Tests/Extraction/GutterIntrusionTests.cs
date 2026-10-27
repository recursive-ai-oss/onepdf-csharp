using OnePdf.Text;

namespace OnePdf.Tests.Extraction;

/// <summary>
/// Two columns of running text whose gutter is about one em wide but holds a few words on some lines (a word that runs into the gutter, a stray
/// page number, a caption across both columns) must still be read column by column, with the full-width text read in place.
/// </summary>
public class GutterIntrusionTests
{
    private const double Size = 10;

    private static string[] Col(string tag, int lines) =>
        [.. Enumerable.Range(1, lines).Select(i => $"{tag} line{i:00} of running text in a column")];

    private static double Width(string s) => (s.Count(c => c != ' ') * 0.5 + s.Count(c => c == ' ') * 0.25) * Size;

    private static readonly double ColWidth = Width(Col("L", 1)[0]);
    private static readonly double X2 = 50 + ColWidth + Size;

    private static string Expected(string[] a, string[] b) => string.Join("\n\n", string.Join('\n', a), string.Join('\n', b));

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void WordsRunningIntoTheGutterStayInTheirColumn(int lines)
    {
        string[] a = Col("L", 30), b = Col("R", 30);
        for (int i = 0; i < lines; i++)
        {
            a[8 + i * 6] += " x";
        }

        List<PdfTextGlyph> g = [.. Syn.Lines(a, 50, 700), .. Syn.Lines(b, X2, 700)];
        Assert.Equal(Expected(a, b), Syn.Analyze(g).Text);
    }

    [Fact]
    public void StrayWordInTheGutterIsNotACut()
    {
        string[] a = Col("L", 30), b = Col("R", 30);
        List<PdfTextGlyph> g = [.. Syn.Lines(a, 50, 700), .. Syn.Lines(b, X2, 700)];
        g.AddRange(Syn.Line("7", 50 + ColWidth + 2.5, 700 - 8 * 14));
        string text = Syn.Analyze(g).Text;
        Assert.True(text.IndexOf("L line30", StringComparison.Ordinal) < text.IndexOf("R line01", StringComparison.Ordinal), text);
    }

    [Fact]
    public void CaptionAcrossBothColumnsReadsAfterThem()
    {
        string[] a = Col("L", 30), b = Col("R", 30);
        string caption = "Figure 1 a caption that runs across both of the columns above it";
        List<PdfTextGlyph> g = [.. Syn.Lines(a, 50, 700), .. Syn.Lines(b, X2, 700), .. Syn.Line(caption, 50, 700 - 30 * 14 - 10)];
        Assert.Equal(Expected(a, b) + "\n\n" + caption, Syn.Analyze(g).Text);
    }

    [Fact]
    public void TitleAcrossBothColumnsReadsFirst()
    {
        string[] a = Col("L", 30), b = Col("R", 30);
        string title = "A title that runs across both of the columns below it";
        List<PdfTextGlyph> g = [.. Syn.Line(title, 50, 730), .. Syn.Lines(a, 50, 700), .. Syn.Lines(b, X2, 700)];
        Assert.Equal(title + "\n\n" + Expected(a, b), Syn.Analyze(g).Text);
    }

    [Fact]
    public void CaptionInTheMiddleReadsInPlace()
    {
        string[] a1 = Col("L", 18), b1 = Col("R", 18), a2 = Col("M", 18), b2 = Col("N", 18);
        string caption = "Table 2 a wide row that runs across both of the columns of the page";
        List<PdfTextGlyph> g =
        [
            .. Syn.Lines(a1, 50, 700), .. Syn.Lines(b1, X2, 700),
            .. Syn.Line(caption, 50, 700 - 18 * 14 - 6),
            .. Syn.Lines(a2, 50, 700 - 20 * 14), .. Syn.Lines(b2, X2, 700 - 20 * 14),
        ];
        Assert.Equal(string.Join("\n\n", Expected(a1, b1), caption, Expected(a2, b2)), Syn.Analyze(g).Text);
    }

    private static void AssertRows(List<PdfTextGlyph> g, string[] rows) =>
        Assert.Equal(string.Join("\n", rows), Syn.Analyze(g).Text.Replace("\n\n", "\n"));

    [Fact]
    public void LongTableWithNarrowGapsStaysRows()
    {
        List<PdfTextGlyph> g = [];
        string[] rows = new string[40];
        for (int i = 0; i < rows.Length; i++)
        {
            string[] cells = [$"alpha{i:00}", $"beta{i:00}", $"gamma{i:00}"];
            double x = 50;
            foreach (string c in cells)
            {
                g.AddRange(Syn.Line(c, x, 750 - i * 14));
                x += Width(c) + 8;
            }

            rows[i] = string.Join(' ', cells);
        }

        AssertRows(g, rows);
    }

    [Fact]
    public void LongLabelAndValueFormStaysRows()
    {
        List<PdfTextGlyph> g = [];
        string[] rows = new string[40];
        for (int i = 0; i < rows.Length; i++)
        {
            string label = $"Field{i:00}", value = $"value of the field {i:00} entered by the user here";
            g.AddRange(Syn.Line(label, 50, 750 - i * 14));
            g.AddRange(Syn.Line(value, 50 + Width(label) + 8, 750 - i * 14));
            rows[i] = label + " " + value;
        }

        AssertRows(g, rows);
    }

    [Fact]
    public void SingleColumnWithRaggedLongLinesIsNotCut()
    {
        // One column of 40 lines of different lengths: the ragged right edge leaves an empty band at the right, which is a margin, not a gutter.
        List<PdfTextGlyph> g = [];
        string[] rows = new string[40];
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = $"line{i:00} of one column of running text" + new string('w', (i * 7) % 11 == 0 ? 20 : (i * 5) % 9);
            g.AddRange(Syn.Line(rows[i], 50, 750 - i * 14));
        }

        Assert.Equal(string.Join("\n", rows), Syn.Analyze(g).Text.Replace("\n\n", "\n"));
    }
}
