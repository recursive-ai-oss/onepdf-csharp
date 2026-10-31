namespace OnePdf.Layout.Tests;

/// <summary>Converting layout output to PDF/A: text the default faces cover converts; a character they lack is drawn as .notdef, which is refused by name.</summary>
public class PdfAConvertTests
{
    private static PdfAConversionResult Convert(string content, MemoryStream output)
    {
        byte[] pdf = Support.Save(Support.Single(new Text { Content = content, Size = 12, Weight = 700 }));
        using var input = new MemoryStream(pdf);
        return PdfAConverter.Convert(input, output, new PdfAConversionOptions { Conformance = PdfAConformance.U });
    }

    [Fact]
    public void LayoutOutputConvertsToPdfA()
    {
        using var output = new MemoryStream();
        Assert.NotEmpty(Convert("plain text only \u00E9\u00E8 \u0416", output).Changes);
        using PdfDocument doc = PdfDocument.Open(output.ToArray());
        Assert.Empty(doc.CheckPdfA(new PdfAOptions { Part = PdfAPart.Part2, Conformance = PdfAConformance.U }));
    }

    [Fact]
    public void MissingGlyphIsRefusedAsNotdefNotAsAMissingFontGlyph()
    {
        using var output = new MemoryStream();
        PdfAException ex = Assert.Throws<PdfAException>(() => Convert("star \u2605 only", output));
        Assert.Contains(ex.Violations, v => v.Clause == "6.2.11.8" && v.Message.Contains(".notdef", StringComparison.Ordinal));
        Assert.DoesNotContain(ex.Violations, v => v.Clause == "6.2.11.4.1");
        Assert.Equal(0, output.Length);
    }
}
