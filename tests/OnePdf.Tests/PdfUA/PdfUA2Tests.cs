using OnePdf.Tests.Creation;
using OnePdf.Tests.PdfA;
using static OnePdf.Tests.PdfUA.PdfUATests;

namespace OnePdf.Tests.PdfUA;

/// <summary>
/// PDF/UA-2 (ISO 14289-2) on documents made with the streaming writer: the veraPDF validator's ua2 flavour (black box) accepts what a
/// part-2 save produces, each requirement OnePDF refuses is refused with its reason, and the files that are refused are also ones
/// veraPDF finds fault with.
/// </summary>
public class PdfUA2Tests
{
    private static readonly PdfUAOptions Part2 = new() { Part = PdfUAPart.Part2 };

    private static byte[] Save(byte[] built, PdfSaveOptions options)
    {
        using PdfDocument doc = Editable(built);
        using var ms = new MemoryStream();
        doc.Save(ms, options);
        return ms.ToArray();
    }

    [Fact]
    public void VeraPdfAcceptsPdfUA2()
    {
        VeraPdf.Require();
        byte[] pdf = Save(Build(new Spec { Language = null }), new PdfSaveOptions { DocumentId = new byte[16], PdfUA = new PdfUAOptions { Part = PdfUAPart.Part2, Language = "en-US" } });
        List<string> failures = VeraPdf.Validate(pdf, "ua2");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void SavedFileIsPdf20WithTheNamespaceAndTheRevision()
    {
        byte[] pdf = Save(Build(new Spec()), new PdfSaveOptions { DocumentId = new byte[16], PdfUA = Part2 });
        Assert.Equal("%PDF-2.0", System.Text.Encoding.ASCII.GetString(pdf, 0, 8));
        using PdfDocument back = PdfDocument.Open(pdf);
        Assert.Empty(back.CheckPdfUA(PdfUAPart.Part2));
        Assert.Equal(2, back.Xmp!.PdfUAPart);
        Assert.Equal(2024, back.Xmp.PdfUARevision);

        // Every element names the PDF 2.0 namespace, which the tree root lists.
        PdfDictionary root = back.StructureTreeRoot!;
        PdfArray namespaces = root.GetArray("Namespaces")!;
        Assert.Equal("http://iso.org/pdf2/ssn", ((PdfDictionary)namespaces[0]!).GetString("NS")!.GetText());
        PdfDictionary first = root.Get("K") is PdfArray a ? (PdfDictionary)a[0]! : (PdfDictionary)root.Get("K")!;
        Assert.Equal("http://iso.org/pdf2/ssn", first.GetDictionary("NS")!.GetString("NS")!.GetText());
    }

    [Fact]
    public void PartOneStaysPartOne()
    {
        byte[] pdf = Save(Build(new Spec()), new PdfSaveOptions { DocumentId = new byte[16], PdfUA = new PdfUAOptions() });
        using PdfDocument back = PdfDocument.Open(pdf);
        Assert.Equal(1, back.Xmp!.PdfUAPart);
        Assert.Null(back.Xmp.PdfUARevision);
        Assert.Null(back.StructureTreeRoot!.GetArray("Namespaces"));
        Assert.Empty(back.CheckPdfUA());
        Assert.NotEmpty(back.CheckPdfUA(PdfUAPart.Part2)); // a 1.7 file is not a PDF/UA-2 file
    }

    public static IEnumerable<object[]> Refusals()
    {
        yield return ["no title", new Spec { Title = false }, "dc:title"];
        yield return ["no language", new Spec { Language = null }, "/Lang"];
        yield return ["figure without alt text", new Spec { FigureAlt = false }, "Figure"];
        yield return ["irregular table", new Spec { IrregularTable = true }, "same number of columns"];
        yield return ["list with a wrong child", new Spec { ListWrongChildren = true }, "not a child of"];
        yield return ["unmapped structure type", new Spec { UnmappedType = true }, "names no structure namespace"];
        yield return ["untagged content", new Spec { UntaggedText = true }, "neither tagged"];
        yield return ["unembedded font", new Spec { EmbeddedFont = false }, "not embedded"];
        yield return ["link outside a Link element", new Spec { LinkInTree = false }, "not part of a Link"];
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public void EachViolationIsRefusedWithItsReason(string name, object spec, string expected)
    {
        byte[] built = Build((Spec)spec);
        using PdfDocument doc = Editable(built);
        using var ms = new MemoryStream();
        PdfUAException ex = Assert.Throws<PdfUAException>(() => doc.Save(ms, new PdfSaveOptions { PdfUA = Part2 }));
        Assert.True(ex.Violations.Any(v => v.Message.Contains(expected, StringComparison.Ordinal)), name + ": " + ex.Message);
        Assert.All(ex.Violations, v => Assert.Equal(2, v.Part));
        Assert.StartsWith("The document cannot be written as PDF/UA-2", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, ms.Length);
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public void VeraPdfAlsoFindsFaultWithWhatIsRefused(string name, object spec, string expected)
    {
        _ = expected;
        VeraPdf.Require();
        using PdfDocument doc = Editable(Build((Spec)spec));
        doc.PreparePdfUA(new PdfUAOptions { Part = PdfUAPart.Part2, Language = ((Spec)spec).Language is null ? null : "en-US" });
        using var ms = new MemoryStream();
        doc.Save(ms, new PdfSaveOptions { MinimumVersion = "2.0" });
        List<string> failures = VeraPdf.Validate(ms.ToArray(), "ua2");
        Assert.True(failures.Count > 0, name + ": veraPDF accepts it");
    }

    [Fact]
    public void PlainHeadingsAreRefusedAndOrderOfLevelsIsNot()
    {
        VeraPdf.Require();
        using PdfDocument plain = Editable(Build(new Spec { Headings = [1, 0] }));
        using var ms = new MemoryStream();
        PdfUAException ex = Assert.Throws<PdfUAException>(() => plain.Save(ms, new PdfSaveOptions { PdfUA = Part2 }));
        Assert.Contains(ex.Violations, v => v.Clause == "8.2.5.12");

        // The first level and skipped levels are people's judgement: neither the checker nor veraPDF's profile refuses them.
        byte[] odd = Save(Build(new Spec { Headings = [2, 4] }), new PdfSaveOptions { DocumentId = new byte[16], PdfUA = Part2 });
        Assert.Empty(VeraPdf.Validate(odd, "ua2"));
    }

    [Fact]
    public void ListsWithLabelsNeedANumberingAttribute()
    {
        VeraPdf.Require();
        using PdfDocument doc = Editable(Build(new Spec { ListNumbering = false }));
        using var ms = new MemoryStream();
        PdfUAException ex = Assert.Throws<PdfUAException>(() => doc.Save(ms, new PdfSaveOptions { PdfUA = Part2 }));
        Assert.Contains(ex.Violations, v => v.Clause == "8.2.5.25");
        doc.PreparePdfUA(Part2);
        using var o = new MemoryStream();
        doc.Save(o, new PdfSaveOptions { MinimumVersion = "2.0" });
        Assert.Contains(VeraPdf.Validate(o.ToArray(), "ua2"), f => f.Contains("8.2.5.25", StringComparison.Ordinal));
    }

    [Fact]
    public void TablesWithoutHeaderCellsAreNotRefusedInPart2()
    {
        VeraPdf.Require();
        byte[] pdf = Save(Build(new Spec { TableHeader = false }), new PdfSaveOptions { DocumentId = new byte[16], PdfUA = Part2 });
        Assert.Empty(VeraPdf.Validate(pdf, "ua2"));
    }

    [Fact]
    public void DestinationsInsideTheDocumentMustBeStructureDestinations()
    {
        VeraPdf.Require();
        using PdfDocument page = Editable(Build(new Spec { Bookmark = 1 }));
        using var ms = new MemoryStream();
        PdfUAException ex = Assert.Throws<PdfUAException>(() => page.Save(ms, new PdfSaveOptions { PdfUA = Part2 }));
        Assert.Contains(ex.Violations, v => v.Clause == "8.8");
        page.PreparePdfUA(Part2);
        using var o = new MemoryStream();
        page.Save(o, new PdfSaveOptions { MinimumVersion = "2.0" });
        Assert.Contains(VeraPdf.Validate(o.ToArray(), "ua2"), f => f.StartsWith("8.8-", StringComparison.Ordinal));

        byte[] structure = Save(Build(new Spec { Bookmark = 2 }), new PdfSaveOptions { DocumentId = new byte[16], PdfUA = Part2 });
        Assert.Empty(VeraPdf.Validate(structure, "ua2"));
        using PdfDocument back = PdfDocument.Open(structure);
        Assert.Empty(back.CheckPdfUA(PdfUAPart.Part2));
        Assert.True(back.Outline[0].Destination!.IsStructureDestination);
        Assert.Equal(0, back.Outline[0].Destination!.PageIndex);
    }

    public static IEnumerable<object[]> Defects()
    {
        yield return ["no /Tabs", (Action<PdfDocument>)(d => d.Pages[0].Dictionary.RemoveEntry("Tabs"))];
        yield return ["no /Lang", (Action<PdfDocument>)(d => d.Catalog.RemoveEntry("Lang"))];
        yield return ["no /MarkInfo", (Action<PdfDocument>)(d => d.Catalog.RemoveEntry("MarkInfo"))];
        yield return ["link without /StructParent", (Action<PdfDocument>)(d => d.Pages[0].Links[0].Dictionary.RemoveEntry("StructParent"))];
        yield return ["page without /StructParents", (Action<PdfDocument>)(d => d.Pages[0].Dictionary.RemoveEntry("StructParents"))];
        yield return ["malformed /Lang", (Action<PdfDocument>)(d => d.Catalog.SetEntry("Lang", PdfString.FromText("not a tag!")))];
        yield return ["no /DisplayDocTitle", (Action<PdfDocument>)(d => d.Catalog.RemoveEntry("ViewerPreferences"))];
        yield return ["no metadata", (Action<PdfDocument>)(d => d.Catalog.RemoveEntry("Metadata"))];
        yield return ["part 1 identification", (Action<PdfDocument>)(d => d.SetXmp(Xmp("<pdfuaid:part>1</pdfuaid:part>")))];
        yield return ["no revision", (Action<PdfDocument>)(d => d.SetXmp(Xmp("<pdfuaid:part>2</pdfuaid:part>")))];
    }

    private static byte[] Xmp(string identification) => System.Text.Encoding.UTF8.GetBytes(
        "<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
        + "<rdf:Description rdf:about=\"\" xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\">" + identification + "</rdf:Description>"
        + "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">T</rdf:li></rdf:Alt></dc:title></rdf:Description>"
        + "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>");

    [Theory]
    [MemberData(nameof(Defects))]
    public void EveryDefectVeraPdfFindsInAPrepared2FileIsAlsoFoundByTheCheck(string name, Action<PdfDocument> damage)
    {
        VeraPdf.Require();
        using PdfDocument doc = Editable(Build(new Spec()));
        doc.PreparePdfUA(Part2);
        damage(doc);
        using var ms = new MemoryStream();
        doc.Save(ms, new PdfSaveOptions { MinimumVersion = "2.0" });
        Assert.True(VeraPdf.Validate(ms.ToArray(), "ua2").Count > 0, name + ": veraPDF accepts the damaged file");
        using PdfDocument back = PdfDocument.Open(ms.ToArray());
        Assert.True(back.CheckPdfUA(PdfUAPart.Part2).Count > 0, name + ": the check finds nothing");
    }

    [Fact]
    public void PdfAAndUa2CannotBeCombined()
    {
        using PdfDocument doc = Editable(Build(new Spec()));
        using var ms = new MemoryStream();
        PdfAException ex = Assert.Throws<PdfAException>(() => doc.Save(ms, new PdfSaveOptions
        {
            PdfA = new PdfAOptions { Part = PdfAPart.Part2, Conformance = PdfAConformance.A },
            PdfUA = Part2,
        }));
        Assert.Contains(ex.Violations, v => v.Message.Contains("PDF 2.0", StringComparison.Ordinal));
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void CheckAgainstPartTwoListsWhatAPartOneFileLacks()
    {
        byte[] pdf = Save(Build(new Spec()), new PdfSaveOptions { PdfUA = new PdfUAOptions() });
        using PdfDocument doc = PdfDocument.Open(pdf);
        string all = string.Join("\n", doc.CheckPdfUA(PdfUAPart.Part2));
        Assert.Contains("PDF 1.7", all);
        Assert.Contains("pdfuaid:part 2", all);
        Assert.Contains("names no structure namespace", all);
    }

    [Fact]
    public void PreparingTwiceChangesNothingMore()
    {
        using PdfDocument doc = Editable(Build(new Spec()));
        Assert.Empty(doc.PreparePdfUA(Part2));
        int objects = doc.ObjectCount;
        Assert.Empty(doc.PreparePdfUA(Part2));
        Assert.Equal(objects, doc.ObjectCount);
    }
}
