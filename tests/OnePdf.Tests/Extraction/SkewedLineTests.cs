using System.Runtime.CompilerServices;
using OnePdf.Text;

namespace OnePdf.Tests.Extraction;

/// <summary>
/// Text set along a baseline a few degrees off the page axes (a text layer over a slightly rotated scan) is grouped into whole lines along each
/// line's own slope and read in order. The files are <c>testdata/repros/skew-*.pdf</c>, written by <see cref="SkewFixtures"/>.
/// </summary>
public class SkewedLineTests
{
    private static string RepoFile(string name, [CallerFilePath] string source = "")
    {
        for (string? d = Path.GetDirectoryName(source); d is not null; d = Path.GetDirectoryName(d))
        {
            string c = Path.Combine(d, "testdata", "repros");
            if (Directory.Exists(c))
            {
                return Path.Combine(c, name);
            }
        }

        throw new DirectoryNotFoundException("testdata/repros");
    }

    private static PdfPageText Extract(string name)
    {
        using PdfDocument doc = PdfDocument.Open(RepoFile(name));
        return doc.ExtractText(new PdfTextOptions { RetainGlyphs = true })[0];
    }

    private static string Expected(params string[][] paragraphs) => string.Join("\n\n", paragraphs.Select(p => string.Join("\n", p)));

    [Fact]
    public void CommittedFixturesEqualTheirGenerator()
    {
        string? regen = Environment.GetEnvironmentVariable("ONEPDF_REGEN_FIXTURES");
        foreach ((string name, byte[] data) in SkewFixtures.All())
        {
            if (regen == "1")
            {
                File.WriteAllBytes(RepoFile(name), data);
                continue;
            }

            Assert.Equal(data, File.ReadAllBytes(RepoFile(name)));
        }
    }

    private static void AssertParagraphs(PdfPageText p, string[][] paragraphs)
    {
        Assert.Equal(Expected(paragraphs), p.Text);
        string[] lines = [.. paragraphs.SelectMany(x => x)];
        PdfTextLine[] got = [.. p.Blocks.SelectMany(b => b.Lines)];
        Assert.Equal(lines.Length, got.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            Assert.Equal(lines[i], got[i].Text);
            Assert.Equal(lines[i].Split(' '), got[i].Words.Select(w => w.Text).ToArray());
        }

        Assert.Equal(paragraphs.Length, p.Blocks.Count);
    }

    [Theory]
    [InlineData("skew-plus2.pdf")]
    [InlineData("skew-minus3.5.pdf")]
    [InlineData("skew-plus5.pdf")]
    public void SkewedParagraphsReadWholeAndInOrder(string file) => AssertParagraphs(Extract(file), SkewFixtures.Paragraphs);

    [Fact]
    public void SkewedColumnsAreReadOneAfterTheOther()
    {
        PdfPageText p = Extract("skew-two-columns.pdf");
        Assert.Equal(Expected([.. SkewFixtures.LeftColumn, .. SkewFixtures.RightColumn]), p.Text);
        Assert.Equal(4, p.Blocks.Count);
    }

    [Fact]
    public void InvisibleWordByWordLayerWithSlightlyDifferentLineAnglesReadsAsLines()
    {
        PdfPageText p = Extract("skew-ocr-layer.pdf");
        Assert.Equal(string.Join("\n", SkewFixtures.OcrLines), p.Text);
        PdfTextLine[] got = [.. p.Blocks.SelectMany(b => b.Lines)];
        Assert.Equal(SkewFixtures.OcrLines.Length, got.Length);
        for (int i = 0; i < got.Length; i++)
        {
            Assert.Equal(SkewFixtures.OcrLines[i].Split(' '), got[i].Words.Select(w => w.Text).ToArray());
            Assert.All(got[i].Words, w => Assert.False(w.IsVisible));
        }
    }

    [Theory]
    [InlineData("skew-plus5.pdf")]
    [InlineData("skew-ocr-layer.pdf")]
    public void ContentOrderAndLayoutModesKeepSkewedLinesWhole(string file)
    {
        PdfPageText p = Extract(file);
        string[] expected = [.. p.Blocks.SelectMany(b => b.Lines).Select(l => l.Text)];
        Assert.Equal(expected, p.GetText(PdfTextMode.ContentOrder).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(expected, p.GetText(PdfTextMode.Layout).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => string.Join(" ", l.Split(" ", StringSplitOptions.RemoveEmptyEntries))).ToArray());
    }

    [Fact]
    public void UprightPageIsNotTreatedAsSkewed()
    {
        // A page of the same text with no turn at all: the frame is the plain axis one.
        List<PdfTextGlyph> g = [];
        foreach (string[] para in SkewFixtures.Paragraphs)
        {
            for (int i = 0; i < para.Length; i++)
            {
                g.AddRange(Syn.Line(para[i], 72, 700 - i * 14));
            }

            break;
        }

        Assert.Equal(0, TextLayout.OwnSkew(g[0], 0));
        PdfPageText p = Syn.Analyze(g);
        Assert.Equal(string.Join("\n", SkewFixtures.Paragraphs[0]), p.Text);
    }

    [Fact]
    public void MostlyUprightPageWithAFewTurnedLinesKeepsItsAxisFrame()
    {
        List<PdfTextGlyph> g = [];
        for (int i = 0; i < 8; i++)
        {
            g.AddRange(Syn.Line("an upright line of running text number " + i, 72, 700 - i * 14));
        }

        // A turned stamp: not enough glyphs to make the page skewed.
        double r = 4 * Math.PI / 180;
        for (int i = 0; i < 6; i++)
        {
            PdfTextGlyph t = Syn.Glyph("x", 400 + i * 5, 300 + i * 0.35);
            g.Add(t with { Matrix = new PdfTextMatrix(10 * Math.Cos(r), 10 * Math.Sin(r), -10 * Math.Sin(r), 10 * Math.Cos(r), t.Matrix.E, t.Matrix.F) });
        }

        using LayoutWork w = new();
        PdfTextGlyph[] arr = [.. g];
        int[] idx = [.. Enumerable.Range(0, arr.Length)];
        Assert.Equal(0, TextLayout.EstimateSkew(w, arr, idx, 0, arr.Length, 0));
    }

    [Fact]
    public void RotatedNinetyDegreesIsUnchanged()
    {
        // Lines running upward (class 1) with a slight skew are measured in the turned frame of their own class.
        List<PdfTextGlyph> g = [];
        for (int line = 0; line < 3; line++)
        {
            string text = "a rotated line of running text number " + line;
            for (int i = 0; i < text.Length; i++)
            {
                g.Add(Syn.Glyph(text[i].ToString(), 0, 0) with { Matrix = new PdfTextMatrix(0, 10, -10, 0, 200 + line * 14, 100 + (i * 5)) });
            }
        }

        PdfPageText p = Syn.Analyze(g);
        Assert.Equal(3, p.Blocks.SelectMany(b => b.Lines).Count());
        Assert.Equal(0, TextLayout.OwnSkew(g[0], 1));
    }
}
