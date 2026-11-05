using System.Runtime.CompilerServices;
using System.Text;
using OnePdf.Text;
using static OnePdf.Tests.Tagging.TagDocs;

namespace OnePdf.Tests.Tagging;

/// <summary>Tagging splits text operators; the glyphs must stay exactly where they were.</summary>
public class TaggingPositionTests
{
    private static string Repros([CallerFilePath] string source = "")
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

    // A kerning number of a split TJ was written rounded to six decimals, which moves the glyph after it by up to 5e-10 em. That is far below anything
    // visible, but a glyph whose position lies within it of a step of the stored grid (1e-6) lands a step away. The page puts the second glyph there.
    [Fact]
    public void SplitTextOperatorKeepsKerningNumbersToTheirFullPrecision()
    {
        const double Fs = 12, Adjust = 500.0000004, Helvetica_A = 0.667;
        double shift = Adjust / 1000 * Fs; // 6.0000000048; written rounded to "500" it was 6, 4.8e-9 less
        double boundary = Math.Round(109.204, 6) + 0.5e-6 + 2.4e-9;
        double x0 = boundary - Helvetica_A * Fs - shift;
        string content = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"BT /F1 {Fs} Tf {x0:R} 700 Td [(A) -{Adjust:R} (B)] TJ ET\n");
        byte[] src = Make(content);
        var options = new PdfTextOptions { RetainGlyphs = true, UseStructureTree = false };
        double before;
        using (PdfDocument d0 = PdfDocument.Open(src))
        {
            before = d0.Pages[0].ExtractText(options).Glyphs[1].Matrix.E;
        }

        byte[] tagged = Tag(src, (_, pages, plan) =>
        {
            PdfTextLine line = pages[0].Blocks[0].Lines[0];
            Assert.Equal(2, line.Words.Count);
            plan.Add(PdfTagRole.P, PdfTagContent.Words(pages[0], line, 0, 1));
            plan.Add(PdfTagRole.P, PdfTagContent.Words(pages[0], line, 1, 1));
        }, out _);
        using PdfDocument d1 = PdfDocument.Open(tagged);
        PdfPageText page = d1.Pages[0].ExtractText(options);
        Assert.Equal("B", page.Glyphs[1].Text);
        Assert.Equal(before, page.Glyphs[1].Matrix.E);
    }

    // Two fuzz inputs (parameter bytes, then a file) on which a tagged and saved page showed a glyph one millionth of a point (one grid step) away from where
    // extraction had put it. The glyph sits within 1e-12 of a step, and a split operator reaches it by another sum: the step is the most it can differ.
    [Theory]
    [InlineData("tag-rounded-tj-adjustment-a.bin")]
    [InlineData("tag-rounded-tj-adjustment-b.bin")]
    public void TaggedFilesKeepEveryGlyphWhereItWas(string name) => OnePdf.Fuzz.TagTarget.Run(File.ReadAllBytes(Path.Combine(Repros(), name)));
}
