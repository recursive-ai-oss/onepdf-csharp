using System.Text;
using OnePdf.Tests.Content;
using OnePdf.Tests.Writing;

namespace OnePdf.Tests.Forms;

/// <summary>Renaming fields and changing how they look: nesting, inheritance, refusals, and what a saved file shows.</summary>
public class FormEditTests
{
    private static readonly PdfInterpretOptions StoredOnly = new() { GenerateAppearances = false };

    private static PdfDocument Open(byte[] data) => PdfDocument.Open(data, FormTestSupport.Editable);

    // 5 AcroForm (DA /Helv 11 Tf 0 g). 6 "name" merged text widget (no own DA). 11 "person" parent (DA 10 Tf blue, FT Tx) with
    // kids 12 "first" and 13 "last"; 14 "solo" merged text without DA, inherits the AcroForm's. 15 "grp" parent without DA over "a"
    // (16) and "b" (17). 18 "multi" with two widget kids (19, 20) and the second has its own DA and MK. 21 check box "agree"
    // with authored states. 22 push button "go".
    private static byte[] Sample() => TestPdf.Build(
        "BT /F1 12 Tf 10 10 Td (Page text) Tj ET",
        "/Annots [6 0 R 12 0 R 13 0 R 14 0 R 16 0 R 17 0 R 19 0 R 20 0 R 21 0 R 22 0 R] /Resources << /Font << /F1 30 0 R >> >>",
        b =>
        {
            b.Obj(5, "<< /Fields [6 0 R 11 0 R 14 0 R 15 0 R 18 0 R 21 0 R 22 0 R] /DR << /Font << /Helv 30 0 R >> >> /DA (/Helv 11 Tf 0 g) >>");
            const string W = "/Type /Annot /Subtype /Widget /F 4 ";
            b.Obj(6, $"<< {W} /FT /Tx /T (name) /V (Ada) /Rect [10 170 110 190] /MK << /BC [0] >> >>");
            b.Obj(11, "<< /T (person) /FT /Tx /DA (/Helv 10 Tf 0 0 1 rg) /Kids [12 0 R 13 0 R] >>");
            b.Obj(12, $"<< {W} /Parent 11 0 R /T (first) /V (Grace) /Rect [10 140 110 160] /MK << /BC [0] >> >>");
            b.Obj(13, $"<< {W} /Parent 11 0 R /T (last) /V (Hopper) /Rect [10 110 110 130] /MK << /BC [0] >> >>");
            b.Obj(14, $"<< {W} /FT /Tx /T (solo) /V (Solo) /Rect [10 80 110 100] >>");
            b.Obj(15, "<< /T (grp) /FT /Tx /Kids [16 0 R 17 0 R] >>");
            b.Obj(16, $"<< {W} /Parent 15 0 R /T (a) /V (AAA) /Rect [120 170 190 190] >>");
            b.Obj(17, $"<< {W} /Parent 15 0 R /T (b) /V (BBB) /Rect [120 140 190 160] >>");
            b.Obj(18, "<< /T (multi) /FT /Tx /V (Many) /Kids [19 0 R 20 0 R] >>");
            b.Obj(19, $"<< {W} /Parent 18 0 R /Rect [120 110 190 130] >>");
            b.Obj(20, $"<< {W} /Parent 18 0 R /Rect [120 80 190 100] /DA (/Helv 9 Tf 1 0 0 rg) /MK << /BG [0.5] >> >>");
            b.Obj(21, $"<< {W} /FT /Btn /T (agree) /V /Yes /AS /Yes /AP << /N << /Yes 31 0 R /Off 32 0 R >> >> /Rect [10 50 24 64] /MK << /BC [0] /CA (4) >> >>");
            b.Obj(22, $"<< {W} /FT /Btn /Ff 65536 /T (go) /Rect [120 50 190 70] /MK << /CA (Go) /BG [0.8] /BC [0] >> >>");
            b.Obj(30, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
            b.Stream(31, "/Type /XObject /Subtype /Form /BBox [0 0 14 14]", Encoding.ASCII.GetBytes("0 0 m 14 14 l S"));
            b.Stream(32, "/Type /XObject /Subtype /Form /BBox [0 0 14 14]", []);
        },
        objects: 40,
        catalogEntries: "/AcroForm 5 0 R");

    private static byte[] SaveFull(PdfDocument d) => WriterSupport.Save(d, new PdfSaveOptions { XrefFormat = PdfXrefFormat.Stream, CompressStreams = true });

    private static string Names(PdfDocument d) => string.Join("|", d.Form!.AllFields.Select(static f => f.FullName));

    private static PdfFormValueError Reason(Action act) => Assert.Throws<PdfFormValueException>(act).Reason;

    /// <summary>The display list lines drawn inside the annotation whose rectangle starts with the given text.</summary>
    private static string Drawn(PdfDocument d, string rectStart, PdfInterpretOptions? options = null)
    {
        var list = new PdfDisplayList();
        d.Pages[0].Interpret(list, options ?? StoredOnly);
        string[] lines = list.ToText().Split('\n');
        var sb = new StringBuilder();
        bool inside = false;
        foreach (string l in lines)
        {
            string t = l.Trim();
            if (t.StartsWith("begin-annot", StringComparison.Ordinal))
            {
                inside = t.Contains("rect=" + rectStart, StringComparison.Ordinal);
            }
            else if (inside)
            {
                sb.AppendLine(t);
            }
        }

        return sb.ToString();
    }

    private static PdfDictionary Widget(PdfDocument d, string name, int i = 0) => d.Form!.FindField(name)!.Widgets[i];

    // ---- rename ----

    [Fact]
    public void RenamesNestedFieldAndKeepsWidgetAndValue()
    {
        byte[] input = Sample();
        using PdfDocument doc = Open(input);
        doc.Form!.RenameField("person.last", "surname");
        Assert.Equal("person.surname", doc.Form.FindField("person.surname")!.FullName);
        Assert.Null(doc.Form.FindField("person.last"));

        FormTestSupport.BothSaves(doc, input, d =>
        {
            PdfFormField f = d.Form!.FindField("person.surname")!;
            Assert.Equal("Hopper", f.Value);
            Assert.Single(f.Widgets);
            Assert.Equal("person", f.Parent!.FullName);
            Assert.Null(d.Form.FindField("person.last"));
            Assert.Equal(2, d.Form.FindField("person")!.Children.Count);
            Assert.Equal("surname", f.Widgets[0].GetString("T")!.GetText());
        }, "rename nested");
    }

    [Fact]
    public void RenamingAParentRenamesEveryDescendantFullName()
    {
        byte[] input = Sample();
        using PdfDocument doc = Open(input);
        PdfFormField first = doc.Form!.FindField("person.first")!;
        doc.Form.FindField("person")!.Rename("human");
        Assert.Equal("human.first", first.FullName);

        FormTestSupport.BothSaves(doc, input, d =>
        {
            Assert.Null(d.Form!.FindField("person"));
            Assert.Equal("Grace", d.Form.FindField("human.first")!.Value);
            Assert.Equal("Hopper", d.Form.FindField("human.last")!.Value);
            Assert.Equal(2, d.Form.FindField("human")!.Children.Count);
        }, "rename parent");
    }

    [Fact]
    public void RenamesAMergedFieldAndWidgetAndAFieldWithSeveralWidgets()
    {
        byte[] input = Sample();
        using PdfDocument doc = Open(input);
        doc.Form!.RenameField("name", "title");
        doc.Form.RenameField("multi", "many");
        FormTestSupport.BothSaves(doc, input, d =>
        {
            PdfFormField t = d.Form!.FindField("title")!;
            Assert.Equal("Ada", t.Value);
            Assert.Same(t.Dictionary, t.Widgets[0]);
            Assert.Null(d.Form.FindField("name"));
            PdfFormField m = d.Form.FindField("many")!;
            Assert.Equal(2, m.Widgets.Count);
            Assert.Equal("Many", m.Value);
        }, "rename merged");
    }

    [Fact]
    public void RenameRefusalsChangeNothing()
    {
        byte[] input = Sample();
        using PdfDocument doc = Open(input);
        PdfAcroForm form = doc.Form!;
        string before = Names(doc);
        Assert.Equal(PdfFormValueError.NameInUse, Reason(() => form.RenameField("person.first", "last")));
        Assert.Equal(PdfFormValueError.NameInUse, Reason(() => form.RenameField("name", "solo")));
        Assert.Equal(PdfFormValueError.NameInUse, Reason(() => form.RenameField("grp", "person")));
        Assert.Equal(PdfFormValueError.InvalidName, Reason(() => form.RenameField("name", "a.b")));
        Assert.Equal(PdfFormValueError.InvalidName, Reason(() => form.RenameField("name", "")));
        Assert.Equal(PdfFormValueError.UnknownField, Reason(() => form.RenameField("nothing", "x")));
        Assert.Throws<ArgumentNullException>(() => form.RenameField("name", null!));
        Assert.Equal(before, Names(doc));
        form.RenameField("name", "name"); // same name: nothing to do

        byte[] saved = SaveFull(doc);
        using PdfDocument d = PdfDocument.Open(saved);
        Assert.Equal(before, Names(d));
        Assert.Equal("Ada", d.Form!.FindField("name")!.Value);
    }

    [Fact]
    public void RenameNeedsAnEditableDocument()
    {
        using PdfDocument doc = PdfDocument.Open(Sample());
        Assert.Throws<InvalidOperationException>(() => doc.Form!.RenameField("name", "x"));
    }

    [Fact]
    public void RenameAfterRenameFindsTheFieldByItsNewName()
    {
        using PdfDocument doc = Open(Sample());
        doc.Form!.RenameField("person", "p2");
        doc.Form.RenameField("p2.first", "given");
        doc.Form.SetValue("p2.given", "Ada");
        Assert.Equal("Ada", doc.Form.FindField("p2.given")!.Value);
        // A name freed by a rename can be used again.
        doc.Form.RenameField("solo", "p2x");
        doc.Form.RenameField("name", "solo");
        Assert.Equal("Ada", doc.Form.FindField("solo")!.Value);
    }

    // ---- appearance ----

    [Fact]
    public void SetsBackgroundAndRebuildsTheStoredAppearance()
    {
        byte[] input = Sample();
        using PdfDocument doc = Open(input);
        int n = doc.Form!.SetFieldAppearance("name", new PdfFieldAppearance { Background = PdfFieldColor.Rgb(1, 1, 0.5) });
        Assert.Equal(1, n);
        FormTestSupport.BothSaves(doc, input, d =>
        {
            PdfDictionary mk = Widget(d, "name").GetDictionary("MK")!;
            Assert.Equal(3, mk.GetArray("BG")!.Count);
            Assert.Equal(1, mk.GetArray("BC")!.Count); // untouched
            string drawn = Drawn(d, "10 170 ");
            Assert.Contains("fill=rgb(1 1 0.5)", drawn);
            Assert.Contains("fill nonzero", drawn);
            Assert.Contains("Ada", FormTestSupport.AnnotationText(d, StoredOnly)[Widget(d, "name").Reference!.Number]);
        }, "background");
    }

    [Fact]
    public void TransparentBackgroundRemovesTheFill()
    {
        byte[] input = Sample();
        using PdfDocument doc = Open(input);
        doc.Form!.SetFieldAppearance("multi", new PdfFieldAppearance { Background = PdfFieldColor.Transparent });
        FormTestSupport.BothSaves(doc, input, d =>
        {
            PdfFormField f = d.Form!.FindField("multi")!;
            Assert.Null(f.Widgets[1].GetDictionary("MK")!.GetRaw("BG"));
            Assert.DoesNotContain("fill ", Drawn(d, "120 80 "));
        }, "transparent");
    }

    [Fact]
    public void SetsFontSizeAndColourInDa()
    {
        byte[] input = Sample();
        using PdfDocument doc = Open(input);
        doc.Form!.SetFieldAppearance("name", new PdfFieldAppearance { FontSize = 14, TextColor = PdfFieldColor.Cmyk(0, 1, 1, 0) });
        FormTestSupport.BothSaves(doc, input, d =>
        {
            Assert.Equal("/Helv 14 Tf 0 1 1 0 k", Widget(d, "name").GetString("DA")!.GetText());
            string drawn = Drawn(d, "10 170 ");
            Assert.Contains("size=14", drawn);
            Assert.Contains("fill=cmyk(0 1 1 0)", drawn);
        }, "size and colour");
    }

    [Fact]
    public void InheritedDaFromAParentChangesOnlyTheTargetField()
    {
        byte[] input = Sample();
        using PdfDocument doc = Open(input);
        doc.Form!.SetFieldAppearance("person.first", new PdfFieldAppearance { FontSize = 0, TextColor = PdfFieldColor.Gray(0.5) });
        FormTestSupport.BothSaves(doc, input, d =>
        {
            Assert.Equal("/Helv 0 Tf 0.5 g", d.Form!.FindField("person.first")!.Dictionary.GetString("DA")!.GetText());
            // The sibling and the parent keep the inherited string.
            Assert.Null(d.Form.FindField("person.last")!.Dictionary.GetString("DA"));
            Assert.Equal("/Helv 10 Tf 0 0 1 rg", d.Form.FindField("person")!.Dictionary.GetString("DA")!.GetText());
            // The sibling has no stored appearance of its own; drawn on demand it still uses the inherited size 10.
            Assert.Contains("size=10", Drawn(d, "10 110 ", new PdfInterpretOptions()));
            Assert.Equal(string.Empty, Drawn(d, "10 110 "));
        }, "inherited from parent");
    }

    [Fact]
    public void InheritedDaFromTheFormKeepsTheFontAndLeavesTheOthersAlone()
    {
        byte[] input = Sample();
        using PdfDocument doc = Open(input);
        doc.Form!.SetFieldAppearance("solo", new PdfFieldAppearance { TextColor = PdfFieldColor.Rgb(1, 0, 0) });
        FormTestSupport.BothSaves(doc, input, d =>
        {
            Assert.Equal("/Helv 11 Tf 1 0 0 rg", d.Form!.FindField("solo")!.Dictionary.GetString("DA")!.GetText());
            Assert.Equal("/Helv 11 Tf 0 g", d.Form.Dictionary.GetString("DA")!.GetText());
            Assert.Null(d.Form.FindField("grp.a")!.Dictionary.GetString("DA"));
        }, "inherited from form");
    }

    [Fact]
    public void ParentScopeAppliesToEveryTerminalDescendantAndTheirWidgetOverrides()
    {
        byte[] input = Sample();
        using PdfDocument doc = Open(input);
        int n = doc.Form!.SetFieldAppearance("multi", new PdfFieldAppearance { FontSize = 7, Background = PdfFieldColor.Gray(0.9) });
        Assert.Equal(2, n);
        n = doc.Form.SetFieldAppearance("grp", new PdfFieldAppearance { FontSize = 8 });
        Assert.Equal(2, n);
        FormTestSupport.BothSaves(doc, input, d =>
        {
            PdfFormField m = d.Form!.FindField("multi")!;
            Assert.Equal("/Helv 7 Tf 0 g", m.Dictionary.GetString("DA")!.GetText());
            Assert.Null(m.Widgets[0].GetString("DA"));
            Assert.Equal("/Helv 7 Tf 1 0 0 rg", m.Widgets[1].GetString("DA")!.GetText());
            Assert.Equal(0.9, m.Widgets[1].GetDictionary("MK")!.GetArray("BG")!.GetDouble(0), 6);
            Assert.Equal("/Helv 8 Tf 0 g", d.Form.FindField("grp.a")!.Dictionary.GetString("DA")!.GetText());
            Assert.Equal("/Helv 8 Tf 0 g", d.Form.FindField("grp.b")!.Dictionary.GetString("DA")!.GetText());
            Assert.Null(d.Form.FindField("solo")!.Dictionary.GetString("DA"));
        }, "parent scope");
    }

    [Fact]
    public void ButtonsAreRedrawnInTheNewColours()
    {
        byte[] input = Sample();
        using PdfDocument doc = Open(input);
        doc.Form!.SetFieldAppearance("agree", new PdfFieldAppearance { Background = PdfFieldColor.Rgb(0, 1, 0), TextColor = PdfFieldColor.Gray(0.2) });
        doc.Form.SetFieldAppearance("go", new PdfFieldAppearance { Background = PdfFieldColor.Transparent, FontSize = 12 });
        FormTestSupport.BothSaves(doc, input, d =>
        {
            PdfFormField agree = d.Form!.FindField("agree")!;
            Assert.Equal("Yes", agree.Widgets[0].GetName("AS"));
            PdfDictionary states = agree.Widgets[0].GetDictionary("AP")!.GetDictionary("N")!;
            Assert.NotNull(states.GetStream("Yes"));
            Assert.NotNull(states.GetStream("Off"));
            string on = Drawn(d, "10 50 ");
            Assert.Contains("fill=rgb(0 1 0)", on);
            Assert.DoesNotContain("stroke M 0 0 L 14 14", on);
            Assert.Contains("fill=gray(0.2)", on);
            Assert.DoesNotContain("fill=gray(0.8)", Drawn(d, "120 50 "));
            Assert.Contains("size=12", Drawn(d, "120 50 "));
            Assert.Null(d.Form.FindField("go")!.Widgets[0].GetDictionary("MK")!.GetRaw("BG"));
        }, "buttons");
    }

    [Fact]
    public void SetAppearanceChecksItsArguments()
    {
        using PdfDocument doc = Open(Sample());
        PdfAcroForm form = doc.Form!;
        Assert.Throws<ArgumentException>(() => form.SetFieldAppearance("name", new PdfFieldAppearance { TextColor = PdfFieldColor.Transparent }));
        Assert.Throws<ArgumentOutOfRangeException>(() => form.SetFieldAppearance("name", new PdfFieldAppearance { FontSize = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfFieldColor.Gray(2));
        Assert.Equal(PdfFormValueError.UnknownField, Reason(() => form.SetFieldAppearance("zzz", new PdfFieldAppearance { FontSize = 9 })));
        Assert.Equal(0, form.SetFieldAppearance("name", new PdfFieldAppearance()));
        Assert.Null(form.FindField("name")!.Dictionary.GetString("DA"));
        using PdfDocument ro = PdfDocument.Open(Sample());
        Assert.Throws<InvalidOperationException>(() => ro.Form!.SetFieldAppearance("name", new PdfFieldAppearance { FontSize = 9 }));
    }

    [Fact]
    public void AppearanceAndRenameWorkTogetherInAnIncrementalSave()
    {
        byte[] input = Sample();
        using PdfDocument doc = Open(input);
        doc.Form!.RenameField("person.first", "given");
        doc.Form.SetFieldAppearance("person.given", new PdfFieldAppearance { Background = PdfFieldColor.Gray(0.75), FontSize = 13 });
        byte[] inc = WriterSupport.SaveIncremental(doc);
        Assert.True(inc.Length > input.Length);
        Assert.True(inc.AsSpan(0, input.Length).SequenceEqual(input));
        using PdfDocument d = PdfDocument.Open(inc);
        PdfFormField f = d.Form!.FindField("person.given")!;
        Assert.Equal("Grace", f.Value);
        Assert.Equal("/Helv 13 Tf 0 0 1 rg", f.Dictionary.GetString("DA")!.GetText());
        Assert.Contains("size=13", Drawn(d, "10 140 "));
        Assert.Null(d.Form.FindField("person.first"));
    }

    [Theory]
    [InlineData("realworld/pdf/form-01-gov336501.pdf")]
    [InlineData("realworld/pdf/form-04-gov756951.pdf")]
    public void RealWorldFormRenameAndAppearanceSurviveASaveAndQpdf(string file)
    {
        byte[] input = FormTestSupport.Corpus(file);
        using PdfDocument doc = Open(input);
        PdfFormField f = doc.Form!.TerminalFields.First(static t => t.Kind == PdfFieldKind.Text && t.Widgets.Count > 0 && t.Widgets[0].Get("Rect") is PdfArray);
        string old = f.FullName, partial = f.PartialName, value = f.Value ?? string.Empty, parent = f.Parent?.FullName ?? string.Empty;
        int widgets = f.Widgets.Count;
        string neu = partial + "Renamed";
        f.Rename(neu);
        string newFull = parent.Length == 0 ? neu : parent + "." + neu;
        Assert.Equal(newFull, f.FullName);
        Assert.Equal(widgets, f.SetAppearance(new PdfFieldAppearance { Background = PdfFieldColor.Rgb(1, 1, 0.8), FontSize = 9, TextColor = PdfFieldColor.Gray(0.1) }));

        byte[] full = SaveFull(doc);
        if (WriterSupport.Qpdf() is not null)
        {
            (int code, string output) = WriterSupport.QpdfRun(full);
            Assert.True(code == 0, output);
        }

        using PdfDocument d = PdfDocument.Open(full);
        PdfFormField g = d.Form!.FindField(newFull)!;
        Assert.NotNull(g);
        Assert.Null(d.Form.FindField(old));
        Assert.Equal(value, g.Value ?? string.Empty);
        Assert.Equal(widgets, g.Widgets.Count);
        Assert.Equal(3, g.Widgets[0].GetDictionary("MK")!.GetArray("BG")!.Count);
        Assert.Contains(" 9 Tf", g.Widgets[0].GetString("DA")?.GetText() ?? g.Dictionary.GetString("DA")!.GetText());
        Assert.NotNull(g.Widgets[0].GetDictionary("AP")!.Get("N"));
    }

    [Fact]
    public void ModifyDaKeepsOtherOperators()
    {
        Assert.Equal("/F1 9 Tf 0 Tc 1 g", OnePdf.Forms.FormEditor.ModifyDa("/F1 12 Tf 0 Tc 1 g", 9, null));
        Assert.Equal("/F1 12 Tf 0 Tc 0.5 0.5 0.5 rg", OnePdf.Forms.FormEditor.ModifyDa("/F1 12 Tf 0 Tc 1 g", null, [0.5, 0.5, 0.5]));
        Assert.Equal("/Helv 10 Tf 0 g", OnePdf.Forms.FormEditor.ModifyDa(null, 10, [0]));
        Assert.Equal("/F1 12 Tf 0 g", OnePdf.Forms.FormEditor.ModifyDa("/F1 12 Tf", null, [0]));
    }
}
