using System;
using System.Security.Cryptography;
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

        return null;
    }
}
