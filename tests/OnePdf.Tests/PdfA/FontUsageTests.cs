using System.Text;
using OnePdf.FontPrograms;
using OnePdf.Tests.Content;
using OnePdf.PdfA;

namespace OnePdf.Tests.PdfA;

/// <summary>Which character codes a PDF/A save embeds for the standard fonts it substitutes.</summary>
public class FontUsageTests
{
    private static byte[] TwoFontPage(string content, string catalogEntries = "") => TestPdf.Build(
        content,
        "/Resources << /Font << /F1 20 0 R /F2 21 0 R >> /XObject << /Fm1 30 0 R >> >>",
        b =>
        {
            b.Obj(20, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
            b.Obj(21, "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding /WinAnsiEncoding >>");
            b.Stream(30, "/Type /XObject /Subtype /Form /BBox [0 0 100 100] /Resources << /Font << /F2 20 0 R >> >>", Encoding.ASCII.GetBytes("BT /F2 10 Tf (G) Tj ET"));
            b.Obj(5, "<< /Fields [] /DR << /Font << /Helv 22 0 R >> >> >>");
            b.Obj(22, "<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>");
        },
        objects: 40,
        catalogEntries: catalogEntries);

    private static string Codes(HashSet<int> set) => new string([.. set.OrderBy(static c => c).Select(static c => (char)c)]);

    [Fact]
    public void CodesAreAttributedToTheFontInForceWithTheGraphicsStateRespected()
    {
        byte[] pdf = TwoFontPage("BT /F1 12 Tf (AB) Tj q /F2 12 Tf [(C) -20 (D)] TJ Q (E) Tj ET /Fm1 Do");
        using PdfDocument doc = PdfDocument.Open(pdf);
        FontUsage usage = FontUsage.Scan(doc, [20, 21, 22], CancellationToken.None);
        Assert.True(usage.Complete);
        // Font 20 is F1 on the page and F2 inside the form; the Q returns to F1 for E.
        Assert.Equal("ABEG", Codes(usage.Used[20]));
        Assert.Equal("CD", Codes(usage.Used[21]));
        Assert.False(usage.Used.ContainsKey(22));
    }

    [Fact]
    public void FontsOfTheFormsDefaultResourcesKeepTheirWholeEncoding()
    {
        byte[] pdf = TwoFontPage("BT /Helv 12 Tf (X) Tj ET", "/AcroForm 5 0 R");
        using PdfDocument doc = PdfDocument.Open(pdf);
        FontUsage usage = FontUsage.Scan(doc, [20, 21, 22], CancellationToken.None);
        Assert.Contains(22, usage.Full);
        Assert.DoesNotContain(20, usage.Full);
    }

    [Fact]
    public void ADamagedFormStreamStillGivesTheCodesOfTheRest()
    {
        byte[] pdf = TestPdf.Build("BT /F1 12 Tf (A) Tj ET", "/Resources << /Font << /F1 20 0 R >> /XObject << /Fm1 30 0 R >> >>", b =>
        {
            b.Obj(20, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
            b.Stream(30, "/Type /XObject /Subtype /Form /BBox [0 0 1 1] /Filter /FlateDecode", [1, 2, 3]);
        }, objects: 40);
        using PdfDocument doc = PdfDocument.Open(pdf);
        FontUsage usage = FontUsage.Scan(doc, [20], CancellationToken.None);
        Assert.Equal("A", Codes(usage.Used[20]));
    }

    private static PdfDocument TwoFonts(out PdfCreatedFont helv)
    {
        PdfDocument d = PdfADocs.New();
        PdfPage page = d.InsertBlankPage(0, 300, 200);
        helv = d.AddStandardFont("Helvetica");
        PdfCreatedFont times = d.AddStandardFont("Times-Roman");
        using PdfPageContentBuilder b = page.BeginContent();
        b.SetFont(helv, 14);
        b.DrawText("Hello PDF/A", 20, 150);
        b.SetFont(times, 14);
        b.DrawText("World caf\u00E9", 20, 120);
        b.Commit();
        return d;
    }

    private static byte[] SaveA(PdfDocument d, PdfAConformance c = PdfAConformance.B)
    {
        using var ms = new MemoryStream();
        d.Save(ms, new PdfSaveOptions { DocumentId = new byte[16], PdfA = new PdfAOptions { Part = PdfAPart.Part2, Conformance = c } });
        return ms.ToArray();
    }

    private static int[] EmbeddedGlyphCounts(byte[] pdf)
    {
        using PdfDocument back = PdfDocument.Open(pdf);
        PdfDictionary fonts = back.Pages[0].Resources.GetDictionary("Font")!;
        var counts = new List<int>();
        foreach (string key in fonts.Keys)
        {
            PdfDictionary f = fonts.GetDictionary(key)!;
            byte[] ttf = f.GetDictionary("FontDescriptor")!.GetStream("FontFile2")!.GetDecodedBytes().ToArray();
            counts.Add(SfntFont.Parse(ttf, 0).GlyphCount);
        }

        return [.. counts];
    }

    [Fact]
    public void TheEmbeddedSubsetHoldsTheGlyphsTheDocumentDraws()
    {
        using PdfDocument d = TwoFonts(out _);
        byte[] pdf = SaveA(d);
        Assert.Equal("Hello PDF/A World caf\u00E9", CreationSupportNormalize(pdf));
        foreach (int glyphs in EmbeddedGlyphCounts(pdf))
        {
            // The text uses at most 12 distinct characters; the whole encoding used to be embedded (about 220 glyphs).
            Assert.InRange(glyphs, 2, 30);
        }

        using PdfDocument back = PdfDocument.Open(pdf);
        PdfDictionary fontDict = back.Pages[0].Resources.GetDictionary("Font")!;
        PdfDictionary f = fontDict.GetDictionary(fontDict.Keys.First())!;
        PdfArray widths = f.GetArray("Widths")!;
        Assert.Equal(f.GetInt("LastChar") - f.GetInt("FirstChar") + 1, widths.Count);
    }

    private static string CreationSupportNormalize(byte[] pdf) =>
        OnePdf.Tests.Creation.CreationSupport.Normalize(OnePdf.Tests.Creation.CreationSupport.AllText(pdf));

    [Fact]
    public void VeraPdfAcceptsTheSubsetsAndTheFileShrinks()
    {
        VeraPdf.Require();
        using PdfDocument d = TwoFonts(out _);
        byte[] pdf = SaveA(d);
        foreach ((string flavour, PdfAConformance c) in new[] { ("2b", PdfAConformance.B), ("2u", PdfAConformance.U) })
        {
            using PdfDocument d2 = TwoFonts(out _);
            List<string> failures = VeraPdf.Validate(SaveA(d2, c), flavour);
            Assert.True(failures.Count == 0, flavour + ": " + string.Join("\n", failures.Take(8)));
        }

        Console.WriteLine($"PDFA-SIZE two standard fonts, PDF/A-2b: {pdf.Length} bytes");
        Assert.True(pdf.Length < 120_000, $"{pdf.Length}");
    }
}
