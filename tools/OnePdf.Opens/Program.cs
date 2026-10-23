using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OnePdf;
using OnePdf.Text;

// Corpus comparison and crash/hang sweep.
//
//   opens <out-dir> [--jobs N] [--timeout S] <label>=<dir>...
//       Opens every .pdf under each directory with OnePDF, PdfPig and PDFsharp (one child process per file and engine) and
//       writes opens.csv and opens-summary.md. "Opens" means: the document opens, the page count is read and the text of
//       every page is read without an exception. PDFsharp has no text layer, so for it "text" means every page object
//       and its media box could be read; it is information only.
//       Passwords: no engine is given a password. Each tries the empty password on its own; a file that still asks for one
//       is classified "password" and is left out of the OnePDF-vs-PdfPig counts when both engines ask.
//   sweep <out-dir> [--jobs N] [--drop-glyphs] <label>=<dir>...
//       For every file, in a child process: open, ExtractText for the whole document, interpret every page into a
//       PdfDisplayList and replay it. The parent measures wall time per page (as printed by the child, which flushes a
//       line per page) and the child's peak resident set. Findings: an unexpected exception, a crash, a hang (no progress
//       for 60 s), any page or the text pass over 10 s, any file over 1 GiB peak. Typed PdfExceptions are expected on broken
//       files and are only counted.
//
// The parent pauses while a BenchmarkDotNet process is running so as not to disturb its timings.
internal static class Program
{
    private const double SlowSeconds = 10;
    private const long PeakLimitKb = 1024L * 1024;

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length >= 3 && args[0] == "--child-open")
            {
                return ChildOpen(args[1], args[2]);
            }

            if (args.Length >= 2 && args[0] == "--child-sweep")
            {
                return ChildSweep(args[1], args.Length > 2 && args[2] == "drop-glyphs");
            }

            if (args.Length >= 3 && (args[0] == "opens" || args[0] == "sweep"))
            {
                return Parent(args);
            }
        }
