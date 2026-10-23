using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OnePdf.Tests.Signatures;

public class SignatureVerifyTests
{
    private static readonly TestPki Pki = TestPki.Shared;

    private static byte[] Signed(SigScheme scheme, HashAlgorithmName hash, bool cades, bool timestamp, X509Certificate2? signer = null, CmsSpec? spec = null)
    {
        byte[] file = SignedPdf.BaseFile(new SignedPdf.Options { SubFilter = cades ? "ETSI.CAdES.detached" : "adbe.pkcs7.detached" }, out _);
        signer ??= scheme == SigScheme.Ecdsa ? Pki.EcSigner : Pki.RsaSigner;
        spec ??= new CmsSpec { Hash = hash, Scheme = scheme, Cades = cades, Timestamp = timestamp };
        return SignedPdf.SignWith(file, signer, spec);
    }

    public static IEnumerable<object[]> Matrix()
    {
        foreach (SigScheme scheme in new[] { SigScheme.RsaPkcs1, SigScheme.RsaPss, SigScheme.Ecdsa })
        {
            foreach (string hash in new[] { "SHA256", "SHA384" })
            {
                foreach (bool cades in new[] { false, true })
                {
                    foreach (bool ts in new[] { false, true })
                    {
                        yield return [scheme, hash, cades, ts];
                    }
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void GeneratedSignaturesVerifyAndChainToTheTestRoot(SigScheme scheme, string hash, bool cades, bool timestamp)
    {
        byte[] file = Signed(scheme, new HashAlgorithmName(hash), cades, timestamp);
        PdfSignatureVerification v = SignedPdf.VerifyOne(file);
        Assert.Equal(PdfByteRangeStatus.Valid, v.ByteRange);
        Assert.Equal(PdfCheckStatus.Valid, v.Integrity);
        Assert.Equal(PdfCheckStatus.Valid, v.SignatureCheck);
        Assert.Equal(PdfChainStatus.Trusted, v.Chain);
        Assert.Equal(3, v.ChainElements.Count);
        Assert.Equal(PdfSignatureCoverage.WholeDocument, v.Coverage);
        Assert.Equal(0, v.CoveredRevision);
        Assert.True(v.IsValid, string.Join("; ", v.Warnings));
        Assert.Equal(PdfRevocationStatus.NotChecked, v.Revocation);
        Assert.Contains(v.Warnings, static w => w.StartsWith("Revocation was not checked", StringComparison.Ordinal));
        Assert.False(v.UsesWeakAlgorithm);
        if (timestamp)
        {
            Assert.NotNull(v.Timestamp);
            Assert.True(v.Timestamp.IsValid, string.Join("; ", v.Timestamp.Warnings));
            Assert.Equal(PdfChainStatus.Trusted, v.Timestamp.Chain);
            Assert.Equal(PdfValidationTimeSource.Timestamp, v.ValidationTimeSource);
        }
        else
        {
            Assert.Null(v.Timestamp);
            Assert.Equal(PdfValidationTimeSource.SigningTime, v.ValidationTimeSource);
        }
    }

    [Fact]
    public void MetadataIsExposedAsData()
    {
        byte[] file = Signed(SigScheme.RsaPkcs1, HashAlgorithmName.SHA256, false, false);
        using var doc = PdfDocument.Open(file);
        PdfSignature s = Assert.Single(doc.GetSignatures());
        Assert.Equal("Sig1", s.FieldName);
        Assert.Equal("adbe.pkcs7.detached", s.SubFilter);
        Assert.Equal("Adobe.PPKLite", s.Filter);
        Assert.Equal("Rita RSA", s.Name);
        Assert.Equal("Testing", s.Reason);
        Assert.Equal("Lab", s.Location);
        Assert.Equal("rita@example.test", s.ContactInfo);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), s.SigningTime);
        Assert.Equal(4, s.ByteRange!.Count);
        Assert.Equal(PdfSignatureKind.Signature, s.Kind);
        Assert.Equal(0, s.CoveredRevision);
        Assert.False(s.HasLaterRevisions);
        Assert.True(s.Contents.Length == SignedPdf.ContentsBytes);
        Assert.Equal(0x30, s.Contents.Span[0]);
        Assert.Same(doc, s.Document);
        Assert.NotNull(doc.Form!.FindField("Sig1")!.GetSignature());
        Assert.Null(doc.Form.FindField("Name")!.GetSignature());
        PdfSignatureReport report = doc.VerifySignatures(Pki.Trusting());
        Assert.True(report.AllValid);
        Assert.Equal("1 signature: 1 valid, 0 not valid.", report.Summary);
    }

    [Fact]
    public void NoAnchorsMeansUntrusted()
    {
        PdfSignatureVerification v = SignedPdf.VerifyOne(Signed(SigScheme.RsaPkcs1, HashAlgorithmName.SHA256, false, false), new PdfSignatureVerifyOptions());
        Assert.Contains(v.Chain, new[] { PdfChainStatus.UntrustedRoot, PdfChainStatus.Incomplete });
        Assert.Equal(PdfCheckStatus.Valid, v.SignatureCheck);
        Assert.False(v.IsValid);
    }

    [Fact]
    public void FlippedByteInsideTheRangeBreaksIntegrityButNotTheSignatureValue()
    {
        byte[] file = Signed(SigScheme.RsaPkcs1, HashAlgorithmName.SHA256, false, false);
        int at = System.Text.Encoding.Latin1.GetString(file).IndexOf("Hello", StringComparison.Ordinal);
        file[at] ^= 1;
        PdfSignatureVerification v = SignedPdf.VerifyOne(file);
        Assert.Equal(PdfByteRangeStatus.Valid, v.ByteRange);
        Assert.Equal(PdfCheckStatus.Invalid, v.Integrity);
        Assert.Equal(PdfCheckStatus.Valid, v.SignatureCheck);
        Assert.False(v.IsValid);
        Assert.Contains(v.Warnings, static w => w.Contains("altered", StringComparison.Ordinal));
    }

    [Fact]
    public void ChangedByteRangeIsReported()
    {
        byte[] file = SignedPdf.SignWith(SignedPdf.BaseFile(new SignedPdf.Options(), out _), Pki.RsaSigner, new CmsSpec(), tamperRange: true);
        PdfSignatureVerification v = SignedPdf.VerifyOne(file);
        Assert.NotEqual(PdfByteRangeStatus.Valid, v.ByteRange);
        Assert.False(v.IsValid);
        Assert.NotEqual(PdfCheckStatus.Valid, v.Integrity);
    }

    [Fact]
    public void RangeThatStopsShortOfTheRevisionEndIsNotCovering()
    {
        byte[] file = SignedPdf.SignWith(SignedPdf.BaseFile(new SignedPdf.Options(), out _), Pki.RsaSigner, new CmsSpec());
        string text = System.Text.Encoding.Latin1.GetString(file);
        int at = text.IndexOf("[0 ", text.IndexOf("/ByteRange", StringComparison.Ordinal), StringComparison.Ordinal);
        int close = text.IndexOf(']', at);
        string[] n = text[(at + 1)..close].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string shorter = $"[0 {n[1]} {n[2]} {long.Parse(n[3]) - 10}]".PadRight(close - at + 1);
        System.Text.Encoding.Latin1.GetBytes(shorter).CopyTo(file, at);
        PdfSignatureVerification v = SignedPdf.VerifyOne(file);
        Assert.Equal(PdfByteRangeStatus.DoesNotCoverRevision, v.ByteRange);
        Assert.Equal(PdfCheckStatus.Invalid, v.Integrity);
        Assert.Equal(PdfSignatureCoverage.Unknown, v.Coverage);
    }

    [Fact]
    public void WrongCertificateFailsTheSignatureCheck()
    {
        // The signature names the stranger's certificate as the signer although another key made it.
        var spec = new CmsSpec { ClaimedSigner = Pki.Stranger, Embedded = [Pki.Stranger, Pki.RsaSigner, Pki.Intermediate], Cades = true };
        PdfSignatureVerification v = SignedPdf.VerifyOne(Signed(SigScheme.RsaPkcs1, HashAlgorithmName.SHA256, true, false, spec: spec));
        Assert.Equal(PdfCheckStatus.Invalid, v.SignatureCheck);
        Assert.Equal(PdfCheckStatus.Valid, v.Integrity);
        Assert.False(v.IsValid);
    }

    [Fact]
    public void SigningCertificateAttributeMustNameTheSigner()
    {
        // The key is Rita's but the CAdES attribute (and the identifier) name the stranger: nothing verifies.
        var spec = new CmsSpec { ClaimedSigner = Pki.Stranger, Embedded = [Pki.Stranger, Pki.Intermediate], Cades = true };
        PdfSignatureVerification v = SignedPdf.VerifyOne(Signed(SigScheme.RsaPkcs1, HashAlgorithmName.SHA256, true, false, spec: spec));
        Assert.NotEqual(PdfCheckStatus.Valid, v.SignatureCheck);
    }

    [Fact]
    public void UntrustedRootIsReportedWithValidSignature()
    {
        var spec = new CmsSpec { Embedded = [Pki.Stranger, Pki.OtherRoot] };
        PdfSignatureVerification v = SignedPdf.VerifyOne(Signed(SigScheme.RsaPkcs1, HashAlgorithmName.SHA256, false, false, Pki.Stranger, spec));
        Assert.Equal(PdfCheckStatus.Valid, v.SignatureCheck);
        Assert.Equal(PdfChainStatus.UntrustedRoot, v.Chain);
        Assert.False(v.IsValid);
    }

    [Fact]
    public void MissingIntermediateIsIncompleteUnlessSuppliedAsExtra()
    {
        var spec = new CmsSpec { Embedded = [Pki.RsaSigner] };
        byte[] file = Signed(SigScheme.RsaPkcs1, HashAlgorithmName.SHA256, false, false, spec: spec);
        PdfSignatureVerification v = SignedPdf.VerifyOne(file);
        Assert.Equal(PdfChainStatus.Incomplete, v.Chain);
        PdfSignatureVerification fixedUp = SignedPdf.VerifyOne(file, new PdfSignatureVerifyOptions { TrustAnchors = [Pki.RootAnchor], ExtraCertificates = [X509CertificateLoader.LoadCertificate(Pki.Intermediate.RawData)] });
        Assert.Equal(PdfChainStatus.Trusted, fixedUp.Chain);
    }

    [Fact]
    public void ExpiredCertificateAtTheValidationTime()
    {
        byte[] file = Signed(SigScheme.RsaPkcs1, HashAlgorithmName.SHA256, false, false, Pki.ExpiredSigner);
        PdfSignatureVerification now = SignedPdf.VerifyOne(file, new PdfSignatureVerifyOptions { TrustAnchors = [Pki.RootAnchor], TimePolicy = PdfValidationTimePolicy.Now });
        Assert.Equal(PdfChainStatus.Expired, now.Chain);
        Assert.Equal(PdfValidationTimeSource.Now, now.ValidationTimeSource);
        Assert.Equal(PdfCheckStatus.Valid, now.SignatureCheck);

        PdfSignatureVerification then = SignedPdf.VerifyOne(file, new PdfSignatureVerifyOptions { TrustAnchors = [Pki.RootAnchor], ValidationTime = DateTimeOffset.UtcNow.AddYears(-3) });
        Assert.Equal(PdfChainStatus.Trusted, then.Chain);
        Assert.Equal(PdfValidationTimeSource.Explicit, then.ValidationTimeSource);
    }

    [Fact]
    public void VerifiedTimestampTimeRescuesAnExpiredCertificate()
    {
        var spec = new CmsSpec { Timestamp = true, TimestampTime = DateTimeOffset.UtcNow.AddYears(-3), SigningTime = DateTimeOffset.UtcNow.AddYears(-3) };
        byte[] file = Signed(SigScheme.RsaPkcs1, HashAlgorithmName.SHA256, true, true, Pki.ExpiredSigner, spec);
        PdfSignatureVerification v = SignedPdf.VerifyOne(file);
        Assert.Equal(PdfValidationTimeSource.Timestamp, v.ValidationTimeSource);
        Assert.Equal(PdfChainStatus.Trusted, v.Chain);
        Assert.True(v.IsValid, string.Join("; ", v.Warnings));
    }

    [Fact]
    public void DigestAlgorithmMismatchIsInvalid()
    {
        var spec = new CmsSpec { Hash = HashAlgorithmName.SHA256, SignatureHashOverride = HashAlgorithmName.SHA384 };
        PdfSignatureVerification v = SignedPdf.VerifyOne(Signed(SigScheme.RsaPkcs1, HashAlgorithmName.SHA256, false, false, spec: spec));
        Assert.Equal(PdfCheckStatus.Invalid, v.SignatureCheck);
        Assert.Contains(v.Warnings, static w => w.Contains("digest algorithm", StringComparison.Ordinal));
    }

    [Fact]
    public void SignedAttributeDigestMismatch()
    {
        var spec = new CmsSpec { MessageDigestOverride = RandomNumberGenerator.GetBytes(32) };
        PdfSignatureVerification v = SignedPdf.VerifyOne(Signed(SigScheme.RsaPkcs1, HashAlgorithmName.SHA256, false, false, spec: spec));
        Assert.Equal(PdfCheckStatus.Invalid, v.Integrity);
        Assert.Equal(PdfCheckStatus.Valid, v.SignatureCheck);
        Assert.False(v.IsValid);
    }

    [Fact]
    public void RevocationCallbackIsHonouredAndNothingIsFetched()
    {
        byte[] file = Signed(SigScheme.RsaPkcs1, HashAlgorithmName.SHA256, false, false);
        var calls = new List<string>();
        PdfSignatureVerification revoked = SignedPdf.VerifyOne(file, new PdfSignatureVerifyOptions
        {
            TrustAnchors = [Pki.RootAnchor],
            RevocationCheck = (c, issuer) =>
            {
                calls.Add(c.Subject);
                return c.Subject.Contains("Rita", StringComparison.Ordinal) ? PdfRevocationStatus.Revoked : PdfRevocationStatus.Good;
            },
        });
        Assert.Equal(PdfChainStatus.Revoked, revoked.Chain);
        Assert.Equal(PdfRevocationStatus.Revoked, revoked.Revocation);
        Assert.False(revoked.IsValid);
        Assert.Equal(2, calls.Count);

        PdfSignatureVerification good = SignedPdf.VerifyOne(file, new PdfSignatureVerifyOptions { TrustAnchors = [Pki.RootAnchor], RevocationCheck = static (_, _) => PdfRevocationStatus.Good });
        Assert.Equal(PdfRevocationStatus.Good, good.Revocation);
        Assert.True(good.IsValid);
        Assert.DoesNotContain(good.Warnings, static w => w.StartsWith("Revocation was not checked", StringComparison.Ordinal));
    }

    [Fact]
    public void Sha1IsValidButFlaggedWeak()
    {
        var spec = new CmsSpec { Hash = HashAlgorithmName.SHA1 };
        PdfSignatureVerification v = SignedPdf.VerifyOne(Signed(SigScheme.RsaPkcs1, HashAlgorithmName.SHA1, false, false, spec: spec));
        Assert.Equal(PdfCheckStatus.Valid, v.SignatureCheck);
        Assert.True(v.UsesWeakAlgorithm);
        Assert.Equal("SHA-1", v.DigestAlgorithm);
    }

    [Fact]
    public void LegacyPkcs7Sha1()
    {
        byte[] file = SignedPdf.BaseFile(new SignedPdf.Options { SubFilter = "adbe.pkcs7.sha1" }, out _);
        var spec = new CmsSpec { Hash = HashAlgorithmName.SHA1 };
        byte[] signed = SignedPdf.Sign(file, digest =>
        {
            byte[] rd = digest(HashAlgorithmName.SHA1);
            return CmsKit.Build(CmsKit.Hash(HashAlgorithmName.SHA1, rd), Pki.RsaSigner, [Pki.RsaSigner, Pki.Intermediate], spec, rd);
        });
        PdfSignatureVerification v = SignedPdf.VerifyOne(signed);
        Assert.Equal(PdfCheckStatus.Valid, v.Integrity);
        Assert.Equal(PdfCheckStatus.Valid, v.SignatureCheck);
        Assert.True(v.UsesWeakAlgorithm);

        int at = System.Text.Encoding.Latin1.GetString(signed).IndexOf("Hello", StringComparison.Ordinal);
        signed[at] ^= 1;
        Assert.Equal(PdfCheckStatus.Invalid, SignedPdf.VerifyOne(signed).Integrity);
    }

    [Fact]
    public void X509RsaSha1IsReportedUnsupportedNotCrashing()
    {
        byte[] file = SignedPdf.BaseFile(new SignedPdf.Options { SubFilter = "adbe.x509.rsa_sha1" }, out _);
        byte[] signed = SignedPdf.Sign(file, _ => Der.Oct(RandomNumberGenerator.GetBytes(256)));
        PdfSignatureVerification v = SignedPdf.VerifyOne(signed);
        Assert.Equal(PdfCheckStatus.NotChecked, v.SignatureCheck);
        Assert.True(v.UsesWeakAlgorithm);
        Assert.False(v.IsValid);
    }

    [Fact]
    public void UnknownSubFilterAndGarbageContentsGiveWarningsNotExceptions()
    {
        byte[] file = SignedPdf.BaseFile(new SignedPdf.Options { SubFilter = "Vendor.Custom" }, out _);
        PdfSignatureVerification v = SignedPdf.VerifyOne(SignedPdf.Sign(file, _ => [1, 2, 3]));
        Assert.False(v.IsValid);
        Assert.NotEmpty(v.Warnings);

        byte[] cms = SignedPdf.BaseFile(new SignedPdf.Options(), out _);
        PdfSignatureVerification g = SignedPdf.VerifyOne(SignedPdf.Sign(cms, _ => [0x30, 0x82, 0xFF, 0xFF, 1, 2]));
        Assert.Equal(PdfCheckStatus.Invalid, g.SignatureCheck);
        Assert.False(g.IsValid);
    }

    [Fact]
    public void BerIndefiniteLengthCmsIsAccepted()
    {
        // Some signing tools write the outer structures with indefinite lengths. Re-wrap the CMS ContentInfo that way.
        byte[] file = SignedPdf.BaseFile(new SignedPdf.Options(), out _);
        byte[] signed = SignedPdf.Sign(file, digest =>
        {
            byte[] der = CmsKit.Build(digest(HashAlgorithmName.SHA256), Pki.RsaSigner, [Pki.RsaSigner, Pki.Intermediate], new CmsSpec());
            // ContentInfo ::= SEQUENCE { oid, [0] { SignedData } }: rebuild the SEQUENCE and the [0] with indefinite lengths.
            int oidLen = 2 + der[Skip(der) + 1];
            int start = Skip(der);
            byte[] oid = der[start..(start + oidLen)];
            int innerStart = start + oidLen;
            int contentStart = innerStart + HeaderLength(der, innerStart);
            byte[] signedData = der[contentStart..];
            return [0x30, 0x80, .. oid, 0xA0, 0x80, .. signedData, 0, 0, 0, 0];
        });
        PdfSignatureVerification v = SignedPdf.VerifyOne(signed);
        Assert.Equal(PdfCheckStatus.Valid, v.SignatureCheck);
        Assert.True(v.IsValid, string.Join("; ", v.Warnings));

        static int Skip(byte[] d) => d[1] < 0x80 ? 2 : 2 + (d[1] & 0x7F);
        static int HeaderLength(byte[] d, int at) => d[at + 1] < 0x80 ? 2 : 2 + (d[at + 1] & 0x7F);
    }

    [Fact]
    public void DocumentTimestampIsVerifiedAndNotedAsALaterChange()
    {
        byte[] file = SignedPdf.SignWith(SignedPdf.BaseFile(new SignedPdf.Options(), out _), Pki.RsaSigner, new CmsSpec());
        byte[] stamped = SignedPdf.AppendDocumentTimestamp(file, DateTimeOffset.UtcNow);
        using var doc = PdfDocument.Open(stamped);
        IReadOnlyList<PdfSignature> sigs = doc.GetSignatures();
        Assert.Equal(2, sigs.Count);
        Assert.Equal(PdfSignatureKind.Signature, sigs[0].Kind);
        Assert.Equal(PdfSignatureKind.DocumentTimestamp, sigs[1].Kind);
        PdfSignatureVerification first = sigs[0].Verify(Pki.Trusting());
        Assert.True(first.HasLaterRevisions);
        Assert.Equal(PdfSignatureCoverage.EarlierRevisionAllowedChanges, first.Coverage);
        Assert.True(first.IsValid, string.Join("; ", first.Warnings));
        PdfSignatureVerification ts = sigs[1].Verify(Pki.Trusting());
        Assert.Equal(PdfCheckStatus.Valid, ts.Integrity);
        Assert.Equal(PdfCheckStatus.Valid, ts.SignatureCheck);
        Assert.Equal(PdfChainStatus.Trusted, ts.Chain);
        Assert.Equal(PdfSignatureCoverage.WholeDocument, ts.Coverage);
        Assert.True(ts.IsValid, string.Join("; ", ts.Warnings));
        Assert.NotNull(ts.Timestamp!.Time);

        // Tampering with the stamped bytes breaks the time-stamp's imprint.
        int at = System.Text.Encoding.Latin1.GetString(stamped).IndexOf("Hello", StringComparison.Ordinal);
        stamped[at] ^= 1;
        using var bad = PdfDocument.Open(stamped);
        Assert.Equal(PdfCheckStatus.Invalid, bad.GetSignatures()[1].Verify(Pki.Trusting()).Integrity);
    }

    [Fact]
    public void ExtraByteAfterTheRangeEndsIsAllowedOnlyAsWhitespace()
    {
        byte[] file = SignedPdf.SignWith(SignedPdf.BaseFile(new SignedPdf.Options(), out _), Pki.RsaSigner, new CmsSpec());
        // Trailing garbage after the last %%EOF is not signed: the range ends before the end of the file.
        byte[] garbage = [.. file, .. "junk"u8.ToArray()];
        PdfSignatureVerification v = SignedPdf.VerifyOne(garbage);
        Assert.NotEqual(PdfSignatureCoverage.Unknown, v.Coverage);
        Assert.Equal(PdfCheckStatus.Valid, v.Integrity);
    }

    [Fact]
    public void CancellationIsHonoured()
    {
        byte[] file = Signed(SigScheme.RsaPkcs1, HashAlgorithmName.SHA256, false, false);
        using var doc = PdfDocument.Open(file);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => doc.GetSignatures()[0].Verify(Pki.Trusting(), cts.Token));
    }
}
