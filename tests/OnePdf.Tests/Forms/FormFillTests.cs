using System.Text;
using OnePdf.Tests.Writing;

namespace OnePdf.Tests.Forms;

/// <summary>Filling form fields: every field type, the rules, the errors, and what a saved file shows.</summary>
public class FormFillTests
{
    private static readonly PdfInterpretOptions StoredOnly = new() { GenerateAppearances = false };

    private static PdfDocument Open(byte[] data) => PdfDocument.Open(data, FormTestSupport.Editable);

    // Full saves renumber objects, so find the widget by field name in the reopened file.
    private static string Drawn(PdfDocument d, Dictionary<int, string> text, string field) =>
        text[d.Form!.FindField(field)!.Widgets[0].Reference!.Number];

    private static PdfFormValueError Reason(Action act) => Assert.Throws<PdfFormValueException>(act).Reason;

    // ---- the synthetic form, field by field ----

    [Fact]
    public void FillsEveryFieldTypeAndStoresAppearances()
    {
        byte[] input = FormTestSupport.Synthetic();
        using PdfDocument doc = Open(input);
        PdfAcroForm form = doc.Form!;
        form.SetValue("name", "Ada");
        form.SetValue("zip", "12345");
        form.SetValue("fruit", "Banana");
        form.SetValues("tags", ["c", "a"]);
        form.SetChecked("agree", true);
        form.SetValue("size", "M");
        form.SetValue("notes", "line1\nline2");

        FormTestSupport.BothSaves(doc, input, d =>
        {
            PdfAcroForm f = d.Form!;
            Assert.Equal("Ada", f.FindField("name")!.Value);
            Assert.Equal("12345", f.FindField("zip")!.Value);
            Assert.Equal("b", f.FindField("fruit")!.Value);
            Assert.Equal(["a", "c"], f.FindField("tags")!.Values);
            Assert.Equal("Yes", f.FindField("agree")!.Value);
            Assert.Equal("M", f.FindField("size")!.Value);
            Assert.Equal("line1\nline2", f.FindField("notes")!.Value!.Replace("\r", string.Empty));

            PdfFormField size = f.FindField("size")!;
            Assert.Equal("Off", size.Widgets[0].GetName("AS"));
            Assert.Equal("M", size.Widgets[1].GetName("AS"));
            Assert.Equal("Yes", f.FindField("agree")!.Widgets[0].GetName("AS"));

            // The stored appearances alone draw the values.
            Dictionary<int, string> text = FormTestSupport.AnnotationText(d, StoredOnly);
            Assert.Contains("Ada", Drawn(d, text, "name"));
            Assert.Contains("12345", Drawn(d, text, "zip"));
            Assert.Contains("Banana", Drawn(d, text, "fruit"));
            Assert.Contains("line1", Drawn(d, text, "notes"));
            Assert.Contains("line2", Drawn(d, text, "notes"));
            Assert.Contains("abcd", Drawn(d, text, "tags"));
        }, "fill");
    }

    [Fact]
    public void FillingUpdatesTheDocumentItself()
    {
        using PdfDocument doc = Open(FormTestSupport.Synthetic());
        PdfFormField name = doc.Form!.FindField("name")!;
        name.SetValue("Zed");
        Assert.Equal("Zed", name.Value);
        PdfStream ap = name.Widgets[0].GetDictionary("AP")!.GetStream("N")!;
        Assert.Equal("Form", ap.Dictionary.GetName("Subtype"));
        Assert.Contains("Tx BMC", Encoding.Latin1.GetString(ap.GetDecodedBytes().Span));
        Assert.NotNull(ap.Reference);
    }

    [Fact]
    public void ComboAcceptsExportOrDisplayAndEditableAcceptsAnything()
    {
        using PdfDocument doc = Open(FormTestSupport.Synthetic());
        PdfFormField fruit = doc.Form!.FindField("fruit")!;
        fruit.SetValue("apple");
        Assert.Equal("apple", fruit.Value);
        fruit.SetValue("b");
        Assert.Equal("b", fruit.Value);
        fruit.SetValue("Banana");
        Assert.Equal("b", fruit.Value);

        // The edit flag (bit 19) lets a combo box hold text that is not an option.
        using PdfDocument doc2 = Open(FormTestSupport.Synthetic());
        PdfFormField f2 = doc2.Form!.FindField("fruit")!;
        f2.Dictionary.SetEntry("Ff", new PdfInteger(131072 | (1 << 18)));
        f2.SetValue("durian");
        Assert.Equal("durian", f2.Value);
    }

