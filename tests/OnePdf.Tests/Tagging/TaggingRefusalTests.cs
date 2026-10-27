using System.Text;
using OnePdf.Text;
using static OnePdf.Tests.Tagging.TagDocs;

namespace OnePdf.Tests.Tagging;

/// <summary>Every reason a plan or a document is refused, the limits, and cancellation. A refused call leaves the document unchanged.</summary>
public class TaggingRefusalTests
{
    private static readonly string Simple = T(72, 700, "First block text") + T(72, 500, "Second block text");

    private static PdfTaggingException Refused(byte[] src, Action<PdfDocument, IReadOnlyList<PdfPageText>, PdfTaggingPlan> build, PdfTaggingFailure reason)
    {
        using PdfDocument doc = TagSupport.Editable(src);
        IReadOnlyList<PdfPageText> pages = TagSupport.Extract(doc);
        PdfTaggingPlan plan = doc.CreateTaggingPlan();
        build(doc, pages, plan);
        PdfTaggingException ex = Assert.Throws<PdfTaggingException>(() => doc.ApplyTagging(plan));
        Assert.Equal(reason, ex.Reason);
        // Nothing of the plan reached the document.
        Assert.Null(doc.Catalog.GetDictionary("StructTreeRoot"));
        Assert.False(doc.Pages[0].Dictionary.ContainsKey("StructParents"));
        return ex;
    }

