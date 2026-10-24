using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BenchmarkDotNet.Running;
using OnePdf;

namespace LayoutBench;

internal static class Program
{
    public static readonly string[] RowNames = ["invoice", "report50", "table10k", "book500", "mixed20", "throughput32"];

    public static IEngine Engine(string name) => name switch
    {
        "onepdf" => new OnePdfEngine(),
        "migradoc" => new MigraDocEngine(),
        "itext" => new ITextEngine(),
        _ => throw new ArgumentException("unknown engine " + name),
    };

    public static readonly string[] EngineNames = ["onepdf", "migradoc", "itext"];

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "rss")
        {
            return Rss(args);
        }

        if (args.Length > 1 && args[0] == "profile")
        {
            return Profile.Run(args[1]);
        }

        if (args.Length > 1 && args[0] == "check")
        {
            return Check(args[1], args.Length > 2 ? args[2].Split(',') : EngineNames);
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }

    /// <summary>One row once in this process, so <c>/usr/bin/time -v</c> around it gives the peak resident set and the wall time includes the first (cold) document.</summary>
    private static int Rss(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: rss <onepdf|migradoc|itext> <invoice|report50|table10k|book500|mixed20|throughput32>");
            return 2;
        }

        IEngine e = Engine(args[1]);
        var clock = Stopwatch.StartNew();
        long bytes;
        if (args[2] == "throughput32")
        {
            long total = 0;
            e.Throughput(32, () => new SizeSink(n => System.Threading.Interlocked.Add(ref total, n)));
            bytes = total;
        }
        else
        {
            var sink = new CountingStream();
            e.Render(ParseRow(args[2]), sink);
            bytes = sink.Count;
        }

        Console.WriteLine($"ok {args[1]} {args[2]} bytes={bytes} wall_ms={clock.Elapsed.TotalMilliseconds:F0}");
        return 0;
    }

    public static Workload ParseRow(string s) => s switch
    {
        "invoice" => Workload.Invoice,
        "report50" => Workload.Report50,
        "table10k" => Workload.Table10k,
        "book500" => Workload.Book500,
        "mixed20" => Workload.Mixed20,
        _ => throw new ArgumentException("unknown row " + s),
    };

    // --- output check -----------------------------------------------------------------------------------------------------------

    private static string Norm(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private static string[] Probes(Workload row)
    {
        switch (row)
        {
            case Workload.Invoice:
                return ["INVOICE 2026-0417", "Contoso Retail GmbH", "Total", "Thank you for your business."];
            case Workload.Report50:
                return ["Quarterly report", "Section 1:", "Section 50:", "Operations", "Page"];
            case Workload.Table10k:
                return ["100000", "104999", "109999", "Settled"];
            case Workload.Book500:
            {
                var r = new Content.Rng(21);
                string p = Content.Paragraph(r, 90 + r.Next(60));
                string[] w = p.Split(' ');
                return [string.Join(' ', w.Take(6)), string.Join(' ', w.TakeLast(4))];
            }

            default:
            {
                var r = new Content.Rng(31);
                string p = Content.Paragraph(r, 70);
                string[] w = p.Split(' ');
                return [string.Join(' ', w.Take(6)), "مرحبا", "नमस्ते"];
            }
        }
    }

    private static int Check(string outDir, string[] engines)
    {
        Directory.CreateDirectory(outDir);
        Console.WriteLine("engine,row,bytes,pages,probes_found,probes_total,missing,seconds");
        foreach (string en in engines)
        {
            IEngine e = Engine(en);
            foreach (Workload row in Enum.GetValues<Workload>())
            {
                string path = Path.Combine(outDir, $"{en}-{row.ToString().ToLowerInvariant()}.pdf");
                var sw = Stopwatch.StartNew();
                try
                {
                    using (FileStream f = File.Create(path))
                    {
                        e.Render(row, f);
                    }

                    double secs = sw.Elapsed.TotalSeconds;
                    using PdfDocument doc = PdfDocument.Open(path);
                    var all = new System.Text.StringBuilder();
                    foreach (OnePdf.Text.PdfPageText t in doc.ExtractText())
                    {
                        all.Append(Norm(t.Text)).Append(' ');
                    }

                    string text = all.ToString();
                    string[] probes = Probes(row);
                    var missing = probes.Where(p => !text.Contains(p, StringComparison.Ordinal)).ToList();
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{en},{row},{new FileInfo(path).Length},{doc.Pages.Count},{probes.Length - missing.Count},{probes.Length},\"{string.Join('|', missing)}\",{secs:F2}"));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Console.WriteLine($"{en},{row},,,,,\"FAILED: {ex.GetType().Name}: {ex.Message.Replace('\n', ' ').Replace('"', '\'')}\",{sw.Elapsed.TotalSeconds:F2}");
                }
            }
        }

        return 0;
    }
}
