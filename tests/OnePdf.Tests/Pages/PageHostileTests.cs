using System.Text;
using OnePdf.Tests.Writing;

namespace OnePdf.Tests.Pages;

/// <summary>Page operations on documents built to cause trouble: cycles, enormous graphs, self-reference, depth.</summary>
public class PageHostileTests
{
    private static PdfDocument Open(byte[] bytes, bool edit = false) => PdfDocument.Open(bytes, new PdfOpenOptions { AllowEditing = edit });

    // A reference to a number the file never defines used to resolve to the object a copy had just reserved under that number,
    // so duplicating a page within its own document followed one new object after another until memory ran out.
    [Fact]
    public void DuplicatingWithDanglingReferenceToTheNextObjectNumberTerminates()
    {
        byte[] input = File.ReadAllBytes(Path.Combine(WriterSupport.Root(), "testdata", "repros", "pages-duplicate-dangling-reference.pdf"));
        using PdfDocument edit = Open(input, edit: true);
        for (int i = 0; i < 2; i++)
        {
            edit.DuplicatePage(0);
        }

        Assert.Equal(3, edit.PageCount);
        byte[] bytes = WriterSupport.Save(edit);
        Assert.True(bytes.Length < 10000, "a copy must not grow beyond the page and its annotation");
        using PdfDocument again = Open(bytes);
        Assert.Equal(3, again.PageCount);
    }

    [Fact]
    public void CyclicPageTreesStillMergeAndEdit()
    {
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R 2 0 R 4 0 R 5 0 R] /Count 3 /Parent 2 0 R /MediaBox [0 0 100 100] >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R >>");
        b.Obj(4, "<< /Type /Page /Parent 4 0 R >>");
        b.Obj(5, "<< /Type /Pages /Parent 6 0 R /Kids [7 0 R] /Count 1 >>");
        b.Obj(6, "<< /Type /Pages /Parent 5 0 R /Kids [5 0 R] /Count 1 >>");
        b.Obj(7, "<< /Type /Page /Parent 5 0 R >>");
        long x = b.XrefTable(8);
        b.Trailer("/Size 8 /Root 1 0 R", x);
        byte[] input = b.ToArray();
        using PdfDocument source = Open(input);
        int count = source.PageCount;
        Assert.InRange(count, 2, 3);
        using PdfDocument merged = PdfDocument.Merge([source, source]);
        Assert.Equal(2 * count, merged.PageCount);
        byte[] bytes = WriterSupport.Save(merged);
        using PdfDocument again = Open(bytes);
        Assert.Equal(2 * count, again.PageCount);

