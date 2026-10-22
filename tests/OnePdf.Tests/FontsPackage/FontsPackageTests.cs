using System.Security.Cryptography;
using OnePdf.Fonts;
using OnePdf.FontData;
using OnePdf.FontPrograms;

namespace OnePdf.Tests.FontsPackageTests;

public class FontsPackageTests
{
    // SHA-256 of the upstream release files (see tools/fonts/fetch.sh); the embedded copies must be identical.
    private static readonly Dictionary<string, string> Upstream = new()
    {
        ["LiberationMono-BoldItalic"] = "79451f3c09fe25116098853b7a2ca6e2436220ccc11af022979adbcf195be130",
        ["LiberationMono-Bold"] = "bd62a0672d0b9b6710b01df434c80ad54fa5f0835207eb7b17b7a761463067bb",
        ["LiberationMono-Italic"] = "605c01c711b44480a7508d349dfbf3264e81fa43d69e61cfa7d10b86e764c4d1",
        ["LiberationMono-Regular"] = "f2b83c763e8afd21709333370bed4774337fae82267937e2b5aea7e2fbd922c1",
        ["LiberationSans-BoldItalic"] = "698da70fc191cc5f33ad4d6d3fe830fe4624b898ea2e3169955928b7c491f1ee",
        ["LiberationSans-Bold"] = "788abee4c806d660e8aee46689dd8540cd4bb98da03dcc9d171ce3efd99a9173",
        ["LiberationSans-Italic"] = "e5bae5c4cde31f22142753855f4f8fb86da6ff39955ed3c0a11248b0d16948b0",
        ["LiberationSans-Regular"] = "76d04c18ea243f426b7de1f3ad208e927008f961dc5945e5aad352d0dfde8ee8",
        ["LiberationSerif-BoldItalic"] = "f17db8af71e24d2066b587546021d4f0b296be389512b658dec3c09affeb11a7",
        ["LiberationSerif-Bold"] = "d754ba427cfe0bca54ae052384baa8f842da5bd6550ad4da024ac441e7a7d5ce",
        ["LiberationSerif-Italic"] = "0e3dea9f8d613e006ccfa62201f33e265d19167bd0907725c3e145368b04fc2e",
        ["LiberationSerif-Regular"] = "058ea80864aef09a23f45cbec2bb5400bc3dfbdea01c3f10538a21fcb497fb74",
        ["NotoSansMath-Regular"] = "b127e84699212b6b2ef50aff58e0ebebeec04ffe6db1b9eb9e209c8c3d97b4aa",
        ["NotoSansSymbols2-Regular"] = "c4a0a80f0041ce4be81e2478faad22776d23edb98ae3f0d19bd37044820ecf9d",
        ["NotoSansSymbols-Regular"] = "6eea9cb4cd39269ea9f95ba5c2735f80ae74049dfc9e1a7c932a5cfc8f0c3030",
    };

    [Fact]
    public void EmbeddedFontsAreIdenticalToUpstream()
    {
        Assert.Equal(Upstream.Count, SubstituteFonts.AllFaces.Count);
        foreach (SubstituteFace f in SubstituteFonts.AllFaces)
        {
            Assert.Equal(Upstream[f.Name], Convert.ToHexStringLower(SHA256.HashData(f.GetData().Span)));
            using Stream s = f.OpenRead();
            Assert.Equal(f.GetData().Length, s.Length);
        }
    }

