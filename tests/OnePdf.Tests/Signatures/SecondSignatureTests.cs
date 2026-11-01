using System.Security.Cryptography.X509Certificates;
using static OnePdf.Tests.Signatures.SigningFixture;

namespace OnePdf.Tests.Signatures;

/// <summary>
/// Adding a signature to a signed document is an update the earlier signatures must survive (ISO 32000-2 12.8.2.2, Table 257): the
/// new field and widget, the append to the page's <c>/Annots</c> and the form's <c>/Fields</c>, <c>/SigFlags</c> and the signature
/// itself. These cover every shape the update takes: with and without a certification, visible and invisible, <c>Sign</c> and the
/// two-phase placeholder, a new field and an existing empty one, and a page whose <c>/Annots</c> is absent, direct or indirect.
/// </summary>
public class SecondSignatureTests
{
    public static IEnumerable<object[]> Shapes()
    {
        foreach (string baseKind in new[] { "form", "bare", "indirect-annots", "object-streams" })
        {
            foreach (int? level in new int?[] { null, 2, 3 })
            {
                foreach (bool visible in new[] { false, true })
                {
                    foreach (bool placeholder in new[] { false, true })
                    {
                        foreach (bool existing in new[] { false, true })
                        {
                            if (existing && baseKind is not ("form" or "object-streams"))
                            {
                                continue;
                            }

                            yield return [baseKind, level ?? 0, visible, placeholder, existing];
                        }
                    }
                }
            }
        }
    }

    private static byte[] BaseFile(string kind) => kind switch
    {
        "form" => UnsignedPdf(),
        "bare" => UnsignedPdf(fields: false),
        "object-streams" => Resaved(UnsignedPdf()),
        _ => IndirectAnnotsPdf(),
    };

    // The same document rewritten with a cross-reference stream and object streams, as most producers write it.
    private static byte[] Resaved(byte[] input)
    {
        using PdfDocument doc = Editable(input);
        using var ms = new MemoryStream();
        doc.Save(ms, new PdfSaveOptions { XrefFormat = PdfXrefFormat.Stream });
        return ms.ToArray();
    }

