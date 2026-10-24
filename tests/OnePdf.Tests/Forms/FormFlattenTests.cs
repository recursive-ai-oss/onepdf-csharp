using OnePdf.Tests.Content;
using OnePdf.Tests.Writing;

namespace OnePdf.Tests.Forms;

/// <summary>Flattening: the flattened page draws what the page with its annotations drew.</summary>
public class FormFlattenTests
{
    private static PdfDocument Open(byte[] data) => PdfDocument.Open(data, FormTestSupport.Editable);

    private static void AssertSamePages(string[][] before, PdfDocument after, string what)
    {
        for (int i = 0; i < before.Length; i++)
        {
            string[] a = WithoutFlattenArtifacts(before[i], FormTestSupport.Lines(after, i));
            Assert.True(before[i].SequenceEqual(a), $"{what}: page {i} differs: first difference at line {FirstDiff(before[i], a)}: [{Line(before[i], FirstDiff(before[i], a))}] vs [{Line(a, FirstDiff(before[i], a))}]");
        }
    }

    // Flattening a tagged document wraps each baked-in appearance in an Artifact sequence; the original has none of those, so they are
    // taken out (with their matching end) when the original itself holds no artifact sequence at all.
    private static string[] WithoutFlattenArtifacts(string[] original, string[] flattened)
    {
        if (original.Any(static l => l.StartsWith("begin-mc /Artifact", StringComparison.Ordinal)))
        {
            return flattened;
        }

        var kept = new List<string>(flattened.Length);
        var open = new Stack<bool>();
        foreach (string l in flattened)
        {
            if (l.StartsWith("begin-mc ", StringComparison.Ordinal))
            {
                bool artifact = l.StartsWith("begin-mc /Artifact props=null", StringComparison.Ordinal);
                open.Push(artifact);
                if (artifact)
                {
                    continue;
                }
            }
            else if (l.StartsWith("end-mc", StringComparison.Ordinal) && open.Count > 0 && open.Pop())
            {
                continue;
            }

            kept.Add(l);
        }

        return [.. kept];
    }

    private static string Line(string[] x, int i) => i < x.Length ? x[i][..Math.Min(400, x[i].Length)] : "(end)";

    private static int FirstDiff(string[] x, string[] y)
    {
        int n = Math.Min(x.Length, y.Length);
        for (int i = 0; i < n; i++)
        {
            if (x[i] != y[i])
            {
                return i;
            }
        }

        return n;
    }

    private static string[][] Before(PdfDocument doc) => [.. Enumerable.Range(0, doc.Pages.Count).Select(i => FormTestSupport.Lines(doc, i))];

    private static int WidgetCount(PdfDocument doc) =>
        Enumerable.Range(0, doc.Pages.Count).Sum(i => doc.Pages[i].Annotations.Count(static a => a.Subtype == "Widget"));

    [Theory]
    [MemberData(nameof(FormTestSupport.FormFiles), MemberType = typeof(FormTestSupport))]
    public void FlattenedFormsDrawTheSameAsTheOriginal(string file)
    {
        byte[] input = FormTestSupport.Corpus(file);
        using PdfDocument doc = Open(input);
        string[][] before = Before(doc);
        int widgets = WidgetCount(doc);
        PdfFlattenResult r = doc.Flatten();
        Assert.Equal(widgets, r.Removed);
        Assert.True(r.FormRemoved);
        AssertSamePages(before, doc, file);
        Assert.Equal(0, WidgetCount(doc));
        Assert.Null(doc.Form);
        FormTestSupport.BothSaves(doc, input, d =>
        {
            Assert.Null(d.Form);
            Assert.Null(d.Catalog.Get("AcroForm"));
            Assert.Equal(0, WidgetCount(d));
            AssertSamePages(before, d, file + " (reopened)");
        }, file);
    }

