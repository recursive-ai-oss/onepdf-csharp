using OnePdf.Text;
using static OnePdf.Tests.Tagging.TagDocs;

namespace OnePdf.Tests.Tagging;

/// <summary>Annotations in the happy path and the plan: written inline in /Annots, listed more than once, and without a description.</summary>
public class TaggingAnnotationTests
{
    private const string LinkInline = "<< /Type /Annot /Subtype /Link /Rect [72 696 130 712] /Border [0 0 0] /A << /S /URI /URI (https://example.com/) >> >>";
    private const string NoteInline = "<< /Type /Annot /Subtype /Text /Rect [400 700 420 720] /Contents (A note) /F 4 >>";

    private static PdfBlockRole? AllParagraphs(PdfTagBlock b) => PdfBlockRole.Paragraph;

    [Fact]
    public void InlineAnnotationsBecomeIndirectObjectsAndAreTagged()
    {
        byte[] src = Make([new PageSpec(T(72, 700, "Visit the site") + T(72, 600, "Para two"), Entries: $"/Annots [{LinkInline} {NoteInline}]")]);
        byte[] tagged = Happy(src, AllParagraphs, null, out PdfTaggingResult r);
        Assert.Equal(2, r.AnnotationsTagged);
        Assert.Equal(2, r.AnnotationsMadeIndirect);
        using PdfDocument doc = PdfDocument.Open(tagged);
        Assert.Contains("Link", Roles(doc));
        Assert.Contains("Annot", Roles(doc));
        foreach (PdfAnnotation a in doc.Pages[0].Annotations)
        {
            Assert.NotNull(a.Dictionary.Reference);
            Assert.True(a.Dictionary.ContainsKey("StructParent"));
        }

        Assert.Equal(2, doc.Pages[0].Dictionary.GetArray("Annots")!.Count);
        AssertConforms(tagged);
    }

    [Fact]
    public void AnnotationReadBeforeTaggingStaysTheSameObject()
    {
        byte[] src = Make([new PageSpec(T(72, 700, "Visit the site"), Entries: $"/Annots [{NoteInline}]")]);
        using PdfDocument doc = TagSupport.Editable(src);
        PdfAnnotation before = doc.Pages[0].Annotations[0];
        Assert.Null(before.Dictionary.Reference);
        PdfTaggingPlan plan = doc.CreateTaggingPlan();
        plan.Add(PdfTagRole.Annot, PdfTagContent.Annotation(before));
        PdfTaggingResult r = doc.ApplyTagging(plan);
        Assert.Equal(1, r.AnnotationsMadeIndirect);
        Assert.NotNull(before.Dictionary.Reference);
        Assert.Same(before.Dictionary, doc.Pages[0].Dictionary.GetArray("Annots")![0]);
        using PdfDocument reopened = PdfDocument.Open(TagSupport.Save(doc));
        Assert.Equal("Annot", Roles(reopened));
    }

    [Fact]
    public void OneLinkListedSeveralTimesIsTaggedOnceAndTheEntriesStay()
    {
        byte[] src = Make([new PageSpec(T(72, 700, "Visit the site") + T(72, 600, "Para two"), Entries: "/Annots [101 0 R 101 0 R 101 0 R]")], b =>
            b.Obj(101, "<< /Type /Annot /Subtype /Link /Rect [72 696 130 712] /Border [0 0 0] /A << /S /URI /URI (https://example.com/) >> >>"));
        byte[] tagged = Happy(src, AllParagraphs, null, out PdfTaggingResult r);
        Assert.Equal(1, r.AnnotationsTagged);
        using PdfDocument doc = PdfDocument.Open(tagged);
        Assert.Equal(3, doc.Pages[0].Dictionary.GetArray("Annots")!.Count);
        Assert.Equal(1, Roles(doc).Split(' ').Count(static x => x == "Link"));
        AssertConforms(tagged);
    }

