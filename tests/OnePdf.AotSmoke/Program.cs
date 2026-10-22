using System;
using System.Collections.Generic;
using System.Text;
using OnePdf;
using OnePdf.Fonts;

// Exercises the library from a NativeAOT-published app; grows as the library does.
if (Library.Name != "OnePDF" || FontsPackage.Name != "OnePdf.Fonts")
{
    Console.Error.WriteLine("library identity missing");
    return 1;
}

if (OnePdf.AotSmoke.FontSmoke.Run() is { } failure)
{
    Console.Error.WriteLine("font data: " + failure);
    return 1;
}

const string Xmp =
    "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
    "<rdf:Description xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\">" +
    "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Smoke</rdf:li></rdf:Alt></dc:title>" +
    "<xmp:CreatorTool>smoke</xmp:CreatorTool></rdf:Description></rdf:RDF></x:xmpmeta>";

string pdf =
    "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Metadata 5 0 R /Outlines 6 0 R /PageLabels << /Nums [0 << /S /r >>] >> " +
    "/AcroForm << /Fields [7 0 R] >> >>\nendobj\n" +
    "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /Rotate 90 /MediaBox [0 0 200 300] >>\nendobj\n" +
    "3 0 obj\n<< /Type /Page /Parent 2 0 R /Annots [8 0 R] >>\nendobj\n" +
    "4 0 obj\n<< /Title (Smoke) /CreationDate (D:20260102030405Z) >>\nendobj\n" +
    $"5 0 obj\n<< /Type /Metadata /Subtype /XML /Length {Xmp.Length} >>\nstream\n{Xmp}\nendstream\nendobj\n" +
    "6 0 obj\n<< /Type /Outlines /First 9 0 R /Last 9 0 R /Count 1 >>\nendobj\n" +
    "7 0 obj\n<< /T (field) /FT /Tx /V (value) >>\nendobj\n" +
    "8 0 obj\n<< /Subtype /Link /Rect [0 0 10 10] /A << /S /URI /URI (https://example.com/) >> >>\nendobj\n" +
    "9 0 obj\n<< /Title (Top) /Parent 6 0 R /Dest [3 0 R /Fit] >>\nendobj\n" +
    "trailer\n<< /Root 1 0 R /Info 4 0 R >>\n";

// No xref table: opening exercises the repair scan, the parser and the page-tree walk.
using PdfDocument doc = PdfDocument.Open(Encoding.ASCII.GetBytes(pdf));
if (doc.PageCount != 1 || doc.Diagnostics.Count == 0)
{
    Console.Error.WriteLine("document did not open as expected");
    return 1;
}

// The typed layer: page attributes, labels, info, XMP (System.Xml.Linq under trimming), outline, links, form.
PdfPage page = doc.Pages[0];
if (page.Rotate != 90 || page.MediaBox.Width != 200 || page.Label != "i"
    || doc.Info.Title != "Smoke" || doc.Info.CreationDate?.Year != 2026
    || doc.Xmp?.Title != "Smoke" || doc.Xmp.CreatorTool != "smoke"
    || doc.Outline.Count != 1 || doc.Outline[0].Destination?.PageIndex != 0
    || page.Links.Count != 1 || page.Links[0].Uri != "https://example.com/"
    || doc.Form?.FindField("field")?.Value != "value"
    || doc.Permissions != PdfPermissions.All)
{
    Console.Error.WriteLine("typed layer did not read as expected");
    return 1;
}

// Fonts: resolve a Standard 14 font and a predefined-CMap CID font, decode strings (metrics, Unicode, CMaps under trimming).
string fontPdf =
    "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
    "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 10 10] >>\nendobj\n" +
    "3 0 obj\n<< /Type /Page /Parent 2 0 R >>\nendobj\n" +
    "4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n" +
    "5 0 obj\n<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /90ms-RKSJ-H /DescendantFonts [6 0 R] >>\nendobj\n" +
    "6 0 obj\n<< /Type /Font /Subtype /CIDFontType0 /BaseFont /X /CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 2 >> >>\nendobj\n" +
    "trailer\n<< /Root 1 0 R >>\n";
using PdfDocument fdoc = PdfDocument.Open(Encoding.ASCII.GetBytes(fontPdf));
fdoc.FontProvider = new NoSubstitutes();
var helv = new List<OnePdf.PdfFonts.PdfGlyph>();
fdoc.GetFont((PdfDictionary?)fdoc.GetObject(4)).Decode("AB "u8, helv);
var sjis = new List<OnePdf.PdfFonts.PdfGlyph>();
fdoc.GetFont((PdfDictionary?)fdoc.GetObject(5)).Decode([0x82, 0xA0, 0x41], sjis);
if (helv.Count != 3 || Math.Abs(helv[0].Advance - 0.667) > 1e-9 || helv[2].Unicode != " "
    || sjis.Count != 2 || sjis[0].Unicode != "\u3042" || sjis[0].CodeLength != 2 || sjis[1].Unicode != "A")
{
    Console.Error.WriteLine("fonts did not decode as expected");
    return 1;
}

Console.WriteLine("ok");
return 0;

internal sealed class NoSubstitutes : IPdfFontProvider
{
    public PdfSubstituteFont? FindFont(PdfFontRequest request) => null;
}
