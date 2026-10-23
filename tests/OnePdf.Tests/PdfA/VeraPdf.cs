using System.Diagnostics;
using System.Text.RegularExpressions;

namespace OnePdf.Tests.PdfA;

/// <summary>
/// Runs the veraPDF command-line validator as a black box. The path comes from ONEPDF_VERAPDF; tests that need it skip when it is
/// unset or missing, so a plain checkout still passes.
/// </summary>
internal static class VeraPdf
{
    public static string? Path
    {
        get
        {
            string? p = Environment.GetEnvironmentVariable("ONEPDF_VERAPDF");
            return p is not null && File.Exists(p) ? p : null;
        }
    }

    /// <summary>What the validator printed to its log (warnings about font programs and the like) on this thread's last <see cref="Validate"/>.</summary>
    [ThreadStatic]
    public static string? LastLog;

    public static string Require()
    {
        string? p = Path;
        if (p is null)
        {
            Assert.Skip("veraPDF is not installed (set ONEPDF_VERAPDF to its launcher script).");
        }

        return p!;
    }

    /// <summary>Validates <paramref name="pdf"/> against <paramref name="flavour"/> (<c>2b</c>, <c>2u</c>, <c>3b</c>, <c>3u</c>); returns the failed rules, empty when compliant.</summary>
    public static List<string> Validate(byte[] pdf, string flavour)
    {
        string exe = Require();
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "onepdf-vera-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = System.IO.Path.Combine(dir, "doc.pdf");
        File.WriteAllBytes(file, pdf);
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("--flavour");
            psi.ArgumentList.Add(flavour);
            psi.ArgumentList.Add("--format");
            psi.ArgumentList.Add("xml");
            psi.ArgumentList.Add(file);
            psi.Environment["JAVA_OPTS"] = "-Xmx1g";
            using Process p = Process.Start(psi)!;
            Task<string> err = p.StandardError.ReadToEndAsync();
            string xml = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            LastLog = err.Result;
            var failures = new List<string>();
            if (!xml.Contains("isCompliant=\"true\"", StringComparison.Ordinal))
            {
                foreach (Match m in Regex.Matches(xml, "<rule [^>]*specification=\"([^\"]*)\"[^>]*clause=\"([^\"]*)\"[^>]*testNumber=\"([^\"]*)\"[^>]*>\\s*<description>([^<]*)</description>(?:.|\\n)*?<context>([^<]*)</context>"))
                {
                    failures.Add($"{m.Groups[2].Value}-{m.Groups[3].Value}: {m.Groups[4].Value} @ {m.Groups[5].Value}");
                }

                if (failures.Count == 0)
                {
                    failures.Add("not compliant; report: " + (xml.Length > 1500 ? xml[..1500] : xml));
                }
            }

            return failures;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    public static string Version()
    {
        string exe = Require();
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true };
        psi.ArgumentList.Add("--version");
        using Process p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return o.Split('\n')[0].Trim();
    }
}