    [Fact]
    public void HappyPathWarnsOncePerPageForRepeatedEntries()
    {
        byte[] src = Make([new PageSpec(T(72, 700, "Visit the site"), Entries: "/Annots [101 0 R 101 0 R]")], b =>
            b.Obj(101, "<< /Type /Annot /Subtype /Link /Rect [72 696 130 712] /A << /S /URI /URI (https://example.com/) >> >>"));
        using PdfDocument doc = TagSupport.Editable(src);
        PdfTaggingPlan plan = doc.PlanTagging(TagSupport.Extract(doc), AllParagraphs);
        Assert.Contains(plan.Warnings, static w => w.StartsWith("Page 1:", StringComparison.Ordinal) && w.Contains("already tagged", StringComparison.Ordinal));
    }

    [Fact]
    public void AnAnnotationGivenTwiceInAPlanNamesThePage()
    {
        byte[] src = Make([new PageSpec(T(72, 700, "Text"), Entries: "/Annots [101 0 R]")], b => b.Obj(101, "<< /Type /Annot /Subtype /Text /Rect [10 10 30 30] /Contents (n) >>"));
        using PdfDocument doc = TagSupport.Editable(src);
        PdfAnnotation a = doc.Pages[0].Annotations[0];
        PdfTaggingPlan plan = doc.CreateTaggingPlan();
        plan.Add(PdfTagRole.Annot, PdfTagContent.Annotation(a));
        plan.Add(PdfTagRole.Annot, PdfTagContent.Annotation(a));
        PdfTaggingException e = Assert.Throws<PdfTaggingException>(() => doc.ApplyTagging(plan));
        Assert.Equal(PdfTaggingFailure.DuplicateContent, e.Reason);
        Assert.Contains("page 1", e.Message, StringComparison.Ordinal);
    }

    private static byte[] WithHeaderFooter(string appearanceText)
    {
        string entries = "/Annots [101 0 R]";
        return Make([new PageSpec(T(72, 700, "Body text of the page"), Entries: entries)], b =>
        {
            b.Stream(100, "/Type /XObject /Subtype /Form /BBox [0 0 120 20] /Resources << /Font << /F1 3 0 R >> >>",
                System.Text.Encoding.Latin1.GetBytes(appearanceText.Length == 0 ? "0.5 g 0 0 120 20 re f\n" : $"BT /F1 10 Tf 2 5 Td ({appearanceText}) Tj ET\n"));
            b.Obj(101, "<< /Type /Annot /Subtype /HeaderFooter /Rect [250 20 370 40] /F 4 /AP << /N 100 0 R >> >>");
        });
    }

    [Fact]
    public void HeaderFooterAnnotationGetsAnAnnotElementAndTheTextItShowsAsContents()
    {
        byte[] tagged = Happy(WithHeaderFooter("Page 1 of 3"), AllParagraphs, null, out PdfTaggingResult r);
        Assert.Equal(1, r.AnnotationsTagged);
        using PdfDocument doc = PdfDocument.Open(tagged);
        Assert.Equal("P Annot", Roles(doc));
        PdfAnnotation a = doc.Pages[0].Annotations[0];
        Assert.Equal("Page 1 of 3", a.Dictionary.GetString("Contents")!.GetText());
        Assert.True(a.Dictionary.ContainsKey("StructParent"));
        Assert.DoesNotContain(doc.CheckPdfUA(), static v => v.Clause == "7.18.1");
    }

    [Fact]
    public void HeaderFooterWithoutTextGetsAGenericDescription()
    {
        byte[] tagged = Happy(WithHeaderFooter(string.Empty), AllParagraphs, null, out _);
        using PdfDocument doc = PdfDocument.Open(tagged);
        Assert.Equal("Page header or footer", doc.Pages[0].Annotations[0].Dictionary.GetString("Contents")!.GetText());
        Assert.DoesNotContain(doc.CheckPdfUA(), static v => v.Clause == "7.18.1");
    }

    [Fact]
    public void ExistingContentsIsKept()
    {
        byte[] src = Make([new PageSpec(T(72, 700, "Text"), Entries: "/Annots [<< /Type /Annot /Subtype /HeaderFooter /Rect [250 20 370 40] /Contents (Mine) /F 4 >>]")]);
        byte[] tagged = Happy(src, AllParagraphs, null, out _);
        using PdfDocument doc = PdfDocument.Open(tagged);
        Assert.Equal("Mine", doc.Pages[0].Annotations[0].Dictionary.GetString("Contents")!.GetText());
    }
}