    [Theory]
    [InlineData("Liberation")]
    [InlineData("NotoSansMath")]
    [InlineData("NotoSansSymbols2")]
    [InlineData("NotoSansSymbols")]
    public void LicenseTextsAreEmbedded(string name)
    {
        var asm = typeof(SubstituteFonts).Assembly;
        using Stream? s = asm.GetManifestResourceStream("OnePdf.Fonts." + name + ".OFL.txt");
        Assert.NotNull(s);
        using var r = new StreamReader(s);
        string text = r.ReadToEnd();
        Assert.Contains("SIL OPEN FONT LICENSE Version 1.1", text);
        Assert.Contains("Copyright", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryFaceParsesWithCoreFontPrograms()
    {
        foreach (SubstituteFace f in SubstituteFonts.AllFaces)
        {
            FontProgram p = FontProgramLoader.Open(f.GetData());
            Assert.True(p.GlyphCount > 100, f.Name);
            Assert.True(p.TryGetAdvance(1, out _), f.Name);
            Assert.NotNull(p.GetOutline(p.GlyphCount / 2));
        }
    }

    public static TheoryData<string, string> Aliases() => new()
    {
        { "Helvetica", "LiberationSans-Regular" },
        { "Helvetica-Bold", "LiberationSans-Bold" },
        { "Helvetica-Oblique", "LiberationSans-Italic" },
        { "Helvetica-BoldOblique", "LiberationSans-BoldItalic" },
        { "Arial", "LiberationSans-Regular" },
        { "ArialMT", "LiberationSans-Regular" },
        { "Arial-BoldMT", "LiberationSans-Bold" },
        { "Arial-ItalicMT", "LiberationSans-Italic" },
        { "Arial-BoldItalicMT", "LiberationSans-BoldItalic" },
        { "Arial,Bold", "LiberationSans-Bold" },
        { "Arial,BoldItalic", "LiberationSans-BoldItalic" },
        { "Arial Bold", "LiberationSans-Bold" },
        { "ABCDEF+Arial-BoldMT", "LiberationSans-Bold" },
        { "Times-Roman", "LiberationSerif-Regular" },
        { "Times-Bold", "LiberationSerif-Bold" },
        { "Times-Italic", "LiberationSerif-Italic" },
        { "Times-BoldItalic", "LiberationSerif-BoldItalic" },
        { "TimesNewRoman", "LiberationSerif-Regular" },
        { "TimesNewRomanPSMT", "LiberationSerif-Regular" },
        { "TimesNewRomanPS-BoldMT", "LiberationSerif-Bold" },
        { "TimesNewRomanPS-ItalicMT", "LiberationSerif-Italic" },
        { "TimesNewRomanPS-BoldItalicMT", "LiberationSerif-BoldItalic" },
        { "TimesNewRoman,Bold", "LiberationSerif-Bold" },
        { "Times New Roman", "LiberationSerif-Regular" },
        { "Courier", "LiberationMono-Regular" },
        { "Courier-Bold", "LiberationMono-Bold" },
        { "Courier-Oblique", "LiberationMono-Italic" },
        { "Courier-BoldOblique", "LiberationMono-BoldItalic" },
        { "CourierNew", "LiberationMono-Regular" },
        { "CourierNewPSMT", "LiberationMono-Regular" },
        { "CourierNewPS-BoldMT", "LiberationMono-Bold" },
        { "CourierNewPS-BoldItalicMT", "LiberationMono-BoldItalic" },
        { "CourierNew,Italic", "LiberationMono-Italic" },
        { "Symbol", "NotoSansMath-Regular" },
        { "SymbolMT", "NotoSansMath-Regular" },
        { "ZapfDingbats", "NotoSansSymbols2-Regular" },
    };

    [Theory]
    [MemberData(nameof(Aliases))]
    public void NamesResolveToTheRightFace(string name, string face)
    {
        Assert.True(SubstituteFonts.TryFind(name, out SubstituteFace f), name);
        Assert.Equal(face, f.Name);
        // The core's own alias table agrees that this is a standard font (SymbolMT and the glued "Arial Bold" are the names only this package adds).
        if (name is not ("SymbolMT" or "Arial Bold"))
        {
            Assert.NotNull(Core14.Resolve(name));
        }
    }

    [Fact]
    public void StandardNamesAllResolveAndMatchCoreStyle()
    {
        Assert.Equal(Core14.Names.Order(), SubstituteFonts.StandardNames.Order());
        foreach (string n in SubstituteFonts.StandardNames)
        {
            Assert.True(SubstituteFonts.TryFind(n, out SubstituteFace f), n);
            if (n is not ("Symbol" or "ZapfDingbats"))
            {
                Assert.Equal(n.Contains("Bold"), f.IsBold);
                Assert.Equal(n.Contains("Italic") || n.Contains("Oblique"), f.IsItalic);
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Verdana")]
    [InlineData("Arial-Black")]
    [InlineData("Courier-Narrow")]
    [InlineData("Helvetica-Bogus")]
    [InlineData("Symbol-Bold")]
    public void OtherNamesDoNotResolve(string? name)
    {
        Assert.False(SubstituteFonts.TryFind(name, out _));
        Assert.Null(SubstituteFonts.GetChain(name));
    }

    private static (int total, List<string> missing) Coverage(string standard, bool zapf)
    {
        var chain = SubstituteFonts.GetChain(standard)!;
        var cmaps = chain.Select(f => ((SfntFont)FontProgramLoader.Open(f.GetData())).FindUnicodeCmap()!).ToList();
        int total = 0;
        var missing = new List<string>();
        foreach (AfmGlyph g in Core14.Get(standard).Glyphs)
        {
            if (g.Code < 0 || g.Name == "space")
            {
                continue;
            }

            total++;
            bool ok = zapf ? GlyphList.TryGetZapfDingbats(g.Name, out string t) : GlyphList.TryGetListed(g.Name, out t);
            ok = ok && t.Length > 0;
            bool found = false;
            if (ok)
            {
                int cp = char.ConvertToUtf32(t, 0);
                if (!zapf)
                {
                    cp = SubstituteFonts.RemapSymbolCodePoint(cp);
                }

                found = cmaps.Any(c => c.Lookup(cp) > 0);
            }

            if (!found)
            {
                missing.Add(g.Name);
            }
        }

        return (total, missing);
    }

    [Fact]
    public void SymbolCoverage()
    {
        (int total, List<string> missing) = Coverage("Symbol", false);
        TestContext.Current.SendDiagnosticMessage($"Symbol: {total - missing.Count}/{total}; missing: {string.Join(' ', missing)}");
        // U+2118 (weierstrass) is in none of the substitute fonts; every other Symbol glyph is covered.
        Assert.Equal(["weierstrass"], missing);
    }

    [Fact]
    public void ZapfDingbatsCoverage()
    {
        (int total, List<string> missing) = Coverage("ZapfDingbats", true);
        TestContext.Current.SendDiagnosticMessage($"ZapfDingbats: {total - missing.Count}/{total}");
        Assert.Empty(missing);
    }
}
