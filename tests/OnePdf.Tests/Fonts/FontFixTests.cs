using System.Text;
using OnePdf.FontData;
using OnePdf.FontWriting;
using OnePdf.Text;
using OnePdf.Tests.Content;
using OnePdf.Tests.Tagging;

namespace OnePdf.Tests.Fonts;

/// <summary>
/// Fixes for fonts a remediation pass meets: text for codes whose glyphs carry no character, widths of embedded CFF programs brought to the
/// PDF's, a caller's face for a font without a substitute, and the codes a substitute report lists.
/// </summary>
[Collection("FontProviderRegistry")]
public class FontFixTests
{
    private static readonly IPdfFontProvider Provider = OnePdf.Fonts.FontsPackage.Provider;

    private static byte[] Page(string content, Action<PdfBuilder> objects, string resources = "/F1 20 0 R", string catalog = "") =>
        TestPdf.Build(content, $"/Resources << /Font << {resources} >> >>", objects, mediaBox: "[0 0 300 300]", objects: 60, catalogEntries: catalog);

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

    private static string Layout(PdfDocument d)
    {
        var dev = new Positions();
        for (int i = 0; i < d.Pages.Count; i++)
        {
            d.Pages[i].Interpret(dev, new PdfInterpretOptions { GlyphOutlines = false, GenerateAppearances = false });
        }

        return string.Join(" ", dev.Items);
    }

    private static PdfFontFile Liberation(string standard)
    {
        PdfSubstituteFont sub = Provider.FindFont(new PdfFontRequest
        {
            FontName = standard,
            FamilyName = standard.Split('-')[0],
            StandardName = standard,
            IsBold = standard.Contains("Bold", StringComparison.Ordinal),
            IsSerif = standard.StartsWith("Times", StringComparison.Ordinal),
        })!;
        return PdfFontFile.Open(sub.Data, sub.FaceIndex);
    }

    private static PdfFontFile ProgramOf(PdfDictionary font)
    {
        PdfDictionary owner = font.GetName("Subtype") == "Type0" ? (PdfDictionary)font.GetArray("DescendantFonts")![0]! : font;
        PdfDictionary desc = owner.GetDictionary("FontDescriptor")!;
        PdfStream file = (PdfStream)(desc.Get("FontFile3") ?? desc.Get("FontFile2") ?? desc.Get("FontFile"))!;
        return PdfFontFile.Open(file.GetDecodedBytes());
    }

    // ---- an embedded Type1C font whose glyphs are named gidNNNNN ----------------------------------------------------------------

    private static byte[] CffFontPdf(byte[]? cff = null) =>
        Page("BT /F1 20 Tf 20 200 Td (ABCDE) Tj ET", b =>
        {
            b.Obj(20, "<< /Type /Font /Subtype /Type1 /BaseFont /ABCDEF+Synth /FirstChar 65 /LastChar 69 /Widths [751 234 300 500 700] "
                + "/Encoding << /Type /Encoding /BaseEncoding /WinAnsiEncoding /Differences [65 /gid00132 /gid00131 /C /D /E] >> /FontDescriptor 21 0 R >>");
            b.Obj(21, "<< /Type /FontDescriptor /FontName /ABCDEF+Synth /Flags 4 /FontBBox [0 -200 1000 900] /ItalicAngle 0 /Ascent 900 /Descent -200 /CapHeight 700 /StemV 80 /FontFile3 22 0 R >>");
            b.Stream(22, "/Subtype /Type1C", cff ?? SyntheticCff.Standard());
        });