    private static void Both(PdfDocument d, IReadOnlyList<PdfPageText> pages, PdfTaggingPlan plan)
    {
        plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], 0));
        plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], 1));
    }

    // ---- already tagged ----

    [Fact]
    public void FileWithAStructureTreeIsRefused()
    {
        byte[] tagged = Tag(Make(Simple), Both, out _);
        using PdfDocument doc = TagSupport.Editable(tagged);
        IReadOnlyList<PdfPageText> pages = TagSupport.Extract(doc);
        PdfTaggingPlan plan = doc.CreateTaggingPlan();
        plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], 0));
        Assert.Equal(PdfTaggingFailure.AlreadyTagged, Assert.Throws<PdfTaggingException>(() => doc.ApplyTagging(plan)).Reason);
    }

    [Fact]
    public void FileThatOnlyDeclaresItselfTaggedIsRefused()
    {
        Refused(Make([new PageSpec(Simple)], catalogExtra: "/MarkInfo << /Marked true >>"), Both, PdfTaggingFailure.AlreadyTagged);
    }

    [Fact]
    public void StrayMcidInContentIsRefused()
    {
        Refused(Make("/P << /MCID 0 >> BDC\n" + Simple + "EMC\n"), Both, PdfTaggingFailure.AlreadyTagged);
    }

    // ---- foreign or unusable references ----

    [Fact]
    public void BlocksFromAnotherDocumentAreRefused()
    {
        using PdfDocument other = PdfDocument.Open(Make(Simple));
        IReadOnlyList<PdfPageText> foreign = TagSupport.Extract(other);
        Refused(Make(Simple), (_, _, plan) => plan.Add(PdfTagRole.P, PdfTagContent.Block(foreign[0], 0)), PdfTaggingFailure.ForeignContent);
    }

    [Fact]
    public void ExtractionWithoutGlyphsIsRefused()
    {
        using PdfDocument doc = TagSupport.Editable(Make(Simple));
        IReadOnlyList<PdfPageText> bare = doc.ExtractText(new PdfTextOptions { RetainGlyphs = false, UseStructureTree = false });
        PdfTaggingPlan plan = doc.CreateTaggingPlan();
        plan.Add(PdfTagRole.P, PdfTagContent.Block(bare[0], 0));
        Assert.Equal(PdfTaggingFailure.ForeignContent, Assert.Throws<PdfTaggingException>(() => doc.ApplyTagging(plan)).Reason);
    }

    [Fact]
    public void PageThatDoesNotExistIsRefused()
    {
        Refused(Make(Simple), (_, _, plan) => plan.Add(PdfTagRole.Figure, PdfTagContent.Graphics(7, new PdfRectangle(0, 0, 10, 10))), PdfTaggingFailure.ForeignContent);
    }

    [Fact]
    public void AnnotationFromAnotherDocumentIsRefused()
    {
        byte[] withLink = Make([new PageSpec(Simple, Entries: "/Annots [100 0 R]")], b => b.Obj(100, "<< /Type /Annot /Subtype /Link /Rect [70 696 200 714] /Border [0 0 0] >>"));
        using PdfDocument other = PdfDocument.Open(withLink);
        PdfAnnotation foreign = other.Pages[0].Annotations[0];
        Refused(withLink, (_, pages, plan) => plan.Add(PdfTagRole.Link, PdfTagContent.Block(pages[0], 0), PdfTagContent.Annotation(foreign)), PdfTaggingFailure.ForeignContent);
    }

    // ---- duplicates ----

    [Fact]
    public void BlockReferencedTwiceIsRefused() =>
        Refused(Make(Simple), (_, pages, plan) =>
        {
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], 0));
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], 0));
        }, PdfTaggingFailure.DuplicateContent);

    [Fact]
    public void WordsOverlappingABlockAreRefused() =>
        Refused(Make(Simple), (_, pages, plan) =>
        {
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], 0));
            plan.Add(PdfTagRole.P, PdfTagContent.Words(pages[0], pages[0].Blocks[0].Lines[0], 1));
        }, PdfTaggingFailure.DuplicateContent);

    [Fact]
    public void OverlappingGraphicsAreasAreRefused()
    {
        string content = Simple + "0.2 g 100 300 100 100 re f\n";
        Refused(Make(content), (_, pages, plan) =>
        {
            plan.Add(PdfTagRole.Figure, PdfTagContent.Graphics(pages[0], new PdfRectangle(99, 299, 201, 401))).Alt = "One";
            plan.Add(PdfTagRole.Figure, PdfTagContent.Graphics(pages[0], new PdfRectangle(90, 290, 210, 410))).Alt = "Two";
        }, PdfTaggingFailure.DuplicateContent);
    }

    [Fact]
    public void AnnotationReferencedTwiceIsRefused()
    {
        byte[] withLink = Make([new PageSpec(Simple, Entries: "/Annots [100 0 R]")], b => b.Obj(100, "<< /Type /Annot /Subtype /Link /Rect [70 696 200 714] /Border [0 0 0] >>"));
        Refused(withLink, (doc, pages, plan) =>
        {
            plan.Add(PdfTagRole.Link, PdfTagContent.Block(pages[0], 0), PdfTagContent.Annotation(doc.Pages[0].Annotations[0]));
            plan.Add(PdfTagRole.Link, PdfTagContent.Block(pages[0], 1), PdfTagContent.Annotation(doc.Pages[0].Annotations[0]));
        }, PdfTaggingFailure.DuplicateContent);
    }

    // ---- nesting ----

    [Fact]
    public void RowOutsideATableIsRefused() =>
        Refused(Make(Simple), (_, pages, plan) =>
        {
            plan.Add(PdfTagRole.TR).Add(PdfTagRole.TD, PdfTagContent.Block(pages[0], 0));
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], 1));
        }, PdfTaggingFailure.InvalidNesting);

    [Fact]
    public void ListItemOutsideAListIsRefused() =>
        Refused(Make(Simple), (_, pages, plan) =>
        {
            PdfTagElement p = plan.Add(PdfTagRole.P);
            p.Add(PdfTagRole.LI).Add(PdfTagRole.LBody, PdfTagContent.Block(pages[0], 0));
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], 1));
        }, PdfTaggingFailure.InvalidNesting);

    [Fact]
    public void CellDirectlyInATableIsRefused() =>
        Refused(Make(Simple), (_, pages, plan) =>
        {
            plan.Add(PdfTagRole.Table).Add(PdfTagRole.TD, PdfTagContent.Block(pages[0], 0));
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], 1));
        }, PdfTaggingFailure.InvalidNesting);

    [Fact]
    public void ContentDirectlyInAListIsRefused() =>
        Refused(Make(Simple), (_, pages, plan) =>
        {
            plan.Add(PdfTagRole.L, PdfTagContent.Block(pages[0], 0));
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], 1));
        }, PdfTaggingFailure.InvalidNesting);

    // ---- the page cannot be read or changed ----

    [Fact]
    public void PageEditedAfterExtractionIsRefused()
    {
        using PdfDocument doc = TagSupport.Editable(Make(Simple));
        IReadOnlyList<PdfPageText> pages = TagSupport.Extract(doc);
        PdfTaggingPlan plan = doc.CreateTaggingPlan();
        Both(doc, pages, plan);
        doc.Pages[0].Dictionary.SetEntry("Contents", doc.AddObject(doc.CreateStream(Encoding.Latin1.GetBytes(Simple + T(72, 300, "Added later")), false)));
        PdfTaggingException ex = Assert.Throws<PdfTaggingException>(() => doc.ApplyTagging(plan));
        Assert.Equal(PdfTaggingFailure.ContentChanged, ex.Reason);
        Assert.Equal(0, ex.PageIndex);
    }

    // The last page draws a form that draws itself: it shows the text the extraction saw, but the page cannot be rewritten safely.
    private static byte[] WithBrokenStream(int pages)
    {
        PageSpec[] specs = [.. Enumerable.Range(0, pages).Select(i => new PageSpec(i == pages - 1 ? "/Fm1 Do\n" : Simple))];
        return Make(specs, b => Form(b, 100, Simple + "/Fm1 Do\n", xobjects: "/Fm1 100 0 R"), "/Fm1 100 0 R");
    }

    [Fact]
    public void FormDrawingItselfIsRefusedAsUnreadable() => Refused(WithBrokenStream(1), Both, PdfTaggingFailure.ContentUnreadable);

    [Fact]
    public void FailureOnALaterPageLeavesEarlierPagesUnchanged()
    {
        using PdfDocument doc = TagSupport.Editable(WithBrokenStream(2));
        IReadOnlyList<PdfPageText> pages = TagSupport.Extract(doc);
        PdfTaggingPlan plan = doc.CreateTaggingPlan();
        foreach (PdfPageText p in pages)
        {
            plan.Add(PdfTagRole.P, PdfTagContent.Block(p, 0));
            plan.Add(PdfTagRole.P, PdfTagContent.Block(p, 1));
        }

        string before = PageContent(doc, 0);
        PdfTaggingException ex = Assert.Throws<PdfTaggingException>(() => doc.ApplyTagging(plan));
        Assert.Equal(1, ex.PageIndex);
        Assert.Equal(before, PageContent(doc, 0));
        Assert.False(doc.Pages[0].Dictionary.ContainsKey("StructParents"));
        Assert.Null(doc.Catalog.GetDictionary("StructTreeRoot"));
    }

    // ---- misuse ----

    [Fact]
    public void PlanCanBeAppliedOnce()
    {
        using PdfDocument doc = TagSupport.Editable(Make(Simple));
        IReadOnlyList<PdfPageText> pages = TagSupport.Extract(doc);
        PdfTaggingPlan plan = doc.CreateTaggingPlan();
        Both(doc, pages, plan);
        doc.ApplyTagging(plan);
        Assert.Throws<InvalidOperationException>(() => doc.ApplyTagging(plan));
    }

    [Fact]
    public void ReadOnlyDocumentCannotBeTagged()
    {
        using PdfDocument doc = PdfDocument.Open(Make(Simple));
        PdfTaggingPlan plan = doc.CreateTaggingPlan();
        plan.Add(PdfTagRole.P, PdfTagContent.Block(TagSupport.Extract(doc)[0], 0));
        Assert.Throws<InvalidOperationException>(() => doc.ApplyTagging(plan));
    }

    [Fact]
    public void PlanOfAnotherDocumentIsRefused()
    {
        using PdfDocument a = TagSupport.Editable(Make(Simple));
        using PdfDocument b = TagSupport.Editable(Make(Simple));
        PdfTaggingPlan plan = a.CreateTaggingPlan();
        Assert.Throws<ArgumentException>(() => b.ApplyTagging(plan));
    }

    [Fact]
    public void CancelledCallLeavesTheDocumentUnchanged()
    {
        using PdfDocument doc = TagSupport.Editable(Make(Simple));
        IReadOnlyList<PdfPageText> pages = TagSupport.Extract(doc);
        PdfTaggingPlan plan = doc.CreateTaggingPlan();
        Both(doc, pages, plan);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => doc.ApplyTagging(plan, cts.Token));
        Assert.Null(doc.Catalog.GetDictionary("StructTreeRoot"));
    }

    // ---- limits ----

    [Fact]
    public void DeepTreeWithinTheLimitIsBuiltWithoutRecursion()
    {
        byte[] tagged = Tag(Make(Simple), (_, pages, plan) =>
        {
            PdfTagElement e = plan.Add(PdfTagRole.Div);
            for (int i = 0; i < 200; i++)
            {
                e = e.Add(PdfTagRole.Div);
            }

            e.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], 0));
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], 1));
        }, out PdfTaggingResult r);
        Assert.True(r.ElementsWritten >= 203);
        using PdfDocument d = PdfDocument.Open(tagged);
        Assert.EndsWith("P P", Roles(d));
    }

    [Fact]
    public void TreeDeeperThanTheNestingLimitIsRefusedWhileBuilding()
    {
        using PdfDocument doc = PdfDocument.Open(Make(Simple), new PdfOpenOptions { AllowEditing = true });
        PdfTaggingPlan plan = doc.CreateTaggingPlan();
        PdfTagElement e = plan.Add(PdfTagRole.Div);
        Assert.Throws<PdfLimitException>(() =>
        {
            for (int i = 0; i < 100_000; i++)
            {
                e = e.Add(PdfTagRole.Div);
            }
        });
    }

    [Fact]
    public void TooManyElementsAreRefusedWhileBuilding()
    {
        using PdfDocument doc = PdfDocument.Open(Make(Simple), new PdfOpenOptions { AllowEditing = true, Limits = new PdfLimits { MaxObjects = 1000 } });
        PdfTaggingPlan plan = doc.CreateTaggingPlan();
        Assert.Throws<PdfLimitException>(() =>
        {
            for (int i = 0; i < 2000; i++)
            {
                plan.Add(PdfTagRole.Div);
            }
        });
    }

    [Fact]
    public void PageThatWouldExceedTheOperatorLimitOnceTaggedIsRefused()
    {
        byte[] src = Make(Simple); // two text objects: 10 operators
        using PdfDocument doc = PdfDocument.Open(src, new PdfOpenOptions { AllowEditing = true, Limits = new PdfLimits { MaxOperatorsPerPage = 12 } });
        IReadOnlyList<PdfPageText> pages = TagSupport.Extract(doc);
        PdfTaggingPlan plan = doc.CreateTaggingPlan();
        Both(doc, pages, plan);
        Assert.Throws<PdfLimitException>(() => doc.ApplyTagging(plan));
        Assert.Null(doc.Catalog.GetDictionary("StructTreeRoot"));
    }

    [Fact]
    public void LargePageTagsInReasonableTime()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 400; i++)
        {
            sb.Append(T(72, 780 - i * 1.9, "Line number " + i + " of a long page", size: 1.5));
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Tag(Make(sb.ToString()), (_, pages, plan) =>
        {
            foreach (PdfTextBlock b in pages[0].Blocks)
            {
                plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], b));
            }
        }, out PdfTaggingResult r);
        Assert.True(r.ContentSequences > 0);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), sw.Elapsed.ToString());
    }
}
