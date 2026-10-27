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
        int shift = data[1];
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

            // Duplicate collapsing visits near-identical positions in an order that depends on the last bits of the coordinates, so on hostile
            // files with thousands of overprinted copies a split text operator (which reaches the same place by another sum) can collapse
            // another copy. It is off for this comparison.
            var options = new PdfTextOptions { RetainGlyphs = true, UseStructureTree = false, CollapseDuplicates = false };
            var pages = new List<PdfPageText>();
            for (int i = 0; i < pageCount; i++)
            {
                pages.Add(doc.Pages[i].ExtractText(options));
            }

            List<PdfTextGlyph> before = [.. pages.SelectMany(static p => p.Glyphs)];
            if ((sel & 16) != 0)
            {
                // The happy path: roles by block number, so lists, tables, links, figures and artifacts all occur.
                var happy = new PdfTagOptions { Title = "t", Language = "en", KeepHeadersAndFooters = (sel & 8) != 0, SplitListBlocks = (sel & 32) == 0 };
                if ((sel & 4) != 0)
                {
                    happy.Figures.Add(new PdfTagFigure(0, new PdfRectangle(0, 0, pages[0].Width / 2, pages[0].Height), "area"));
                }

                PdfTaggingPlan p2 = doc.PlanTagging(pages, bl => RoleFor(bl, shift), happy);
                doc.ApplyTagging(p2);
                CheckSaved(doc, pageCount, before, options);
                return;
            }

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
            CheckSaved(doc, pageCount, before, options);
        }
        catch (PdfException)
        {
        }
    }

    private static PdfBlockRole? RoleFor(PdfTagBlock bl, int shift)
    {
        switch ((bl.BlockIndex + shift) % 6)
        {
            case 0: return PdfBlockRole.Heading(1 + (bl.BlockIndex % 6));
            case 1: return PdfBlockRole.Paragraph;
            case 2: return PdfBlockRole.ListItem;
            case 3: return PdfBlockRole.Table;
            case 4: return PdfBlockRole.TableContinued;
            default: return null;
        }
    }

    // The glyphs, in order, are the same and sit in the same places. Words and lines are not compared: they come from thresholds on gaps,
    // which a sum of advances taken in another order can tip on files whose glyphs have zero advance.
    private static void CheckSaved(PdfDocument doc, int pageCount, List<PdfTextGlyph> before, PdfTextOptions options)
    {
        using var ms = new MemoryStream();
        doc.Save(ms, new PdfSaveOptions { XrefFormat = PdfXrefFormat.Table, UseObjectStreams = false });
        using PdfDocument again = PdfDocument.Open(ms.ToArray(), new PdfOpenOptions { Limits = Limits });
        List<PdfTextGlyph> after = [.. Enumerable.Range(0, Math.Min(pageCount, again.Pages.Count)).SelectMany(i => again.Pages[i].ExtractText(options).Glyphs)];
        if (before.Count != after.Count)
        {
            throw new InvalidOperationException($"tagging changed the number of glyphs: {before.Count} to {after.Count}");
        }

        for (int i = 0; i < before.Count; i++)
        {
            if (before[i].Text != after[i].Text || Math.Abs(before[i].Matrix.E - after[i].Matrix.E) > 1e-6 || Math.Abs(before[i].Matrix.F - after[i].Matrix.F) > 1e-6)
            {
                throw new InvalidOperationException($"tagging changed glyph {i}: '{before[i].Text}' at {before[i].Matrix.E},{before[i].Matrix.F} became '{after[i].Text}' at {after[i].Matrix.E},{after[i].Matrix.F}");
            }
        }

        _ = again.CheckPdfUA();
    }
}
