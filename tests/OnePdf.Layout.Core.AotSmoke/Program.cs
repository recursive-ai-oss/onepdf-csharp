using System;
using System.IO;
using System.Linq;
using OnePdf;
using OnePdf.Hyphenation;
using OnePdf.Layout;

// Lays out a document with a font the caller registers, reads it back and checks the text; then checks that a document with no
// registered font fails with the typed error. The fonts package is not referenced, so its assembly must never be loaded.
byte[] font = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fonts", "LiberationSans-Regular.ttf"));

LayoutDocument doc = LayoutDocument.Compose(d =>
{
    d.Fonts.Add(font, "Acme");
    d.Fonts.DefaultFamily = "Acme";
    d.Page(p =>
    {
        p.Size(PageSize.A5);
        p.Content().Column(col =>
        {
            col.Spacing(6);
            col.Item().Text("Layout core smoke").FontSize(18).Bold();
            col.Item().Text("A paragraph of hyphenation-capable text that wraps over a few lines of the narrow page.").Hyphenator(Hyphenators.Auto(null, "en-US"));
            col.Item().Table(t =>
            {
                t.Columns(c => { c.Relative(); c.Relative(); });
                t.Row(r =>
                {
                    r.Cell().Text("cell one");
                    r.Cell().Text("cell two");
                });
            });
        });
    });
});
using var ms = new MemoryStream();
doc.Save(ms, new PdfDocumentWriterOptions { LeaveOpen = true });
using PdfDocument back = PdfDocument.Open(ms.ToArray());
string text = string.Concat(back.ExtractText().Select(t => t.Text));
if (back.Pages.Count != 1 || !text.Contains("Layout core smoke", StringComparison.Ordinal) || !text.Contains("cell two", StringComparison.Ordinal))
{
    Console.WriteLine("FAIL: " + text);
    return 1;
}

// A QR Code is vector rectangles: it needs no font, and a registered font adds human-readable text to a linear symbol.
LayoutDocument qrDoc = LayoutDocument.Compose(d => d.Page(p => p.Content().QrCode("https://example.com/core").ModuleSize(2)));
using var qrStream = new MemoryStream();
qrDoc.Save(qrStream, new PdfDocumentWriterOptions { LeaveOpen = true });
using PdfDocument qrBack = PdfDocument.Open(qrStream.ToArray());
if (qrBack.Pages.Count != 1 || qrBack.Pages[0].ContentStreams.Count == 0)
{
    Console.WriteLine("FAIL: QR Code page");
    return 1;
}

LayoutDocument eanDoc = LayoutDocument.Compose(d =>
{
    d.Fonts.Add(font, "Acme");
    d.Fonts.DefaultFamily = "Acme";
    d.Page(p => p.Content().Barcode(BarcodeSymbology.Ean13, "400638133393"));
});
using var eanStream = new MemoryStream();
eanDoc.Save(eanStream, new PdfDocumentWriterOptions { LeaveOpen = true });
using PdfDocument eanBack = PdfDocument.Open(eanStream.ToArray());
string eanText = new([.. eanBack.Pages[0].ExtractText().Text.Where(ch => !char.IsWhiteSpace(ch))]);
if (!eanText.Contains("4006381333931", StringComparison.Ordinal))
{
    Console.WriteLine("FAIL: EAN-13 digits");
    return 1;
}

LayoutDocument bare = LayoutDocument.Compose(d => d.Page(p => p.Content().Text("no font")));
try
{
    bare.Save(new MemoryStream());
    Console.WriteLine("FAIL: no error without a registered font");
    return 1;
}
catch (PdfFontNotRegisteredException e) when (e.Message.Contains("'Sans'", StringComparison.Ordinal))
{
}

if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "OnePdf.Fonts"))
{
    Console.WriteLine("FAIL: the fonts assembly was loaded");
    return 1;
}

Console.WriteLine("ok");
return 0;
