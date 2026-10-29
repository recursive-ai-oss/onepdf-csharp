using OnePdf.Tests.Content;
using OnePdf.Tests.Creation;
using OnePdf.Tests.Tagging;
using static OnePdf.Tests.TagEditing.StructureDocs;

namespace OnePdf.Tests.TagEditing;

/// <summary>Empty structure elements, refreshing the typed tree after raw edits, and the read-only tagging probe.</summary>
public class StructureConvenienceTests
{
    // A table whose second row has one cell where the header row has two.
    private static byte[] MissingCell()
    {
        byte[] font = CreationSupport.RequireFont("LiberationSans-Regular.ttf");
        using var ms = new MemoryStream();
        using (var w = new PdfDocumentWriter(ms, new PdfDocumentWriterOptions { DocumentId = new byte[16], Language = "en-US", DisplayDocumentTitle = true, LeaveOpen = true }))
        {
            w.SetInfo("Title", "Missing cell");
            PdfCreatedFont f = w.AddFont(font, new PdfEmbedFontOptions());
            PdfStructureElement doc = w.BeginStructureElement("Document");
            PdfStreamPageBuilder p = w.BeginPage(300, 300);
            p.SetFont(f, 12);
            PdfStructureElement table = w.BeginStructureElement(doc, "Table");
            double y = 250;
            for (int r = 0; r < 2; r++)
            {
                PdfStructureElement row = w.BeginStructureElement(table, "TR");
                int cells = r == 0 ? 2 : 1;
                for (int c = 0; c < cells; c++)
                {
                    bool header = r == 0;
                    PdfStructureElement cell = w.BeginStructureElement(row, header ? "TH" : "TD", header ? new PdfStructureElementOptions { Attributes = PdfStructureAttributes.TableHeader("Column") } : null);
                    p.BeginTaggedContent(cell);
                    p.DrawText(header ? "Head" + c : "cell", 20 + 100 * c, y);
                    p.EndMarkedContent();
                    w.EndStructureElement(cell);
                }

                w.EndStructureElement(row);
                y -= 20;
            }

            w.EndStructureElement(table);
            p.Commit();
            w.EndStructureElement(doc);
            w.Finish();
        }

        return ms.ToArray();
    }

    private static PdfSaveOptions Ua => new() { PdfUA = new PdfUAOptions { Language = "en-US" } };

    [Fact]
    public void AnEmptyTdFillsAMissingCellAndThePdfUaSaveAndVeraPdfAgree()
    {
        byte[] src = MissingCell();
        List<string> before = Findings(src);
        Assert.Contains(before, static f => f.StartsWith("7.5", StringComparison.Ordinal));

        using PdfDocument doc = TagSupport.Editable(src);
        string extract = TagSupport.ExtractionJson(doc);
        PdfStructureNode row = Find(doc.StructureTree!, "TR", 1);
        Assert.Single(row.Children);
        PdfStructureNode cell = row.InsertChild("TD", 1);
        Assert.Equal("TD", cell.Type);
        Assert.Same(row, cell.Parent);
        Assert.Equal(1, cell.Index);
        Assert.Empty(cell.Children);
        Assert.Equal(row.Dictionary.Reference!.Number, ((PdfReference)cell.Dictionary.GetRaw("P")!).Number);
        PdfStructureNode first = row.InsertChild("TD", 0);
        Assert.Equal(0, first.Index);
        Assert.Throws<ArgumentOutOfRangeException>(() => row.InsertChild("TD", 9));
        Assert.Throws<ArgumentException>(() => row.InsertChild(""));
        first.Remove();
        Assert.True(first.IsRemoved);
        Assert.Throws<InvalidOperationException>(() => first.InsertChild("P"));

        byte[] saved = TagSupport.Save(doc, Ua);
        using (PdfDocument back = TagSupport.Editable(saved))
        {
            PdfStructureNode r = Find(back.StructureTree!, "TR", 1);
            Assert.Equal(["TD", "TD"], r.Children.Select(static c => ((PdfStructureNode)c).Type).ToArray());
            Assert.Empty(((PdfStructureNode)r.Children[1]).Children);
            Assert.Empty(back.StructureTree!.Diagnostics);
            Assert.Equal(extract, TagSupport.ExtractionJson(back));
            Assert.DoesNotContain(back.CheckPdfUA(), static v => v.Clause.StartsWith("7.5", StringComparison.Ordinal));
        }

        AssertConsistent(saved);
        List<string> after = Findings(saved);
        Assert.DoesNotContain(after, static f => f.StartsWith("7.5", StringComparison.Ordinal));
        if (OnePdf.Tests.PdfA.VeraPdf.Path is not null)
        {
            List<string> vb = Vera(src), va = Vera(saved);
            // veraPDF's profile does not flag the ragged row of this document; what matters is that the empty cell adds no failure.
            Assert.DoesNotContain(va, static r => r.StartsWith("7.5", StringComparison.Ordinal));
            Assert.True(va.All(vb.Contains), "new veraPDF rules: " + string.Join(", ", va.Except(vb)));
        }
    }

