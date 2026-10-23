using System;
using System.IO;
using OnePdf;
using OnePdf.Tests.Creation;

namespace OnePdf.AotSmoke;

/// <summary>Writes a PDF/A-2b file with an embedded font, a standard font replaced by a Liberation face, and a stamp; reads it back and checks it.</summary>
internal static class PdfASmoke
{
    public static string? Run()
    {
        using PdfDocument doc = PdfDocument.CreateEmpty();
        doc.FontProvider = OnePdf.Fonts.FontsPackage.Provider;
        doc.SetInfo("Title", "Smoke");
        PdfPage page = doc.InsertBlankPage(0, 300, 200);
        PdfCreatedFont embedded = doc.AddFont(SyntheticFont.Build());
        PdfCreatedFont standard = doc.AddStandardFont("Helvetica");
        using (PdfPageContentBuilder b = page.BeginContent())
        {
            b.SetFont(embedded, 14);
            b.DrawText("ABCD", 10, 150);
            b.SetFont(standard, 12);
            b.DrawText("Plain text", 10, 100);
            b.Commit();
        }

        using var ms = new MemoryStream();
        doc.Save(ms, new PdfSaveOptions { DocumentId = new byte[16], PdfA = new PdfAOptions() });
        using PdfDocument back = PdfDocument.Open(ms.ToArray());
        if (back.CheckPdfA().Count != 0 || back.Xmp?.PdfAPart != 2 || back.Xmp.Title != "Smoke" || !back.ExtractText()[0].Text.Contains("Plain text", StringComparison.Ordinal))
        {
            return "PDF/A output did not read back as PDF/A-2b";
        }

        using PdfDocument bad = PdfDocument.CreateEmpty();
        bad.FontProvider = new NoSubstitutes();
        PdfPage p2 = bad.InsertBlankPage(0, 100, 100);
        using (PdfPageContentBuilder b = p2.BeginContent())
        {
            b.SetFont(bad.AddStandardFont("Courier"), 10);
            b.DrawText("x", 10, 10);
            b.Commit();
        }

        try
        {
            bad.Save(new MemoryStream(), new PdfSaveOptions { PdfA = new PdfAOptions() });
            return "a non-embedded font was not refused";
        }
        catch (PdfAException ex) when (ex.Violations.Count > 0)
        {
            return null;
        }
    }
}
