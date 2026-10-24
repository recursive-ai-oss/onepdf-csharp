using System;
using System.IO;
using OnePdf;
using OnePdf.Tests.Creation;
using OnePdf.Tests.Images;

namespace OnePdf.AotSmoke;

/// <summary>Streams a tagged two-page document with a PNG image (plain and with alpha), a shaped glyph run with a ligature-style cluster, a link and a bookmark, then reads it back.</summary>
internal static class WriterSmoke
{
    public static string? Run()
    {
        using var ms = new MemoryStream();
        using (var w = new PdfDocumentWriter(ms, new PdfDocumentWriterOptions { DocumentId = new byte[16], LeaveOpen = true, Language = "en" }))
        {
            PdfCreatedFont font = w.AddFont(SyntheticFont.Build());
            PdfCreatedImage plain = w.AddPngImage(Png(4, 4, 2));
            PdfCreatedImage alpha = w.AddPngImage(Png(4, 4, 6));
            PdfFormXObject total = w.ReserveForm(new PdfRectangle(0, 0, 30, 12));
            PdfStructureElement doc = w.BeginStructureElement("Document");
            for (int i = 0; i < 2; i++)
            {
                PdfStreamPageBuilder p = w.BeginPage(200, 200);
                p.SetFont(font, 14);
                PdfStructureElement para = w.BeginStructureElement("P");
                p.BeginTaggedContent(para);
                p.DrawText("ABCD", 10, 150);
                // Glyphs 1 and 2 as one two-glyph cluster for the text "AB" (an ActualText span), then glyph 3 for "C".
                p.DrawGlyphRun(10, 120, [new PdfShapedGlyph(2, 0, 600), new PdfShapedGlyph(1, 0, 600), new PdfShapedGlyph(3, 2, 600)], "ABC");
                p.EndMarkedContent();
                w.EndStructureElement();
                PdfStructureElement fig = w.BeginStructureElement("Figure", new PdfStructureElementOptions { AlternateText = "pictures" });
                p.BeginTaggedContent(fig);
                p.DrawImage(plain, 10, 20, 30, 30);
                p.DrawImage(alpha, 60, 20, 30, 30);
                p.EndMarkedContent();
                w.EndStructureElement();
                p.BeginArtifact(PdfArtifactType.Pagination, PdfArtifactSubtype.Footer);
                p.DrawForm(total);
                p.EndMarkedContent();
                PdfStructureElement link = w.BeginStructureElement("Link");
                p.AddLink(new PdfRectangle(100, 100, 150, 120), "https://example.com", link, "example");
                w.EndStructureElement();
                w.AddBookmark("Page " + (i + 1), 0, i, 190);
                p.Commit();
            }

            w.EndStructureElement();
            _ = doc;
            PdfFormContentBuilder tf = w.BeginForm(total);
            tf.SetFont(font, 10);
            tf.DrawText("AA", 0, 2);
            tf.Commit();
            w.Finish();
        }

        using PdfDocument back = PdfDocument.Open(ms.ToArray());
        if (back.Pages.Count != 2 || back.Outline.Count != 2 || back.StructureTreeRoot is null)
        {
            return "streamed document did not read back";
        }

        string text = back.Pages[0].ExtractText().Text;
        if (!text.Contains("ABCD", StringComparison.Ordinal) || !text.Contains("ABC", StringComparison.Ordinal))
        {
            return "glyph run text did not extract: " + text;
        }

        return null;
    }

    private static byte[] Png(int w, int h, int colorType)
    {
        var spec = new PngKit.Spec(w, h, colorType, 8);
        return PngKit.Encode(spec, PngKit.RandomRows(spec, 1));
    }
}
