using System;
using System.Collections.Generic;
using System.Text;
using OnePdf;
using OnePdf.Fonts;
using OnePdf.Text;

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
var helv = new List<OnePdf.PdfFonts.FontGlyph>();
fdoc.GetFont((PdfDictionary?)fdoc.GetObject(4)).Decode("AB "u8, helv);
var sjis = new List<OnePdf.PdfFonts.FontGlyph>();
fdoc.GetFont((PdfDictionary?)fdoc.GetObject(5)).Decode([0x82, 0xA0, 0x41], sjis);
if (helv.Count != 3 || Math.Abs(helv[0].Advance - 0.667) > 1e-9 || helv[2].Unicode != " "
    || sjis.Count != 2 || sjis[0].Unicode != "\u3042" || sjis[0].CodeLength != 2 || sjis[1].Unicode != "A")
{
    Console.Error.WriteLine("fonts did not decode as expected");
    return 1;
}

// Interpretation: a page with content, a form and text goes into a display list and out as text.
string pdf2 =
    "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
    "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
    "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /XObject << /F 5 0 R >> /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Courier >> >> >> >>\nendobj\n" +
    "4 0 obj\n<< /Length 70 >>\nstream\nq 1 0 0 rg 10 10 30 30 re f /F Do Q BT /F1 12 Tf 10 50 Td (Hi) Tj ET\nendstream\nendobj\n" +
    "5 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 50 50] /Length 17 >>\nstream\n0 0 20 20 re f\nendstream\nendobj\n" +
    "trailer\n<< /Root 1 0 R >>\n";
using PdfDocument doc2 = PdfDocument.Open(Encoding.ASCII.GetBytes(pdf2));
var list = new PdfDisplayList();
doc2.Pages[0].Interpret(list);
string text = list.ToText();

// Images: colour-space resolution, Indexed expansion and a tint function through the finished-image path.
string pdf3 =
    "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
    "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
    "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] >>\nendobj\n" +
    "4 0 obj\n<< /Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8 /ColorSpace [/Separation /S /DeviceCMYK 5 0 R] /Length 2 >>\nstream\n\u0000\u00ff\nendstream\nendobj\n" +
    "5 0 obj\n<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 1 1 0] /N 1 >>\nendobj\n" +
    "trailer\n<< /Root 1 0 R >>\n";
using PdfDocument doc3 = PdfDocument.Open(Encoding.Latin1.GetBytes(pdf3));
PdfImageData? img = PdfImageData.FromXObject(doc3, (PdfStream)doc3.GetObject(4)!);
if (img is not { IsDecoded: true } || img.Samples.Length != 8 || img.Samples.Span[5] != 255 || img.ColorSpace!.Kind != PdfColorSpaceKind.DeviceCmyk)
{
    Console.Error.WriteLine("image conversion failed");
    return 1;
}

if (!text.StartsWith("onepdf-display-list 5", StringComparison.Ordinal) || !text.Contains("fill nonzero M 10 10", StringComparison.Ordinal)
    || !text.Contains("codes=4869", StringComparison.Ordinal) || !text.Contains("clip nonzero", StringComparison.Ordinal))
{
    Console.Error.WriteLine("interpretation did not produce the expected display list");
    return 1;
}

// Text extraction: fonts decoded, glyphs positioned, words in reading order.
if (!doc2.Pages[0].ExtractText().Text.Contains("Hi", StringComparison.Ordinal) || !doc2.ExtractText()[0].Text.Contains("Hi", StringComparison.Ordinal))
{
    Console.Error.WriteLine("text extraction did not find the page text");
    return 1;
}

