using System;
using System.IO;
using OnePdf;
using OnePdf.Tests.Creation;

namespace OnePdf.AotSmoke;

/// <summary>Writes a tagged file with the streaming writer, saves it as PDF/UA-1, PDF/UA-2 and PDF/A-2a, reads both back and checks them; a figure without alternate text is refused.</summary>
internal static class PdfUASmoke
{
    public static string? Run()
    {
        byte[] good = Build(figureAlt: true);
        foreach (bool both in new[] { false, true })
        {
            using PdfDocument doc = PdfDocument.Open(good, new PdfOpenOptions { AllowEditing = true });
            using var ms = new MemoryStream();
            doc.Save(ms, new PdfSaveOptions
            {
                DocumentId = new byte[16],
                PdfUA = new PdfUAOptions(),
                PdfA = both ? new PdfAOptions { Conformance = PdfAConformance.A } : null,
            });
            using PdfDocument back = PdfDocument.Open(ms.ToArray());
            if (back.CheckPdfUA().Count != 0 || back.Xmp?.PdfUAPart != 1 || (both && (back.CheckPdfA(new PdfAOptions { Conformance = PdfAConformance.A }).Count != 0 || back.Xmp.PdfAConformance != "A")))
            {
                return "PDF/UA output did not read back as expected";
            }
        }

        // Part 2: PDF 2.0, the namespace on every element, the revision in the XMP, and the combination with PDF/A refused.
        using (PdfDocument two = PdfDocument.Open(good, new PdfOpenOptions { AllowEditing = true }))
        {
            using var ms = new MemoryStream();
            two.Save(ms, new PdfSaveOptions { DocumentId = new byte[16], PdfUA = new PdfUAOptions { Part = PdfUAPart.Part2 } });
            using PdfDocument back = PdfDocument.Open(ms.ToArray());
            if (back.Version != "2.0" || back.CheckPdfUA(PdfUAPart.Part2).Count != 0 || back.Xmp?.PdfUAPart != 2 || back.Xmp.PdfUARevision != 2024)
            {
                return "PDF/UA-2 output did not read back as expected";
            }

            try
            {
                two.Save(new MemoryStream(), new PdfSaveOptions { PdfUA = new PdfUAOptions { Part = PdfUAPart.Part2 }, PdfA = new PdfAOptions { Conformance = PdfAConformance.A } });
                return "PDF/A with PDF/UA-2 was not refused";
            }
            catch (PdfAException)
            {
            }
        }

        using PdfDocument bad = PdfDocument.Open(Build(figureAlt: false), new PdfOpenOptions { AllowEditing = true });
        try
        {
            bad.Save(new MemoryStream(), new PdfSaveOptions { PdfUA = new PdfUAOptions() });
            return "a figure without alternate text was not refused";
        }
        catch (PdfUAException ex) when (ex.Violations.Count > 0 && bad.CheckPdfUA().Count > 0)
        {
            return null;
        }
    }

    private static byte[] Build(bool figureAlt)
    {
        using var ms = new MemoryStream();
        using (var w = new PdfDocumentWriter(ms, new PdfDocumentWriterOptions { DocumentId = new byte[16], LeaveOpen = true, Language = "en" }))
        {
            w.SetInfo("Title", "Smoke");
            PdfCreatedFont font = w.AddFont(SyntheticFont.Build());
            PdfCreatedImage image = w.AddRawImage(2, 2, PdfImageColor.Rgb, new byte[12]);
            PdfStructureElement root = w.BeginStructureElement("Document");
            PdfStreamPageBuilder p = w.BeginPage(200, 200);
            p.SetFont(font, 12);
            PdfStructureElement h = w.BeginStructureElement("H1");
            p.BeginTaggedContent(h);
            p.DrawText("ABCD", 10, 170);
            p.EndMarkedContent();
            w.EndStructureElement();
            PdfStructureElement fig = w.BeginStructureElement("Figure", figureAlt ? new PdfStructureElementOptions { AlternateText = "A square" } : null);
            p.BeginTaggedContent(fig);
            p.DrawImage(image, 10, 100, 40, 40);
            p.EndMarkedContent();
            w.EndStructureElement();
            p.Commit();
            w.EndStructureElement(root);
            w.Finish();
        }

        return ms.ToArray();
    }
}
