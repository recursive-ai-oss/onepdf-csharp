namespace OnePdf.Layout.Tests;

/// <summary>Colour glyphs: OpenType COLR version 0 layers drawn in CPAL palette colours, palette choice, fallbacks for tables that cannot be used, and extraction.</summary>
public class ColorGlyphTests
{
    private const string Family = "ColorTest";

    private sealed record Run(double R, double G, double B, string? Text, int Glyphs);

    private sealed class Capture : PdfDevice
    {
        public List<Run> Runs { get; } = [];

        public override void ShowGlyphRun(in PdfGlyphRun run, in PdfGraphicsState state)
        {
            string text = string.Concat(run.Glyphs.ToArray().Select(g => g.Unicode));
            Runs.Add(new Run(Math.Round(state.FillColor.C0, 2), Math.Round(state.FillColor.C1, 2), Math.Round(state.FillColor.C2, 2), text, run.Glyphs.Length));
        }
    }

    private static (double R, double G, double B) Rgb(int r, int g, int b) => (Math.Round(r / 255.0, 2), Math.Round(g / 255.0, 2), Math.Round(b / 255.0, 2));

    private static TextFontRegistry Registry(byte[]? font = null)
    {
        TextFontRegistry fonts = TextFontRegistry.CreateDefault();
        fonts.Add(font ?? ColorFont.Build(), Family);
        return fonts;
    }

    private static LayoutDocument Doc(Element text, byte[]? font = null, ColorPalette? palette = null)
    {
        LayoutDocument doc = Support.Single(new Column { Items = { text } });
        doc.Options.Fonts = Registry(font);
        if (palette is { } p)
        {
            doc.Options.ColorPalette = p;
        }

        return doc;
    }

    private static Text Sample(string content, LayoutColor? color = null) => new() { Content = content, FontName = Family, Size = 40, Color = color ?? LayoutColor.Black };

    private static List<Run> Draw(LayoutDocument doc, out byte[] pdf, out List<LayoutDiagnostic> notes)
    {
        pdf = Support.Save(doc);
        notes = [.. doc.Diagnostics];
        using PdfDocument d = PdfDocument.Open(pdf);
        var cap = new Capture();
        d.Pages[0].Interpret(cap);
        return cap.Runs;
    }

    private static List<Run> Draw(LayoutDocument doc) => Draw(doc, out _, out _);

    [Fact]
    public void LayersAreDrawnInOrderInTheirPaletteColours()
    {
        List<Run> runs = Draw(Doc(Sample(ColorFont.Grin)));
        Assert.Equal(3, runs.Count);
        Assert.Equal(Rgb(230, 30, 30), (runs[0].R, runs[0].G, runs[0].B));
        Assert.Equal((0.0, 0.0, 0.0), (runs[1].R, runs[1].G, runs[1].B)); // the foreground entry takes the text colour
        Assert.Equal(Rgb(30, 160, 60), (runs[2].R, runs[2].G, runs[2].B));

        runs = Draw(Doc(Sample(ColorFont.Beam)));
        Assert.Equal(2, runs.Count);
        Assert.Equal(Rgb(30, 60, 220), (runs[0].R, runs[0].G, runs[0].B));
        Assert.Equal(Rgb(230, 30, 30), (runs[1].R, runs[1].G, runs[1].B));
    }

    [Fact]
    public void TheForegroundLayerFollowsTheTextColour()
    {
        List<Run> runs = Draw(Doc(Sample(ColorFont.Grin, new LayoutColor(0.2, 0.4, 0.6))));
        Assert.Equal((0.2, 0.4, 0.6), (runs[1].R, runs[1].G, runs[1].B));
    }

    [Fact]
    public void TextAfterAColourGlyphIsDrawnInTheTextColour()
    {
        List<Run> runs = Draw(Doc(Sample(ColorFont.Beam + "A", new LayoutColor(0.5, 0.5, 0.5))));
        Run last = runs[^1];
        Assert.Equal((0.5, 0.5, 0.5), (last.R, last.G, last.B));
        Assert.Equal(3, runs.Count);
    }

