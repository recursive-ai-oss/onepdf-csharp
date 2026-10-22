using System;
using System.Diagnostics;

namespace OnePdf.Benchmarks;

/// <summary>
/// <c>rss check</c> opens every input with both libraries. <c>rss &lt;engine&gt; &lt;row&gt; &lt;input&gt; [degree]</c>: runs one row once in this process and prints the result and wall time, so
/// <c>/usr/bin/time -v</c> around it gives the peak resident set. Engines: onepdf, pdfpig, pdfium, mutool. Rows: open, metadata,
/// text, words, interpret, encrypted-open, encrypted-text. Exit code 3 means the row is not available yet.
/// </summary>
internal static class PeakMemoryRunner
{
    /// <summary>Opens every input with both libraries and prints the page counts, so a damaged or missing input shows up before a run.</summary>
    private static int Check()
    {
        foreach (string name in new[] { "small", "small-metadata", "pages-1000", "broken", "born-300", "aes256", "scan-ocr", "cjk", "vector-page", "text-page" })
        {
            string path = BenchData.Resolve(name);
            string? pw = name == "aes256" ? BenchData.EncryptedPassword : null;
            string one;
            try
            {
                one = Ops.OnePdfOpen(path, pw).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (PdfException ex)
            {
                one = ex.GetType().Name;
            }

            Console.WriteLine($"{name,-15} {new System.IO.FileInfo(path).Length,10:N0} bytes  onepdf pages={one}  pdfpig pages={Ops.PdfPigOpen(path, pw)}");
        }

        return 0;
    }

    public static int Run(string[] args)
    {
        if (args.Length > 1 && args[1] == "check")
        {
            return Check();
        }

        if (args.Length < 4)
        {
            Console.Error.WriteLine("usage: rss <onepdf|pdfpig|pdfium|mutool> <row> <input> [1|default]");
            return 2;
        }

        string engine = args[1], row = args[2];
        string name = args[3];
        int degree = BenchData.Degree(args.Length > 4 ? args[4] : "1");
        string path = BenchData.Resolve(name);
        string? pw = row.StartsWith("encrypted", StringComparison.Ordinal) ? BenchData.EncryptedPassword : null;
        var clock = Stopwatch.StartNew();
        long result;
        switch ((engine, row))
        {
            case ("onepdf", "open" or "encrypted-open"): result = Ops.OnePdfOpen(path, pw); break;
            case ("onepdf", "metadata"): result = Ops.OnePdfMetadata(path); break;
            case ("onepdf", "interpret"): result = Ops.OnePdfInterpret(path); break;
            case ("onepdf", "text" or "encrypted-text") when OnePdfTextSeam.Available: result = Ops.OnePdfText(path, degree, pw); break;
            case ("onepdf", "words") when OnePdfTextSeam.Available: result = Ops.OnePdfWords(path, degree, pw); break;
            case ("pdfpig", "open" or "encrypted-open"): result = Ops.PdfPigOpen(path, pw); break;
            case ("pdfpig", "metadata"): result = Ops.PdfPigMetadata(path); break;
            case ("pdfpig", "text" or "encrypted-text"): result = Ops.PdfPigText(path, degree, pw); break;
            case ("pdfpig", "words"): result = Ops.PdfPigWords(path, degree, pw); break;
            case ("pdfium", "text"): result = Ops.PdfiumText(path); break;
            case ("mutool", "text") when Ops.MutoolPath is not null: result = Ops.MutoolText(path); break;
            default:
                Console.WriteLine($"pending {engine} {row} {name}");
                return 3;
        }

        Console.WriteLine($"ok {engine} {row} {name} degree={degree} result={result} wall_ms={clock.Elapsed.TotalMilliseconds:F0}");
        return 0;
    }
}