    [Fact]
    public void AnEmptyElementTakesContentMovedIntoItWithTheParentTreeFollowing()
    {
        using PdfDocument doc = TagSupport.Editable(Defective());
        PdfStructureNode empty = Find(doc.StructureTree!, "P", 1);
        PdfStructureNode inserted = empty.InsertChild("Span");
        PdfStructureNode heading = Find(doc.StructureTree!, "H1");
        PdfStructureItem content = heading.Children[0];
        content.MoveTo(inserted);
        byte[] saved = TagSupport.Save(doc);
        AssertConsistent(saved);
    }

    // ---- refreshing --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void RefreshingAfterRawEditsGivesCurrentNodesAndOldOnesThrow()
    {
        using PdfDocument doc = TagSupport.Editable(Defective());
        PdfStructureTree old = doc.StructureTree!;
        Assert.False(old.IsStale);
        PdfStructureNode p = Find(old, "P", 1);
        Assert.Empty(p.Children);

        // A raw edit: a Span child written straight into the dictionary.
        var span = new PdfDictionary();
        span.SetEntry("Type", new PdfName("StructElem"));
        span.SetEntry("S", new PdfName("Span"));
        span.SetEntry("P", p.Dictionary.Reference!);
        p.Dictionary.SetEntry("K", doc.AddObject(span));
        Assert.Same(old, doc.StructureTree);      // the same root: the property cannot see this edit
        Assert.Empty(p.Children);                  // and the model still shows what it read

        PdfStructureTree fresh = doc.RefreshStructureTree()!;
        Assert.NotSame(old, fresh);
        Assert.Same(fresh, doc.StructureTree);
        Assert.True(old.IsStale);
        Assert.Equal("Span", Assert.IsType<PdfStructureNode>(Assert.Single(Find(fresh, "P", 1).Children)).Type);

        Assert.Throws<InvalidOperationException>(() => p.Children);
        Assert.Throws<InvalidOperationException>(() => old.Children);
        Assert.Throws<InvalidOperationException>(() => old.Elements());
        Assert.Throws<InvalidOperationException>(() => p.InsertChild("Span"));
        Assert.Throws<InvalidOperationException>(() => p.SetType("Div"));
        Assert.Equal("P", p.Type); // dictionary-backed properties still read the live dictionary
    }

    [Fact]
    public void ReplacingTheRootObjectIsSeenWithoutAnExplicitRefresh()
    {
        using PdfDocument doc = TagSupport.Editable(Defective());
        PdfStructureTree old = doc.StructureTree!;
        PdfDictionary oldRoot = doc.StructureTreeRoot!;
        var root = new PdfDictionary();
        root.SetEntry("Type", new PdfName("StructTreeRoot"));
        root.SetEntry("K", oldRoot.GetRaw("K")!);
        doc.Catalog.SetEntry("StructTreeRoot", doc.AddObject(root));
        PdfStructureTree now = doc.StructureTree!;
        Assert.NotSame(old, now);
        Assert.True(old.IsStale);
        Assert.Equal(old.ElementCount, now.ElementCount);
    }

    [Fact]
    public void ThePdfUaPreparationKeepsTheTreeUnlessItRewritesIt()
    {
        using PdfDocument doc = TagSupport.Editable(Defective());
        PdfStructureTree tree = doc.StructureTree!;
        doc.PreparePdfUA(new PdfUAOptions { Language = "en-US" });
        Assert.Same(tree, doc.StructureTree);
        Assert.False(tree.IsStale);

        // Part 2 declares namespaces on the root in place, so the nodes read before it are replaced.
        doc.PreparePdfUA(new PdfUAOptions { Language = "en-US", Part = PdfUAPart.Part2 });
        Assert.True(tree.IsStale);
        PdfStructureTree after = doc.StructureTree!;
        Assert.NotSame(tree, after);
        Assert.Equal(tree.ElementCount, after.ElementCount);
    }

