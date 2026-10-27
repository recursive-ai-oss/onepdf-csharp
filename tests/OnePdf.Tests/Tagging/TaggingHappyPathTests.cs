using OnePdf.Text;
using static OnePdf.Tests.Tagging.TagDocs;

namespace OnePdf.Tests.Tagging;

/// <summary>The happy path (<see cref="PdfDocument.Tag"/>): each mechanical default, and the option that overrides it.</summary>
public class TaggingHappyPathTests
{
    private static PdfBlockRole? ByPrefix(PdfTagBlock b) => b.Text switch
    {
        var t when t.StartsWith("Title", StringComparison.Ordinal) => PdfBlockRole.Heading(1),
        var t when t.StartsWith("Sub", StringComparison.Ordinal) => PdfBlockRole.Heading(2),
        var t when t.StartsWith("Para", StringComparison.Ordinal) => PdfBlockRole.Paragraph,
        var t when t.StartsWith("Item", StringComparison.Ordinal) || t.Length > 1 && char.IsDigit(t[0]) && t[1] == '.' => PdfBlockRole.ListItem,
        var t when t.StartsWith("Cell", StringComparison.Ordinal) => PdfBlockRole.Table,
        _ => null,
    };

    private static string Table3x3() =>
        "BT /F1 12 Tf 14 TL 72 500 Td (Name) Tj ET\nBT /F1 12 Tf 250 500 Td (Count) Tj ET\nBT /F1 12 Tf 430 500 Td (Place) Tj ET\n" +
        "BT /F1 12 Tf 72 486 Td (alpha) Tj ET\nBT /F1 12 Tf 250 486 Td (one) Tj ET\nBT /F1 12 Tf 430 486 Td (north) Tj ET\n" +
        "BT /F1 12 Tf 72 472 Td (beta) Tj ET\nBT /F1 12 Tf 250 472 Td (two) Tj ET\nBT /F1 12 Tf 430 472 Td (south) Tj ET\n";

    [Fact]
    public void RolesMapToHeadingsParagraphsFiguresAndArtifacts()
    {
        string page = T(72, 700, "Title of it") + T(72, 650, "Sub heading") + T(72, 600, "Para one here.") + T(72, 500, "Stray words") + T(72, 400, "Figure text");
        byte[] src = Make(page);
        byte[] tagged = Happy(src, b => b.Text switch
        {
            var t when t.StartsWith("Figure", StringComparison.Ordinal) => PdfBlockRole.Figure("A figure"),
            _ => ByPrefix(b),
        }, new PdfTagOptions { Title = "Happy", Language = "fr" }, out _);

        using PdfDocument doc = PdfDocument.Open(tagged);
        Assert.Equal("H1 H2 P Figure", Roles(doc));
        Assert.Equal("fr", doc.Catalog.GetString("Lang")!.GetText());
        Assert.Equal("Happy", doc.Info.Title);
        Assert.Contains("/Artifact", PageContent(doc, 0)); // the stray words: the delegate answered null
        AssertConforms(tagged);
    }

    [Fact]
    public void RoleDefaultIsAnArtifactAndValuesAreChecked()
    {
        Assert.Equal(PdfBlockKind.Artifact, default(PdfBlockRole).Kind);
        Assert.Equal(PdfBlockRole.Artifact, default);
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfBlockRole.Heading(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfBlockRole.Heading(7));
        Assert.Throws<ArgumentException>(() => PdfBlockRole.Figure(" "));
        Assert.Equal(3, PdfBlockRole.Heading(3).Level);
    }

    // ---- lists ----

    [Fact]
    public void ConsecutiveListItemsShareOneListAndSplitLabelFromBody()
    {
        byte[] src = Make(T(72, 700, "Title") + T(72, 650, "1. First thing") + T(72, 600, "2. Second thing") + T(72, 550, "Para between") + T(72, 500, "3. Third thing"));
        byte[] tagged = Happy(src, ByPrefix, null, out _);
        using PdfDocument doc = PdfDocument.Open(tagged);
        Assert.Equal("H1 L LI Lbl LBody LI Lbl LBody P L LI Lbl LBody", Roles(doc));
        AssertConforms(tagged);
    }

