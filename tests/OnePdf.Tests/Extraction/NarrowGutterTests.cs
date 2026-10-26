using OnePdf.Text;

namespace OnePdf.Tests.Extraction;

/// <summary>
/// Columns of running text separated by a gutter narrower than the usual 1.25 em must be read column by column, while rows of labels,
/// cells, bullets and justified words under a similar gap stay rows.
/// </summary>
public class NarrowGutterTests
{
    private const double Size = 10;

    private static string[] Col(string tag, int lines) =>
        [.. Enumerable.Range(1, lines).Select(i => $"{tag} line{i:00} of running text in a column")];

    /// <summary>Width in points of a line from <see cref="Syn.Line"/>: half an em per letter, a quarter em per space.</summary>
    private static double Width(string s) => (s.Count(c => c != ' ') * 0.5 + s.Count(c => c == ' ') * 0.25) * Size;

    /// <summary>Two columns whose gutter is <paramref name="gutterEm"/> em wide.</summary>
    private static (List<PdfTextGlyph> Glyphs, string Expected) TwoColumns(double gutterEm, int lines = 14)
    {
        string[] a = Col("L", lines), b = Col("R", lines);
        double x2 = 50 + Width(a[0]) + gutterEm * Size;
        List<PdfTextGlyph> g = [.. Syn.Lines(a, 50, 700), .. Syn.Lines(b, x2, 700)];
        return (g, string.Join("\n\n", string.Join('\n', a), string.Join('\n', b)));
    }

    [Theory]
    [InlineData(0.6)]
    [InlineData(0.9)]
    [InlineData(1.1)]
    [InlineData(1.5)]
    public void ColumnsAreReadOneAfterAnother(double gutterEm)
    {
        var (g, expected) = TwoColumns(gutterEm);
        PdfPageText p = Syn.Analyze(g);
        Assert.Equal(expected, p.Text);
        string[] want = expected.Replace("\n\n", "\n").Split('\n');
        Assert.Equal(want, p.Blocks.SelectMany(b => b.Lines.Select(l => l.Text)).ToArray());
    }

    [Fact]
    public void LatinColumnsStartWithTheLeftOne()
    {
        string[] a = Col("L", 12), b = Col("R", 12);
        double x2 = 50 + Width(a[0]) + 0.8 * Size;
        List<PdfTextGlyph> g = [.. Syn.Lines(a, 50, 700), .. Syn.Lines(b, x2, 700)];
        PdfPageText p = Syn.Analyze(g);

        // Latin text is left to right, so the left column comes first.
        Assert.StartsWith(a[0] + "\n" + a[1], p.Text);
    }

    private static void AssertRows(List<PdfTextGlyph> g, string[] rows)
    {
        PdfPageText p = Syn.Analyze(g);
        Assert.Equal(string.Join("\n", rows), p.Text.Replace("\n\n", "\n"));
    }

    [Fact]
    public void LabelAndValueRowsStayRows()
    {
        List<PdfTextGlyph> g = [];
        string[] rows = new string[14];
        for (int i = 0; i < rows.Length; i++)
        {
            string label = $"Field{i:00}", value = $"value of the field {i:00} entered by the user here";
            g.AddRange(Syn.Line(label, 50, 700 - i * 14));
            g.AddRange(Syn.Line(value, 50 + Width(label) + 8, 700 - i * 14));
            rows[i] = label + " " + value;
        }

        AssertRows(g, rows);
    }

    [Fact]
    public void TableWithNarrowGapsStaysRows()
    {
        List<PdfTextGlyph> g = [];
        string[] rows = new string[14];
        for (int i = 0; i < rows.Length; i++)
        {
            string[] cells = [$"alpha{i:00}", $"beta{i:00}", $"gamma{i:00}"];
            double x = 50;
            foreach (string c in cells)
            {
                g.AddRange(Syn.Line(c, x, 700 - i * 14));
                x += Width(c) + 8;
            }

            rows[i] = string.Join(' ', cells);
        }

        AssertRows(g, rows);
    }

    [Fact]
    public void BulletsStayWithTheirText()
    {
        List<PdfTextGlyph> g = [];
        string[] rows = new string[14];
        for (int i = 0; i < rows.Length; i++)
        {
            string text = $"item {i:00} of a list with some running text in it";
            g.AddRange(Syn.Line("-", 50, 700 - i * 14));
            g.AddRange(Syn.Line(text, 50 + 5 + 6, 700 - i * 14));
            rows[i] = "- " + text;
        }

        AssertRows(g, rows);
    }

    [Fact]
    public void JustifiedColumnIsNotCut()
    {
        // Word gaps of up to 0.6 em that move from line to line: no stripe runs through the lines.
        List<PdfTextGlyph> g = [];
        string[] rows = new string[14];
        string[] words = ["aaaaaa", "bbbbbb", "cccccc", "dddddd", "eeeeee"];
        for (int i = 0; i < rows.Length; i++)
        {
            double x = 50;
            for (int k = 0; k < words.Length; k++)
            {
                g.AddRange(Syn.Line(words[k], x, 700 - i * 14));
                x += 30 + 2.5 + ((i * 3 + k * 5) % 4) * 1.2;
            }

            rows[i] = string.Join(' ', words);
        }

        AssertRows(g, rows);
    }

    [Fact]
    public void ThreeNarrowColumnsAreReadInOrderUnderATitle()
    {
        string title = "a title across the whole width of the page above the columns";
        List<PdfTextGlyph> g = [.. Syn.Line(title, 50, 750)];
        List<string> expected = [title];
        double x = 50;
        for (int c = 0; c < 3; c++)
        {
            string[] col = Col($"C{c}", 12);
            g.AddRange(Syn.Lines(col, x, 700));
            expected.Add(string.Join("\n", col));
            x += Width(col[0]) + 7;
        }

        Assert.Equal(string.Join("\n\n", expected), Syn.Analyze(g).Text);
    }
}
