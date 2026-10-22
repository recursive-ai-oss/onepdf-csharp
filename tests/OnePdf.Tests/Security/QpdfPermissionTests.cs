using System.Diagnostics;
using OnePdf.Security;


namespace OnePdf.Tests.Security;

/// <summary>
/// Compares the decoded permission bits with what the qpdf binary reports for the same file (black-box use of
/// <c>--show-encryption</c>). Skipped when qpdf is not installed; set ONEPDF_QPDF to its path.
/// </summary>
public class QpdfPermissionTests
{
    private static string? FindQpdf()
    {
        string? env = Environment.GetEnvironmentVariable("ONEPDF_QPDF");
        if (env is not null && File.Exists(env))
        {
            return env;
        }

        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, "qpdf");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string Run(string qpdf, params string[] args)
    {
        var psi = new ProcessStartInfo(qpdf) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return output;
    }

    private static readonly (string Line, SecurityPermissions Flag)[] Map =
    [
        ("print low resolution", SecurityPermissions.Print),
        ("print high resolution", SecurityPermissions.PrintHighQuality),
        ("extract for any purpose", SecurityPermissions.CopyContent),
        ("extract for accessibility", SecurityPermissions.ExtractForAccessibility),
        ("modify document assembly", SecurityPermissions.AssembleDocument),
        ("modify forms", SecurityPermissions.FillForms),
        ("modify annotations", SecurityPermissions.ModifyAnnotations),
        ("modify other", SecurityPermissions.ModifyContents),
    ];

    private static void Compare(string qpdf, string path, string password)
    {
        string report = Run(qpdf, "--show-encryption", "--password=" + password, path);
        EncryptedPdf pdf = EncryptedPdf.Parse(File.ReadAllBytes(path));
        SecurityResult r = StandardSecurityHandler.Authenticate(pdf.Parameters, pdf.Id, password);
        Assert.NotEqual(SecurityOutcome.Failed, r.Outcome);
        r.Decryptor!.Dispose();
        foreach ((string line, SecurityPermissions flag) in Map)
        {
            string? l = report.Split('\n').FirstOrDefault(x => x.StartsWith(line + ":", StringComparison.Ordinal));
            Assert.True(l is not null, "qpdf printed no line for " + line + " in " + path);
            bool allowed = l!.Contains("allowed", StringComparison.Ordinal) && !l.Contains("not allowed", StringComparison.Ordinal);
            Assert.True(allowed == r.Permissions.HasFlag(flag), path + ": " + line + " qpdf says '" + l + "', P=" + r.RawPermissions);
        }
    }

    [Fact]
    public void CommittedFilesMatchQpdf()
    {
        string? qpdf = FindQpdf();
        if (qpdf is null)
        {
            Assert.Skip("qpdf not found");
        }

        foreach (string f in new[] { "lo-encrypted-rc4-128.pdf", "enc-chrome-aes-128.pdf", "enc-chrome-aes-256.pdf", "enc-raw-aes-256-r5.pdf" })
        {
            Compare(qpdf, Path.Combine(EncryptedPdf.RepoRoot(), "testdata", "generated", "pdf", f), "user-pw");
        }
    }

    [Theory]
    [InlineData("256", "--print=none", "--modify=none", "--extract=n", "--accessibility=n")]
    [InlineData("256", "--print=low", "--modify=annotate", "--extract=y", "--accessibility=n")]
    [InlineData("256", "--print=full", "--modify=form", "--extract=n", "--accessibility=y")]
    [InlineData("256", "--print=full", "--modify=assembly", "--extract=y", "--accessibility=y")]
    [InlineData("128", "--print=low", "--modify=assembly", "--extract=n", "--accessibility=y")]
    [InlineData("128", "--print=none", "--modify=annotate", "--extract=y", "--accessibility=n")]
    public void GeneratedRestrictionsMatchQpdf(string bits, string print, string modify, string extract, string accessibility)
    {
        string? qpdf = FindQpdf();
        if (qpdf is null)
        {
            Assert.Skip("qpdf not found");
        }

        string input = Path.Combine(EncryptedPdf.RepoRoot(), "testdata", "generated", "pdf", "raw-base.pdf");
        foreach (string aes in bits == "128" ? new[] { "--use-aes=y", "--use-aes=n" } : new[] { "--use-aes=y" })
        {
            string output = Path.Combine(Path.GetTempPath(), "onepdf-perm-" + Guid.NewGuid().ToString("N") + ".pdf");
            try
            {
                var args = new List<string> { "--allow-weak-crypto", "--encrypt", "u", "o", bits };
                if (bits == "128")
                {
                    args.Add(aes);
                }

                args.AddRange([print, modify, extract, accessibility, "--", input, output]);
                Run(qpdf, args.ToArray());
                Compare(qpdf, output, "u");
            }
            finally
            {
                File.Delete(output);
            }
        }
    }
}

/// <summary>Revision 6 hashing ends after a data-dependent number of rounds; random salts exercise different counts.</summary>
public class QpdfRevision6Tests
{
    [Fact]
    public void RandomSaltsAuthenticate()
    {
        string qpdf = Environment.GetEnvironmentVariable("ONEPDF_QPDF") ?? string.Empty;
        if (!File.Exists(qpdf))
        {
            Assert.Skip("set ONEPDF_QPDF to a qpdf binary");
        }

        string input = Path.Combine(EncryptedPdf.RepoRoot(), "testdata", "generated", "pdf", "raw-base.pdf");
        string output = Path.Combine(Path.GetTempPath(), "onepdf-r6-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            for (int i = 0; i < 60; i++)
            {
                var psi = new ProcessStartInfo(qpdf);
                foreach (string a in new[] { "--encrypt", "u" + i, "o" + i, "256", "--", input, output })
                {
                    psi.ArgumentList.Add(a);
                }

                using (Process p = Process.Start(psi)!)
                {
                    p.WaitForExit();
                }

                EncryptedPdf pdf = EncryptedPdf.Parse(File.ReadAllBytes(output));
                SecurityResult u = StandardSecurityHandler.Authenticate(pdf.Parameters, pdf.Id, "u" + i);
                Assert.Equal(SecurityOutcome.User, u.Outcome);
                u.Decryptor!.Dispose();
                SecurityResult o = StandardSecurityHandler.Authenticate(pdf.Parameters, pdf.Id, "o" + i);
                Assert.Equal(SecurityOutcome.Owner, o.Outcome);
                o.Decryptor!.Dispose();
            }
        }
        finally
        {
            File.Delete(output);
        }
    }
}
