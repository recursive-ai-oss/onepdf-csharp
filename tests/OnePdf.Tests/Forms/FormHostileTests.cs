using System.Diagnostics;
using System.Text;
using OnePdf.Tests.Content;

namespace OnePdf.Tests.Forms;

/// <summary>Hostile forms: cycles, very many fields, huge values, malformed options. Each is bounded.</summary>
public class FormHostileTests
{
    private static PdfDocument Open(byte[] data, PdfLimits? limits = null) =>
        PdfDocument.Open(data, new PdfOpenOptions { AllowEditing = true, Limits = limits ?? new PdfLimits() });

    [Fact]
    public void CyclicKidsAreFilledAndFlattenedOnce()
    {
        byte[] data = TestPdf.Build(
            string.Empty,
            "/Annots [6 0 R]",
            b =>
            {
                b.Obj(5, "<< /Fields [7 0 R] /DA (/Helv 12 Tf 0 g) >>");
                b.Obj(7, "<< /T (outer) /FT /Tx /Kids [8 0 R] >>");
                b.Obj(8, "<< /T (inner) /Parent 7 0 R /Kids [7 0 R 6 0 R] >>");
                b.Obj(6, "<< /Type /Annot /Subtype /Widget /F 4 /Parent 8 0 R /T (leaf) /Rect [10 10 100 30] >>");
            },
            objects: 9,
            catalogEntries: "/AcroForm 5 0 R");
        using PdfDocument doc = Open(data);
        doc.Form!.SetValue("outer.inner.leaf", "ok");
        Assert.Equal("ok", doc.Form!.FindField("outer.inner.leaf")!.Value);
        PdfFlattenResult r = doc.Flatten();
        Assert.Equal(1, r.Removed);
        Assert.Null(doc.Form);
    }

    [Fact]
    public void ADeepFieldTreeStopsAtTheNestingLimit()
    {
        const int Depth = 400;
        byte[] data = TestPdf.Build(
            string.Empty,
            "/Annots [6 0 R]",
            b =>
            {
                b.Obj(5, "<< /Fields [10 0 R] >>");
                for (int i = 0; i < Depth; i++)
                {
                    int me = 10 + i;
                    b.Obj(me, $"<< /T (n{i}) /FT /Tx /Kids [{(i + 1 < Depth ? (me + 1) + " 0 R" : "6 0 R")}] {(i > 0 ? "/Parent " + (me - 1) + " 0 R" : string.Empty)} >>");
                }

                b.Obj(6, "<< /Type /Annot /Subtype /Widget /F 4 /Rect [10 10 100 30] >>");
            },
            objects: 10 + Depth,
            catalogEntries: "/AcroForm 5 0 R");
        using PdfDocument doc = Open(data);
        Assert.NotNull(doc.Form);
        Assert.True(doc.Form!.AllFields.Count < Depth);
        PdfFlattenResult r = doc.Flatten();
        Assert.True(r.Removed >= 0);
    }

    private static byte[] ManyFields(int count)
    {
        var annots = new StringBuilder();
        var fields = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            annots.Append(10 + i).Append(" 0 R ");
        }

