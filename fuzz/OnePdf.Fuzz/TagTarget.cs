using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OnePdf;
using OnePdf.Text;

namespace OnePdf.Fuzz;

/// <summary>
/// Tagging target. The input is 2 parameter bytes followed by a PDF file or a bare content stream. The pages are extracted with their
/// glyphs, a plan gives blocks (or single words, or every second block, or a graphics area) a role, the plan is applied, the document is
/// saved and read again. Only the library's typed exceptions may escape, and when tagging succeeded the saved file must reopen with the
/// same extracted text and the structure tree the check can read.
/// </summary>
internal static class TagTarget
{
    private static readonly PdfLimits Limits = PdfLimits.Default with { MaxOperatorsPerPage = 100_000, MaxDecodedStreamBytes = 8 << 20, MaxObjects = 200_000, MaxImagePixels = 1 << 22 };

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length <= 2)
        {
            return;
        }

        int sel = data[0];
        ReadOnlySpan<byte> body = data[2..];
        byte[] file = body.Length > 4 && body[0] == '%' && body[1] == 'P' && body[2] == 'D' && body[3] == 'F' ? body.ToArray() : ContentTarget.Wrap(body);
        try
        {
            using PdfDocument doc = PdfDocument.Open(file, new PdfOpenOptions { Limits = Limits, AllowEditing = true });
            int pageCount = Math.Min(doc.Pages.Count, 3);
            if (pageCount == 0)
            {
                return;
            }

            var options = new PdfTextOptions { RetainGlyphs = true, UseStructureTree = false };
            var pages = new List<PdfPageText>();
            for (int i = 0; i < pageCount; i++)
            {
                pages.Add(doc.Pages[i].ExtractText(options));
            }

            string before = string.Concat(pages.Select(static p => p.Text));
            PdfTaggingPlan plan = doc.CreateTaggingPlan();
            plan.Title = "t";
            plan.Language = "en";
            foreach (PdfPageText page in pages)
            {
                for (int b = 0; b < page.Blocks.Count; b++)
                {
                    if ((sel & 1) != 0 && (b & 1) == 1)
                    {
                        continue;
                    }

                    PdfTextBlock block = page.Blocks[b];
                    if ((sel & 2) != 0)
                    {
                        foreach (PdfTextLine line in block.Lines)
                        {
                            for (int w = 0; w < line.Words.Count; w++)
                            {
                                plan.Add(PdfTagRole.P, PdfTagContent.Words(page, line, w));
                            }
                        }
                    }
                    else
                    {
                        plan.Add(b % 3 == 0 ? PdfTagRole.H1 : PdfTagRole.P, PdfTagContent.Block(page, block));
                    }
                }

                if ((sel & 4) != 0)
                {
                    plan.Add(PdfTagRole.Figure, PdfTagContent.Graphics(page, new PdfRectangle(0, 0, page.Width / 2, page.Height))).Alt = "area";
                }

                if ((sel & 8) != 0)
                {
                    plan.ArtifactHeadersAndFooters([page]);
                }
            }

            doc.ApplyTagging(plan);
            using var ms = new MemoryStream();
            doc.Save(ms, new PdfSaveOptions { XrefFormat = PdfXrefFormat.Table, UseObjectStreams = false });
            using PdfDocument again = PdfDocument.Open(ms.ToArray(), new PdfOpenOptions { Limits = Limits });
            string after = string.Concat(Enumerable.Range(0, Math.Min(pageCount, again.Pages.Count)).Select(i => again.Pages[i].ExtractText(options).Text));
            if (before != after)
            {
                throw new InvalidOperationException("the text changed by tagging");
            }

            _ = again.CheckPdfUA();
        }
        catch (PdfException)
        {
        }
    }
}
