using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OnePdf;

// Converts every file of the given sets to PDF/A with PdfAConverter, validates each output with the veraPDF command-line validator
// (a black box, named by ONEPDF_VERAPDF) and writes a per-file CSV and a summary.
//
//   OnePdf.PdfASweep <out-dir> [--part 2|3] [--level b|u] [--jobs N] [--vera-jobs N] [--timeout S] [--max-mb N] [--no-vera] [--keep]
//                    [--no-substitute-fonts] <label>=<dir>[@sample=N[,seed=S]] ...
//
// A set is every *.pdf below <dir>; "sample" takes a fixed, seeded random subset (the default seed is 1) of the sorted file list.
internal static partial class Program
{
    private sealed record Row(string Set, string File)
    {
        public string Status = "";          // converted | refused | unreadable | password | crash | timeout | skipped
        public string Detail = "";
        public List<string> Reasons = [];
        public List<string> Raw = [];
        public double Seconds;
        public long InBytes;
        public long OutBytes;
        public string Vera = "";           // ok | fail | error | (empty when not run)
        public string VeraDetail = "";
        public string Out = "";
        public string Changes = "";
    }

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: OnePdf.PdfASweep <out-dir> [options] <label>=<dir>[@sample=N[,seed=S]] ...");
            return 2;
        }

        if (args[0] == "--explain")
        {
            return Explain(args[1], args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : -1);
        }

        string outDir = Path.GetFullPath(args[0]);
        PdfAPart part = PdfAPart.Part2;
        PdfAConformance level = PdfAConformance.B;
        int jobs = 3, veraJobs = 2, timeout = 180, maxMb = 80;
        bool vera = true, keep = false, substitutes = true;
        var sets = new List<(string Label, string Dir, int Sample, int Seed)>();
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--part": part = args[++i] == "3" ? PdfAPart.Part3 : PdfAPart.Part2; break;
                case "--level": level = args[++i].Equals("u", StringComparison.OrdinalIgnoreCase) ? PdfAConformance.U : PdfAConformance.B; break;
                case "--jobs": jobs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--vera-jobs": veraJobs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--timeout": timeout = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--max-mb": maxMb = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--no-vera": vera = false; break;
                case "--keep": keep = true; break;
                case "--no-substitute-fonts": substitutes = false; break;
                default:
                    {
                        string spec = args[i];
                        int eq = spec.IndexOf('=', StringComparison.Ordinal);
                        string label = spec[..eq];
                        string rest = spec[(eq + 1)..];
                        int sample = 0, seed = 1;
                        int at = rest.IndexOf('@', StringComparison.Ordinal);
                        if (at >= 0)
                        {
                            foreach (string kv in rest[(at + 1)..].Split(','))
                            {
                                string[] p = kv.Split('=');
                                if (p[0] == "sample") { sample = int.Parse(p[1], CultureInfo.InvariantCulture); }
                                else if (p[0] == "seed") { seed = int.Parse(p[1], CultureInfo.InvariantCulture); }
                            }

                            rest = rest[..at];
                        }

                        sets.Add((label, Path.GetFullPath(rest), sample, seed));
                        break;
                    }
            }
        }

        string? veraPath = Environment.GetEnvironmentVariable("ONEPDF_VERAPDF");
        if (vera && (veraPath is null || !File.Exists(veraPath)))
        {
            Console.Error.WriteLine("ONEPDF_VERAPDF is not set; running without validation (use --no-vera to silence).");
            vera = false;
        }

        string flavour = $"{(int)part}{(level == PdfAConformance.U ? 'u' : 'b')}";
        OnePdf.Fonts.FontsPackage.Register();
        Directory.CreateDirectory(outDir);
        var all = new List<Row>();
        foreach ((string label, string dir, int sample, int seed) in sets)
        {
            List<string> files = [.. Directory.EnumerateFiles(dir, "*.pdf", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];
            if (sample > 0 && files.Count > sample)
            {
                var rng = new Random(seed);
                files = [.. files.OrderBy(_ => rng.Next()).Take(sample).Order(StringComparer.Ordinal)];
            }

            string setOut = Path.Combine(outDir, label);
            Directory.CreateDirectory(setOut);
            var rows = new ConcurrentBag<Row>();
            Console.WriteLine($"[{label}] {files.Count} files, {flavour}");
            int done = 0;
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = jobs }, file =>
            {
                string rel = Path.GetRelativePath(dir, file).Replace('/', '_').Replace('\\', '_');
                var row = new Row(label, Path.GetRelativePath(dir, file)) { InBytes = new FileInfo(file).Length };
                row.Out = Path.Combine(setOut, rel);
                Convert(file, row, part, level, timeout, maxMb, substitutes);
                rows.Add(row);
                int n = Interlocked.Increment(ref done);
                if (n % 50 == 0)
                {
                    Console.WriteLine($"[{label}] converted {n}/{files.Count}");
                }
            });
            List<Row> setRows = [.. rows.OrderBy(r => r.File, StringComparer.Ordinal)];
            if (vera)
            {
                Validate(veraPath!, flavour, setRows.Where(r => r.Status == "converted").ToList(), veraJobs);
            }

            if (!keep)
            {
                foreach (Row r in setRows)
                {
                    if (r.Vera != "fail" && File.Exists(r.Out))
                    {
                        File.Delete(r.Out);
                    }
                }
            }

            all.AddRange(setRows);
        }

        WriteCsv(Path.Combine(outDir, "sweep.csv"), all);
        Summarize(all, vera, Path.Combine(outDir, "summary.txt"));
        return all.Any(r => r.Vera == "fail" || r.Status == "crash") ? 1 : 0;
    }

    // Prints what the substitute-font embedder decides for each font of one file, and every violation a conversion would be refused with.
    private static int Explain(string file, int find)
    {
        OnePdf.Fonts.FontsPackage.Register();
        using PdfDocument doc = PdfDocument.Open(file, new PdfOpenOptions { AllowEditing = true, Password = "" });
        if (Environment.GetEnvironmentVariable("SWEEP_CONTENT") is { } pageNo)
        {
            PdfObject? contents = doc.Pages[int.Parse(pageNo, CultureInfo.InvariantCulture)].Dictionary.Get("Contents");
            var parts = contents is PdfArray ca ? Enumerable.Range(0, ca.Count).Select(i => ca[i]).OfType<PdfStream>().ToList() : contents is PdfStream one ? [one] : [];
            foreach (PdfStream st in parts)
            {
                Console.WriteLine("%%% stream " + st.Reference?.Number);
                Console.WriteLine(Encoding.Latin1.GetString(st.GetDecodedBytes().Span));
            }

            return 0;
        }

        if (find < -1)
        {
            PdfObject? target = doc.GetObject(-find);
            Console.WriteLine(Dump(target, 0));
            if (target is PdfStream ts)
            {
                byte[] bytes = ts.GetDecodedBytes().ToArray();
                Console.WriteLine($"decoded {bytes.Length} bytes, starts {System.Convert.ToHexString(bytes.AsSpan(0, Math.Min(16, bytes.Length)))}");
                try
                {
                    PdfFontFile ff = PdfFontFile.Open(bytes);
                    Console.WriteLine($"font file: {ff.Kind}, glyphs {ff.GlyphCount}, upem {ff.UnitsPerEm}, name {ff.PostScriptName}");
                    for (int gi = 0; gi < Math.Min(ff.GlyphCount, 12); gi++)
                    {
                        Console.WriteLine($"  glyph {gi} {ff.GetGlyphName(gi)} width {ff.GetAdvanceWidth(gi)}");
                    }
                }
                catch (PdfException ex)
                {
                    Console.WriteLine("not a font file: " + ex.Message);
                }
            }

            return 0;
        }

        if (find >= 0)
        {
            // Breadth-first path from the catalog to an object, for finding out where a font hides.
            var prev = new Dictionary<int, (int From, string Key)>();
            var queue = new Queue<(PdfObject Obj, int Owner)>();
            queue.Enqueue((doc.Trailer.GetRaw("Root")!, -1));
            var seen = new HashSet<int>();
            while (queue.Count > 0 && !prev.ContainsKey(find))
            {
                (PdfObject o, int owner) = queue.Dequeue();
                PdfObject cur = o;
                int me = owner;
                if (o is PdfReference r)
                {
                    if (!seen.Add(r.Number)) { continue; }
                    cur = doc.Resolve(o)!;
                    me = r.Number;
                }

                void Edge(PdfObject child, string key)
                {
                    if (child is PdfReference cr && !prev.ContainsKey(cr.Number)) { prev[cr.Number] = (me, key); }
                    queue.Enqueue((child, me));
                }

                if (cur is PdfDictionary d)
                {
                    foreach (KeyValuePair<string, PdfObject> e in d.Entries) { if (e.Key != "Parent") { Edge(e.Value, e.Key); } }
                }
                else if (cur is PdfArray a)
                {
                    for (int i = 0; i < a.Count; i++) { Edge(a.GetRaw(i), "[" + i + "]"); }
                }
            }

            for (int n = find, guard = 0; prev.TryGetValue(n, out (int From, string Key) p) && n > 0 && guard++ < 30;)
            {
                Console.WriteLine($"{n} <- {p.From} via /{p.Key}");
                n = p.From;
            }

            return 0;
        }

        if (Environment.GetEnvironmentVariable("SWEEP_WIDTHS") is not null)
        {
            foreach (PdfWidthMatchEntry e in doc.MatchEmbeddedFontWidths(new PdfWidthMatchOptions { Tolerance = 0.95 }).Entries)
            {
                Console.WriteLine($"width {e.ObjectNumber} {e.BaseFont}: {e.Outcome} {e.Note} changes={e.WidthChanges.Count} {string.Join(",", e.WidthChanges.Take(3).Select(c => $"{c.Code}:{c.PdfWidth:0.##}/{c.ProgramWidth:0.##}"))}");
            }
        }

        PdfSubstituteEmbedReport report = doc.EmbedSubstituteFonts();
        foreach (PdfSubstituteEntry e in report.Entries)
        {
            Console.WriteLine($"font {e.ObjectNumber} {e.BaseFont}: {e.Outcome} {e.Substitute} {e.Message} missing={e.MissingCodes.Count}");
        }

        try
        {
            using var ms = new MemoryStream();
            PdfAConversionResult r = doc.ConvertToPdfA(ms, new PdfAConversionOptions());
            Console.WriteLine("converted: " + string.Join("; ", r.Changes));
            foreach (string w in r.Warnings)
            {
                Console.WriteLine("warning: " + w);
            }
        }
        catch (PdfAException ex)
        {
            foreach (PdfAViolation v in ex.Violations.Take(60))
            {
                Console.WriteLine(v);
            }

            foreach (PdfAViolation v in ex.Violations.Where(v => v.ObjectNumber >= 0).Take(2))
            {
                if (doc.GetObject(v.ObjectNumber) is PdfStream st)
                {
                    Console.WriteLine($"object {v.ObjectNumber}: filters [{string.Join(",", st.FilterNames)}] undecoded={st.UndecodedFilter} keys={string.Join(",", st.Dictionary.Keys)}");
                }
            }
        }

        return 0;
    }

    // A readable dump of one object, to depth 4; references are shown as numbers, not followed.
    private static string Dump(PdfObject? o, int depth)
    {
        switch (o)
        {
            case null: return "null";
            case PdfReference r: return r.Number + " 0 R";
            case PdfName n: return "/" + n.Value;
            case PdfInteger i: return i.Value.ToString(CultureInfo.InvariantCulture);
            case PdfReal d: return d.Value.ToString("0.###", CultureInfo.InvariantCulture);
            case PdfString str: return "(" + str.GetText() + ")";
            case PdfBoolean b: return b.Value ? "true" : "false";
            case PdfArray a:
                return depth > 4 ? "[...]" : "[" + string.Join(" ", Enumerable.Range(0, Math.Min(a.Count, 40)).Select(k => Dump(a.GetRaw(k), depth + 1))) + (a.Count > 40 ? " ..." : "") + "]";
            case PdfStream st: return "stream" + Dump(st.Dictionary, depth);
            case PdfDictionary dict:
                return depth > 4 ? "<<...>>" : "<<" + string.Join(" ", dict.Entries.Take(40).Select(e => "/" + e.Key + " " + Dump(e.Value, depth + 1))) + ">>";
            default: return o.GetType().Name;
        }
    }

    private static void Convert(string file, Row row, PdfAPart part, PdfAConformance level, int timeoutSeconds, int maxMb, bool substitutes)
    {
        if (row.InBytes > maxMb * 1024L * 1024L)
        {
            row.Status = "skipped";
            row.Detail = "larger than " + maxMb + " MB";
            return;
        }

        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var options = new PdfAConversionOptions { Part = part, Conformance = level, EmbedSubstituteFonts = substitutes, Password = "" };
            PdfAConversionResult res = PdfAConverter.Convert(file, row.Out, options, cts.Token);
            row.Status = "converted";
            row.OutBytes = new FileInfo(row.Out).Length;
            row.Changes = string.Join("; ", res.Changes.Select(c => $"{c.Kind}x{c.Count}"));
        }
        catch (PdfAException ex)
        {
            row.Status = "refused";
            row.Detail = ex.Violations.Count + " violations";
            row.Reasons = [.. ex.Violations.Select(v => Normalize(v)).Distinct()];
            row.Raw = [.. ex.Violations.Select(v => v.ToString()).Distinct().Take(40)];
        }
        catch (PdfPasswordException ex)
        {
            row.Status = "password";
            row.Detail = ex.Message;
        }
        catch (PdfException ex)
        {
            row.Status = "unreadable";
            row.Detail = ex.GetType().Name + ": " + ex.Message;
        }
        catch (OperationCanceledException)
        {
            row.Status = "timeout";
        }
        catch (Exception ex)
        {
            row.Status = "crash";
            row.Detail = ex.GetType().Name + ": " + ex.Message + " @ " + (ex.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim();
        }

        row.Seconds = sw.Elapsed.TotalSeconds;
    }

    [GeneratedRegex(@"\d+(\.\d+)?")]
    private static partial Regex Digits();

    [GeneratedRegex(@"(font|Font) \S+")]
    private static partial Regex FontName();

    private static string Normalize(PdfAViolation v)
    {
        string m = FontName().Replace(v.Message, "font X");
        m = Digits().Replace(m, "#");
        return v.Clause + " " + (m.Length > 90 ? m[..90] : m);
    }

    private static void Validate(string vera, string flavour, List<Row> rows, int veraJobs)
    {
        const int Batch = 12;
        var batches = new List<List<Row>>();
        for (int i = 0; i < rows.Count; i += Batch)
        {
            batches.Add(rows.GetRange(i, Math.Min(Batch, rows.Count - i)));
        }

        Console.WriteLine($"[veraPDF] {rows.Count} outputs in {batches.Count} batches");
        Parallel.ForEach(batches, new ParallelOptions { MaxDegreeOfParallelism = veraJobs }, batch =>
        {
            var psi = new ProcessStartInfo(vera) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(batch[0].Out)! };
            psi.ArgumentList.Add("--flavour");
            psi.ArgumentList.Add(flavour);
            psi.ArgumentList.Add("--format");
            psi.ArgumentList.Add("xml");
            foreach (Row r in batch)
            {
                psi.ArgumentList.Add(r.Out);
            }

            psi.Environment["JAVA_OPTS"] = "-Xmx2g";
            string xml;
            using (Process p = Process.Start(psi)!)
            {
                Task<string> err = p.StandardError.ReadToEndAsync();
                xml = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(TimeSpan.FromMinutes(20)))
                {
                    p.Kill(true);
                }

                _ = err.Result;
            }

            var byName = batch.ToDictionary(r => r.Out, StringComparer.Ordinal);
            foreach (Match job in JobRegex().Matches(xml))
            {
                string body = job.Value;
                Match name = Regex.Match(body, "<name>([^<]*)</name>");
                if (!name.Success || !byName.TryGetValue(System.Net.WebUtility.HtmlDecode(name.Groups[1].Value), out Row? found))
                {
                    continue;
                }

                Row row = found;

                if (body.Contains("isCompliant=\"true\"", StringComparison.Ordinal))
                {
                    row.Vera = "ok";
                }
                else if (body.Contains("jobEndStatus=\"normal\"", StringComparison.Ordinal))
                {
                    row.Vera = "fail";
                    var rules = new List<string>();
                    foreach (Match m in Regex.Matches(body, "<rule [^>]*clause=\"([^\"]*)\"[^>]*testNumber=\"([^\"]*)\"[^>]*>\\s*<description>([^<]*)</description>"))
                    {
                        rules.Add($"{m.Groups[1].Value}-{m.Groups[2].Value}");
                    }

                    row.VeraDetail = string.Join(" ", rules);
                }
                else
                {
                    row.Vera = "error";
                    row.VeraDetail = Regex.Match(body, "jobEndStatus=\"([^\"]*)\"").Groups[1].Value;
                }
            }
        });
        foreach (Row r in rows.Where(r => r.Vera.Length == 0))
        {
            r.Vera = "error";
            r.VeraDetail = "no report";
        }
    }

    [GeneratedRegex("<job>.*?</job>", RegexOptions.Singleline)]
    private static partial Regex JobRegex();

    private static string Csv(string s) => "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal).Replace('\n', ' ') + "\"";

    private static void WriteCsv(string path, List<Row> rows)
    {
        var sb = new StringBuilder("set,file,status,detail,seconds,in_bytes,out_bytes,vera,vera_rules,reasons,changes,raw\n");
        foreach (Row r in rows)
        {
            sb.Append(Csv(r.Set)).Append(',').Append(Csv(r.File)).Append(',').Append(r.Status).Append(',').Append(Csv(r.Detail)).Append(',')
              .Append(r.Seconds.ToString("0.00", CultureInfo.InvariantCulture)).Append(',').Append(r.InBytes).Append(',').Append(r.OutBytes).Append(',')
              .Append(r.Vera).Append(',').Append(Csv(r.VeraDetail)).Append(',').Append(Csv(string.Join(" | ", r.Reasons))).Append(',').Append(Csv(r.Changes)).Append(',').Append(Csv(string.Join(" || ", r.Raw))).Append('\n');
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static void Summarize(List<Row> rows, bool vera, string path)
    {
        var sb = new StringBuilder();
        foreach (IGrouping<string, Row> set in rows.GroupBy(r => r.Set))
        {
            int n = set.Count();
            int conv = set.Count(r => r.Status == "converted");
            sb.Append($"== {set.Key}: {n} files, converted {conv} ({100.0 * conv / n:0.0}%)");
            foreach (IGrouping<string, Row> g in set.GroupBy(r => r.Status).OrderBy(g => g.Key))
            {
                sb.Append($", {g.Key} {g.Count()}");
            }

            sb.AppendLine();
            if (vera)
            {
                sb.AppendLine($"   veraPDF on converted: ok {set.Count(r => r.Vera == "ok")}, fail {set.Count(r => r.Vera == "fail")}, error {set.Count(r => r.Vera == "error")}");
                foreach (IGrouping<string, Row> g in set.Where(r => r.Vera == "fail").SelectMany(r => r.VeraDetail.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().Select(x => (x, r))).GroupBy(t => t.x, t => t.r).OrderByDescending(g => g.Count()).Take(15))
                {
                    sb.AppendLine($"     vera rule {g.Key}: {g.Count()} files (e.g. {g.First().File})");
                }
            }

            foreach (IGrouping<string, Row> g in set.Where(r => r.Status == "refused").SelectMany(r => r.Reasons.Select(x => (x, r))).GroupBy(t => t.x, t => t.r).OrderByDescending(g => g.Count()).Take(25))
            {
                sb.AppendLine($"   refused {g.Count(),5}  {g.Key}  (e.g. {g.First().File})");
            }

            foreach (Row r in set.Where(r => r.Status is "crash" or "timeout" or "unreadable").Take(12))
            {
                sb.AppendLine($"   {r.Status}: {r.File} {r.Detail}");
            }
        }

        File.WriteAllText(path, sb.ToString());
        Console.WriteLine(sb.ToString());
    }
}
