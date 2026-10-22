using System.Text;
using OnePdf.Fonts;
using OnePdf.FontData;
using OnePdf.PdfFonts;
using static OnePdf.Tests.PdfFonts.FontPdf;

namespace OnePdf.Tests.FontsPackageTests;

[CollectionDefinition("GlobalFontProviders", DisableParallelization = true)]
public sealed class GlobalFontProvidersDefinition;

/// <summary>
/// Registration changes process-wide state, so these tests run alone. The one test that needs the package unregistered
/// does its check before registering.
/// </summary>
[Collection("GlobalFontProviders")]
public class FontsRegistrationTests
{
    private static readonly (string Base, string Face)[] Standard =
    [
        ("Courier", "LiberationMono-Regular"), ("Courier-Bold", "LiberationMono-Bold"),
        ("Courier-Oblique", "LiberationMono-Italic"), ("Courier-BoldOblique", "LiberationMono-BoldItalic"),
        ("Helvetica", "LiberationSans-Regular"), ("Helvetica-Bold", "LiberationSans-Bold"),
        ("Helvetica-Oblique", "LiberationSans-Italic"), ("Helvetica-BoldOblique", "LiberationSans-BoldItalic"),
        ("Times-Roman", "LiberationSerif-Regular"), ("Times-Bold", "LiberationSerif-Bold"),
        ("Times-Italic", "LiberationSerif-Italic"), ("Times-BoldItalic", "LiberationSerif-BoldItalic"),
        ("Symbol", "NotoSansMath-Regular"), ("ZapfDingbats", "NotoSansSymbols2-Regular"),
    ];

    private static PdfDocument OpenWith(string baseFont, IPdfFontProvider? provider)
    {
        string subtype = baseFont.StartsWith("Arial", StringComparison.Ordinal) || baseFont.Contains("New", StringComparison.Ordinal) ? "TrueType" : "Type1";
        return Open(b => b.Obj(10, $"<< /Type /Font /Subtype /{subtype} /BaseFont /{baseFont} >>"), provider);
    }

    private static string Shown(List<FontGlyph> g) => string.Concat(g.Select(static x => x.Unicode));

    [Fact]
    public void WithoutThePackageAndWithoutSystemFontsThereAreNoOutlines()
    {
        bool saved = PdfFontProviders.UseSystemFonts;
        PdfFontProviders.UseSystemFonts = false;
        try
        {
            using PdfDocument doc = OpenWith("Helvetica", PdfFontProviders.Default);
            PdfFont f = Font(doc, 10);
            FontGlyph g = Decode(f, (byte)'A')[0];
            Assert.False(f.IsSubstituted);
            Assert.True(g.GetOutline().IsEmpty);
            Assert.Equal(0.667, g.Advance, 3);

            // Registering is what brings the outlines; the chain then answers without any installed font.
            FontsPackage.Register();
            FontsPackage.Register();
            using PdfDocument doc2 = OpenWith("Helvetica", PdfFontProviders.Default);
            PdfFont f2 = Font(doc2, 10);
            Assert.True(f2.IsSubstituted);
            Assert.False(Decode(f2, (byte)'A')[0].GetOutline().IsEmpty);
        }
        finally
        {
            PdfFontProviders.UseSystemFonts = saved;
        }
    }

    [Fact]
    public void EveryStandardFontResolvesToItsDocumentedFace()
    {
        foreach ((string name, string face) in Standard)
        {
            PdfSubstituteFont? s = FontsPackage.Provider.FindFont(new PdfFontRequest { FontName = name, StandardName = name });
            Assert.NotNull(s);
            Assert.Equal(face, s.Name);
            Assert.Equal("OnePdf.Fonts/" + face, s.CacheKey);
            Assert.Equal(SubstituteFonts.AllFaces.First(x => x.Name == face).GetData().Length, s.Data.Length);
        }
    }

    [Fact]
    public void StandardFontsGetOutlinesFromThePackageWithAfmWidths()
    {
        foreach ((string name, string face) in Standard.Where(static x => x.Base is not ("Symbol" or "ZapfDingbats")))
        {
            using PdfDocument doc = OpenWith(name, FontsPackage.Provider);
            PdfFont f = Font(doc, 10);
            Assert.True(f.IsSubstituted, name);
            List<FontGlyph> g = Decode(f, Encoding.ASCII.GetBytes("Hamburgefonts 0123"));
            Assert.Equal("Hamburgefonts 0123", Shown(g));
            StandardFontMetrics afm = Core14.Get(name);
            for (int i = 0; i < g.Count; i++)
            {
                char c = "Hamburgefonts 0123"[i];
                // Widths are the AFM's, whatever the substitute's own advance is.
                Assert.Equal((afm.GetWidth(g[i].Name!) ?? 0) / 1000.0, g[i].Advance, 4);
                if (c != ' ')
                {
                    Assert.True(g[i].Exists, name + " " + c);
                    Assert.False(g[i].GetOutline().IsEmpty, name + " " + c);
                }
            }
        }
    }

