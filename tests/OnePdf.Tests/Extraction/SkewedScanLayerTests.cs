using System.Runtime.CompilerServices;
using OnePdf.Text;

namespace OnePdf.Tests.Extraction;

/// <summary>
/// Searchable scans: a page image turned a few degrees under an invisible (render mode 3) text layer placed word by word, each word's
/// matrix turned to its line. Extraction returns every line whole, in reading order (title, then each column top to bottom).
/// </summary>
public class SkewedScanLayerTests
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

    [Theory]
    [InlineData("scan-skew-plus3-one-column")]
    [InlineData("scan-skew-minus4-two-column")]
    public void LinesComeOutWholeInReadingOrder(string name)
    {
        string[] expected = File.ReadAllLines(RepoFile(name + ".lines.txt"));
        using PdfDocument doc = PdfDocument.Open(RepoFile(name + ".pdf"));
        PdfPageText page = doc.ExtractText(new PdfTextOptions { RetainGlyphs = true })[0];

        string[] lines = [.. page.Blocks.SelectMany(b => b.Lines).Select(l => string.Join(' ', l.Words.Select(w => w.Text)))];
        Assert.Equal(expected, lines);

        string[] textLines = [.. page.Text.Split('\n').Select(l => string.Join(' ', l.Split(' ', StringSplitOptions.RemoveEmptyEntries))).Where(l => l.Length > 0)];
        Assert.Equal(expected, textLines);
    }
}