        using PdfDocument edit = Open(input, edit: true);
        edit.ReorderPages([.. Enumerable.Range(0, count).Reverse()]);
        edit.RemovePage(0);
        edit.DuplicatePage(0);
        Assert.Equal(count, edit.PageCount);
        using PdfDocument reopened = Open(WriterSupport.Save(edit));
        Assert.Equal(count, reopened.PageCount);
    }

    [Fact]
    public void SelfReferencingAnnotationsAreCopiedOnceAndTerminate()
    {
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 100 100] >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R /Annots [4 0 R 5 0 R 6 0 R] >>");
        b.Obj(4, "<< /Type /Annot /Subtype /Text /Rect [1 1 20 20] /Popup 4 0 R /Parent 4 0 R /IRT 4 0 R /P 3 0 R /Contents (loop) >>");
        b.Obj(5, "<< /Type /Annot /Subtype /Link /Rect [1 30 20 40] /A 7 0 R /Dest [3 0 R /Fit] >>");
        b.Obj(6, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (a) /Rect [1 50 20 60] /Parent 8 0 R /P 3 0 R >>");
        b.Obj(7, "<< /S /GoTo /D [3 0 R /Fit] /Next 7 0 R >>");
        b.Obj(8, "<< /T (b) /FT /Tx /Parent 9 0 R /Kids [6 0 R] >>");
        b.Obj(9, "<< /T (c) /Parent 8 0 R /Kids [8 0 R] >>");
        long x = b.XrefTable(10);
        b.Trailer("/Size 10 /Root 1 0 R", x);
        using PdfDocument source = Open(b.ToArray());
        using PdfDocument target = PdfDocument.CreateEmpty();
        target.ImportPages(source, [0, 0]);
        using PdfDocument again = Open(WriterSupport.Save(target));
        Assert.Equal(2, again.PageCount);
        PdfAnnotation text = again.Pages[0].Annotations.First(a => a.Subtype == "Text");
        Assert.Equal(text.Dictionary.Reference!.Number, ((PdfReference)text.Dictionary.GetRaw("Popup")!).Number);
        PdfAnnotation text2 = again.Pages[1].Annotations.First(a => a.Subtype == "Text");
        Assert.NotEqual(text.Dictionary.Reference.Number, text2.Dictionary.Reference!.Number);
        Assert.Equal(again.Pages[0].Dictionary.Reference!.Number, ((PdfReference)text.Dictionary.GetRaw("P")!).Number);
        _ = again.Form; // the looping field chain reads without hanging
    }

    [Fact]
    public void AMillionObjectResourceGraphIsCopiedIteratively()
    {
        const int count = 1_000_000;
        byte[] bytes = ChainDocument(count);
        using PdfDocument source = Open(bytes);
        using PdfDocument target = PdfDocument.CreateEmpty();
        target.ImportPages(source);
        Assert.True(target.ObjectCount > count);
        byte[] saved = WriterSupport.Save(target, new PdfSaveOptions { XrefFormat = PdfXrefFormat.Stream });
        using PdfDocument again = Open(saved);
        Assert.Equal(1, again.PageCount);
        Assert.True(again.ObjectCount > count);
    }

    [Fact]
    public void ALargeGraphBeyondTheObjectLimitFailsWithATypedExceptionAndLeavesTheTargetUsable()
    {
        byte[] bytes = ChainDocument(50_000);
        using PdfDocument source = Open(bytes);
        using PdfDocument target = PdfDocument.CreateEmpty(new PdfOpenOptions { Limits = PdfLimits.Default with { MaxObjects = 10_000 } });
        Assert.Throws<PdfLimitException>(() => target.ImportPages(source));
        Assert.Equal(0, target.PageCount);
        using PdfDocument other = Open(BookFixture.Book("A", 2));
        target.ImportPages(other);
        Assert.Equal(2, target.PageCount);
        // The failed import left nothing half-copied to be reused: a second try fails the same way.
        Assert.Throws<PdfLimitException>(() => target.ImportPages(source));
    }

    // A page whose resources lead into a chain of `count` dictionaries, each referring to the next.
    private static byte[] ChainDocument(int count)
    {
        var sb = new StringBuilder(count * 32);
        var offsets = new long[count + 10];
        sb.Append("%PDF-1.7\n");
        void Obj(int n, string body)
        {
            offsets[n] = sb.Length;
            sb.Append(n).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 100 100] >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /Resources << /ExtGState << /G 4 0 R >> >> >>");
        for (int i = 0; i < count; i++)
        {
            Obj(4 + i, i + 1 < count ? $"<< /Next {5 + i} 0 R >>" : "<< >>");
        }

        int size = count + 4;
        long xref = sb.Length;
        sb.Append("xref\n0 ").Append(size).Append("\n0000000000 65535 f \n");
        for (int i = 1; i < size; i++)
        {
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        }

        sb.Append("trailer\n<< /Size ").Append(size).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    [Fact]
    public void ValuesNestedBeyondTheLimitAreRefused()
    {
        using PdfDocument source = Open(BookFixture.Book("A", 1), edit: true);
        var deep = new PdfArray();
        PdfArray cur = deep;
        for (int i = 0; i < 1000; i++)
        {
            var next = new PdfArray();
            cur.Append(next);
            cur = next;
        }

        source.Pages[0].Dictionary.SetEntry("Deep", deep);
        using PdfDocument target = PdfDocument.CreateEmpty();
        Assert.Throws<PdfLimitException>(() => target.ImportPages(source));
        Assert.Equal(0, target.PageCount);
    }

    [Fact]
    public void EmptyDestinationArraysLeadNowhere()
    {
        // Found by fuzzing: a bookmark and a link whose destination array is empty.
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 100 100] >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R /Annots [6 0 R] >>");
        b.Obj(4, "<< /Type /Outlines /First 5 0 R /Last 5 0 R /Count 1 >>");
        b.Obj(5, "<< /Title (empty) /Parent 4 0 R /Dest [] >>");
        b.Obj(6, "<< /Type /Annot /Subtype /Link /Rect [0 0 5 5] /Dest [] >>");
        long x = b.XrefTable(7);
        b.Trailer("/Size 7 /Root 1 0 R", x);
        byte[] input = b.ToArray();
        using PdfDocument source = Open(input);
        using PdfDocument target = PdfDocument.CreateEmpty();
        target.ImportPages(source);
        Assert.Empty(target.Outline);
        using PdfDocument edit = Open(input, edit: true);
        edit.RemovePage(0);
        Assert.Equal(0, edit.PageCount);
    }

    [Fact]
    public void APageTreeRootThatReadsAsAPageStillRebuilds()
    {
        // Found by fuzzing: a damaged root dictionary has no /Kids, so the reader counts it as the one page of the file.
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Obj(2, "<< 1 /Pages /Kids [3 0 R] /Count 1 >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>");
        long x = b.XrefTable(4);
        b.Trailer("/Size 4 /Root 1 0 R", x);
        using PdfDocument doc = Open(b.ToArray(), edit: true);
        int before = doc.Pages.Count;
        doc.InsertBlankPage(0, 50, 60);
        Assert.Equal(before + 1, doc.Pages.Count);
        Assert.Equal(doc.Pages.Count, doc.PageCount);
        using PdfDocument again = Open(WriterSupport.Save(doc));
        Assert.Equal(before + 1, again.PageCount);
    }

    [Fact]
    public void ACyclicOutlineAndDestinationChainsTerminate()
    {
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R /Dests 7 0 R /Names << /Dests << /Names [(a) (b) (b) (a)] >> >> >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 100 100] /Parent 2 0 R >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R /Annots [8 0 R] >>");
        b.Obj(4, "<< /Type /Outlines /First 5 0 R /Last 6 0 R /Count 2 >>");
        b.Obj(5, "<< /Title (one) /Parent 4 0 R /Next 6 0 R /Prev 6 0 R /Dest (a) >>");
        b.Obj(6, "<< /Title (two) /Parent 4 0 R /Next 5 0 R /Prev 5 0 R /First 5 0 R /Count 1 >>");
        b.Obj(7, "<< /a (b) /b (a) >>");
        b.Obj(8, "<< /Type /Annot /Subtype /Link /Rect [0 0 5 5] /Dest (a) >>");
        long x = b.XrefTable(9);
        b.Trailer("/Size 9 /Root 1 0 R", x);
        using PdfDocument source = Open(b.ToArray());
        using PdfDocument target = PdfDocument.CreateEmpty();
        target.ImportPages(source);
        using PdfDocument again = Open(WriterSupport.Save(target));
        Assert.Equal(1, again.PageCount);
        Assert.Null(again.Pages[0].Links[0].Destination);

        using PdfDocument edit = Open(b.ToArray(), edit: true);
        edit.RemovePage(0);
        Assert.Equal(0, edit.PageCount);
    }
}
