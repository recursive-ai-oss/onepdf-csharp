using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace OnePdf.Layout.Tests;

internal static class Support
{
    public static readonly PdfDocumentWriterOptions Fixed = new() { DocumentId = new byte[16], LeaveOpen = true };

    public static string Root([CallerFilePath] string here = "")
    {
        string? dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "OnePdf.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Repository root not found.");
    }

    public static byte[] Jpeg() => File.ReadAllBytes(Path.Combine(Root(), "testdata", "generated", "images", "rgb16.jpg"));

    public static byte[] Save(LayoutDocument layout, string? dump = null)
    {
        using var ms = new MemoryStream();
        layout.Save(ms, Fixed);
        byte[] bytes = ms.ToArray();
        if (dump is not null && Environment.GetEnvironmentVariable("ONEPDF_LAYOUT_DUMP") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, dump + ".pdf"), bytes);
        }

        return bytes;
    }

    public static LayoutDocument Single(Element content, PageSize? size = null, double margin = 20, Action<LayoutPage>? setup = null)
    {
        var doc = new LayoutDocument();
        var page = new LayoutPage { Size = size ?? new PageSize(300, 400), Margin = Insets.All(margin), Content = content is Column ? content : new Column { Items = { content } } };
        setup?.Invoke(page);
        doc.Pages.Add(page);
        return doc;
    }

    /// <summary>Lays out into a fresh document and returns each page's body tree.</summary>
    public static List<Placed> Pages(LayoutDocument layout, out List<LayoutDiagnostic> notes)
    {
        var bodies = new List<Placed>();
        using PdfDocument doc = PdfDocument.CreateEmpty();
        layout.RenderCore(doc, (_, _, body, _, _) => bodies.Add(body!));
        notes = [.. layout.Diagnostics];
        return bodies;
    }

    public static IEnumerable<Placed> Walk(Placed p)
    {
        yield return p;
        foreach (Placed c in p.Children)
        {
            foreach (Placed x in Walk(c))
            {
                yield return x;
            }
        }
    }

    public static (int Code, string Output) Qpdf(byte[] pdf, string scratch)
    {
        string? qpdf = Environment.GetEnvironmentVariable("ONEPDF_QPDF");
        if (qpdf is null || !File.Exists(qpdf))
        {
            return (0, "qpdf not available");
        }

        string path = Path.Combine(scratch, "layout-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            var psi = new ProcessStartInfo(qpdf) { RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("--check");
            psi.ArgumentList.Add(path);
            using Process p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, output);
        }
        finally
        {
            File.Delete(path);
        }
    }

    public static string Normalize(string s) => string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