    [Fact]
    public void RefreshingADocumentWithoutATreeReturnsNull()
    {
        using PdfDocument doc = TagSupport.Editable(TestPdf.Build("0 0 10 10 re f"));
        Assert.Null(doc.RefreshStructureTree());
    }

    // ---- the probe ---------------------------------------------------------------------------------------------------------------

    private static PdfRemoveTagsResult Probe(byte[] pdf, bool editable = false)
    {
        using PdfDocument doc = PdfDocument.Open(pdf, new PdfOpenOptions { AllowEditing = editable });
        PdfRemoveTagsResult r = doc.RemoveTags(new PdfRemoveTagsOptions { DryRun = true });
        // Nothing the probe did shows in the document's objects.
        if (editable)
        {
            using PdfDocument other = PdfDocument.Open(pdf, new PdfOpenOptions { AllowEditing = true });
            Assert.Equal(other.Catalog.Keys.OrderBy(static k => k, StringComparer.Ordinal), doc.Catalog.Keys.OrderBy(static k => k, StringComparer.Ordinal));
            Assert.Equal(other.Pages[0].ContentStreams[0].GetDecodedBytes().ToArray(), doc.Pages[0].ContentStreams[0].GetDecodedBytes().ToArray());
        }

        return r;
    }

    [Fact]
    public void TheProbeSeesMarkedContentAndRemnantsWithoutChangingAnything()
    {
        byte[] untagged = TestPdf.Build("10 10 50 50 re f");
        PdfRemoveTagsResult u = Probe(untagged, editable: true);
        Assert.Equal(0, u.SequencesRemoved);
        Assert.False(u.StructureTreeRemoved);
        Assert.False(u.MarkInfoRemoved);
        Assert.Equal(0, u.StructParentsRemoved);

        byte[] mcidOnly = TestPdf.Build("/Span <</MCID 0>> BDC 10 10 50 50 re f EMC /Span <</MCID 1>> BDC 0 0 5 5 re f EMC /Artifact BMC EMC");
        PdfRemoveTagsResult m = Probe(mcidOnly, editable: true);
        Assert.Equal(2, m.SequencesRemoved);
        Assert.False(m.StructureTreeRemoved);
        Assert.False(m.MarkInfoRemoved);
        Assert.Equal(1, m.StreamsChanged);

        byte[] markInfo = TestPdf.Build("10 10 50 50 re f", catalogEntries: "/MarkInfo << /Marked true >>");
        PdfRemoveTagsResult mi = Probe(markInfo);
        Assert.True(mi.MarkInfoRemoved);
        Assert.False(mi.StructureTreeRemoved);
        Assert.Equal(0, mi.SequencesRemoved);

        byte[] treeOnly = TestPdf.Build("10 10 50 50 re f", pageEntries: "/StructParents 0", catalogEntries: "/StructTreeRoot << /Type /StructTreeRoot >>");
        PdfRemoveTagsResult t = Probe(treeOnly);
        Assert.True(t.StructureTreeRemoved);
        Assert.False(t.MarkInfoRemoved);
        Assert.Equal(1, t.StructParentsRemoved);

        PdfRemoveTagsResult tagged = Probe(Defective(), editable: true);
        Assert.True(tagged.StructureTreeRemoved);
        Assert.True(tagged.MarkInfoRemoved);
        Assert.True(tagged.SequencesRemoved > 5);
        Assert.True(tagged.StructParentsRemoved > 0);

        // The probe is the same count a real call returns.
        using PdfDocument real = TagSupport.Editable(Defective());
        PdfRemoveTagsResult actual = real.RemoveTags();
        Assert.Equal(actual.SequencesRemoved, tagged.SequencesRemoved);
        Assert.Equal(actual.StructParentsRemoved, tagged.StructParentsRemoved);
        Assert.Equal(actual.StreamsChanged, tagged.StreamsChanged);
    }

    [Fact]
    public void AProbeLeavesTheTypedTreeAlone()
    {
        using PdfDocument doc = TagSupport.Editable(Defective());
        PdfStructureTree tree = doc.StructureTree!;
        doc.RemoveTags(new PdfRemoveTagsOptions { DryRun = true });
        Assert.Same(tree, doc.StructureTree);
        Assert.False(tree.IsStale);
        Assert.NotEmpty(doc.StructureTree!.Children);
    }
}
