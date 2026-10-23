using System;
using System.IO;
using OnePdf;

namespace OnePdf.Fuzz;

/// <summary>
/// PDF/A target: opens the bytes as an editable document, checks it, prepares it and saves it as PDF/A (part and level chosen from
/// the first byte). A save that succeeds must read back as a document the checker finds nothing wrong with. Only the library's
/// typed exceptions may escape.
/// </summary>
internal static class PdfATarget
{
    public static void Run(ReadOnlySpan<byte> data)
    {
        int sel = data.Length > 0 ? data[0] : 0;
        var limits = PdfLimits.Default with { MaxDecodedStreamBytes = 8 << 20, MaxObjects = 200_000 };
        var options = new PdfAOptions { Part = (sel & 1) == 0 ? PdfAPart.Part2 : PdfAPart.Part3, Conformance = (sel & 2) == 0 ? PdfAConformance.B : PdfAConformance.U };
        byte[] input = data.ToArray();
        try
        {
            using (PdfDocument ro = PdfDocument.Open(input, new PdfOpenOptions { Limits = limits }))
            {
                _ = ro.CheckPdfA(options);
            }

            using PdfDocument doc = PdfDocument.Open(input, new PdfOpenOptions { Limits = limits, AllowEditing = true });
            if ((sel & 4) != 0)
            {
                doc.AttachFile("a.txt", new byte[] { 1, 2, 3 }, new PdfAttachmentOptions { MimeType = "text/plain" });
            }

            using var ms = new MemoryStream();
            try
            {
                doc.Save(ms, new PdfSaveOptions
                {
                    XrefFormat = (sel & 8) != 0 ? PdfXrefFormat.Table : PdfXrefFormat.Stream,
                    Encryption = doc.IsEncrypted ? PdfEncryptionAction.Remove : PdfEncryptionAction.Keep,
                    PdfA = options,
                });
            }
            catch (PdfAException ex)
            {
                if (ex.Violations.Count == 0 || ms.Length != 0)
                {
                    throw new InvalidOperationException("a refusal must list violations and write nothing");
                }

                return;
            }

            using PdfDocument back = PdfDocument.Open(ms.ToArray(), new PdfOpenOptions { Limits = limits });
            foreach (PdfAViolation v in back.CheckPdfA(options))
            {
                if (!v.Message.Contains("cannot be read", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("a written PDF/A file fails the checker: " + v);
                }
            }
        }
        catch (PdfException)
        {
        }
    }
}
