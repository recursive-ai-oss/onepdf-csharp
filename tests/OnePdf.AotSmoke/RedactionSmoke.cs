using System;
using System.IO;
using System.Linq;
using OnePdf;

namespace OnePdf.AotSmoke;

/// <summary>Writes a page with a line of text, redacts one word, saves, and checks that the word is gone and the others are not.</summary>
internal static class RedactionSmoke
{
    public static string? Run()
    {
        using var source = new MemoryStream();
        using (PdfDocument doc = PdfDocument.CreateEmpty())
        {
            PdfPage page = doc.InsertBlankPage(0, 300, 200);
            PdfCreatedFont font = doc.AddStandardFont("Helvetica");
            using (PdfPageContentBuilder b = page.BeginContent())
            {
                b.SetFont(font, 14);
                b.DrawText("Keep secret words", 10, 100);
                b.Commit();
            }

            doc.Save(source, new PdfSaveOptions { DocumentId = new byte[16] });
        }

        using PdfDocument edit = PdfDocument.Open(source.ToArray(), new PdfOpenOptions { AllowEditing = true });
        PdfRectangle box = edit.Pages[0].ExtractText().Words.First(w => w.Text == "secret").Bounds;
        PdfRedactionResult result = edit.Redact([new PdfRedactionRegion(0, new PdfRectangle(box.Left + 0.5, box.Bottom + 1, box.Right - 0.5, box.Top - 1))]);
        if (result.GlyphsRemoved != 6)
        {
            return "expected 6 glyphs removed, got " + result.GlyphsRemoved;
        }

        using var saved = new MemoryStream();
        edit.Save(saved);
        using PdfDocument back = PdfDocument.Open(saved.ToArray());
        string text = back.ExtractText()[0].Text;
        if (text.Contains("secret", StringComparison.Ordinal) || !text.Contains("Keep", StringComparison.Ordinal) || !text.Contains("words", StringComparison.Ordinal))
        {
            return "unexpected text after redaction: " + text;
        }

        return null;
    }
}
