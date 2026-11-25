using System.Globalization;
using System.Text;
using OnePdf.Text;

namespace OnePdf.Tests.Extraction;

/// <summary>
/// Two columns of running text under a title, the second column shorter than the first (down to a fifth of its length), upright and turned a few
/// degrees, with narrow and wide titles of one or two lines. The text must read title, first column, second column. A short second column that
/// starts beside the first must not be taken for a card in the middle of it.
/// </summary>
public class ShortColumnTests
{
    private const double Cx = 306, Cy = 396, Size = 10, Pitch = 13;

    private static readonly string[] Filler =
    [
        "aaaa bbbbb cccc ddd eeeee fff gggg",
        "river stone water hills over the sea",
        "slow patient oxen along the road now",
        "salt was packed in wooden barrels",
    ];

    private static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Tm(double degrees, double x, double y)
    {
        double r = degrees * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        double px = Cx + ((x - Cx) * c) - ((y - Cy) * s), py = Cy + ((x - Cx) * s) + ((y - Cy) * c);
        return $"{Num(c)} {Num(s)} {Num(-s)} {Num(c)} {Num(px)} {Num(py)} Tm";
    }

    private static string ColumnLine(char column, int n) => $"{column}{n:00} {Filler[(n + column) % Filler.Length]}";

    /// <summary>Builds the page and returns the truth: the words in reading order.</summary>
    private static (byte[] Pdf, string[] Truth) Build(int titleLines, bool wideTitle, int firstLines, double shortShare, double degrees)
    {
        StringBuilder sb = new();
        List<string> truth = [];
        double top = 720;
        string[] titles = ["Annual statement of the working group", "Notes for the readers of this guide"];
        for (int t = 0; t < titleLines; t++)
        {
            string title = wideTitle ? titles[t] + " and offices" : titles[t].Split(' ').Take(2).Aggregate((a, b) => a + " " + b) + (t == 0 ? " one" : " two");
            sb.Append($"BT /F1 16 Tf {Tm(degrees, 60, top)} ({title}) Tj ET\n");
            truth.AddRange(title.Split(' '));
            top -= 22;
        }

        top -= 24;
        int secondLines = Math.Max(3, (int)Math.Round(firstLines * shortShare));
        foreach ((char col, int count, double x) in new[] { ('A', firstLines, 60.0), ('B', secondLines, 330.0) })
        {
            double y = top;
            for (int i = 0; i < count; i++)
            {
                string line = ColumnLine(col, i + 1);
                sb.Append($"BT /F1 {Size} Tf {Tm(degrees, x, y)} ({line}) Tj ET\n");
                truth.AddRange(line.Split(' '));
                y -= Pitch + (((i + 1) % 5 == 0) ? 10 : 0);
            }
        }

        return (Page(sb.ToString()), [.. truth]);
    }

    private static byte[] Page(string content)
    {
        PdfBuilder b = new();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        b.Stream(4, "", Encoding.Latin1.GetBytes(content));
        b.Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        long x = b.XrefTable(6);
        b.Trailer("/Size 6 /Root 1 0 R", x);
        return b.ToArray();
    }

    private static double OrderError(string[] truth, string[] got)
    {
        int[] prev = new int[got.Length + 1], cur = new int[got.Length + 1];
        for (int j = 0; j <= got.Length; j++)
        {
            prev[j] = j;
        }

        for (int i = 1; i <= truth.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= got.Length; j++)
            {
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (truth[i - 1] == got[j - 1] ? 0 : 1));
            }

            (prev, cur) = (cur, prev);
        }

        return (double)prev[got.Length] / truth.Length;
    }

    private static string[] Words(string text) => text.Split([' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);

    private static string Describe(int titleLines, bool wide, double share, double degrees) =>
        $"{titleLines}-line {(wide ? "wide" : "narrow")} title, column 2 at {share:P0}, {degrees}°";

    [Fact]
    public void AFullWidthNoteUnderTheColumnsIsReadLast()
    {
        // The note crosses the gutter, so it is not the first column going on; it closes the page.
        StringBuilder sb = new();
        List<string> truth = [];
        foreach ((char col, double x) in new[] { ('A', 60.0), ('B', 330.0) })
        {
            double y = 700;
            for (int i = 0; i < 10; i++)
            {
                string line = ColumnLine(col, i + 1);
                sb.Append($"BT /F1 {Size} Tf {Tm(0, x, y)} ({line}) Tj ET\n");
                truth.AddRange(line.Split(' '));
                y -= Pitch + (((i + 1) % 5 == 0) ? 10 : 0);
            }
        }

        string[] note = ["N01 a note that runs under both columns of the page from the left margin to the right", "N02 and goes on for a second line that is just as long as the first one was here"];
        for (int i = 0; i < note.Length; i++)
        {
            sb.Append($"BT /F1 {Size} Tf {Tm(0, 60, 540 - (i * Pitch))} ({note[i]}) Tj ET\n");
            truth.AddRange(note[i].Split(' '));
        }

        using PdfDocument doc = PdfDocument.Open(Page(sb.ToString()));
        Assert.Equal(truth, Words(doc.ExtractText(new PdfTextOptions())[0].Text));
    }

    [Fact]
    public void ShortSecondColumnsReadAfterTheFirst()
    {
        List<string> failed = [];
        int total = 0;
        foreach (int titleLines in new[] { 1, 2 })
        {
            foreach (bool wide in new[] { false, true })
            {
                foreach (double share in new[] { 0.2, 0.35, 0.5, 0.75, 1.0 })
                {
                    foreach (double degrees in new[] { 0, 2, -2, 3.5, -3.5, 5, -5 })
                    {
                        (byte[] pdf, string[] truth) = Build(titleLines, wide, 30, share, degrees);
                        using PdfDocument doc = PdfDocument.Open(pdf);
                        string[] got = Words(doc.ExtractText(new PdfTextOptions())[0].Text);
                        double e = OrderError(truth, got);
                        total++;
                        if (e > 0)
                        {
                            failed.Add($"{Describe(titleLines, wide, share, degrees)}: {e:P1}");
                        }
                    }
                }
            }
        }

        Assert.True(failed.Count == 0, $"{failed.Count} of {total} pages read out of order:\n" + string.Join('\n', failed.Take(12)));
    }
}
