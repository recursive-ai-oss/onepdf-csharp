using System.Text;
using System.Threading.Tasks;
using OnePdf.Hyphenation;
using OnePdf.Text;

namespace OnePdf.Layout.Tests;

/// <summary>
/// The bundled hyphenation patterns and the layout engine's use of them. Expected points come from the pattern files' own exception
/// lists (which must win over the patterns) and from the standard syllabification of common words in each language.
/// </summary>
public class HyphenationTests
{
    private static string Mark(PatternHyphenator h, string word)
    {
        var sb = new StringBuilder(word);
        int[] points = h.GetBreaks(word);
        for (int i = points.Length - 1; i >= 0; i--)
        {
            sb.Insert(points[i], '-');
        }

        return sb.ToString();
    }

    [Theory]
    [InlineData("en-US", "hyphenation", "hy-phen-ation")]
    [InlineData("en-US", "table", "ta-ble")]
    [InlineData("en-US", "algorithm", "al-go-rithm")]
    [InlineData("en-US", "typesetting", "type-set-ting")]
    [InlineData("en-US", "associate", "as-so-ciate")] // exception list
    [InlineData("en-US", "reciprocity", "reci-procity")] // exception list
    [InlineData("en-US", "project", "project")] // exception list: no breaks
    [InlineData("en-US", "Hyphenation", "Hy-phen-ation")]
    [InlineData("en", "obligatory", "oblig-a-tory")] // exception list
    [InlineData("en-GB", "hyphenation", "hy-phen-a-tion")]
    [InlineData("en-GB", "universities", "uni-ver-sit-ies")] // exception list
    [InlineData("en-AU", "throughout", "through-out")] // exception list
    [InlineData("de", "Silbentrennung", "Sil-ben-tren-nung")]
    [InlineData("de-DE", "Donaudampfschifffahrt", "Do-nau-dampf-schiff-fahrt")]
    [InlineData("de-AT", "Kindergarten", "Kin-der-gar-ten")]
    [InlineData("de-1901", "Zusammenhang", "Zu-sam-men-hang")]
    [InlineData("de-CH", "Bibliothek", "Bi-blio-thek")]
    [InlineData("fr", "développement", "dé-ve-lop-pe-ment")]
    [InlineData("fr-CA", "ordinateur", "or-di-na-teur")]
    [InlineData("es", "ordenador", "or-de-na-dor")]
    [InlineData("es-MX", "murciélago", "mur-cié-la-go")]
    [InlineData("it", "automobile", "au-to-mo-bi-le")]
    [InlineData("nl", "boekhandel", "boek-han-del")]
    [InlineData("nl-BE", "fietsenstalling", "fiet-sen-stal-ling")]
    [InlineData("pt-BR", "computador", "com-pu-ta-dor")]
    [InlineData("pt", "hardware", "hard-ware")] // exception list
    [InlineData("pl", "komputer", "kom-pu-ter")]
    [InlineData("pl", "nigdy", "ni-gdy")] // exception list
    [InlineData("da", "bibliotek", "bi-bli-o-tek")]
    [InlineData("no", "bibliotek", "bi-blio-tek")]
    [InlineData("nb", "attende", "at-ten-de")] // Bokmal exception beats the shared list
    [InlineData("nn", "attende", "att-en-de")] // Nynorsk exception beats the shared list
    [InlineData("no", "attende", "atten-de")]
    [InlineData("sk", "knižnica", "kniž-nica")]
    [InlineData("sl", "knjižnica", "knji-žni-ca")]
    [InlineData("bg", "библиотека", "биб-ли-о-те-ка")]
    [InlineData("uk", "бібліотека", "бі-бліо-те-ка")]
    [InlineData("gl", "biblioteca", "bi-blio-te-ca")]
    [InlineData("et", "raamatukogu", "raa-ma-tu-kogu")]
    [InlineData("el", "βιβλιοθήκη", "βι-βλιο-θή-κη")]
    [InlineData("la", "bibliotheca", "bi-blio-the-ca")]
    [InlineData("cy", "llyfrgell", "llyfr-gell")]
    [InlineData("ga", "leabharlann", "leabh-ar-lann")]
    [InlineData("lt", "biblioteka", "bi-b-lio-te-ka")]
    public void KnownWordsHyphenateAtTheExpectedPoints(string language, string word, string expected)
    {
        PatternHyphenator? h = Hyphenators.For(language);
        Assert.NotNull(h);
        Assert.Equal(expected, Mark(h, word));
    }

