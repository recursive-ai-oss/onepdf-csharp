namespace OnePdf.Tests.Creation;

/// <summary>An empty document is built from written-out cross-reference data; it must match what reading the same bytes gives.</summary>
public class EmptyTemplateTests
{
    [Fact]
    public void BuiltCrossReferenceDataMatchesReadingTheBytes()
    {
        using PdfDocument built = PdfDocument.CreateEmpty();
        using PdfDocument read = PdfDocument.Open(PdfDocument.EmptyTemplate, new PdfOpenOptions { AllowEditing = true });

        Assert.Equal(read.Version, built.Version);
        Assert.Equal(read.ObjectCount, built.ObjectCount);
        Assert.Equal(read.Revisions.Count, built.Revisions.Count);
        Assert.Equal(read.PageCount, built.PageCount);
        Assert.Empty(built.Diagnostics);
        Assert.Equal(read.Trailer.GetRaw("Root")?.ToString(), built.Trailer.GetRaw("Root")?.ToString());
        Assert.Equal(read.Trailer.GetInt("Size"), built.Trailer.GetInt("Size"));
        Assert.Equal("Catalog", built.Catalog.GetName("Type"));
        Assert.Equal(0, built.Catalog.GetDictionary("Pages")!.GetArray("Kids")!.Count);
        Assert.Equal(0, built.Catalog.GetDictionary("Pages")!.GetInt("Count"));
    }

    [Fact]
    public void OffsetsPointAtTheObjectsAndTheTable()
    {
        string text = System.Text.Encoding.Latin1.GetString(PdfDocument.EmptyTemplate);
        Assert.StartsWith("1 0 obj", text[9..]);
        Assert.StartsWith("2 0 obj", text[58..]);
        Assert.StartsWith("xref", text[110..]);
        Assert.EndsWith("startxref\n110\n%%EOF\n", text);
    }

    [Fact]
    public void AnEmptyDocumentTakesPagesAndSaves()
    {
        using PdfDocument doc = PdfDocument.CreateEmpty();
        doc.InsertBlankPage(0, 200, 100);
        using var ms = new MemoryStream();
        doc.Save(ms);
        using PdfDocument back = PdfDocument.Open(ms.ToArray());
        Assert.Equal(1, back.PageCount);
    }
}