    [Fact]
    public void ThePaletteIsChosenByTheDocumentThenBySpan()
    {
        (double, double, double) First(List<Run> r) => (r[0].R, r[0].G, r[0].B);
        Assert.Equal(Rgb(255, 140, 0), First(Draw(Doc(Sample(ColorFont.Grin), palette: ColorPalette.Dark))));
        Assert.Equal(Rgb(230, 30, 30), First(Draw(Doc(Sample(ColorFont.Grin), palette: ColorPalette.Light))));
        Assert.Equal(Rgb(255, 140, 0), First(Draw(Doc(Sample(ColorFont.Grin), palette: ColorPalette.FromIndex(1)))));
        Assert.Equal(Rgb(230, 30, 30), First(Draw(Doc(Sample(ColorFont.Grin), palette: ColorPalette.FromIndex(9)))));

        var spans = new Text { FontName = Family, Size = 40 };
        spans.Spans.Add(new TextSpan(ColorFont.Grin, new TextStyle { Families = [Family], Size = 40 }));
        spans.Spans.Add(new TextSpan(ColorFont.Grin, new TextStyle { Families = [Family], Size = 40, Palette = ColorPalette.Dark }));
        List<Run> runs = Draw(Doc(spans));
        Assert.Equal(6, runs.Count);
        Assert.Equal(Rgb(230, 30, 30), (runs[0].R, runs[0].G, runs[0].B));
        Assert.Equal(Rgb(255, 140, 0), (runs[3].R, runs[3].G, runs[3].B));
    }

    [Fact]
    public void FluentPaletteReachesTheText()
    {
        LayoutDocument doc = LayoutDocument.Compose(c => c.Page(p =>
        {
            p.Content().Text(t => t.Span(ColorFont.Grin).FontFamily(Family).FontSize(30).Palette(ColorPalette.Dark));
        }));
        doc.Options.Fonts = Registry();
        List<Run> runs = Draw(doc);
        Assert.Equal(Rgb(255, 140, 0), (runs[0].R, runs[0].G, runs[0].B));
    }

    [Fact]
    public void GlyphsWithoutLayersDrawAsBefore()
    {
        byte[] withColour = ColorFont.Build();
        byte[] without = ColorFont.Build(edit: p => p.OmitColr = true);
        string Content(byte[] font)
        {
            byte[] pdf = Support.Save(Doc(Sample("AAA A"), font));
            using PdfDocument d = PdfDocument.Open(pdf);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(d.Pages[0].ContentStreams.SelectMany(c => c.GetDecodedBytes().ToArray()).ToArray()));
        }

