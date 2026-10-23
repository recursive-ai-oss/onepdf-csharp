using System.Text;
using OnePdf.Tests.Writing;

namespace OnePdf.Tests.Pages;

/// <summary>Reordering, deleting, rotating, inserting and duplicating pages, and what follows the pages: bookmarks, links, names, labels, forms.</summary>
public class PageEditTests
{
    private static PdfDocument Edit(byte[] bytes) => PdfDocument.Open(bytes, new PdfOpenOptions { AllowEditing = true });

    // Saves and reads the result back, with qpdf's opinion.
    internal static PdfDocument Reopen(PdfDocument doc, byte[]? input = null, PdfXrefFormat form = PdfXrefFormat.Table)
    {
        byte[] bytes = WriterSupport.Save(doc, new PdfSaveOptions { XrefFormat = form });
        if (input is not null)
        {
            WriterSupport.AssertNoWorse(input, bytes, null, "page edit");
        }
        else
        {
            (int code, string text) = WriterSupport.QpdfRun(bytes);
            Assert.True(code == 0, "qpdf: " + text);
        }

        return PdfDocument.Open(bytes, new PdfOpenOptions { AllowEditing = true });
    }

    private static string[] Texts(PdfDocument d) => [.. Enumerable.Range(0, d.Pages.Count).Select(i => BookFixture.Text(d, i))];

    [Fact]
    public void ReorderFollowsLinksBookmarksAndLabels()
    {
        byte[] src = BookFixture.Book("A", 4);
        using PdfDocument doc = Edit(src);
        doc.ReorderPages([2, 0, 3, 1]);
        Assert.Equal(["A2", "A0", "A3", "A1"], Texts(doc));
        using PdfDocument again = Reopen(doc, src);
        Assert.Equal(["A2", "A0", "A3", "A1"], Texts(again));
        Assert.Equal(["p-5", "i", "p-6", "ii"], Enumerable.Range(0, 4).Select(i => BookFixture.Label(again, i)));

        // The link on the first page (formerly page 2) led to page 3, now at index 2.
        Assert.Equal(2, BookFixture.Target(again.Pages[0].Links[0].Destination));
        Assert.Equal(2, BookFixture.Target(again.Pages[0].Links[1].Action?.Destination));

        // Bookmarks stay in their order and follow their pages.
        Assert.Equal(["A chapter 0", "A chapter 1", "A chapter 2", "A chapter 3"], again.Outline.Select(o => o.Title));
        Assert.Equal([1, 3, 0, 2], again.Outline.Select(o => BookFixture.Target(o.Destination)));
        Assert.Equal(2, BookFixture.Target(again.GetNamedDestination("An3")));
    }

