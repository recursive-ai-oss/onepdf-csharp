using OnePdf.Layout.Barcodes;

namespace OnePdf.Layout.Tests;

/// <summary>The encoders against the numbers the standards publish, with no reader involved.</summary>
public class BarcodeSpecTests
{
    [Theory]
    [InlineData("400638133393", 1)]
    [InlineData("03600029145", 2)]
    [InlineData("01234567890", 5)]
    [InlineData("7351353", 7)]
    [InlineData("629104150021", 3)]
    public void RetailCheckDigitsMatchThePublishedExamples(string digits, int check)
        => Assert.Equal(check, Retail.CheckDigit(digits));

    [Fact]
    public void Ean13AddsAndVerifiesTheCheckDigit()
    {
        LinearSymbol added = Retail.Ean13("400638133393", null, false, false);
        LinearSymbol given = Retail.Ean13("4006381333931", null, false, false);
        Assert.Equal(added.Modules, given.Modules);
        Assert.Equal("4006381333931", added.Data);
        Assert.Equal(95, added.Modules.Length);
        PdfBarcodeException wrong = Assert.Throws<PdfBarcodeException>(() => Retail.Ean13("4006381333932", null, false, false));
        Assert.Contains("check digit", wrong.Message, StringComparison.Ordinal);
        Assert.Equal(BarcodeSymbology.Ean13, wrong.Symbology);
        Assert.Throws<PdfBarcodeException>(() => Retail.Ean13("40063813339", null, false, false));
        Assert.Throws<PdfBarcodeException>(() => Retail.Ean13("40063813339A", null, false, false));
    }

    [Fact]
    public void UpcAIsAnEan13WithALeadingZero()
    {
        LinearSymbol upc = Retail.Ean13("03600029145", null, false, true);
        LinearSymbol ean = Retail.Ean13("0036000291452", null, false, false);
        Assert.Equal(ean.Modules, upc.Modules);
        Assert.Equal("036000291452", upc.Data);
        Assert.Equal(9, upc.LeftQuiet);
        Assert.Throws<PdfBarcodeException>(() => Retail.Ean13("036000291453", null, false, true));
    }

    [Fact]
    public void Ean13StructureIsGuardsAndTwoHalves()
    {
        // ISO/IEC 15420: 101, six digits, 01010, six digits, 101 = 95 modules. 4006381333931 starts with 4, so the left half is
        // coded L G L L G G: the first left digit (0) in set L, the second (0) in set G.
        string bits = string.Concat(Retail.Ean13("4006381333931", null, false, false).Modules.Select(m => m ? '1' : '0'));
        Assert.StartsWith("101", bits, StringComparison.Ordinal);
        Assert.EndsWith("101", bits, StringComparison.Ordinal);
        Assert.Equal("01010", bits.Substring(45, 5));
        Assert.Equal("0001101", bits.Substring(3, 7));
        Assert.Equal("0100111", bits.Substring(10, 7));

        // The check digit 1 on the right is the complement of set L: 1100110.
        Assert.Equal("1100110", bits.Substring(85, 7));
    }

    [Fact]
    public void Ean8CheckDigitAndLength()
    {
        LinearSymbol e = Retail.Ean8("7351353", null, true);
        Assert.Equal("73513537", e.Data);
        Assert.Equal(67, e.Modules.Length);
        Assert.Equal(2, e.Groups!.Length);
    }

    [Fact]
    public void Code128SymbolPatternsAreWellFormed()
    {
        var seen = new HashSet<string>();
        for (int v = 0; v <= 106; v++)
        {
            string p = Code128.Pattern(v);
            Assert.Equal(v == 106 ? 7 : 6, p.Length);
            Assert.Equal(v == 106 ? 13 : 11, p.Sum(c => c - '0'));
            Assert.All(p, c => Assert.InRange(c - '0', 1, 4));
            Assert.True(seen.Add(p), $"duplicate pattern for {v}");
        }
    }

    [Fact]
    public void Code128CheckCharacterIsTheWeightedSumModulo103()
    {
        // Code set B: start B (104), 'a' = 65; check = (104 + 1 * 65) mod 103 = 66.
        Assert.Equal([104, 65, 66], Code128.Encode(['a']));

        // Code set C: start C (105), pairs 12 34 56; check = (105 + 12 + 2 * 34 + 3 * 56) mod 103 = 353 mod 103 = 44.
        Assert.Equal([105, 12, 34, 56, 44], Code128.Encode(['1', '2', '3', '4', '5', '6']));
    }

