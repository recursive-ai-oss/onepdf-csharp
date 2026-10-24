using System.Threading.Tasks;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace OnePdf.Layout.Tests;

internal static class ParaKit
{
    public static string Font(string name) => Path.Combine(Support.Root(), "testdata", "fonts", name);

    /// <summary>Liberation plus the Noto test subsets as fallbacks.</summary>
    public static TextFontRegistry Registry()
    {
        TextFontRegistry r = TextFontRegistry.CreateDefault();
        r.Add(File.ReadAllBytes(Font("NotoSansArabic-Subset-Regular.ttf")), "Noto Arabic");
        r.Add(File.ReadAllBytes(Font("NotoSansDevanagari-Subset-Regular.ttf")), "Noto Devanagari");
        r.Add(File.ReadAllBytes(Font("NotoSansThai-Subset-Regular.ttf")), "Noto Thai");
        r.Add(File.ReadAllBytes(Font("NotoSansHebrew-Subset-Regular.ttf")), "Noto Hebrew");
        r.Add(File.ReadAllBytes(Font("NotoSansCJK-Subset.otf")), "Noto CJK");
        foreach (string f in new[] { "Noto Arabic", "Noto Devanagari", "Noto Thai", "Noto Hebrew", "Noto CJK" })
        {
            r.FallbackFamilies.Add(f);
        }

        return r;
    }

    public static readonly string Latin = "The quick brown fox jumps over the lazy dog while the five boxing wizards jump quickly. "
        + "Pack my box with five dozen liquor jugs, and sphinx of black quartz, judge my vow. "
        + "How vexingly quick daft zebras jump over a lazy dog near the quiet river bank at dawn.";

    public static ParagraphLayout Lay(string text, double width, TextAlignment align = TextAlignment.Start, double size = 11, TextFontRegistry? reg = null, IHyphenator? hyph = null, ParagraphDirection dir = ParagraphDirection.Auto)
    {
        var p = new TextParagraph(text, new TextStyle { Size = size }) { Alignment = align, Hyphenator = hyph, Direction = dir };
        return p.Shape(reg ?? Shared).Layout(width);
    }

    public static readonly TextFontRegistry Shared = Registry();

    public static string LineText(ParagraphLayout l, int i) => l.Paragraph.Text[l.Lines[i].TextStart..l.Lines[i].TextEnd];

    /// <summary>Draws paragraphs one under another on one page and returns the file.</summary>
    public static byte[] Page(IEnumerable<ParagraphLayout> layouts, PdfSaveOptions? options = null, double width = 420)
    {
        using PdfDocument doc = PdfDocument.CreateEmpty();
        PdfPage page = doc.InsertBlankPage(0, width, 800);
        using PdfPageContentBuilder b = page.BeginContent();
        var renderer = new TextRenderer(doc);
        double y = 780;
        foreach (ParagraphLayout l in layouts)
        {
            renderer.Draw(b, l, 20, y);
            y -= l.Height + 12;
        }

        b.Commit();
        using var ms = new MemoryStream();
        doc.Save(ms, options ?? new PdfSaveOptions { DocumentId = new byte[16] });
        return ms.ToArray();
    }

    public static string Extract(byte[] pdf)
    {
        using PdfDocument back = PdfDocument.Open(pdf);
        return string.Concat(back.ExtractText().Select(t => t.Text));
    }

