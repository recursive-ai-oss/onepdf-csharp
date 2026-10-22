using System.Text;
using System.Text.Json;
using OnePdf.Text;
using UglyToad.PdfPig;
using PigDocument = UglyToad.PdfPig.PdfDocument;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace OnePdf.Quality;

/// <summary>
/// Extraction quality: scores text extraction against the exact truth of the generated test set, by category, for OnePDF and for PdfPig.
/// <code>dotnet run -c Release --project tools/OnePdf.Quality -- all [--dir testdata/generated] [--md report.md] [--json report.json] [--files]</code>
/// Commands: <c>all</c> (default) runs every column, <c>pdfpig</c> only PdfPig, <c>onepdf</c> only OnePDF.
/// </summary>
internal static class Program
{
    /// <summary>Truth for one file. <see cref="Scored"/> (real-world files) lists the zero-based document pages the truth pages belong to; null means pages 0..n-1.</summary>
    private sealed record Truth(string Name, string Category, string[] Pages, string? UserPassword, string? OwnerPassword, string PdfPath, int[]? Scored)
    {
        public int DocPage(int i) => Scored is null ? i : Scored[i];
    }

    private sealed record Column(string Name, Func<string, Truth, string[]>? Extract);

    private static int Main(string[] args)
    {
        string command = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "all";
        if (command == "selftest")
        {
            return SelfTest();
        }

        if (command == "smoke")
        {
            return Smoke();
        }

        string? dir = Opt(args, "--dir");
        string? md = Opt(args, "--md");
        string? json = Opt(args, "--json");
        string set = Opt(args, "--set") ?? (dir is null ? "generated" : "dir");
        bool perFile = args.Contains("--files");
        List<(string Label, List<Truth> Truths)> sets = [];
        if (set == "dir")
        {
            sets.Add((dir!, LoadTruth(dir!)));
        }

        if (set is "generated" or "all")
        {
            string? g = FindDir("generated");
            if (g is null)
            {
                Console.Error.WriteLine("Cannot find testdata/generated; pass --dir.");
                return 2;
            }

            sets.Add(("generated set", LoadTruth(g)));
        }

        if (set is "realworld" or "all")
        {
            List<Truth> rw = [];
            string? r = FindDir("realworld");
            if (r is not null)
            {
                rw.AddRange(LoadTruth(r));
            }

            // Files whose terms do not allow committing live outside the repository; the cache directory has the same pdf/ and truth/ layout.
            string? cache = Environment.GetEnvironmentVariable("ONEPDF_QUALITY_CACHE");
            if (!string.IsNullOrEmpty(cache) && Directory.Exists(Path.Combine(cache, "truth")))
            {
                rw.AddRange(LoadTruth(cache));
            }

            if (rw.Count == 0)
            {
                Console.Error.WriteLine("Cannot find testdata/realworld; run from the repository.");
                return 2;
            }

            sets.Add(("real-world set" + (string.IsNullOrEmpty(cache) ? "" : " (committed + local-only)"), rw));
        }

        StringBuilder report = new();
        foreach ((string label, List<Truth> truths) in sets)
        {
            report.AppendLine($"=== {label} ===").AppendLine();
            RunSet(command, truths, perFile, json is null || sets.Count == 1 ? json : Path.ChangeExtension(json, null) + "-" + (label.StartsWith("real", StringComparison.Ordinal) ? "realworld" : "generated") + ".json", report);
            report.AppendLine();
        }

        Console.Write(report);
        if (md is not null)
        {
            File.WriteAllText(md, report.ToString());
        }

        return 0;
    }

