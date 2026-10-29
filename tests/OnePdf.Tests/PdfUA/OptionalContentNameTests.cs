using OnePdf.Tests.PdfA;
using OnePdf.Tests.TagEditing;
using OnePdf.Tests.Tagging;

namespace OnePdf.Tests.PdfUA;

/// <summary>ISO 14289-1 7.10: each optional content configuration dictionary has a /Name.</summary>
[Collection("PdfUA structure and veraPDF")]
public class OptionalContentNameTests
{
    private static PdfDocument WithConfigurations(bool nameDefault, bool nameExtra)
    {
        PdfDocument doc = TagSupport.Editable(HeaderScopeDocs.Table(["TH:Column", "TH:Column"], ["TD", "TD"]));
        var group = new PdfDictionary();
        group.SetEntry("Type", new PdfName("OCG"));
        group.SetEntry("Name", PdfString.FromText("Layer"));
        PdfObject groupRef = doc.AddObject(group);
        var groups = new PdfArray();
        groups.Append(groupRef);

        var d = new PdfDictionary();
        if (nameDefault)
        {
            d.SetEntry("Name", PdfString.FromText("Everything"));
        }

        var extra = new PdfDictionary();
        if (nameExtra)
        {
            extra.SetEntry("Name", PdfString.FromText("Print"));
        }

        var configs = new PdfArray();
        configs.Append(extra);
        var props = new PdfDictionary();
        props.SetEntry("OCGs", groups);
        props.SetEntry("D", d);
        props.SetEntry("Configs", configs);
        doc.Catalog.SetEntry("OCProperties", props);
        return doc;
    }

    private static bool Reported(PdfDocument doc, int expected) =>
        doc.CheckPdfUA().Count(static v => v.Clause == "7.10") == expected;

    [Fact]
    public void ConfigurationsWithoutANameAreReported()
    {
        using PdfDocument doc = WithConfigurations(nameDefault: false, nameExtra: false);
        Assert.True(Reported(doc, 2));
    }

    [Fact]
    public void ConfigurationsWithANameAreNot()
    {
        using PdfDocument doc = WithConfigurations(nameDefault: true, nameExtra: true);
        Assert.True(Reported(doc, 0));
    }

    [Fact]
    public void OnlyTheMissingNameIsReported()
    {
        using PdfDocument doc = WithConfigurations(nameDefault: true, nameExtra: false);
        Assert.True(Reported(doc, 1));
        Assert.Contains("Configs entry 0", doc.CheckPdfUA().First(static v => v.Clause == "7.10").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PrepareNamesTheConfigurationsAndKeepsTheNamesThereAre()
    {
        using PdfDocument doc = WithConfigurations(nameDefault: true, nameExtra: false);
        byte[] before = TagSupport.Save(doc);
        if (VeraPdf.Path is not null)
        {
            Assert.Contains(StructureDocs.Vera(before), static r => r.StartsWith("7.10-1", StringComparison.Ordinal));
        }

        Assert.DoesNotContain(doc.PreparePdfUA(new PdfUAOptions { Language = "en" }), static v => v.Clause == "7.10");
        PdfDictionary props = doc.Catalog.GetDictionary("OCProperties")!;
        Assert.Equal("Everything", props.GetDictionary("D")!.GetString("Name")!.GetText());
        Assert.Equal("Configuration 2", (props.GetArray("Configs")![0] as PdfDictionary)!.GetString("Name")!.GetText());

        byte[] saved = TagSupport.Save(doc);
        if (VeraPdf.Path is not null)
        {
            Assert.DoesNotContain(StructureDocs.Vera(saved), static r => r.StartsWith("7.10-", StringComparison.Ordinal));
        }
    }
}