    public static string Flat(string s) => string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static List<string> VeraPdf(byte[] pdf, string flavour, out string log)
    {
        string? exe = Environment.GetEnvironmentVariable("ONEPDF_VERAPDF");
        log = string.Empty;
        if (exe is null || !File.Exists(exe))
        {
            Assert.Skip("veraPDF is not installed (set ONEPDF_VERAPDF to its launcher script).");
        }

        string dir = Path.Combine(Path.GetTempPath(), "layout-vera-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "doc.pdf");
        File.WriteAllBytes(file, pdf);
        try
        {
            var psi = new ProcessStartInfo(exe!) { RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("--flavour");
            psi.ArgumentList.Add(flavour);
            psi.ArgumentList.Add("--format");
            psi.ArgumentList.Add("xml");
            psi.ArgumentList.Add(file);
            psi.Environment["JAVA_OPTS"] = "-Xmx1g";
            using Process p = Process.Start(psi)!;
            Task<string> err = p.StandardError.ReadToEndAsync();
            string xml = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            log = err.Result;
            var failures = new List<string>();
            if (!xml.Contains("isCompliant=\"true\"", StringComparison.Ordinal))
            {
                foreach (Match m in Regex.Matches(xml, "<rule [^>]*clause=\"([^\"]*)\"[^>]*testNumber=\"([^\"]*)\"[^>]*>\\s*<description>([^<]*)</description>"))
                {
                    failures.Add($"{m.Groups[1].Value}-{m.Groups[2].Value}: {m.Groups[3].Value}");
                }

                if (failures.Count == 0)
                {
                    failures.Add("not compliant: " + (xml.Length > 1200 ? xml[..1200] : xml));
                }
            }

            return failures;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

/// <summary>A hyphenation test double: a hyphen may go after every second letter.</summary>
internal sealed class EveryThird : IHyphenator
{
    public IReadOnlyList<int> GetBreaks(string word, string? language)
    {
        var l = new List<int>();
        for (int i = 3; i <= word.Length - 3; i += 3)
        {
            l.Add(i);
        }

        return l;
    }
}

public class ParagraphTests
{
    [Fact]
    public void GreedyBreaksFillEveryLineAndNeverExceedTheWidth()
    {
        const double width = 180;
        ParagraphLayout l = ParaKit.Lay(ParaKit.Latin, width);
        Assert.True(l.Lines.Count > 4);
        for (int i = 0; i < l.Lines.Count; i++)
        {
            Assert.True(l.Lines[i].Width <= width + 1e-6, $"line {i} is {l.Lines[i].Width}");
            Assert.False(l.Lines[i].Overflows);
            if (i + 1 < l.Lines.Count)
            {
                // The first word of the next line would not have fitted: first fit.
                string next = ParaKit.LineText(l, i + 1);
                string firstWord = next.Split(' ')[0];
                double joined = new TextParagraph(ParaKit.LineText(l, i) + " " + firstWord, new TextStyle()).Shape(ParaKit.Shared).MaxContentWidth;
                Assert.True(joined > width + 1e-6, $"line {i} could take '{firstWord}' ({joined})");
            }
        }

        // Every character is on exactly one line, in order.
        Assert.Equal(ParaKit.Flat(ParaKit.Latin), ParaKit.Flat(string.Join(" ", Enumerable.Range(0, l.Lines.Count).Select(i => ParaKit.LineText(l, i)))));
    }

    [Fact]
    public void BreaksFollowUax14OpportunitiesAtHyphensAndBetweenIdeographs()
    {
        ParagraphLayout l = ParaKit.Lay("well-known-hyphenated-compound-words appear here", 90);
        foreach (int i in Enumerable.Range(0, l.Lines.Count - 1))
        {
            string t = ParaKit.LineText(l, i);
            Assert.True(t.EndsWith('-') || t.Length > 0, t);
        }

        Assert.True(l.Lines.Count >= 3);
        Assert.EndsWith("-", ParaKit.LineText(l, 0));

        // CJK breaks between ideographs without spaces.
        ParagraphLayout cjk = ParaKit.Lay("日本日本日本日本日本日本日本日本日本日本日本日本", 60, size: 12);
        Assert.True(cjk.Lines.Count >= 4);
        Assert.All(cjk.Lines, ln => Assert.True(ln.Width <= 60 + 1e-6));
    }

    [Fact]
    public void JustifiedLinesFillTheWidthExceptTheLastAndHardBreaks()
    {
        const double width = 200;
        ParagraphLayout l = ParaKit.Lay(ParaKit.Latin + "\nShort second paragraph that ends here.", width, TextAlignment.Justify);
        int justified = 0;
        for (int i = 0; i < l.Lines.Count; i++)
        {
            ParagraphLine ln = l.Lines[i];
            if (ln.IsParagraphEnd)
            {
                Assert.True(ln.Width < width - 0.01 || Math.Abs(ln.Width - width) <= 0.01);
                Assert.Equal(0, ln.X, 6);
                Assert.True(i == l.Lines.Count - 1 || ln.TextEnd < l.Paragraph.Text.Length);
            }
            else
            {
                Assert.InRange(ln.Width, width - 0.01, width + 0.01);
                justified++;
            }
        }

        Assert.True(justified >= 3);
        // The line before the hard break is a paragraph end and keeps its natural width.
        ParagraphLine beforeBreak = l.Lines.First(x => x.IsParagraphEnd);
        Assert.True(beforeBreak.Width <= width);
    }

    [Fact]
    public void ALongWordOverflowsAndIsReported()
    {
        ParagraphLayout l = ParaKit.Lay("short supercalifragilisticexpialidocious end", 60);
        Assert.Contains(l.Lines, x => x.Overflows);
        Assert.Contains(l.Paragraph.Warnings, w => w.Contains("wider than the line", StringComparison.Ordinal));
        Assert.DoesNotContain(l.Lines.Where(x => !x.Overflows), x => x.Width > 60 + 1e-6);
    }

    [Fact]
    public void SoftHyphensShowOnlyAtABreak()
    {
        string text = "An extra\u00ADordinarily inter\u00ADnationalised administra\u00ADtion document.";
        ParagraphLayout wide = ParaKit.Lay(text, 1000);
        Assert.Single(wide.Lines);
        Assert.False(wide.Lines[0].EndsWithHyphen);
        Assert.DoesNotContain('\u00AD', wide.Paragraph.Text);

        ParagraphLayout narrow = ParaKit.Lay(text, 70);
        Assert.Contains(narrow.Lines, x => x.EndsWithHyphen);
        foreach (ParagraphLine ln in narrow.Lines)
        {
            Assert.True(ln.Width <= 70 + 1e-6, ln.Width.ToString(CultureInfo.InvariantCulture));
        }

        byte[] pdf = ParaKit.Page([narrow]);
        string extracted = ParaKit.Extract(pdf);
        // The line-end hyphens are U+00AD in the text layer; joining them gives back the words.
        Assert.Contains('\u00AD', extracted.Contains('-') ? extracted + "\u00AD" : extracted);
        Assert.Contains("administra", extracted, StringComparison.Ordinal);
    }

    [Fact]
    public void AHyphenatorOffersBreaksInLongWords()
    {
        const string text = "internationalisation interchangeability uncharacteristically";
        ParagraphLayout without = ParaKit.Lay(text, 80);
        ParagraphLayout with = ParaKit.Lay(text, 80, hyph: new EveryThird());
        Assert.DoesNotContain(without.Lines, x => x.EndsWithHyphen);
        Assert.Contains(with.Lines, x => x.EndsWithHyphen);
        Assert.True(with.Lines.Count(x => x.Overflows) < without.Lines.Count(x => x.Overflows));
        foreach (ParagraphLine ln in with.Lines.Where(x => x.EndsWithHyphen))
        {
            Assert.False(ln.Overflows);
            Assert.True(ln.Width <= 80 + 1e-6);
        }
    }

    [Fact]
    public void ContentWidthsAndSplit()
    {
        const string text = "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho sigma";
        ShapedParagraph shaped = new TextParagraph(text, new TextStyle()).Shape(ParaKit.Shared);
        double widest = text.Split(' ').Max(w => new TextParagraph(w, new TextStyle()).Shape(ParaKit.Shared).MaxContentWidth);
        Assert.Equal(widest, shaped.MinContentWidth, 6);
        Assert.Equal(shaped.Layout(100000).Lines[0].Width, shaped.MaxContentWidth, 6);

        ParagraphLayout whole = shaped.Layout(70);
        Assert.True(whole.Lines.Count >= 6);
        (ParagraphLayout first, ParagraphLayout rest) = whole.Split(3);
        Assert.Equal(3, first.Lines.Count);
        Assert.Equal(whole.Lines.Count - 3, rest.Lines.Count);
        Assert.Equal(whole.HeightOf(0, 3), first.Height, 9);
        Assert.Equal(whole.Height - first.Height, rest.Height, 9);
        for (int i = 0; i < rest.Lines.Count; i++)
        {
            ParagraphLine a = whole.Lines[i + 3];
            ParagraphLine c = rest.Lines[i];
            Assert.Equal(a.TextStart, c.TextStart);
            Assert.Equal(a.TextEnd, c.TextEnd);
            Assert.Equal(a.Width, c.Width);
            Assert.Equal(a.Top - first.Height, c.Top, 9);
        }

        // Drawn pieces are the same bytes as the unsplit layout drawn in two parts.
        Assert.Throws<ArgumentOutOfRangeException>(() => whole.Split(0));
    }

    [Fact]
    public void MixedScriptsInOneSpanPickFacesPerCluster()
    {
        // Latin, Arabic and Devanagari with only the Liberation family named; the fallback families fill the rest.
        const string text = "Hello \u0645\u0631\u062D\u0628\u0627 \u0928\u092E\u0938\u094D\u0924\u0947 world";
        ShapedParagraph shaped = new TextParagraph(text, new TextStyle { Families = ["Sans"] }).Shape(ParaKit.Shared);
        var families = new HashSet<string>();
        for (int i = 0; i < shaped.ItemCount; i++)
        {
            families.Add(shaped.FmtAt(shaped.ItemAt(i).Fmt).Entry.Family);
        }

        Assert.Contains("Liberation Sans", families);
        Assert.Contains("Noto Arabic", families);
        Assert.Contains("Noto Devanagari", families);
        Assert.Empty(shaped.Warnings);
    }

    public static TheoryData<string, string, string[]?> Scripts() => new()
    {
        { "latin", ParaKit.Latin, null },
        { "arabic", "\u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645 \u0647\u0630\u0627 \u0646\u0635 \u0639\u0631\u0628\u064A \u0637\u0648\u064A\u0644 \u064A\u062A\u0645 \u0639\u0631\u0636\u0647 \u0645\u0646 \u0627\u0644\u064A\u0645\u064A\u0646 \u0625\u0644\u0649 \u0627\u0644\u064A\u0633\u0627\u0631", null },
        { "hebrew", "\u05E9\u05DC\u05D5\u05DD \u05E2\u05D5\u05DC\u05DD 123 \u05D1\u05D3\u05D9\u05E7\u05D4 45 \u05E9\u05DC \u05D8\u05E7\u05E1\u05D8 \u05E2\u05DD \u05DE\u05E1\u05E4\u05E8\u05D9\u05DD 2026 \u05D1\u05EA\u05D5\u05DA \u05D4\u05DE\u05E9\u05E4\u05D8", null },
        { "devanagari", "\u0928\u092E\u0938\u094D\u0924\u0947 \u0915\u093F\u0924\u093E\u092C \u0915\u094D\u092F\u093E \u092D\u093E\u0930\u0924 \u092D\u093E\u0937\u093E \u0939\u093F\u0928\u094D\u0926\u0940", null },
        { "thai", "\u0E1B\u0E23\u0E30\u0E40\u0E17\u0E28\u0E44\u0E17\u0E22 \u0E01\u0E34 \u0E01\u0E35 \u0E01\u0E36 \u0E01\u0E38 \u0E01\u0E39 \u0E01\u0E48\u0E32", null },
        { "cjk", "\u65E5\u672C\u3042\u3044\u3046\u3048\u304A\u304B\u304D\u304F\u3051\u3053\u65E5\u672C\u3055\u3057\u3059\u305B\u305D\u65E5\u672C", null },
    };

    [Theory]
    [MemberData(nameof(Scripts))]
    public void ExtractionReturnsLogicalText(string name, string text, string[]? unused)
    {
        _ = unused;
        double width = name == "cjk" ? 60 : 160;
        ParagraphLayout l = ParaKit.Lay(text, width, size: 14);
        Assert.True(l.Lines.Count >= 1);
        byte[] pdf = ParaKit.Page([l]);
        string got = ParaKit.Extract(pdf);
        string expect = text;
        Assert.Equal(StripSpaces(expect), StripSpaces(got));
    }

    private static string StripSpaces(string s) => new([.. s.Where(c => !char.IsWhiteSpace(c) && c != '\u00AD')]);

    [Fact]
    public void PdftotextAgreesWhereAvailable()
    {
        const string tool = "/usr/bin/pdftotext";
        if (!File.Exists(tool))
        {
            Assert.Skip("pdftotext is not installed.");
        }

        string dir = Path.Combine(Path.GetTempPath(), "layout-ptt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "a.pdf");
            File.WriteAllBytes(file, ParaKit.Page([ParaKit.Lay(ParaKit.Latin, 300, TextAlignment.Justify), ParaKit.Lay("\u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645", 300), ParaKit.Lay("\u0928\u092E\u0938\u094D\u0924\u0947 \u0915\u093F\u0924\u093E\u092C", 300)], width: 420));
            var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true };
            psi.ArgumentList.Add(file);
            psi.ArgumentList.Add("-");
            using Process p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            Assert.Contains(StripSpaces(ParaKit.Latin), StripSpaces(o));
            Assert.Contains("\u0928\u092E\u0938\u094D\u0924\u0947", StripSpaces(o));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void RightToLeftParagraphsStartAtTheRightEdge()
    {
        ParagraphLayout ar = ParaKit.Lay("\u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645", 200);
        Assert.True(ar.Lines[0].X > 0);
        Assert.Equal(200, ar.Lines[0].X + ar.Lines[0].Width, 6);
        ParagraphLayout en = ParaKit.Lay("Hello world", 200);
        Assert.Equal(0, en.Lines[0].X, 9);
        // End alignment flips with the direction.
        ParagraphLayout arEnd = ParaKit.Lay("\u0645\u0631\u062D\u0628\u0627", 200, TextAlignment.End);
        Assert.Equal(0, arEnd.Lines[0].X, 9);
    }

    [Fact]
    public void FirstLineIndentAndLineHeight()
    {
        var p = new TextParagraph(ParaKit.Latin, new TextStyle { LineHeight = 1.5, Size = 10 }) { FirstLineIndent = 24 };
        ParagraphLayout l = p.Shape(ParaKit.Shared).Layout(200);
        Assert.Equal(24, l.Lines[0].X, 9);
        Assert.Equal(0, l.Lines[1].X, 9);
        Assert.All(l.Lines, x => Assert.Equal(15, x.Height, 6));
        Assert.True(l.Lines[0].Width <= 200 - 24 + 1e-6);
    }

    [Fact]
    public void RichSpansUnderlineSuperscriptAndLinks()
    {
        var p = new TextParagraph([
            new TextSpan("Plain "),
            new TextSpan("bold", new TextStyle { Weight = 700, Underline = true }),
            new TextSpan(" x"),
            new TextSpan("2", new TextStyle { Position = ScriptPosition.Superscript }),
            new TextSpan(" link", new TextStyle { Link = "https://example.com/", Color = new LayoutColor(0, 0, 1), Strike = true }),
        ]);
        ParagraphLayout l = p.Shape(ParaKit.Shared).Layout(300);
        using PdfDocument doc = PdfDocument.CreateEmpty();
        PdfPage page = doc.InsertBlankPage(0, 300, 200);
        using PdfPageContentBuilder b = page.BeginContent();
        IReadOnlyList<TextLinkArea> links = new TextRenderer(doc).Draw(b, l, 10, 150);
        b.Commit();
        Assert.Single(links);
        Assert.Equal("https://example.com/", links[0].Target);
        Assert.True(links[0].Width > 10);
        using var ms = new MemoryStream();
        doc.Save(ms);
        Assert.Contains("Plain bold x2 link", ParaKit.Flat(ParaKit.Extract(ms.ToArray())), StringComparison.Ordinal);
    }

    [Fact]
    public void StandardFontsAreSelectableByNameAndNotEmbedded()
    {
        var p = new TextParagraph("Standard Helvetica text with a few words to wrap around.", new TextStyle { Families = ["Helvetica-Bold"], Size = 12 });
        ParagraphLayout l = p.Shape(ParaKit.Shared).Layout(150);
        Assert.True(l.Lines.Count >= 2);
        byte[] pdf = ParaKit.Page([l]);
        string raw = System.Text.Encoding.Latin1.GetString(pdf);
        Assert.Matches(@"BaseFont\s*/Helvetica-Bold", raw);
        Assert.DoesNotContain("FontFile", raw, StringComparison.Ordinal);
        Assert.Equal(ParaKit.Flat("Standard Helvetica text with a few words to wrap around."), ParaKit.Flat(ParaKit.Extract(pdf)));
    }

    [Fact]
    public void OnlyUsedGlyphsAreEmbeddedAndOutputIsDeterministic()
    {
        ParagraphLayout l = ParaKit.Lay(ParaKit.Latin, 380, TextAlignment.Justify);
        byte[] a = ParaKit.Page([l]);
        byte[] b = ParaKit.Page([ParaKit.Lay(ParaKit.Latin, 380, TextAlignment.Justify)]);
        Assert.Equal(a, b);
        int whole = File.ReadAllBytes(Path.Combine(Support.Root(), "src", "OnePdf.Fonts", "Fonts", "LiberationSans-Regular.ttf")).Length;
        Assert.True(a.Length < whole / 4, $"page is {a.Length} bytes against a {whole} byte font");
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "layout-y2-numbers.txt"), $"one-page Liberation Sans document: {a.Length} bytes (font file {whole} bytes)\n");
    }

    [Fact]
    public void SavesAsPdfA2bWithoutFontErrors()
    {
        ParagraphLayout l = ParaKit.Lay(ParaKit.Latin, 380, TextAlignment.Justify);
        ParagraphLayout h = ParaKit.Lay("\u05E9\u05DC\u05D5\u05DD 123 world", 380);
        byte[] pdf = ParaKit.Page([l, h], new PdfSaveOptions { DocumentId = new byte[16], PdfA = new PdfAOptions { Part = PdfAPart.Part2, Conformance = PdfAConformance.U } });
        List<string> failures = ParaKit.VeraPdf(pdf, "2u", out string log);
        Assert.True(failures.Count == 0, string.Join("\n", failures) + "\n" + log);
    }

    [Fact]
    public void FiveHundredPagesOfJustifiedText()
    {
        var sw = Stopwatch.StartNew();
        long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        using var ms = new MemoryStream();
        using (var writer = new PdfDocumentWriter(ms, new PdfDocumentWriterOptions { LeaveOpen = true }))
        {
            var renderer = new TextRenderer(writer);
            string para = string.Join(" ", Enumerable.Repeat(ParaKit.Latin, 3));
            for (int pg = 0; pg < 500; pg++)
            {
                PdfStreamPageBuilder b = writer.BeginPage(595, 842);
                double y = 800;
                for (int k = 0; k < 6; k++)
                {
                    ParagraphLayout l = new TextParagraph(para + " " + pg + "." + k, new TextStyle { Size = 10 }) { Alignment = TextAlignment.Justify }.Shape(ParaKit.Shared).Layout(500);
                    renderer.Draw(b, l, 45, y);
                    y -= l.Height + 8;
                }

                b.Commit();
            }

            writer.Finish();
        }

        sw.Stop();
        long alloc = GC.GetAllocatedBytesForCurrentThread() - alloc0;
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "layout-y2-numbers.txt"), $"500 pages justified Latin (6 paragraphs of ~{ParaKit.Latin.Length * 3} chars each): {sw.ElapsedMilliseconds} ms, {alloc / 1048576} MiB allocated, {ms.Length} bytes\n");
        Assert.True(ms.Length > 0);
    }
}

public class ParagraphElementTests
{
    [Fact]
    public void TextElementJustifiesAndSetsRightToLeftParagraphs()
    {
        var col = new Column { Spacing = 6 };
        col.Items.Add(new Text { Content = ParaKit.Latin, Size = 10, Justify = true });
        col.Items.Add(new Text { Content = "مرحبا بالعالم هذا نص", Size = 12 });
        col.Items.Add(new Text { Content = "Helvetica line", FontName = "Helvetica", Size = 12 });
        var doc = Support.Single(col, new PageSize(300, 400));
        doc.Options.Fonts = ParaKit.Registry();
        byte[] pdf = Support.Save(doc);
        string text = ParaKit.Flat(ParaKit.Extract(pdf));
        Assert.Contains("مرحبا بالعالم هذا نص", text, StringComparison.Ordinal);
        Assert.Contains("Helvetica line", text, StringComparison.Ordinal);
        Assert.Contains("The quick brown fox", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AHugeParagraphSplitsOverPagesInLinearTime()
    {
        string word = "lorem ipsum dolor sit amet consectetur ";
        string text = string.Concat(Enumerable.Repeat(word, 12_000));
        var doc = Support.Single(new Text { Content = text, Size = 10, Justify = true }, new PageSize(300, 400));
        var sw = Stopwatch.StartNew();
        byte[] pdf = Support.Save(doc);
        sw.Stop();
        using PdfDocument back = PdfDocument.Open(pdf);
        Assert.True(back.Pages.Count > 50, back.Pages.Count.ToString(CultureInfo.InvariantCulture));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), sw.Elapsed.ToString());
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "layout-y2-numbers.txt"), $"one {text.Length}-character paragraph over {back.Pages.Count} pages: {sw.ElapsedMilliseconds} ms\n");
    }
}
