using System.Text;
using OnePdf.Tests.Content;
using OnePdf.Tests.Writing;

namespace OnePdf.Tests.Forms;

/// <summary>Files and helpers shared by the form tests.</summary>
internal static partial class FormTestSupport
{
    public static readonly PdfOpenOptions Editable = new() { AllowEditing = true };

    /// <summary>
    /// A one-page form with one field of each kind. Objects: 6 text (MaxLen 8), 7 comb, 8 combo with display texts, 9 multi-select
    /// list, 10 check box, 11 radio group (kids 12 "S" and 13 "M"), 14 read-only text, 15 push button, 16 multi-line text.
    /// Markup: 30 Square, 31 Highlight, 32 FreeText, 33 Stamp without an appearance, 34 hidden Square, 35 Link. Widgets have no
    /// appearances except the check box and radio states.
    /// </summary>
    public static byte[] Synthetic(int rotate = 0, bool needAppearances = true, bool markup = true, string extraFlags = "")
    {
        string annots = "6 0 R 7 0 R 8 0 R 9 0 R 10 0 R 12 0 R 13 0 R 14 0 R 15 0 R 16 0 R" + (markup ? " 30 0 R 31 0 R 32 0 R 33 0 R 34 0 R 35 0 R" : string.Empty);
        return TestPdf.Build(
            "BT /F1 12 Tf 10 10 Td (Page text) Tj ET",
            $"/Annots [{annots}] /Rotate {rotate} /Resources << /Font << /F1 20 0 R >> >>",
            b =>
            {
                string na = needAppearances ? "/NeedAppearances true" : string.Empty;
                b.Obj(5, "<< /Fields [6 0 R 7 0 R 8 0 R 9 0 R 10 0 R 11 0 R 14 0 R 15 0 R 16 0 R] /DR << /Font << /Helv 20 0 R >> >> /DA (/Helv 0 Tf 0 g) " + na + " >>");
                const string W = "/Type /Annot /Subtype /Widget /F 4 ";
                b.Obj(6, $"<< {W} /FT /Tx /T (name) /MaxLen 8 /DA (/Helv 12 Tf 0 g) /Rect [10 170 110 190] /MK << /BC [0] >> >>");
                b.Obj(7, $"<< {W} /FT /Tx /T (zip) /Ff 16777216 /MaxLen 5 /DA (/Helv 12 Tf 0 g) /Rect [10 140 110 160] /MK << /BC [0] >> >>");
                b.Obj(8, $"<< {W} /FT /Ch /T (fruit) /Ff 131072 /Opt [(apple) [(b) (Banana)] (cherry)] /DA (/Helv 12 Tf 0 g) /Rect [10 110 110 130] /MK << /BC [0] >> >>");
                b.Obj(9, $"<< {W} /FT /Ch /T (tags) /Ff 2097152 /Opt [(a) (b) (c) (d)] /DA (/Helv 10 Tf 0 g) /Rect [120 100 190 190] /MK << /BC [0] >> >>");
                b.Obj(10, $"<< {W} /FT /Btn /T (agree) /AS /Off /AP << /N << /Yes 21 0 R /Off 22 0 R >> >> /Rect [10 80 24 94] >>");
                b.Obj(11, "<< /FT /Btn /Ff 49152 /T (size) /Kids [12 0 R 13 0 R] >>");
                b.Obj(12, $"<< {W} /Parent 11 0 R /AS /Off /AP << /N << /S 21 0 R /Off 22 0 R >> >> /Rect [30 80 44 94] >>");
                b.Obj(13, $"<< {W} /Parent 11 0 R /AS /Off /AP << /N << /M 21 0 R /Off 22 0 R >> >> /Rect [50 80 64 94] >>");
                b.Obj(14, $"<< {W} /FT /Tx /T (locked) /Ff 1 /V (fixed) /DA (/Helv 12 Tf 0 g) /Rect [10 50 110 70] /MK << /BC [0] >> >>");
                b.Obj(15, $"<< {W} /FT /Btn /T (go) /Ff 65536 /Rect [120 50 190 70] /MK << /CA (Go) /BG [0.8] /BC [0] >> /DA (/Helv 10 Tf 0 g) >>");
                b.Obj(16, $"<< {W} /FT /Tx /T (notes) /Ff 4096 /DA (/Helv 10 Tf 0 g) /Rect [10 10 190 45] /MK << /BC [0] >> {extraFlags} >>");
                b.Obj(20, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
                b.Stream(21, "/Type /XObject /Subtype /Form /BBox [0 0 14 14]", Encoding.ASCII.GetBytes("0 0 m 14 14 l S 0 14 m 14 0 l S"));
                b.Stream(22, "/Type /XObject /Subtype /Form /BBox [0 0 14 14]", []);
                b.Obj(30, "<< /Type /Annot /Subtype /Square /F 4 /Rect [100 60 140 80] /C [1 0 0] /IC [1 1 0] >>");
                b.Obj(31, "<< /Type /Annot /Subtype /Highlight /F 4 /Rect [10 2 60 12] /QuadPoints [10 12 60 12 10 2 60 2] /C [1 1 0] >>");
                b.Obj(32, "<< /Type /Annot /Subtype /FreeText /F 4 /Rect [100 20 190 45] /Contents (Free note) /DA (/Helv 9 Tf 0 g) /C [0.9 0.9 0.9] >>");
                b.Obj(33, "<< /Type /Annot /Subtype /Stamp /F 4 /Rect [150 150 190 170] /Name /Draft >>");
                b.Obj(34, "<< /Type /Annot /Subtype /Square /F 2 /Rect [100 100 120 120] /C [0 0 1] >>");
                b.Obj(35, "<< /Type /Annot /Subtype /Link /F 4 /Rect [0 0 20 20] /Border [0 0 1] >>");
            },
            objects: 40,
            catalogEntries: "/AcroForm 5 0 R");
    }

    public static byte[] Corpus(string relative) => File.ReadAllBytes(Path.Combine(WriterSupport.Root(), "testdata", relative));

    public static IEnumerable<object[]> FormFiles()
    {
        yield return ["generated/pdf/lo-form-fields.pdf"];
        yield return ["generated/pdf/raw-acroform.pdf"];
        string dir = Path.Combine(WriterSupport.Root(), "testdata", "realworld", "pdf");
        foreach (string f in Directory.GetFiles(dir, "form-*.pdf").Order(StringComparer.Ordinal))
        {
            yield return ["realworld/pdf/" + Path.GetFileName(f)];
        }
    }

    /// <summary>The text drawn inside each annotation, by annotation object number, over all pages.</summary>
    public static Dictionary<int, string> AnnotationText(PdfDocument doc, PdfInterpretOptions? options = null)
    {
        var map = new Dictionary<int, string>();
        for (int i = 0; i < doc.Pages.Count; i++)
        {
            var c = new AppearanceGenerationTests.Collector();
            doc.Pages[i].Interpret(c, options);
            foreach (AppearanceGenerationTests.AnnotRecord r in c.Records)
            {
                map[r.Obj] = r.Text.ToString();
            }
        }

        return map;
    }

    /// <summary>The display list of a page as comparable lines: annotation brackets dropped, indentation removed.</summary>
    public static string[] Lines(PdfDocument doc, int page)
    {
        var list = new PdfDisplayList();
        doc.Pages[page].Interpret(list);
        return [.. list.ToText().Split('\n').Select(static l => ObjectNumbers().Replace(l.Trim(), "N 0 R")).Where(static l => l.Length > 0 && !l.StartsWith("begin-annot", StringComparison.Ordinal) && !l.StartsWith("end-annot", StringComparison.Ordinal))];
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\b\d+ 0 R\b")]
    private static partial System.Text.RegularExpressions.Regex ObjectNumbers();

    public static PdfDocument Reopen(byte[] data) => PdfDocument.Open(data);

    /// <summary>Saves in both ways, reopens and runs the checks on each.</summary>
    public static void BothSaves(PdfDocument doc, byte[] input, Action<PdfDocument> check, string what)
    {
        byte[] full = WriterSupport.Save(doc, new PdfSaveOptions { XrefFormat = PdfXrefFormat.Stream, CompressStreams = true });
        using (PdfDocument d = PdfDocument.Open(full))
        {
            check(d);
        }

        WriterSupport.AssertNoWorse(input, full, null, what + " (full)");
        using UglyToad.PdfPig.PdfDocument pig = UglyToad.PdfPig.PdfDocument.Open(full);
        Assert.True(pig.NumberOfPages >= 1);

        byte[] inc;
        try
        {
            inc = WriterSupport.SaveIncremental(doc);
        }
        catch (PdfFormatException)
        {
            // A file that was rebuilt by scanning has no section to chain to (docs/writing.md).
            return;
        }

        using (PdfDocument d = PdfDocument.Open(inc))
        {
            check(d);
        }

        WriterSupport.AssertNoWorse(input, inc, null, what + " (incremental)");
        using UglyToad.PdfPig.PdfDocument pig2 = UglyToad.PdfPig.PdfDocument.Open(inc);
        Assert.True(pig2.NumberOfPages >= 1);
    }
}
