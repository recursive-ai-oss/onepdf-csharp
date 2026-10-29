using OnePdf.Tests.PdfA;
using OnePdf.Tests.TagEditing;
using OnePdf.Tests.Tagging;

namespace OnePdf.Tests.PdfUA;

/// <summary>
/// ISO 14289-1 7.5: a table whose data cells cannot find their headers needs a /Scope on its header cells. The expected answers were
/// observed on veraPDF (black box) with the same tables; when it is installed each case is also put to it.
/// </summary>
[Collection("PdfUA structure and veraPDF")]
public class HeaderScopeTests
{
    public static IEnumerable<object[]> Cases()
    {
        // name, rows, header cells are in breach
        yield return ["every header has a scope", new[] { new[] { "TH:Column", "TH:Column" }, ["TD", "TD"] }, false];
        yield return ["unscoped headers over data", new[] { new[] { "TH", "TH" }, ["TD", "TD"] }, true];
        yield return ["first header unscoped", new[] { new[] { "TH", "TH:Column" }, ["TD", "TD"] }, true];
        yield return ["extra empty unscoped header", new[] { new[] { "TH:Column", "TH:Column", "TH!" }, ["TD", "TD", "TD"] }, true];
        yield return ["unscoped row header under scoped columns", new[] { new[] { "TH:Column", "TH:Column" }, ["TH", "TD"] }, false];
        yield return ["only header cells", new[] { new[] { "TH", "TH" } }, false];
        yield return ["row scope in the header row", new[] { new[] { "TH:Row", "TH:Row" }, ["TD", "TD"] }, false];
        yield return ["Headers lists name every header", new[] { new[] { "TH#a", "TH#b" }, ["TD>a", "TD>b"] }, false];
        yield return ["Headers on one data cell only", new[] { new[] { "TH#a", "TH#b" }, ["TD>a", "TD"] }, true];
        yield return ["Headers names an undefined id", new[] { new[] { "TH#a", "TH#b" }, ["TD>x", "TD>b"] }, true];
        yield return ["unscoped column header beside a scoped one", new[] { new[] { "TH:Column", "TH" }, ["TD", "TD"], ["TD", "TD"] }, true];
        yield return ["unscoped row header with scoped column above", new[] { new[] { "TH:Column", "TH:Column" }, ["TH", "TD"], ["TD", "TD"] }, false];
        yield return ["unscoped headers beside data that a scoped header reaches", new[] { new[] { "TH:Column", "TH:Column", "TH" }, ["TD", "TD", "TH"] }, false];
        yield return ["row header unscoped under a row-scope header", new[] { new[] { "TH:Row", "TD" }, ["TH", "TD"] }, true];
        yield return ["row headers scoped", new[] { new[] { "TH:Row", "TD" }, ["TH:Row", "TD"] }, false];
        yield return ["column scope placed to the right of the data", new[] { new[] { "TD", "TH:Column" }, ["TH", "TH"] }, true];
        yield return ["column scope below the data", new[] { new[] { "TD" }, ["TH:Column"], ["TH"] }, true];
        yield return ["row scope above the data", new[] { new[] { "TH:Row", "TH" }, ["TD", "TD"] }, true];
        yield return ["unscoped header far from data without a header", new[] { new[] { "TH:Column", "TD", "TD" }, ["TD", "TD", "TD"], ["TH", "TD", "TD"] }, true];
        yield return ["column header spanning two columns", new[] { new[] { "TH:Column*2x1" }, ["TD", "TD"], ["TH", "TD"] }, false];
        yield return ["scoped headers leave unreached data alone", new[] { new[] { "TH:Row", "TD" }, ["TD", "TD"] }, false];
        yield return ["irregular rows: the width finding applies, not this one", new[] { new[] { "TH", "TD" }, ["TH"] }, false];
        yield return ["irregular rows through a column span", new[] { new[] { "TH*2x1", "TD" }, ["TD", "TD"] }, false];
        yield return ["row span, regular", new[] { new[] { "TH*1x2", "TD" }, ["TD"] }, true];
        yield return ["Both reaches down and right", new[] { new[] { "TH", "TH:Column" }, ["TH:Both", "TD"], ["TD", "TD"] }, false];
    }

