using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using OnePdf.Text;

namespace OnePdf.Tests.Extraction;

/// <summary>
/// Locks the extraction output: words, lines, blocks, roles, all three plain-text modes and the JSON form for a set of synthetic pages
/// (scenarios, a 3,400-glyph two-column page under several option sets, seeded mixed-script pages and the inputs the layout fuzz target decodes).
/// The expected SHA-256 per case is the embedded <c>golden.txt</c>; set <c>ONEPDF_UPDATE_GOLDEN</c> to a file path to rewrite it from the current output.
/// </summary>
public class GoldenTests
{
    private static readonly string[] Left =
    [
        "the harbor town woke slowly", "fishing boats rocked against", "their moorings while gulls", "argued over the morning catch", "by nine the market stalls", "were open and the smell",
    ];

    private static readonly string[] Right =
    [
        "visitors who arrived early", "could watch the ferry load", "crates of lemons bicycles", "a piano wrapped in blankets", "at noon the bell on the", "clock tower rang twelve times",
    ];

    private static readonly string[] Words =
    [
        "harbor", "lemons", "ferry", "the", "of", "and", "market", "ﬁnal", "oﬃce", "re-", "morning", "שלום", "עולם", "مرحبا", "123", "4.5", "x", "bell",
        "tower", "ふたつ", "漢字", "waffle", "e\u0301", "a-", "-b", "Δx", "(note)",
    ];

    /// <summary>A page like a typical born-digital two-column article: <paramref name="lines"/> lines of text per column.</summary>
    internal static List<PdfTextGlyph> TwoColumnPage(int lines, int seed = 7)
    {
        Random rng = new(seed);
        List<PdfTextGlyph> g = [];
        g.AddRange(Syn.Line("An Article About a Harbor Town", 150, 750, 16));
        double[] xs = [50, 320];
        for (int i = 0; i < lines; i++)
        {
            for (int c = 0; c < 2; c++)
            {
                StringBuilder sb = new();
                while (sb.Length < 38)
                {
                    sb.Append(Words[rng.Next(0, 11)]).Append(' ');
                }

                g.AddRange(Syn.Line(sb.ToString().TrimEnd(), xs[c], 720 - i * 12, 9));
            }
        }

        g.AddRange(Syn.Line("Page 3", 290, 30, 9));
        return g;
    }

    internal static List<PdfTextGlyph> MixedPage(int seed) => Mixed(seed);

    private static List<PdfTextGlyph> Mixed(int seed)
    {
        Random rng = new(seed);
        List<PdfTextGlyph> g = [];
        for (int line = 0; line < 14; line++)
        {
            double x = 40 + rng.Next(0, 3) * 150;
            double y = 740 - line * 18;
            double size = rng.Next(0, 5) == 0 ? 14 : 10;
            int n = rng.Next(2, 7);
            StringBuilder sb = new();
            for (int w = 0; w < n; w++)
            {
                sb.Append(Words[rng.Next(Words.Length)]).Append(' ');
            }

            foreach (char ch in sb.ToString().TrimEnd())
            {
                if (ch == ' ')
                {
                    if (rng.Next(0, 4) == 0)
                    {
                        g.Add(Syn.Glyph(" ", x, y, size, 0.25));
                    }

                    x += 0.25 * size;
                }
                else
                {
                    g.Add(Syn.Glyph(ch.ToString(), x, y, size));
                    if (rng.Next(0, 40) == 0)
                    {
                        g.Add(Syn.Glyph(ch.ToString(), x + 0.3, y, size)); // fake bold
                    }

                    x += 0.5 * size;
                }
            }
        }

        g.AddRange(Syn.Column("縦書きの文章です", 560, 700));
        g.Add(Syn.Glyph("1", 100, 746, 6));
        g.Add(new PdfTextGlyph { Text = "r", Matrix = new PdfTextMatrix(0, -10, 10, 0, 500, 300), Advance = 0.5, FillColor = 0 });
        g.Add(new PdfTextGlyph { Text = "k", Matrix = new PdfTextMatrix(10, 0, 0, 10, 70, 700), Advance = 0.5, IsVisible = false, RenderMode = 3 });
        g.Add(new PdfTextGlyph { Text = "c", Matrix = new PdfTextMatrix(10, 0, 0, 10, 70, 600), Advance = 0.5, IsClipped = true });
        g.Add(new PdfTextGlyph { Text = "o", Matrix = new PdfTextMatrix(10, 0, 0, 10, 900, 600), Advance = 0.5 });
        g.Add(new PdfTextGlyph { Text = "n", Matrix = new PdfTextMatrix(double.NaN, 0, 0, 10, 70, 600), Advance = 0.5 });
        g.Add(new PdfTextGlyph { Text = "\uD800", Matrix = new PdfTextMatrix(10, 0, 0, 10, 200, 100), Advance = 0.5 });
        return g;
    }

