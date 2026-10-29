using System;
using System.IO;
using System.Text;
using OnePdf;
using OnePdf.Fonts;

namespace OnePdf.AotSmoke;

/// <summary>Reads a font program and embeds a substitute subset into a document's non-embedded font.</summary>
internal static class FontEmbedSmoke
{
    public static string? Run()
    {
        string pdf =
            "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n" +
            "4 0 obj\n<< /Length 36 >>\nstream\nBT /F1 12 Tf 20 100 Td (Hello) Tj ET\nendstream\nendobj\n" +
            "5 0 obj\n<< /Type /Font /Subtype /TrueType /BaseFont /Arial /Encoding /WinAnsiEncoding >>\nendobj\n" +
            "trailer\n<< /Root 1 0 R >>\n";
        using PdfDocument doc = PdfDocument.Open(Encoding.ASCII.GetBytes(pdf), new PdfOpenOptions { AllowEditing = true });
        doc.FontProvider = FontsPackage.Provider;
        PdfSubstituteEmbedReport report = doc.EmbedSubstituteFonts();
        if (report.EmbeddedCount != 1 || report.FontProviderMissing || report.Entries[0].WidthConflicts.Count != 0)
        {
            return "the substitute font was not embedded";
        }

        if (doc.AddMissingToUnicode().FontsWithUnknownCodes != 0)
        {
            return "AddMissingToUnicode found unknown codes in a WinAnsi font";
        }

        // The caller-supplied paths: a face for fonts with no substitute, text for unmapped codes, widths of embedded CFF programs.
        _ = doc.EmbedSubstituteFonts(new PdfSubstituteEmbedOptions { FaceForFont = static _ => null });
        _ = doc.AddMissingToUnicode(new PdfToUnicodeOptions { CodeToText = static _ => null });
        if (doc.MatchEmbeddedFontWidths().FontsRewritten != 0)
        {
            return "a TrueType font was rewritten by the CFF width match";
        }

        using var ms = new MemoryStream();
        doc.Save(ms);
        using PdfDocument back = PdfDocument.Open(ms.ToArray());
        PdfDictionary font = back.Pages[0].Dictionary.GetDictionary("Resources")!.GetDictionary("Font")!.GetDictionary("F1")!;
        if (font.GetDictionary("FontDescriptor")?.Get("FontFile2") is not PdfStream file)
        {
            return "no embedded program after save";
        }

        PdfFontFile program = PdfFontFile.Open(file.GetDecodedBytes());
        if (program.Kind != PdfFontFileKind.TrueType || program.GetGlyphForUnicode('H') == 0 || program.GetAdvanceWidth(program.GetGlyphForUnicode('H')) <= 0)
        {
            return "the embedded program did not read back";
        }

        return null;
    }
}