    private static void RunSet(string command, List<Truth> truths, bool perFile, string? json, StringBuilder report)
    {
        List<Column> columns = [];
        if (command is "all" or "pdfpig")
        {
            columns.Add(new Column("PdfPig default", PdfPigDefault));
            columns.Add(new Column("PdfPig layout", PdfPigLayout));
        }

        if (command is "all" or "onepdf")
        {
            columns.Add(new Column("OnePDF", OnePdfExtract));
        }

        // Results[column][file] = per-page scores (null = column unavailable)
        Dictionary<string, Dictionary<string, Metrics.Score>> results = [];
        Dictionary<string, List<string>> failures = [];
        foreach (Column c in columns)
        {
            Dictionary<string, Metrics.Score> perFileScores = [];
            List<string> fails = [];
            bool available = true;
            foreach (Truth t in truths)
            {
                string pdf = t.PdfPath;
                string[] pages;
                try
                {
                    pages = c.Extract!(pdf, t);
                }
                catch (NotSupportedException)
                {
                    available = false;
                    break;
                }
#pragma warning disable CA1031 // A comparison run records any failure of the library under test and carries on.
                catch (Exception e)
#pragma warning restore CA1031
                {
                    fails.Add($"{t.Name}: {e.GetType().Name}: {Short(e.Message)}");
                    pages = [];
                }

                Metrics.Score total = default;
                for (int i = 0; i < t.Pages.Length; i++)
                {
                    total += Metrics.ScorePage(t.Pages[i], i < pages.Length ? pages[i] : "");
                }

                perFileScores[t.Name] = total;
            }

            if (available)
            {
                results[c.Name] = perFileScores;
                failures[c.Name] = fails;
            }
            else
            {
                Console.WriteLine($"{c.Name}: not available in this build (the glyph producer is not joined yet).");
            }
        }

        string[] names = [.. columns.Select(c => c.Name).Where(results.ContainsKey)];
        if (names.Length == 0)
        {
            return;
        }

        Table(report, truths, names, results, perFile);
        foreach (string n in names)
        {
            if (failures[n].Count > 0)
            {
                report.AppendLine().AppendLine($"{n}: {failures[n].Count} file(s) failed to open or extract (scored as empty output):");
                foreach (string f in failures[n])
                {
                    report.AppendLine("  " + f);
                }
            }
        }

        if (json is not null)
        {
            WriteJson(json, truths, names, results);
        }
    }

    /// <summary>Checks the scorer on cases whose answers are known.</summary>
    private static int SelfTest()
    {
        Metrics.Score same = Metrics.ScorePage("one two three", "one two three");
        Metrics.Score swapped = Metrics.ScorePage("one two three", "three one two");
        Metrics.Score wrong = Metrics.ScorePage("abcd efgh", "abcd efgx");
        Metrics.Score cjk = Metrics.ScorePage("あいう", "あ い う");
        bool ok = same.ContentDistance == 0 && same.OrderDistance == 0
            && swapped.ContentDistance == 0 && swapped.OrderDistance == 2
            && wrong.ContentDistance == 1 && wrong.ContentSize == 9 && wrong.OrderDistance == 1
            && cjk.ContentDistance == 0 && cjk.OrderDistance == 0;
        Console.WriteLine(ok ? "scorer self-test passed" : "scorer self-test FAILED");
        return ok ? 0 : 1;
    }

    /// <summary>Times the layout of synthetic pages (about 4000 glyphs each, two columns): a sanity check of the hot path, not a benchmark.</summary>
    private static int Smoke()
    {
        const int Pages = 100;
        PdfPageGlyphs[] pages = new PdfPageGlyphs[Pages];
        for (int p = 0; p < Pages; p++)
        {
            List<PdfTextGlyph> g = [];
            for (int col = 0; col < 2; col++)
            {
                for (int line = 0; line < 50; line++)
                {
                    double x = 50 + col * 300;
                    for (int c = 0; c < 40; c++)
                    {
                        string t = (c % 6 == 5) ? " " : ((char)('a' + (c * 7 + line + p) % 26)).ToString();
                        if (t != " ")
                        {
                            g.Add(new PdfTextGlyph { Text = t, Matrix = new PdfTextMatrix(10, 0, 0, 10, x, 740 - line * 14), Advance = 0.5, FillColor = 0 });
                        }

                        x += 5;
                    }
                }
            }

            pages[p] = new PdfPageGlyphs(p, 612, 792, g);
        }

        PdfTextExtractor.Analyze(pages[0]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        int chars = 0;
        foreach (PdfPageGlyphs pg in pages)
        {
            chars += PdfTextExtractor.Analyze(pg).Text.Length;
        }

        sw.Stop();
        long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"{Pages} synthetic pages, {pages[0].Glyphs.Count} glyphs each, {chars} chars: {sw.Elapsed.TotalMilliseconds / Pages:0.00} ms/page, {alloc / Pages / 1024} KiB allocated/page (single thread, local smoke only)");
        return 0;
    }

    private static string Short(string s) => s.Length > 120 ? s[..120] : s;

