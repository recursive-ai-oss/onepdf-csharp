using System;
using System.Text;
using OnePdf;
using OnePdf.Fonts;

// Exercises the library from a NativeAOT-published app; grows as the library does.
if (Library.Name != "OnePDF" || FontsPackage.Name != "OnePdf.Fonts")
{
    Console.Error.WriteLine("library identity missing");
    return 1;
}

const string Pdf =
    "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
    "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
    "3 0 obj\n<< /Type /Page /Parent 2 0 R >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n";

// No xref table: opening exercises the repair scan, the parser and the page-tree walk.
using PdfDocument doc = PdfDocument.Open(Encoding.ASCII.GetBytes(Pdf));
if (doc.PageCount != 1 || doc.Diagnostics.Count == 0)
{
    Console.Error.WriteLine("document did not open as expected");
    return 1;
}

Console.WriteLine("ok");
return 0;
