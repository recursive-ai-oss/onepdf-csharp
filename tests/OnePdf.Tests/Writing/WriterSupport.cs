using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace OnePdf.Tests.Writing;

/// <summary>Finds test files, runs qpdf as a black box and reads a file back for comparison.</summary>
internal static class WriterSupport
{
    public static string Root([CallerFilePath] string source = "")
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

    public static IEnumerable<string> AllPdfs() =>
        Directory.EnumerateFiles(Path.Combine(Root(), "testdata"), "*.pdf", SearchOption.AllDirectories).Order(StringComparer.Ordinal);

    public static IEnumerable<object[]> CorpusCases() => AllPdfs().Select(f => new object[] { Path.GetRelativePath(Root(), f).Replace('\\', '/') });

    public static string Generated(string name) => Path.Combine(Root(), "testdata", "generated", "pdf", name);

    // Encrypted generated files name the password that opens them in their truth sidecar.
    public static string? PasswordFor(string file)
    {
        if (Path.GetFileName(file) == "encrypted-objstm-aes128.pdf")
        {
            return "up";
        }

        string truth = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(file))!, "truth", Path.GetFileNameWithoutExtension(file) + ".json");
        if (!File.Exists(truth))
        {
            return null;
        }

        using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(truth));
        return json.RootElement.TryGetProperty("passwords", out JsonElement pw) && pw.TryGetProperty("user", out JsonElement user) ? user.GetString() : null;
    }

    public static string? Qpdf()
    {
        string? p = Environment.GetEnvironmentVariable("ONEPDF_QPDF");
        return p is not null && File.Exists(p) ? p : null;
    }

    /// <summary>Runs <c>qpdf --check</c>: its exit code (0 clean, 3 warnings, 2 errors; 0 when qpdf is not available) and output.</summary>
    public static (int Code, string Output) QpdfRun(byte[] pdf, string? password = null)
    {
        string? qpdf = Qpdf();
        if (qpdf is null)
        {
            return (0, string.Empty);
        }

        string tmp = Path.Combine(Path.GetTempPath(), "onepdf-w-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(tmp, pdf);
        try
        {
            var psi = new ProcessStartInfo(qpdf) { RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("--check");
            if (password is not null)
            {
                psi.ArgumentList.Add("--password=" + password);
            }

            psi.ArgumentList.Add(tmp);
            using Process p = Process.Start(psi)!;
            Task<string> err = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd() + err.GetAwaiter().GetResult();
            p.WaitForExit();
            return (p.ExitCode, output);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    /// <summary>
    /// Fails when qpdf finds more wrong with <paramref name="output"/> than with <paramref name="input"/>: a clean input must give a clean
    /// output, while a file that was damaged to begin with may keep the damage it had (a stream cut short stays cut short).
    /// </summary>
    public static void AssertNoWorse(byte[] input, byte[] output, string? password, string what)
    {
        (int inCode, _) = QpdfRun(input, password);
        (int outCode, string text) = QpdfRun(output, password);
        Assert.True(outCode <= inCode, $"{what}: qpdf exit {outCode} (input {inCode}): {text}");
    }

    public static byte[] Save(PdfDocument doc, PdfSaveOptions? options = null)
    {
        using var ms = new MemoryStream();
        doc.Save(ms, options);
        return ms.ToArray();
    }

    public static byte[] SaveIncremental(PdfDocument doc, PdfSaveOptions? options = null)
    {
        using var ms = new MemoryStream();
        doc.SaveIncremental(ms, options);
        return ms.ToArray();
    }

    public static PdfOpenOptions Editable(string? password = null) => new() { AllowEditing = true, Password = password };
}
