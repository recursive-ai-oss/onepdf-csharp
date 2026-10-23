using System.Diagnostics;

namespace OnePdf.Tests.Creation;

/// <summary>Shared helpers of the content creation tests: fonts, a collecting device and black-box tools.</summary>
internal static class CreationSupport
{
    public static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
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

    public static byte[]? Font(string name)
    {
        string? dir = Environment.GetEnvironmentVariable("ONEPDF_FONT_CACHE");
        if (dir is null || !Directory.Exists(dir))
        {
            return null;
        }

        string? found = Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
        return found is null ? null : File.ReadAllBytes(found);
    }

    public static byte[] RequireFont(string name)
    {
        byte[]? f = Font(name);
        if (f is null)
        {
            Assert.Skip($"{name} is not in ONEPDF_FONT_CACHE.");
        }

        return f!;
    }

    public static byte[] Save(PdfDocument doc, PdfSaveOptions? options = null)
    {
        using var ms = new MemoryStream();
        doc.Save(ms, options ?? new PdfSaveOptions { DocumentId = new byte[16] });
        return ms.ToArray();
    }

    public static string AllText(byte[] pdf)
    {
        using PdfDocument d = PdfDocument.Open(pdf);
        return string.Join("\n", d.ExtractText().Select(p => p.Text));
    }

    public static string Normalize(string s) => string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static (int Code, string Output) Qpdf(byte[] pdf)
    {
        string? qpdf = Environment.GetEnvironmentVariable("ONEPDF_QPDF");
        if (qpdf is null || !File.Exists(qpdf))
        {
            return (0, "qpdf not available");
        }

        string path = Path.Combine(Path.GetTempPath(), "onepdf-create-" + Guid.NewGuid().ToString("N") + ".pdf");
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

    public static bool HasQpdf => Environment.GetEnvironmentVariable("ONEPDF_QPDF") is { } q && File.Exists(q);

    /// <summary>Records what the interpreter reports in output (device) space.</summary>
    public sealed class Collector : PdfDevice
    {
        public List<(PdfBounds Bounds, bool Fill, bool Stroke, PdfGraphicsState State)> Paths { get; } = [];

        public List<(string Text, double X, double Y, double Size, string? FontName)> Runs { get; } = [];

        public List<(PdfMatrix Ctm, PdfImage Image)> Images { get; } = [];

        public override void FillPath(in PdfPath path, bool evenOdd, in PdfGraphicsState state) => Paths.Add((Transformed(path, state.Ctm), true, false, state));

        public override void StrokePath(in PdfPath path, in PdfGraphicsState state) => Paths.Add((Transformed(path, state.Ctm), false, true, state));

        public override void ShowGlyphRun(in PdfGlyphRun run, in PdfGraphicsState state)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < run.Glyphs.Length; i++)
            {
                sb.Append(run.Glyphs.Span[i].Unicode);
            }

            PdfMatrix m = run.GlyphMatrix(0).Then(state.Ctm);
            Runs.Add((sb.ToString(), m.E, m.F, run.FontSize, run.FontName));
        }

        public override void DrawImage(in PdfImage image, in PdfGraphicsState state) => Images.Add((state.Ctm, image));

        private static PdfBounds Transformed(in PdfPath path, in PdfMatrix ctm)
        {
            ReadOnlySpan<double> p = path.Points.Span;
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            for (int i = 0; i + 1 < p.Length; i += 2)
            {
                (double x, double y) = ctm.Transform(p[i], p[i + 1]);
                x0 = Math.Min(x0, x);
                y0 = Math.Min(y0, y);
                x1 = Math.Max(x1, x);
                y1 = Math.Max(y1, y);
            }

            return new PdfBounds(x0, y0, x1, y1);
        }
    }
}