    private static List<(string Name, PdfPageGlyphs[] Pages, PdfTextOptions Options, PageStructure? Structure)> Cases()
    {
        List<(string, PdfPageGlyphs[], PdfTextOptions, PageStructure?)> c = [];

        void Add(string name, IEnumerable<PdfTextGlyph> g, PdfTextOptions? o = null, PageStructure? ps = null) => c.Add((name, [Syn.Page(g)], o ?? new PdfTextOptions(), ps));

        List<PdfTextGlyph> cols = [];
        for (int i = 0; i < Left.Length; i++)
        {
            cols.AddRange(Syn.Line(Left[i], 50, 700 - i * 14));
            cols.AddRange(Syn.Line(Right[i], 330, 700 - i * 14));
        }

        Add("two-columns", cols);
        Add("title-columns", [.. Syn.Line("a long title across the whole page width here", 100, 740, 14), .. Syn.Lines(Left, 50, 700), .. Syn.Lines(Right, 330, 700)]);
        Add("space-glyphs", Syn.Line("some words with space glyphs between them", 50, 700, 10, true));

        string[][] rows = [["Item", "Quantity", "Unit", "Notes"], ["Lemons", "120", "crate", "unwaxed"], ["Bicycles", "14", "each", "assorted"], ["Blankets", "40", "bale", "wool"], ["Lamp oil", "9", "drum", "sealed"]];
        List<PdfTextGlyph> table = [];
        double[] xs = [50, 150, 250, 350];
        for (int r = 0; r < rows.Length; r++)
        {
            for (int k = 0; k < 4; k++)
            {
                table.AddRange(Syn.Line(rows[r][k], xs[k], 600 - r * 16));
            }
        }

        Add("table", table);

        List<PdfTextGlyph> sup = Syn.Line("word", 50, 700);
        sup.Add(Syn.Glyph("1", 70, 704, 6));
        sup.AddRange(Syn.Line("and more text", 74, 700));
        Add("superscript", sup);

        List<PdfTextGlyph> vert = [];
        vert.AddRange(Syn.Column("ふたつめの列です", 300, 700));
        vert.AddRange(Syn.Column("ひとつめの列です", 330, 700));
        vert.AddRange(Syn.Column("みっつめ", 270, 700));
        Add("vertical-cjk", vert);

        Add("rtl", [.. Syn.Line(Syn.Rev("שלום עולם"), 50, 700), .. Syn.Line(Syn.Rev("مرحبا بالعالم"), 50, 680), .. Syn.Line("abc " + Syn.Rev("שלום") + " def 12", 50, 660)]);
        Add("ligatures", Syn.Line("ﬁnal oﬃce shelf-", 50, 700), new PdfTextOptions { DecomposeLigatures = false });
        Add("hyphens", [.. Syn.Line("a hyphen-", 50, 700), .. Syn.Line("ated word", 50, 686), .. Syn.Line("re-", 50, 672), .. Syn.Line("Upper", 50, 658)], new PdfTextOptions { JoinHyphenatedWords = true });

        PdfTextGlyph a1 = Syn.Glyph("a", 50, 700) with { ActualTextSpan = 1, ActualText = "XYZ" };
        Add("actualtext", [a1, Syn.Glyph("b", 55, 700) with { ActualTextSpan = 1 }, Syn.Glyph("c", 60, 700) with { ActualTextSpan = 1 }, .. Syn.Line("tail text", 70, 700)]);

        Add("big-two-column", TwoColumnPage(40));
        Add("big-two-column-keep", TwoColumnPage(40), new PdfTextOptions { DecomposeLigatures = false, CollapseDuplicates = false, IncludeClippedAndOffPage = true });
        Add("big-two-column-join", TwoColumnPage(40), new PdfTextOptions { JoinHyphenatedWords = true });
        for (int s = 1; s <= 6; s++)
        {
            Add("mixed-" + s, Mixed(s));
        }

        Add("mixed-content-mode", Mixed(9), new PdfTextOptions { Mode = PdfTextMode.ContentOrder, ExcludeHeadersAndFooters = true });

        // Several pages with running heads and page numbers, extracted as a document.
        PdfPageGlyphs[] doc = new PdfPageGlyphs[6];
        for (int i = 0; i < doc.Length; i++)
        {
            List<PdfTextGlyph> g = [];
            g.AddRange(Syn.Line("Quarterly Harbor Report", 50, 760));
            g.AddRange(TwoColumnPage(10, 100 + i).Skip(30));
            g.AddRange(Syn.Line($"Page {i + 1}", 280, 30));
            doc[i] = Syn.Page(g, i);
        }

        c.Add(("document", doc, new PdfTextOptions(), null));
        c.Add(("document-excluded", doc, new PdfTextOptions { ExcludeHeadersAndFooters = true }, null));

        // Structure tree, full and partial coverage.
        List<PdfTextGlyph> st = [];
        for (int i = 0; i < Left.Length; i++)
        {
            st.AddRange(Syn.Line(Left[i], 50, 700 - i * 14).Select(x => x with { MarkedContentId = 0 }));
            st.AddRange(Syn.Line(Right[i], 330, 700 - i * 14).Select(x => x with { MarkedContentId = 1 }));
        }

        st.AddRange(Syn.Line("running head", 50, 770));
        PageStructure full = new();
        full.Map[1] = (0, 1);
        full.Map[0] = (1, 2);
        Add("structure-full", st, null, full);
        PageStructure partial = new();
        partial.Map[1] = (0, 1);
        Add("structure-partial", st, null, partial);

        // The inputs the layout fuzz target decodes.
        Random rng = new(777);
        byte[] seed = new byte[1 + 10 * 90];
        for (int round = 0; round < 60; round++)
        {
            rng.NextBytes(seed);
            if (round % 3 == 0)
            {
                for (int i = 0; i < 60; i++)
                {
                    seed[1 + i * 10 + 6] = 0;
                    seed[1 + i * 10 + 5] = 10;
                    seed[1 + i * 10 + 1] = 0;
                    seed[1 + i * 10 + 2] = (byte)(i * 6);
                }
            }

            if (OnePdf.Fuzz.ExtractionTarget.Decode(seed) is var (o, glyphs))
            {
                c.Add(("fuzz-" + round, [Syn.Page(glyphs)], o, null));
            }
        }

        return c;
    }