    private static string? Opt(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string? FindDir(string set)
    {
        for (DirectoryInfo? d = new(Directory.GetCurrentDirectory()); d is not null; d = d.Parent)
        {
            string p = Path.Combine(d.FullName, "testdata", set);
            if (Directory.Exists(Path.Combine(p, "truth")))
            {
                return p;
            }
        }

        return null;
    }

    private static List<Truth> LoadTruth(string dir)
    {
        List<Truth> list = [];
        foreach (string f in Directory.GetFiles(Path.Combine(dir, "truth"), "*.json").Order(StringComparer.Ordinal))
        {
            using JsonDocument d = JsonDocument.Parse(File.ReadAllText(f));
            JsonElement r = d.RootElement;
            string[] pages = [.. r.GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("text").GetString() ?? "")];
            string pdfPath = Path.Combine(dir, "pdf", r.GetProperty("name").GetString() + ".pdf");
            if (pages.Length == 0 || !File.Exists(pdfPath))
            {
                continue; // nothing to score (an unrecoverable repair case)
            }

            string? user = null, owner = null;
            if (r.TryGetProperty("passwords", out JsonElement pw))
            {
                user = pw.TryGetProperty("user", out JsonElement u) ? u.GetString() : null;
                owner = pw.TryGetProperty("owner", out JsonElement o) ? o.GetString() : null;
            }

            int[]? scored = r.TryGetProperty("scoredPages", out JsonElement sp) ? [.. sp.EnumerateArray().Select(x => x.GetInt32())] : null;
            list.Add(new Truth(r.GetProperty("name").GetString()!, r.GetProperty("category").GetString() ?? "other", pages, user, owner, pdfPath, scored));
        }

        return list;
    }

    // ---- columns ------------------------------------------------------------------------------------------------------------------

    private static PigDocument OpenPig(string path, Truth t)
    {
        List<string> pws = [];
        if (!string.IsNullOrEmpty(t.UserPassword))
        {
            pws.Add(t.UserPassword);
        }

        if (!string.IsNullOrEmpty(t.OwnerPassword))
        {
            pws.Add(t.OwnerPassword);
        }

        return PigDocument.Open(path, new ParsingOptions { Passwords = pws.Count > 0 ? pws : [""], SkipMissingFonts = true, UseLenientParsing = true });
    }

    /// <summary>PdfPig's default text extraction: <c>Page.Text</c>.</summary>
    private static string[] PdfPigDefault(string path, Truth t)
    {
        using PigDocument doc = OpenPig(path, t);
        return [.. Enumerable.Range(0, t.Pages.Length).Select(i => t.DocPage(i) < doc.NumberOfPages ? doc.GetPage(t.DocPage(i) + 1).Text : "")];
    }

    /// <summary>PdfPig's documented layout pipeline: nearest-neighbour words, Docstrum blocks, unsupervised reading order.</summary>
    private static string[] PdfPigLayout(string path, Truth t)
    {
        using PigDocument doc = OpenPig(path, t);
        List<string> pages = [];
        for (int i = 0; i < t.Pages.Length; i++)
        {
            if (t.DocPage(i) >= doc.NumberOfPages)
            {
                pages.Add("");
                continue;
            }

            Page p = doc.GetPage(t.DocPage(i) + 1);
            IEnumerable<Word> words = p.GetWords(NearestNeighbourWordExtractor.Instance);
            var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
            var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks);
            pages.Add(string.Join("\n\n", ordered.Select(b => b.Text)));
        }