    [Fact]
    public void MultiSelectWritesAnArrayAndSortedIndices()
    {
        using PdfDocument doc = Open(FormTestSupport.Synthetic());
        PdfFormField tags = doc.Form!.FindField("tags")!;
        tags.SetValues(["d", "b", "d"]);
        Assert.Equal(["b", "d"], tags.Values);
        PdfArray i = tags.Dictionary.GetArray("I")!;
        Assert.Equal([1, 3], [i.GetInt(0), i.GetInt(1)]);
        tags.SetValues([]);
        Assert.Empty(tags.Values);
        Assert.Null(tags.Dictionary.GetArray("I"));
    }

    [Fact]
    public void RadioButtonsTurnOnOneWidgetAndOthersOff()
    {
        using PdfDocument doc = Open(FormTestSupport.Synthetic());
        PdfFormField size = doc.Form!.FindField("size")!;
        size.SetValue("S");
        Assert.Equal(["S", "Off"], [size.Widgets[0].GetName("AS"), size.Widgets[1].GetName("AS")]);
        size.SetValue("M");
        Assert.Equal(["Off", "M"], [size.Widgets[0].GetName("AS"), size.Widgets[1].GetName("AS")]);
        size.SetChecked(false);
        Assert.Equal("Off", size.Value);
        Assert.Equal(["Off", "Off"], [size.Widgets[0].GetName("AS"), size.Widgets[1].GetName("AS")]);
    }

    [Fact]
    public void RadioExportValuesFromOptSelectTheWidgetByIndex()
    {
        // 12.7.5.2.4: with /Opt the appearance states are index names and /Opt holds the export values.
        byte[] data = FormTestSupport.Synthetic();
        using PdfDocument doc = Open(data);
        PdfFormField size = doc.Form!.FindField("size")!;
        foreach (PdfDictionary w in size.Widgets)
        {
            PdfDictionary n = w.GetDictionary("AP")!.GetDictionary("N")!;
            string old = n.Keys.First(static k => k != "Off");
            PdfObject stream = n.GetRaw(old)!;
            n.RemoveEntry(old);
            n.SetEntry(w == size.Widgets[0] ? "0" : "1", stream);
        }

        var opt = new PdfArray();
        opt.Append(PdfString.FromText("Red"));
        opt.Append(PdfString.FromText("Blue"));
        size.Dictionary.SetEntry("Opt", opt);
        size.SetValue("Blue");
        Assert.Equal("1", size.Value);
        Assert.Equal("1", size.Widgets[1].GetName("AS"));
        Assert.Equal("Off", size.Widgets[0].GetName("AS"));
    }

    [Fact]
    public void RadiosInUnisonTurnOnEveryWidgetWithTheState()
    {
        using PdfDocument doc = Open(FormTestSupport.Synthetic());
        PdfFormField size = doc.Form!.FindField("size")!;
        // Both widgets use state S; with the unison flag (bit 26) both turn on, without it only the first does.
        PdfDictionary n1 = size.Widgets[1].GetDictionary("AP")!.GetDictionary("N")!;
        PdfObject stream = n1.GetRaw("M")!;
        n1.RemoveEntry("M");
        n1.SetEntry("S", stream);
        size.SetValue("S");
        Assert.Equal(["S", "Off"], [size.Widgets[0].GetName("AS"), size.Widgets[1].GetName("AS")]);
        size.Dictionary.SetEntry("Ff", new PdfInteger(49152 | (1 << 25)));
        size.SetValue("S");
        Assert.Equal(["S", "S"], [size.Widgets[0].GetName("AS"), size.Widgets[1].GetName("AS")]);
    }

    [Fact]
    public void CheckBoxWithoutAppearanceGetsOneForItsOnState()
    {
        using PdfDocument doc = Open(FormTestSupport.Synthetic());
        PdfFormField agree = doc.Form!.FindField("agree")!;
        agree.Widgets[0].GetDictionary("AP")!.RemoveEntry("N");
        agree.SetChecked(true);
        PdfDictionary n = agree.Widgets[0].GetDictionary("AP")!.GetDictionary("N")!;
        Assert.NotNull(n.GetStream("Yes"));
        Assert.Equal("Yes", agree.Widgets[0].GetName("AS"));
    }

