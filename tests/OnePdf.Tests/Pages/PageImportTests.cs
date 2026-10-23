using System.Text;
using OnePdf.Tests.Writing;

namespace OnePdf.Tests.Pages;

/// <summary>Importing pages from other documents, merging and splitting.</summary>
public class PageImportTests
{
    private static PdfDocument Open(byte[] bytes) => PdfDocument.Open(bytes);

    private static string[] Texts(PdfDocument d) => [.. Enumerable.Range(0, d.Pages.Count).Select(i => BookFixture.Text(d, i))];

    [Fact]
    public void ImportWritesInheritedAttributesAndNeutralisesLinksToPagesLeftBehind()
    {
        using PdfDocument source = Open(BookFixture.Book("A", 4));
        using PdfDocument target = PdfDocument.CreateEmpty();
        IReadOnlyList<PdfPage> added = target.ImportPages(source, [1, 2]);
        Assert.Equal(2, added.Count);
        Assert.Equal(["A1", "A2"], Texts(target));

        using PdfDocument again = PageEditTests.Reopen(target);
        PdfDictionary page = again.Pages[0].Dictionary;
        Assert.True(page.ContainsKey("MediaBox"));
        Assert.True(page.ContainsKey("Resources"));
        Assert.Equal(300, again.Pages[0].MediaBox.Width);
        Assert.Equal(["A1", "A2"], Texts(again));

        // A1's links lead to A2, which came along; A2's lead to A3, which did not.
        Assert.Equal(1, BookFixture.Target(again.Pages[0].Links[0].Destination));
        Assert.Equal(1, BookFixture.Target(again.Pages[0].Links[1].Action?.Destination));
        Assert.Null(again.Pages[1].Links[0].Destination);
        Assert.Null(again.Pages[1].Links[1].Action);

        // Only the names that imported links use are copied.
        Assert.Equal(["An2"], again.NamedDestinationNames);
        Assert.Equal(1, BookFixture.Target(again.GetNamedDestination("An2")));

        // Bookmarks of imported pages arrive; the others do not.
        Assert.Equal(["A chapter 1", "A chapter 2"], again.Outline.Select(o => o.Title));
        Assert.Equal([0, 1], again.Outline.Select(o => BookFixture.Target(o.Destination)));

        // The page labels of the source travel with the pages.
        Assert.Equal(["ii", "p-5"], Enumerable.Range(0, 2).Select(i => BookFixture.Label(again, i)));

        // The form field sat on page 0, which was not imported.
        Assert.Null(again.Form);
    }

    [Fact]
    public void SharedResourcesAreCopiedOnce()
    {
        using PdfDocument source = Open(BookFixture.Book("A", 6));
        using PdfDocument target = PdfDocument.CreateEmpty();
        target.ImportPages(source, [0, 1, 2]);
        target.ImportPages(source, [3, 4, 5]);
        using PdfDocument again = PageEditTests.Reopen(target);
        var fonts = new HashSet<int>();
        foreach (PdfPage p in again.Pages)
        {
            fonts.Add(((PdfReference)p.Dictionary.GetRaw("Resources")!).Number);
        }

        Assert.Single(fonts);
    }