    [Fact]
    public void SubstituteIsScaledHorizontallyToTheWidths()
    {
        using PdfDocument doc = Open(static b =>
            b.Obj(10, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FirstChar 65 /LastChar 65 /Widths [400] >>"), FontsPackage.Provider);
        FontGlyph g = Decode(Font(doc, 10), (byte)'A')[0];
        Assert.Equal(0.4, g.Advance, 4);
        // Liberation Sans 'A' is 667 wide; squeezing to 400 scales x by about 0.6.
        Assert.InRange(g.OutlineMatrix![0] / g.OutlineMatrix[3], 0.55, 0.65);
    }

    private static (int Covered, int Total, List<string> Missing) Cover(string name)
    {
        using PdfDocument doc = OpenWith(name, FontsPackage.Provider);
        PdfFont f = Font(doc, 10);
        int covered = 0, total = 0;
        var missing = new List<string>();
        foreach (AfmGlyph a in Core14.Get(name).Glyphs)
        {
            if (a.Code < 0 || a.Name == "space")
            {
                continue;
            }

            total++;
            FontGlyph g = Decode(f, (byte)a.Code)[0];
            if (g.Exists && !g.GetOutline().IsEmpty)
            {
                covered++;
            }
            else
            {
                missing.Add(a.Name);
            }
        }

        return (covered, total, missing);
    }

    [Fact]
    public void ZapfDingbatsIsFullyCovered()
    {
        (int c, int t, List<string> m) = Cover("ZapfDingbats");
        TestContext.Current.SendDiagnosticMessage($"ZapfDingbats via core: {c}/{t}; missing {string.Join(' ', m)}");
        Assert.Equal(201, t);
        Assert.True(m.Count == 0, string.Join(" ", m));
    }

    [Fact]
    public void SymbolIsCoveredExceptWeierstrass()
    {
        (int c, int t, List<string> m) = Cover("Symbol");
        TestContext.Current.SendDiagnosticMessage($"Symbol via core: {c}/{t}; missing {string.Join(' ', m)}");
        Assert.Equal(188, t);
        Assert.True(m.SequenceEqual(["weierstrass"]), string.Join(" ", m));
    }

    [Theory]
    [InlineData("Arial", "LiberationSans-Regular")]
    [InlineData("ArialMT", "LiberationSans-Regular")]
    [InlineData("Arial-BoldMT", "LiberationSans-Bold")]
    [InlineData("Arial,Italic", "LiberationSans-Italic")]
    [InlineData("ABCDEF+Arial-BoldItalicMT", "LiberationSans-BoldItalic")]
    [InlineData("TimesNewRoman", "LiberationSerif-Regular")]
    [InlineData("TimesNewRomanPSMT", "LiberationSerif-Regular")]
    [InlineData("TimesNewRoman,Bold", "LiberationSerif-Bold")]
    [InlineData("TimesNewRomanPS-ItalicMT", "LiberationSerif-Italic")]
    [InlineData("CourierNew", "LiberationMono-Regular")]
    [InlineData("CourierNewPS-BoldMT", "LiberationMono-Bold")]
    [InlineData("CourierNew,BoldItalic", "LiberationMono-BoldItalic")]
    public void CommonAliasesResolveThroughADocument(string baseFont, string face)
    {
        using PdfDocument doc = OpenWith(baseFont, FontsPackage.Provider);
        PdfFont f = Font(doc, 10);
        Assert.True(f.IsSubstituted, baseFont);
        FontGlyph g = Decode(f, (byte)'e')[0];
        Assert.False(g.GetOutline().IsEmpty);
        PdfSubstituteFont? s = FontsPackage.Provider.FindFont(new PdfFontRequest { FontName = baseFont[(baseFont.IndexOf('+') + 1)..] });
        Assert.Equal(face, s?.Name);
    }

    [Theory]
    [InlineData("Verdana")]
    [InlineData("Arial-Black")]
    public void UnknownNamesAndCidFontsAreLeftAlone(string baseFont)
    {
        Assert.Null(FontsPackage.Provider.FindFont(new PdfFontRequest { FontName = baseFont }));
        Assert.Null(FontsPackage.Provider.FindFont(new PdfFontRequest { FontName = "Helvetica", StandardName = "Helvetica", CidOrdering = "Japan1" }));
    }

    [Fact]
    public void FacesAreParsedOnceAndSharedAcrossDocumentsAndThreads()
    {
        var programs = new System.Collections.Concurrent.ConcurrentBag<object?>();
        Parallel.For(0, 16, _ =>
        {
            using PdfDocument doc = OpenWith("Helvetica", FontsPackage.Provider);
            FontGlyph g = Decode(Font(doc, 10), (byte)'A')[0];
            Assert.False(g.GetOutline().IsEmpty);
            programs.Add(Font(doc, 10).Program);
        });
        Assert.Single(programs.Distinct());
        Assert.Equal(SubstituteFonts.AllFaces[4].GetData().ToArray().Length, SubstituteFonts.AllFaces[4].GetData().Length);
        Assert.True(SubstituteFonts.AllFaces[4].GetData().Equals(SubstituteFonts.AllFaces[4].GetData()));
    }
}
