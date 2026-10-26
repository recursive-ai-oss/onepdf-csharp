using System.Text;
using static OnePdf.Tests.Tagging.TagDocs;

namespace OnePdf.Tests.Tagging;

/// <summary>What the PDF/UA check says about forms drawn without a wrapper, and about content the tree does not cover.</summary>
public class TaggingCheckerTests
{
    private static byte[] TaggedWithForm(string formContent, string formEntries = "")
    {
        byte[] src = Make([new PageSpec(T(72, 700, "Page text here.") + "/Fm1 Do\n")], b => Form(b, 100, formContent, formEntries), "/Fm1 100 0 R");
        return Tag(src, (_, pages, plan) =>
        {
            foreach (var blk in pages[0].Blocks)
            {
                plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], blk));
            }
        }, out _);
    }

    private static byte[] WithExtraForm(byte[] tagged, string content, int? structParents)
    {
        using PdfDocument ed = TagSupport.Editable(tagged);
        PdfStream form = ed.CreateStream(Encoding.Latin1.GetBytes(content), false);
        form.Dictionary.SetEntry("Type", new PdfName("XObject"));
        form.Dictionary.SetEntry("Subtype", new PdfName("Form"));
        var box = new PdfArray();
        foreach (int v in new[] { 0, 0, 612, 792 })
        {
            box.Append(new PdfInteger(v));
        }

        form.Dictionary.SetEntry("BBox", box);
        if (structParents is { } sp)
        {
            form.Dictionary.SetEntry("StructParents", new PdfInteger(sp));
        }

        ed.Pages[0].Resources.GetDictionary("XObject")!.SetEntry("Fm9", ed.AddObject(form));
        ed.Pages[0].Dictionary.SetEntry("Contents", ed.AddObject(ed.CreateStream(Encoding.Latin1.GetBytes(PageContent(ed, 0) + "/Fm9 Do\n"), false)));
        return TagSupport.Save(ed);
    }

    [Fact]
    public void FormWithItsOwnMarkedContentDrawnWithoutWrapperIsAccepted()
    {
        byte[] tagged = Tag(Make([new PageSpec("/Fm1 Do\n")], b => Form(b, 100, "BT /F1 12 Tf 72 700 Td (Heading in form) Tj ET\nBT /F1 12 Tf 72 500 Td (Body in form) Tj ET\n"), "/Fm1 100 0 R"), (_, pages, plan) =>
        {
            plan.Add(PdfTagRole.H1, PdfTagContent.Block(pages[0], 0));
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], 1));
        }, out PdfTaggingResult r);
        Assert.Equal(1, r.FormsCopied);
        using PdfDocument d = PdfDocument.Open(tagged);
        // The page content has the Do outside any marked content: the check follows the form instead of reporting it.
        Assert.DoesNotContain(d.CheckPdfUA(), static v => v.Message.Contains("neither tagged", StringComparison.Ordinal));
        AssertNoTagProblems(d);
    }

    [Fact]
    public void FormPaintingWithoutMarksIsStillReported()
    {
        byte[] tagged = TaggedWithForm("0.5 g 10 10 100 100 re f\n");
        byte[] broken = WithExtraForm(tagged, "0.1 g 200 200 50 50 re f", null);
        using PdfDocument d = PdfDocument.Open(broken);
        Assert.Contains(d.CheckPdfUA(), static v => v.Message.Contains("neither tagged", StringComparison.Ordinal));
    }

    [Fact]
    public void FormMarkedContentMissingFromTheTreeIsReported()
    {
        byte[] tagged = TaggedWithForm("BT /F1 12 Tf 72 500 Td (Words) Tj ET\n");
        byte[] broken = WithExtraForm(tagged, "/P << /MCID 7 >> BDC 0.1 g 200 200 50 50 re f EMC", 40);
        using PdfDocument d = PdfDocument.Open(broken);
        Assert.Contains(d.CheckPdfUA(), static v => v.Message.Contains("of a form XObject is not part of the structure tree", StringComparison.Ordinal));
    }

    [Fact]
    public void FormMarkedContentWithoutStructParentsIsReported()
    {
        byte[] tagged = TaggedWithForm("BT /F1 12 Tf 72 500 Td (Words) Tj ET\n");
        byte[] broken = WithExtraForm(tagged, "/P << /MCID 0 >> BDC 0.1 g 200 200 50 50 re f EMC", null);
        using PdfDocument d = PdfDocument.Open(broken);
        Assert.Contains(d.CheckPdfUA(), static v => v.Message.Contains("has no /StructParents", StringComparison.Ordinal));
    }

    [Fact]
    public void UntaggedDocumentIsReportedAsUntaggedContent()
    {
        using PdfDocument d = PdfDocument.Open(Make(T(72, 700, "Nothing is tagged here.")));
        Assert.NotEmpty(d.CheckPdfUA());
    }

    [Fact]
    public void SelfReferencingFormInTheCheckTerminates()
    {
        byte[] src = Make([new PageSpec("/Fm1 Do\n")], b => Form(b, 100, T(72, 700, "Loop") + "/Fm1 Do\n", xobjects: "/Fm1 100 0 R"), "/Fm1 100 0 R");
        using PdfDocument d = PdfDocument.Open(src);
        _ = d.CheckPdfUA();
    }
}