    private static bool Reported(byte[] pdf) =>
        StructureDocs.Findings(pdf).Exists(static f => f.Contains("has no /Scope", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheCheckAgreesWithTheCases(string name, string[][] rows, bool breach)
    {
        byte[] pdf = HeaderScopeDocs.Table(rows);
        Assert.True(breach == Reported(pdf), name);
        if (VeraPdf.Path is not null)
        {
            bool vera = StructureDocs.Vera(pdf).Exists(static r => r.StartsWith("7.5-", StringComparison.Ordinal));
            Assert.True(breach == vera, name + " (veraPDF)");
        }
    }

    [Fact]
    public void TheFindingNamesTheHeaderCellsAndTheDataCellsWithoutHeaders()
    {
        byte[] pdf = HeaderScopeDocs.Table(["TH", "TH:Column", "TH"], ["TD", "TD", "TD"]);
        using PdfDocument doc = PdfDocument.Open(pdf);
        List<PdfUAViolation> found = [.. doc.CheckPdfUA().Where(static v => v.Message.Contains("has no /Scope", StringComparison.Ordinal))];
        Assert.Equal(2, found.Count);
        Assert.All(found, static v => Assert.Equal("7.5", v.Clause));
        Assert.All(found, static v => Assert.Equal("15", v.Checkpoint));
        Assert.All(found, static v => Assert.True(v.ObjectNumber > 0));
        Assert.Contains("2 data cells", found[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PrepareSetsTheScopeARowMakesPlain()
    {
        // A row of header cells heads its columns; the first cell of a row with data heads the row.
        byte[] pdf = HeaderScopeDocs.Table(["TH", "TH", "TH"], ["TH", "TD", "TD"], ["TH", "TD", "TD"]);
        Assert.True(Reported(pdf));
        using PdfDocument doc = TagSupport.Editable(pdf);
        IReadOnlyList<PdfUAViolation> left = doc.PreparePdfUA(new PdfUAOptions { Language = "en" });
        Assert.DoesNotContain(left, static v => v.Message.Contains("has no /Scope", StringComparison.Ordinal));

        PdfStructureTree tree = doc.StructureTree!;
        var scopes = tree.Elements().Where(static n => n.Type == "TH").Select(static n => n.Scope).ToList();
        Assert.Equal([PdfTagScope.Column, PdfTagScope.Column, PdfTagScope.Column, PdfTagScope.Row, PdfTagScope.Row], scopes);

        byte[] saved = TagSupport.Save(doc);
        Assert.False(Reported(saved));
        if (VeraPdf.Path is not null)
        {
            Assert.DoesNotContain(StructureDocs.Vera(saved), static r => r.StartsWith("7.5-", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void PrepareLeavesAnUnclearHeaderCellAsAViolation()
    {
        // The third cell of a row of mixed cells: it is not plain what it heads.
        byte[] pdf = HeaderScopeDocs.Table(["TH:Column", "TD", "TH", "TD"], ["TD", "TD", "TD", "TD"]);
        using PdfDocument doc = TagSupport.Editable(pdf);
        IReadOnlyList<PdfUAViolation> left = doc.PreparePdfUA(new PdfUAOptions { Language = "en" });
        Assert.Contains(left, static v => v.Message.Contains("has no /Scope", StringComparison.Ordinal));
        Assert.All(doc.StructureTree!.Elements().Where(static n => n.Type == "TH"), static n => Assert.True(n.Scope is null or PdfTagScope.Column));
        Assert.Null(doc.StructureTree!.Elements().Where(static n => n.Type == "TH").Last().Scope);

        var ex = Assert.Throws<PdfUAException>(() =>
        {
            using var ms = new MemoryStream();
            doc.Save(ms, new PdfSaveOptions { PdfUA = new PdfUAOptions { Language = "en" } });
        });
        Assert.Contains(ex.Violations, static v => v.Message.Contains("has no /Scope", StringComparison.Ordinal));
    }

    [Fact]
    public void PrepareDoesNotTouchTablesThatAreNotInBreach()
    {
        // E: the unscoped row header is left as it is, because its data cell already has a column header.
        byte[] pdf = HeaderScopeDocs.Table(["TH:Column", "TH:Column"], ["TH", "TD"]);
        using PdfDocument doc = TagSupport.Editable(pdf);
        doc.PreparePdfUA(new PdfUAOptions { Language = "en" });
        Assert.Null(doc.StructureTree!.Elements().Where(static n => n.Type == "TH").Last().Scope);
    }

    [Fact]
    public void AHeaderCellInsertedWithoutAScopeIsCaughtAndRepaired()
    {
        byte[] pdf = HeaderScopeDocs.Table(["TH:Column", "TH:Column"], ["TD", "TD"]);
        using PdfDocument doc = TagSupport.Editable(pdf);
        PdfStructureNode header = doc.StructureTree!.Elements().First(static n => n.Type == "TR");
        PdfStructureNode body = doc.StructureTree!.Elements().Where(static n => n.Type == "TR").Last();
        header.InsertChild("TH");
        body.InsertChild("TD");
        byte[] saved = TagSupport.Save(doc);

        Assert.True(Reported(saved));
        if (VeraPdf.Path is not null)
        {
            Assert.Contains(StructureDocs.Vera(saved), static r => r.StartsWith("7.5-1", StringComparison.Ordinal));
        }

        using PdfDocument again = TagSupport.Editable(saved);
        Assert.DoesNotContain(again.PreparePdfUA(new PdfUAOptions { Language = "en" }), static v => v.Message.Contains("has no /Scope", StringComparison.Ordinal));
        Assert.Equal(PdfTagScope.Column, again.StructureTree!.Elements().Where(static n => n.Type == "TH").Last().Scope);
        byte[] fixedUp = TagSupport.Save(again);
        Assert.False(Reported(fixedUp));
        if (VeraPdf.Path is not null)
        {
            Assert.DoesNotContain(StructureDocs.Vera(fixedUp), static r => r.StartsWith("7.5-", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void AScopeGivenByAClassCounts()
    {
        byte[] pdf = HeaderScopeDocs.Table(["TH", "TH"], ["TD", "TD"]);
        using PdfDocument doc = TagSupport.Editable(pdf);
        var attr = new PdfDictionary();
        attr.SetEntry("O", new PdfName("Table"));
        attr.SetEntry("Scope", new PdfName("Column"));
        var classMap = new PdfDictionary();
        classMap.SetEntry("Head", attr);
        doc.StructureTreeRoot!.SetEntry("ClassMap", classMap);
        foreach (PdfStructureNode th in doc.StructureTree!.Elements().Where(static n => n.Type == "TH"))
        {
            th.Dictionary.SetEntry("C", new PdfName("Head"));
        }

        Assert.DoesNotContain(doc.CheckPdfUA(), static v => v.Message.Contains("has no /Scope", StringComparison.Ordinal));
    }
}