    [Fact]
    public void MergeCombinesBooksFormsAndLabels()
    {
        byte[] a = BookFixture.Book("A", 4);
        byte[] b = BookFixture.Book("B", 3);
        using PdfDocument da = Open(a);
        using PdfDocument db = Open(b);
        using PdfDocument merged = PdfDocument.Merge([da, db]);
        da.Dispose();
        db.Dispose();
        using PdfDocument again = PageEditTests.Reopen(merged);
        Assert.Equal(["A0", "A1", "A2", "A3", "B0", "B1", "B2"], Texts(again));
        Assert.Equal(["i", "ii", "p-5", "p-6", "i", "ii", "p-5"], Enumerable.Range(0, 7).Select(i => BookFixture.Label(again, i)));
        Assert.Equal(7, again.Outline.Count);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], again.Outline.Select(o => BookFixture.Target(o.Destination)));

        // Same-named fields: the later one is renamed.
        PdfAcroForm form = again.Form!;
        Assert.Equal(["name", "name_2"], form.Fields.Select(f => f.FullName));
        Assert.Equal(["A-value", "B-value"], form.Fields.Select(f => f.Value));
        // The source asked for its fields to be redrawn; that flag would redraw the whole result, so the appearances are stored instead.
        Assert.False(form.NeedAppearances);
        Assert.All(form.Fields, f => Assert.NotNull(Assert.Single(f.Widgets).GetDictionary("AP")?.GetStream("N")));
        Assert.NotNull(form.Dictionary.GetDictionary("DR")?.GetDictionary("Font")?.GetRaw("Helv"));
        Assert.Contains(merged.Diagnostics, d => d.Message.Contains("name_2"));

        // Every widget belongs to its field and to its page.
        Assert.Equal(0, again.Pages.IndexOfObject(again.Pages[0].Dictionary.Reference!.Number));
        PdfFormField second = form.Fields[1];
        PdfDictionary widget = Assert.Single(second.Widgets);
        Assert.Equal(again.Pages[4].Dictionary.Reference!.Number, ((PdfReference)widget.GetRaw("P")!).Number);

        // Links and names work across the whole result.
        Assert.Equal(5, BookFixture.Target(again.Pages[4].Links[0].Destination));
        Assert.Equal(6, BookFixture.Target(again.GetNamedDestination("Bn2")));
        Assert.Equal(1, BookFixture.Target(again.GetNamedDestination("An1")));
        Assert.Equal(7, again.NamedDestinationNames.Count);
    }

    [Fact]
    public void SplitGivesEachPartOnlyItsOwnPages()
    {
        byte[] src = BookFixture.Book("Zebra", 6);
        using PdfDocument source = Open(src);
        IReadOnlyList<PdfDocument> parts = PdfDocument.Split(source, 2);
        try
        {
            Assert.Equal(3, parts.Count);
            for (int p = 0; p < 3; p++)
            {
                byte[] bytes = WriterSupport.Save(parts[p]);
                string text = Encoding.Latin1.GetString(bytes);
                for (int i = 0; i < 6; i++)
                {
                    Assert.Equal(i / 2 == p, text.Contains($"(Zebra{i})", StringComparison.Ordinal));
                }

                Assert.True(bytes.Length < src.Length, $"part {p}: {bytes.Length} >= {src.Length}");
                using PdfDocument again = Open(bytes);
                Assert.Equal([$"Zebra{2 * p}", $"Zebra{(2 * p) + 1}"], Texts(again));
            }

            // And back together.
            using PdfDocument whole = PdfDocument.Merge(parts);
            Assert.Equal(["Zebra0", "Zebra1", "Zebra2", "Zebra3", "Zebra4", "Zebra5"], Texts(whole));
        }
        finally
        {
            foreach (PdfDocument d in parts)
            {
                d.Dispose();
            }
        }
    }

    [Fact]
    public void SplitByRangesValidatesThem()
    {
        using PdfDocument source = Open(BookFixture.Book("A", 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfDocument.Split(source, [new PdfPageRange(3, 2)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfDocument.Split(source, [new PdfPageRange(0, 0)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfDocument.Split(source, 0));
        IReadOnlyList<PdfDocument> parts = PdfDocument.Split(source, [new PdfPageRange(1, 2), new PdfPageRange(0, 4)]);
        Assert.Equal(["A1", "A2"], Texts(parts[0]));
        Assert.Equal(4, parts[1].PageCount);
        foreach (PdfDocument d in parts)
        {
            d.Dispose();
        }
    }

    [Fact]
    public void ImportingAPageTwiceMakesTwoPages()
    {
        using PdfDocument source = Open(BookFixture.Book("A", 3));
        using PdfDocument target = PdfDocument.CreateEmpty();
        target.ImportPages(source, [0, 0, 1]);
        using PdfDocument again = PageEditTests.Reopen(target);
        Assert.Equal(["A0", "A0", "A1"], Texts(again));
        Assert.NotEqual(again.Pages[0].Dictionary.Reference!.Number, again.Pages[1].Dictionary.Reference!.Number);
        Assert.Equal(["name", "name_2"], again.Form!.Fields.Select(f => f.FullName));
    }

    [Fact]
    public void ImportIntoTheMiddleKeepsTheTargetPagesAndRebuildsLabels()
    {
        using PdfDocument source = Open(BookFixture.Book("S", 2, form: false));
        using PdfDocument target = PdfDocument.Open(BookFixture.Book("T", 3, form: false), new PdfOpenOptions { AllowEditing = true });
        target.ImportPages(source, null, 1);
        using PdfDocument again = PageEditTests.Reopen(target);
        Assert.Equal(["T0", "S0", "S1", "T1", "T2"], Texts(again));
        Assert.Equal(["i", "i", "ii", "ii", "p-5"], Enumerable.Range(0, 5).Select(i => BookFixture.Label(again, i)));
        Assert.Equal(5, again.Outline.Count);
    }

    [Fact]
    public void ImportWithoutFormFieldsOrOutlines()
    {
        using PdfDocument source = Open(BookFixture.Book("A", 2));
        using PdfDocument target = PdfDocument.CreateEmpty();
        target.ImportPages(source, null, -1, new PdfImportOptions { ImportFormFields = false, ImportOutlines = false });
        using PdfDocument again = PageEditTests.Reopen(target);
        Assert.Null(again.Form);
        Assert.Empty(again.Outline);
        Assert.DoesNotContain(again.Pages[0].Annotations, a => a.Subtype == "Widget");
    }

    [Fact]
    public void ImportKeepsTheStructureOfTheImportedPages()
    {
        byte[] tagged = TaggedBook("Tag");
        using PdfDocument source = Open(tagged);
        Assert.True(source.IsTagged);

        // Into a document that is tagged already: both trees are there, with different parent tree keys.
        using PdfDocument target = PdfDocument.Open(tagged, new PdfOpenOptions { AllowEditing = true });
        target.ImportPages(source, [0]);
        using PdfDocument again = PageEditTests.Reopen(target);
        Assert.True(again.IsTagged);
        PdfDictionary root = again.StructureTreeRoot!;
        Assert.Equal(2, root.GetArray("K")!.Count);
        Assert.Equal(2, root.GetDictionary("ParentTree")!.GetArray("Nums")!.Count / 2);
        Assert.NotEqual(again.Pages[0].Dictionary.GetInt("StructParents"), again.Pages[1].Dictionary.GetInt("StructParents"));
        PdfDictionary element = ((PdfDictionary)root.GetArray("K")![1]!);
        Assert.Equal(again.Pages[1].Dictionary.Reference!.Number, ((PdfReference)element.GetRaw("Pg")!).Number);
        Assert.Equal(root.Reference!.Number, ((PdfReference)element.GetRaw("P")!).Number);
        Assert.Equal(["Tag", "Tag"], Texts(again));

        // Into a new document, or with the option off.
        using PdfDocument fresh = PdfDocument.CreateEmpty();
        fresh.ImportPages(source);
        Assert.True(fresh.IsTagged);
        Assert.Equal(Texts(source), Texts(fresh));
        using PdfDocument untagged = PdfDocument.CreateEmpty();
        untagged.ImportPages(source, null, -1, new PdfImportOptions { ImportStructure = false });
        Assert.False(untagged.IsTagged);
        Assert.DoesNotContain("StructTreeRoot", Encoding.Latin1.GetString(WriterSupport.Save(untagged)));
    }

    [Fact]
    public void OnlyTheElementsOfImportedPagesAreKept()
    {
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /StructTreeRoot 8 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 200 200] /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R /Contents 5 0 R /StructParents 0 >>");
        b.Obj(4, "<< /Type /Page /Parent 2 0 R /Contents 6 0 R /StructParents 1 >>");
        b.Stream(5, string.Empty, Encoding.ASCII.GetBytes("/P << /MCID 0 >> BDC BT /F1 12 Tf 10 100 Td (one) Tj ET EMC"));
        b.Stream(6, string.Empty, Encoding.ASCII.GetBytes("/P << /MCID 0 >> BDC BT /F1 12 Tf 10 100 Td (two) Tj ET EMC"));
        b.Obj(8, "<< /Type /StructTreeRoot /K 9 0 R /ParentTree 12 0 R /RoleMap << /Heading /H1 >> >>");
        b.Obj(9, "<< /Type /StructElem /S /Document /P 8 0 R /K [10 0 R 11 0 R] >>");
        b.Obj(10, "<< /Type /StructElem /S /P /P 9 0 R /Pg 3 0 R /K 0 >>");
        b.Obj(11, "<< /Type /StructElem /S /P /P 9 0 R /Pg 4 0 R /K 0 >>");
        b.Obj(12, "<< /Nums [0 [10 0 R] 1 [11 0 R]] >>");
        long x = b.XrefTable(13);
        b.Trailer("/Size 13 /Root 1 0 R", x);
        using PdfDocument source = Open(b.ToArray());
        using PdfDocument target = PdfDocument.CreateEmpty();
        target.ImportPages(source, [1]);
        byte[] bytes = WriterSupport.Save(target);
        using PdfDocument again = Open(bytes);
        PdfDictionary document = (PdfDictionary)again.StructureTreeRoot!.GetArray("K")![0]!;
        PdfArray kids = document.GetArray("K")!;
        PdfDictionary only = Assert.IsType<PdfDictionary>(Assert.Single(kids.Items));
        Assert.Equal(again.Pages[0].Dictionary.Reference!.Number, ((PdfReference)only.GetRaw("Pg")!).Number);
        Assert.Equal(0, only.GetInt("K"));
        Assert.Equal("/H1", again.StructureTreeRoot.GetDictionary("RoleMap")!.GetRaw("Heading")!.ToString());
        PdfArray byMcid = (PdfArray)again.StructureTreeRoot.GetDictionary("ParentTree")!.GetArray("Nums")![1]!;
        Assert.Equal(only.Reference!.Number, ((PdfReference)byMcid.GetRaw(0)).Number);
        Assert.DoesNotContain("(one)", Encoding.Latin1.GetString(bytes));
        Assert.Equal(["two"], Texts(again));
    }

    private static byte[] TaggedBook(string prefix)
    {
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /StructTreeRoot 5 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 200 200] /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R /Contents 4 0 R /StructParents 0 >>");
        b.Stream(4, string.Empty, Encoding.ASCII.GetBytes($"/P << /MCID 0 >> BDC BT /F1 12 Tf 10 100 Td ({prefix}) Tj ET EMC"));
        b.Obj(5, "<< /Type /StructTreeRoot /K [6 0 R] /ParentTree 7 0 R >>");
        b.Obj(6, "<< /Type /StructElem /S /P /P 5 0 R /Pg 3 0 R /K 0 >>");
        b.Obj(7, "<< /Nums [0 [6 0 R]] >>");
        long x = b.XrefTable(8);
        b.Trailer("/Size 8 /Root 1 0 R", x);
        return b.ToArray();
    }

    [Fact]
    public void OptionalContentKeepsItsDefaultVisibility()
    {
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [5 0 R 6 0 R] /D << /OFF [6 0 R] >> >> >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 200 200] >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> /Properties << /On 5 0 R /Off 6 0 R >> >> >>");
        b.Stream(4, string.Empty, Encoding.ASCII.GetBytes("/OC /On BDC BT /F1 12 Tf 10 100 Td (shown) Tj ET EMC /OC /Off BDC BT /F1 12 Tf 10 50 Td (hidden) Tj ET EMC"));
        b.Obj(5, "<< /Type /OCG /Name (On) >>");
        b.Obj(6, "<< /Type /OCG /Name (Off) >>");
        long x = b.XrefTable(7);
        b.Trailer("/Size 7 /Root 1 0 R", x);
        using PdfDocument source = Open(b.ToArray());
        using PdfDocument target = PdfDocument.CreateEmpty();
        target.ImportPages(source);
        using PdfDocument again = PageEditTests.Reopen(target);
        Assert.Equal(["On", "Off"], again.OptionalContentGroups.Select(g => g.Name));
        Assert.Equal([true, false], again.OptionalContentGroups.Select(g => g.IsVisibleByDefault));
        string text = BookFixture.Text(again, 0);
        Assert.Contains("shown", text);
        Assert.DoesNotContain("hidden", text);
    }

    [Fact]
    public void EncryptedSourcesImportAsPlainAndPlainSourcesEncryptOnSave()
    {
        string file = WriterSupport.Generated("enc-chrome-aes-256.pdf");
        string? pw = WriterSupport.PasswordFor(file);
        using PdfDocument encrypted = PdfDocument.Open(file, new PdfOpenOptions { Password = pw });
        using PdfDocument target = PdfDocument.CreateEmpty();
        target.ImportPages(encrypted);
        byte[] plain = WriterSupport.Save(target);
        using (PdfDocument again = Open(plain))
        {
            Assert.False(again.IsEncrypted);
            Assert.Equal(Texts(encrypted), Texts(again));
        }

        using PdfDocument book = Open(BookFixture.Book("A", 2));
        using PdfDocument merged = PdfDocument.Merge([book, encrypted]);
        byte[] bytes = WriterSupport.Save(merged, new PdfSaveOptions
        {
            Encryption = PdfEncryptionAction.Set,
            NewEncryption = new PdfEncryptionOptions { UserPassword = "u", OwnerPassword = "o" },
        });
        Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(bytes));
        using PdfDocument reopened = PdfDocument.Open(bytes, new PdfOpenOptions { Password = "u" });
        Assert.True(reopened.IsEncrypted);
        Assert.Equal(["A0", "A1", .. Texts(encrypted)], Texts(reopened));
        (int code, string output) = WriterSupport.QpdfRun(bytes, "u");
        Assert.True(code == 0, output);
    }

    [Fact]
    public void ImportFromAnEditedSourceSeesTheEdit()
    {
        using PdfDocument source = PdfDocument.Open(BookFixture.Book("A", 2), new PdfOpenOptions { AllowEditing = true });
        source.ReorderPages([1, 0]);
        using PdfDocument target = PdfDocument.CreateEmpty();
        target.ImportPages(source);
        Assert.Equal(["A1", "A0"], Texts(target));
        source.RemovePage(0);
        target.ImportPages(source);
        Assert.Equal(["A1", "A0", "A0"], Texts(target));
    }

    [Fact]
    public void ImportedPagesKeepTheirRotationAndInheritedValues()
    {
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /Rotate 90 /MediaBox [0 0 400 300] /CropBox [10 10 300 200] >>");
        b.Obj(3, "<< /Type /Pages /Parent 2 0 R /Kids [5 0 R] /Count 1 /Rotate 180 >>");
        b.Obj(4, "<< /Type /Page /Parent 2 0 R >>");
        b.Obj(5, "<< /Type /Page /Parent 3 0 R >>");
        long x = b.XrefTable(6);
        b.Trailer("/Size 6 /Root 1 0 R", x);
        using PdfDocument source = Open(b.ToArray());
        using PdfDocument merged = PdfDocument.Merge([source, source]);
        Assert.Equal([180, 90, 180, 90], merged.Pages.Select(p => p.Rotate));
        Assert.Equal([400, 400, 400, 400], merged.Pages.Select(p => p.MediaBox.Width));
        Assert.Equal([290, 290, 290, 290], merged.Pages.Select(p => p.CropBox.Width));
    }
}
