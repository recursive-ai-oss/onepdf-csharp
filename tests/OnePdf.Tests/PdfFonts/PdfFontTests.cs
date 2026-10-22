using OnePdf.FontData;
using OnePdf.PdfFonts;
using static OnePdf.Tests.PdfFonts.FontPdf;

namespace OnePdf.Tests.PdfFonts;

public class PdfFontTests
{
    private static Dictionary<int, int> Pairs(params (int Code, int Gid)[] p) => p.ToDictionary(static x => x.Code, static x => x.Gid);

    private static void AddFontFile(PdfBuilder b, int stream, byte[] data) => b.Stream(stream, $"/Length1 {data.Length}", data);

    // ---- Standard 14 and encodings -------------------------------------------------------------------------------

    [Fact]
    public void Standard14WithoutEmbeddingTakesWidthsFromTheAfm()
    {
        using PdfDocument doc = Open(static b =>
        {
            b.Obj(10, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
            b.Obj(11, "<< /Type /Font /Subtype /TrueType /BaseFont /Arial,Bold >>");
        });
        PdfFont f = Font(doc, 10);
        List<FontGlyph> g = Decode(f, Ascii("AB "));
        Assert.Equal(3, g.Count);
        Assert.Equal("AB ", Shown(g));
        Assert.Equal(0.667, g[0].Advance, 3);
        Assert.Equal(0.667, g[1].Advance, 3);
        Assert.Equal(0.278, g[2].Advance, 3);
        Assert.False(f.IsEmbedded);
        Assert.Equal(-1, g[0].Gid);
        Assert.All(g, static x => Assert.Equal(1, x.CodeLength));

        // An Arial alias without /Widths gets Helvetica-Bold metrics.
        Assert.Equal(0.722, Decode(Font(doc, 11), Ascii("A"))[0].Advance, 3);
    }

    [Fact]
    public void SymbolUsesItsBuiltInEncodingAndGreekNames()
    {
        using PdfDocument doc = Open(static b => b.Obj(10, "<< /Type /Font /Subtype /Type1 /BaseFont /Symbol >>"));
        FontGlyph a = Decode(Font(doc, 10), 0x61)[0];
        Assert.Equal("alpha", a.Name);
        Assert.Equal("\u03B1", a.Unicode);
        Assert.Equal(0.631, a.Advance, 3);
        using PdfDocument z = Open(static b => b.Obj(10, "<< /Type /Font /Subtype /Type1 /BaseFont /ZapfDingbats >>"));
        FontGlyph d = Decode(Font(z, 10), 0x21)[0];
        Assert.Equal("a1", d.Name);
        Assert.Equal("\u2701", d.Unicode);
    }

    [Fact]
    public void DifferencesWithUniNamesOddNamesAndSuffixes()
    {
        using PdfDocument doc = Open(static b => b.Obj(10,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding << /Type /Encoding /BaseEncoding /WinAnsiEncoding /Differences [65 /uni0042 /g23 /Euro /fi /a.sc 200 /Delta /u1F600] >> >>"));
        List<FontGlyph> g = Decode(Font(doc, 10), 65, 66, 67, 68, 69, 200, 201, 70);
        Assert.Equal("B", g[0].Unicode);
        Assert.False(g[1].HasUnicode);
        Assert.Equal("\uFFFD", g[1].Unicode);
        Assert.Equal("\u20AC", g[2].Unicode);
        Assert.Equal("\uFB01", g[3].Unicode);
        Assert.Equal("a", g[4].Unicode);
        Assert.Equal("\u2206", g[5].Unicode);
        Assert.Equal("\U0001F600", g[6].Unicode);
        Assert.Equal("F", g[7].Unicode);
        Assert.Equal("fi", Ligatures.Decompose(g[3].Unicode));
    }

    [Fact]
    public void ToUnicodeBeatsTheEncodingForSimpleFonts()
    {
        using PdfDocument doc = Open(static b =>
        {
            b.Obj(10, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding /ToUnicode 11 0 R >>");
            b.Stream(11, string.Empty, Ascii("/CIDInit /ProcSet findresource begin 12 dict begin begincmap 1 begincodespacerange <00> <FF> endcodespacerange 1 beginbfchar <41> <0058> endbfchar endcmap end end"));
        });
        Assert.Equal("XB", Shown(Decode(Font(doc, 10), Ascii("AB"))));
    }

    // ---- TrueType ---------------------------------------------------------------------------------------------------

    [Fact]
    public void SymbolicTrueTypeFindsGlyphsThroughTheF000Block()
    {
        byte[] ttf = TinyTrueType.Build(3, [new(3, 0, Pairs((0xF041, 1), (0xF042, 2)))]);
        using PdfDocument doc = Open(b =>
        {
            b.Obj(10, "<< /Type /Font /Subtype /TrueType /BaseFont /ABCDEF+Sym /FirstChar 65 /LastChar 66 /Widths [600 700] /FontDescriptor 11 0 R >>");
            b.Obj(11, "<< /Type /FontDescriptor /FontName /ABCDEF+Sym /Flags 4 /FontFile2 12 0 R >>");
            AddFontFile(b, 12, ttf);
        });
        PdfFont f = Font(doc, 10);
        List<FontGlyph> g = Decode(f, Ascii("AB"));
        Assert.True(f.IsEmbedded);
        Assert.Equal([1, 2], g.Select(static x => x.Gid));
        Assert.All(g, static x => Assert.True(x.Exists));
        Assert.Equal(0.6, g[0].Advance, 6);
        Assert.Equal(0.7, g[1].Advance, 6);
        Assert.Equal(0.001, g[0].OutlineMatrix![0], 9);
        Assert.False(g[0].GetOutline().IsEmpty);
        // No ToUnicode, no encoding and only a (3,0) cmap: printable ASCII codes stand for themselves.
        Assert.True(g[0].HasUnicode);
        Assert.Equal("A", g[0].Unicode);
        // Code 67 has no glyph: .notdef, no existence, and the missing width.
        FontGlyph missing = Decode(f, Ascii("C"))[0];
        Assert.False(missing.Exists);
        Assert.Equal(0, missing.Gid);
        Assert.Equal(0, missing.Advance);
    }

    [Fact]
    public void SymbolicTrueTypeWithMacCmapUsesTheCodeDirectly()
    {
        byte[] ttf = TinyTrueType.Build(2, [new(1, 0, Pairs((65, 1)))]);
        using PdfDocument doc = Open(b =>
        {
            b.Obj(10, "<< /Type /Font /Subtype /TrueType /BaseFont /Sym /FirstChar 65 /Widths [500] /FontDescriptor 11 0 R >>");
            b.Obj(11, "<< /Type /FontDescriptor /FontName /Sym /Flags 4 /FontFile2 12 0 R >>");
            AddFontFile(b, 12, ttf);
        });
        Assert.Equal(1, Decode(Font(doc, 10), 65)[0].Gid);
    }

    [Fact]
    public void NonsymbolicTrueTypeMapsNamesThroughUnicodeCmapThenPost()
    {
        byte[] withCmap = TinyTrueType.Build(3, [new(3, 1, Pairs((0x41, 1), (0x20AC, 2)))]);
        byte[] postOnly = TinyTrueType.Build(3, [], ["square", "triangle"]);
        using PdfDocument doc = Open(b =>
        {
            b.Obj(10, "<< /Type /Font /Subtype /TrueType /BaseFont /A+One /Encoding /WinAnsiEncoding /FontDescriptor 11 0 R /FirstChar 65 /Widths [500] >>");
            b.Obj(11, "<< /Type /FontDescriptor /FontName /A+One /Flags 32 /FontFile2 12 0 R >>");
            AddFontFile(b, 12, withCmap);
            b.Obj(13, "<< /Type /Font /Subtype /TrueType /BaseFont /A+Two /Encoding << /Differences [65 /square /triangle] >> /FontDescriptor 14 0 R >>");
            b.Obj(14, "<< /Type /FontDescriptor /FontName /A+Two /Flags 32 /FontFile2 15 0 R >>");
            AddFontFile(b, 15, postOnly);
        });
        List<FontGlyph> a = Decode(Font(doc, 10), 0x41, 0x80);
        // WinAnsi 0x80 is Euro, found through the (3,1) cmap.
        Assert.Equal(1, a[0].Gid);
        Assert.Equal("A", a[0].Unicode);
        Assert.Equal(2, a[1].Gid);
        Assert.Equal("\u20AC", a[1].Unicode);

        List<FontGlyph> p = Decode(Font(doc, 13), Ascii("AB"));
        Assert.Equal([1, 2], p.Select(static x => x.Gid));
        Assert.All(p, static x => Assert.True(x.Exists));
    }

    // ---- Composite fonts -------------------------------------------------------------------------------------------

    private static void AddType0(PdfBuilder b, string encoding, byte[] ttf, string extraCid = "", string? toUnicode = null)
    {
        b.Obj(10, $"<< /Type /Font /Subtype /Type0 /BaseFont /ABCDEF+T /Encoding /{encoding} /DescendantFonts [11 0 R]{(toUnicode is null ? string.Empty : " /ToUnicode 15 0 R")} >>");
        b.Obj(11, $"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /ABCDEF+T /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 12 0 R /DW 1000 /W [1 [500 400]] /CIDToGIDMap 14 0 R {extraCid} >>");
        b.Obj(12, "<< /Type /FontDescriptor /FontName /ABCDEF+T /Flags 4 /FontFile2 13 0 R >>");
        AddFontFile(b, 13, ttf);
        b.Stream(14, string.Empty, [0, 0, 0, 2, 0, 1, 0, 3]);
        if (toUnicode is not null)
        {
            b.Stream(15, string.Empty, Ascii(toUnicode));
        }
    }

    [Fact]
    public void IdentityHMapsCidsThroughCidToGidMapAndUsesTheFontCmapForUnicode()
    {
        byte[] ttf = TinyTrueType.Build(4, [new(3, 1, Pairs((0x41, 1), (0x42, 2), (0x3042, 3)))]);
        using PdfDocument doc = Open(b => AddType0(b, "Identity-H", ttf));
        PdfFont f = Font(doc, 10);
        List<FontGlyph> g = Decode(f, 0, 1, 0, 2, 0, 3, 0, 9);
        Assert.True(f.IsComposite);
        Assert.Equal(0, f.WMode);
        Assert.Equal([2, 1, 3, 0], g.Select(static x => x.Gid));
        Assert.Equal([1, 2, 3, 9], g.Select(static x => x.Cid));
        Assert.All(g, static x => Assert.Equal(2, x.CodeLength));
        Assert.Equal(0.5, g[0].Advance, 6);
        Assert.Equal(0.4, g[1].Advance, 6);
        Assert.Equal(1.0, g[2].Advance, 6);
        // Unicode from the embedded program's cmap, run backwards: gid 2 is 'B', gid 1 'A', gid 3 hiragana A.
        Assert.Equal("BA\u3042\uFFFD", Shown(g));
        Assert.False(g[3].HasUnicode);
        Assert.False(g[3].Exists);
    }

    [Fact]
    public void ToUnicodeOverridesTheFontCmapForIdentityH()
    {
        byte[] ttf = TinyTrueType.Build(4, [new(3, 1, Pairs((0x41, 1), (0x42, 2)))]);
        using PdfDocument doc = Open(b => AddType0(b, "Identity-H", ttf, toUnicode:
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap 1 begincodespacerange <0000> <FFFF> endcodespacerange 1 beginbfrange <0001> <0002> <0058> endbfrange endcmap end end"));
        Assert.Equal("XY", Shown(Decode(Font(doc, 10), 0, 1, 0, 2)));
    }

    [Fact]
    public void PredefinedCMapsSplitCodesAndGiveUnicodeThroughTheCollection()
    {
        using PdfDocument doc = Open(static b =>
        {
            b.Obj(10, "<< /Type /Font /Subtype /Type0 /BaseFont /MS-Gothic /Encoding /90ms-RKSJ-H /DescendantFonts [11 0 R] >>");
            b.Obj(11, "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /MS-Gothic /CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 2 >> /DW 1000 >>");
            b.Obj(12, "<< /Type /Font /Subtype /Type0 /BaseFont /SimSun /Encoding /UniGB-UCS2-H /DescendantFonts [13 0 R] >>");
            b.Obj(13, "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /SimSun /CIDSystemInfo << /Registry (Adobe) /Ordering (GB1) /Supplement 2 >> >>");
        });
        // Shift-JIS: a two-byte hiragana then a one-byte Latin letter.
        List<FontGlyph> j = Decode(Font(doc, 10), 0x82, 0xA0, 0x41);
        Assert.Equal([2, 1], j.Select(static x => x.CodeLength));
        Assert.Equal("\u3042A", Shown(j));
        Assert.True(j.All(static x => x.HasUnicode));
        // UCS2: the code is the character.
        List<FontGlyph> c = Decode(Font(doc, 12), 0x4E, 0x2D, 0x00, 0x41);
        Assert.Equal("\u4E2DA", Shown(c));
        Assert.Equal(2, c[0].CodeLength);
    }

    [Fact]
    public void CidsOutsideTheCollectionTableUseHalfWidthAndVerticalCMaps()
    {
        using PdfDocument doc = Open(static b =>
        {
            b.Obj(10, "<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /DescendantFonts [11 0 R] >>");
            b.Obj(11, "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /X /CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 6 >> >>");
        });
        // CID 232 is the half-width exclamation mark; 7891 is a vertical variant form.
        List<FontGlyph> g = Decode(Font(doc, 10), 0, 232, 0x1E, 0xD3, 0, 1);
        Assert.Equal("!", g[0].Unicode);
        Assert.True(g[1].HasUnicode);
        Assert.True(g[2].HasUnicode);
    }

    [Fact]
    public void VerticalWritingModeCarriesW2AndDw2()
    {
        using PdfDocument doc = Open(static b =>
        {
            b.Obj(10, "<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-V /DescendantFonts [11 0 R] >>");
            b.Obj(11, "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /X /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /DW 800 /DW2 [900 -800] /W2 [5 [-500 300 800]] >>");
        });
        PdfFont f = Font(doc, 10);
        Assert.Equal(1, f.WMode);
        List<FontGlyph> g = Decode(f, 0, 1, 0, 5);
        Assert.Equal(0.8, g[0].Advance, 6);
        Assert.Equal(-0.8, g[0].VerticalAdvance, 6);
        Assert.Equal(0.4, g[0].OriginX, 6);
        Assert.Equal(0.9, g[0].OriginY, 6);
        Assert.Equal(-0.5, g[1].VerticalAdvance, 6);
        Assert.Equal(0.3, g[1].OriginX, 6);
        Assert.Equal(0.8, g[1].OriginY, 6);
    }

    [Fact]
    public void EmbeddedCMapWithCidRangesAndMixedLengths()
    {
        const string cmap = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /Test def /WMode 0 def 2 begincodespacerange <00> <7F> <8000> <FFFF> endcodespacerange 1 begincidrange <20> <7E> 100 endcidrange 1 begincidchar <8001> 500 endcidchar endcmap end end";
        using PdfDocument doc = Open(static b =>
        {
            b.Obj(10, "<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding 12 0 R /DescendantFonts [11 0 R] >>");
            b.Obj(11, "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /X /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /W [100 [250] 500 500 777] >>");
            b.Stream(12, "/Type /CMap /CMapName /Test", Ascii(cmap));
        });
        List<FontGlyph> g = Decode(Font(doc, 10), 0x20, 0x80, 0x01, 0x21);
        Assert.Equal([1, 2, 1], g.Select(static x => x.CodeLength));
        Assert.Equal([100, 500, 101], g.Select(static x => x.Cid));
        Assert.Equal(0.25, g[0].Advance, 6);
        Assert.Equal(0.777, g[1].Advance, 6);
        Assert.Equal(1.0, g[2].Advance, 6);
    }

    // ---- Type 3 ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Type3ExposesCharProcsMatrixAndResources()
    {
        using PdfDocument doc = Open(static b =>
        {
            b.Obj(10, "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 50 50] /FontMatrix [0.01 0 0 0.01 0 0] /CharProcs << /sq 11 0 R >> /Encoding << /Type /Encoding /Differences [65 /sq /A] >> /FirstChar 65 /LastChar 66 /Widths [50 20] /Resources << >> >>");
            b.Stream(11, string.Empty, Ascii("50 0 0 0 50 50 d1 0 0 50 50 re f"));
        });
        var f = Assert.IsType<Type3Font>(Font(doc, 10));
        List<FontGlyph> g = Decode(f, Ascii("AB"));
        Assert.True(f.IsType3);
        Assert.Equal(0.01, f.FontMatrix[0], 9);
        Assert.NotNull(f.Resources);
        Assert.Equal("sq", g[0].Name);
        Assert.NotNull(g[0].CharProc);
        Assert.True(g[0].Exists);
        Assert.Equal(0.5, g[0].Advance, 6);
        Assert.Null(g[1].CharProc);
        Assert.False(g[1].Exists);
        Assert.Equal("A", g[1].Unicode);
        Assert.Equal(0.2, g[1].Advance, 6);
        Assert.True(g[0].GetOutline().IsEmpty);
    }

    // ---- Failures and substitution ---------------------------------------------------------------------------------

    private const string BrokenFont =
        "<< /Type /Font /Subtype /TrueType /BaseFont /ABCDEF+Arial /Encoding /WinAnsiEncoding /FirstChar 65 /LastChar 65 /Widths [300] /FontDescriptor 11 0 R >>";

    private static void AddBroken(PdfBuilder b)
    {
        b.Obj(10, BrokenFont);
        b.Obj(11, "<< /Type /FontDescriptor /FontName /ABCDEF+Arial /Flags 32 /FontFile2 12 0 R >>");
        AddFontFile(b, 12, Ascii(new string('x', 200)));
    }

    [Fact]
    public void BrokenEmbeddedProgramFallsBackToASubstituteScaledToThePdfWidths()
    {
        byte[] sub = TinyTrueType.Build(3, [new(3, 1, Pairs((0x41, 1)))]);
        var provider = new FixedFont(sub);
        using PdfDocument doc = Open(AddBroken, provider);
        PdfFont f = Font(doc, 10);
        FontGlyph g = Decode(f, Ascii("A"))[0];
        Assert.True(f.EmbeddedFailed);
        Assert.False(f.IsEmbedded);
        Assert.True(f.IsSubstituted);
        Assert.Contains(doc.Diagnostics, static d => d.Message.Contains("could not be read", StringComparison.Ordinal));
        Assert.Equal("Arial", provider.Requests[0].FontName);
        Assert.Equal("Helvetica", provider.Requests[0].StandardName);
        Assert.Equal(1, g.Gid);
        Assert.True(g.Exists);
        Assert.Equal(0.3, g.Advance, 6);
        // Substitute advance 600, PDF width 300: the glyph is squeezed to half.
        Assert.Equal(0.0005, g.OutlineMatrix![0], 9);
        Assert.Equal(0.001, g.OutlineMatrix[3], 9);
    }

    [Fact]
    public void BrokenEmbeddedProgramWithNoSubstituteKeepsMetricsAndUnicode()
    {
        using PdfDocument doc = Open(AddBroken);
        PdfFont f = Font(doc, 10);
        FontGlyph g = Decode(f, Ascii("A"))[0];
        Assert.True(f.EmbeddedFailed);
        Assert.Null(f.Program);
        Assert.Equal(-1, g.Gid);
        Assert.False(g.Exists);
        Assert.Equal("A", g.Unicode);
        Assert.Equal(0.3, g.Advance, 6);
        Assert.True(g.GetOutline().IsEmpty);
    }

    [Fact]
    public void DamagedFontDictionariesDecodeWithoutThrowing()
    {
        using PdfDocument doc = Open(static b =>
        {
            b.Obj(10, "<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /NoSuchCMap >>");
            b.Obj(11, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Widths [1 2 3] /FirstChar 9999 /Encoding 7 >>");
            b.Obj(12, "<< /Type /Font /Subtype /Type3 /CharProcs 5 /Widths /x >>");
        });
        Assert.Single(Decode(Font(doc, 10), 0, 65));
        Assert.Equal(2, Decode(Font(doc, 11), 65, 66).Count);
        Assert.Equal(2, Decode(Font(doc, 12), 65, 66).Count);
        Assert.Single(Decode(doc.GetFont((PdfDictionary?)null), 65));
    }

    // ---- Caching ---------------------------------------------------------------------------------------------------

    [Fact]
    public void FontsAreBuiltOncePerObjectEvenUnderContention()
    {
        using PdfDocument doc = Open(static b => b.Obj(10, "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman >>"));
        var fonts = new PdfFont[64];
        Parallel.For(0, fonts.Length, i => fonts[i] = Font(doc, 10));
        Assert.All(fonts, f => Assert.Same(fonts[0], f));
        Assert.True(fonts[0].Id > 0);
        var glyphs = new FontGlyph[64];
        Parallel.For(0, glyphs.Length, i => glyphs[i] = Decode(fonts[0], 65)[0]);
        Assert.All(glyphs, g => Assert.Same(glyphs[0], g));
    }

    // ---- Provider --------------------------------------------------------------------------------------------------

    [Fact]
    public void SystemProviderFindsArialAndTimesLikeFontsOnThisHost()
    {
        if (!Directory.Exists("/usr/share/fonts") && !Directory.Exists("/Library/Fonts") && !Directory.Exists(Environment.GetFolderPath(Environment.SpecialFolder.Windows)))
        {
            return;
        }

        PdfFontRequest arial = new() { FontName = "Arial", FamilyName = "Arial", StandardName = "Helvetica" };
        PdfFontRequest times = new() { FontName = "TimesNewRomanPS-BoldMT", FamilyName = "TimesNewRoman", StandardName = "Times-Bold", IsBold = true, IsSerif = true };
        PdfSubstituteFont? a = PdfFontProviders.Default.FindFont(arial);
        PdfSubstituteFont? t = PdfFontProviders.Default.FindFont(times);
        Assert.NotNull(a);
        Assert.NotNull(t);
        Assert.True(a.Data.Length > 1000);
        Console.WriteLine($"Arial -> {a.Name} ({a.CacheKey}); Times New Roman Bold -> {t.Name} ({t.CacheKey})");
    }

    [Fact]
    public void ADocumentWithSystemFontsGetsOutlinesForHelvetica()
    {
        if (!Directory.Exists("/usr/share/fonts"))
        {
            return;
        }

        using PdfDocument doc = Open(static b => b.Obj(10, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"), PdfFontProviders.Default);
        PdfFont f = Font(doc, 10);
        FontGlyph g = Decode(f, Ascii("A"))[0];
        Assert.True(f.IsSubstituted);
        Assert.True(g.Exists);
        Assert.False(g.GetOutline().IsEmpty);
        Assert.Equal(0.667, g.Advance, 3);
        double scale = g.OutlineMatrix![0];
        Assert.InRange(scale, 0.0004, 0.0025);
    }

    [Fact]
    public void LigatureCharactersDecompose()
    {
        Assert.Equal("office", Ligatures.Decompose("o\uFB03ce"));
        Assert.Equal("fl", Ligatures.Decompose("\uFB02"));
        Assert.Same("plain", Ligatures.Decompose("plain"));
    }
}