    [Theory]
    [MemberData(nameof(FormTestSupport.FormFiles), MemberType = typeof(FormTestSupport))]
    public void FilledAndFlattenedValuesAreExtracted(string file)
    {
        byte[] input = FormTestSupport.Corpus(file);
        using PdfDocument doc = Open(input);
        var options = new PdfFillOptions { OverrideReadOnly = true, TruncateToMaxLength = true };
        var fillable = new List<string>();
        foreach (PdfFormField f in doc.Form!.TerminalFields.Where(static f => f.Kind == PdfFieldKind.Text && f.Widgets.Count > 0 && !f.IsPassword && f.Widgets.All(static w => (w.GetInt("F") & 34) == 0)).Take(3).ToList())
        {
            if ((f.MaxLength ?? 99) >= 6)
            {
                f.SetValue("Qx9Zv", options);
                fillable.Add(f.FullName);
            }
        }

        doc.Flatten();
        byte[] saved = WriterSupport.Save(doc);
        using PdfDocument d = PdfDocument.Open(saved);
        string text = string.Join("\n", d.ExtractText().Select(static p => p.Text));
        if (fillable.Count > 0)
        {
            Assert.Contains("Qx9Zv", text);
        }
    }

    [Fact]
    public void WithMarkupAndRotationTheFlattenedPageMatches()
    {
        foreach (int rotate in new[] { 0, 90, 180, 270 })
        {
            byte[] input = FormTestSupport.Synthetic(rotate);
            using PdfDocument doc = Open(input);
            doc.Form!.SetValue("name", "Ada");
            doc.Form!.SetChecked("agree", true);
            doc.Form!.SetValue("size", "S");
            string[][] before = Before(doc);
            PdfFlattenResult r = doc.Flatten(new PdfFlattenOptions { IncludeMarkup = true });
            AssertSamePages(before, doc, "rotate " + rotate);
            Assert.True(r.Drawn >= 10);
            // Only the link is left.
            Assert.Equal(["Link"], doc.Pages[0].Annotations.Select(static a => a.Subtype));
            Assert.Null(doc.Form);
            FormTestSupport.BothSaves(doc, input, d => AssertSamePages(before, d, "rotate " + rotate + " reopened"), "rotate" + rotate);
        }
    }

    [Fact]
    public void NoRotateAnnotationsStayUprightOnRotatedPages()
    {
        foreach (int rotate in new[] { 90, 180, 270 })
        {
            byte[] data = TestPdf.Build(
                "0 0 1 rg 0 0 50 50 re f",
                $"/Rotate {rotate} /Annots [6 0 R]",
                b =>
                {
                    b.Stream(7, "/Type /XObject /Subtype /Form /BBox [0 0 40 20]", "1 0 0 rg 0 0 40 20 re f"u8.ToArray());
                    b.Obj(6, "<< /Type /Annot /Subtype /Stamp /F 20 /Rect [100 100 140 120] /AP << /N 7 0 R >> >>");
                },
                objects: 8);
            using PdfDocument doc = Open(data);
            string[][] before = Before(doc);
            doc.Flatten(new PdfFlattenOptions { IncludeMarkup = true });
            AssertSamePages(before, doc, "noRotate " + rotate);
        }
    }

    [Fact]
    public void HiddenAndPrintRulesAreHonoured()
    {
        byte[] input = FormTestSupport.Synthetic();
        using PdfDocument doc = Open(input);
        // The hidden square (34) is removed but not drawn; the highlight loses its Print flag.
        string[][] viewBefore = Before(doc);
        using PdfDocument doc2 = Open(input);
        PdfFlattenResult r = doc2.Flatten(new PdfFlattenOptions { IncludeMarkup = true });
        AssertSamePages(viewBefore, doc2, "view");
        Assert.Equal(15, r.Removed);

        using PdfDocument doc3 = Open(input);
        var print = new PdfInterpretOptions { Annotations = PdfAnnotationMode.Print };
        var pl = new PdfDisplayList();
        doc3.Pages[0].Interpret(pl, print);
        string[] expected = [.. pl.ToText().Split('\n').Select(static l => l.Trim()).Where(static l => l.Length > 0 && !l.StartsWith("begin-annot", StringComparison.Ordinal) && !l.StartsWith("end-annot", StringComparison.Ordinal))];
        doc3.Flatten(new PdfFlattenOptions { IncludeMarkup = true, Visibility = PdfAnnotationMode.Print });
        Assert.Equal(expected, FormTestSupport.Lines(doc3, 0));

        Assert.Throws<ArgumentException>(() => doc3.Flatten(new PdfFlattenOptions { Visibility = PdfAnnotationMode.None }));
    }

