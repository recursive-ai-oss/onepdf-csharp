using System.Diagnostics;
using System.Text;
using OnePdf.Text;
using UglyToad.PdfPig;

namespace OnePdf.Tests.Redaction;

/// <summary>
/// Redacts words chosen from what extraction finds on real and generated files, saves, reopens and checks that the words are gone from
/// every reading of the file while the other words have not moved.
/// </summary>
public class RedactionCorpusTests
{
    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (string? d = Path.GetDirectoryName(source); d is not null; d = Path.GetDirectoryName(d))
        {
            if (Directory.Exists(Path.Combine(d, "testdata")))
            {
                return d;
            }
        }

        throw new DirectoryNotFoundException("testdata");
    }

    public static IEnumerable<object[]> Files()
    {
        string root = Root();
        bool all = Environment.GetEnvironmentVariable("ONEPDF_REDACT_ALL") == "1";
        var seen = new HashSet<string>();
        foreach (string f in Directory.EnumerateFiles(Path.Combine(root, "testdata", "realworld", "pdf"), "*.pdf").Order(StringComparer.Ordinal))
        {
            string cat = Path.GetFileName(f).Split('-')[0];
            if (all || seen.Add(cat))
            {
                yield return new object[] { Path.GetRelativePath(root, f).Replace('\\', '/') };
            }
        }

        foreach (string f in Directory.EnumerateFiles(Path.Combine(root, "testdata", "generated", "pdf"), "*.pdf").Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(f);
            if (name.StartsWith("enc-", StringComparison.Ordinal) || name.StartsWith("lo-encrypted", StringComparison.Ordinal) || name.Contains("broken", StringComparison.Ordinal))
            {
                continue;
            }

            if (all || name.StartsWith("chrome-", StringComparison.Ordinal) || name.StartsWith("lo-", StringComparison.Ordinal) || name.StartsWith("tex-", StringComparison.Ordinal) || name.StartsWith("raw-base", StringComparison.Ordinal))
            {
                yield return new object[] { Path.GetRelativePath(root, f).Replace('\\', '/') };
            }
        }
    }

    // Upright extraction space (y up, rotation and box applied) back to default user space.
    private static PdfRectangle ToUser(PdfPage page, PdfRectangle r)
    {
        PdfRectangle box = page.CropBox;
        double w = box.Width, h = box.Height;
        (double X, double Y) P(double x, double y) => page.Rotate switch
        {
            90 => (box.Left + w - y, box.Bottom + x),
            180 => (box.Left + w - x, box.Bottom + h - y),
            270 => (box.Left + y, box.Bottom + h - x),
            _ => (box.Left + x, box.Bottom + y),
        };
        var pts = new[] { P(r.Left, r.Bottom), P(r.Right, r.Bottom), P(r.Right, r.Top), P(r.Left, r.Top) };
        return new PdfRectangle(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y));
    }

    private static bool Overlaps(PdfRectangle a, PdfRectangle b) => a.Left < b.Right && a.Right > b.Left && a.Bottom < b.Top && a.Top > b.Bottom;

    [Theory]
    [MemberData(nameof(Files))]
    public void RedactedWordsAreGoneAndTheRestStaysPut(string relative)
    {
        byte[] pdf = File.ReadAllBytes(Path.Combine(Root(), relative));
        using PdfDocument doc = PdfDocument.Open(pdf, new PdfOpenOptions { AllowEditing = true });
        int pages = Math.Min(doc.Pages.Count, 2);
        string structural = StructuralText(doc);
        var regions = new List<PdfRedactionRegion>();
        var gone = new List<(int Page, string Word, PdfRectangle Upright)>();
        var before = new List<List<PdfTextWord>>();
        var beforeGlyphs = new List<IReadOnlyList<PdfTextGlyph>>();
        var shrunkBoxes = new List<(int Page, PdfRectangle Box)>();
        var beforeText = new Dictionary<(int, PdfTextMode), string>();
        string all = string.Empty;
        foreach (PdfPageText t in doc.ExtractText())
        {
            all += " " + t.Text;
        }

        for (int p = 0; p < pages; p++)
        {
            PdfPageText text = doc.Pages[p].ExtractText();
            List<PdfTextWord> words = [.. text.Words.Where(w => w.IsVisible)];
            before.Add(words);
            foreach (PdfTextMode mode in Enum.GetValues<PdfTextMode>())
            {
                beforeText[(p, mode)] = doc.Pages[p].ExtractText(new PdfTextOptions { Mode = mode }).Text;
            }

            beforeGlyphs.Add(doc.Pages[p].ExtractText(new PdfTextOptions { RetainGlyphs = true }).Glyphs);
            int taken = 0;
            foreach (PdfTextWord w in words)
            {
                if (taken == 3 || w.Text.Length < 5 || !w.Text.All(char.IsLetter) || CountOf(all, w.Text) != 1 || structural.Contains(w.Text, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                PdfRectangle b = w.Bounds;
                double inH = 0.3, inV = b.Height * 0.15;
                if (b.Width <= 2 * inH + 1 || b.Height <= 2 * inV + 1 || gone.Any(g => g.Page == p && Overlaps(g.Upright, b)))
                {
                    continue;
                }

                var shrunk = new PdfRectangle(b.Left + inH, b.Bottom + inV, b.Right - inH, b.Top - inV);
                regions.Add(new PdfRedactionRegion(p, ToUser(doc.Pages[p], shrunk)));
                gone.Add((p, w.Text, b));
                shrunkBoxes.Add((p, shrunk));
                taken++;
            }
        }

        if (regions.Count == 0)
        {
            Assert.Skip("No suitable word on the first pages.");
        }

        PdfRedactionResult r = doc.Redact(regions, new PdfRedactionOptions { MetadataTerms = [.. gone.Select(g => g.Word)], RemoveJavaScript = true, RemoveEmbeddedFiles = true });
        Assert.True(r.GlyphsRemoved > 0, relative);
        using var ms = new MemoryStream();
        doc.Save(ms, new PdfSaveOptions { XrefFormat = PdfXrefFormat.Table, UseObjectStreams = false });
        byte[] saved = ms.ToArray();

        using PdfDocument reopened = PdfDocument.Open(saved);

        // A page without areas is drawn exactly as before.
        if (doc.Pages.Count > pages)
        {
            // Saving renumbers objects, so references are compared by shape only.
            static string Norm(string s) => System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(s, @"/Length (\d+ \d+ R|\d+)", "/Length N"), @"\d+ \d+ R", "N R").TrimEnd();
            Assert.Equal(Norm(RedactionSupport.DisplayList(pdf, pages)), Norm(RedactionSupport.DisplayList(saved, pages)));
        }

        for (int p = 0; p < pages; p++)
        {
            string[] banned = [.. gone.Where(g => g.Page == p).Select(g => g.Word)];
            foreach (PdfTextMode mode in Enum.GetValues<PdfTextMode>())
            {
                string text = reopened.Pages[p].ExtractText(new PdfTextOptions { Mode = mode }).Text;
                foreach (string w in banned)
                {
                    // Another mode may lay the same page out so that the word occurs somewhere else too; the redacted occurrence must be gone.
                    Assert.True(CountOf(text, w) < Math.Max(1, CountOf(beforeText[(p, mode)], w)), $"{relative} page {p + 1} {mode}: '{w}' is still extracted: ...{Around(text, w)}...");
                }
            }

            PdfPageText after = reopened.Pages[p].ExtractText();
            string json = System.Text.RegularExpressions.Regex.Replace(PdfExtractionJson.ToJson([after]), "\"font[A-Za-z]*\":\"[^\"]*\"", string.Empty);
            foreach (string w in banned)
            {
                Assert.False(json.Replace("\"" + w + "\":", string.Empty).Contains(w, StringComparison.Ordinal), $"{relative} page {p + 1} JSON: '{w}' is still there");
            }

            // Every glyph that is not near a redacted word is where it was.
            IReadOnlyList<PdfTextGlyph> afterGlyphs = reopened.Pages[p].ExtractText(new PdfTextOptions { RetainGlyphs = true }).Glyphs;
            var hitBoxes = shrunkBoxes.Where(g => g.Page == p).Select(g => g.Box).ToList();
            int missing = 0, checkedGlyphs = 0;
            string? firstMissing = null;
            foreach (PdfTextGlyph g in beforeGlyphs[p])
            {
                PdfRectangle gb = g.Bounds;
                if (!g.IsVisible || hitBoxes.Any(h => gb.Left < h.Right + 0.02 && gb.Right > h.Left - 0.02 && gb.Bottom < h.Top + 0.02 && gb.Top > h.Bottom - 0.02))
                {
                    continue;
                }

                checkedGlyphs++;
                bool found = false;
                foreach (PdfTextGlyph a in afterGlyphs)
                {
                    if (Math.Abs(a.Matrix.E - g.Matrix.E) <= 0.01 && Math.Abs(a.Matrix.F - g.Matrix.F) <= 0.01 && a.Text == g.Text)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    missing++;
                    firstMissing ??= $"'{g.Text}' at {g.Matrix.E:0.###},{g.Matrix.F:0.###}";
                }
            }

            Assert.True(missing == 0, $"{relative} page {p + 1}: {missing} of {checkedGlyphs} untouched glyphs moved or vanished; first: {firstMissing}");
        }

        // PdfPig reads the saved file too.
        using (UglyToad.PdfPig.PdfDocument pp = UglyToad.PdfPig.PdfDocument.Open(saved))
        using (UglyToad.PdfPig.PdfDocument pp0 = UglyToad.PdfPig.PdfDocument.Open(pdf))
        {
            for (int p = 0; p < pages; p++)
            {
                string text = pp.GetPage(p + 1).Text;
                foreach (string w in gone.Where(g => g.Page == p).Select(g => g.Word))
                {
                    Assert.True(CountOf(text, w) < Math.Max(1, CountOf(pp0.GetPage(p + 1).Text, w)), $"{relative} page {p + 1}: PdfPig still reads '{w}'");
                }
            }
        }

        // pdftotext, where installed, is a second opinion.
        if (File.Exists("/usr/bin/pdftotext"))
        {
            string tmp = Path.Combine(Path.GetTempPath(), "onepdf-redact-" + Guid.NewGuid().ToString("N") + ".pdf");
            File.WriteAllBytes(tmp, saved);
            try
            {
                var psi = new ProcessStartInfo("/usr/bin/pdftotext", ["-f", "1", "-l", pages.ToString(), tmp, "-"]) { RedirectStandardOutput = true, RedirectStandardError = true };
                using Process proc = Process.Start(psi)!;
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                string beforeOutput = PdfToText(pdf, pages);
                foreach ((int _, string w, PdfRectangle _) in gone)
                {
                    // Text that extraction leaves out (clipped, off the page) may repeat a word; the redacted occurrence must be gone.
                    if (CountOf(beforeOutput, w) > 0)
                    {
                        Assert.True(CountOf(output, w) < CountOf(beforeOutput, w), $"{relative}: pdftotext still reads '{w}'");
                    }
                    if (CountOf(beforeOutput, w) == 1)
                    {
                        Assert.Equal(0, CountOf(output, w));
                    }
                }
            }
            finally
            {
                File.Delete(tmp);
            }
        }

        // A search of every decoded byte for the words (as written, hex, UTF-16).
        byte[] bytes = RedactionSupport.Everything(saved, skipFonts: true);
        byte[] bytesBefore = RedactionSupport.Everything(pdf, skipFonts: true);
        foreach ((int _, string w, PdfRectangle _) in gone)
        {
            // A word that is also part of text extraction does not see (clipped text, another word that contains it) stays there.
            int afterCount = RedactionSupport.Count(bytes, w), beforeCount = RedactionSupport.Count(bytesBefore, w);
            Assert.True(beforeCount == 0 ? afterCount == 0 : afterCount < beforeCount, $"{relative}: '{w}' occurs {afterCount} times in the file's bytes after, {beforeCount} before; in object(s) {Holders(saved, w)}");
        }
    }

    // Every name and string of the document's dictionaries: font names, destinations, titles. Words in them are not page text.
    private static string StructuralText(PdfDocument d)
    {
        var sb = new StringBuilder();
        var stack = new Stack<PdfObject>();
        for (int n = 1; n <= d.ObjectCount; n++)
        {
            PdfObject? o = d.GetObject(n);
            if (o is PdfStream st)
            {
                o = st.Dictionary;
            }

            if (o is not null)
            {
                stack.Push(o);
            }

            int budget = 100000;
            while (stack.Count > 0 && budget-- > 0)
            {
                switch (stack.Pop())
                {
                    case PdfName name:
                        sb.Append(name.Value).Append(' ');
                        break;
                    case PdfString s:
                        sb.Append(s.GetText()).Append(' ');
                        break;
                    case PdfDictionary dict:
                        foreach (string k in dict.Keys)
                        {
                            sb.Append(k).Append(' ');
                            if (dict.GetRaw(k) is { } v and not PdfReference)
                            {
                                stack.Push(v);
                            }
                        }

                        break;
                    case PdfArray arr:
                        for (int i = 0; i < arr.Count; i++)
                        {
                            if (arr.GetRaw(i) is not PdfReference)
                            {
                                stack.Push(arr.GetRaw(i));
                            }
                        }

                        break;
                }
            }

            stack.Clear();
        }

        return sb.ToString();
    }

    private static string Holders(byte[] saved, string word)
    {
        using PdfDocument d = PdfDocument.Open(saved);
        var found = new List<string>();
        for (int n = 1; n <= d.ObjectCount; n++)
        {
            if (d.GetObject(n) is not PdfStream st)
            {
                continue;
            }

            try
            {
                if (RedactionSupport.Contains(st.GetDecodedBytes().ToArray(), word))
                {
                    found.Add(n.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            catch (PdfException)
            {
            }
        }

        return found.Count == 0 ? "the file body outside streams" : string.Join(",", found);
    }

    private static string PdfToText(byte[] file, int pages)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "onepdf-redact-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(tmp, file);
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/pdftotext", ["-f", "1", "-l", pages.ToString(), tmp, "-"]) { RedirectStandardOutput = true, RedirectStandardError = true };
            using Process proc = Process.Start(psi)!;
            string o = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return o;
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    private static string Around(string t, string w)
    {
        int i = t.IndexOf(w, StringComparison.Ordinal);
        return i < 0 ? string.Empty : t.Substring(Math.Max(0, i - 40), Math.Min(100, t.Length - Math.Max(0, i - 40))).Replace("\n", "/");
    }

    private static string Letters(string s) => new([.. s.Where(char.IsLetterOrDigit)]);

    private static int CountOf(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal))
        {
            n++;
        }

        return n;
    }
}