    [Fact]
    public void Code128PicksTheShortestSwitching()
    {
        static int Count(string s) => Code128.Encode([.. s.Select(c => (int)c)]).Length;

        Assert.Equal(1 + 2 + 1, Count("1234"));          // start C, two pairs, check
        Assert.Equal(1 + 1 + 2 + 1 + 1, Count("12345")); // start C, two pairs, code B, 5, check: six, not seven
        Assert.Equal(1 + 1 + 1 + 2 + 1, Count("A1234")); // start B, A, code C, two pairs, check
        Assert.Equal(1 + 11 + 1, Count("Hello World"));  // all code set B
        Assert.Equal(8, Count("AB\u0001cd"));            // a control character among lower case costs one shift or one switch, never more
    }

    [Fact]
    public void Code128UsesFnc1ForGs1()
    {
        LinearSymbol s = Code128.Create("(01)09501101530003(17)150119(10)AB-123", true, null);
        Assert.Equal("(01)09501101530003(17)150119(10)AB-123", s.Text);
        Assert.Throws<PdfBarcodeException>(() => Code128.Create("(01)123(10)A", true, null));
        Assert.Throws<PdfBarcodeException>(() => Code128.Create("0109501101530003", true, null));
    }

    [Fact]
    public void Gs1TokensCarryFnc1AtTheStartAndAfterVariableLengthData()
    {
        int[] t = Code128.ParseGs1("(01)09501101530003(17)150119(10)AB-123(21)SER9", out string shown);
        Assert.Equal("(01)09501101530003(17)150119(10)AB-123(21)SER9", shown);
        Assert.Equal(-1, t[0]);
        Assert.Equal(2, t.Count(x => x < 0));

        // 1 for the leading FNC1, 16 for (01) and its 14 digits, 8 for (17) and 6, 8 for (10) and AB-123: the separator follows the lot number.
        Assert.Equal(-1, t[33]);
    }

    [Fact]
    public void Code128RejectsWhatItCannotEncode()
    {
        PdfBarcodeException e = Assert.Throws<PdfBarcodeException>(() => Code128.Create("caf\u00e9", false, null));
        Assert.Contains("position 3", e.Message, StringComparison.Ordinal);
        Assert.Throws<PdfBarcodeException>(() => Code128.Create("", false, null));
    }

    [Fact]
    public void Code39PatternsHaveThreeWideElements()
    {
        const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%";
        var seen = new HashSet<string>();
        foreach (char c in alphabet)
        {
            string p = Code39.PatternOf(c);
            Assert.Equal(9, p.Length);
            Assert.Equal(3, p.Count(w => w == 'W'));
            Assert.True(seen.Add(p));
        }
    }

    [Fact]
    public void Code39CheckCharacterIsModulo43()
    {
        // The sum of CODE39 is 12 + 24 + 13 + 14 + 3 + 9 = 75, and 75 mod 43 = 32, which is W.
        Assert.Equal("CODE39W", Code39.Create("CODE39", true, null).Data);
        Assert.Equal("CODE39", Code39.Create("CODE39", false, null).Data);
        PdfBarcodeException e = Assert.Throws<PdfBarcodeException>(() => Code39.Create("code39", false, null));
        Assert.Contains("character set", e.Message, StringComparison.Ordinal);
        Assert.Throws<PdfBarcodeException>(() => Code39.Create("A*B", false, null));
    }

    [Fact]
    public void QrWorkedExampleOfTheStandard()
    {
        // ISO/IEC 18004 Annex I: "01234567" in version 1, level M.
        (int version, byte[] data) = QrEncoder.Prepare("01234567", QrErrorCorrection.Medium, 1, 1, QrCharacterSet.Utf8);
        Assert.Equal(1, version);
        Assert.Equal(16, data.Length);
        Assert.Equal("1020" + "0C56" + "6180" + "EC11EC11EC11EC11EC11", Convert.ToHexString(data));
        Assert.Equal("A524D4C1ED36C7872C55", Convert.ToHexString(QrEncoder.ReedSolomon(data, 10)));
    }

    [Theory]
    [InlineData(QrErrorCorrection.Low, 0, "111011111000100")]
    [InlineData(QrErrorCorrection.Low, 4, "110011000101111")]
    [InlineData(QrErrorCorrection.Low, 7, "110100101110110")]
    [InlineData(QrErrorCorrection.Medium, 0, "101010000010010")]
    [InlineData(QrErrorCorrection.Medium, 2, "101111001111100")]
    [InlineData(QrErrorCorrection.Medium, 5, "100000011001110")]
    [InlineData(QrErrorCorrection.Quartile, 0, "011010101011111")]
    public void QrFormatInformationBits(QrErrorCorrection level, int mask, string expected)
        => Assert.Equal(expected, Convert.ToString(QrEncoder.FormatBits(level, mask), 2).PadLeft(15, '0'));