    [Fact]
    public void MovePageShiftsTheOthers()
    {
        using PdfDocument doc = Edit(BookFixture.Book("A", 4));
        doc.MovePage(0, 3);
        Assert.Equal(["A1", "A2", "A3", "A0"], Texts(doc));
        doc.MovePage(3, 1);
        Assert.Equal(["A1", "A0", "A2", "A3"], Texts(doc));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.MovePage(0, 4));
        Assert.Throws<ArgumentException>(() => doc.ReorderPages([0, 0, 1, 2]));
        Assert.Throws<ArgumentException>(() => doc.ReorderPages([0, 1]));
    }

    [Fact]
    public void DeleteRepairsEverythingThatPointedAtThePage()
    {
        byte[] src = BookFixture.Book("Zebra", 4);
        using PdfDocument doc = Edit(src);
        doc.RemovePage(1);
        Assert.Equal(["Zebra0", "Zebra2", "Zebra3"], Texts(doc));

        byte[] bytes = WriterSupport.Save(doc);
        Assert.DoesNotContain("Zebra1", Encoding.Latin1.GetString(bytes));
        Assert.Contains("Zebra2", Encoding.Latin1.GetString(bytes));
        using PdfDocument again = Reopen(doc, src);
        Assert.Equal(["Zebra0", "Zebra2", "Zebra3"], Texts(again));
        Assert.Equal(["i", "p-5", "p-6"], Enumerable.Range(0, 3).Select(i => BookFixture.Label(again, i)));

        // Zebra0's links led to the deleted page: they stay as rectangles that go nowhere.
        Assert.Null(again.Pages[0].Links[0].Destination);
        Assert.Null(again.Pages[0].Links[1].Action);
        Assert.Equal(0, BookFixture.Target(again.Pages[2].Links[0].Destination));
        Assert.Equal(0, BookFixture.Target(again.Pages[2].Links[1].Action?.Destination));

        // The bookmark of the deleted page is gone, the others point at their moved pages.
        Assert.Equal(["Zebra chapter 0", "Zebra chapter 2", "Zebra chapter 3"], again.Outline.Select(o => o.Title));
        Assert.Equal([0, 1, 2], again.Outline.Select(o => BookFixture.Target(o.Destination)));
        Assert.DoesNotContain("Zebran1", again.NamedDestinationNames);
        Assert.Contains("Zebran2", again.NamedDestinationNames);
        Assert.Equal(1, BookFixture.Target(again.GetNamedDestination("Zebran2")));
    }

    [Fact]
    public void DeletingAHeadingsPageKeepsTheHeadingWhenItHasKids()
    {
        // A two-level outline: item 0 (page 0) has a child that goes to page 1.
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R /Outlines 6 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 100 100] >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R >>");
        b.Obj(4, "<< /Type /Page /Parent 2 0 R >>");
        b.Obj(6, "<< /Type /Outlines /First 7 0 R /Last 7 0 R /Count 2 >>");
        b.Obj(7, "<< /Title (Parent) /Parent 6 0 R /First 8 0 R /Last 8 0 R /Count 1 /Dest [3 0 R /Fit] >>");
        b.Obj(8, "<< /Title (Child) /Parent 7 0 R /Dest [4 0 R /Fit] >>");
        long x = b.XrefTable(9);
        b.Trailer("/Size 9 /Root 1 0 R", x);
        byte[] input = b.ToArray();
        using PdfDocument doc = Edit(input);
        doc.RemovePage(0);
        using PdfDocument again = Reopen(doc, input);
        PdfOutlineItem heading = Assert.Single(again.Outline);
        Assert.Equal("Parent", heading.Title);
        Assert.Null(heading.Destination);
        PdfOutlineItem child = Assert.Single(heading.Children);
        Assert.Equal(0, BookFixture.Target(child.Destination));
        Assert.Equal(1, heading.Dictionary.GetInt("Count"));
    }

    [Fact]
    public void DeletingThePageOfAFormFieldRemovesTheField()
    {
        using PdfDocument doc = Edit(BookFixture.Book("A", 3));
        Assert.Equal("name", Assert.Single(doc.Form!.Fields).FullName);
        doc.RemovePage(0);
        using PdfDocument again = Reopen(doc);
        Assert.Empty(again.Form!.Fields);
        Assert.DoesNotContain("A-value", Encoding.Latin1.GetString(WriterSupport.Save(doc)));
    }

    [Fact]
    public void DeleteEverythingLeavesAnEmptyDocument()
    {
        using PdfDocument doc = Edit(BookFixture.Book("A", 3));
        doc.RemovePages([0, 1, 2]);
        Assert.Equal(0, doc.PageCount);
        using PdfDocument again = PdfDocument.Open(WriterSupport.Save(doc));
        Assert.Equal(0, again.PageCount);
        Assert.Empty(again.Outline);
        Assert.Null(again.PageLabels);
    }

    [Fact]
    public void DeletingDropsTheStructureTreeWithADiagnostic()
    {
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /StructTreeRoot 5 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 100 100] >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R >>");
        b.Obj(4, "<< /Type /Page /Parent 2 0 R >>");
        b.Obj(5, "<< /Type /StructTreeRoot /K [6 0 R] >>");
        b.Obj(6, "<< /Type /StructElem /S /P /P 5 0 R /Pg 3 0 R >>");
        long x = b.XrefTable(7);
        b.Trailer("/Size 7 /Root 1 0 R", x);
        using PdfDocument doc = Edit(b.ToArray());
        Assert.True(doc.IsTagged);
        doc.RemovePage(0);
        Assert.False(doc.IsTagged);
        Assert.Null(doc.StructureTreeRoot);
        Assert.Contains(doc.Diagnostics, d => d.Severity == PdfSeverity.Info && d.Message.Contains("structure tree"));
    }

    [Fact]
    public void RotateAddsToTheCurrentAngle()
    {
        byte[] src = BookFixture.Book("A", 3);
        using PdfDocument doc = Edit(src);
        doc.RotatePage(1, 90);
        doc.RotatePage(1, 180);
        doc.RotatePage(2, -90);
        doc.SetPageRotation(0, 450);
        Assert.Equal([90, 270, 270], doc.Pages.Select(p => p.Rotate));
        Assert.Throws<ArgumentException>(() => doc.RotatePage(0, 45));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.RotatePage(3, 90));
        using PdfDocument again = Reopen(doc, src);
        Assert.Equal([90, 270, 270], again.Pages.Select(p => p.Rotate));
        Assert.Equal(["A0", "A1", "A2"], Texts(again));
    }

    [Fact]
    public void InsertBlankPageKeepsTheNumbering()
    {
        byte[] src = BookFixture.Book("A", 3);
        using PdfDocument doc = Edit(src);
        PdfPage blank = doc.InsertBlankPage(1, 200, 100);
        Assert.Equal(1, blank.Index);
        Assert.Equal(200, blank.MediaBox.Width);
        using PdfDocument again = Reopen(doc, src);
        Assert.Equal(4, again.PageCount);
        Assert.Equal(["A0", string.Empty, "A1", "A2"], Texts(again));
        Assert.Equal(200, again.Pages[1].MediaBox.Width);
        Assert.Equal(300, again.Pages[2].MediaBox.Width);
        // The pages after the new one keep their own labels; the new one is numbered from its own place in the source (page 1).
        Assert.Equal(["i", "1", "ii", "p-5"], Enumerable.Range(0, 4).Select(i => BookFixture.Label(again, i)));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.InsertBlankPage(5, 10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.InsertBlankPage(0, 0, 10));
    }

    [Fact]
    public void DuplicateGivesThePageItsOwnAnnotationsAndSharesTheContent()
    {
        byte[] src = BookFixture.Book("A", 3);
        using PdfDocument doc = Edit(src);
        PdfPage copy = doc.DuplicatePage(0);
        Assert.Equal(1, copy.Index);
        using PdfDocument again = Reopen(doc, src);
        Assert.Equal(["A0", "A0", "A1", "A2"], Texts(again));
        PdfDictionary a = again.Pages[0].Dictionary;
        PdfDictionary b = again.Pages[1].Dictionary;
        Assert.NotEqual(a.Reference!.Number, b.Reference!.Number);
        Assert.Equal(((PdfReference)a.GetRaw("Contents")!).Number, ((PdfReference)b.GetRaw("Contents")!).Number);
        Assert.NotEqual(((PdfReference)a.GetArray("Annots")!.GetRaw(0)).Number, ((PdfReference)b.GetArray("Annots")!.GetRaw(0)).Number);

        // Both links go to the page that followed the original; the copy's does not lead to itself.
        Assert.Equal(2, BookFixture.Target(again.Pages[0].Links[0].Destination));
        Assert.Equal(2, BookFixture.Target(again.Pages[1].Links[0].Destination));

        // The form field of the copied page is a second field of the same document.
        Assert.Equal(["name", "name_2"], again.Form!.Fields.Select(f => f.FullName));

        // Bookmarks are not copied with a duplicate.
        Assert.Equal(3, again.Outline.Count);
    }

    [Fact]
    public void EditedDocumentsSaveIncrementally()
    {
        byte[] src = BookFixture.Book("A", 3);
        using PdfDocument doc = Edit(src);
        doc.ReorderPages([2, 1, 0]);
        doc.RemovePage(1);
        byte[] bytes = WriterSupport.SaveIncremental(doc);
        using PdfDocument again = PdfDocument.Open(bytes);
        Assert.Equal(["A2", "A0"], Texts(again));
        Assert.True(bytes.AsSpan(0, src.Length).SequenceEqual(src));
    }

    [Fact]
    public void ReadOnlyDocumentsRefuseEditing()
    {
        using PdfDocument doc = PdfDocument.Open(BookFixture.Book("A", 2));
        Assert.Throws<InvalidOperationException>(() => doc.RemovePage(0));
        Assert.Throws<InvalidOperationException>(() => doc.MovePage(0, 1));
        Assert.Throws<InvalidOperationException>(() => doc.RotatePage(0, 90));
        Assert.Throws<InvalidOperationException>(() => doc.InsertBlankPage(0, 10, 10));
        Assert.Throws<InvalidOperationException>(() => doc.DuplicatePage(0));
        using PdfDocument target = PdfDocument.Open(BookFixture.Book("B", 2));
        Assert.Throws<InvalidOperationException>(() => target.ImportPages(doc));
    }

    [Fact]
    public void ManyPagesGetABalancedTree()
    {
        using PdfDocument doc = PdfDocument.CreateEmpty();
        for (int i = 0; i < 1100; i++)
        {
            doc.InsertBlankPage(i, 100 + i, 100);
        }

        Assert.Equal(1100, doc.PageCount);
        using PdfDocument again = Reopen(doc);
        Assert.Equal(1100, again.PageCount);
        Assert.Equal(100 + 777, again.Pages[777].MediaBox.Width);
        PdfDictionary root = again.Catalog.GetDictionary("Pages")!;
        Assert.True(root.GetArray("Kids")!.Count <= 32);
        Assert.Equal(1100, root.GetInt("Count"));
    }
}