// Shadings and patterns: an axial shading, a Coons patch mesh and a tiling pattern, evaluated and recorded.
{
    var patch = new System.Text.StringBuilder();
    patch.Append('\0');
    int[][] order = { new[] { 0, 0 }, new[] { 0, 1 }, new[] { 0, 2 }, new[] { 0, 3 }, new[] { 1, 3 }, new[] { 2, 3 }, new[] { 3, 3 }, new[] { 3, 2 }, new[] { 3, 1 }, new[] { 3, 0 }, new[] { 2, 0 }, new[] { 1, 0 } };
    foreach (int[] ij in order)
    {
        patch.Append((char)(85 * ij[0])).Append((char)(85 * ij[1]));
    }

    patch.Append('\0').Append((char)85).Append((char)255).Append((char)170);
    string fn = "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >>";
    string pdf4 =
        "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
        "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
        "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /Shading << /A 5 0 R /M 6 0 R >> /Pattern << /P 7 0 R >> >> >>\nendobj\n" +
        "4 0 obj\n<< /Length 45 >>\nstream\n/A sh /M sh /Pattern cs /P scn 0 0 50 50 re f\nendstream\nendobj\n" +
        "5 0 obj\n<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 100 0] /Extend [true true] " + fn + " >>\nendobj\n" +
        "6 0 obj\n<< /ShadingType 6 /ColorSpace /DeviceGray /BitsPerCoordinate 8 /BitsPerComponent 8 /BitsPerFlag 8 /Decode [0 100 0 100 0 1] /Length " + patch.Length + " >>\nstream\n" + patch + "\nendstream\nendobj\n" +
        "7 0 obj\n<< /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 /Resources << >> /Length 21 >>\nstream\n1 0 0 rg 0 0 5 5 re f\nendstream\nendobj\n" +
        "trailer\n<< /Root 1 0 R >>\n";
    using PdfDocument doc4 = PdfDocument.Open(Encoding.Latin1.GetBytes(pdf4));
    var list4 = new PdfDisplayList();
    doc4.Pages[0].Interpret(list4);
    string t4 = list4.ToText();
    if (!t4.Contains("shading type=2 cs=rgb fn=1", StringComparison.Ordinal) || !t4.Contains("at-mid=(0.5 0 0.5)", StringComparison.Ordinal)
        || !t4.Contains("shading type=6 cs=gray fn=0 bbox=none bg=none aa=0 vertices=81 triangles=128 patches=1 div=8", StringComparison.Ordinal)
        || !t4.Contains("pattern-def #1 /P tiling", StringComparison.Ordinal) || !t4.Contains("fill=rgb(1 0 0)", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("shadings and patterns did not produce the expected display list: " + t4);
        return 1;
    }
}

// Text extraction: two columns of glyphs (content order interleaved), an RTL word, layout, bidi and the JSON writer.
{
    var gl = new System.Collections.Generic.List<PdfTextGlyph>();
    void Put(string t, double x, double y) => gl.Add(new PdfTextGlyph { Text = t, Matrix = new PdfTextMatrix(10, 0, 0, 10, x, y), Advance = 0.5 });
    string[] left = { "alpha beta gamma delta epsilon", "zeta eta theta iota kappa lambda", "mu nu xi omicron pi rho sigma" };
    string[] right = { "one two three four five six", "seven eight nine ten eleven", "twelve thirteen fourteen fifteen" };
    for (int i = 0; i < 3; i++)
    {
        for (int k = 0; k < left[i].Length; k++) { if (left[i][k] != ' ') { Put(left[i][k].ToString(), 50 + 5 * k, 700 - 14 * i); } }
        for (int k = 0; k < right[i].Length; k++) { if (right[i][k] != ' ') { Put(right[i][k].ToString(), 330 + 5 * k, 700 - 14 * i); } }
    }

    string he = "\u05DD\u05D5\u05DC\u05E9"; // visual order of a Hebrew word
    for (int k = 0; k < he.Length; k++) { Put(he[k].ToString(), 50 + 5 * k, 600); }
    PdfPageText pt = PdfTextExtractor.Analyze(new PdfPageGlyphs(0, 612, 792, gl));
    // The Hebrew word sits under the left column, so it belongs to that column.
    string want = string.Join("\n", left) + "\n\n\u05E9\u05DC\u05D5\u05DD\n\n" + string.Join("\n", right);
    string json = PdfExtractionJson.ToJson(new[] { pt });
    if (pt.Text != want || pt.GetText(PdfTextMode.Layout).Length == 0 || !json.StartsWith("{\"format\":\"onepdf.extraction\"", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("text extraction did not produce the expected text: " + pt.Text);
        return 1;
    }
}

// Appearance generation: a text field and a highlight with no stored appearance are synthesised and drawn.
string pdf5 =
    "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>\nendobj\n" +
    "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
    "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Annots [4 0 R 5 0 R] >>\nendobj\n" +
    "4 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Tx /T (a) /V (Hello) /DA (/Helv 12 Tf 0 g) /Rect [10 10 150 40] /MK << /BC [0] >> >>\nendobj\n" +
    "5 0 obj\n<< /Type /Annot /Subtype /Highlight /Rect [10 60 100 80] /QuadPoints [10 80 100 80 10 60 100 60] /C [1 1 0] >>\nendobj\n" +
    "trailer\n<< /Root 1 0 R >>\n";
using PdfDocument doc5 = PdfDocument.Open(Encoding.ASCII.GetBytes(pdf5));
var list5 = new PdfDisplayList();
doc5.Pages[0].Interpret(list5);
PdfGeneratedAppearance? generated = doc5.Pages[0].Annotations[0].GenerateAppearance();
if (generated is null || !Encoding.Latin1.GetString(generated.Content.Span).Contains("(Hello) Tj", StringComparison.Ordinal)
    || list5.ToText().Split("begin-annot").Length != 3)
{
    Console.Error.WriteLine("appearance generation did not produce the expected drawing");
    return 1;
}

// Writing: edit, save in full (both forms, new AES-256), save incrementally, reopen.
using PdfDocument editable = PdfDocument.Open(Encoding.ASCII.GetBytes(pdf5), new PdfOpenOptions { AllowEditing = true });
editable.SetInfo("Title", "Smoke é");
editable.Catalog.SetEntry("Extra", editable.AddObject(new PdfArray()));
foreach (PdfSaveOptions so in new[]
{
    new PdfSaveOptions { XrefFormat = PdfXrefFormat.Table },
    new PdfSaveOptions { XrefFormat = PdfXrefFormat.Stream, CompressStreams = true },
    new PdfSaveOptions { Encryption = PdfEncryptionAction.Set, NewEncryption = new PdfEncryptionOptions { UserPassword = "u" } },
})
{
    using var saved = new System.IO.MemoryStream();
    editable.Save(saved, so);
    using PdfDocument back = PdfDocument.Open(saved.ToArray(), new PdfOpenOptions { Password = "u" });
    if (back.Info.Title != "Smoke é" || back.PageCount != 1)
    {
        Console.Error.WriteLine("a saved document did not read back");
        return 1;
    }
}

// The source above was repaired on open, so the incremental update goes on top of a file the writer made.
using var clean = new System.IO.MemoryStream();
editable.Save(clean);
using PdfDocument second = PdfDocument.Open(clean.ToArray(), new PdfOpenOptions { AllowEditing = true });
second.SetInfo("Subject", "again");
using var update = new System.IO.MemoryStream();
second.SaveIncremental(update);
using (PdfDocument updated = PdfDocument.Open(update.ToArray()))
{
    if (updated.Revisions.Count != 2 || updated.Info.Title != "Smoke é")
    {
        Console.Error.WriteLine("an incremental save did not read back");
        return 1;
    }
}

Console.WriteLine("ok");
return 0;

internal sealed class NoSubstitutes : IPdfFontProvider
{
    public PdfSubstituteFont? FindFont(PdfFontRequest request) => null;
}
