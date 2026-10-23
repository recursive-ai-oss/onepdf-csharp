using System;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using OnePdf.Tests.Signatures;

namespace OnePdf.AotSmoke;

/// <summary>Verifies generated signatures (RSA, RSA-PSS and ECDSA, with a time-stamp) so the AOT build shows the platform primitives work trimmed.</summary>
internal static class SignatureSmoke
{
    public static string? Run()
    {
        TestPki pki = TestPki.Shared;
        foreach ((SigScheme scheme, bool timestamp) in new[] { (SigScheme.RsaPkcs1, false), (SigScheme.RsaPss, true), (SigScheme.Ecdsa, true) })
        {
            byte[] file = SignedPdf.BaseFile(new SignedPdf.Options { SubFilter = "ETSI.CAdES.detached" }, out _);
            var spec = new CmsSpec { Scheme = scheme, Hash = HashAlgorithmName.SHA256, Cades = true, Timestamp = timestamp };
            byte[] signed = SignedPdf.SignWith(file, scheme == SigScheme.Ecdsa ? pki.EcSigner : pki.RsaSigner, spec);
            PdfSignatureVerification v = SignedPdf.VerifyOne(signed);
            if (!v.IsValid)
            {
                return $"{scheme}: {v.ByteRange} {v.Integrity} {v.SignatureCheck} {v.Chain} {string.Join("; ", v.Warnings)}";
            }

            signed[signed.AsSpan().IndexOf("Hello"u8)] ^= 1;
            if (SignedPdf.VerifyOne(signed).Integrity != PdfCheckStatus.Invalid)
            {
                return $"{scheme}: tampering was not detected";
            }
        }

        return Sign();
    }

    // Signs through the public API (PAdES B-T with RSA-PSS, then an ECDSA external signer plus a document time-stamp) and verifies the result.
    private static string? Sign()
    {
        TestPki pki = TestPki.Shared;
        byte[] blank;
        using (PdfDocument empty = PdfDocument.CreateEmpty())
        using (var ms = new System.IO.MemoryStream())
        {
            empty.InsertBlankPage(0, 200, 200);
            empty.Save(ms);
            blank = ms.ToArray();
        }

        PdfTimestampCallback tsa = request => DerSeq(CmsKit.Timestamp(request.MessageImprint.ToArray(), request.HashAlgorithm, DateTimeOffset.UtcNow, null, request.Nonce.ToArray()));
        byte[] one;
        using (PdfDocument doc = PdfDocument.Open(blank, new PdfOpenOptions { AllowEditing = true }))
        using (var ms = new System.IO.MemoryStream())
        {
            doc.Sign(ms, PdfSigningCredential.FromCertificate(pki.RsaSigner, [pki.Intermediate]),
                new PdfSigningOptions { RsaPadding = PdfRsaPadding.Pss, Timestamp = tsa, Rectangle = new PdfRectangle(10, 10, 150, 50), Reason = "smoke" });
            one = ms.ToArray();
        }

        byte[] two;
        using (PdfDocument doc = PdfDocument.Open(one, new PdfOpenOptions { AllowEditing = true }))
        using (var ms = new System.IO.MemoryStream())
        {
            PdfExternalSigner external = request =>
            {
                using ECDsa ec = pki.EcSigner.GetECDsaPrivateKey()!;
                return ec.SignHash(request.Hash.ToArray(), DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            };
            doc.Sign(ms, PdfSigningCredential.FromExternalSigner(X509CertificateLoader.LoadCertificate(pki.EcSigner.RawData), external, [pki.Intermediate]));
            two = ms.ToArray();
        }

        byte[] three;
        using (PdfDocument doc = PdfDocument.Open(two, new PdfOpenOptions { AllowEditing = true }))
        using (var ms = new System.IO.MemoryStream())
        {
            doc.AddDocumentTimestamp(ms, tsa);
            three = ms.ToArray();
        }

        using PdfDocument check = PdfDocument.Open(three);
        PdfSignatureReport report = check.VerifySignatures(pki.Trusting());
        return report.Signatures.Count == 3 && report.AllValid ? null : "signing: " + report.Summary + " " + string.Join("; ", report.Signatures.SelectMany(static s => s.Warnings));
    }

    private static byte[] DerSeq(byte[] token) => Der.Seq(Der.Seq(Der.Int(0)), token);
}
