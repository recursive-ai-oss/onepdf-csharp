namespace OnePdf.Tests.Creation;

public class ContentCreationTests
{
    private static PdfDocument NewDocWithPage(out PdfPage page, double w = 612, double h = 792)
    {
        PdfDocument doc = PdfDocument.CreateEmpty();
        page = doc.InsertBlankPage(0, w, h);
        return doc;
    }

    [Fact]
    public void StandardFontTextRoundTripsWithPosition()
    {
        using PdfDocument doc = NewDocWithPage(out PdfPage page);
        PdfCreatedFont helv = doc.AddStandardFont("Helvetica");
        using (PdfPageContentBuilder b = page.BeginContent())
        {
            b.SetFont(helv, 12);
            b.DrawText("Hello, (World) \\ café", 72, 700);
            b.Commit();
        }

        byte[] pdf = CreationSupport.Save(doc);
        Assert.Equal("Hello, (World) \\ café", CreationSupport.Normalize(CreationSupport.AllText(pdf)));

        using PdfDocument back = PdfDocument.Open(pdf);
        var c = new CreationSupport.Collector();
        back.Pages[0].Interpret(c, null);
        Assert.Single(c.Runs);
        // Output space is y-down from the top of the page: baseline at 792 - 700.
        Assert.Equal(72, c.Runs[0].X, 0.01);
        Assert.Equal(792 - 700, c.Runs[0].Y, 0.01);
        Assert.Equal(12, c.Runs[0].Size, 0.01);
    }

    [Fact]
    public void MeasureUsesAfmWidths()
    {
        using PdfDocument doc = NewDocWithPage(out _);
        PdfCreatedFont helv = doc.AddStandardFont("Helvetica");
        // H=722 e=556 l=222 l=222 o=556 in 1/1000 em.
        Assert.Equal((722 + 556 + 222 + 222 + 556) * 10 / 1000.0, helv.MeasureText("Hello", 10), 1e-9);
        Assert.Same(helv, doc.AddStandardFont("Helvetica"));
        Assert.Same(helv, doc.AddStandardFont("ArialMT"));
    }

    private const string Multi = "Hello Wörld ΑΒΓ αβγ Привет мир";

    [Theory]
    [InlineData("LiberationSans-Regular.ttf")]
    [InlineData("NotoSans-Regular.ttf")]
    public void EmbeddedFontTextRoundTripsAndSubsetIsSmall(string fontFile)
    {
        byte[] font = CreationSupport.RequireFont(fontFile);
        using PdfDocument doc = NewDocWithPage(out PdfPage page);
        PdfCreatedFont f = doc.AddFont(font);
        using (PdfPageContentBuilder b = page.BeginContent())
        {
            b.SetFont(f, 14);
            b.DrawText(Multi, 50, 600);
            b.DrawText("Second line", 50, 580);
            b.Commit();
        }

        byte[] pdf = CreationSupport.Save(doc);
        Assert.Equal(Multi + " Second line", CreationSupport.Normalize(CreationSupport.AllText(pdf)));
        Assert.Contains("+", f.BaseFontName);
        Assert.Equal(6, f.BaseFontName.IndexOf('+'));

        using PdfDocument back = PdfDocument.Open(pdf);
        byte[] program = FontProgramOf(back.Pages[0]);
        Assert.True(program.Length < font.Length / 8, $"subset {program.Length} of {font.Length}");
        var sfnt = OnePdf.FontPrograms.SfntFont.Parse(program, 0);
        Assert.True(sfnt.GlyphCount >= 20 && sfnt.GlyphCount < 60, sfnt.GlyphCount.ToString());

        var c = new CreationSupport.Collector();
        back.Pages[0].Interpret(c, null);
        Assert.Equal(2, c.Runs.Count);
        Assert.Equal(Multi, c.Runs[0].Text);
        Assert.Equal(50, c.Runs[0].X, 0.01);
        Assert.Equal(792 - 600, c.Runs[0].Y, 0.01);
        Console.WriteLine($"{fontFile}: font {font.Length} bytes, subset {program.Length} bytes, file {pdf.Length} bytes");
    }

    private static byte[] FontProgramOf(PdfPage page)
    {
        PdfDictionary fonts = page.Resources.GetDictionary("Font")!;
        foreach (string k in fonts.Keys)
        {
            PdfDictionary type0 = fonts.GetDictionary(k)!;
            PdfDictionary cid = (PdfDictionary)((PdfArray)type0.Get("DescendantFonts")!)[0]!;
            PdfDictionary desc = cid.GetDictionary("FontDescriptor")!;
            PdfStream file = desc.GetStream("FontFile2") ?? desc.GetStream("FontFile3")!;
            return file.GetDecodedBytes().ToArray();
        }

        throw new InvalidOperationException("no font");
    }
}
