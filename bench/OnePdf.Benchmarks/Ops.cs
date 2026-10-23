using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Docnet.Core;
using Docnet.Core.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;

namespace OnePdf.Benchmarks;

/// <summary>Where the benchmark input files live. See <c>bench/README.md</c>.</summary>
internal static class BenchData
{
    /// <summary>Directory of the large generated inputs (<c>ONEPDF_BENCH_DATA</c>), made by <c>tools/corpus/make_bench_inputs.py</c>.</summary>
    public static string Large => Environment.GetEnvironmentVariable("ONEPDF_BENCH_DATA")
        ?? throw new InvalidOperationException("Set ONEPDF_BENCH_DATA to the directory made by tools/corpus/make_bench_inputs.py.");

    /// <summary>The committed <c>testdata</c> directory, found from <c>ONEPDF_TESTDATA</c> or by walking up from the working directory.</summary>
    public static string TestData
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("ONEPDF_TESTDATA");
            if (env is not null)
            {
                return env;
            }

            for (DirectoryInfo? d = new(Directory.GetCurrentDirectory()); d is not null; d = d.Parent)
            {
                string candidate = Path.Combine(d.FullName, "testdata");
                if (Directory.Exists(Path.Combine(candidate, "generated")))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException("Run from inside the checkout or set ONEPDF_TESTDATA.");
        }
    }

    /// <summary>The password of the AES-256 input.</summary>
    public const string EncryptedPassword = "bench";

    /// <summary>Resolves a short input name to a path. Names are listed in <c>bench/README.md</c>.</summary>
    public static string Resolve(string name) => name switch
    {
        "small" => Path.Combine(TestData, "generated", "pdf", "lo-report.pdf"),
        "small-metadata" => Path.Combine(TestData, "generated", "pdf", "raw-metadata.pdf"),
        "pages-1000" => Path.Combine(Large, "pages-1000.pdf"),
        "broken" => Path.Combine(Large, "born-300-broken.pdf"),
        "born-300" => Path.Combine(Large, "born-300.pdf"),
        "aes256" => Path.Combine(Large, "born-300-aes256.pdf"),
        "scan-ocr" => Path.Combine(Large, "scan-ocr-40.pdf"),
        "cjk" => Path.Combine(Large, "cjk-120.pdf"),
        "vector-page" => Path.Combine(Large, "vector-page.pdf"),
        "text-page" => Path.Combine(Large, "text-page.pdf"),
        _ => throw new ArgumentException("unknown input " + name),
    };

    /// <summary>Degree of parallelism for a row: "1", or "default" (the library's default, min(4, cores)).</summary>
    public static int Degree(string degree) => degree == "1" ? 1 : Math.Min(4, Environment.ProcessorCount);
}

/// <summary>
/// The work of every row, written once so BenchmarkDotNet and the peak-memory runner measure the same thing. Each method
/// returns a number that depends on the result so nothing is optimized away. PdfPig and PDFium (through Docnet.Core) are
/// compiled packages used through their public APIs.
/// </summary>
internal static class Ops
{
    // ---- OnePDF

    public static long OnePdfOpen(string path, string? password = null)
    {
        using var doc = PdfDocument.Open(path, new PdfOpenOptions { Password = password });
        return doc.PageCount;
    }

    public static long OnePdfMetadata(string path)
    {
        using var doc = PdfDocument.Open(path);
        DocumentInfo info = doc.Info;
        long n = doc.PageCount;
        n += (info.Title?.Length ?? 0) + (info.Author?.Length ?? 0) + (info.Subject?.Length ?? 0) + (info.Keywords?.Length ?? 0)
            + (info.Creator?.Length ?? 0) + (info.Producer?.Length ?? 0) + (info.CreationDate is null ? 0 : 1) + (info.ModificationDate is null ? 0 : 1);
        n += doc.Xmp?.Title?.Length ?? 0;
        return n;
    }

    public static long OnePdfInterpret(string path)
    {
        using var doc = PdfDocument.Open(path);
        var list = new PdfDisplayList();
        doc.Pages[0].Interpret(list);
        return list.ItemCount;
    }

    public static long OnePdfText(string path, int degree, string? password = null, bool retainGlyphs = false)
    {
        using var doc = PdfDocument.Open(path, new PdfOpenOptions { Password = password });
        return OnePdfTextSeam.ExtractText(doc, degree, retainGlyphs);
    }

