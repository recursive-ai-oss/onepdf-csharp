using System.Buffers.Binary;
using System.Text;
using OnePdf.FontPrograms;
using OnePdf.FontWriting;
using OnePdf.Tests.Content;
using OnePdf.Tests.PdfA;
using OnePdf.Tests.Tagging;
using OnePdf.Text;

namespace OnePdf.Tests.Fonts;

/// <summary>Embedding a font program into font dictionaries a file already has, and the substitute happy path built on it.</summary>
public class FontEmbeddingTests
{
    private static readonly IPdfFontProvider Provider = OnePdf.Fonts.FontsPackage.Provider;

    private static PdfFontFile Liberation(string standard)
    {
        PdfSubstituteFont sub = Provider.FindFont(new PdfFontRequest
        {
            FontName = standard,
            FamilyName = standard,
            StandardName = standard,
            IsBold = standard.Contains("Bold", StringComparison.Ordinal),
            IsItalic = standard.Contains("Italic", StringComparison.Ordinal) || standard.Contains("Oblique", StringComparison.Ordinal),
            IsSerif = standard.StartsWith("Times", StringComparison.Ordinal),
            IsFixedPitch = standard.StartsWith("Courier", StringComparison.Ordinal),
        })!;
        return PdfFontFile.Open(sub.Data, sub.FaceIndex);
    }

    private static string Widths(PdfFontFile f, int first, int last, Func<int, int, int>? tweak = null)
    {
        var sb = new StringBuilder("[");
        for (int c = first; c <= last; c++)
        {
            int g = f.GetGlyphForUnicode(c);
            int w = g > 0 ? (int)Math.Round(f.GetAdvanceWidth(g)) : 0;
            sb.Append(tweak is null ? w : tweak(c, w)).Append(' ');
        }

        return sb.Append(']').ToString();
    }