    private static string Describe(PdfPageGlyphs[] pages, PdfTextOptions o, PageStructure? ps)
    {
        List<PdfPageText> result = [];
        foreach (PdfPageGlyphs p in pages)
        {
            result.Add(PdfTextExtractor.AnalyzeCore(p, o, ps, ps is not null, CancellationToken.None));
        }

        PdfTextExtractor.FlagHeadersAndFooters(result);
        StringBuilder sb = new();
        foreach (PdfPageText p in result)
        {
            foreach (PdfTextMode m in Enum.GetValues<PdfTextMode>())
            {
                sb.Append(p.GetText(m)).Append('\u0001');
            }

            sb.Append(p.UsedStructureTree).Append(p.Diagnostics.Count).Append('\u0001');
            foreach (PdfTextWord w in p.Words)
            {
                sb.Append(w.IsVisible).Append(string.Join(',', w.GlyphIndices)).Append('|');
            }
        }

        sb.Append(PdfExtractionJson.ToJson(result));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static Dictionary<string, string> LoadGolden()
    {
        using Stream? s = typeof(GoldenTests).Assembly.GetManifestResourceStream("golden.txt");
        Assert.NotNull(s);
        using StreamReader r = new(s);
        Dictionary<string, string> d = [];
        while (r.ReadLine() is { } line)
        {
            int sp = line.IndexOf(' ', StringComparison.Ordinal);
            if (sp > 0)
            {
                d[line[..sp]] = line[(sp + 1)..];
            }
        }

        return d;
    }

    [Fact]
    public void OutputMatchesTheRecordedHashes()
    {
        var cases = Cases();
        string? update = Environment.GetEnvironmentVariable("ONEPDF_UPDATE_GOLDEN");
        if (!string.IsNullOrEmpty(update))
        {
            File.WriteAllLines(update, cases.Select(x => x.Name + " " + Describe(x.Pages, x.Options, x.Structure)));
            return;
        }

        Dictionary<string, string> golden = LoadGolden();
        Assert.Equal(cases.Count, golden.Count);
        List<string> bad = [];
        foreach (var x in cases)
        {
            if (!golden.TryGetValue(x.Name, out string? h) || h != Describe(x.Pages, x.Options, x.Structure))
            {
                bad.Add(x.Name);
            }
        }

        Assert.True(bad.Count == 0, "output changed for: " + string.Join(", ", bad));
    }
}