#pragma warning disable CA1031 // The tool reports whatever escapes as its own failure.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Console.Error.WriteLine(ex);
            return 3;
        }

        Console.Error.WriteLine("usage: opens|sweep <out-dir> [--jobs N] [--timeout S] <label>=<dir>...");
        return 2;
    }

    // ---------------------------------------------------------------- children

    private static string Clean(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (char c in s.Length > 160 ? s[..160] : s)
        {
            sb.Append(c is '\t' or '\r' or '\n' or ',' or '"' ? ' ' : c);
        }

        return sb.ToString();
    }

    /// <summary>One engine on one file. Prints one RESULT line: opened, pages, textOk, class, stage, type, ms, message.</summary>
    private static int ChildOpen(string engine, string file)
    {
        var sw = Stopwatch.StartNew();
        bool opened = false, textOk = false;
        int pages = -1;
        string stage = "open";
        Exception? error = null;
        try
        {
            switch (engine)
            {
                case "onepdf":
                    {
                        using PdfDocument doc = PdfDocument.Open(file);
                        opened = true;
                        stage = "pages";
                        pages = doc.Pages.Count;
                        stage = "text";
                        int bad = 0;
                        for (int i = 0; i < pages; i++)
                        {
                            try
                            {
                                _ = doc.Pages[i].ExtractText().Text;
                            }
                            catch (PdfException ex)
                            {
                                bad++;
                                error ??= ex;
                            }
                        }

                        textOk = bad == 0;
                        break;
                    }

                case "pdfpig":
                    {
                        using var doc = UglyToad.PdfPig.PdfDocument.Open(file);
                        opened = true;
                        stage = "pages";
                        pages = doc.NumberOfPages;
                        stage = "text";
                        int bad = 0;
                        for (int i = 1; i <= pages; i++)
                        {
#pragma warning disable CA1031 // Any exception from the comparison library means that page could not be read.
                            try
                            {
                                _ = doc.GetPage(i).Text;
                            }
                            catch (Exception ex)
                            {
                                bad++;
                                error ??= ex;
                            }
#pragma warning restore CA1031
                        }

                        textOk = bad == 0;
                        break;
                    }

                case "pdfsharp":
                    {
                        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(file, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
                        opened = true;
                        stage = "pages";
                        pages = doc.PageCount;
                        stage = "text";
                        int bad = 0;
                        for (int i = 0; i < pages; i++)
                        {
#pragma warning disable CA1031
                            try
                            {
                                var p = doc.Pages[i];
                                _ = p.MediaBox;
                                _ = p.Contents.Elements.Count;
                            }
                            catch (Exception ex)
                            {
                                bad++;
                                error ??= ex;
                            }
#pragma warning restore CA1031
                        }

                        textOk = bad == 0;
                        break;
                    }

                default:
                    return 2;
            }
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            error = ex;
            textOk = false;
        }

        string cls = "ok";
        if (error is not null)
        {
            string name = error.GetType().Name;
            string msg = error.Message;
            cls = error switch
            {
                PdfPasswordException => "password",
                PdfEncryptionException => "encryption",
                PdfLimitException => "limit",
                PdfFormatException => "format",
                PdfException => "typed",
                OutOfMemoryException => "oom",
                _ when name.Contains("Encrypt", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("password", StringComparison.OrdinalIgnoreCase) => "password",
                _ => "error",
            };
        }

        Console.WriteLine(string.Join('\t', "RESULT", opened ? 1 : 0, pages, textOk ? 1 : 0, cls, error is null ? "" : stage,
            error?.GetType().Name ?? "", sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture), Clean(error?.Message)));
        return 0;
    }

    private sealed class Sink : PdfDevice;

    /// <summary>Open, whole-document text, every page into a display list. Prints a progress line per step.</summary>
    private static int ChildSweep(string file, bool dropGlyphs)
    {
        var ci = CultureInfo.InvariantCulture;
        // Diagnosis aid: ONEPDF_SWEEP_PHASES=text or interpret runs only that phase (default: both).
        string phases = Environment.GetEnvironmentVariable("ONEPDF_SWEEP_PHASES") ?? "all";
        try
        {
            PdfDocument doc;
            int n;
            Console.WriteLine("S open");
            try
            {
                doc = PdfDocument.Open(file);
                n = doc.Pages.Count;
            }
            catch (PdfException ex)
            {
                Console.WriteLine($"E open {ex.GetType().Name} {Clean(ex.Message)}");
                return 0;
            }

            using (doc)
            {
                Console.WriteLine($"N {n}");
                Console.WriteLine("S text");
                var sw = Stopwatch.StartNew();
                try
                {
                    if (phases == "interpret")
                    {
                        throw new PdfFormatException("text skipped");
                    }

                    int chars = 0;
                    foreach (var t in doc.ExtractText(new PdfTextOptions { RetainGlyphs = dropGlyphs ? false : null }))
                    {
                        chars += t.Text.Length;
                    }

                    Console.WriteLine($"T {sw.ElapsedMilliseconds.ToString(ci)} {chars.ToString(ci)}");
                }
                catch (PdfException ex)
                {
                    Console.WriteLine($"T {sw.ElapsedMilliseconds.ToString(ci)} 0");
                    Console.WriteLine($"E text {ex.GetType().Name} {Clean(ex.Message)}");
                }

                for (int i = 0; i < n && phases != "text"; i++)
                {
                    Console.WriteLine($"S {i.ToString(ci)}");
                    sw.Restart();
                    try
                    {
                        var list = new PdfDisplayList();
                        doc.Pages[i].Interpret(list);
                        list.Replay(new Sink());
                    }
                    catch (PdfException ex)
                    {
                        Console.WriteLine($"E page {i.ToString(ci)} {ex.GetType().Name} {Clean(ex.Message)}");
                    }

                    Console.WriteLine($"P {i.ToString(ci)} {sw.ElapsedMilliseconds.ToString(ci)}");
                }
            }

            Console.WriteLine("DONE");
            return 0;
        }
#pragma warning disable CA1031 // Anything else escaping the library is a finding; the parent reads the X line.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Console.WriteLine($"X {ex.GetType().Name} {Clean(ex.Message)} {Clean(ex.StackTrace)}");
            return 4;
        }
    }

    // ---------------------------------------------------------------- parent

    private sealed record ChildRun(int ExitCode, List<string> Lines, bool Killed, long PeakKb, string Stderr, double Seconds);

    private static readonly string Self = typeof(Program).Assembly.Location;
    private static readonly bool HaveTime = File.Exists("/usr/bin/time");
    private static long _gateCheckedAt;
    private static readonly object GateLock = new();

    /// <summary>Waits while a benchmark process is running (checked at most every 10 s).</summary>
    private static void Gate()
    {
        if (Environment.GetEnvironmentVariable("ONEPDF_NO_PAUSE") == "1")
        {
            return;
        }

        while (true)
        {
            lock (GateLock)
            {
                if (Stopwatch.GetElapsedTime(_gateCheckedAt).TotalSeconds < 10 && _gateCheckedAt != 0)
                {
                    return;
                }

                bool busy = false;
                foreach (string d in Directory.EnumerateDirectories("/proc"))
                {
                    try
                    {
                        string cmd = File.ReadAllText(Path.Combine(d, "cmdline"));
                        if (cmd.Contains("BenchmarkDotNet", StringComparison.Ordinal) || cmd.Contains("OnePdf.Benchmarks", StringComparison.Ordinal))
                        {
                            busy = true;
                            break;
                        }
                    }
                    catch (IOException)
                    {
                        // the process went away
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }

                if (!busy)
                {
                    _gateCheckedAt = Stopwatch.GetTimestamp();
                    return;
                }
            }

            Console.Error.WriteLine("benchmark running; pausing 30 s");
            Thread.Sleep(30000);
        }
    }

    private static ChildRun RunChild(string[] childArgs, double idleSeconds, double hardSeconds, bool limitHeap)
    {
        Gate();
        string rssFile = Path.Combine(Path.GetTempPath(), "onepdf-opens-" + Guid.NewGuid().ToString("N"));
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        string host = Environment.ProcessPath!;
        if (HaveTime)
        {
            psi.FileName = "/usr/bin/time";
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("%M");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(rssFile);
            psi.ArgumentList.Add(host);
        }
        else
        {
            psi.FileName = host;
        }

        psi.ArgumentList.Add(Self);
        foreach (string a in childArgs)
        {
            psi.ArgumentList.Add(a);
        }

        if (limitHeap)
        {
            // A runaway comparison engine ends with an OutOfMemoryException instead of taking the machine down.
            psi.Environment["DOTNET_GCHeapHardLimit"] = "0x200000000";
        }

        var lines = new List<string>();
        var err = new StringBuilder();
        long last = Stopwatch.GetTimestamp();
        var total = Stopwatch.StartNew();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (lines)
                {
                    lines.Add(e.Data);
                    last = Stopwatch.GetTimestamp();
                }
            }
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null && err.Length < 400)
            {
                err.AppendLine(e.Data);
            }
        };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        bool killed = false;
        while (!p.WaitForExit(250))
        {
            double idle = Stopwatch.GetElapsedTime(Interlocked.Read(ref last)).TotalSeconds;
            if (idle > idleSeconds || total.Elapsed.TotalSeconds > hardSeconds)
            {
                killed = true;
                p.Kill(entireProcessTree: true);
                p.WaitForExit();
                break;
            }
        }

        p.WaitForExit();
        long peak = -1;
        if (HaveTime && File.Exists(rssFile))
        {
            foreach (string l in File.ReadLines(rssFile))
            {
                if (long.TryParse(l.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long kb))
                {
                    peak = kb;
                }
            }

            File.Delete(rssFile);
        }

        lock (lines)
        {
            return new ChildRun(p.ExitCode, [.. lines], killed, peak, err.ToString(), total.Elapsed.TotalSeconds);
        }
    }

    private static int Parent(string[] args)
    {
        string mode = args[0];
        string outDir = args[1];
        int jobs = 8;
        double timeout = 120;
        bool dropGlyphs = false;
        var corpora = new List<(string Label, string Dir)>();
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--jobs")
            {
                jobs = int.Parse(args[++i], CultureInfo.InvariantCulture);
            }
            else if (args[i] == "--drop-glyphs")
            {
                dropGlyphs = true;
            }
            else if (args[i] == "--timeout")
            {
                timeout = double.Parse(args[++i], CultureInfo.InvariantCulture);
            }
            else
            {
                int eq = args[i].IndexOf('=', StringComparison.Ordinal);
                corpora.Add((args[i][..eq], args[i][(eq + 1)..]));
            }
        }

        Directory.CreateDirectory(outDir);
        var rows = new List<string[]>();
        var rowLock = new object();
        var summary = new StringBuilder();
        foreach (var (label, dir) in corpora)
        {
            string[] files = [.. Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)];
            Console.Error.WriteLine($"{mode} {label}: {files.Length} files");
            var results = new string[files.Length][];
            int done = 0;
            Parallel.For(0, files.Length, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i =>
            {
                string rel = Path.GetRelativePath(dir, files[i]);
                results[i] = mode == "opens" ? OpensRow(label, rel, files[i], timeout) : SweepRow(label, rel, files[i], dropGlyphs);
                int d = Interlocked.Increment(ref done);
                if (d % 500 == 0)
                {
                    Console.Error.WriteLine($"  {label}: {d}/{files.Length}");
                }
            });
            lock (rowLock)
            {
                rows.AddRange(results);
            }

            summary.Append(mode == "opens" ? OpensSummary(label, results) : SweepSummary(label, results));
        }

        string[] header = mode == "opens"
            ? ["corpus", "file", "bytes", .. Engines.SelectMany(e => Fields.Select(f => e + "_" + f))]
            : ["corpus", "file", "bytes", "result", "pages", "typed_errors", "text_ms", "worst_page_ms", "worst_page", "peak_mib", "seconds", "findings", "detail"];
        File.WriteAllLines(Path.Combine(outDir, mode + ".csv"), rows.Prepend(header).Select(r => string.Join(',', r)));
        File.WriteAllText(Path.Combine(outDir, mode + "-summary.md"), summary.ToString());
        Console.Write(summary);
        return 0;
    }

    private static readonly string[] Fields = ["opened", "pages", "text_ok", "class", "stage", "type", "ms", "message"];
    private static readonly string[] Engines = ["onepdf", "pdfpig", "pdfsharp"];

    private static string[] OpensRow(string label, string rel, string path, double timeout)
    {
        var row = new List<string> { label, rel, new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture) };
        foreach (string engine in Engines)
        {
            ChildRun r = RunChild(["--child-open", engine, path], timeout, timeout, limitHeap: true);
            string? result = r.Lines.FirstOrDefault(l => l.StartsWith("RESULT\t", StringComparison.Ordinal));
            if (result is not null && !r.Killed)
            {
                row.AddRange(result.Split('\t').Skip(1).Select(Clean));
            }
            else
            {
                string cls = r.Killed ? "timeout" : "crash";
                row.AddRange(["0", "-1", "0", cls, "", "exit" + r.ExitCode.ToString(CultureInfo.InvariantCulture),
                    ((long)(r.Seconds * 1000)).ToString(CultureInfo.InvariantCulture), Clean(r.Stderr)]);
            }
        }

        return [.. row];
    }

    private const int ColPerEngine = 8;

    private static string Col(string[] row, int engine, int field) => row[3 + (engine * ColPerEngine) + field];

    private static bool Opens(string[] row, int engine) => Col(row, engine, 0) == "1" && Col(row, engine, 2) == "1";

    private static string OpensSummary(string label, string[][] rows)
    {
        var sb = new StringBuilder();
        int both = 0, onlyOne = 0, onlyPig = 0, neither = 0, bothPw = 0, sharpOpen = 0, sharpOnly = 0;
        var pigOnly = new List<string>();
        var oneFail = new SortedDictionary<string, int>();
        foreach (string[] r in rows)
        {
            bool one = Opens(r, 0), pig = Opens(r, 1), sharp = Opens(r, 2);
            if (sharp)
            {
                sharpOpen++;
                if (!one && !pig)
                {
                    sharpOnly++;
                }
            }

            if (!one && !pig && Col(r, 0, 3) == "password" && Col(r, 1, 3) == "password")
            {
                bothPw++;
                continue;
            }

            if (one && pig)
            {
                both++;
            }
            else if (one)
            {
                onlyOne++;
            }
            else if (pig)
            {
                onlyPig++;
                pigOnly.Add($"{r[1]} (OnePDF: {Col(r, 0, 3)}/{Col(r, 0, 4)}/{Col(r, 0, 5)} {Col(r, 0, 7)})");
            }
            else
            {
                neither++;
            }

            if (!one)
            {
                string k = $"{Col(r, 0, 3)}/{Col(r, 0, 4)}/{Col(r, 0, 5)}";
                oneFail[k] = oneFail.GetValueOrDefault(k) + 1;
            }
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"## {label} ({rows.Length} files)");
        sb.AppendLine();
        sb.AppendLine("| | PdfPig opens | PdfPig fails |");
        sb.AppendLine("|---|---:|---:|");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| OnePDF opens | {both} | {onlyOne} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| OnePDF fails | {onlyPig} | {neither} |");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Both engines ask for a password (left out of the table): {bothPw}. PDFsharp (information only) opens {sharpOpen}; {sharpOnly} of those neither other engine opens.");
        sb.AppendLine();
        if (oneFail.Count > 0)
        {
            sb.AppendLine("OnePDF failures by class/stage/type: " + string.Join("; ", oneFail.Select(kv => $"{kv.Key} = {kv.Value}")));
            sb.AppendLine();
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"PdfPig opens, OnePDF does not ({pigOnly.Count}):");
        foreach (string f in pigOnly)
        {
            sb.AppendLine("- " + f);
        }

        sb.AppendLine();
        return sb.ToString();
    }

    private static string[] SweepRow(string label, string rel, string path, bool dropGlyphs)
    {
        ChildRun r = RunChild(dropGlyphs ? ["--child-sweep", path, "drop-glyphs"] : ["--child-sweep", path], idleSeconds: 60, hardSeconds: 3600, limitHeap: false);
        var inv = CultureInfo.InvariantCulture;
        var findings = new List<string>();
        string result = "ok";
        int pages = 0, typed = 0;
        long textMs = 0, worst = 0;
        int worstPage = -1;
        string detail = "";
        bool done = false, openFailed = false;
        foreach (string l in r.Lines)
        {
            string[] t = l.Split(' ', 4);
            switch (t[0])
            {
                case "N":
                    pages = int.Parse(t[1], inv);
                    break;
                case "T":
                    textMs = long.Parse(t[1], inv);
                    break;
                case "P":
                    long ms = long.Parse(t[2], inv);
                    if (ms > worst)
                    {
                        worst = ms;
                        worstPage = int.Parse(t[1], inv);
                    }

                    break;
                case "E":
                    typed++;
                    openFailed |= t[1] == "open";
                    detail = Clean(l);
                    break;
                case "X":
                    findings.Add("unexpected-exception");
                    detail = Clean(l);
                    break;
                case "DONE":
                    done = true;
                    break;
            }
        }

        if (r.Killed)
        {
            findings.Add("hang");
            detail = "no progress; last line: " + (r.Lines.Count > 0 ? Clean(r.Lines[^1]) : "none");
        }
        else if (r.ExitCode != 0 && !findings.Contains("unexpected-exception"))
        {
            findings.Add("crash");
            detail = $"exit {r.ExitCode.ToString(inv)} {Clean(r.Stderr)}";
        }
        else if (!done && !openFailed && !findings.Contains("unexpected-exception"))
        {
            findings.Add("incomplete");
        }

        if (worst > SlowSeconds * 1000)
        {
            findings.Add("slow-page");
        }

        if (textMs > SlowSeconds * 1000)
        {
            findings.Add("slow-text");
        }

        if (r.PeakKb > PeakLimitKb)
        {
            findings.Add("peak-over-1GiB");
        }

        if (findings.Count > 0)
        {
            result = "FINDING";
        }
        else if (openFailed)
        {
            result = "open-error";
        }

        return [label, rel, new FileInfo(path).Length.ToString(inv), result, pages.ToString(inv), typed.ToString(inv),
            textMs.ToString(inv), worst.ToString(inv), worstPage.ToString(inv), (r.PeakKb / 1024).ToString(inv),
            r.Seconds.ToString("F1", inv), string.Join('|', findings), detail];
    }

    private static string SweepSummary(string label, string[][] rows)
    {
        var inv = CultureInfo.InvariantCulture;
        long pages = rows.Sum(r => long.Parse(r[4], inv));
        string[] worstRow = rows.OrderByDescending(r => long.Parse(r[7], inv)).First();
        string[] rssRow = rows.OrderByDescending(r => long.Parse(r[9], inv)).First();
        string[] textRow = rows.OrderByDescending(r => long.Parse(r[6], inv)).First();
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"## {label}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- files: {rows.Length}; opened and swept: {rows.Count(r => r[3] != "open-error" && r[3] != "FINDING") + rows.Count(r => r[3] == "FINDING" && r[4] != "0")}; typed open errors: {rows.Count(r => r[3] == "open-error")}; pages interpreted: {pages}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- worst page: {double.Parse(worstRow[7], inv) / 1000:F2} s ({worstRow[1]} page {worstRow[8]})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- worst whole-document text pass: {double.Parse(textRow[6], inv) / 1000:F2} s ({textRow[1]})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- worst peak RSS: {rssRow[9]} MiB ({rssRow[1]})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- typed page/text errors (lenient, expected on broken files): {rows.Sum(r => long.Parse(r[5], inv))}");
        var bad = rows.Where(r => r[3] == "FINDING").ToList();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **findings: {bad.Count}**");
        foreach (string[] r in bad)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  - {r[1]}: {r[11]} ({r[12]})");
        }

        sb.AppendLine();
        return sb.ToString();
    }
}