    [Fact]
    public void EveryBundledLanguageLoadsAndResolvesToItself()
    {
        Assert.Equal(26, Hyphenators.Languages.Count);
        foreach (string id in Hyphenators.Languages)
        {
            Assert.Equal(id, Hyphenators.Resolve(id));
            PatternHyphenator? h = Hyphenators.For(id);
            Assert.NotNull(h);
            Assert.Equal(id, h.Language);
        }
    }

    [Theory]
    [InlineData("en", "en-us")]
    [InlineData("en-CA", "en-us")]
    [InlineData("en_gb", "en-gb")]
    [InlineData("en-Latn-IN", "en-gb")]
    [InlineData("de", "de-1996")]
    [InlineData("de-CH-1996", "de-1996")]
    [InlineData("de-CH", "de-ch-1901")]
    [InlineData("de-1901", "de-1901")]
    [InlineData("de-CH-1901", "de-ch-1901")]
    [InlineData("el-GR", "el-monoton")]
    [InlineData("nb-NO", "nb")]
    [InlineData("pt-PT", "pt")]
    [InlineData("FR", "fr")]
    [InlineData("sv", null)]
    [InlineData("fi", null)]
    [InlineData("ru", null)]
    [InlineData("ja", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TagsMapToBundledPatterns(string? tag, string? expected)
    {
        Assert.Equal(expected, Hyphenators.Resolve(tag));
        Assert.Equal(expected is not null, Hyphenators.IsSupported(tag));
        Assert.Equal(expected is null, Hyphenators.For(tag) is null);
    }

    [Fact]
    public void MinimumsAreApplied()
    {
        PatternHyphenator def = Hyphenators.For("en-US")!;
        Assert.Equal((5, 2, 3), (def.MinWordLength, def.LeftMin, def.RightMin));
        Assert.Equal("hy-phen-ation", Mark(def, "hyphenation"));

        PatternHyphenator wide = Hyphenators.For("en-US", new HyphenationOptions { LeftMin = 4, RightMin = 5 })!;
        Assert.Equal("hyphen-ation", Mark(wide, "hyphenation"));

        PatternHyphenator longWords = Hyphenators.For("en-US", new HyphenationOptions { MinWordLength = 12 })!;
        Assert.Equal("hyphenation", Mark(longWords, "hyphenation"));

        // Exceptions are held to the minimums too.
        Assert.Equal("as-so-ciate", Mark(def, "associate"));
        Assert.Equal("asso-ciate", Mark(Hyphenators.For("en-US", new HyphenationOptions { LeftMin = 3 })!, "associate"));
    }

    [Fact]
    public void ShortAndHostileWordsGiveNoPoints()
    {
        PatternHyphenator h = Hyphenators.For("de")!;
        Assert.Empty(h.GetBreaks(""));
        Assert.Empty(h.GetBreaks("a"));
        Assert.Empty(h.GetBreaks("Haus"));
        Assert.Empty(h.GetBreaks((string?)null));
        Assert.Empty(h.GetBreaks(new string('a', 5000)));
        Assert.Empty(h.GetBreaks("1234-5678"));
        _ = h.GetBreaks(new string('a', 255)); // at the cap: must not throw
        IReadOnlyList<int> viaInterface = ((OnePdf.Layout.IHyphenator)h).GetBreaks("Silbentrennung", "fr");
        Assert.Equal(h.GetBreaks("Silbentrennung"), viaInterface);
    }

    [Fact]
    public void CustomPatternsAndExceptionsWork()
    {
        // Liang's published example: hyphenation with the patterns he lists for it.
        PatternHyphenator h = PatternHyphenator.FromPatterns(
            ["hy3ph", "he2n", "hena4", "hen5at", "1na", "n2at", "1tio", "2io", "o2n"],
            ["ta-ble"],
            new HyphenationOptions { MinWordLength = 2 });
        Assert.Equal("hy-phen-ation", Mark(h, "hyphenation"));
        Assert.Equal("ta-ble", Mark(h, "table"));
        Assert.Equal("ta-ble", Mark(h, "TABLE").ToLowerInvariant());
        Assert.Throws<ArgumentNullException>(() => PatternHyphenator.FromPatterns(null!));
    }

    [Fact]
    public void AutoPicksPatternsFromTheSpanLanguage()
    {
        IHyphenator auto = Hyphenators.Auto();
        Assert.Equal([3, 6, 10], auto.GetBreaks("Silbentrennung", "de"));
        Assert.Equal([2, 6], auto.GetBreaks("hyphenation", "en-US"));
        Assert.Empty(auto.GetBreaks("hyphenation", null));
        Assert.Empty(auto.GetBreaks("Hyphenering", "sv"));
        IHyphenator withDefault = Hyphenators.Auto(defaultLanguage: "en-US");
        Assert.NotEmpty(withDefault.GetBreaks("hyphenation", null));
        Assert.NotEmpty(withDefault.GetBreaks("hyphenation", ""));
    }

    [Fact]
    public void ParallelUseOfOneHyphenatorIsConsistent()
    {
        IHyphenator auto = Hyphenators.Auto();
        string[] langs = ["en-US", "de", "fr", "es", "nl", "pl", "nb", "it"];
        int[][] expected = [.. langs.Select(l => auto.GetBreaks("international", l).ToArray())];
        Parallel.For(0, 200, i =>
        {
            int k = i % langs.Length;
            Assert.Equal(expected[k], auto.GetBreaks("international", langs[k]));
        });
    }

    private const string Prose =
        "Internationalization of hyphenation algorithms characterizes extraordinarily comprehensive documentation, "
        + "notwithstanding unpredictable circumstances surrounding administrative responsibilities.";

    private static LayoutDocument Narrow(IHyphenator? hyphenator, string text = Prose, string language = "en-US") =>
        LayoutDocument.Compose(doc =>
        {
            doc.Language("en-US");
            doc.Title("Hyphenation");
            doc.Page(page =>
        {
            page.Size(130, 300);
            page.Margin(10);
            page.Content().Text(t =>
            {
                t.FontSize(11);
                t.Language(language);
                if (hyphenator is not null)
                {
                    t.Hyphenator(hyphenator);
                }

                t.Span(text);
            });
        });
        });

    private static string Extract(byte[] pdf, bool join)
    {
        using PdfDocument d = PdfDocument.Open(pdf);
        return Support.Normalize(string.Join(" ", d.ExtractText(new PdfTextOptions { JoinHyphenatedWords = join }).Select(t => t.Text)));
    }

    [Fact]
    public void ANarrowColumnHyphenatesAndExtractionReturnsWholeWords()
    {
        byte[] plain = Support.Save(Narrow(null));
        byte[] hyph = Support.Save(Narrow(Hyphenators.Auto()));

        string raw = Extract(hyph, join: false);
        Assert.True(raw.Contains('\u00AD') || raw.Contains('-'), "no line ended in a hyphen: " + raw);
        Assert.NotEqual(Extract(plain, join: false), raw);

        // Joined, every word is whole again and the text equals the source.
        string joined = Extract(hyph, join: true);
        Assert.Equal(Prose, joined);
        foreach (string word in Prose.Split(' ', ',', '.'))
        {
            Assert.Contains(word, joined, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GermanTextUsesGermanPatternsFromTheLanguageTag()
    {
        const string text = "Donaudampfschifffahrtsgesellschaft Silbentrennungsalgorithmus Kindergartenbetreuung Hochschulabschluss";
        byte[] pdf = Support.Save(Narrow(Hyphenators.Auto(), text, "de-DE"));
        Assert.Equal(text, Extract(pdf, join: true));
        string raw = Extract(pdf, join: false);
        Assert.True(raw.Contains('\u00AD') || raw.Contains('-'));
        // No hyphenator means a word wider than the column overflows instead.
        byte[] none = Support.Save(Narrow(null, text, "de-DE"));
        Assert.DoesNotContain('\u00AD', Extract(none, join: false));
    }

    [Fact]
    public void HyphenatedTaggedPdfAIsCompliant()
    {
        LayoutDocument layout = Narrow(Hyphenators.Auto());
        using var b = new MemoryStream();
        layout.Save(b, new PdfSaveOptions { PdfA = new PdfAOptions { Part = PdfAPart.Part2, Conformance = PdfAConformance.B } });
        Assert.Empty(TagSupport.Vera(b.ToArray(), "2b"));

        using var a = new MemoryStream();
        layout.Save(a, new PdfSaveOptions { PdfA = new PdfAOptions { Part = PdfAPart.Part2, Conformance = PdfAConformance.A } });
        Assert.Empty(TagSupport.Vera(a.ToArray(), "2a"));
    }

    [Fact]
    public void HyphenationIsOptInAndDeterministic()
    {
        Assert.Equal(Support.Save(Narrow(null)), Support.Save(Narrow(null)));
        Assert.Equal(Support.Save(Narrow(Hyphenators.Auto())), Support.Save(Narrow(Hyphenators.Auto())));
        Assert.Equal(Support.Save(Narrow(Hyphenators.For("en-US"))), Support.Save(Narrow(Hyphenators.Auto())));
    }
}
