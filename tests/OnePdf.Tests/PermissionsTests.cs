using System.Diagnostics;

namespace OnePdf.Tests;

public class PermissionsTests
{
    private static string? FindQpdf()
    {
        string? env = Environment.GetEnvironmentVariable("QPDF");
        if (env is not null && File.Exists(env))
        {
            return env;
        }

        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string c = Path.Combine(dir, "qpdf");
            if (File.Exists(c))
            {
                return c;
            }
        }

        return null;
    }

    [Fact]
    public void PermissionsMatchQpdfShowEncryption()
    {
        string? qpdf = FindQpdf();
        Assert.SkipWhen(qpdf is null, "qpdf is not available (set QPDF or put it on PATH)");
        int checkedFiles = 0;
        foreach (string name in TestData.GeneratedNames().Where(static n => n.StartsWith("enc-", StringComparison.Ordinal) || n.Contains("encrypted", StringComparison.Ordinal)))
        {
            string user = TestData.Truth(name).GetProperty("passwords").GetProperty("user").GetString()!;
            string path = TestData.Generated(name);
            var psi = new ProcessStartInfo(qpdf!) { RedirectStandardOutput = true, UseShellExecute = false };
            psi.ArgumentList.Add("--show-encryption");
            psi.ArgumentList.Add("--password=" + user);
            psi.ArgumentList.Add(path);
            using Process p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();

            using PdfDocument doc = PdfDocument.Open(path, new PdfOpenOptions { Password = user });
            Check(output, "extract for accessibility", doc, PdfPermissions.ExtractForAccessibility, name);
            Check(output, "extract for any purpose", doc, PdfPermissions.CopyContent, name);
            Check(output, "print low resolution", doc, PdfPermissions.Print, name);
            Check(output, "print high resolution", doc, PdfPermissions.PrintHighQuality, name);
            Check(output, "modify document assembly", doc, PdfPermissions.AssembleDocument, name);
            Check(output, "modify forms", doc, PdfPermissions.FillForms, name);
            Check(output, "modify annotations", doc, PdfPermissions.ModifyAnnotations, name);
            Check(output, "modify other", doc, PdfPermissions.ModifyContents, name);
            checkedFiles++;
        }

        Assert.True(checkedFiles >= 10);
    }

    private static void Check(string qpdfOutput, string label, PdfDocument doc, PdfPermissions flag, string file)
    {
        string? line = qpdfOutput.Split('\n').FirstOrDefault(l => l.StartsWith(label + ":", StringComparison.Ordinal));
        Assert.True(line is not null, $"{file}: no '{label}' line");
        bool allowed = line!.Contains(": allowed", StringComparison.Ordinal);
        Assert.True(allowed == doc.Permissions.HasFlag(flag), $"{file}: {label} qpdf says {(allowed ? "allowed" : "not allowed")}");
    }

    [Fact]
    public void UnencryptedDocumentAllowsEverything()
    {
        using PdfDocument doc = PdfDocument.Open(PdfBuilder.Simple());
        Assert.Equal(PdfPermissions.All, doc.Permissions);
        Assert.Equal(PdfAccessLevel.NotEncrypted, doc.AccessLevel);
        Assert.Null(doc.Encryption);
    }
}
