using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using OnePdf.Asn1;

namespace OnePdf.Tests.Signatures;

/// <summary>Cross-checks against poppler's pdfsig and OpenSSL, used as compiled programs only. Skipped when they are not installed.</summary>
public class BlackBoxTests
{
    private static string? Find(string name)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string p = Path.Combine(dir, name);
            if (File.Exists(p))
            {
                return p;
            }
        }

        return null;
    }

    private static (int Code, string Output) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o);
    }

    private static string Temp(string name)
    {
        string dir = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "onepdf-sig");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    public static IEnumerable<object[]> Cases()
    {
        yield return [SigScheme.RsaPkcs1, "SHA256"];
        yield return [SigScheme.RsaPss, "SHA256"];
        yield return [SigScheme.Ecdsa, "SHA384"];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void OpenSslVerifiesTheCmsWeBuildAndOnePdfAgreesWithPdfsig(SigScheme scheme, string hash)
    {
        string? openssl = Find("openssl");
        string? pdfsig = Find("pdfsig");
        if (openssl is null && pdfsig is null)
        {
            return;
        }

        TestPki pki = TestPki.Shared;
        var spec = new CmsSpec { Scheme = scheme, Hash = new HashAlgorithmName(hash), Cades = true };
        byte[] file = SignedPdf.SignWith(SignedPdf.BaseFile(new SignedPdf.Options { SubFilter = "ETSI.CAdES.detached" }, out _), scheme == SigScheme.Ecdsa ? pki.EcSigner : pki.RsaSigner, spec);
        string tag = $"{scheme}-{hash}";
        PdfSignatureVerification ours = SignedPdf.VerifyOne(file);
        Assert.True(ours.IsValid, string.Join("; ", ours.Warnings));

        using var doc = PdfDocument.Open(file);
        PdfSignature sig = doc.GetSignatures()[0];
        IReadOnlyList<long> r = sig.ByteRange!;
        byte[] signedBytes = [.. file.AsSpan(0, (int)r[1]), .. file.AsSpan((int)r[2], (int)r[3])];
        Assert.True(Asn1Reader.TryRead(sig.Contents, out Asn1Element ci, out int used, out _));
        byte[] cms = sig.Contents.Span[..used].ToArray();
        Assert.True(ci.IsSequence);

        if (openssl is not null)
        {
            string cmsPath = Temp(tag + ".cms");
            string dataPath = Temp(tag + ".bin");
            string rootPath = Temp("root.pem");
            File.WriteAllBytes(cmsPath, cms);
            File.WriteAllBytes(dataPath, signedBytes);
            File.WriteAllText(rootPath, pki.Root.ExportCertificatePem());
            (int code, string output) = Run(openssl, "cms", "-verify", "-inform", "DER", "-in", cmsPath, "-content", dataPath, "-binary", "-CAfile", rootPath, "-purpose", "any", "-out", Temp(tag + ".out"));
            Assert.True(code == 0, output);

            signedBytes[3] ^= 1;
            File.WriteAllBytes(dataPath, signedBytes);
            (int badCode, _) = Run(openssl, "cms", "-verify", "-inform", "DER", "-in", cmsPath, "-content", dataPath, "-binary", "-CAfile", rootPath, "-purpose", "any", "-out", Temp(tag + ".out"));
            Assert.NotEqual(0, badCode);
        }

        if (pdfsig is not null)
        {
            string pdfPath = Temp(tag + ".pdf");
            File.WriteAllBytes(pdfPath, file);
            (_, string output) = Run(pdfsig, pdfPath);
            Assert.Contains("Signature is Valid", output, StringComparison.Ordinal);

            byte[] bad = (byte[])file.Clone();
            bad[Encoding.Latin1.GetString(file).IndexOf("Hello", StringComparison.Ordinal)] ^= 1;
            File.WriteAllBytes(pdfPath, bad);
            (_, string badOutput) = Run(pdfsig, pdfPath);
            Assert.DoesNotContain("Signature is Valid", badOutput, StringComparison.Ordinal);
        }
    }
}