    public static long OnePdfWords(string path, int degree, string? password = null)
    {
        using var doc = PdfDocument.Open(path, new PdfOpenOptions { Password = password });
        return OnePdfTextSeam.Words(doc, degree);
    }

    // ---- PdfPig

    private static ParsingOptions PigOptions(string? password) => new()
    {
        UseLenientParsing = true,
        SkipMissingFonts = true,
        Passwords = password is null ? [] : [password],
    };

    /// <summary>Opens and counts pages. A file PdfPig cannot open counts as -1 (the row records that it failed).</summary>
    public static long PdfPigOpen(string path, string? password = null)
    {
        try
        {
            using var doc = UglyToad.PdfPig.PdfDocument.Open(path, PigOptions(password));
            return doc.NumberOfPages;
        }
        catch (PdfDocumentFormatException)
        {
            return -1;
        }
    }

    public static long PdfPigMetadata(string path)
    {
        using var doc = UglyToad.PdfPig.PdfDocument.Open(path, PigOptions(null));
        var info = doc.Information;
        long n = doc.NumberOfPages;
        n += (info.Title?.Length ?? 0) + (info.Author?.Length ?? 0) + (info.Subject?.Length ?? 0) + (info.Keywords?.Length ?? 0)
            + (info.Creator?.Length ?? 0) + (info.Producer?.Length ?? 0) + (info.CreationDate?.Length ?? 0) + (info.ModifiedDate?.Length ?? 0);
        n += doc.TryGetXmpMetadata(out var xmp) && xmp is not null ? 1 : 0;
        return n;
    }

    /// <summary>
    /// Page text, one page after another at degree 1. At a higher degree PdfPig has no parallel mode and its document object is
    /// not safe to share between threads, so each worker opens its own copy of the file and takes a contiguous run of pages.
    /// </summary>
    public static long PdfPigText(string path, int degree, string? password = null) =>
        PdfPigPages(path, degree, password, static (page, acc) => acc + page.Text.Length);

    public static long PdfPigWords(string path, int degree, string? password = null) =>
        PdfPigPages(path, degree, password, static (page, acc) =>
        {
            foreach (var w in page.GetWords())
            {
                acc += w.Text.Length + (long)(w.BoundingBox.Left + w.BoundingBox.Bottom);
            }

            return acc;
        });

    private static long PdfPigPages(string path, int degree, string? password, Func<UglyToad.PdfPig.Content.Page, long, long> perPage)
    {
        ParsingOptions options = PigOptions(password);
        int pages;
        using (var probe = UglyToad.PdfPig.PdfDocument.Open(path, options))
        {
            pages = probe.NumberOfPages;
        }

        if (degree <= 1)
        {
            using var doc = UglyToad.PdfPig.PdfDocument.Open(path, options);
            long total = 0;
            for (int p = 1; p <= pages; p++)
            {
                total = perPage(doc.GetPage(p), total);
            }

            return total;
        }

        long sum = 0;
        Parallel.For(0, degree, new ParallelOptions { MaxDegreeOfParallelism = degree }, w =>
        {
            int from = (int)((long)pages * w / degree) + 1, to = (int)((long)pages * (w + 1) / degree);
            using var doc = UglyToad.PdfPig.PdfDocument.Open(path, options);
            long local = 0;
            for (int p = from; p <= to; p++)
            {
                local = perPage(doc.GetPage(p), local);
            }

            System.Threading.Interlocked.Add(ref sum, local);
        });
        return sum;
    }

    // ---- PDFium through Docnet.Core (information only; single-threaded by design of that wrapper)

    public static long PdfiumText(string path)
    {
        using var reader = DocLib.Instance.GetDocReader(path, new PageDimensions(1.0));
        long total = 0;
        int n = reader.GetPageCount();
        for (int i = 0; i < n; i++)
        {
            using var page = reader.GetPageReader(i);
            total += page.GetText().Length;
        }

        return total;
    }

    // ---- mutool, when installed (information only; includes starting the process)

    public static string? MutoolPath { get; } = FindOnPath("mutool");

    public static long MutoolText(string path)
    {
        var psi = new ProcessStartInfo(MutoolPath!) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (string a in new[] { "draw", "-q", "-F", "txt", "-o", "-", path })
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi)!;
        long n = 0;
        char[] buf = new char[1 << 16];
        int read;
        while ((read = p.StandardOutput.Read(buf, 0, buf.Length)) > 0)
        {
            n += read;
        }

        p.WaitForExit();
        return n;
    }

    private static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, exe)).FirstOrDefault(File.Exists);
}
