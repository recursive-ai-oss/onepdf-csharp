using System.Text;
using OnePdf.Text;
using OnePdf.Tests.Content;
using OnePdf.Tests.PdfA;
using OnePdf.Tests.Tagging;

namespace OnePdf.Tests.Fonts;

[CollectionDefinition("FontProviderRegistry", DisableParallelization = true)]
public sealed class FontProviderRegistry;

/// <summary>Font embedding conveniences: a missing provider is named, per-font width conflicts, and /ToUnicode derived from what the font chain knows.</summary>
[Collection("FontProviderRegistry")]
public class FontFrictionTests
{
    private static readonly IPdfFontProvider Provider = OnePdf.Fonts.FontsPackage.Provider;

    private static byte[] Page(string content, string fontObjects, string resources = "/F1 20 0 R") =>
        TestPdf.Build(content, $"/Resources << /Font << {resources} >> >>", b =>
        {
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(fontObjects, "(\\d+) 0 obj\n(.*?)\nendobj\n", System.Text.RegularExpressions.RegexOptions.Singleline))
            {
                b.Obj(int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), m.Groups[2].Value);
            }
        }, mediaBox: "[0 0 300 300]", objects: 60);

    private static PdfDocument Editable(byte[] pdf) => PdfDocument.Open(pdf, new PdfOpenOptions { AllowEditing = true });

    private const string Arial = "20 0 obj\n<< /Type /Font /Subtype /TrueType /BaseFont /ArialMT /Encoding /WinAnsiEncoding >>\nendobj\n";
    private const string Verdana = "21 0 obj\n<< /Type /Font /Subtype /TrueType /BaseFont /Verdana /Encoding /WinAnsiEncoding >>\nendobj\n";

    [Fact]
    public void WithoutAnyFontProviderTheReportNamesThePackageAndWithOneItEmbeds()
    {
        IPdfFontProvider[] saved = PdfFontProviders.ReplaceRegistered([]);
        try
        {
            byte[] pdf = Page("BT /F1 12 Tf 20 250 Td (Hello) Tj /F2 12 Tf (There) Tj ET", Arial + Verdana, "/F1 20 0 R /F2 21 0 R");
            using (PdfDocument d = Editable(pdf))
            {
                PdfSubstituteEmbedReport report = d.EmbedSubstituteFonts();
                Assert.True(report.FontProviderMissing);
                Assert.Contains("RecursiveAI.OnePdf.Fonts", report.ProviderMessage, StringComparison.Ordinal);
                Assert.Contains("FontsPackage.Register()", report.ProviderMessage, StringComparison.Ordinal);
                Assert.Equal(PdfSubstituteOutcome.NoFontProvider, report.Entries.Single(static e => e.BaseFont == "ArialMT").Outcome);
                Assert.Contains("RecursiveAI.OnePdf.Fonts", report.Entries.Single(static e => e.BaseFont == "ArialMT").Message, StringComparison.Ordinal);

                // A face that is not a standard one has no substitute even with a provider; that stays NoSubstitute.
                Assert.Equal(PdfSubstituteOutcome.NoSubstitute, report.Entries.Single(static e => e.BaseFont == "Verdana").Outcome);
                Assert.Equal(0, report.EmbeddedCount);

                // The PDF/UA and PDF/A preparation fail with the same fix instead of carrying on with unembedded fonts.
                InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => d.PreparePdfUA(new PdfUAOptions { EmbedSubstituteFonts = true }));
                Assert.Contains("RecursiveAI.OnePdf.Fonts", ex.Message, StringComparison.Ordinal);
                ex = Assert.Throws<InvalidOperationException>(() => d.PreparePdfA(new PdfAOptions { EmbedSubstituteFonts = true }));
                Assert.Contains("RecursiveAI.OnePdf.Fonts", ex.Message, StringComparison.Ordinal);
            }

            // Setting the provider on the document is enough.
            using (PdfDocument d = Editable(pdf))
            {
                d.FontProvider = Provider;
                PdfSubstituteEmbedReport report = d.EmbedSubstituteFonts();
                Assert.False(report.FontProviderMissing);
                Assert.Null(report.ProviderMessage);
                Assert.Equal(1, report.EmbeddedCount);
                Assert.Equal(PdfSubstituteOutcome.NoSubstitute, report.Entries.Single(static e => e.BaseFont == "Verdana").Outcome);
            }

            // So is registering the package.
            PdfFontProviders.ReplaceRegistered([Provider]);
            using (PdfDocument d = Editable(pdf))
            {
                Assert.Equal(1, d.EmbedSubstituteFonts().EmbeddedCount);
            }
        }
        finally
        {
            PdfFontProviders.ReplaceRegistered(saved);
        }
    }

    [Fact]
    public void AnEmbeddedEntryCarriesTheWidthConflictsOfItsFont()
    {
        // Codes 65 and 66 both name /A but have different widths in the PDF: one glyph can have one advance.
        byte[] pdf = Page("BT /F1 12 Tf 20 250 Td (AB) Tj ET",
            "20 0 obj\n<< /Type /Font /Subtype /TrueType /BaseFont /ArialMT /FirstChar 65 /LastChar 66 /Widths [667 700] /Encoding << /Type /Encoding /BaseEncoding /WinAnsiEncoding /Differences [66 /A] >> >>\nendobj\n");
        using PdfDocument d = Editable(pdf);
        d.FontProvider = Provider;
        PdfSubstituteEntry e = Assert.Single(d.EmbedSubstituteFonts().Entries);
        Assert.Equal(PdfSubstituteOutcome.Embedded, e.Outcome);
        Assert.Single(e.WidthConflicts);
        Assert.Contains(e.WidthConflicts, static c => c is 65 or 66);
        Assert.NotEmpty(e.WidthChanges);
        Assert.Empty(e.MissingCodes);
    }

    // ---- AddMissingToUnicode -------------------------------------------------------------------------------------------------------

    private static string PlainText(PdfDocument d) => string.Join("\n", d.ExtractText(new PdfTextOptions { UseStructureTree = false }).Select(static p => p.Text));

    private const string DifferencesFont =
        "20 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FirstChar 65 /LastChar 68 /Widths [700 700 700 700] /Encoding << /Type /Encoding /BaseEncoding /WinAnsiEncoding /Differences [65 /Euro /bullet /gid123 /uni0416] >> >>\nendobj\n";

    private static string ToUnicodeOf(PdfDictionary font) => Encoding.ASCII.GetString(((PdfStream)font.Get("ToUnicode")!).GetDecodedBytes().Span);

    [Fact]
    public void AFontWithoutToUnicodeGetsEntriesForItsKnownCodesAndExtractionIsUnchanged()
    {
        byte[] pdf = Page("BT /F1 12 Tf 20 250 Td (ABCD) Tj ET", DifferencesFont);
        using PdfDocument d = Editable(pdf);
        string text = PlainText(d);
        Assert.Equal("\u20AC\u2022", text.Substring(0, 2));
        PdfDictionary font = d.Pages[0].Resources.GetDictionary("Font")!.GetDictionary("F1")!;
        Assert.False(font.ContainsKey("ToUnicode"));

        PdfToUnicodeReport report = d.AddMissingToUnicode();
        PdfToUnicodeEntry e = Assert.Single(report.Entries);
        Assert.Equal(3, e.CodesAdded);
        Assert.Equal([67], e.CodesWithoutUnicode); // /gid123 names no character, and nothing is guessed
        Assert.Equal(1, report.FontsChanged);
        Assert.Equal(1, report.FontsWithUnknownCodes);
        Assert.Equal(text, PlainText(d));

        string cmap = ToUnicodeOf(font);
        Assert.Contains("<41>", cmap, StringComparison.Ordinal);
        Assert.Contains("<20AC>", cmap, StringComparison.Ordinal);
        Assert.Contains("<0416>", cmap, StringComparison.Ordinal);
        Assert.DoesNotContain("<43>", cmap, StringComparison.Ordinal);

        // A second call finds nothing more to add.
        PdfToUnicodeReport again = d.AddMissingToUnicode();
        Assert.Equal(0, again.CodesAdded);

        // The saved file reads the same.
        using var ms = new MemoryStream();
        d.Save(ms);
        using PdfDocument back = PdfDocument.Open(ms.ToArray());
        Assert.Equal(text, PlainText(back));
    }

    [Fact]
    public void AnExistingMapKeepsItsEntriesAndGainsTheMissingCodes()
    {
        string tu = "30 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n";
        byte[] pdf = Page("BT /F1 12 Tf 20 250 Td (ABD) Tj ET", DifferencesFont.Replace("/Encoding <<", "/ToUnicode 30 0 R /Encoding <<", StringComparison.Ordinal) + tu);
        using PdfDocument d = Editable(pdf);
        Assert.True(d.SetToUnicode(d.Pages[0].Resources.GetDictionary("Font")!.GetDictionary("F1")!, new Dictionary<int, string> { [65] = "X" }));
        PdfToUnicodeReport report = d.AddMissingToUnicode();
        Assert.Equal(2, report.CodesAdded); // 66 and 68; 65 was already mapped, by the caller, and stays
        Assert.Contains("X", PlainText(d), StringComparison.Ordinal);
        string cmap = ToUnicodeOf(d.Pages[0].Resources.GetDictionary("Font")!.GetDictionary("F1")!);
        Assert.Contains("<0058>", cmap, StringComparison.Ordinal);
        Assert.DoesNotContain("<20AC>", cmap, StringComparison.Ordinal);
    }

    [Fact]
    public void FontsWhoseEncodingAlreadyDeterminesUnicodeAreLeftAlone()
    {
        byte[] pdf = Page("BT /F1 12 Tf 20 250 Td (Hello) Tj ET", Arial);
        using PdfDocument d = Editable(pdf);
        Assert.Empty(d.AddMissingToUnicode().Entries);
        Assert.False(d.Pages[0].Resources.GetDictionary("Font")!.GetDictionary("F1")!.ContainsKey("ToUnicode"));
    }

    [Fact]
    public void ThePdfUaToUnicodeFindingClearsInTheCheckerAndInVeraPdfWhereEveryCodeIsKnown()
    {
        byte[] pdf = Page("BT /F1 12 Tf 20 250 Td (ABD) Tj ET", DifferencesFont);
        using PdfDocument d = Editable(pdf);
        Assert.Contains(d.CheckPdfUA(), static v => v.Clause == "7.21.7");
        byte[] before = TagSupport.Save(d);
        d.AddMissingToUnicode();
        Assert.DoesNotContain(d.CheckPdfUA(), static v => v.Clause == "7.21.7");
        byte[] after = TagSupport.Save(d);
        if (VeraPdf.Path is not null)
        {
            Assert.Contains(VeraPdf.Validate(before, "ua1"), static f => f.StartsWith("7.21.7-", StringComparison.Ordinal));
            Assert.DoesNotContain(VeraPdf.Validate(after, "ua1"), static f => f.StartsWith("7.21.7-", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ACompositeFontGetsEntriesForTheCodesItShowsThatTheChainKnows()
    {
        // Identity-H over an embedded-less CIDFontType2 in the Adobe-Identity collection: the chain knows no Unicode for any code, so nothing is written and the codes are listed.
        byte[] pdf = Page("BT /F1 12 Tf 20 250 Td <00410042> Tj ET",
            "20 0 obj\n<< /Type /Font /Subtype /Type0 /BaseFont /Foo /Encoding /Identity-H /DescendantFonts [21 0 R] >>\nendobj\n"
            + "21 0 obj\n<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Foo /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /DW 500 >>\nendobj\n");
        using PdfDocument d = Editable(pdf);
        PdfToUnicodeReport report = d.AddMissingToUnicode();
        Assert.Equal(0, report.CodesAdded);
        PdfToUnicodeEntry e = Assert.Single(report.Entries);
        Assert.Equal([0x41, 0x42], e.CodesWithoutUnicode);
        Assert.False(d.Pages[0].Resources.GetDictionary("Font")!.GetDictionary("F1")!.ContainsKey("ToUnicode"));
    }

    [Fact]
    public void ReadOnlyDocumentsAreRefused() =>
        Assert.Throws<InvalidOperationException>(() =>
        {
            using PdfDocument d = PdfDocument.Open(Page("BT ET", Arial));
            d.AddMissingToUnicode();
        });
}
