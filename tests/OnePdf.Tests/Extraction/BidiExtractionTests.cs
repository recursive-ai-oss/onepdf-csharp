using System.Runtime.CompilerServices;
using OnePdf.Text;

namespace OnePdf.Tests.Extraction;

/// <summary>
/// Right-to-left runs in logical order, tatweel and joining controls inside words, and format characters kept in the text. The files are
/// <c>testdata/repros/*.pdf</c>, written by <see cref="BidiFixtures"/>; a test checks that they still equal what the generator writes.
/// </summary>
public class BidiExtractionTests
{
    // Build output lives outside the checkout, so the files are found from this source file's location.
    private static string ReprosDir([CallerFilePath] string source = "")
    {
        for (string? d = Path.GetDirectoryName(source); d is not null; d = Path.GetDirectoryName(d))
        {
            string c = Path.Combine(d, "testdata", "repros");
            if (Directory.Exists(c))
            {
                return c;
            }
        }

        throw new DirectoryNotFoundException("testdata/repros");
    }

    private static string RepoFile(string name) => Path.Combine(ReprosDir(), name);

    private static PdfPageText Extract(string name)
    {
        using PdfDocument doc = PdfDocument.Open(RepoFile(name));
        return doc.ExtractText(new PdfTextOptions { RetainGlyphs = true })[0];
    }

    private static List<PdfTextLine> Lines(PdfPageText p) => [.. p.Blocks.SelectMany(b => b.Lines).OrderByDescending(l => l.Bounds.Top)];

    private static void AssertLine(PdfTextLine line, string text, PdfTextDirection direction, string[]? words = null)
    {
        Assert.Equal(text, line.Text);
        Assert.Equal(direction, line.Direction);
        Assert.Equal(words ?? text.Split(' '), line.Words.Select(w => w.Text).ToArray());
    }

    [Fact]
    public void CommittedFixturesEqualTheirGenerator()
    {
        string? regen = Environment.GetEnvironmentVariable("ONEPDF_REGEN_FIXTURES");
        foreach ((string name, byte[] data) in BidiFixtures.All())
        {
            if (regen == "1")
            {
                File.WriteAllBytes(RepoFile(name), data);
                continue;
            }

            Assert.Equal(data, File.ReadAllBytes(RepoFile(name)));
        }
    }

    [Theory]
    [InlineData("rtl-bidi-logical-stream.pdf")]
    [InlineData("rtl-bidi-visual-stream.pdf")]
    public void RtlRunsComeOutInLogicalOrderWhateverTheLineDirection(string file)
    {
        PdfPageText p = Extract(file);
        List<PdfTextLine> l = Lines(p);
        Assert.Equal(7, l.Count);

        // Left-to-right lines whose Arabic or Hebrew words outnumber the English ones.
        AssertLine(l[0], "abc مرحبا بالعالم def", PdfTextDirection.LeftToRight);
        AssertLine(l[1], "Hi שלום עולם bye", PdfTextDirection.LeftToRight);
        // A number inside the run stays in its place, and a bracketed word at the end of the run keeps its brackets.
        AssertLine(l[2], "abc مرحبا 2026 بالعالم def", PdfTextDirection.LeftToRight);
        AssertLine(l[3], "abc مرحبا بالعالم (كتاب) def", PdfTextDirection.LeftToRight);

        // Right-to-left lines with an English word, a number and brackets inside.
        AssertLine(l[4], "مرحبا hello بالعالم", PdfTextDirection.RightToLeft);
        AssertLine(l[5], "مرحبا (بالعالم) 2026 كتاب", PdfTextDirection.RightToLeft);
        AssertLine(l[6], "שלום world עולם", PdfTextDirection.RightToLeft);

        string expected = string.Join("\n\n", l.Select(x => x.Text));
        Assert.Equal(expected, p.Text);
        // The words of the page follow the lines.
        Assert.Equal(l.SelectMany(x => x.Text.Split(' ')), p.Words.Select(w => w.Text));
    }

    [Fact]
    public void BidiLinesStayLogicalInTheOtherPlainTextModes()
    {
        PdfPageText p = Extract("rtl-bidi-logical-stream.pdf");
        // Layout mode keeps words where they stand on the page but each word is logical; content order is the stream, which is logical here.
        string layout = p.GetText(PdfTextMode.Layout);
        Assert.Contains("مرحبا", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("ابحرم", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void TatweelAndPositionOnlyKashidaDoNotSplitTheWord()
    {
        PdfPageText p = Extract("rtl-tatweel.pdf");
        List<PdfTextLine> l = Lines(p);
        Assert.Equal(2, l.Count);
        // The tatweel codes stay in the text.
        AssertLine(l[0], "مرحبا كـــتاب بالعالم", PdfTextDirection.RightToLeft);
        Assert.Equal(3, l[0].Text.Count(c => c == '\u0640'));
        // A kashida that is only a gap between joined letters leaves the word whole and puts nothing into the text.
        AssertLine(l[1], "مرحبا كتاب بالعالم", PdfTextDirection.RightToLeft);
    }

    [Fact]
    public void ZwnjStaysInTheWordAndIsNotASpace()
    {
        PdfPageText p = Extract("rtl-zwnj.pdf");
        List<PdfTextLine> l = Lines(p);
        Assert.Equal(4, l.Count);
        // A zero-width glyph, the joiner in a letter's ToUnicode text, and the joiner only in /ActualText.
        AssertLine(l[0], "می\u200Cخواهم بالعالم", PdfTextDirection.RightToLeft);
        AssertLine(l[1], "می\u200Cخواهم بالعالم", PdfTextDirection.RightToLeft);
        AssertLine(l[2], "می\u200Cخواهم بالعالم", PdfTextDirection.RightToLeft);
        // Inside a left-to-right line.
        AssertLine(l[3], "abc کتاب\u200Cها def", PdfTextDirection.LeftToRight);
        Assert.Equal(4, p.Text.Count(c => c == '\u200C'));
    }

    [Fact]
    public void FormatCharactersAreKeptInEveryTextForm()
    {
        PdfPageText p = Extract("emoji-zwj.pdf");
        List<PdfTextLine> l = Lines(p);
        Assert.Equal(3, l.Count);
        foreach (PdfTextLine line in l)
        {
            AssertLine(line, "a " + BidiFixtures.Family + " b", PdfTextDirection.LeftToRight);
            Assert.Equal(2, line.Text.Count(c => c == '\u200D'));
        }

        Assert.Equal(6, p.Text.Count(c => c == '\u200D'));
        Assert.Equal(6, p.GetText(PdfTextMode.ContentOrder).Count(c => c == '\u200D'));
        Assert.Equal(6, p.GetText(PdfTextMode.Layout).Count(c => c == '\u200D'));
    }

    [Fact]
    public void JoinersAreKeptByWholeDocumentExtractionToo()
    {
        using PdfDocument doc = PdfDocument.Open(RepoFile("rtl-zwnj.pdf"));
        string text = doc.ExtractText()[0].Text;
        Assert.Equal(4, text.Count(c => c == '\u200C'));
        using PdfDocument emoji = PdfDocument.Open(RepoFile("emoji-zwj.pdf"));
        Assert.Equal(6, emoji.Pages[0].ExtractText().Text.Count(c => c == '\u200D'));
    }
}
