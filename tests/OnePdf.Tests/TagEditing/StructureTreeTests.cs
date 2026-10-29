using OnePdf.Tests.Tagging;
using OnePdf.Text;
using static OnePdf.Tests.TagEditing.StructureDocs;

namespace OnePdf.Tests.TagEditing;

/// <summary>The structure tree model: reading, and each edit judged by the PDF/UA checker, veraPDF and the parent tree.</summary>
public class StructureTreeTests
{
    private static PdfDocument Open(byte[] pdf) => TagSupport.Editable(pdf);

    /// <summary>Applies the edit, saves, and returns the bytes; also checks that pages read and draw as before and the tree is consistent.</summary>
    private static byte[] Edited(byte[] src, Action<PdfStructureTree> edit)
    {
        using PdfDocument doc = Open(src);
        string extract = TagSupport.ExtractionJson(doc);
        string display = TagSupport.DisplayText(doc);
        edit(doc.StructureTree!);
        byte[] saved = TagSupport.Save(doc);
        using PdfDocument after = Open(saved);
        TagSupport.AssertSame(extract, TagSupport.ExtractionJson(after));
        TagSupport.AssertSame(display, TagSupport.DisplayText(after));
        AssertConsistent(saved);
        return saved;
    }

    /// <summary>The edit removes the findings that contain <paramref name="targeted"/> and adds none.</summary>
    private static byte[] FixesFinding(Action<PdfStructureTree> edit, string? targeted, string veraRule)
    {
        byte[] src = Defective();
        byte[] saved = Edited(src, edit);
        List<string> before = Findings(src), after = Findings(saved);
        if (targeted is not null)
        {
            Assert.Contains(before, f => f.Contains(targeted, StringComparison.Ordinal));
            Assert.DoesNotContain(after, f => f.Contains(targeted, StringComparison.Ordinal));
        }

        Assert.True(after.All(before.Contains), "new findings: " + string.Join(" | ", after.Except(before)));
        if (VeraPdfAvailable)
        {
            List<string> vb = Vera(src), va = Vera(saved);
            Assert.Contains(veraRule, vb);
            Assert.DoesNotContain(veraRule, va);
            Assert.True(va.All(vb.Contains), "new veraPDF rules: " + string.Join(", ", va.Except(vb)));
        }

        return saved;
    }

    private static bool VeraPdfAvailable => OnePdf.Tests.PdfA.VeraPdf.Path is not null;

    [Fact]
    public void ReadsTheTreeAsTypedObjects()
    {
        using PdfDocument doc = Open(Defective());
        PdfStructureTree tree = doc.StructureTree!;
        Assert.Empty(tree.Diagnostics);
        Assert.Same(tree, doc.StructureTree);
        PdfStructureNode root = Assert.IsType<PdfStructureNode>(Assert.Single(tree.Children));
        Assert.Equal("Document", root.Type);
        Assert.Equal("Document", root.StandardType);
        PdfStructureNode strong = Find(tree, "Strong");
        Assert.Null(strong.StandardType);
        Assert.Equal("strong words", strong.GetText());
        Assert.Equal(0, strong.PageIndex);
        Assert.Equal("P", strong.Parent!.Type);
        Assert.Equal("Body text with strong words emphasis", strong.Parent.GetText());
        PdfStructureNode th = Find(tree, "TH");
        Assert.Null(th.Scope);
        Assert.Equal("Name", th.GetText());
        Assert.Equal(tree.ElementCount, tree.Elements().Count());
        Assert.Empty(tree.FindMissingContent());
        Assert.Empty(Find(tree, "P", 1).Children);
    }

    [Fact]
    public void ReadingChangesNothing()
    {
        byte[] src = Defective();
        using PdfDocument plain = Open(src);
        byte[] expected = TagSupport.Save(plain);
        using PdfDocument read = Open(src);
        PdfStructureTree tree = read.StructureTree!;
        foreach (PdfStructureNode n in tree.Elements())
        {
            _ = (n.Type, n.StandardType, n.Alt, n.Scope, n.PageIndex, n.Attributes, n.GetText());
        }

        _ = tree.FindMissingContent();
        Assert.True(expected.AsSpan().SequenceEqual(TagSupport.Save(read)));
    }

    [Fact]
    public void FixesATableHeaderWithoutScope()
    {
        byte[] saved = FixesFinding(t =>
        {
            foreach (PdfStructureNode th in t.Elements().Where(static n => n.Type == "TH").ToList())
            {
                th.Scope = PdfTagScope.Column;
            }
        }, null, "7.5-1");
        using PdfDocument d = PdfDocument.Open(saved);
        Assert.Equal(PdfTagScope.Column, Find(d.StructureTree!, "TH").Scope);
    }

