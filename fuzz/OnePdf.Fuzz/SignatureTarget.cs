using System;
using System.Collections.Generic;
using System.Text;
using OnePdf.Asn1;
using OnePdf.Tests.Signatures;

namespace OnePdf.Fuzz;

/// <summary>
/// Signature target. A first byte picks the mode. Even: the rest is a CMS blob; it is parsed on its own and also placed in the
/// <c>/Contents</c> of a correctly ranged signed file, so certificate parsing, signature checks, chain building and
/// the time-stamp path see it. Odd: the rest is a whole PDF and every signature in it is verified, with change analysis.
/// Only the library's typed exceptions may escape.
/// </summary>
internal static class SignatureTarget
{
    private static readonly Lazy<(byte[] File, int HoleStart)> Template = new(Build);

    /// <summary>A signed file and the offset of its <c>/Contents</c> hex digits (for the seed writer and the CMS mode).</summary>
    public static (byte[] File, int HoleStart) Sample => Template.Value;

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return;
        }

        byte[] rest = data[1..].ToArray();
        try
        {
            if ((data[0] & 1) == 0)
            {
                if (CmsSignedData.TryParse(rest, out CmsSignedData? sd, out _) && sd is not null)
                {
                    _ = sd.Signers.Count + sd.Certificates.Count;
                }

                (byte[] file, int hole) = Template.Value;
                byte[] copy = (byte[])file.Clone();
                string hex = Convert.ToHexString(rest, 0, Math.Min(rest.Length, SignedPdf.ContentsBytes));
                Encoding.ASCII.GetBytes(hex).CopyTo(copy, hole);
                Verify(copy);
            }
            else
            {
                Verify(rest);
            }
        }
        catch (PdfException)
        {
        }
    }

    private static void Verify(byte[] file)
    {
        if (!PdfDocument.TryOpen(file, out PdfDocument? doc, out _))
        {
            return;
        }

        using (doc)
        {
            PdfSignatureReport report = doc.VerifySignatures(new PdfSignatureVerifyOptions { TrustAnchors = [TestPki.Shared.RootAnchor] });
            foreach (PdfSignatureVerification v in report.Signatures)
            {
                _ = v.IsValid;
                _ = v.Signature.Contents.Length;
            }
        }
    }

    private static (byte[], int) Build()
    {
        byte[] file = SignedPdf.SignWith(SignedPdf.BaseFile(new SignedPdf.Options { SubFilter = "ETSI.CAdES.detached" }, out _), TestPki.Shared.RsaSigner, new CmsSpec { Cades = true, Timestamp = true });
        int at = Encoding.Latin1.GetString(file).IndexOf("/Contents <", StringComparison.Ordinal) + "/Contents <".Length;
        return (file, at);
    }

    /// <summary>Starting inputs: a signed file, its CMS, and a second signature, a certified file with an update, and a time-stamp.</summary>
    public static IEnumerable<byte[]> Seeds()
    {
        (byte[] file, int hole) = Template.Value;
        yield return [1, .. file];
        byte[] cms = Convert.FromHexString(Encoding.ASCII.GetString(file, hole, SignedPdf.ContentsBytes * 2));
        int len = cms.Length;
        while (len > 0 && cms[len - 1] == 0)
        {
            len--;
        }

        yield return [0, .. cms.AsSpan(0, len)];
        byte[] certified = SignedPdf.SignWith(SignedPdf.BaseFile(new SignedPdf.Options { CertifyLevel = 2 }, out _), TestPki.Shared.EcSigner, new CmsSpec { Scheme = SigScheme.Ecdsa });
        yield return [1, .. SignedPdf.AppendDocumentTimestamp(certified, DateTimeOffset.UtcNow)];
        yield return [1, .. SignedPdf.AppendSecondSignature(certified, digest => CmsKit.Build(digest(System.Security.Cryptography.HashAlgorithmName.SHA256), TestPki.Shared.RsaSigner, [TestPki.Shared.RsaSigner], new CmsSpec { Scheme = SigScheme.RsaPss }))];
    }
}