        return [.. pages];
    }

    private static string[] OnePdfExtract(string path, Truth t)
    {
        using OnePdf.PdfDocument doc = OnePdf.PdfDocument.Open(path, new PdfOpenOptions { Password = t.UserPassword ?? t.OwnerPassword });
        PdfTextExtractor? ex = PdfTextExtractor.TryCreate(doc);
        if (ex is null)
        {
            throw new NotSupportedException();
        }

        return [.. Enumerable.Range(0, t.Pages.Length).Select(i => t.DocPage(i) < doc.Pages.Count ? ex.ExtractPage(t.DocPage(i)).Text : "")];
    }

    // ---- report -------------------------------------------------------------------------------------------------------------------

    private const double TieMargin = 0.001; // 0.1 percentage points

    private static void Table(StringBuilder sb, List<Truth> truths, string[] names, Dictionary<string, Dictionary<string, Metrics.Score>> results, bool perFile)
    {
        sb.AppendLine("Extraction error rates in percent (lower is better): content = order-free character error rate, order = reading-order error over the word sequence.");
        sb.AppendLine($"Files with truth: {truths.Count}. A difference under {TieMargin * 100:0.0} percentage points is a tie.");
        sb.AppendLine();
        string[] categories = [.. truths.Select(t => t.Category).Distinct().Order(StringComparer.Ordinal)];

        sb.Append("| category | files | truth chars |");
        foreach (string n in names)
        {
            sb.Append($" {n} content | {n} order |");
        }

        sb.AppendLine(names.Contains("OnePDF") && names.Length > 1 ? " verdict |" : "");
        sb.Append("|---|---:|---:|");
        foreach (string _ in names)
        {
            sb.Append("---:|---:|");
        }

        sb.AppendLine(names.Contains("OnePDF") && names.Length > 1 ? "---|" : "");

        foreach (string cat in categories.Append("ALL"))
        {
            List<Truth> sel = cat == "ALL" ? truths : [.. truths.Where(t => t.Category == cat)];
            Metrics.Score first = Sum(results[names[0]], sel);
            sb.Append($"| {cat} | {sel.Count} | {first.ContentSize} |");
            foreach (string n in names)
            {
                Metrics.Score s = Sum(results[n], sel);
                sb.Append($" {s.ContentRate * 100:0.0} | {s.OrderRate * 100:0.0} |");
            }

            if (names.Contains("OnePDF") && names.Length > 1)
            {
                sb.Append(' ').Append(Verdict(names, results, sel)).AppendLine(" |");
            }
            else
            {
                sb.AppendLine();
            }
        }

        if (perFile)
        {
            sb.AppendLine().AppendLine("Per file (content / order, percent):");
            foreach (Truth t in truths)
            {
                sb.Append($"  {t.Name,-40} {t.Category,-14}");
                foreach (string n in names)
                {
                    Metrics.Score s = results[n][t.Name];
                    sb.Append($"  {n}: {s.ContentRate * 100,6:0.0} / {s.OrderRate * 100,6:0.0}");
                }

                sb.AppendLine();
            }
        }
    }

    private static Metrics.Score Sum(Dictionary<string, Metrics.Score> perFile, List<Truth> sel)
    {
        Metrics.Score s = default;
        foreach (Truth t in sel)
        {
            s += perFile[t.Name];
        }

        return s;
    }

    /// <summary>OnePDF against the better of the PdfPig variants, per metric.</summary>
    private static string Verdict(string[] names, Dictionary<string, Dictionary<string, Metrics.Score>> results, List<Truth> sel)
    {
        Metrics.Score ours = Sum(results["OnePDF"], sel);
        double bestContent = double.MaxValue, bestOrder = double.MaxValue;
        foreach (string n in names.Where(n => n != "OnePDF"))
        {
            Metrics.Score s = Sum(results[n], sel);
            bestContent = Math.Min(bestContent, s.ContentRate);
            bestOrder = Math.Min(bestOrder, s.OrderRate);
        }

        return $"content {Cmp(ours.ContentRate, bestContent)}, order {Cmp(ours.OrderRate, bestOrder)}";

        static string Cmp(double ours, double bar) => Math.Abs(ours - bar) < TieMargin ? "tie" : ours < bar ? "better" : "WORSE";
    }

    private static void WriteJson(string path, List<Truth> truths, string[] names, Dictionary<string, Dictionary<string, Metrics.Score>> results)
    {
        using FileStream fs = File.Create(path);
        using Utf8JsonWriter w = new(fs, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WriteStartObject("columns");
        foreach (string n in names)
        {
            w.WriteStartObject(n);
            foreach (string cat in truths.Select(t => t.Category).Distinct().Order(StringComparer.Ordinal).Append("ALL"))
            {
                Metrics.Score s = Sum(results[n], cat == "ALL" ? truths : [.. truths.Where(t => t.Category == cat)]);
                w.WriteStartObject(cat);
                w.WriteNumber("contentErrorRate", Math.Round(s.ContentRate, 5));
                w.WriteNumber("orderErrorRate", Math.Round(s.OrderRate, 5));
                w.WriteNumber("truthChars", s.ContentSize);
                w.WriteNumber("truthTokens", s.OrderSize);
                w.WriteEndObject();
            }

            w.WriteEndObject();
        }

        w.WriteEndObject();
        w.WriteEndObject();
    }
}
