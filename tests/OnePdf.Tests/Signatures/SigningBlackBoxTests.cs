using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using OnePdf.Asn1;
using static OnePdf.Tests.Signatures.SigningFixture;
using WriterSupport = OnePdf.Tests.Writing.WriterSupport;

namespace OnePdf.Tests.Signatures;

/// <summary>Signing against other programs used as compiled binaries (pdfsig, OpenSSL, qpdf, pdftoppm), plus encrypted and stream-based inputs.</summary>
public class SigningBlackBoxTests
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
        Task<string> err = p.StandardError.ReadToEndAsync();
        string o = p.StandardOutput.ReadToEnd() + err.GetAwaiter().GetResult();
        p.WaitForExit();
        return (p.ExitCode, o);
    }

    private static string Temp(string name)
    {
        string dir = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "onepdf-signing");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    public static IEnumerable<object[]> Cases()
    {
        yield return ["rsa-pkcs1", true, false];
        yield return ["rsa-pss", true, true];
        yield return ["ec256", true, true];
        yield return ["ec384", false, false];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void PdfsigOpenSslAndQpdfAgreeWithUs(string key, bool pades, bool stamped)
    {
        (var cert, PdfRsaPadding padding) = Key(key);
        byte[] input = UnsignedPdf();
        byte[] signed = SignToBytes(Editable(input), Credential(cert), new PdfSigningOptions
        {
            RsaPadding = padding,
            Format = pades ? PdfSignatureFormat.PadesBaseline : PdfSignatureFormat.Pkcs7Detached,
            Timestamp = stamped ? new TestTsa().Callback : null,
        });
        Assert.True(signed.AsSpan(0, input.Length).SequenceEqual(input));
        string tag = $"{key}-{pades}-{stamped}";

        (int qcode, string qout) = WriterSupport.QpdfRun(signed);
        Assert.True(qcode == 0, qout);

        using PdfDocument doc = PdfDocument.Open(signed);
        PdfSignature sig = doc.GetSignatures()[0];
        IReadOnlyList<long> r = sig.ByteRange!;
        byte[] signedBytes = [.. signed.AsSpan(0, (int)r[1]), .. signed.AsSpan((int)r[2], (int)r[3])];
        Assert.True(Asn1Reader.TryRead(sig.Contents, out _, out int used, out _));
        byte[] cms = sig.Contents.Span[..used].ToArray();

        if (Find("openssl") is { } openssl)
        {
            File.WriteAllBytes(Temp(tag + ".cms"), cms);
            File.WriteAllBytes(Temp(tag + ".bin"), signedBytes);
            File.WriteAllText(Temp("root.pem"), TestPki.Shared.Root.ExportCertificatePem());
            (int code, string output) = Run(openssl, "cms", "-verify", "-inform", "DER", "-in", Temp(tag + ".cms"), "-content", Temp(tag + ".bin"), "-binary", "-CAfile", Temp("root.pem"), "-purpose", "any", "-out", Temp(tag + ".out"));
            Assert.True(code == 0, output);
            if (stamped)
            {
                // The token inside the signature is itself a CMS that OpenSSL verifies against the signature value.
                Assert.True(CmsSignedData.TryParse(sig.Contents, out CmsSignedData? sd, out _));
                ReadOnlyMemory<byte> token = sd!.Signers[0].FindUnsigned(Oids.SignatureTimeStampToken)!.Values[0];
                File.WriteAllBytes(Temp(tag + ".tst"), token.ToArray());
                (int tcode, string tout) = Run(openssl, "cms", "-verify", "-inform", "DER", "-in", Temp(tag + ".tst"), "-CAfile", Temp("root.pem"), "-purpose", "any", "-out", Temp(tag + ".tstinfo"));
                Assert.True(tcode == 0, tout);
            }
        }

        if (Find("pdfsig") is { } pdfsig)
        {
            File.WriteAllBytes(Temp(tag + ".pdf"), signed);
            (_, string output) = Run(pdfsig, Temp(tag + ".pdf"));
            Assert.Contains("Signature is Valid", output, StringComparison.Ordinal);
            Assert.Contains("Signature Type: " + (pades ? "ETSI.CAdES.detached" : "adbe.pkcs7.detached"), output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PdfsigAcceptsASecondSignatureAndATimestampAfterAFormFill()
    {
        byte[] one = SignToBytes(Editable(UnsignedPdf()), Credential(TestPki.Shared.RsaSigner), new PdfSigningOptions { FieldName = "Sig2" });
        byte[] two;
        using (PdfDocument doc = Editable(one))
        {
            doc.Form!.SetValue("Name", "changed");
            two = SignToBytes(doc, Credential(TestPki.Shared.EcSigner));
        }

        using PdfDocument stamp = Editable(two);
        using var ms = new MemoryStream();
        stamp.AddDocumentTimestamp(ms, new TestTsa().Callback);
        byte[] three = ms.ToArray();
        Assert.True(WriterSupport.QpdfRun(three).Code == 0);
        if (Find("pdfsig") is { } pdfsig)
        {
            File.WriteAllBytes(Temp("three.pdf"), three);
            (_, string output) = Run(pdfsig, Temp("three.pdf"));
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(output, "Signature is Valid"));
            Assert.Contains("Timestamp1", output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void VisibleSignatureRendersWithPdftoppm()
    {
        string? pdftoppm = Find("pdftoppm");
        if (pdftoppm is null)
        {
            return;
        }

        byte[] input = UnsignedPdf();
        byte[] signed = SignToBytes(Editable(input), Credential(TestPki.Shared.RsaSigner), new PdfSigningOptions { FieldName = "Sig2", SignerName = "Rita RSA" });
        int before = InkInBox(pdftoppm, input, "before");
        int after = InkInBox(pdftoppm, signed, "after");
        Assert.True(after > before + 200, $"ink before {before}, after {after}");
    }

    // Renders page 1 at 72 dpi and counts the dark pixels inside the signature rectangle [110 10 260 70] (y runs down in the image).
    private static int InkInBox(string pdftoppm, byte[] file, string name)
    {
        File.WriteAllBytes(Temp(name + ".pdf"), file);
        (int code, string output) = Run(pdftoppm, "-r", "72", "-f", "1", "-l", "1", "-gray", Temp(name + ".pdf"), Temp(name));
        Assert.True(code == 0, output);
        string produced = Directory.EnumerateFiles(Path.GetDirectoryName(Temp(name))!, name + "-*.pgm").First();
        byte[] pgm = File.ReadAllBytes(produced);
        // "P5\nW H\n255\n" then one byte per pixel.
        int pos = 0;
        string[] tokens = new string[4];
        for (int t = 0; t < 4; t++)
        {
            while (char.IsWhiteSpace((char)pgm[pos]))
            {
                pos++;
            }

            int start = pos;
            while (!char.IsWhiteSpace((char)pgm[pos]))
            {
                pos++;
            }

            tokens[t] = Encoding.ASCII.GetString(pgm, start, pos - start);
        }

        pos++;
        int width = int.Parse(tokens[1], System.Globalization.CultureInfo.InvariantCulture);
        int height = int.Parse(tokens[2], System.Globalization.CultureInfo.InvariantCulture);
        int ink = 0;
        for (int y = height - 70; y < height - 10; y++)
        {
            for (int x = 110; x < 260 && x < width; x++)
            {
                if (pgm[pos + (y * width) + x] < 160)
                {
                    ink++;
                }
            }
        }

        return ink;
    }

    [Fact]
    public void SignsEncryptedDocumentsAndKeepsTheContentsUnencrypted()
    {
        foreach (PdfCipher cipher in new[] { PdfCipher.Aes256, PdfCipher.Aes128 })
        {
            byte[] plain = UnsignedPdf();
            byte[] encrypted;
            using (PdfDocument source = Editable(plain))
            using (var ms = new MemoryStream())
            {
                source.Save(ms, new PdfSaveOptions
                {
                    Encryption = PdfEncryptionAction.Set,
                    NewEncryption = new PdfEncryptionOptions { UserPassword = "user", OwnerPassword = "owner", Cipher = cipher },
                });
                encrypted = ms.ToArray();
            }

            byte[] signed = SignToBytes(Editable(encrypted, "user"), Credential(TestPki.Shared.RsaSigner), new PdfSigningOptions { Reason = "secret reason", Timestamp = new TestTsa().Callback });
            Assert.True(signed.AsSpan(0, encrypted.Length).SequenceEqual(encrypted));
            Assert.DoesNotContain("secret reason", Encoding.Latin1.GetString(signed), StringComparison.Ordinal);
            using PdfDocument check = PdfDocument.Open(signed, new PdfOpenOptions { Password = "user" });
            PdfSignatureReport report = check.VerifySignatures(Trust);
            Assert.True(report.AllValid, WarningsOf(report));
            Assert.Equal("secret reason", report.Signatures[0].Signature.Reason);
            (int code, string output) = WriterSupport.QpdfRun(signed, "user");
            Assert.True(code == 0, output);
        }
    }

    [Fact]
    public void SignsFilesWithCrossReferenceAndObjectStreams()
    {
        byte[] stream;
        using (PdfDocument source = Editable(UnsignedPdf()))
        using (var ms = new MemoryStream())
        {
            source.Save(ms, new PdfSaveOptions { XrefFormat = PdfXrefFormat.Stream, UseObjectStreams = true });
            stream = ms.ToArray();
        }

        byte[] one = SignToBytes(Editable(stream), Credential(TestPki.Shared.RsaSigner));
        byte[] two = SignToBytes(Editable(one), Credential(TestPki.Shared.EcSigner));
        using PdfDocument check = PdfDocument.Open(two);
        PdfSignatureReport report = check.VerifySignatures(Trust);
        Assert.True(report.AllValid, WarningsOf(report));
        Assert.True(WriterSupport.QpdfRun(two).Code == 0);
    }
}
