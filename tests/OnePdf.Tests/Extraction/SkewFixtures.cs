using System.Text;

namespace OnePdf.Tests.Extraction;

/// <summary>
/// Builds the small PDFs under <c>testdata/repros/skew-*.pdf</c>: pages of text turned a few degrees, as the text of a slightly rotated scan is,
/// and an invisible word-by-word text layer. Each line is one text object with a rotated text matrix, so the glyph quads are exact.
/// Set <c>ONEPDF_REGEN_FIXTURES=1</c> to rewrite the committed files from these definitions.
/// </summary>
internal static class SkewFixtures
{
    internal static readonly string[][] Paragraphs =
    [
        [
            "The committee met on the first Monday of the month to review the",
            "annual budget and the proposals that had been sent in over the",
            "summer by the regional offices, most of which asked for more staff.",
            "After a long discussion the members agreed to defer the decision.",
        ],
        [
            "Appendix tables list the figures by region and by year, and the",
            "notes below each table explain how the numbers were collected.",
            "Readers who want the raw data can request it from the secretariat.",
        ],
        [
            "The next meeting is planned for early spring. Until then the",
            "working group will draft a short summary of the open questions,",
            "collect comments from the offices, and circulate a revised plan",
            "to all members at least two weeks before the date.",
        ],
    ];

    internal static readonly string[][] LeftColumn =
    [
        [
            "Rivers carry water from the hills",
            "down to the sea, and over many",
            "thousands of years they cut deep",
            "valleys through the softer rock.",
        ],
        [
            "Towns grew where the water was",
            "easy to cross, and the first",
            "bridges were made of simple logs.",
        ],
    ];

    internal static readonly string[][] RightColumn =
    [
        [
            "Fishing boats left the harbour",
            "before dawn and came back late",
            "in the day with their nets full,",
            "while the market stayed quiet.",
        ],
        [
            "Salt was packed in wooden barrels",
            "and sent inland on carts drawn by",
            "slow patient oxen along the road.",
        ],
    ];

    internal static readonly string[] OcrLines =
    [
        "Invoice number 20931 was issued on the fourth of March",
        "to the customer named below for the goods and services",
        "listed in the table. Payment is due within thirty days",
        "of the date of issue, by bank transfer to the account",
        "shown at the foot of this page. Thank you for your order.",
    ];

    private const double Cx = 306, Cy = 396;

    private static string Num(double v) => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A text matrix turned by <paramref name="degrees"/> about the page centre, for a point given as if the page were upright.</summary>
    private static string Tm(double degrees, double x, double y)
    {
        double r = degrees * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        double px = Cx + ((x - Cx) * c) - ((y - Cy) * s), py = Cy + ((x - Cx) * s) + ((y - Cy) * c);
        return $"{Num(c)} {Num(s)} {Num(-s)} {Num(c)} {Num(px)} {Num(py)} Tm";
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    private static byte[] Page(string content, string baseFont)
    {
        PdfBuilder b = new();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        b.Stream(4, "", Encoding.Latin1.GetBytes(content));
        b.Obj(5, $"<< /Type /Font /Subtype /Type1 /BaseFont /{baseFont} /Encoding /WinAnsiEncoding >>");
        long x = b.XrefTable(6);
        b.Trailer("/Size 6 /Root 1 0 R", x);
        return b.ToArray();
    }

    /// <summary>Three paragraphs turned by <paramref name="degrees"/>, one text object per line.</summary>
    internal static byte[] Paragraphs_(double degrees)
    {
        StringBuilder sb = new();
        double y = 700;
        foreach (string[] p in Paragraphs)
        {
            foreach (string line in p)
            {
                sb.Append($"BT /F1 11 Tf {Tm(degrees, 72, y)} ({Esc(line)}) Tj ET\n");
                y -= 15;
            }

            y -= 12;
        }

        return Page(sb.ToString(), "Helvetica");
    }

    /// <summary>Two columns of two paragraphs each, turned together.</summary>
    internal static byte[] TwoColumns(double degrees)
    {
        StringBuilder sb = new();
        foreach ((string[][] col, double x) in new[] { (LeftColumn, 72.0), (RightColumn, 330.0) })
        {
            double y = 700;
            foreach (string[] p in col)
            {
                foreach (string line in p)
                {
                    sb.Append($"BT /F1 11 Tf {Tm(degrees, x, y)} ({Esc(line)}) Tj ET\n");
                    y -= 15;
                }

                y -= 12;
            }
        }

        return Page(sb.ToString(), "Helvetica");
    }

    /// <summary>
    /// An invisible text layer (render mode 3) placed word by word, as an OCR engine writes it. Each line has its own angle, a little different from the
    /// last. The font is monospaced, so a word's width is known and the words sit exactly where the scan's words were.
    /// </summary>
    internal static byte[] OcrLayer()
    {
        const double size = 10, adv = 6;
        StringBuilder sb = new("3 Tr\n");
        double y = 700;
        for (int l = 0; l < OcrLines.Length; l++)
        {
            double degrees = 3 + (0.35 * ((l % 3) - 1));
            double r = degrees * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
            double x = 60;
            foreach (string word in OcrLines[l].Split(' '))
            {
                // The line turns about its own start, so the left margin stays put and the lines fan out slightly.
                double px = 60 + ((x - 60) * c), py = y + ((x - 60) * s);
                sb.Append($"BT /F1 {size} Tf {Num(c)} {Num(s)} {Num(-s)} {Num(c)} {Num(px)} {Num(py)} Tm ({Esc(word)}) Tj ET\n");
                x += (word.Length + 1) * adv;
            }

            y -= 16;
        }

        return Page(sb.ToString(), "Courier");
    }

    internal static IEnumerable<(string Name, byte[] Data)> All()
    {
        yield return ("skew-plus2.pdf", Paragraphs_(2));
        yield return ("skew-minus3.5.pdf", Paragraphs_(-3.5));
        yield return ("skew-plus5.pdf", Paragraphs_(5));
        yield return ("skew-two-columns.pdf", TwoColumns(2.5));
        yield return ("skew-ocr-layer.pdf", OcrLayer());
    }
}