    [Theory]
    [InlineData(1, 19, 16, 13, 9)]
    [InlineData(7, 156, 124, 88, 66)]
    [InlineData(10, 274, 216, 154, 122)]
    [InlineData(40, 2956, 2334, 1666, 1276)]
    public void QrDataCapacitiesMatchTable7(int version, int l, int m, int q, int h)
    {
        Assert.Equal(l, QrEncoder.DataCodewords(version, QrErrorCorrection.Low));
        Assert.Equal(m, QrEncoder.DataCodewords(version, QrErrorCorrection.Medium));
        Assert.Equal(q, QrEncoder.DataCodewords(version, QrErrorCorrection.Quartile));
        Assert.Equal(h, QrEncoder.DataCodewords(version, QrErrorCorrection.High));
    }

    [Fact]
    public void QrRawModuleCounts()
    {
        Assert.Equal(208, QrEncoder.RawModules(1));
        Assert.Equal(359, QrEncoder.RawModules(2));
        Assert.Equal(1568, QrEncoder.RawModules(7));
        Assert.Equal(29648, QrEncoder.RawModules(40));
    }

    [Fact]
    public void QrAlignmentPatternPositions()
    {
        Assert.Equal([6, 18], QrEncoder.AlignmentPositions(2));
        Assert.Equal([6, 22, 38], QrEncoder.AlignmentPositions(7));
        Assert.Equal([6, 34, 60, 86, 112, 138], QrEncoder.AlignmentPositions(32));
        Assert.Equal([6, 24, 50, 76, 102, 128, 154], QrEncoder.AlignmentPositions(36));
        Assert.Equal([6, 30, 58, 86, 114, 142, 170], QrEncoder.AlignmentPositions(40));
    }

    [Fact]
    public void QrPicksTheSmallestVersionAtTheModeBoundaries()
    {
        static int V(string s) => QrEncoder.Encode(s, QrErrorCorrection.Low, 1, 40, QrCharacterSet.Utf8).Version;

        // Version 1-L holds 17 bytes, 25 alphanumeric characters or 41 digits (Table 7); one more needs version 2.
        Assert.Equal(1, V(new string('a', 17)));
        Assert.Equal(2, V(new string('a', 18)));
        Assert.Equal(1, V(new string('Q', 25)));
        Assert.Equal(2, V(new string('Q', 26)));
        Assert.Equal(1, V(new string('7', 41)));
        Assert.Equal(2, V(new string('7', 42)));

        // A minimum version is honoured, and the symbol has the size of its version.
        QrSymbol big = QrEncoder.Encode("x", QrErrorCorrection.Low, 12, 40, QrCharacterSet.Utf8);
        Assert.Equal(12, big.Version);
        Assert.Equal(17 + (4 * 12), big.Size);
    }

    [Fact]
    public void QrTooMuchDataIsATypedError()
    {
        PdfBarcodeException e = Assert.Throws<PdfBarcodeException>(() => QrEncoder.Encode(new string('x', 1277), QrErrorCorrection.High, 1, 40, QrCharacterSet.Utf8));
        Assert.Equal(BarcodeSymbology.QrCode, e.Symbology);
        Assert.Contains("version 40", e.Message, StringComparison.Ordinal);
        Assert.Equal(40, QrEncoder.Encode(new string('x', 1273), QrErrorCorrection.High, 1, 40, QrCharacterSet.Utf8).Version);
        Assert.Throws<PdfBarcodeException>(() => QrEncoder.Encode("x\uD800", QrErrorCorrection.Low, 1, 40, QrCharacterSet.Utf8));
        Assert.Throws<PdfBarcodeException>(() => QrEncoder.Encode("\u4e2d", QrErrorCorrection.Low, 1, 40, QrCharacterSet.Latin1));
        Assert.Throws<PdfBarcodeException>(() => QrEncoder.Encode(string.Empty, QrErrorCorrection.Low, 1, 40, QrCharacterSet.Utf8));
        Assert.Throws<PdfBarcodeException>(() => QrEncoder.Encode("abc", QrErrorCorrection.Low, 5, 3, QrCharacterSet.Utf8));
    }

    [Fact]
    public void QrFunctionPatternsAreInPlace()
    {
        QrSymbol s = QrEncoder.Encode("function patterns", QrErrorCorrection.Quartile, 7, 7, QrCharacterSet.Utf8);
        int n = s.Size;
        Assert.Equal(45, n);

        // The three finder patterns, the timing patterns and the dark module.
        foreach ((int ox, int oy) in new[] { (0, 0), (n - 7, 0), (0, n - 7) })
        {
            for (int y = 0; y < 7; y++)
            {
                for (int x = 0; x < 7; x++)
                {
                    bool edge = x is 0 or 6 || y is 0 or 6;
                    bool core = x is >= 2 and <= 4 && y is >= 2 and <= 4;
                    Assert.Equal(edge || core, s[ox + x, oy + y]);
                }
            }
        }

        for (int i = 8; i < n - 8; i++)
        {
            Assert.Equal(i % 2 == 0, s[i, 6]);
            Assert.Equal(i % 2 == 0, s[6, i]);
        }

        Assert.True(s[8, n - 8]);
    }
}