    // Writes "N 0 obj ... endobj" texts through the builder, so the offsets are recorded.
    private static void Put(PdfBuilder b, string text)
    {
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, "(\\d+) 0 obj\n(.*?)\nendobj\n", System.Text.RegularExpressions.RegexOptions.Singleline))
        {
            b.Obj(int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), m.Groups[2].Value);
        }
    }

    private static byte[] OnePage(string content, string fontObjects, string fontResources = "/F1 20 0 R") =>
        TestPdf.Build(content, $"/Resources << /Font << {fontResources} >> >>", b => Put(b, fontObjects), mediaBox: "[0 0 300 300]", objects: 60);

    // A page with several fonts: each object is written by the caller.
    private static byte[] WithObjects(string content, string fontResources, Action<PdfBuilder> objects) =>
        TestPdf.Build(content, $"/Resources << /Font << {fontResources} >> >>", objects, mediaBox: "[0 0 300 300]", objects: 60);

    private static PdfDocument Editable(byte[] pdf)
    {
        PdfDocument d = PdfDocument.Open(pdf, new PdfOpenOptions { AllowEditing = true });
        d.FontProvider = Provider;
        return d;
    }

    private static PdfDictionary FontOf(PdfDocument d, string name = "F1") => d.Pages[0].Resources.GetDictionary("Font")!.GetDictionary(name)!;

    private static string PlainText(PdfDocument d) => string.Join("\n", d.ExtractText(new PdfTextOptions { UseStructureTree = false }).Select(static p => p.Text));

    private sealed class Positions : PdfDevice
    {
        public readonly List<string> Items = [];

        public override void ShowGlyphRun(in PdfGlyphRun run, in PdfGraphicsState state)
        {
            for (int i = 0; i < run.Glyphs.Length; i++)
            {
                PdfGlyph g = run.Glyphs.Span[i];
                PdfMatrix m = run.GlyphMatrix(i);
                Items.Add($"{g.Code}:{g.Width:F4}@{m.E:F3},{m.F:F3}");
            }
        }
    }

    // Where every glyph is drawn: text positions are what embedding must not move.
    private static string Layout(PdfDocument d)
    {
        var dev = new Positions();
        for (int i = 0; i < d.Pages.Count; i++)
        {
            d.Pages[i].Interpret(dev, new PdfInterpretOptions { GlyphOutlines = false, GenerateAppearances = false });
        }

        return string.Join(" ", dev.Items);
    }

    private static void AssertEmbedded(PdfDocument d, string fontName = "F1")
    {
        PdfDictionary f = FontOf(d, fontName);
        PdfDictionary desc = (f.GetName("Subtype") == "Type0" ? ((PdfDictionary)f.GetArray("DescendantFonts")![0]!) : f).GetDictionary("FontDescriptor")!;
        Assert.True(desc.ContainsKey("FontFile2"));
    }

    private static byte[] Tagged(PdfDocument d, PdfUAOptions? ua = null)
    {
        PdfTaggingResult r = d.Tag(TagSupport.Extract(d), static _ => PdfBlockRole.Paragraph, new PdfTagOptions { Title = "Test", Language = "en-US" });
        Assert.True(r.ElementsWritten > 0);
        return TagSupport.Save(d, new PdfSaveOptions { PdfUA = ua ?? new PdfUAOptions { Language = "en-US" } });
    }

    private static void AssertUaClean(byte[] saved)
    {
        using PdfDocument back = PdfDocument.Open(saved);
        Assert.Empty(back.CheckPdfUA());
        if (VeraPdf.Path is not null)
        {
            Assert.Empty(VeraPdf.Validate(saved, "ua1"));
        }
    }

    // ---- cases ------------------------------------------------------------------------------------------------------------------

    private const string Hello = "BT /F1 14 Tf 20 250 Td (Hello, World! 0123) Tj 0 -20 Td [(Kern) -120 (ed)] TJ ET";

    private static PdfFontFile Sans => Liberation("Helvetica");

    private static string ArialFont(int objectNumber = 20) =>
        $"{objectNumber} 0 obj\n<< /Type /Font /Subtype /TrueType /BaseFont /Arial /FirstChar 32 /LastChar 126 /Widths {Widths(Sans, 32, 126, (c, w) => c == 'l' ? w + 7 : w)} /Encoding /WinAnsiEncoding /FontDescriptor 21 0 R >>\nendobj\n"
        + "21 0 obj\n<< /Type /FontDescriptor /FontName /Arial /Flags 32 /FontBBox [-665 -325 2000 1006] /ItalicAngle 0 /Ascent 905 /Descent -212 /CapHeight 716 /StemV 80 >>\nendobj\n";

    [Fact]
    public void ArialTrueTypeGetsLiberationLowLevelWithPositionsAndTextUnchanged()
    {
        using PdfDocument d = Editable(OnePage(Hello, ArialFont()));
        string text = PlainText(d), layout = Layout(d);
        PdfFontEmbedResult r = d.EmbedFontProgram(FontOf(d), Sans);

        AssertEmbedded(d);
        Assert.Matches("^[A-Z]{6}\\+Arial$", r.BaseFont);
        Assert.False(r.Symbolic);
        Assert.Equal(text, PlainText(d));
        Assert.Equal(layout, Layout(d));
        Assert.Contains(r.WidthChanges, static w => w.Code == 'l' && w.PdfWidth - w.ProgramWidth > 6);
        // The PDF's widths are untouched; the program now carries them.
        Assert.Equal(Widths(Sans, 32, 126, (c, w) => c == 'l' ? w + 7 : w), "[" + string.Join(' ', Enumerable.Range(0, 95).Select(i => FontOf(d).GetArray("Widths")!.GetInt(i))) + " ]");
        PdfFontFile written = PdfFontFile.Open(((PdfStream)d.ResolveChain(r.FontFile.Number, 0)).GetDecodedBytes());
        int gl = written.GetGlyphForUnicode('l');
        Assert.True(gl > 0);
        Assert.Equal(Sans.GetAdvanceWidth(Sans.GetGlyphForUnicode('l')) + 7, written.GetAdvanceWidth(gl), 0.6);
        Assert.True(written.GlyphCount < 30);
    }

    [Fact]
    public void ArialTaggedAndSavedAsPdfUaPassesThroughTheHappyPath()
    {
        using PdfDocument d = Editable(OnePage(Hello, ArialFont()));
        string text = PlainText(d), layout = Layout(d);
        byte[] saved = Tagged(d, new PdfUAOptions { Language = "en-US", EmbedSubstituteFonts = true });
        using PdfDocument back = PdfDocument.Open(saved);
        Assert.Equal(text, PlainText(back));
        Assert.Equal(layout, Layout(back));
        AssertUaClean(saved);
    }

    [Fact]
    public void EveryEmbeddedCaseTaggedAndSavedPassesPdfUaOneAndKeepsItsPositions()
    {
        var cases = new List<(string Name, byte[] Pdf, Action<PdfDocument> Embed)>
        {
            ("times-bold", WithObjects("BT /F1 12 Tf 20 150 Td (Bold Roman) Tj ET", "/F1 20 0 R", b =>
                Put(b, "20 0 obj\n<< /Type /Font /Subtype /TrueType /BaseFont /TimesNewRoman,Bold /Encoding /WinAnsiEncoding >>\nendobj\n")), d => d.EmbedSubstituteFonts()),
            ("courier-new", WithObjects("BT /F1 12 Tf 20 150 Td (mono 123) Tj ET", "/F1 20 0 R", b =>
                Put(b, "20 0 obj\n<< /Type /Font /Subtype /TrueType /BaseFont /CourierNewPSMT /Encoding /WinAnsiEncoding >>\nendobj\n")), d => d.EmbedSubstituteFonts()),
            ("differences", OnePage("BT /F1 12 Tf 20 150 Td (ABC) Tj ET",
                "20 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding << /Type /Encoding /BaseEncoding /WinAnsiEncoding /Differences [65 /Euro /bullet] >> >>\nendobj\n"),
                d => d.EmbedFontProgram(FontOf(d), Sans)),
            ("symbolic", OnePage("BT /F1 12 Tf 20 150 Td (ABCD) Tj ET",
                "20 0 obj\n<< /Type /Font /Subtype /TrueType /BaseFont /Dingbats /FirstChar 65 /LastChar 68 /Widths [500 510 520 530] /FontDescriptor 21 0 R >>\nendobj\n"
                + "21 0 obj\n<< /Type /FontDescriptor /FontName /Dingbats /Flags 4 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 900 /Descent -200 /CapHeight 700 /StemV 80 >>\nendobj\n"),
                d => d.EmbedFontProgram(FontOf(d), PdfFontFile.Open(SymbolicProgram()), new PdfFontEmbedOptions { CodeToUnicode = new Dictionary<int, string> { [65] = "A", [66] = "B", [67] = "C", [68] = "D" } })),
            ("cid", CidFontPdf(CidMap).Length > 0 ? CidFontPdf(CidMap) : [], d => d.EmbedFontProgram(FontOf(d), Sans)),
        };
        foreach ((string name, byte[] pdf, Action<PdfDocument> embed) in cases)
        {
            using PdfDocument d = Editable(pdf);
            string layout = Layout(d), text = PlainText(d);
            embed(d);
            Assert.True(text == PlainText(d), name + ": text");
            Assert.True(layout == Layout(d), name + ": positions");
            byte[] saved = Tagged(d);
            using PdfDocument back = PdfDocument.Open(saved);
            Assert.True(text == PlainText(back), name + ": text after save");
            Assert.True(layout == Layout(back), name + ": positions after save");
            if (VeraPdf.Path is not null)
            {
                Assert.True(VeraPdf.Validate(saved, "ua1").Count == 0, name + ": " + string.Join("; ", VeraPdf.Validate(saved, "ua1")));
            }
            else
            {
                Assert.Empty(back.CheckPdfUA());
            }
        }
    }

    [Fact]
    public void WithoutTheOptInAPdfUaSaveStillRefusesAnUnembeddedFont()
    {
        using PdfDocument d = Editable(OnePage(Hello, ArialFont()));
        PdfUAException ex = Assert.Throws<PdfUAException>(() => Tagged(d));
        Assert.Contains(ex.Violations, static v => v.Clause == "7.21.4.1");
    }

    [Fact]
    public void TimesNewRomanBoldAndCourierNewAreFoundByNameAndStyle()
    {
        byte[] pdf = WithObjects("BT /F1 12 Tf 20 250 Td (Bold Roman) Tj ET BT /F2 12 Tf 20 220 Td (mono 123) Tj ET", "/F1 20 0 R /F2 21 0 R", b =>
            Put(b, "20 0 obj\n<< /Type /Font /Subtype /TrueType /BaseFont /TimesNewRoman,Bold /Encoding /WinAnsiEncoding >>\nendobj\n"
                + "21 0 obj\n<< /Type /Font /Subtype /TrueType /BaseFont /CourierNewPSMT /Encoding /WinAnsiEncoding /FirstChar 32 /LastChar 126 /Widths " + Widths(Liberation("Courier"), 32, 126) + " >>\nendobj\n"));
        using PdfDocument d = Editable(pdf);
        string text = PlainText(d), layout = Layout(d);
        PdfSubstituteEmbedReport report = d.EmbedSubstituteFonts();
        Assert.Equal(2, report.EmbeddedCount);
        AssertEmbedded(d, "F1");
        AssertEmbedded(d, "F2");
        Assert.Matches("^[A-Z]{6}\\+TimesNewRoman,Bold$", FontOf(d).GetName("BaseFont")!);
        Assert.Equal(text, PlainText(d));
        Assert.Equal(layout, Layout(d));

        // The bold face was chosen: its glyphs are Liberation Serif Bold's, whose 'B' is wider than the regular one.
        PdfStream file = (PdfStream)FontOf(d).GetDictionary("FontDescriptor")!.Get("FontFile2")!;
        PdfFontFile used = PdfFontFile.Open(file.GetDecodedBytes());
        PdfFontFile bold = Liberation("Times-Bold");
        PdfFontFile regular = Liberation("Times-Roman");
        int gb = used.GetGlyphForUnicode('a');
        Assert.True(Math.Abs(used.GetAdvanceWidth(gb) - bold.GetAdvanceWidth(bold.GetGlyphForUnicode('a'))) < 1.5);
        Assert.NotEqual(Math.Round(regular.GetAdvanceWidth(regular.GetGlyphForUnicode('a'))), Math.Round(bold.GetAdvanceWidth(bold.GetGlyphForUnicode('a'))));
    }

    [Fact]
    public void ACodeWithoutWidthsUsesTheStandardMetricsAndTheWidthsAreAdded()
    {
        byte[] pdf = OnePage("BT /F1 12 Tf 20 250 Td (Hi) Tj ET", "20 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        using PdfDocument d = Editable(pdf);
        string layout = Layout(d);
        PdfFontEmbedResult r = d.EmbedFontProgram(FontOf(d), Sans);
        Assert.Equal("TrueType", FontOf(d).GetName("Subtype"));
        Assert.Equal(layout, Layout(d));
        PdfArray w = FontOf(d).GetArray("Widths")!;
        Assert.Equal('H', FontOf(d).GetInt("FirstChar"));
        Assert.Equal(722, w.GetInt(0)); // Helvetica 'H'
        Assert.Contains(r.EmbeddedCodes, static c => c == 'i');
    }

    [Fact]
    public void ADifferencesTypeOneFontKeepsItsNamesAndGetsAToUnicode()
    {
        byte[] pdf = OnePage("BT /F1 12 Tf 20 250 Td (ABC) Tj ET",
            "20 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding << /Type /Encoding /BaseEncoding /WinAnsiEncoding /Differences [65 /Euro /bullet] >> >>\nendobj\n");
        using PdfDocument d = Editable(pdf);
        string text = PlainText(d), layout = Layout(d);
        Assert.Equal("\u20AC\u2022C", text.Trim());
        PdfFontEmbedResult r = d.EmbedFontProgram(FontOf(d), Sans);
        Assert.Equal(text, PlainText(d));
        Assert.Equal(layout, Layout(d));
        Assert.True(r.ToUnicodeWritten);
        PdfDictionary enc = FontOf(d).GetDictionary("Encoding")!;
        Assert.Equal("WinAnsiEncoding", enc.GetName("BaseEncoding"));
        Assert.NotNull(enc.GetArray("Differences"));
    }

    // A program whose characters are reached by code through a (3,0) map, as symbolic fonts do: Liberation Sans glyphs at U+F041.. for A...
    private static byte[] SymbolicProgram()
    {
        PdfFontFile sans = Sans;
        var glyphs = new List<int>();
        var pairs = new List<(int, int)>();
        for (int c = 'A'; c <= 'F'; c++)
        {
            int g = sans.GetGlyphForUnicode(c + 0x20); // the lower-case shapes sit at the upper-case codes
            glyphs.Add(g);
            pairs.Add((0xF000 + c, g));
        }

        return TrueTypeSubsetter.Subset(sans.Sfnt!, glyphs, pairs, null, 3, 0);
    }

    [Fact]
    public void ASymbolicTrueTypeTakesItsGlyphsFromTheProgramsSymbolMap()
    {
        byte[] program = SymbolicProgram();
        PdfFontFile symbolic = PdfFontFile.Open(program);
        Assert.True(symbolic.HasSymbolicCmap);
        Assert.False(symbolic.HasUnicodeCmap);
        Assert.True(symbolic.GetGlyphForSymbolicCode('B') > 0);
        byte[] pdf = OnePage("BT /F1 12 Tf 20 250 Td (ABCD) Tj ET",
            "20 0 obj\n<< /Type /Font /Subtype /TrueType /BaseFont /Dingbats /FirstChar 65 /LastChar 68 /Widths [500 510 520 530] /FontDescriptor 21 0 R >>\nendobj\n"
            + "21 0 obj\n<< /Type /FontDescriptor /FontName /Dingbats /Flags 4 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 900 /Descent -200 /CapHeight 700 /StemV 80 >>\nendobj\n");
        using PdfDocument d = Editable(pdf);
        string layout = Layout(d);
        var map = new Dictionary<int, string> { [65] = "a", [66] = "b", [67] = "c", [68] = "d" };
        PdfFontEmbedResult r = d.EmbedFontProgram(FontOf(d), symbolic, new PdfFontEmbedOptions { CodeToUnicode = map });
        Assert.True(r.Symbolic);
        Assert.False(FontOf(d).ContainsKey("Encoding"));
        Assert.Equal(layout, Layout(d));
        Assert.Equal("abcd", PlainText(d).Trim());
        PdfDictionary desc = FontOf(d).GetDictionary("FontDescriptor")!;
        Assert.Equal(4, desc.GetInt("Flags") & 36);
        PdfFontFile written = PdfFontFile.Open(((PdfStream)desc.Get("FontFile2")!).GetDecodedBytes());
        Assert.True(written.HasSymbolicCmap);
        Assert.False(written.HasUnicodeCmap);
        Assert.Equal(Math.Round(510.0 * written.UnitsPerEm / 1000), Math.Round(written.GetAdvanceWidth(written.GetGlyphForSymbolicCode('B')) * written.UnitsPerEm / 1000), 1);
    }

    private static byte[] CidFontPdf(string toUnicode = "") =>
        WithObjects("BT /F1 14 Tf 20 250 Td <00010002000300040005> Tj ET", "/F1 20 0 R", b =>
        {
            Put(b, "20 0 obj\n<< /Type /Font /Subtype /Type0 /BaseFont /ArialMT /Encoding /Identity-H /DescendantFonts [21 0 R] " + (toUnicode.Length > 0 ? "/ToUnicode 23 0 R " : string.Empty) + ">>\nendobj\n"
                + "21 0 obj\n<< /Type /Font /Subtype /CIDFontType2 /BaseFont /ArialMT /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /DW 600 /W [1 [700 710] 3 5 650] /FontDescriptor 22 0 R /CIDToGIDMap /Identity >>\nendobj\n"
                + "22 0 obj\n<< /Type /FontDescriptor /FontName /ArialMT /Flags 4 /FontBBox [-665 -325 2000 1006] /ItalicAngle 0 /Ascent 905 /Descent -212 /CapHeight 716 /StemV 80 >>\nendobj\n");
            if (toUnicode.Length > 0)
            {
                b.Stream(23, string.Empty, Encoding.ASCII.GetBytes(toUnicode));
            }
        });

    private const string CidMap = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /X def /CMapType 2 def 1 begincodespacerange <0000> <FFFF> endcodespacerange 5 beginbfchar <0001> <0054> <0002> <0065> <0003> <0073> <0004> <0074> <0005> <0021> endbfchar endcmap end end";

    [Fact]
    public void ACidFontType2WithIdentityHGetsAGlyphMapFromItsToUnicode()
    {
        using PdfDocument d = Editable(CidFontPdf(CidMap));
        string text = PlainText(d), layout = Layout(d);
        Assert.Equal("Test!", text.Trim());
        PdfFontEmbedResult r = d.EmbedFontProgram(FontOf(d), Sans);
        AssertEmbedded(d);
        Assert.Equal(5, r.EmbeddedCodes.Count);
        Assert.Equal("Test!", PlainText(d).Trim());
        Assert.Equal(layout, Layout(d));
        PdfDictionary cid = (PdfDictionary)FontOf(d).GetArray("DescendantFonts")![0]!;
        Assert.Matches("^[A-Z]{6}\\+ArialMT$", cid.GetName("BaseFont")!);
        Assert.Equal(cid.GetName("BaseFont"), FontOf(d).GetName("BaseFont"));
        PdfStream map = (PdfStream)cid.Get("CIDToGIDMap")!;
        Assert.Equal(12, map.GetDecodedBytes().Length);
        PdfFontFile written = PdfFontFile.Open(((PdfStream)cid.GetDictionary("FontDescriptor")!.Get("FontFile2")!).GetDecodedBytes());
        int g1 = BinaryPrimitives.ReadUInt16BigEndian(map.GetDecodedBytes().Span[2..]);
        Assert.Equal(Math.Round(700.0 * written.UnitsPerEm / 1000), Math.Round(written.GetAdvanceWidth(g1) * written.UnitsPerEm / 1000), 1);
    }

    [Fact]
    public void ACompositeFontWithoutAnyUnicodeIsRefusedWithTheCodes()
    {
        using PdfDocument d = Editable(CidFontPdf());
        PdfFontEmbedException ex = Assert.Throws<PdfFontEmbedException>(() => d.EmbedFontProgram(FontOf(d), Sans));
        Assert.Equal(PdfFontEmbedFailure.NoGlyphForCode, ex.Reason);
        Assert.Equal([1, 2, 3, 4, 5], ex.Codes);
        // Nothing changed.
        Assert.False(((PdfDictionary)FontOf(d).GetArray("DescendantFonts")![0]!).GetDictionary("FontDescriptor")!.ContainsKey("FontFile2"));
    }

    [Fact]
    public void ACompositeFontWhoseCidsAreGlyphIdsOfTheGivenProgramIsEmbeddedByCid()
    {
        PdfFontFile sans = Sans;
        // The program is "the original font": character id n is glyph n.
        byte[] pdf = WithObjects("BT /F1 14 Tf 20 250 Td <00440045> Tj ET", "/F1 20 0 R", b =>
            Put(b, "20 0 obj\n<< /Type /Font /Subtype /Type0 /BaseFont /ArialMT /Encoding /Identity-H /DescendantFonts [21 0 R] >>\nendobj\n"
                + "21 0 obj\n<< /Type /Font /Subtype /CIDFontType2 /BaseFont /ArialMT /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /DW 600 >>\nendobj\n"));
        using PdfDocument d = Editable(pdf);
        PdfFontEmbedResult r = d.EmbedFontProgram(FontOf(d), sans, new PdfFontEmbedOptions { CidsAreGlyphIds = true, CodeToUnicode = new Dictionary<int, string> { [0x44] = "a", [0x45] = "b" } });
        Assert.Equal([0x44, 0x45], r.EmbeddedCodes);
        Assert.True(r.ToUnicodeWritten);
        Assert.Equal("ab", PlainText(d).Trim());
    }

    [Fact]
    public void EachRefusalHasItsOwnTypedReason()
    {
        // Type 3
        byte[] t3 = OnePage("BT /F1 12 Tf (A) Tj ET", "20 0 obj\n<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1 1] /FontMatrix [1 0 0 1 0 0] /CharProcs << >> /Encoding << /Type /Encoding /Differences [] >> /FirstChar 0 /LastChar 0 /Widths [0] >>\nendobj\n");
        using (PdfDocument d = Editable(t3))
        {
            Assert.Equal(PdfFontEmbedFailure.Type3Font, Assert.Throws<PdfFontEmbedException>(() => d.EmbedFontProgram(FontOf(d), Sans)).Reason);
        }

        // Already embedded: embed twice.
        using (PdfDocument d = Editable(OnePage(Hello, ArialFont())))
        {
            d.EmbedFontProgram(FontOf(d), Sans);
            Assert.Equal(PdfFontEmbedFailure.AlreadyEmbedded, Assert.Throws<PdfFontEmbedException>(() => d.EmbedFontProgram(FontOf(d), Sans)).Reason);
            PdfFontEmbedResult again = d.EmbedFontProgram(FontOf(d), Sans, new PdfFontEmbedOptions { Replace = true });
            Assert.NotNull(again.FontFile);
        }

        // A code with no glyph: the program lacks U+20AC.
        byte[] euro = OnePage("BT /F1 12 Tf (\u0080) Tj ET", "20 0 obj\n<< /Type /Font /Subtype /TrueType /BaseFont /Arial /Encoding /WinAnsiEncoding >>\nendobj\n");
        PdfFontFile noEuro = PdfFontFile.Open(TrueTypeSubsetter.Subset(Sans.Sfnt!, [Sans.GetGlyphForUnicode('A')], [('A', Sans.GetGlyphForUnicode('A'))]));
        using (PdfDocument d = Editable(euro))
        {
            PdfFontEmbedException ex = Assert.Throws<PdfFontEmbedException>(() => d.EmbedFontProgram(FontOf(d), noEuro));
            Assert.Equal(PdfFontEmbedFailure.NoGlyphForCode, ex.Reason);
            Assert.Equal([0x80], ex.Codes);
            PdfFontEmbedResult lenient = d.EmbedFontProgram(FontOf(d), noEuro, new PdfFontEmbedOptions { MissingGlyphs = PdfMissingGlyphPolicy.UseNotdef });
            Assert.Equal([0x80], lenient.MissingCodes);
        }

        // fsType forbids embedding: set the restricted-license bit in a copy of the program.
        byte[] restricted = Sans.Data.ToArray();
        int os2 = OffsetOfTable(restricted, "OS/2");
        BinaryPrimitives.WriteUInt16BigEndian(restricted.AsSpan(os2 + 8), 2);
        PdfFontFile locked = PdfFontFile.Open(restricted);
        Assert.Equal(PdfFontEmbeddingRights.Restricted, locked.EmbeddingRights);
        Assert.False(locked.AllowsEmbedding);
        Assert.True(Sans.AllowsEmbedding);
        using (PdfDocument d = Editable(OnePage(Hello, ArialFont())))
        {
            Assert.Equal(PdfFontEmbedFailure.ProgramForbidsEmbedding, Assert.Throws<PdfFontEmbedException>(() => d.EmbedFontProgram(FontOf(d), locked)).Reason);
            Assert.NotNull(d.EmbedFontProgram(FontOf(d), locked, new PdfFontEmbedOptions { IgnoreEmbeddingRestrictions = true }));
        }

        // Widths refused beyond a tolerance.
        using (PdfDocument d = Editable(OnePage(Hello, ArialFont())))
        {
            PdfFontEmbedException ex = Assert.Throws<PdfFontEmbedException>(() => d.EmbedFontProgram(FontOf(d), Sans, new PdfFontEmbedOptions { Widths = PdfFontWidthPolicy.Refuse }));
            Assert.Equal(PdfFontEmbedFailure.WidthMismatch, ex.Reason);
            Assert.Contains((int)'l', ex.Codes);
        }

        // Not editable.
        using PdfDocument ro = PdfDocument.Open(OnePage(Hello, ArialFont()));
        Assert.Throws<InvalidOperationException>(() => ro.EmbedFontProgram(FontOf(ro), Sans));
    }

    private static int OffsetOfTable(byte[] sfnt, string tag)
    {
        int n = BinaryPrimitives.ReadUInt16BigEndian(sfnt.AsSpan(4));
        for (int i = 0; i < n; i++)
        {
            int rec = 12 + (16 * i);
            if (Encoding.ASCII.GetString(sfnt, rec, 4) == tag)
            {
                return (int)BinaryPrimitives.ReadUInt32BigEndian(sfnt.AsSpan(rec + 8));
            }
        }

        throw new InvalidOperationException(tag);
    }

    [Fact]
    public void ACffProgramIsReadableButNotEmbedded()
    {
        byte[] otf = File.ReadAllBytes(Content.AppearanceGenerationTests.TestDataPath("fonts/NotoSansCJK-Subset.otf"));
        PdfFontFile cff = PdfFontFile.Open(otf);
        Assert.Equal(PdfFontFileKind.OpenTypeCff, cff.Kind);
        Assert.True(cff.GlyphCount > 1);
        Assert.True(cff.HasGlyphNames || cff.GlyphCount > 0);
        Assert.True(cff.UnitsPerEm > 0);
        using PdfDocument d = Editable(OnePage(Hello, ArialFont()));
        Assert.Equal(PdfFontEmbedFailure.UnsupportedProgram, Assert.Throws<PdfFontEmbedException>(() => d.EmbedFontProgram(FontOf(d), cff)).Reason);
    }

    [Fact]
    public void TheReaderAnswersGlyphQuestionsAndRefusesGarbage()
    {
        PdfFontFile f = Sans;
        Assert.Equal(PdfFontFileKind.TrueType, f.Kind);
        Assert.Equal(2048, f.UnitsPerEm);
        int a = f.GetGlyphForUnicode('A');
        Assert.True(a > 0);
        Assert.Equal(667, f.GetAdvanceWidth(a), 1);
        Assert.Equal(0, f.GetGlyphForUnicode(0x10FFFF));
        Assert.Equal(0, f.GetGlyphForUnicode(-5));
        Assert.Equal(PdfFontEmbeddingRights.Installable, f.EmbeddingRights);
        Assert.Throws<ArgumentOutOfRangeException>(() => f.GetAdvanceWidth(f.GlyphCount));
        Assert.False(f.TryGetAdvanceWidth(-1, out _));
        Assert.Throws<PdfFormatException>(() => PdfFontFile.Open(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        byte[] cut = f.Data.Span[..400].ToArray();
        Assert.Throws<PdfFormatException>(() => PdfFontFile.Open(cut));
    }

    // ---- ToUnicode --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void SetToUnicodeAddsEntriesKeepsTheOldOnesAndExtractionSeesThem()
    {
        byte[] pdf = WithObjects("BT /F1 14 Tf 20 250 Td <00010002> Tj ET", "/F1 20 0 R", b =>
        {
            Put(b, "20 0 obj\n<< /Type /Font /Subtype /Type0 /BaseFont /ArialMT /Encoding /Identity-H /DescendantFonts [21 0 R] /ToUnicode 23 0 R >>\nendobj\n"
                + "21 0 obj\n<< /Type /Font /Subtype /CIDFontType2 /BaseFont /ArialMT /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /DW 600 >>\nendobj\n");
            b.Stream(23, string.Empty, Encoding.ASCII.GetBytes("/CIDInit /ProcSet findresource begin 12 dict begin begincmap 1 begincodespacerange <0000> <FFFF> endcodespacerange 1 beginbfchar <0001> <0041> endbfchar endcmap end end"));
        });
        using PdfDocument d = Editable(pdf);
        Assert.StartsWith("A\uFFFD", PlainText(d).Trim());
        Assert.True(d.SetToUnicode(FontOf(d), new Dictionary<int, string> { [1] = "Z", [2] = "B" }));
        Assert.Equal("AB", PlainText(d).Trim()); // code 1 keeps A, code 2 is new
        Assert.True(d.SetToUnicode(FontOf(d), new Dictionary<int, string> { [1] = "Z" }, overrideExisting: true));
        Assert.Equal("ZB", PlainText(d).Trim());
        Assert.False(d.SetToUnicode(FontOf(d), new Dictionary<int, string> { [1] = "Z" }, overrideExisting: true));
    }

    [Fact]
    public void SetToUnicodeCreatesAMapForASimpleFont()
    {
        using PdfDocument d = Editable(OnePage("BT /F1 12 Tf (\u0001\u0002) Tj ET", "20 0 obj\n<< /Type /Font /Subtype /TrueType /BaseFont /Sym /FirstChar 1 /LastChar 2 /Widths [500 500] >>\nendobj\n"));
        Assert.True(d.SetToUnicode(FontOf(d), new Dictionary<int, string> { [1] = "x", [2] = "y" }));
        Assert.Equal("xy", PlainText(d).Trim());
        string saved = Encoding.Latin1.GetString(TagSupport.Save(d));
        Assert.Contains("ToUnicode", saved, StringComparison.Ordinal);
    }

    // ---- annotations in the tagging plan ----------------------------------------------------------------------------------------

    [Fact]
    public void ATextAnnotationIsTaggedAsAnnotWithAnObjectReference()
    {
        byte[] pdf = TestPdf.Build("BT /F1 12 Tf 20 150 Td (Note here) Tj ET", "/Resources << /Font << /F1 20 0 R >> >> /Annots [30 0 R]", b =>
        {
            Put(b, "20 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
            b.Obj(30, "<< /Type /Annot /Subtype /Text /Rect [100 100 120 120] /Contents (A sticky note) /F 4 /Name /Note >>");
        }, objects: 60);
        using PdfDocument d = Editable(pdf);
        d.Tag(TagSupport.Extract(d), static _ => PdfBlockRole.Paragraph, new PdfTagOptions { Title = "T", Language = "en-US" });
        Assert.Contains("Annot", TagDocs.Roles(d).Split(' '));
        PdfDictionary annot = d.Pages[0].Annotations[0].Dictionary;
        Assert.True(annot.ContainsKey("StructParent"));
        byte[] saved = TagSupport.Save(d);
        using PdfDocument back = PdfDocument.Open(saved);
        Assert.DoesNotContain(back.CheckPdfUA(), static v => v.Clause == "7.18.1");

        // The low-level way: an element of role Annot holding the annotation.
        using PdfDocument d2 = Editable(pdf);
        PdfTaggingPlan plan = d2.CreateTaggingPlan();
        IReadOnlyList<PdfPageText> text = TagSupport.Extract(d2);
        plan.Add(PdfTagRole.P, PdfTagContent.Block(text[0], 0));
        plan.Add(PdfTagRole.Annot, PdfTagContent.Annotation(d2.Pages[0].Annotations[0]));
        d2.ApplyTagging(plan);
        Assert.Contains("P Annot", TagDocs.Roles(d2));
    }
}
