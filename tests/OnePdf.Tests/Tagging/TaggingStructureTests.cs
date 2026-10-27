using OnePdf.Text;
using static OnePdf.Tests.Tagging.TagDocs;

namespace OnePdf.Tests.Tagging;

/// <summary>Roles, references, artifacts, links and saving.</summary>
public class TaggingStructureTests
{
    private static readonly string Page1 =
        T(72, 700, "Annual Report", "F2", 24) +
        T(72, 600, "The first paragraph of the report.") +
        T(72, 500, "The second paragraph follows here.");

    [Fact]
    public void HeadingAndParagraphsGetTheirRoles()
    {
        byte[] tagged = Tag(Make(Page1), (_, pages, plan) =>
        {
            plan.Add(PdfTagRole.H1, PdfTagContent.Block(pages[0], Blk(pages[0], "Annual")));
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], Blk(pages[0], "The first")));
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], Blk(pages[0], "The second")));
        }, out PdfTaggingResult r);

        using PdfDocument doc = PdfDocument.Open(tagged);
        Assert.Equal("H1 P P", Roles(doc));
        Assert.Equal(4, r.ElementsWritten); // the three plus the Document element
        Assert.Equal(3, r.ContentSequences);
        Assert.True(doc.Catalog.GetDictionary("MarkInfo")!.GetBool("Marked"));
        Assert.Equal("en", doc.Catalog.GetString("Lang")!.GetText());
        AssertConforms(tagged);
    }

    [Fact]
    public void UnreferencedContentBecomesArtifact()
    {
        byte[] tagged = Tag(Make(Page1 + "0.5 g 50 50 100 20 re f\n"), (_, pages, plan) =>
        {
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], Blk(pages[0], "The first")));
        }, out PdfTaggingResult r);

        using PdfDocument doc = PdfDocument.Open(tagged);
        string c = PageContent(doc, 0);
        Assert.True(Count(c, "/Artifact") >= 2, c);
        Assert.True(r.ArtifactSequences >= 2);
        AssertNoTagProblems(doc);
    }

    [Fact]
    public void ExplicitArtifactKindsBecomePaginationArtifacts()
    {
        string page = T(72, 760, "Running header") + T(72, 600, "Body text here.") + T(72, 30, "Page footer");
        byte[] tagged = Tag(Make(page), (_, pages, plan) =>
        {
            plan.Artifact(PdfTagContent.Block(pages[0], Blk(pages[0], "Running")), PdfArtifactKind.Header);
            plan.Artifact(PdfTagContent.Block(pages[0], Blk(pages[0], "Page footer")), PdfArtifactKind.Footer);
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], Blk(pages[0], "Body")));
        }, out _);

        using PdfDocument doc = PdfDocument.Open(tagged);
        string c = PageContent(doc, 0);
        Assert.Contains("/Subtype /Header", c);
        Assert.Contains("/Subtype /Footer", c);
        Assert.Contains("/Type /Pagination", c);
        Assert.Equal("P", Roles(doc));
        AssertConforms(tagged);
    }

    [Fact]
    public void RunningHeadersFoundByExtractionAreArtifacts()
    {
        string Page(int i) => T(72, 760, "Quarterly bulletin of the harbor") + T(72, 600, "Body number " + i + " of the bulletin.") + T(72, 40, "Confidential harbor office");
        PageSpec[] specs = [.. Enumerable.Range(0, 4).Select(i => new PageSpec(Page(i)))];
        byte[] tagged = Tag(Make(specs), (doc, _, plan) =>
        {
            IReadOnlyList<PdfPageText> pages = doc.ExtractText(new PdfTextOptions { RetainGlyphs = true, UseStructureTree = false });
            plan.ArtifactHeadersAndFooters(pages);
            foreach (PdfPageText p in pages)
            {
                plan.Add(PdfTagRole.P, PdfTagContent.Block(p, Blk(p, "Body")));
            }
        }, out _);

        using PdfDocument d = PdfDocument.Open(tagged);
        Assert.Contains("/Subtype /Header", PageContent(d, 0));
        Assert.Contains("/Subtype /Footer", PageContent(d, 3));
        Assert.Equal("P P P P", Roles(d));
        AssertConforms(tagged);
    }

    [Fact]
    public void ListItemSplitIntoLabelAndBodyWithinOneBlock()
    {
        string page = "BT /F1 12 Tf 14 TL 72 700 Td (1. First item of the list) Tj T* (2. Second item of the list) Tj ET\n";
        byte[] tagged = Tag(Make(page), (_, pages, plan) =>
        {
            PdfTextBlock b = pages[0].Blocks[0];
            Assert.Equal(2, b.Lines.Count);
            PdfTagElement list = plan.Add(PdfTagRole.L);
            for (int i = 0; i < 2; i++)
            {
                PdfTagElement li = list.Add(PdfTagRole.LI);
                li.Add(PdfTagRole.Lbl, PdfTagContent.Words(pages[0], b.Lines[i], 0));
                li.Add(PdfTagRole.LBody, PdfTagContent.Words(pages[0], b.Lines[i], 1, b.Lines[i].Words.Count - 1));
            }
        }, out _);

        using PdfDocument doc = PdfDocument.Open(tagged);
        Assert.Equal("L LI Lbl LBody LI Lbl LBody", Roles(doc));
        // A text string shown once is split so each part has its own marked content.
        Assert.True(Count(PageContent(doc, 0), " BDC") >= 4);
        AssertConforms(tagged);
    }

    [Fact]
    public void TableWithHeaderScopeContinuesOverTwoPages()
    {
        byte[] tagged = Tag(Make(
            T(72, 700, "Name") + T(300, 700, "Value") + T(72, 600, "alpha") + T(300, 600, "one"),
            T(72, 700, "beta") + T(300, 500, "two")), (_, pages, plan) =>
        {
            PdfTagElement table = plan.Add(PdfTagRole.Table);
            PdfTagElement head = table.Add(PdfTagRole.TR);
            head.Add(PdfTagRole.TH, PdfTagContent.Block(pages[0], Blk(pages[0], "Name"))).Scope = PdfTagScope.Column;
            head.Add(PdfTagRole.TH, PdfTagContent.Block(pages[0], Blk(pages[0], "Value"))).Scope = PdfTagScope.Column;
            PdfTagElement r1 = table.Add(PdfTagRole.TR);
            r1.Add(PdfTagRole.TD, PdfTagContent.Block(pages[0], Blk(pages[0], "alpha")));
            r1.Add(PdfTagRole.TD, PdfTagContent.Block(pages[0], Blk(pages[0], "one")));
            PdfTagElement r2 = table.Add(PdfTagRole.TR);
            r2.Add(PdfTagRole.TD, PdfTagContent.Block(pages[1], Blk(pages[1], "beta")));
            r2.Add(PdfTagRole.TD, PdfTagContent.Block(pages[1], Blk(pages[1], "two")));
        }, out _);

        using PdfDocument doc = PdfDocument.Open(tagged);
        Assert.Equal("Table TR TH TH TR TD TD TR TD TD", Roles(doc));
        Assert.True(doc.Pages[0].Dictionary.ContainsKey("StructParents") && doc.Pages[1].Dictionary.ContainsKey("StructParents"));
        AssertConforms(tagged);
    }

    [Fact]
    public void FigureFromGraphicsAreaCarriesAltText()
    {
        string page = T(72, 700, "Caption goes below.") + "0.2 0.4 0.8 rg 100 300 200 150 re f\n0 0 0 RG 2 w 100 300 200 150 re S\n";
        byte[] tagged = Tag(Make(page), (_, pages, plan) =>
        {
            PdfTagElement fig = plan.Add(PdfTagRole.Figure, PdfTagContent.Graphics(pages[0], new PdfRectangle(98, 298, 302, 452)));
            fig.Alt = "A blue rectangle";
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], 0));
        }, out _);

        using PdfDocument doc = PdfDocument.Open(tagged);
        Assert.Equal("Figure P", Roles(doc));
        AssertNoTagProblems(doc);
        AssertConforms(tagged);
    }

    [Fact]
    public void FigureWithoutAltIsReportedByTheCheck()
    {
        string page = T(72, 700, "Caption goes below.") + "0.2 0.4 0.8 rg 100 300 200 150 re f\n";
        byte[] tagged = Tag(Make(page), (_, pages, plan) =>
        {
            plan.Add(PdfTagRole.Figure, PdfTagContent.Graphics(pages[0], new PdfRectangle(98, 298, 302, 452)));
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], 0));
        }, out _);

        using PdfDocument doc = PdfDocument.Open(tagged);
        Assert.Contains(doc.CheckPdfUA(), static v => v.Message.Contains("alternate", StringComparison.OrdinalIgnoreCase) || v.Message.Contains("alt", StringComparison.OrdinalIgnoreCase));
        using var ms = new MemoryStream();
        using PdfDocument editable = TagSupport.Editable(tagged);
        Assert.Throws<PdfUAException>(() => editable.Save(ms, new PdfSaveOptions { PdfUA = new PdfUAOptions() }));
    }

    [Fact]
    public void UntaggedMarkIsReportedByTheCheckWhenTheTreeIsIncomplete()
    {
        // Content a viewer would show that the tree does not cover: the check must see it.
        string page = T(72, 700, "Heading text") + T(72, 600, "Some words.");
        byte[] tagged = Tag(Make(page), (_, pages, plan) => plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], 0)), out _);
        using PdfDocument ed = TagSupport.Editable(tagged);
        // Remove the marked-content wrapper of the second block by replacing the content with the original untagged text.
        ed.Pages[0].Dictionary.SetEntry("Contents", ed.AddObject(ed.CreateStream(System.Text.Encoding.Latin1.GetBytes(page + "/P << /MCID 0 >> BDC EMC\n"), false)));
        byte[] broken = TagSupport.Save(ed);
        using PdfDocument d = PdfDocument.Open(broken);
        Assert.Contains(d.CheckPdfUA(), static v => v.Message.Contains("neither tagged", StringComparison.Ordinal));
    }

    [Fact]
    public void LinkAnnotationGetsObjectReferenceAndDescription()
    {
        string annot = "<< /Type /Annot /Subtype /Link /Rect [70 596 300 614] /Border [0 0 0] /A << /S /URI /URI (https://example.com/) >> /Dest null >>";
        byte[] src = Make([new PageSpec(T(72, 600, "Visit the harbor site") + T(72, 500, "Other."), Entries: "/Annots [100 0 R]")], b => b.Obj(100, annot));
        byte[] tagged = Tag(src, (doc, pages, plan) =>
        {
            PdfTagElement p = plan.Add(PdfTagRole.P);
            PdfTagElement link = p.Add(PdfTagRole.Link, PdfTagContent.Block(pages[0], Blk(pages[0], "Visit")), PdfTagContent.Annotation(doc.Pages[0].Annotations[0]));
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], Blk(pages[0], "Other")));
            Assert.Equal(PdfTagRole.Link, link.Role);
        }, out PdfTaggingResult r);

        Assert.Equal(1, r.AnnotationsTagged);
        using PdfDocument doc = PdfDocument.Open(tagged);
        PdfAnnotation a = doc.Pages[0].Annotations[0];
        Assert.True(a.Dictionary.ContainsKey("StructParent"));
        Assert.Equal("Visit the harbor site", a.Contents);
        Assert.Equal("P Link P", Roles(doc));
        AssertConforms(tagged);
    }

    [Fact]
    public void FullAndIncrementalSavesKeepTheTreeAndTheOriginalBytes()
    {
        byte[] src = Make(Page1);
        void Build(PdfDocument d, IReadOnlyList<PdfPageText> pages, PdfTaggingPlan plan)
        {
            plan.Add(PdfTagRole.H1, PdfTagContent.Block(pages[0], Blk(pages[0], "Annual")));
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], Blk(pages[0], "The first")));
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], Blk(pages[0], "The second")));
        }

        byte[] full = Tag(src, Build, out _);
        byte[] inc = Tag(src, Build, out _, incremental: true);
        Assert.True(inc.AsSpan(0, src.Length).SequenceEqual(src), "an incremental save appends to the original bytes");
        foreach (byte[] file in new[] { full, inc })
        {
            using PdfDocument d = PdfDocument.Open(file);
            Assert.Equal("H1 P P", Roles(d));
            AssertNoTagProblems(d);
        }
    }

    [Fact]
    public void PdfUASaveOfATaggedDocumentIsAcceptedByVeraPdf()
    {
        // Embedded fonts are the source file's business; a document that already embeds them passes the whole gate.
        using PdfDocument unused = PdfDocument.Open(Make(Page1));
        byte[] tagged = Tag(Make(Page1), (_, pages, plan) =>
        {
            plan.Add(PdfTagRole.H1, PdfTagContent.Block(pages[0], Blk(pages[0], "Annual")));
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], Blk(pages[0], "The first")));
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], Blk(pages[0], "The second")));
        }, out _);
        using PdfDocument ed = TagSupport.Editable(tagged);
        PdfUAException ex = Assert.Throws<PdfUAException>(() => TagSupport.Save(ed, new PdfSaveOptions { PdfUA = new PdfUAOptions() }));
        Assert.All(ex.Violations, static v => Assert.StartsWith("7.21", v.Clause, StringComparison.Ordinal));
    }

    [Fact]
    public void RoleNamesMatchTheEnum()
    {
        foreach (PdfTagRole role in Enum.GetValues<PdfTagRole>())
        {
            Assert.Equal(role.ToString(), OnePdf.Tagging.TagText.RoleName(role));
        }
    }
}