    [Fact]
    public void CodesWhoseGlyphNamesCarryNoCharacterAreReportedWithTheirGlyphAndTheCallerSuppliesTheText()
    {
        using PdfDocument plain = Editable(CffFontPdf());
        Assert.StartsWith("\uFFFD\uFFFDCDE", PlainText(plain).Trim(), StringComparison.Ordinal);
        PdfToUnicodeEntry e = Assert.Single(plain.AddMissingToUnicode().Entries);
        Assert.Equal([65, 66], e.CodesWithoutUnicode);
        Assert.Equal(3, e.CodesAdded); // C, D and E come from the Adobe Glyph List
        Assert.Equal(["gid00132", "gid00131"], e.Unmapped.Select(static u => u.GlyphName));
        Assert.Equal([1, 2], e.Unmapped.Select(static u => u.GlyphId));
        Assert.Equal(751, e.Unmapped[0].Width, 0.01);
        Assert.NotNull(e.Font);

        // The same through the callback: it is asked only for the unmapped codes, and what it returns is written to /ToUnicode.
        using PdfDocument d = Editable(CffFontPdf());
        string layout = Layout(d);
        var asked = new List<int>();
        PdfToUnicodeReport report = d.AddMissingToUnicode(new PdfToUnicodeOptions
        {
            CodeToText = u =>
            {
                asked.Add(u.Code);
                return u.Code == 65 ? "ti" : u.Code == 66 ? "x" : null;
            },
        });
        Assert.Equal([65, 66], asked);
        PdfToUnicodeEntry r = Assert.Single(report.Entries);
        Assert.Equal(5, r.CodesAdded);
        Assert.Equal(2, r.CodesFromCaller);
        Assert.Empty(r.CodesWithoutUnicode);
        Assert.Equal(0, report.FontsWithUnknownCodes);
        Assert.StartsWith("tixCDE", PlainText(d).Trim(), StringComparison.Ordinal);
        Assert.Equal(layout, Layout(d));

        // Nothing is left to do the second time.
        Assert.Empty(d.AddMissingToUnicode().Entries);

        // And the low-level call reaches the same font from the report.
        using PdfDocument low = Editable(CffFontPdf());
        PdfToUnicodeEntry le = Assert.Single(low.AddMissingToUnicode().Entries);
        Assert.True(low.SetToUnicode(le.Font!, new Dictionary<int, string> { [65] = "ti", [66] = "x" }));
        Assert.StartsWith("tixCDE", PlainText(low).Trim(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyOrNullAnswerLeavesTheCodeUnmapped()
    {
        using PdfDocument d = Editable(CffFontPdf());
        PdfToUnicodeEntry e = Assert.Single(d.AddMissingToUnicode(new PdfToUnicodeOptions { CodeToText = static u => u.Code == 65 ? string.Empty : null }).Entries);
        Assert.Equal(0, e.CodesFromCaller);
        Assert.Equal([65, 66], e.CodesWithoutUnicode);
    }

    [Fact]
    public void EmbeddedCffWidthsAreBroughtToThePdfsWithoutMovingTextOrTouchingOutlines()
    {
        using PdfDocument d = Editable(CffFontPdf());
        string text = PlainText(d), layout = Layout(d);
        PdfFontFile before = ProgramOf(FontOf(d));
        Assert.Equal(751.42, before.GetAdvanceWidth(1), 2);

        PdfWidthMatchReport report = d.MatchEmbeddedFontWidths();
        PdfWidthMatchEntry e = Assert.Single(report.Entries);
        Assert.Equal(PdfWidthMatchOutcome.Matched, e.Outcome);
        Assert.Equal(1, report.FontsRewritten);
        Assert.Equal(5, e.WidthChanges.Count);

        PdfFontFile after = ProgramOf(FontOf(d));
        double[] pdf = [751, 234, 300, 500, 700];
        for (int g = 1; g <= 5; g++)
        {
            Assert.Equal(pdf[g - 1], after.GetAdvanceWidth(g), 3);
        }

        Assert.Equal(text, PlainText(d));
        Assert.Equal(layout, Layout(d));
        Assert.True(after.GlyphCount == before.GlyphCount);
        for (int g = 0; g < before.GlyphCount; g++)
        {
            Assert.Equal(before.GetGlyphName(g), after.GetGlyphName(g));
        }

        // The widths are now in step, so a second call finds nothing to do; the result survives a save.
        Assert.Equal(PdfWidthMatchOutcome.AlreadyMatching, Assert.Single(d.MatchEmbeddedFontWidths().Entries).Outcome);
        using PdfDocument back = PdfDocument.Open(TagSupport.Save(d));
        Assert.Equal(234, ProgramOf(FontOf(back)).GetAdvanceWidth(2), 3);
        Assert.Equal(layout, Layout(back));
    }

    [Fact]
    public void AFontWhoseProgramReachesItsWidthThroughASubroutineIsLeftAsItWasWithTheReason()
    {
        byte[] subr = SyntheticCff.Cat(SyntheticCff.Num(50), [11]);
        byte[] g1 = SyntheticCff.Cat(SyntheticCff.Num(-107), [10], SyntheticCff.Num(10), SyntheticCff.Num(20), [21], [14]);
        using PdfDocument d = Editable(CffFontPdf(SyntheticCff.Standard([subr], g1)));
        PdfWidthMatchEntry e = Assert.Single(d.MatchEmbeddedFontWidths().Entries);
        Assert.Equal(PdfWidthMatchOutcome.Refused, e.Outcome);
        Assert.Contains("subroutine", e.Note, StringComparison.Ordinal);
        Assert.Equal(705.66, ProgramOf(FontOf(d)).GetAdvanceWidth(1), 2); // the width the subroutine gives, untouched
    }

    [Fact]
    public void ACidKeyedCffGetsItsWidthsFromWAndItsTextFromTheCaller()
    {
        byte[] cid = CffSubsetter.Subset(SyntheticCff.Standard(), [1, 2, 3, 4], "Sub");
        byte[] pdf = Page("BT /F1 20 Tf 20 200 Td <0001000200030004> Tj ET", b =>
        {
            b.Obj(20, "<< /Type /Font /Subtype /Type0 /BaseFont /ABCDEF+SynthCid /Encoding /Identity-H /DescendantFonts [21 0 R] >>");
            b.Obj(21, "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /ABCDEF+SynthCid /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /DW 1000 /W [1 [750 234 300 500]] /FontDescriptor 24 0 R >>");
            b.Obj(24, "<< /Type /FontDescriptor /FontName /ABCDEF+SynthCid /Flags 4 /FontBBox [0 -200 1000 900] /ItalicAngle 0 /Ascent 900 /Descent -200 /CapHeight 700 /StemV 80 /FontFile3 25 0 R >>");
            b.Stream(25, "/Subtype /CIDFontType0C", cid);
        });
        using PdfDocument d = Editable(pdf);
        string layout = Layout(d);
        PdfToUnicodeEntry e = Assert.Single(d.AddMissingToUnicode(new PdfToUnicodeOptions { CodeToText = static u => ((char)('A' + u.Code - 1)).ToString() }).Entries);
        Assert.Equal(4, e.CodesFromCaller);
        Assert.StartsWith("ABCD", PlainText(d).Trim(), StringComparison.Ordinal);

        PdfWidthMatchEntry w = Assert.Single(d.MatchEmbeddedFontWidths().Entries);
        Assert.Equal(PdfWidthMatchOutcome.Matched, w.Outcome);
        PdfFontFile program = ProgramOf(FontOf(d));
        double[] expected = [750, 234, 300, 500];
        for (int g = 1; g <= 4; g++)
        {
            Assert.Equal(expected[g - 1], program.GetAdvanceWidth(g), 3);
        }

        Assert.Equal(layout, Layout(d));
    }

    // ---- a /ToUnicode with a range across blocks of 256 codes ------------------------------------------------------------------------

    [Fact]
    public void AToUnicodeRangeAcrossBlocksOf256CodesIsWrittenAgainWithTheSameMeaning()
    {
        byte[] pdf = Page("BT /F1 20 Tf 20 200 Td <04100412041A8A3B0041> Tj ET", b =>
        {
            b.Obj(20, "<< /Type /Font /Subtype /Type0 /BaseFont /Untitled /Encoding /Identity-H /DescendantFonts [21 0 R] /ToUnicode 23 0 R >>");
            b.Obj(21, "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Untitled /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /DW 500 >>");
            b.Stream(23, string.Empty, Encoding.ASCII.GetBytes("/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /Adobe-Identity-H def /CMapType 2 def "
                + "1 begincodespacerange <0000> <FFFF> endcodespacerange 1 beginbfrange <0000> <FFFF> <0000> endbfrange endcmap end end"));
        });
        using PdfDocument d = Editable(pdf);
        string before = PlainText(d), layout = Layout(d);
        Assert.StartsWith("\u0410\u0412\u041A\u8A3BA", before.Trim(), StringComparison.Ordinal);

        PdfToUnicodeEntry e = Assert.Single(d.AddMissingToUnicode().Entries);
        Assert.True(e.MapRewritten);
        Assert.Equal(0, e.CodesAdded);
        Assert.Empty(d.AddMissingToUnicode().Entries); // a second call has nothing more to do

        var map = CMapParser.Parse(((PdfStream)FontOf(d).Get("ToUnicode")!).GetDecodedBytes().Span, static _ => null);
        Assert.False(map.HasWideBfRanges());
        Assert.Equal(before, PlainText(d));
        Assert.Equal(layout, Layout(d));
    }

    // ---- a caller's face for a font without a substitute -----------------------------------------------------------------------

    private static string Widths600(int first, int last) => "[" + string.Join(" ", Enumerable.Repeat("600", last - first + 1)) + "]";

    private static byte[] VerdanaPdf(string content = "BT /F1 12 Tf 20 250 Td (Verdana text) Tj /F2 12 Tf 0 -20 Td (Plain) Tj ET") =>
        Page(content, b =>
        {
            b.Obj(20, $"<< /Type /Font /Subtype /TrueType /BaseFont /Verdana-Bold /Encoding /WinAnsiEncoding /FirstChar 32 /LastChar 126 /Widths {Widths600(32, 126)} /FontDescriptor 22 0 R >>");
            b.Obj(21, "<< /Type /Font /Subtype /TrueType /BaseFont /ArialMT /Encoding /WinAnsiEncoding >>");
            b.Obj(22, "<< /Type /FontDescriptor /FontName /Verdana-Bold /Flags 32 /FontBBox [-50 -207 1447 1000] /ItalicAngle 0 /Ascent 1005 /Descent -210 /CapHeight 727 /StemV 160 /FontWeight 700 >>");
        }, "/F1 20 0 R /F2 21 0 R");

    [Fact]
    public void ACallersFaceIsEmbeddedForAFontWithNoSubstituteWithTheWidthsOfThePdf()
    {
        PdfFontFile face = Liberation("Helvetica-Bold");
        using PdfDocument d = Editable(VerdanaPdf());
        string text = PlainText(d), layout = Layout(d);
        var asked = new List<PdfSubstituteRequest>();
        PdfSubstituteEmbedReport report = d.EmbedSubstituteFonts(new PdfSubstituteEmbedOptions
        {
            FaceForFont = r =>
            {
                asked.Add(r);
                return r.FontName == "Verdana-Bold" ? face : null;
            },
        });

        // Asked once, for the font the provider has no face for; Arial keeps its provider face.
        PdfSubstituteRequest request = Assert.Single(asked);
        Assert.Equal("Verdana-Bold", request.FontName);
        Assert.True(request.IsBold);
        PdfSubstituteEntry verdana = report.Entries.Single(static e => e.BaseFont == "Verdana-Bold");
        Assert.Equal(PdfSubstituteOutcome.Embedded, verdana.Outcome);
        Assert.True(verdana.FromCaller);
        Assert.False(report.Entries.Single(static e => e.BaseFont == "ArialMT").FromCaller);
        Assert.Equal(2, report.EmbeddedCount);

        // The face's glyphs, the PDF's widths and positions; the name stays the document's.
        PdfFontFile written = ProgramOf(FontOf(d));
        Assert.Equal(600, written.GetAdvanceWidth(written.GetGlyphForUnicode('e')), 0.6);
        Assert.Matches("^[A-Z]{6}\\+Verdana-Bold$", FontOf(d).GetName("BaseFont")!);
        Assert.NotEmpty(verdana.WidthChanges);
        Assert.Equal(text, PlainText(d));
        Assert.Equal(layout, Layout(d));
    }

    [Fact]
    public void WithoutAHookOrWithOneThatDeclinesTheFontHasNoSubstituteAndAFaceThatIsNotTrueTypeIsRefused()
    {
        using (PdfDocument d = Editable(VerdanaPdf()))
        {
            Assert.Equal(PdfSubstituteOutcome.NoSubstitute, d.EmbedSubstituteFonts().Entries.Single(static e => e.BaseFont == "Verdana-Bold").Outcome);
        }

        using (PdfDocument d = Editable(VerdanaPdf()))
        {
            PdfSubstituteEntry e = d.EmbedSubstituteFonts(new PdfSubstituteEmbedOptions { FaceForFont = static _ => null }).Entries.Single(static x => x.BaseFont == "Verdana-Bold");
            Assert.Equal(PdfSubstituteOutcome.NoSubstitute, e.Outcome);
            Assert.False(e.FromCaller);
        }

        using (PdfDocument d = Editable(VerdanaPdf()))
        {
            PdfFontFile cff = PdfFontFile.Open(SyntheticCff.Standard());
            PdfSubstituteEntry e = d.EmbedSubstituteFonts(new PdfSubstituteEmbedOptions { FaceForFont = _ => cff }).Entries.Single(static x => x.BaseFont == "Verdana-Bold");
            Assert.Equal(PdfSubstituteOutcome.Refused, e.Outcome);
            Assert.Equal(PdfFontEmbedFailure.UnsupportedProgram, e.Failure);
            Assert.True(e.FromCaller);
            Assert.False(FontOf(d).GetDictionary("FontDescriptor")!.ContainsKey("FontFile2"));
        }
    }

    [Fact]
    public void ACodeTheCallersFaceLacksIsListedAndDrawsNotdefInsteadOfRefusing()
    {
        PdfFontFile full = Liberation("Helvetica-Bold");
        int a = full.GetGlyphForUnicode('V');
        PdfFontFile onlyV = PdfFontFile.Open(TrueTypeSubsetter.Subset(full.Sfnt!, [a], [('V', a)]));
        using PdfDocument d = Editable(VerdanaPdf("BT /F1 12 Tf 20 250 Td (VW) Tj ET"));
        PdfSubstituteEntry e = d.EmbedSubstituteFonts(new PdfSubstituteEmbedOptions { FaceForFont = _ => onlyV }).Entries.Single(static x => x.BaseFont == "Verdana-Bold");
        Assert.Equal(PdfSubstituteOutcome.Embedded, e.Outcome);
        Assert.Equal([(int)'W'], e.MissingCodes);
    }

    // ---- MissingCodes lists only what the pages show ---------------------------------------------------------------------------------

    private static byte[] DifferencesPdf(string content, string catalog) =>
        Page(content, b => b.Obj(20, "<< /Type /Font /Subtype /TrueType /BaseFont /ArialMT /Encoding << /Type /Encoding /BaseEncoding /WinAnsiEncoding /Differences [200 /uni0378 /uni0379] >> >>"), catalog: catalog);

    private const string FormUsesTheFont = "/AcroForm << /DR << /Font << /F1 20 0 R >> >> /Fields [] >>";

    [Fact]
    public void MissingCodesAreOnlyTheCodesThePagesShowEvenWhenAFormMayFillInTheFontLater()
    {
        // The font is a form default, so it keeps its whole encoding; the two named codes have no glyph, but nothing shows them.
        using (PdfDocument d = Editable(DifferencesPdf("BT /F1 12 Tf 20 250 Td (Hello) Tj ET", FormUsesTheFont)))
        {
            PdfSubstituteEntry e = Assert.Single(d.EmbedSubstituteFonts().Entries);
            Assert.Equal(PdfSubstituteOutcome.Embedded, e.Outcome);
            Assert.Empty(e.MissingCodes);
        }

        // Shown code 200 is listed; code 201 is not shown and is not listed.
        using (PdfDocument d = Editable(DifferencesPdf("BT /F1 12 Tf 20 250 Td (Hi\\310) Tj ET", FormUsesTheFont)))
        {
            PdfSubstituteEntry e = Assert.Single(d.EmbedSubstituteFonts().Entries);
            Assert.Equal([200], e.MissingCodes);
        }

        // Without a form the same is true.
        using (PdfDocument d = Editable(DifferencesPdf("BT /F1 12 Tf 20 250 Td (Hi\\311) Tj ET", string.Empty)))
        {
            Assert.Equal([201], Assert.Single(d.EmbedSubstituteFonts().Entries).MissingCodes);
        }
    }
}
