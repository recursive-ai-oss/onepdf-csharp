using System;
using System.IO;
using OnePdf;
using OnePdf.Tests.Creation;

namespace OnePdf.AotSmoke;

/// <summary>Creates a page with text in an embedded (subset) font and a standard font, an image and a stamp, saves it and reads the text back.</summary>
internal static class CreationSmoke
{
    public static string? Run()
    {
        using PdfDocument doc = PdfDocument.CreateEmpty();
        PdfPage page = doc.InsertBlankPage(0, 300, 200);
        PdfCreatedFont embedded = doc.AddFont(SyntheticFont.Build());
        PdfCreatedFont standard = doc.AddStandardFont("Helvetica");
        PdfCreatedImage image = doc.AddRawImage(2, 2, PdfImageColor.Gray, [0, 85, 170, 255]);
        using (PdfPageContentBuilder b = page.BeginContent())
        {
            b.SetFont(embedded, 14);
            b.DrawText("ABCD", 10, 150);
            b.SetFont(standard, 12);
            b.DrawTextBox("Plain text wraps in a box", 10, 60, 80, 60);
            b.SetFillOpacity(0.5);
            b.DrawImage(image, 150, 100, 40, 40);
            b.Commit();
        }

        doc.AddTextStamp("STAMP", standard, 20, new PdfStampOptions { Opacity = 0.3 });
        using var ms = new MemoryStream();
        doc.Save(ms, new PdfSaveOptions { DocumentId = new byte[16], CompressStreams = true, XrefFormat = PdfXrefFormat.Stream });
        using PdfDocument back = PdfDocument.Open(ms.ToArray());
        string text = back.ExtractText()[0].Text;
        if (!text.Contains("ABCD", StringComparison.Ordinal) || !text.Contains("STAMP", StringComparison.Ordinal) || !text.Contains("Plain", StringComparison.Ordinal))
        {
            return "created text did not extract: " + text;
        }

        return null;
    }
}