    // A page whose /Annots is an indirect array of a single (non-form) link, and whose form is absent.
    private static byte[] IndirectAnnotsPdf()
    {
        string s =
            "%PDF-1.7\n" +
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R /Resources << >> /Annots 5 0 R >>\nendobj\n" +
            "4 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n" +
            "5 0 obj\n[6 0 R]\nendobj\n" +
            "6 0 obj\n<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /Border [0 0 0] >>\nendobj\n";
        var sb = new System.Text.StringBuilder(s);
        var offsets = new List<int>();
        for (int n = 1; n <= 6; n++)
        {
            offsets.Add(s.IndexOf($"\n{n} 0 obj", StringComparison.Ordinal) + 1);
        }

        int xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        foreach (int o in offsets)
        {
            sb.Append($"{o:D10} 00000 n \n");
        }

        sb.Append($"trailer\n<< /Size 7 /Root 1 0 R /ID [<0102030405060708090A0B0C0D0E0F10> <0102030405060708090A0B0C0D0E0F10>] >>\nstartxref\n{xref}\n%%EOF\n");
        return System.Text.Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static PdfSigningOptions Options(string? field, bool visible, int? level) => new()
    {
        FieldName = field,
        Rectangle = visible ? new PdfRectangle(20, 20, 150, 60) : null,
        Certify = level is { } l ? (PdfCertificationLevel)l : null,
    };

    private static byte[] SignOnce(byte[] input, PdfSigningOptions options, bool placeholder, X509Certificate2 cert)
    {
        using PdfDocument doc = Editable(input);
        if (!placeholder)
        {
            return SignToBytes(doc, Credential(cert), options);
        }

        using var ms = new MemoryStream();
        PdfSignaturePlaceholder p = doc.AddSignaturePlaceholder(ms, options);
        p.Fill(ms, CmsKit.Build(p.Digest, cert, [cert, TestPki.Shared.Intermediate], new CmsSpec { Hash = p.HashAlgorithm, Cades = true, Scheme = cert.GetRSAPublicKey() is null ? SigScheme.Ecdsa : SigScheme.RsaPkcs1 }));
        return ms.ToArray();
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void EarlierSignaturesSurviveAnAddedSignature(string kind, int level, bool visible, bool placeholder, bool existing)
    {
        // The first signature carries the certification; the second is an ordinary signature added afterwards.
        byte[] first = SignOnce(BaseFile(kind), Options(null, visible, level == 0 ? null : level), placeholder: false, TestPki.Shared.RsaSigner);
        byte[] second = SignOnce(first, Options(existing ? "Sig2" : "Second", visible, null), placeholder, TestPki.Shared.EcSigner);

        using PdfDocument check = PdfDocument.Open(second);
        PdfSignatureReport report = check.VerifySignatures(Trust);
        Assert.Equal(2, report.Signatures.Count);
        Assert.True(report.AllValid, WarningsOf(report));
        Assert.Equal(PdfSignatureCoverage.EarlierRevisionAllowedChanges, report.Signatures[0].Coverage);
        Assert.All(report.Signatures[0].Changes, c => Assert.True(c.Allowed, c.Description));
    }

    [Theory]
    [InlineData("form")]
    [InlineData("bare")]
    [InlineData("indirect-annots")]
    [InlineData("object-streams")]
    public void ThreeSignaturesInARowStayValid(string kind)
    {
        byte[] one = SignOnce(BaseFile(kind), Options(null, true, null), false, TestPki.Shared.RsaSigner);
        byte[] two = SignOnce(one, Options("B", false, null), true, TestPki.Shared.EcSigner);
        byte[] three = SignOnce(two, Options("C", true, null), false, TestPki.Shared.Ec384Signer);
        using PdfDocument check = PdfDocument.Open(three);
        PdfSignatureReport report = check.VerifySignatures(Trust);
        Assert.Equal(3, report.Signatures.Count);
        Assert.True(report.AllValid, WarningsOf(report));
    }

    [Fact]
    public void NoChangesCertificationStillFlagsASecondSignature()
    {
        byte[] first = SignOnce(UnsignedPdf(false), Options(null, true, 1), false, TestPki.Shared.RsaSigner);
        using PdfDocument doc = Editable(first);
        byte[] second = SignToBytes(doc, Credential(TestPki.Shared.EcSigner), new PdfSigningOptions { FieldName = "Second", AllowBreakingCertification = true });
        using PdfDocument check = PdfDocument.Open(second);
        PdfSignatureReport report = check.VerifySignatures(Trust);
        Assert.False(report.Signatures[0].IsValid);
        Assert.Equal(PdfSignatureCoverage.EarlierRevisionDisallowedChanges, report.Signatures[0].Coverage);
        Assert.True(report.Signatures[1].IsValid, WarningsOf(report));
    }

    [Theory]
    [InlineData("form")]
    [InlineData("bare")]
    [InlineData("indirect-annots")]
    [InlineData("object-streams")]
    public void DocumentTimestampOnANoChangesCertifiedDocumentLeavesItValid(string kind)
    {
        byte[] first = SignOnce(BaseFile(kind), Options(null, true, 1), false, TestPki.Shared.RsaSigner);
        using PdfDocument doc = Editable(first);
        using var ms = new MemoryStream();
        doc.AddDocumentTimestamp(ms, new TestTsa().Callback);
        using PdfDocument check = PdfDocument.Open(ms.ToArray());
        PdfSignatureReport report = check.VerifySignatures(Trust);
        Assert.Equal(2, report.Signatures.Count);
        Assert.True(report.AllValid, WarningsOf(report));
        Assert.All(report.Signatures[0].Changes, c => Assert.True(c.Allowed, c.Description));
    }

    [Fact]
    public void LockOnAnExistingFieldIsPartOfSigning()
    {
        byte[] first = SignOnce(UnsignedPdf(), Options(null, false, null), false, TestPki.Shared.RsaSigner);
        PdfSigningOptions locked = new() { FieldName = "Sig2", FieldLock = new PdfSignatureLock(PdfLockAction.Exclude, ["Other"], null) };
        byte[] second = SignOnce(first, locked, false, TestPki.Shared.EcSigner);
        using PdfDocument check = PdfDocument.Open(second);
        PdfSignatureReport report = check.VerifySignatures(Trust);
        Assert.True(report.AllValid, WarningsOf(report));
    }
}