    [Fact]
    public void ArtifactsBetweenListItemsDoNotBreakTheList()
    {
        byte[] src = Make(T(72, 700, "1. First thing") + T(72, 650, "stray") + T(72, 600, "2. Second thing"));
        byte[] tagged = Happy(src, b => b.Text.StartsWith("stray", StringComparison.Ordinal) ? null : PdfBlockRole.ListItem, null, out _);
        using PdfDocument doc = PdfDocument.Open(tagged);
        Assert.Equal("L LI Lbl LBody LI Lbl LBody", Roles(doc));
    }

    [Fact]
    public void LabelSplitCanBeSwitchedOff()
    {
        byte[] src = Make(T(72, 700, "1. First thing") + T(72, 600, "2. Second thing"));
        byte[] tagged = Happy(src, ByPrefix, new PdfTagOptions { SplitListLabels = false }, out _);
        using PdfDocument doc = PdfDocument.Open(tagged);
        Assert.Equal("L LI LBody LI LBody", Roles(doc));
    }

    [Fact]
    public void LabelTestCanBeReplaced()
    {
        byte[] src = Make(T(72, 700, "Item: First thing") + T(72, 600, "Item: Second thing"));
        PdfTagOptions o = new() { IsListLabel = static w => w == "Item:" };
        byte[] tagged = Happy(src, ByPrefix, o, out _);
        using PdfDocument doc = PdfDocument.Open(tagged);
        Assert.Equal("L LI Lbl LBody LI Lbl LBody", Roles(doc));

        // The built-in test does not take "Item:" for a label.
        byte[] plain = Happy(src, ByPrefix, null, out _);
        using PdfDocument d2 = PdfDocument.Open(plain);
        Assert.Equal("L LI LBody LI LBody", Roles(d2));
    }

    [Fact]
    public void BlockWithSeveralLabelledLinesIsCutIntoItemsUnlessTold()
    {
        string page = "BT /F1 12 Tf 14 TL 72 700 Td (1. First item) Tj T* (continued here) Tj T* (2. Second item) Tj T* (3. Third item) Tj ET\n";
        byte[] src = Make(page);
        using (PdfDocument probe = PdfDocument.Open(src))
        {
            Assert.Single(TagSupport.Extract(probe)[0].Blocks);
        }

        byte[] split = Happy(src, _ => PdfBlockRole.ListItem, null, out _);
        using PdfDocument d1 = PdfDocument.Open(split);
        Assert.Equal("L LI Lbl LBody LI Lbl LBody LI Lbl LBody", Roles(d1));

        byte[] whole = Happy(src, _ => PdfBlockRole.ListItem, new PdfTagOptions { SplitListBlocks = false }, out _);
        using PdfDocument d2 = PdfDocument.Open(whole);
        Assert.Equal("L LI Lbl LBody", Roles(d2));
    }

    [Fact]
    public void ListItemWithoutLabelWordIsAnItemWithBodyOnly()
    {
        byte[] src = Make(T(72, 700, "Just words here"));
        byte[] tagged = Happy(src, _ => PdfBlockRole.ListItem, null, out _);
        using PdfDocument d = PdfDocument.Open(tagged);
        Assert.Equal("L LI LBody", Roles(d));
    }

    // ---- tables ----

    [Fact]
    public void TableBecomesRowsAndCellsWithTheFirstRowAsColumnHeaders()
    {
        byte[] src = Make(Table3x3());
        byte[] tagged = Happy(src, _ => PdfBlockRole.Table, null, out _);
        using PdfDocument d = PdfDocument.Open(tagged);
        Assert.Equal("Table TR TH TH TH TR TD TD TD TR TD TD TD", Roles(d));
        PdfDictionary th = Elements(d).First(static e => e.GetName("S") == "TH");
        Assert.Equal("Column", ((PdfDictionary)th.Get("A")!).GetName("Scope"));
        AssertConforms(tagged);
    }

    [Fact]
    public void HeaderRowsColumnsAndScopesAreTunable()
    {
        byte[] src = Make(Table3x3());
        var o = new PdfTagOptions { TableHeaderRows = 2, TableHeaderColumns = 1, HeaderRowScope = PdfTagScope.Both, HeaderColumnScope = PdfTagScope.Row };
        byte[] tagged = Happy(src, _ => PdfBlockRole.Table, o, out _);
        using PdfDocument d = PdfDocument.Open(tagged);
        Assert.Equal("Table TR TH TH TH TR TH TH TH TR TH TD TD", Roles(d));
        string[] scopes = [.. Elements(d).Where(static e => e.GetName("S") == "TH").Select(static e => ((PdfDictionary)e.Get("A")!).GetName("Scope")!)];
        Assert.Equal(["Both", "Both", "Both", "Both", "Both", "Both", "Row"], scopes);

        byte[] none = Happy(src, _ => PdfBlockRole.Table, new PdfTagOptions { TableHeaderRows = 0 }, out _);
        using PdfDocument d2 = PdfDocument.Open(none);
        Assert.Equal("Table TR TD TD TD TR TD TD TD TR TD TD TD", Roles(d2));
    }

