using System;
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
if (!text.StartsWith("onepdf-display-list 1", StringComparison.Ordinal) || !text.Contains("fill nonzero M 10 10", StringComparison.Ordinal)
    || !text.Contains("codes=4869", StringComparison.Ordinal) || !text.Contains("clip nonzero", StringComparison.Ordinal))
{
    Console.Error.WriteLine("interpretation did not produce the expected display list");
    return 1;
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

Console.WriteLine("ok");
return 0;