    [Fact]
    public void SelectedFieldsOnlyAreFlattened()
    {
        byte[] input = FormTestSupport.Synthetic();
        using PdfDocument doc = Open(input);
        doc.Form!.SetValue("name", "Ada");
        string[][] before = Before(doc);
        PdfFlattenResult r = doc.Flatten(new PdfFlattenOptions { FieldNames = ["name", "size"] });
        Assert.Equal(1 + 2, r.Removed);
        Assert.Equal(2, r.FieldsRemoved);
        Assert.False(r.FormRemoved);
        Assert.Null(doc.Form!.FindField("name"));
        Assert.Null(doc.Form!.FindField("size"));
        Assert.NotNull(doc.Form!.FindField("zip"));
        Assert.Equal(7, doc.Form!.Fields.Count);
        // Every widget, flattened or not, still draws; the page keeps the same appearance overall.
        Assert.Equal(before[0].Length, FormTestSupport.Lines(doc, 0).Length);
        Assert.Equal(PdfFormValueError.UnknownField, Assert.Throws<PdfFormValueException>(() => doc.Flatten(new PdfFlattenOptions { FieldNames = ["missing"] })).Reason);
        FormTestSupport.BothSaves(doc, input, d => Assert.Equal(7, d.Form!.Fields.Count), "selected");
    }

    [Fact]
    public void MarkupOnlyKeepsTheForm()
    {
        using PdfDocument doc = Open(FormTestSupport.Synthetic());
        PdfFlattenResult r = doc.Flatten(new PdfFlattenOptions { Fields = false, IncludeMarkup = true });
        Assert.Equal(5, r.Removed); // square, highlight, free text, stamp, hidden square
        Assert.NotNull(doc.Form);
        Assert.Equal(10, doc.Pages[0].Annotations.Count(static a => a.Subtype == "Widget"));
    }

    [Fact]
    public void FlattenedAppearancesComeBackToTheSameFormXObject()
    {
        // The stored appearance stream of a widget becomes the XObject, not a copy.
        using PdfDocument doc = Open(FormTestSupport.Synthetic());
        doc.Form!.SetChecked("agree", true);
        int yes = doc.Form!.FindField("agree")!.Widgets[0].GetDictionary("AP")!.GetDictionary("N")!.GetStream("Yes")!.Reference!.Number;
        doc.Flatten();
        PdfDictionary xo = doc.Pages[0].Resources.GetDictionary("XObject")!;
        Assert.Contains(xo.Keys, k => xo.GetRaw(k) is PdfReference r && r.Number == yes);
        // The page's original content is wrapped in q ... Q.
        Assert.Equal(1 + 1 + 1 + 1, doc.Pages[0].ContentStreams.Count);
    }

    [Fact]
    public void SignedSignatureFieldsAreKept()
    {
        byte[] data = TestPdf.Build(
            string.Empty,
            "/Annots [6 0 R 7 0 R]",
            b =>
            {
                b.Obj(5, "<< /Fields [6 0 R 7 0 R] /SigFlags 3 >>");
                b.Obj(6, "<< /Type /Annot /Subtype /Widget /F 4 /FT /Sig /T (signed) /V << /Type /Sig /Filter /Adobe.PPKLite /Contents <00> /ByteRange [0 0 0 0] >> /Rect [0 0 10 10] >>");
                b.Obj(7, "<< /Type /Annot /Subtype /Widget /F 4 /FT /Tx /T (t) /V (x) /Rect [20 0 100 20] /DA (/Helv 10 Tf 0 g) >>");
            },
            objects: 8,
            catalogEntries: "/AcroForm 5 0 R");
        using PdfDocument doc = Open(data);
        PdfFlattenResult r = doc.Flatten();
        Assert.Equal(1, r.SignedKept);
        Assert.False(r.FormRemoved);
        Assert.Single(doc.Pages[0].Annotations);
        Assert.Equal("signed", doc.Form!.Fields.Single().FullName);
    }

    [Fact]
    public void FlatteningTwiceDoesNothingTheSecondTime()
    {
        using PdfDocument doc = Open(FormTestSupport.Synthetic());
        doc.Flatten(new PdfFlattenOptions { IncludeMarkup = true });
        PdfFlattenResult again = doc.Flatten(new PdfFlattenOptions { IncludeMarkup = true });
        Assert.Equal(0, again.Removed);
        Assert.Equal(0, again.Drawn);
    }
}