    [Fact]
    public void ContinuedTableAddsBodyRowsToTheTableBefore()
    {
        byte[] src = Make(
            "BT /F1 12 Tf 72 700 Td (Name) Tj ET\nBT /F1 12 Tf 250 700 Td (Count) Tj ET\n",
            "BT /F1 12 Tf 72 700 Td (gamma) Tj ET\nBT /F1 12 Tf 250 700 Td (three) Tj ET\n");
        byte[] tagged = Happy(src, b => b.PageIndex == 0 ? PdfBlockRole.Table : PdfBlockRole.TableContinued, null, out _);
        using PdfDocument d = PdfDocument.Open(tagged);
        Assert.Equal("Table TR TH TH TR TD TD", Roles(d));
    }

    // ---- headers, footers, figures, artifacts ----

    private static byte[] ThreePageBulletin()
    {
        string Page(int i) => T(72, 760, "Quarterly bulletin of the harbor") + T(72, 600, "Para number " + i + " of the bulletin.") + T(72, 40, "Confidential harbor office");
        return Make([.. Enumerable.Range(0, 4).Select(i => new PageSpec(Page(i)))]);
    }

    [Fact]
    public void HeadersAndFootersAreArtifactsAndTheDelegateIsNotAskedAboutThem()
    {
        var asked = new List<string>();
        byte[] tagged = Happy(ThreePageBulletin(), b =>
        {
            asked.Add(b.Text);
            return PdfBlockRole.Paragraph;
        }, null, out _);
        Assert.All(asked, static t => Assert.StartsWith("Para", t, StringComparison.Ordinal));
        using PdfDocument d = PdfDocument.Open(tagged);
        Assert.Equal("P P P P", Roles(d));
        Assert.Contains("/Subtype /Header", PageContent(d, 0));
        Assert.Contains("/Subtype /Footer", PageContent(d, 3));
    }

    [Fact]
    public void HeadersAndFootersCanBeKept()
    {
        byte[] tagged = Happy(ThreePageBulletin(), _ => PdfBlockRole.Paragraph, new PdfTagOptions { KeepHeadersAndFooters = true }, out _);
        using PdfDocument d = PdfDocument.Open(tagged);
        Assert.Equal(12, Roles(d).Split(' ').Length);
        Assert.DoesNotContain("/Subtype /Header", PageContent(d, 0));
    }

    [Fact]
    public void FiguresByAreaAreFoundAndPlacedInReadingOrder()
    {
        string page = T(72, 700, "Title") + "0.2 0.4 0.8 rg 100 400 200 150 re f\n" + T(72, 300, "Para after");
        var o = new PdfTagOptions();
        o.Figures.Add(new PdfTagFigure(0, new PdfRectangle(98, 398, 302, 552), "A blue box"));
        byte[] tagged = Happy(Make(page), ByPrefix, o, out _);
        using PdfDocument d = PdfDocument.Open(tagged);
        Assert.Equal("H1 Figure P", Roles(d));
        Assert.Equal("A blue box", Elements(d).First(static e => e.GetName("S") == "Figure").GetString("Alt")!.GetText());
        AssertConforms(tagged);
    }

    [Fact]
    public void UnreferencedMarksAreArtifacts()
    {
        byte[] tagged = Happy(Make(T(72, 700, "Para") + "0.5 g 50 50 100 20 re f\n"), ByPrefix, null, out PdfTaggingResult r);
        Assert.True(r.ArtifactSequences >= 1);
        using PdfDocument d = PdfDocument.Open(tagged);
        AssertConforms(tagged);
    }

    // ---- links ----

    private static byte[] WithLink(string annotRect = "[70 596 300 614]") =>
        Make([new PageSpec(T(72, 600, "Para visit the harbor site now") + T(72, 500, "Para other."), Entries: "/Annots [100 0 R]")],
            b => b.Obj(100, $"<< /Type /Annot /Subtype /Link /Rect {annotRect} /Border [0 0 0] /A << /S /URI /URI (https://example.com/) >> >>"));