    [Fact]
    public void ReadOnlyCanBeOverridden()
    {
        using PdfDocument doc = Open(FormTestSupport.Synthetic());
        PdfFormField locked = doc.Form!.FindField("locked")!;
        Assert.Equal(PdfFormValueError.ReadOnly, Reason(() => locked.SetValue("x")));
        locked.SetValue("x", new PdfFillOptions { OverrideReadOnly = true });
        Assert.Equal("x", locked.Value);
    }

    [Fact]
    public void TooLongTextIsRefusedOrCutAtCodePoints()
    {
        using PdfDocument doc = Open(FormTestSupport.Synthetic());
        PdfFormField name = doc.Form!.FindField("name")!;
        Assert.Equal(PdfFormValueError.TooLong, Reason(() => name.SetValue("123456789")));
        Assert.Null(name.Value);
        name.SetValue("12345678"); // exactly /MaxLen
        Assert.Equal("12345678", name.Value);
        // MaxLen 8 counts characters, not UTF-16 units: the emoji is one character.
        name.SetValue("abc\U0001F600defghij", new PdfFillOptions { TruncateToMaxLength = true });
        Assert.Equal("abc\U0001F600defg", name.Value);
    }

    [Fact]
    public void ValueErrorsAreTyped()
    {
        using PdfDocument doc = Open(FormTestSupport.Synthetic());
        PdfAcroForm form = doc.Form!;
        Assert.Equal(PdfFormValueError.UnknownField, Reason(() => form.SetValue("nope", "x")));
        Assert.Equal(PdfFormValueError.NotInOptions, Reason(() => form.SetValue("fruit", "kiwi")));
        Assert.Equal(PdfFormValueError.NotInOptions, Reason(() => form.SetValues("tags", ["a", "z"])));
        Assert.Equal(PdfFormValueError.NotInOptions, Reason(() => form.SetValue("size", "XL")));
        Assert.Equal(PdfFormValueError.WrongType, Reason(() => form.SetChecked("name", true)));
        Assert.Equal(PdfFormValueError.WrongType, Reason(() => form.SetValue("go", "x")));
        Assert.Equal(PdfFormValueError.WrongType, Reason(() => form.SetChecked("size", true)));
        Assert.Equal(PdfFormValueError.NotMultiSelect, Reason(() => form.SetValues("fruit", ["apple", "b"])));
        Assert.Equal(PdfFormValueError.NotMultiSelect, Reason(() => form.SetValues("name", ["a", "b"])));
        // A refused value leaves the field as it was.
        Assert.Null(form.FindField("fruit")!.Value);
        Assert.Equal("nope", Assert.Throws<PdfFormValueException>(() => form.SetValue("nope", "x")).FieldName);
    }

    [Fact]
    public void ParentFieldsAndReadOnlyDocumentsAreRefused()
    {
        using PdfDocument doc = Open(FormTestSupport.Synthetic());
        Assert.NotNull(doc.Form);
        using PdfDocument ro = PdfDocument.Open(FormTestSupport.Synthetic());
        Assert.Throws<InvalidOperationException>(() => ro.Form!.SetValue("name", "x"));
        Assert.Throws<InvalidOperationException>(() => ro.Flatten());
    }

    [Fact]
    public void NotTerminalFieldIsRefused()
    {
        byte[] data = TestPdf_ParentForm();
        using PdfDocument doc = Open(data);
        Assert.Equal(PdfFormValueError.NotTerminal, Reason(() => doc.Form!.SetValue("addr", "x")));
        doc.Form!.SetValue("addr.city", "Oslo");
        Assert.Equal("Oslo", doc.Form!.FindField("addr.city")!.Value);
    }

    private static byte[] TestPdf_ParentForm() => Content.TestPdf.Build(
        string.Empty,
        "/Annots [6 0 R]",
        b =>
        {
            b.Obj(5, "<< /Fields [7 0 R] /DA (/Helv 12 Tf 0 g) >>");
            b.Obj(7, "<< /T (addr) /FT /Tx /Kids [6 0 R] >>");
            b.Obj(6, "<< /Type /Annot /Subtype /Widget /F 4 /Parent 7 0 R /T (city) /Rect [10 10 100 30] >>");
        },
        objects: 8,
        catalogEntries: "/AcroForm 5 0 R");