        Assert.Equal(Content(without), Content(withColour));
    }

    [Fact]
    public void ExtractionReturnsEachColourCharacterOnce()
    {
        byte[] pdf = Support.Save(Doc(Sample("A" + ColorFont.Grin + ColorFont.Beam + "A")));
        using PdfDocument d = PdfDocument.Open(pdf);
        string text = d.ExtractText().Single().Text;
        Assert.Equal(1, Count(text, ColorFont.Grin));
        Assert.Equal(1, Count(text, ColorFont.Beam));
        Assert.StartsWith("A" + ColorFont.Grin + ColorFont.Beam + "A", text.Trim(), StringComparison.Ordinal);
    }

    private static int Count(string s, string part)
    {
        int n = 0;
        for (int i = s.IndexOf(part, StringComparison.Ordinal); i >= 0; i = s.IndexOf(part, i + part.Length, StringComparison.Ordinal))
        {
            n++;
        }

        return n;
    }

    [Fact]
    public void AVersion1OnlyGlyphIsMonochromeWithOneNotePerFont()
    {
        List<Run> runs = Draw(Doc(Sample(ColorFont.Joy + ColorFont.Joy + ColorFont.Grin), ColorFont.Build(version1: true)), out _, out List<LayoutDiagnostic> notes);
        Assert.Equal(2 + 3, runs.Count); // two monochrome glyphs; the grin has three layers
        Assert.Single(notes, n => n.Message.Contains("version 1", StringComparison.Ordinal));
        Assert.Equal(Rgb(230, 30, 30), (runs[2].R, runs[2].G, runs[2].B));
        Assert.Equal((0.0, 0.0, 0.0), (runs[0].R, runs[0].G, runs[0].B));
    }

    public static TheoryData<string> Hostile =>
    [
        "short", "version", "baseOffset", "layerOffset", "unsorted", "layerCount", "layerGlyph", "paletteEntry", "manyLayers", "noCpal", "cpalShort", "cpalColors", "cpalTypes",
    ];

    [Theory]
    [MemberData(nameof(Hostile))]
    public void BrokenTablesGiveTheMonochromeGlyphAndANote(string kind)
    {
        byte[] font = ColorFont.Build(edit: p =>
        {
            byte[] colr = p.Colr;
            switch (kind)
            {
                case "short": p.Colr = colr[..10]; break;
                case "version": colr[1] = 9; break;
                case "baseOffset": colr[4] = 0xFF; colr[5] = 0xFF; break;
                case "layerOffset": colr[8] = 0x7F; break;
                case "unsorted": colr[14 + 1] = 6; colr[14 + 7] = 5; break; // records 6, 5
                case "layerCount": colr[12] = 0; colr[13] = 2; break; // fewer layers than the base glyphs use
                case "layerGlyph": colr[26 + 1] = 0xF0; break;
                case "paletteEntry": colr[26 + 3] = 77; break;
                case "manyLayers": colr[14 + 4] = 0xFF; colr[14 + 5] = 0xFF; break;
                case "noCpal": p.OmitCpal = true; break;
                case "cpalShort": p.Cpal = p.Cpal[..9]; break;
                case "cpalColors": p.Cpal[6] = 0; p.Cpal[7] = 2; break;
                case "cpalTypes": p.Cpal[16] = 0xFF; break;
            }
        });

        List<Run> runs = Draw(Doc(Sample("A" + ColorFont.Grin + ColorFont.Grin), font), out byte[] pdf, out List<LayoutDiagnostic> notes);
        Assert.Equal(3, runs.Count); // one plain glyph each
        Assert.All(runs, r => Assert.Equal((0.0, 0.0, 0.0), (r.R, r.G, r.B)));
        Assert.Single(notes, n => n.Message.Contains("colour tables", StringComparison.Ordinal));
        Assert.True(pdf.Length > 0);
    }

    [Fact]
    public void ACharacterMappedToAGlyphOutsideTheFontDrawsAsTheMissingGlyph()
    {
        // Found by fuzzing: a font's character map may name a glyph the font does not have.
        byte[] font = ColorFont.Build(edit: p => p.ExtraMap.Add((0x42, 60000)));
        byte[] pdf = Support.Save(Doc(Sample("ABA"), font));
        using PdfDocument d = PdfDocument.Open(pdf);
        Assert.Single(d.Pages);
    }

    [Fact]
    public void OutputIsTheSameEveryTime()
    {
        Assert.Equal(Support.Save(Doc(Sample("A" + ColorFont.Grin + ColorFont.Beam))), Support.Save(Doc(Sample("A" + ColorFont.Grin + ColorFont.Beam))));
    }

    [Fact]
    public void ColourRunsInsideRightToLeftTextKeepTheirText()
    {
        byte[] pdf = Support.Save(Doc(Sample("\u05D0 " + ColorFont.Grin + " \u05D1")));
        using PdfDocument d = PdfDocument.Open(pdf);
        Assert.Equal(1, Count(d.ExtractText().Single().Text, ColorFont.Grin));
    }

    private static LayoutDocument WithColour()
    {
        LayoutDocument layout = PdfUATests.Sample(12, body =>
        {
            body.Items.Add(new Text { Content = "Reactions", Size = 14 });
            body.Items.Add(new Text { Content = ColorFont.Grin + " " + ColorFont.Beam + " " + ColorFont.Joy, FontName = Family, Size = 14 });
        });
        layout.Options.Fonts = Registry(ColorFont.Build(version1: true));
        return layout;
    }

    private static byte[] Save(LayoutDocument layout, PdfSaveOptions options)
    {
        using var ms = new MemoryStream();
        layout.Save(ms, options);
        return ms.ToArray();
    }

    [Fact]
    public void ColourGlyphsPassPdfA2b()
    {
        byte[] pdf = Save(WithColour(), new PdfSaveOptions { DocumentId = new byte[16], PdfA = new PdfAOptions { Part = PdfAPart.Part2, Conformance = PdfAConformance.B } });
        List<string> failures = TagSupport.Vera(pdf, "2b");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void ColourGlyphsPassPdfA2uAndUa1()
    {
        byte[] pdf = Save(WithColour(), new PdfSaveOptions { DocumentId = new byte[16], PdfA = new PdfAOptions { Part = PdfAPart.Part2, Conformance = PdfAConformance.A }, PdfUA = new PdfUAOptions() });
        List<string> failures = TagSupport.Vera(pdf, "2a");
        Assert.True(failures.Count == 0, "2a: " + string.Join("\n", failures));
        failures = TagSupport.Vera(pdf, "ua1");
        Assert.True(failures.Count == 0, "ua1: " + string.Join("\n", failures));
    }
}
