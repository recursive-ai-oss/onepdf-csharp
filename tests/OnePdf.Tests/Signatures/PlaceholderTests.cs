using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using static OnePdf.Tests.Signatures.SigningFixture;

namespace OnePdf.Tests.Signatures;

public class PlaceholderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onepdf-ph-" + Guid.NewGuid().ToString("N"));

    public PlaceholderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Directory.Delete(_dir, true);
        GC.SuppressFinalize(this);
    }

    private static byte[] Cms(PdfSignaturePlaceholder p, bool cades)
    {
        X509Certificate2 signer = TestPki.Shared.RsaSigner;
        return CmsKit.Build(p.Digest, signer, [signer, TestPki.Shared.Intermediate], new CmsSpec { Hash = p.HashAlgorithm, Cades = cades });
    }

    [Theory]
    [InlineData("ETSI.CAdES.detached", true)]
    [InlineData("adbe.pkcs7.detached", false)]
    public void FilledHoleVerifiesAndOffsetsAreExact(string subFilter, bool cades)
    {
        byte[] input = UnsignedPdf();
        using PdfDocument doc = Editable(input);
        using var ms = new MemoryStream();
        PdfSignaturePlaceholder p = doc.AddSignaturePlaceholder(ms, new PdfSigningOptions { SubFilter = subFilter, SignerName = "Ext Signer", Reason = "two-phase" }, 8192);

        byte[] file = ms.ToArray();
        Assert.True(file.AsSpan(0, input.Length).SequenceEqual(input));
        Assert.Equal(file.Length, p.SignedLength);
        Assert.Equal((byte)'<', file[p.HoleStart]);
        Assert.Equal((byte)'>', file[p.HoleEnd - 1]);
        Assert.Equal((2L * 8192) + 2, p.HoleEnd - p.HoleStart);
        Assert.All(file.AsSpan((int)p.HoleStart + 1, 2 * 8192).ToArray(), b => Assert.Equal((byte)'0', b));
        string text = Encoding.Latin1.GetString(file);
        Assert.Contains($"/ByteRange[ 0 {p.HoleStart,-10} {p.HoleEnd,-10} {file.Length - p.HoleEnd,-10}]", text);
        Assert.Equal(p.Digest, PdfSignatureHole.ComputeDigest(ms, p.HoleStart, p.HoleEnd, p.HashAlgorithm));

        p.Fill(ms, Cms(p, cades));
        Assert.Equal(file.Length, ms.Length);

        using PdfDocument check = PdfDocument.Open(ms.ToArray());
        PdfSignatureReport report = check.VerifySignatures(Trust);
        Assert.True(report.AllValid, WarningsOf(report));
        PdfSignatureVerification v = Assert.Single(report.Signatures);
        Assert.Equal(subFilter, v.Signature.SubFilter);
        Assert.Equal(p.FieldName, v.Signature.FieldName);
        Assert.Equal("two-phase", v.Signature.Reason);
        Assert.Equal(PdfSignatureCoverage.WholeDocument, v.Coverage);
    }

    [Fact]
    public void ReusesFieldAndCertifiesWithLock()
    {
        using PdfDocument doc = Editable(UnsignedPdf());
        using var ms = new MemoryStream();
        PdfSignaturePlaceholder p = doc.AddSignaturePlaceholder(ms, new PdfSigningOptions
        {
            FieldName = "Sig2",
            Certify = PdfCertificationLevel.FormFilling,
            FieldLock = new PdfSignatureLock(PdfLockAction.Exclude, ["Other"], null),
            SubFilter = "ETSI.CAdES.detached",
        });
        Assert.Equal("Sig2", p.FieldName);
        p.Fill(ms, Cms(p, true));
        using PdfDocument check = PdfDocument.Open(ms.ToArray());
        PdfSignatureReport report = check.VerifySignatures(Trust);
        Assert.True(report.AllValid, WarningsOf(report));
        PdfSignature sig = Assert.Single(check.GetSignatures());
        Assert.Equal(2, sig.CertificationLevel);
        Assert.NotNull(sig.FieldLock);
    }

    [Fact]
    public void TooLargeCmsFailsAndLeavesTheStreamAlone()
    {
        using PdfDocument doc = Editable(UnsignedPdf());
        using var ms = new MemoryStream();
        PdfSignaturePlaceholder p = doc.AddSignaturePlaceholder(ms, null, 64);
        byte[] before = ms.ToArray();
        ms.Position = 5;
        byte[] cms = Cms(p, true);
        Assert.True(cms.Length > 64);
        PdfSigningException e = Assert.Throws<PdfSigningException>(() => p.Fill(ms, cms));
        Assert.Equal(PdfSigningError.ContentsTooSmall, e.Reason);
        Assert.Equal(cms.Length, e.RequiredBytes);
        Assert.Equal(5, ms.Position);
        Assert.True(before.AsSpan().SequenceEqual(ms.ToArray()));

        // Still usable: a CMS that fits is accepted afterwards, but a second fill is refused.
        PdfSignatureHole.Fill(ms, p.HoleStart, p.HoleEnd, new byte[] { 1, 2, 3 });
        Assert.Equal(PdfSigningError.BadHole, Assert.Throws<PdfSigningException>(() => PdfSignatureHole.Fill(ms, p.HoleStart, p.HoleEnd, new byte[] { 4 })).Reason);
    }

    [Fact]
    public void WrongOffsetsAreRefused()
    {
        using PdfDocument doc = Editable(UnsignedPdf());
        using var ms = new MemoryStream();
        PdfSignaturePlaceholder p = doc.AddSignaturePlaceholder(ms, null, 256);
        byte[] before = ms.ToArray();
        Assert.Throws<ArgumentException>(() => PdfSignatureHole.Fill(ms, p.HoleStart, p.HoleEnd + 1000, [1]));
        Assert.Throws<ArgumentException>(() => PdfSignatureHole.Fill(ms, -1, p.HoleEnd, [1]));
        Assert.Throws<ArgumentException>(() => PdfSignatureHole.Fill(ms, p.HoleStart, p.HoleEnd, []));
        Assert.Throws<ArgumentException>(() => PdfSignatureHole.Fill(ms, p.HoleStart, p.HoleEnd - 1, [1]));
        Assert.Equal(PdfSigningError.BadHole, Assert.Throws<PdfSigningException>(() => PdfSignatureHole.Fill(ms, p.HoleStart + 2, p.HoleEnd - 2, [1])).Reason);
        Assert.Equal(PdfSigningError.BadHole, Assert.Throws<PdfSigningException>(() => PdfSignatureHole.Fill(ms, 0, 10, [1])).Reason);
        Assert.True(before.AsSpan().SequenceEqual(ms.ToArray()));
        Assert.Throws<ArgumentException>(() => PdfSignatureHole.Fill(new MemoryStream(before, false), p.HoleStart, p.HoleEnd, [1]));
    }

    [Fact]
    public void TwoPhaseAcrossDocumentLifetimes()
    {
        string path = Path.Combine(_dir, "out.pdf");
        long start;
        long end;
        using (PdfDocument doc = Editable(UnsignedPdf()))
        {
            PdfSignaturePlaceholder p = doc.AddSignaturePlaceholder(path, new PdfSigningOptions { SubFilter = "ETSI.CAdES.detached", Rectangle = new PdfRectangle(20, 20, 200, 80) }, 10000);
            start = p.HoleStart;
            end = p.HoleEnd;
        }

        // Phase two: only the file and the two offsets.
        byte[] digest;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
        {
            digest = PdfSignatureHole.ComputeDigest(fs, start, end, HashAlgorithmName.SHA256);
        }

        X509Certificate2 signer = TestPki.Shared.RsaSigner;
        byte[] cms = CmsKit.Build(digest, signer, [signer, TestPki.Shared.Intermediate], new CmsSpec { Cades = true });
        PdfSignatureHole.Fill(path, start, end, cms);

        using PdfDocument check = PdfDocument.Open(path);
        PdfSignatureReport report = check.VerifySignatures(Trust);
        Assert.True(report.AllValid, WarningsOf(report));
    }

    [Fact]
    public void OneSigningAttemptPerDocumentInstance()
    {
        using PdfDocument doc = Editable(UnsignedPdf());
        using var ms = new MemoryStream();
        doc.AddSignaturePlaceholder(ms);
        Assert.Equal(PdfSigningError.DocumentUsed, Assert.Throws<PdfSigningException>(() => doc.AddSignaturePlaceholder(new MemoryStream())).Reason);
        Assert.Equal(PdfSigningError.DocumentUsed, Assert.Throws<PdfSigningException>(() => doc.Sign(new MemoryStream(), Credential(TestPki.Shared.RsaSigner))).Reason);
        using PdfDocument ro = PdfDocument.Open(UnsignedPdf());
        Assert.Throws<InvalidOperationException>(() => ro.AddSignaturePlaceholder(new MemoryStream()));
    }

    [Fact]
    public void BadOptionsAreRefusedBeforeAnythingIsWritten()
    {
        using PdfDocument doc = Editable(UnsignedPdf());
        var ms = new MemoryStream();
        Assert.Throws<ArgumentException>(() => doc.AddSignaturePlaceholder(ms, new PdfSigningOptions { SubFilter = "a b/c" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.AddSignaturePlaceholder(ms, null, 10));
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void SignedDataMatchesTheVerifiersDigest()
    {
        byte[] input = UnsignedPdf();
        using PdfDocument doc = Editable(input);
        using var ms = new MemoryStream();
        PdfSignaturePlaceholder p = doc.AddSignaturePlaceholder(ms, null, 4096);
        p.Fill(ms, Cms(p, true));
        string path = Path.Combine(_dir, "signed.pdf");
        File.WriteAllBytes(path, ms.ToArray());

        foreach (bool inMemory in new[] { true, false })
        {
            using PdfDocument check = inMemory ? PdfDocument.Open(ms.ToArray()) : PdfDocument.Open(path);
            PdfSignature sig = Assert.Single(check.GetSignatures());
            using Stream data = sig.OpenSignedData()!;
            Assert.NotNull(data);
            Assert.Equal(p.HoleStart + (p.SignedLength - p.HoleEnd), data.Length);
            Assert.True(data.CanSeek && data.CanRead && !data.CanWrite);
            Assert.Equal(p.Digest, SHA256.HashData(data));

            // Odd read sizes across the seam, and seeking.
            data.Position = p.HoleStart - 3;
            byte[] seam = new byte[6];
            Assert.Equal(6, data.Read(seam, 0, 6));
            Assert.True(seam.AsSpan(0, 3).SequenceEqual(ms.ToArray().AsSpan((int)p.HoleStart - 3, 3)));
            Assert.True(seam.AsSpan(3).SequenceEqual(ms.ToArray().AsSpan((int)p.HoleEnd, 3)));
            Assert.Equal(0, data.Seek(0, SeekOrigin.End) == data.Length ? data.Read(seam, 0, 6) : -1);
            Assert.Throws<NotSupportedException>(() => data.Write(seam, 0, 1));
        }
    }

    [Fact]
    public void SignedDataIsNullForUnusableRanges()
    {
        using PdfDocument doc = Editable(UnsignedPdf());
        using var ms = new MemoryStream();
        PdfSignaturePlaceholder p = doc.AddSignaturePlaceholder(ms, null, 4096);
        p.Fill(ms, Cms(p, true));
        byte[] file = ms.ToArray();
        string text = Encoding.Latin1.GetString(file);
        int at = text.IndexOf("/ByteRange[ 0 ", StringComparison.Ordinal) + "/ByteRange[ 0 ".Length;
        // Make the last range run past the end of the file, keeping the length of the text.
        string bad = text[..at] + text[at..].Replace($"{p.SignedLength - p.HoleEnd,-10}]", "9999999999]", StringComparison.Ordinal);
        using PdfDocument check = PdfDocument.Open(Encoding.Latin1.GetBytes(bad));
        PdfSignature sig = Assert.Single(check.GetSignatures());
        Assert.Null(sig.OpenSignedData());
        Assert.False(check.VerifySignatures(Trust).AllValid);
    }
}
