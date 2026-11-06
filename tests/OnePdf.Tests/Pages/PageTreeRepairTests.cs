using System.Text;

namespace OnePdf.Tests;

/// <summary>
/// A page tree that lists entries that are not pages (null or missing kids, other dictionaries, cycles, wrong counts) saves as a
/// tree of only the pages that exist, and the lost entries are reported after open.
/// </summary>
public class PageTreeRepairTests
{
    // Objects 1 (catalog) and 2 (root) come from the test; 3..n are given. The table lists every object that is defined.
    private static byte[] Build(string root, params string[] others)
    {
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Obj(2, root);
        for (int i = 0; i < others.Length; i++)
        {
            b.Obj(3 + i, others[i]);
        }

        int size = 3 + others.Length + 8;
        long x = b.XrefTable(size);
        b.Trailer($"/Size {size} /Root 1 0 R", x);
        return b.ToArray();
    }

    private const string Page = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>";

    private static byte[] SaveTable(PdfDocument doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms, new PdfSaveOptions { XrefFormat = PdfXrefFormat.Table });
        return ms.ToArray();
    }

    // Walks a saved file's page tree through the COS layer: every kid is a dictionary, every /Count is the number of pages below.
    private static int CheckTree(PdfDocument saved)
    {
        PdfDictionary root = saved.Catalog.GetDictionary("Pages")!;
        (int pages, bool ok) = Walk(root, 0);
        Assert.True(ok);
        return pages;

        static (int, bool) Walk(PdfDictionary node, int depth)
        {
            Assert.True(depth < 20);
            PdfArray? kids = node.GetArray("Kids");
            if (kids is null)
            {
                Assert.Equal("Page", node.GetName("Type"));
                return (1, true);
            }

            int total = 0;
            for (int i = 0; i < kids.Count; i++)
            {
                PdfDictionary child = Assert.IsAssignableFrom<PdfDictionary>(kids[i]);
                total += Walk(child, depth + 1).Item1;
            }

            Assert.Equal(total, (int)node.GetInt("Count", -1));
            return (total, true);
        }
    }

    private static void AssertSavedConsistent(PdfDocument doc, int expectedPages)
    {
        Assert.Equal(expectedPages, doc.Pages.Count);
        byte[] saved = SaveTable(doc);
        using PdfDocument again = PdfDocument.Open(saved);
        Assert.Equal(expectedPages, again.PageCount);
        Assert.Equal(expectedPages, CheckTree(again));
        Assert.Empty(again.LostPages);
        Assert.DoesNotContain(again.Diagnostics, static d => d.Message.Contains("page tree", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(again.Diagnostics);
        Assert.DoesNotContain("null", Encoding.Latin1.GetString(saved).Replace("/Null", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void SoundTreeHasNoLostPagesAndSavesAsIs()
    {
        using PdfDocument doc = PdfDocument.Open(Build("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>", Page, Page));
        Assert.Empty(doc.LostPages);
        AssertSavedConsistent(doc, 2);
    }

    [Fact]
    public void NullKid()
    {
        using PdfDocument doc = PdfDocument.Open(Build("<< /Type /Pages /Kids [3 0 R null 4 0 R] /Count 3 >>", Page, Page));
        PdfLostPage lost = Assert.Single(doc.LostPages);
        Assert.Equal(1, lost.Index);
        Assert.Equal(PdfLostPageReason.Null, lost.Reason);
        Assert.Null(lost.ObjectNumber);
        Assert.Equal(2, lost.ParentObjectNumber);
        AssertSavedConsistent(doc, 2);
    }

    [Fact]
    public void KidPointingAtAMissingObject()
    {
        using PdfDocument doc = PdfDocument.Open(Build("<< /Type /Pages /Kids [3 0 R 9 0 R 4 0 R] /Count 3 >>", Page, Page));
        PdfLostPage lost = Assert.Single(doc.LostPages);
        Assert.Equal(1, lost.Index);
        Assert.Equal(PdfLostPageReason.MissingObject, lost.Reason);
        Assert.Equal(9, lost.ObjectNumber);
        AssertSavedConsistent(doc, 2);
    }

    [Fact]
    public void KidThatIsNotAPage()
    {
        using PdfDocument doc = PdfDocument.Open(Build(
            "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R 6 0 R] /Count 4 >>", Page, "<< /Type /Font /BaseFont /Helvetica /Subtype /Type1 >>", Page, "42"));
        Assert.Equal(2, doc.LostPages.Count);
        Assert.Equal(PdfLostPageReason.NotAPage, doc.LostPages[0].Reason);
        Assert.Equal(4, doc.LostPages[0].ObjectNumber);
        Assert.Equal(1, doc.LostPages[0].Index);
        Assert.Equal(PdfLostPageReason.NotADictionary, doc.LostPages[1].Reason);
        Assert.Equal(3, doc.LostPages[1].Index);
        AssertSavedConsistent(doc, 2);
    }

    [Fact]
    public void NestedNodeWithWrongCountsAndInheritedAttributes()
    {
        // The root claims 9 pages, the inner node 7; only 3 exist, one of them behind a missing kid of the inner node.
        const string Inner = "<< /Type /Pages /Parent 2 0 R /Kids [4 0 R 9 0 R 5 0 R] /Count 7 /Rotate 180 >>";
        using PdfDocument doc = PdfDocument.Open(Build(
            "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 9 /MediaBox [0 0 300 400] /Rotate 90 >>",
            "<< /Type /Page /Parent 2 0 R >>",
            "<< /Type /Page /Parent 6 0 R >>",
            "<< /Type /Page /Parent 6 0 R /Rotate 0 /MediaBox [0 0 50 60] >>",
            Inner));
        Assert.Equal(3, doc.Pages.Count);
        PdfLostPage lost = Assert.Single(doc.LostPages);
        Assert.Equal(PdfLostPageReason.MissingObject, lost.Reason);
        Assert.Equal(2, lost.Index);
        Assert.Equal(6, lost.ParentObjectNumber);

        byte[] saved = SaveTable(doc);
        using PdfDocument again = PdfDocument.Open(saved);
        Assert.Equal(3, CheckTree(again));
        Assert.Empty(again.Diagnostics);
        Assert.Equal(90, again.Pages[0].Rotate);
        Assert.Equal(180, again.Pages[1].Rotate);
        Assert.Equal(0, again.Pages[2].Rotate);
        Assert.Equal(300, again.Pages[0].MediaBox.Width, 3);
        Assert.Equal(300, again.Pages[1].MediaBox.Width, 3);
        Assert.Equal(50, again.Pages[2].MediaBox.Width, 3);
    }

    [Fact]
    public void KidCycleAndRepeatedPage()
    {
        // Node 5 lists the root again; page 3 is listed twice.
        using PdfDocument doc = PdfDocument.Open(Build(
            "<< /Type /Pages /Kids [3 0 R 5 0 R 3 0 R] /Count 3 >>", Page, Page, "<< /Type /Pages /Parent 2 0 R /Kids [4 0 R 2 0 R] /Count 2 >>"));
        Assert.Equal(2, doc.Pages.Count);
        Assert.Equal(2, doc.LostPages.Count);
        Assert.All(doc.LostPages, static l => Assert.Equal(PdfLostPageReason.Repeated, l.Reason));
        Assert.Equal(2, doc.LostPages[0].Index);
        Assert.Equal(3, doc.LostPages[1].Index);
        AssertSavedConsistent(doc, 2);
    }

    [Fact]
    public void NodeLeftWithNoPageIsDropped()
    {
        using PdfDocument doc = PdfDocument.Open(Build(
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>", Page, "<< /Type /Pages /Parent 2 0 R /Kids [null 8 0 R] /Count 2 >>"));
        Assert.Single(doc.Pages);
        Assert.Equal(2, doc.LostPages.Count);
        AssertSavedConsistent(doc, 1);
    }

    [Fact]
    public void WrongCountAloneIsFixedOnFullSave()
    {
        using PdfDocument doc = PdfDocument.Open(Build("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 7 >>", Page, Page));
        Assert.Empty(doc.LostPages);
        AssertSavedConsistent(doc, 2);
    }

    [Fact]
    public void ObjectStreamAndPdfASavesWriteTheSameTree()
    {
        byte[] input = Build("<< /Type /Pages /Kids [3 0 R null 4 0 R] /Count 3 >>", Page, Page);
        using PdfDocument doc = PdfDocument.Open(input);
        using var ms = new MemoryStream();
        doc.Save(ms, new PdfSaveOptions { XrefFormat = PdfXrefFormat.Stream, UseObjectStreams = true });
        using PdfDocument again = PdfDocument.Open(ms.ToArray());
        Assert.Equal(2, CheckTree(again));
        Assert.Empty(again.Diagnostics);
    }

    [Fact]
    public void IncrementalSaveRefusesALostPageAndAcceptsASoundTree()
    {
        using PdfDocument damaged = PdfDocument.Open(Build("<< /Type /Pages /Kids [3 0 R null 4 0 R] /Count 3 >>", Page, Page));
        Assert.Throws<PdfFormatException>(() => damaged.SaveIncremental(new MemoryStream()));

        using PdfDocument sound = PdfDocument.Open(Build("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 5 >>", Page, Page));
        using var ms = new MemoryStream();
        sound.SaveIncremental(ms);
        Assert.True(ms.Length > 0);
    }

    [Fact]
    public void EditingARepairedDocumentClearsTheReport()
    {
        using PdfDocument doc = PdfDocument.Open(
            Build("<< /Type /Pages /Kids [3 0 R null 4 0 R] /Count 3 >>", Page, Page), new PdfOpenOptions { AllowEditing = true });
        Assert.Single(doc.LostPages);
        doc.RotatePage(0, 90);
        Assert.Single(doc.LostPages);
        doc.InsertBlankPage(0, 100, 100);
        Assert.Empty(doc.LostPages);
        Assert.Equal(3, doc.PageCount);
        using var ms = new MemoryStream();
        doc.SaveIncremental(ms);
        using PdfDocument again = PdfDocument.Open(ms.ToArray());
        Assert.Equal(3, CheckTree(again));
    }

    [Fact]
    public void DamagedFormW9LosesOnePageAndSavesConsistently()
    {
        string path = Path.Combine(TestData.Root(), "repros", "page-tree-w9-damaged.pdf");
        using PdfDocument doc = PdfDocument.Open(path);
        Assert.Equal(5, doc.PageCount);
        Assert.Equal(5, doc.Pages.Count);
        PdfLostPage lost = Assert.Single(doc.LostPages);
        Assert.Equal(PdfLostPageReason.MissingObject, lost.Reason);
        Assert.Equal(5, lost.ObjectNumber);
        Assert.Equal(3, lost.Index);

        byte[] saved = SaveTable(doc);
        Assert.Contains("/Count 5", Encoding.Latin1.GetString(saved), StringComparison.Ordinal);
        using PdfDocument again = PdfDocument.Open(saved);
        Assert.Equal(5, again.PageCount);
        Assert.Equal(5, CheckTree(again));
        Assert.Empty(again.LostPages);
        Assert.Empty(again.Diagnostics);
        Assert.Throws<PdfFormatException>(() => doc.SaveIncremental(new MemoryStream()));
    }
}