    [Fact]
    public void NeedAppearancesGoesOnlyWhenEveryWidgetHasOne()
    {
        byte[] input = FormTestSupport.Synthetic();
        using PdfDocument doc = Open(input);
        PdfAcroForm form = doc.Form!;
        form.SetValue("name", "Ada");
        // The text fields and the push button have no appearance yet, so the flag stays.
        Assert.True(form.NeedAppearances);
        int made = form.GenerateMissingAppearances();
        Assert.True(made > 0);
        Assert.False(form.NeedAppearances);
        Assert.False(doc.Form!.NeedAppearances);
        foreach (PdfFormField f in form.TerminalFields)
        {
            foreach (PdfDictionary w in f.Widgets)
            {
                Assert.NotNull(w.GetDictionary("AP")?.Get("N"));
            }
        }

        FormTestSupport.BothSaves(doc, input, d => Assert.False(d.Form!.NeedAppearances), "needappearances");
    }

    [Fact]
    public void FlagIsClearedByTheFillThatCompletesTheForm()
    {
        // Only text fields, all filled one after the other: the last fill clears the flag, and the fields not filled by
        // the caller that held stale appearances were rebuilt first.
        byte[] data = Content.TestPdf.Build(
            string.Empty,
            "/Annots [6 0 R 7 0 R]",
            b =>
            {
                b.Obj(5, "<< /Fields [6 0 R 7 0 R] /DA (/Helv 12 Tf 0 g) /NeedAppearances true >>");
                b.Obj(6, "<< /Type /Annot /Subtype /Widget /F 4 /FT /Tx /T (a) /Rect [10 10 100 30] /V (old) >>");
                b.Obj(7, "<< /Type /Annot /Subtype /Widget /F 4 /FT /Tx /T (b) /Rect [10 40 100 60] /V (stale value) >>");
            },
            objects: 8,
            catalogEntries: "/AcroForm 5 0 R");
        using PdfDocument doc = Open(data);
        doc.Form!.SetValue("a", "one");
        Assert.True(doc.Form!.NeedAppearances);
        doc.Form!.SetValue("b", "two");
        Assert.False(doc.Form!.NeedAppearances);
        FormTestSupport.BothSaves(doc, data, d =>
        {
            Dictionary<int, string> text = FormTestSupport.AnnotationText(d, StoredOnly);
            Assert.Contains("one", Drawn(d, text, "a"));
            Assert.Contains("two", Drawn(d, text, "b"));
        }, "flag");
    }

    [Fact]
    public void WithoutAppearanceGenerationOnlyTheValueChanges()
    {
        using PdfDocument doc = Open(FormTestSupport.Synthetic());
        doc.Form!.SetValue("name", "Ada", new PdfFillOptions { GenerateAppearances = false });
        Assert.Equal("Ada", doc.Form!.FindField("name")!.Value);
        Assert.Null(doc.Form!.FindField("name")!.Widgets[0].GetDictionary("AP"));
        Assert.True(doc.Form!.NeedAppearances);
    }

    [Fact]
    public void ResetRestoresDefaults()
    {
        using PdfDocument doc = Open(FormTestSupport.Synthetic());
        PdfAcroForm form = doc.Form!;
        form.FindField("name")!.Dictionary.SetEntry("DV", PdfString.FromText("Anon"));
        form.FindField("size")!.Dictionary.SetEntry("DV", new PdfName("S"));
        form.SetValue("name", "Ada");
        form.SetValue("size", "M");
        form.SetValue("fruit", "apple");
        form.SetChecked("agree", true);
        form.ResetAll();
        Assert.Equal("Anon", form.FindField("name")!.Value);
        Assert.Equal("S", form.FindField("size")!.Value);
        Assert.Equal("S", form.FindField("size")!.Widgets[0].GetName("AS"));
        Assert.Equal("Off", form.FindField("size")!.Widgets[1].GetName("AS"));
        Assert.Null(form.FindField("fruit")!.Value);
        Assert.Equal("Off", form.FindField("agree")!.Value);
        Assert.Equal("fixed", form.FindField("locked")!.Value);
    }

    [Fact]
    public void ScriptsAreNeverRun()
    {
        // A calculate action is data: the value is stored as given.
        byte[] data = FormTestSupport.Synthetic(extraFlags: "/AA << /K << /S /JavaScript /JS (event.value = 'changed';) >> /C << /S /JavaScript /JS (event.value = 'x';) >> >>");
        using PdfDocument doc = Open(data);
        doc.Form!.SetValue("notes", "as given");
        Assert.Equal("as given", doc.Form!.FindField("notes")!.Value);
    }

    [Fact]
    public void TextOutsideTheFontEncodingIsKeptInTheValue()
    {
        using PdfDocument doc = Open(FormTestSupport.Synthetic());
        doc.Form!.SetValue("notes", "Grüße \u4e2d");
        Assert.Equal("Grüße \u4e2d", doc.Form!.FindField("notes")!.Value);
        byte[] saved = WriterSupport.Save(doc);
        using PdfDocument d = PdfDocument.Open(saved);
        Assert.Equal("Grüße \u4e2d", d.Form!.FindField("notes")!.Value);
    }

