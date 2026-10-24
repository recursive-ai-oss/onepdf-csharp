using System.Text;

namespace OnePdf.Tests;

/// <summary>Structure destinations (ISO 32000-2 12.3.2.3) report the page of the structure element's content.</summary>
public class StructureDestinationTests
{
    // Pages 3, 4, 5. Structure elements:
    //   10: /Pg page 4 of its own                       -> page 1
    //   11: no /Pg; a marked-content reference on page 5 -> page 2
    //   12: no /Pg; a child element (13) with /Pg page 3 -> page 0
    //   14: no content, no /Pg anywhere                  -> -1
    //   15: /Pg refers to an object that is not a page   -> -1
    //   16: no /Pg; the parent (10) has one              -> page 1 (nearest ancestor)
    private static byte[] Build()
    {
        var b = new PdfBuilder("2.0");
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 9 0 R /Outlines 30 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 /MediaBox [0 0 200 200] >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R /Annots [20 0 R 21 0 R 22 0 R 23 0 R 24 0 R 25 0 R] >>");
        b.Obj(4, "<< /Type /Page /Parent 2 0 R >>");
        b.Obj(5, "<< /Type /Page /Parent 2 0 R >>");
        b.Obj(9, "<< /Type /StructTreeRoot /K [10 0 R 11 0 R 12 0 R 14 0 R 15 0 R] >>");
        b.Obj(10, "<< /Type /StructElem /S /P /P 9 0 R /Pg 4 0 R /K [0 16 0 R] >>");
        b.Obj(11, "<< /Type /StructElem /S /P /P 9 0 R /K << /Type /MCR /Pg 5 0 R /MCID 0 >> >>");
        b.Obj(12, "<< /Type /StructElem /S /Div /P 9 0 R /K [13 0 R] >>");
        b.Obj(13, "<< /Type /StructElem /S /P /P 12 0 R /Pg 3 0 R /K 0 >>");
        b.Obj(14, "<< /Type /StructElem /S /P /P 9 0 R >>");
        b.Obj(15, "<< /Type /StructElem /S /P /P 9 0 R /Pg 2 0 R /K 0 >>");
        b.Obj(16, "<< /Type /StructElem /S /Span /P 10 0 R >>");
        string Link(string entry) => $"<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] {entry} >>";
        b.Obj(20, Link("/A << /S /GoTo /SD [10 0 R /XYZ 5 6 0] >>"));
        b.Obj(21, Link("/A << /S /GoTo /SD [11 0 R /Fit] >>"));
        b.Obj(22, Link("/Dest [12 0 R /Fit]"));
        b.Obj(23, Link("/A << /S /GoTo /SD [14 0 R /Fit] >>"));
        b.Obj(24, Link("/A << /S /GoTo /SD [15 0 R /Fit] /D [4 0 R /Fit] >>"));
        b.Obj(25, Link("/Dest [16 0 R /Fit]"));
        // an ordinary page destination in an outline item
        b.Obj(30, "<< /Type /Outlines /First 31 0 R /Last 32 0 R /Count 2 >>");
        b.Obj(31, "<< /Title (structure) /Parent 30 0 R /Next 32 0 R /A << /S /GoTo /SD [11 0 R /Fit] >> >>");
        b.Obj(32, "<< /Title (page) /Parent 30 0 R /Prev 31 0 R /Dest [5 0 R /Fit] >>");
        long x = b.XrefTable(33);
        b.Trailer("/Size 33 /Root 1 0 R", x);
        return b.ToArray();
    }

    [Fact]
    public void LinksReportThePageOfTheirStructureElement()
    {
        using PdfDocument d = PdfDocument.Open(Build());
        IReadOnlyList<PdfAnnotation> links = d.Pages[0].Links;
        Assert.Equal(6, links.Count);
        int[] expected = [1, 2, 0, -1, -1, 1];
        for (int i = 0; i < links.Count; i++)
        {
            PdfDestination dest = links[i].Destination!;
            Assert.True(dest.IsStructureDestination, $"link {i}");
            Assert.False(dest.IsRemotePage);
            Assert.Equal(expected[i], dest.PageIndex);
            Assert.NotNull(dest.StructureElement);
            Assert.Equal(expected[i] >= 0, dest.Page is not null);
        }

        Assert.Equal(5, links[0].Destination!.Left);
        Assert.Equal(6, links[0].Destination!.Top);
        Assert.Equal("XYZ", links[0].Destination!.FitType);
        Assert.Equal(10, links[0].Destination!.StructureElement!.Reference!.Number);
    }

    [Fact]
    public void OutlineItemsReportTheirStructureAndPageDestinations()
    {
        using PdfDocument d = PdfDocument.Open(Build());
        Assert.True(d.Outline[0].Destination!.IsStructureDestination);
        Assert.Equal(2, d.Outline[0].Destination!.PageIndex);
        Assert.False(d.Outline[1].Destination!.IsStructureDestination);
        Assert.Null(d.Outline[1].Destination!.StructureElement);
        Assert.Equal(2, d.Outline[1].Destination!.PageIndex);
    }

    [Fact]
    public void ACyclicStructureTreeEndsWithoutAPage()
    {
        var b = new PdfBuilder("2.0");
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 9 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 200 200] >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R /Annots [20 0 R] >>");
        b.Obj(9, "<< /Type /StructTreeRoot /K [10 0 R] >>");
        b.Obj(10, "<< /Type /StructElem /S /Div /P 11 0 R /K [11 0 R] >>");
        b.Obj(11, "<< /Type /StructElem /S /Div /P 10 0 R /K [10 0 R] >>");
        b.Obj(20, "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /GoTo /SD [10 0 R /Fit] >> >>");
        long x = b.XrefTable(21);
        b.Trailer("/Size 21 /Root 1 0 R", x);
        using PdfDocument d = PdfDocument.Open(b.ToArray());
        PdfDestination dest = d.Pages[0].Links[0].Destination!;
        Assert.True(dest.IsStructureDestination);
        Assert.Equal(-1, dest.PageIndex);
    }
}
