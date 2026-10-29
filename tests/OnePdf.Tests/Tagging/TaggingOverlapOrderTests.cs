using static OnePdf.Tests.Tagging.TagDocs;

namespace OnePdf.Tests.Tagging;

/// <summary>
/// Text drawn over itself (fake bold, two rows on one baseline) must extract in the same order before and after a file is tagged, although
/// tagging wraps the runs in marked content.
/// </summary>
public class TaggingOverlapOrderTests
{
    private static void AssertTaggingKeepsExtraction(string content)
    {
        // Happy() compares the extraction JSON and the display list before and after, and fails on any difference.
        Happy(Make(content), static _ => PdfBlockRole.Paragraph, null, out _);
    }

    [Fact]
    public void FakeBoldDrawnTwiceInSeparateOperators()
    {
        AssertTaggingKeepsExtraction(T(72, 700, "TEXAS EDUCATION AGENCY") + T(72.5, 700, "TEXAS EDUCATION AGENCY"));
    }

    [Fact]
    public void FakeBoldDrawnTwiceWithTheCopyInTheSameTextObject()
    {
        AssertTaggingKeepsExtraction(
            "BT /F1 12 Tf 72 700 Td (District Name: RIVERSIDE) Tj 0.5 0 Td (District Name: RIVERSIDE) Tj ET\n" +
            "BT /F1 12 Tf 72 680 Td (Section I - Page 2) Tj -0.5 0 Td (Section I - Page 2) Tj ET\n");
    }

    [Fact]
    public void LettersSpacedAndEachDrawnTwice()
    {
        string s = string.Empty;
        const string word = "TEXAS EDUCATION";
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < word.Length; i++)
            {
                if (word[i] != ' ')
                {
                    s += T(72 + i * 9 + pass * 0.4, 700, word[i].ToString());
                }
            }
        }

        AssertTaggingKeepsExtraction(s + T(72, 650, "Paragraph below"));
    }

    [Fact]
    public void TwoRowsPrintedOverEachOtherOnOneBaseline()
    {
        AssertTaggingKeepsExtraction(
            T(72, 700, "88.0%    40.0%  12/17/12") + T(72, 700.2, "Eng Lang Arts 2012    93%      93%") +
            T(300, 700, "Indicates that t") + T(250, 700, "Pry Sprn dinOc") + T(72, 600, "Para after"));
    }

    // The same text shown in one operator or in pieces gives matrices that differ in the last bits; positions compared for ties must
    // not notice (the cause of reordered overlapping text after tagging).
    [Fact]
    public void SplitOperatorsGiveTheSameGlyphPositions()
    {
        const string line = "Reading: 95 percent proficient";
        string whole = "0.1 0 0 0.1 0 0 cm\nBT /F1 120 Tf 1 0 0 1 723.3 6990.7 Tm (" + line + ") Tj ET\n";
        string pieces = "0.1 0 0 0.1 0 0 cm\nBT /F1 120 Tf 1 0 0 1 723.3 6990.7 Tm (Reading: ) Tj (95 ) Tj (percent) Tj ( proficient) Tj ET\n";
        using PdfDocument a = PdfDocument.Open(Make(whole));
        using PdfDocument b = PdfDocument.Open(Make(pieces));
        var opts = new OnePdf.Text.PdfTextOptions { RetainGlyphs = true, UseStructureTree = false };
        var ga = a.ExtractText(opts)[0].Glyphs;
        var gb = b.ExtractText(opts)[0].Glyphs;
        Assert.Equal(ga.Count, gb.Count);
        for (int i = 0; i < ga.Count; i++)
        {
            Assert.Equal(ga[i].Matrix, gb[i].Matrix);
        }
    }
}