    // ---- committed form files ----

    [Theory]
    [MemberData(nameof(FormTestSupport.FormFiles), MemberType = typeof(FormTestSupport))]
    public void FillsCommittedForms(string file)
    {
        byte[] input = FormTestSupport.Corpus(file);
        using PdfDocument doc = Open(input);
        PdfAcroForm form = doc.Form!;
        var expect = new Dictionary<string, string>();
        var options = new PdfFillOptions { OverrideReadOnly = true, TruncateToMaxLength = true };
        foreach (PdfFormField f in form.TerminalFields.ToList())
        {
            if (f.Widgets.Count == 0 || f.IsPassword)
            {
                continue;
            }

            switch (f.Kind)
            {
                case PdfFieldKind.Text:
                    int max = f.MaxLength ?? int.MaxValue;
                    string v = "Zq7 test"[..Math.Min(8, Math.Max(1, max))];
                    f.SetValue(v, options);
                    expect[f.FullName] = v;
                    break;
                case PdfFieldKind.ComboBox or PdfFieldKind.ListBox when f.Options.Count > 0:
                    f.SetValue(f.Options[0].Export, options);
                    expect[f.FullName] = f.Options[0].Export;
                    break;
                case PdfFieldKind.CheckBox:
                    f.SetChecked(true, options);
                    expect[f.FullName] = f.Value!;
                    Assert.NotEqual("Off", f.Value);
                    break;
                case PdfFieldKind.RadioButton:
                    string? state = f.Widgets.Select(static w => w.GetDictionary("AP")?.GetDictionary("N")?.Keys.FirstOrDefault(static k => k != "Off")).FirstOrDefault(static s => s is not null);
                    if (state is not null)
                    {
                        f.SetValue(state, options);
                        expect[f.FullName] = state;
                    }

                    break;
            }
        }

        Assert.NotEmpty(expect);
        FormTestSupport.BothSaves(doc, input, d =>
        {
            PdfAcroForm f2 = d.Form!;
            foreach ((string name, string value) in expect)
            {
                PdfFormField? field = f2.FindField(name);
                Assert.NotNull(field);
                Assert.Equal(value, field.Value);
            }

            // The saved appearances alone draw every text value that was set.
            Dictionary<int, string> text = FormTestSupport.AnnotationText(d, StoredOnly);
            foreach (PdfFormField f in f2.TerminalFields.Where(static f => f.Kind == PdfFieldKind.Text && f.Widgets.Count > 0))
            {
                if (!expect.TryGetValue(f.FullName, out string? value))
                {
                    continue;
                }

                foreach (PdfDictionary w in f.Widgets)
                {
                    long flags = w.GetInt("F");
                    if ((flags & 34) != 0 || w.Reference is null || !text.TryGetValue(w.Reference.Number, out string? drawn))
                    {
                        continue;
                    }

                    Assert.True(drawn.Replace(" ", string.Empty).Contains(value[..Math.Min(3, value.Length)].Replace(" ", string.Empty), StringComparison.Ordinal), $"{file}: field {f.FullName} draws '{drawn}' for '{value}'");
                }
            }
        }, file);
    }

    [Theory]
    [MemberData(nameof(FormTestSupport.FormFiles), MemberType = typeof(FormTestSupport))]
    public void ResetsCommittedForms(string file)
    {
        byte[] input = FormTestSupport.Corpus(file);
        using PdfDocument doc = Open(input);
        var before = doc.Form!.TerminalFields.ToDictionary(static f => f.FullName, static f => f.Value, StringComparer.Ordinal);
        var options = new PdfFillOptions { OverrideReadOnly = true, TruncateToMaxLength = true };
        foreach (PdfFormField f in doc.Form!.TerminalFields.Where(static f => f.Kind == PdfFieldKind.Text).ToList())
        {
            f.SetValue("temp", options);
        }

        doc.Form!.ResetAll(options);
        foreach (PdfFormField f in doc.Form!.TerminalFields.Where(static f => f.Kind == PdfFieldKind.Text))
        {
            Assert.Equal(f.DefaultValue is { Length: > 0 } dv ? dv : null, f.Value is { Length: > 0 } v ? v : null);
        }

        Assert.NotEmpty(before);
    }
}