    [Fact]
    public void AddsAlternateTextToAFigure()
    {
        byte[] saved = FixesFinding(t => Find(t, "Figure").Alt = "A blue bar", "alternate text", "7.3-1");
        using PdfDocument d = PdfDocument.Open(saved);
        Assert.Equal("A blue bar", Find(d.StructureTree!, "Figure").Alt);
    }

    [Fact]
    public void MapsCustomTypesToStandardOnes()
    {
        byte[] saved = FixesFinding(t =>
        {
            t.MapRole("Strong", "Span");
            t.MapRole("Em", "Span");
        }, "not a standard type", "7.1-5");
        using PdfDocument d = PdfDocument.Open(saved);
        Assert.Equal("Span", Find(d.StructureTree!, "Strong").StandardType);
        Assert.Equal("Span", d.StructureTree!.RoleMap["Em"]);
    }

    [Fact]
    public void ChangingTheTypeFixesTheFinding()
    {
        FixesFinding(t =>
        {
            Find(t, "Strong").SetType(PdfTagRole.Span);
            Find(t, "Em").SetType("Span");
        }, "not a standard type", "7.1-5");
    }

    [Fact]
    public void RemovesTheSpanInsideAList()
    {
        byte[] saved = FixesFinding(t => Find(t, "Span").Remove(PdfStructureRemoval.Unwrap), "contains a Span", "7.2-19");
        using PdfDocument d = PdfDocument.Open(saved);
        PdfStructureNode list = Find(d.StructureTree!, "L");
        Assert.Equal(["LI"], list.Children.OfType<PdfStructureNode>().Select(static n => n.Type));
        Assert.Equal("1. first item", list.GetText());
    }

    [Fact]
    public void DropsAnEmptyElement()
    {
        byte[] src = Defective();
        byte[] saved = Edited(src, t => Find(t, "P", 1).Remove(PdfStructureRemoval.Unwrap));
        using PdfDocument d = PdfDocument.Open(saved);
        Assert.Equal(1, d.StructureTree!.Elements().Count(static n => n.Type == "P"));
        using PdfDocument before = PdfDocument.Open(src);
        Assert.Equal(before.StructureTree!.ElementCount - 1, d.StructureTree.ElementCount);
        Assert.True(Findings(saved).All(Findings(src).Contains));
    }

    [Fact]
    public void RemovesAFigureWithItsContentAsArtifacts()
    {
        byte[] src = Defective();
        byte[] saved = Edited(src, t => Find(t, "Figure").Remove(PdfStructureRemoval.ToArtifacts));
        using PdfDocument d = PdfDocument.Open(saved);
        Assert.DoesNotContain(d.StructureTree!.Elements(), static n => n.Type == "Figure");
        string content = string.Concat(d.Pages[0].ContentStreams.Select(static s => System.Text.Encoding.Latin1.GetString(s.GetDecodedBytes().Span)));
        Assert.Contains("/Artifact BMC", content, StringComparison.Ordinal);
        List<string> after = Findings(saved);
        Assert.DoesNotContain(after, static f => f.Contains("neither tagged", StringComparison.Ordinal));
        Assert.DoesNotContain(after, static f => f.Contains("alternate text", StringComparison.Ordinal));
        Assert.True(after.All(Findings(src).Contains));
        if (VeraPdfAvailable)
        {
            List<string> vb = Vera(src), va = Vera(saved);
            Assert.True(va.All(vb.Contains), "new veraPDF rules: " + string.Join(", ", va.Except(vb)));
        }
    }

    [Fact]
    public void WrapsChildrenInANewElement()
    {
        byte[] src = Defective();
        byte[] saved = Edited(src, t =>
        {
            PdfStructureNode para = Find(t, "P");
            PdfStructureNode wrapper = para.WrapChildren("Div", 1, 2);
            Assert.Equal(["Strong", "Em"], wrapper.Children.OfType<PdfStructureNode>().Select(static n => n.Type));
            Assert.Same(para, wrapper.Parent);
        });
        using PdfDocument d = PdfDocument.Open(saved);
        PdfStructureNode p = Find(d.StructureTree!, "P");
        Assert.Equal(["Div"], p.Children.OfType<PdfStructureNode>().Select(static n => n.Type));
        Assert.Equal("Body text with strong words emphasis", p.GetText());
        Assert.Single(p.Children.OfType<PdfMarkedContentReference>());
    }

    [Fact]
    public void WrapsContentReferencesAndKeepsTheParentTree()
    {
        byte[] saved = Edited(Defective(), t =>
        {
            PdfStructureNode lbody = Find(t, "LBody");
            PdfStructureNode inner = lbody.WrapChildren("Span", 0, 1);
            Assert.IsType<PdfMarkedContentReference>(Assert.Single(inner.Children));
        });
        using PdfDocument d = PdfDocument.Open(saved);
        Assert.Equal("first item", Find(d.StructureTree!, "LBody").Children.OfType<PdfStructureNode>().Single().GetText());
    }

