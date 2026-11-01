using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using OnePdf.Tests.Signatures;

namespace OnePdf.Fuzz;

/// <summary>
/// Two-phase signing target. Six parameter bytes (field name, page, rectangle, certification, sub-filter, hash, reserve, field lock),
/// then a PDF. The file is opened for editing, a placeholder is written, the hole is filled with a dummy CMS, and the result is
/// reopened: signatures are listed and the signed bytes are read through <c>OpenSignedData</c>; the whole-document stream is
/// compared with the input. Only the library's typed exceptions may escape; every option value is a legal one, so an argument
/// exception is a finding too.
/// </summary>
internal static class PlaceholderTarget
{
    private static readonly string?[] Names = [null, "Sig2", "Signature1", "X", "a.b", "", "Name", "Approval"];

    private static readonly string?[] SubFilters = [null, "ETSI.CAdES.detached", "adbe.pkcs7.detached", "ETSI.RFC3161", "x.y-z_1"];

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 7)
        {
            return;
        }

        byte b0 = data[0], b1 = data[1], b2 = data[2], b3 = data[3], b4 = data[4], b5 = data[5];
        byte[] input = data[6..].ToArray();
        var limits = PdfLimits.Default with { MaxDecodedStreamBytes = 8 << 20, MaxObjects = 200_000 };
        try
        {
            using PdfDocument doc = PdfDocument.Open(input, new PdfOpenOptions { Limits = limits, AllowEditing = true });
            int pages = doc.Pages.Count;
            var options = new PdfSigningOptions
            {
                FieldName = Names[b0 % Names.Length],
                PageIndex = pages == 0 ? 0 : b1 % pages,
                Rectangle = (b2 & 1) == 0 ? null : new PdfRectangle(10 + (b2 >> 1), 10, 150 + b2, 40 + (b3 & 31)),
                Certify = (b3 >> 5) switch { 1 => PdfCertificationLevel.NoChanges, 2 => PdfCertificationLevel.FormFilling, 3 => PdfCertificationLevel.FormFillingAndAnnotations, _ => null },
                SubFilter = SubFilters[b4 % SubFilters.Length],
                Hash = (b4 >> 4) switch { 1 => HashAlgorithmName.SHA384, 2 => HashAlgorithmName.SHA512, _ => HashAlgorithmName.SHA256 },
                Reason = (b5 & 1) == 0 ? null : "fuzz (reason)",
                SignerName = (b5 & 2) == 0 ? null : "Fuzz Signer",
                AllowBreakingCertification = (b5 & 4) != 0,
                FieldLock = (b5 & 8) == 0 ? null : new PdfSignatureLock(PdfLockAction.Include, ["Name"], null),
            };
            int reserve = 64 + ((b5 >> 4) * 700);
            using var ms = new MemoryStream();
            PdfSignaturePlaceholder p = doc.AddSignaturePlaceholder(ms, options, reserve);
            byte[] cms = [0x30, 0x03, 0x02, 0x01, 0x05];
            p.Fill(ms, cms);
            byte[] written = ms.ToArray();
            if (!written.AsSpan(0, input.Length).SequenceEqual(input) || p.SignedLength != written.Length)
            {
                throw new InvalidOperationException("the placeholder output is not the input plus an update");
            }

            using (Stream whole = doc.OpenRead())
            {
                byte[] again = new byte[(int)whole.Length];
                whole.ReadExactly(again);
                if (!again.AsSpan().SequenceEqual(input))
                {
                    throw new InvalidOperationException("OpenRead is not the original source");
                }
            }

            if (PdfDocument.TryOpen(written, out PdfDocument? check, out _) && check is not null)
            {
                using (check)
                {
                    foreach (PdfSignature sig in check.GetSignatures())
                    {
                        using Stream? signed = sig.OpenSignedData();
                        signed?.CopyTo(Stream.Null);
                    }
                }
            }
        }
        catch (PdfException)
        {
        }
    }

    /// <summary>Starting inputs: the signing tests' base file with a few option sets, and a document with no pages.</summary>
    public static IEnumerable<byte[]> Seeds()
    {
        byte[] baseFile = SignedPdf.BaseFile(new SignedPdf.Options(), out _);
        for (byte i = 0; i < 8; i++)
        {
            yield return [i, 0, (byte)(i * 37), (byte)(i << 5), (byte)(i * 17), (byte)(i * 0x23), .. baseFile];
        }

        yield return [0, 0, 0, 0, 0, 0, .. "%PDF-1.7\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\ntrailer\n<< /Root 1 0 R /Size 3 >>\n"u8.ToArray()];
    }
}
