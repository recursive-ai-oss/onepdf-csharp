using System;
using System.IO;
using OnePdf;

namespace OnePdf.Fuzz;

/// <summary>
/// PDF/A and PDF/UA target: opens the bytes as an editable document, checks it, prepares it and saves it as PDF/A, PDF/UA-1 or both
/// (part, level and profile chosen from the first byte; PDF/UA-2 is one of the choices). A save that succeeds must read back as a document the checkers find nothing
/// wrong with. Only the library's typed exceptions may escape.
/// </summary>
internal static class PdfATarget
{
    public static void Run(ReadOnlySpan<byte> data)
    {
        int sel = data.Length > 0 ? data[0] : 0;
        var limits = PdfLimits.Default with { MaxDecodedStreamBytes = 8 << 20, MaxObjects = 200_000 };
        var options = new PdfAOptions { Part = (sel & 1) == 0 ? PdfAPart.Part2 : PdfAPart.Part3, Conformance = (sel & 16) != 0 ? PdfAConformance.A : (sel & 2) == 0 ? PdfAConformance.B : PdfAConformance.U };
        bool uaOnly = (sel & 64) != 0;
        PdfUAOptions? ua = (sel & 32) != 0 || uaOnly ? new PdfUAOptions { Language = "en", Part = (sel & 128) != 0 ? PdfUAPart.Part2 : PdfUAPart.Part1, RemoveInvisibleNotdefText = (sel & 4) != 0 } : null;
        PdfAOptions? pdfa = uaOnly ? null : options;
        byte[] input = data.ToArray();
        try
        {
            using (PdfDocument ro = PdfDocument.Open(input, new PdfOpenOptions { Limits = limits }))
            {
                _ = ro.CheckPdfA(options);
                _ = ro.CheckPdfUA();
                _ = ro.CheckPdfUA(PdfUAPart.Part2);
            }

            using PdfDocument doc = PdfDocument.Open(input, new PdfOpenOptions { Limits = limits, AllowEditing = true });
            if ((sel & 2) != 0)
            {
                // The .notdef removal rewrites page and form content; what is left may only be what it kept, never more than there was.
                int before = doc.FindNotdefText().Count;
                _ = doc.RemoveNotdefText(new PdfNotdefRemoveOptions { Scope = (sel & 16) != 0 ? PdfNotdefScope.All : PdfNotdefScope.Invisible });
                if (doc.FindNotdefText().Count > before)
                {
                    throw new InvalidOperationException("removing .notdef text left more of it than there was");
                }
            }

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
                    PdfA = pdfa,
                    PdfUA = ua,
                });
            }
            catch (PdfUAException ex)
            {
                if (ex.Violations.Count == 0 || ms.Length != 0)
                {
                    throw new InvalidOperationException("a refusal must list violations and write nothing");
                }

                return;
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
            if (pdfa is not null)
            {
                foreach (PdfAViolation v in back.CheckPdfA(pdfa))
                {
                    if (!v.Message.Contains("cannot be read", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("a written PDF/A file fails the checker: " + v);
                    }
                }
            }

            if (ua is not null)
            {
                foreach (PdfUAViolation v in back.CheckPdfUA(ua.Part))
                {
                    if (!v.Message.Contains("cannot be read", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("a written PDF/UA file fails the checker: " + v);
                    }
                }
            }
        }
        catch (PdfException)
        {
        }
    }
}
