using System;
using System.IO;
using System.Text;
using OnePdf;

namespace OnePdf.Fuzz;

/// <summary>
/// Fuzz target for appearance generation. The first byte picks an annotation subtype or field type; the rest is text appended
/// to the annotation dictionary, so the fuzzer mutates keys and values (rectangles, <c>/DA</c>, values, option lists, point
/// arrays, border styles). The annotation sits in a page with an AcroForm that sets <c>/NeedAppearances</c> and has fonts to
/// find. The page is interpreted and the appearance is generated directly; only the library's documented exceptions may escape.
/// </summary>
internal static class AppearanceTarget
{
    private static readonly string[] Heads =
    [
        "/Subtype /Widget /FT /Tx",
        "/Subtype /Widget /FT /Tx /Ff 4096",
        "/Subtype /Widget /FT /Tx /Ff 16777216 /MaxLen 6",
        "/Subtype /Widget /FT /Tx /Ff 8192",
        "/Subtype /Widget /FT /Ch /Ff 131072",
        "/Subtype /Widget /FT /Ch",
        "/Subtype /Widget /FT /Btn",
        "/Subtype /Widget /FT /Btn /Ff 32768",
        "/Subtype /Widget /FT /Btn /Ff 65536",
        "/Subtype /Square",
        "/Subtype /Circle",
        "/Subtype /Line",
        "/Subtype /Polygon",
        "/Subtype /PolyLine",
        "/Subtype /Ink",
        "/Subtype /Highlight",
        "/Subtype /Squiggly",
        "/Subtype /Underline",
        "/Subtype /StrikeOut",
        "/Subtype /FreeText",
        "/Subtype /Text",
    ];

    private static readonly PdfLimits Limits = PdfLimits.Default with { MaxOperatorsPerPage = 100_000, MaxAppearanceText = 4096, MaxAppearanceItems = 1024 };

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1)
        {
            return;
        }

        try
        {
            using PdfDocument doc = PdfDocument.Open(Wrap(data[0], data[1..]), new PdfOpenOptions { Limits = Limits });
            PdfPage page = doc.Pages[0];
            foreach (PdfAnnotation a in page.Annotations)
            {
                _ = a.GenerateAppearance()?.Content.Length;
            }

            var list = new PdfDisplayList();
            page.Interpret(list);
            _ = list.ToText().Length;
        }
        catch (PdfException)
        {
        }
    }

    public static byte[] Wrap(byte selector, ReadOnlySpan<byte> entries)
    {
        string head = Heads[selector % Heads.Length];
        string tail = Encoding.Latin1.GetString(entries);
        string[] bodies =
        [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /NeedAppearances true /DA (/Helv 12 Tf 0 g) /DR << /Font << /Helv 6 0 R /Cour 7 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [5 0 R] >>",
            "<< >>",
            $"<< /Type /Annot /F 4 /Rect [10 10 190 60] /T (f) /V (Hello world) /DA (/Helv 0 Tf 0 g) /Opt [(a) (b)] /QuadPoints [10 60 100 60 10 40 100 40] /Vertices [10 10 50 50 90 10] /L [10 10 190 60] /InkList [[10 10 50 50]] /Contents (text) {head} {tail} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>",
        ];
        var offsets = new int[bodies.Length];
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.7\n");
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = (int)ms.Length;
            W($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }

        int xref = (int)ms.Length;
        W($"xref\n0 {bodies.Length + 1}\n0000000000 65535 f \n");
        foreach (int o in offsets)
        {
            W($"{o:D10} 00000 n \n");
        }

        W($"trailer\n<< /Size {bodies.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }
}