    [Fact]
    public void MovesAnElement()
    {
        byte[] src = Defective();
        byte[] saved = Edited(src, t =>
        {
            PdfStructureNode root = (PdfStructureNode)t.Children[0];
            PdfStructureNode heading = Find(t, "H1");
            PdfStructureNode table = Find(t, "Table");
            table.MoveTo(root, 0);
            heading.MoveTo(Find(t, "TD"), 0);
            Assert.Equal(0, table.Index);
            Assert.Throws<ArgumentException>(() => root.MoveTo(table));
            Assert.Throws<ArgumentOutOfRangeException>(() => table.MoveTo(root, 99));
        });
        using PdfDocument d = PdfDocument.Open(saved);
        PdfStructureNode r = (PdfStructureNode)d.StructureTree!.Children[0];
        Assert.Equal("Table", ((PdfStructureNode)r.Children[0]).Type);
        Assert.Equal("Annual report", Find(d.StructureTree!, "TD").GetText().Split(' ')[0] + " report");
    }

    [Fact]
    public void MovingContentToAnotherElementUpdatesTheParentTree()
    {
        byte[] saved = Edited(Defective(), t =>
        {
            PdfStructureNode heading = Find(t, "H1");
            PdfMarkedContentReference mc = heading.Children.OfType<PdfMarkedContentReference>().Single();
            PdfStructureNode lbody = Find(t, "LBody");
            mc.MoveTo(lbody, 0);
            Assert.Empty(heading.Children);
        });
        using PdfDocument d = PdfDocument.Open(saved);
        Assert.Equal("Annual report first item", Find(d.StructureTree!, "LBody").GetText());
    }

    [Fact]
    public void EditsNeedAnEditableDocumentAndALiveItem()
    {
        using PdfDocument ro = PdfDocument.Open(Defective());
        Assert.Throws<InvalidOperationException>(() => Find(ro.StructureTree!, "Figure").Alt = "x");
        Assert.Throws<InvalidOperationException>(() => ro.StructureTree!.MapRole("a", "b"));
        using PdfDocument doc = Open(Defective());
        PdfStructureNode span = Find(doc.StructureTree!, "Span");
        span.Remove();
        Assert.True(span.IsRemoved);
        Assert.Throws<InvalidOperationException>(() => span.Alt = "x");
        Assert.Throws<InvalidOperationException>(() => span.Remove());
    }

    [Fact]
    public void SpansAndAttributesRoundTrip()
    {
        byte[] saved = Edited(Defective(), t =>
        {
            PdfStructureNode td = Find(t, "TD");
            td.ColSpan = 2;
            td.RowSpan = 3;
            Find(t, "L").ListNumbering = "Decimal";
            td.Language = "fr";
            td.Title = "cell";
            td.ActualText = "A";
            td.Expansion = "expanded";
            Assert.Equal(2, td.ColSpan);
            Assert.Single(td.Attributes);
        });
        using PdfDocument d = PdfDocument.Open(saved);
        PdfStructureNode cell = Find(d.StructureTree!, "TD");
        Assert.Equal((2, 3, "fr", "cell", "A", "expanded"), (cell.ColSpan, cell.RowSpan, cell.Language, cell.Title, cell.ActualText, cell.Expansion));
        Assert.Equal("Decimal", Find(d.StructureTree!, "L").ListNumbering);
        using PdfDocument e = Open(saved);
        PdfStructureNode c2 = Find(e.StructureTree!, "TD");
        c2.ColSpan = null;
        c2.RowSpan = null;
        Assert.Empty(c2.Attributes);
        c2.Language = null;
        Assert.Null(c2.Language);
    }

    [Fact]
    public void EditedDocumentSavesIncrementallyAndWithPdfUA()
    {
        byte[] src = Defective();
        using PdfDocument doc = Open(src);
        PdfStructureTree t = doc.StructureTree!;
        foreach (PdfStructureNode th in t.Elements().Where(static n => n.Type == "TH").ToList())
        {
            th.Scope = PdfTagScope.Column;
        }

        Find(t, "Figure").Alt = "A blue bar";
        t.MapRole("Strong", "Span");
        t.MapRole("Em", "Span");
        Find(t, "Span").Remove();
        Find(t, "P", 1).Remove();
        byte[] inc = TagSupport.SaveIncremental(doc);
        Assert.True(inc.AsSpan(0, src.Length).SequenceEqual(src));
        AssertConsistent(inc);
        Assert.Empty(Findings(inc));
        byte[] ua = TagSupport.Save(doc, new PdfSaveOptions { PdfUA = new PdfUAOptions() });
        Assert.Empty(Findings(ua));
        if (VeraPdfAvailable)
        {
            Assert.Empty(Vera(ua));
            Assert.Empty(Vera(inc));
        }
    }
}