        fields.Append(annots);
        return TestPdf.Build(
            string.Empty,
            $"/Annots [{annots}]",
            b =>
            {
                b.Obj(5, $"<< /Fields [{fields}] /DA (/Helv 9 Tf 0 g) >>");
                for (int i = 0; i < count; i++)
                {
                    b.Obj(10 + i, $"<< /Type /Annot /Subtype /Widget /F 4 /FT /Tx /T (f{i}) /Rect [{10 + (i % 20 * 9)} {10 + (i / 20 % 150)} {18 + (i % 20 * 9)} {18 + (i / 20 % 150)}] >>");
                }
            },
            objects: 10 + count,
            catalogEntries: "/AcroForm 5 0 R");
    }

    [Fact]
    public void AHundredThousandFieldsFillAndFlattenInBoundedTime()
    {
        const int N = 100_000;
        byte[] data = ManyFields(N);
        var sw = Stopwatch.StartNew();
        using PdfDocument doc = Open(data);
        Assert.Equal(N, doc.Form!.AllFields.Count);
        for (int i = 0; i < N; i += 10)
        {
            doc.Form!.SetValue("f" + i, "v" + i);
        }

        TimeSpan fill = sw.Elapsed;
        PdfFlattenResult r = doc.Flatten();
        Assert.Equal(N, r.Removed);
        Assert.Null(doc.Form);
        TimeSpan total = sw.Elapsed;
        Assert.True(total < TimeSpan.FromSeconds(120), $"fill {fill}, total {total}");
        using var ms = new MemoryStream();
        doc.Save(ms);
        using PdfDocument d = PdfDocument.Open(ms.ToArray());
        Assert.Empty(d.Pages[0].Annotations);
    }

    [Fact]
    public void HugeValuesAreStoredButOnlyBoundedTextIsDrawn()
    {
        byte[] data = ManyFields(1);
        using PdfDocument doc = Open(data);
        string huge = new('x', 5_000_000);
        doc.Form!.SetValue("f0", huge);
        Assert.Equal(huge.Length, doc.Form!.FindField("f0")!.Value!.Length);
        PdfStream ap = doc.Form!.FindField("f0")!.Widgets[0].GetDictionary("AP")!.GetStream("N")!;
        Assert.True(ap.GetDecodedBytes().Length < 200_000);

        using PdfDocument limited = Open(data, new PdfLimits { MaxMetadataBytes = 1000 });
        Assert.Throws<PdfLimitException>(() => limited.Form!.SetValue("f0", new string('y', 5000)));
        Assert.Null(limited.Form!.FindField("f0")!.Value);
        Assert.Throws<PdfLimitException>(() => limited.Form!.SetValues("f0", [new string('y', 5000)]));
    }

    [Fact]
    public void MalformedOptionsNeverCrash()
    {
        string[] opts =
        [
            "[1 2 [3] [(a)] << >> null (x) [(e) (d) (extra)] [(only)]]",
            "(not an array)",
            "[[[[[[[[[[(deep)]]]]]]]]]]",
            "[" + string.Concat(Enumerable.Repeat("(o) ", 100_000)) + "]",
            "[]",
        ];
        foreach (string opt in opts)
        {
            byte[] data = TestPdf.Build(
                string.Empty,
                "/Annots [6 0 R 7 0 R]",
                b =>
                {
                    b.Obj(5, "<< /Fields [6 0 R 7 0 R] /DA (/Helv 12 Tf 0 g) >>");
                    b.Obj(6, $"<< /Type /Annot /Subtype /Widget /F 4 /FT /Ch /Ff 131072 /T (c) /Opt {opt} /Rect [10 10 100 30] >>");
                    b.Obj(7, $"<< /Type /Annot /Subtype /Widget /F 4 /FT /Btn /Ff 32768 /T (r) /Opt {opt} /AP << /N << /A 8 0 R /Off 8 0 R >> >> /AS /Off /Rect [10 40 30 60] >>");
                    b.Stream(8, "/Type /XObject /Subtype /Form /BBox [0 0 10 10]", []);
                },
                objects: 9,
                catalogEntries: "/AcroForm 5 0 R");
            using PdfDocument doc = Open(data);
            foreach (string v in new[] { "x", "o", "e", "deep", "a" })
            {
                try
                {
                    doc.Form!.SetValue("c", v);
                }
                catch (PdfFormValueException)
                {
                }

                try
                {
                    doc.Form!.SetValue("r", v);
                }
                catch (PdfFormValueException)
                {
                }
            }

            doc.Form!.SetValue("r", "A");
            doc.Form!.ResetAll();
            doc.Flatten();
        }
    }

    [Fact]
    public void ManyValuesForAListBoxAreBoundedByTheObjectLimit()
    {
        byte[] data = ManyFields(1);
        using PdfDocument doc = Open(data, new PdfLimits { MaxObjects = 1000 });
        PdfFormField f = doc.Form!.FindField("f0")!;
        // A text field refuses lists; the limit check comes first for oversized ones.
        Assert.Throws<PdfLimitException>(() => f.SetValues([.. Enumerable.Repeat("a", 2000)]));
    }
}