    [Fact]
    public void LinkBecomesALinkElementAroundTheWordsItCovers()
    {
        // The rectangle covers "visit the harbor" (roughly x 100 to 200).
        byte[] src = WithLink("[104 596 190 614]");
        byte[] tagged = Happy(src, ByPrefix, null, out PdfTaggingResult r);
        Assert.Equal(1, r.AnnotationsTagged);
        using PdfDocument d = PdfDocument.Open(tagged);
        Assert.Equal("P Link P", Roles(d));
        Assert.True(d.Pages[0].Annotations[0].Dictionary.ContainsKey("StructParent"));
        Assert.False(string.IsNullOrWhiteSpace(d.Pages[0].Annotations[0].Contents));
        AssertConforms(tagged);
    }

    [Fact]
    public void LinksCanBeLeftAlone()
    {
        byte[] tagged = Happy(WithLink("[104 596 190 614]"), ByPrefix, new PdfTagOptions { TagLinks = false }, out PdfTaggingResult r);
        Assert.Equal(0, r.AnnotationsTagged);
        using PdfDocument d = PdfDocument.Open(tagged);
        Assert.Equal("P P", Roles(d));
    }

    [Fact]
    public void LinkOverNoTextIsLeftAloneWithAWarning()
    {
        byte[] tagged = Happy(WithLink("[400 300 450 320]"), ByPrefix, null, out PdfTaggingResult r);
        Assert.Equal(0, r.AnnotationsTagged);
        Assert.Contains(r.Warnings, static w => w.Contains("covers no text", StringComparison.Ordinal));
        Assert.NotNull(tagged);
    }

    // ---- hook and plan ----

    [Fact]
    public void CustomizeSeesEveryElementWithItsBlock()
    {
        var seen = new List<(PdfTagRole, string)>();
        var o = new PdfTagOptions
        {
            Customize = (e, b) =>
            {
                seen.Add((e.Role, b.Text));
                if (e.Role == PdfTagRole.P)
                {
                    e.Lang = "de";
                }

                if (e.Role == PdfTagRole.Link)
                {
                    e.Alt = "The harbor site";
                }
            },
        };
        byte[] tagged = Happy(WithLink("[104 596 190 614]"), ByPrefix, o, out _);
        Assert.Contains(seen, static s => s.Item1 == PdfTagRole.Link);
        Assert.Equal(2, seen.Count(static s => s.Item1 == PdfTagRole.P));
        using PdfDocument d = PdfDocument.Open(tagged);
        Assert.Equal(2, Elements(d).Count(static e => e.GetName("S") == "P" && e.GetString("Lang")?.GetText() == "de"));
        Assert.Contains(Elements(d), static e => e.GetName("S") == "Link" && e.GetString("Alt")?.GetText() == "The harbor site");
    }

    [Fact]
    public void PlanTaggingChangesNothingUntilItIsApplied()
    {
        using PdfDocument doc = TagSupport.Editable(Make(T(72, 700, "Para one") + T(72, 600, "Para two")));
        IReadOnlyList<PdfPageText> pages = TagSupport.Extract(doc);
        PdfTaggingPlan plan = doc.PlanTagging(pages, ByPrefix);
        Assert.Null(doc.Catalog.GetDictionary("StructTreeRoot"));
        // The plan is the low-level plan: add to it by hand before applying.
        plan.Title = "Edited";
        PdfTaggingResult r = doc.ApplyTagging(plan);
        Assert.Equal(3, r.ElementsWritten);
        Assert.Equal("Edited", doc.Info.Title);
    }

    [Fact]
    public void NullArgumentsAreRefused()
    {
        using PdfDocument doc = TagSupport.Editable(Make(T(72, 700, "Para one")));
        Assert.Throws<ArgumentNullException>(() => doc.Tag(null!, ByPrefix));
        Assert.Throws<ArgumentNullException>(() => doc.Tag(TagSupport.Extract(doc), null!));
    }

    [Fact]
    public void ForeignExtractionIsRefusedLikeInTheLowLevelApi()
    {
        using PdfDocument other = PdfDocument.Open(Make(T(72, 700, "Para one")));
        using PdfDocument doc = TagSupport.Editable(Make(T(72, 700, "Para one")));
        PdfTaggingException ex = Assert.Throws<PdfTaggingException>(() => doc.Tag(TagSupport.Extract(other), ByPrefix));
        Assert.Equal(PdfTaggingFailure.ForeignContent, ex.Reason);
    }
}
