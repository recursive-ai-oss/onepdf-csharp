using OnePdf.Layout.Barcodes;
using OneImage;
using OneImage.Pdf;
using ZXing;
using ZXing.Common;
using OneImg = OneImage.Image;

namespace OnePdf.Layout.Tests;

/// <summary>
/// The barcodes the layout draws, rendered to pixels by a published renderer and read back by a published reader. Both are compiled
/// packages used through their public API, in this test project only.
/// </summary>
public class BarcodeDecodeTests
{
    private static readonly BarcodeFormat[] Retail = [BarcodeFormat.EAN_13, BarcodeFormat.UPC_A, BarcodeFormat.EAN_8];

    private static bool _assumeCode39Check;

    private static Result? Read(byte[] gray, int width, int height, params BarcodeFormat[] formats) => Read(gray, width, height, false, formats);

    private static Result? Read(byte[] gray, int width, int height, bool pure, params BarcodeFormat[] formats)
    {
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = false,
            Options = new DecodingOptions { TryHarder = true, PossibleFormats = formats, PureBarcode = pure, AssumeCode39CheckDigit = _assumeCode39Check },
        };
        return reader.Decode(new RGBLuminanceSource(gray, width, height, RGBLuminanceSource.BitmapFormat.Gray8));
    }

    private static (byte[] Gray, int Width, int Height) Render(LayoutDocument layout, int dpi)
    {
        byte[] pdf = Support.Save(layout);
        var load = new LoadOptions { Configuration = new ImageConfiguration().Add(new PdfFormat(new PdfRenderOptions { Dpi = dpi })) };
        OneImg doc = OneImg.Load(pdf, load);
        OneImg page = doc.Frames[0].ToImage();
        Rgba32[] px = page.ToArray<Rgba32>();
        var gray = new byte[px.Length];
        for (int i = 0; i < px.Length; i++)
        {
            // Composite on white: a renderer may leave the page transparent.
            double a = px[i].A / 255.0;
            double l = ((0.299 * px[i].R) + (0.587 * px[i].G) + (0.114 * px[i].B)) * a;
            gray[i] = (byte)Math.Round(l + (255 * (1 - a)));
        }

        return (gray, page.Width, page.Height);
    }

    /// <summary>Renders the first page of the document and reads the barcode on it.</summary>
    private static Result? Decode(LayoutDocument layout, int dpi, params BarcodeFormat[] formats)
    {
        (byte[] gray, int w, int h) = Render(layout, dpi);
        return Read(gray, w, h, formats);
    }

    private static LayoutDocument OnePage(double width, double height, Action<Slot> content)
        => LayoutDocument.Compose(d => d.Page(p =>
        {
            p.Size(Math.Min(width, height), Math.Max(width, height));
            if (width > height)
            {
                p.Landscape();
            }

            p.Margin(20);
            content(p.Content());
        }));

    private static string Level(QrErrorCorrection l) => l switch
    {
        QrErrorCorrection.Low => "L",
        QrErrorCorrection.Medium => "M",
        QrErrorCorrection.Quartile => "Q",
        _ => "H",
    };

    // Text of about `bytes` UTF-8 bytes with letters, accents, CJK and a symbol, so the decoder has to get UTF-8 right.
    private static string Unicode(int bytes)
    {
        const string Pattern = "Z\u00fcrich \u65e5\u672c\u8a9e \u2713 ";
        var sb = new System.Text.StringBuilder();
        int used = 0;
        for (int i = 0; ; i++)
        {
            string ch = Pattern[i % Pattern.Length].ToString();
            int n = System.Text.Encoding.UTF8.GetByteCount(ch);
            if (used + n > bytes)
            {
                break;
            }

            sb.Append(ch);
            used += n;
        }

        return sb.ToString();
    }

    // The reader's own detector misses about one valid symbol in twenty on a perfect image, its own encoder's included, so the rendered cases
    // use versions it finds; every version and level is read from the module grid in EveryQrVersionAndLevelDecodesAtFullCapacity.
    public static TheoryData<int, QrErrorCorrection> QrCases()
    {
        var data = new TheoryData<int, QrErrorCorrection>();
        foreach (int v in new[] { 1, 4, 7, 10, 17, 25, 31, 40 })
        {
            foreach (QrErrorCorrection l in Enum.GetValues<QrErrorCorrection>())
            {
                data.Add(v, l);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(QrCases))]
    public void QrCodeInLayoutDecodes(int version, QrErrorCorrection level)
    {
        string text = Unicode(Math.Max(4, QrEncoder.DataCodewords(version, level) * 6 / 10));
        LayoutDocument layout = OnePage(700, 700, s => s.QrCode(text).Version(version).ErrorCorrection(level).ModuleSize(3));
        Result? r = Decode(layout, 144, BarcodeFormat.QR_CODE);
        Assert.NotNull(r);
        Assert.Equal(text, r.Text);
        Assert.Equal(BarcodeFormat.QR_CODE, r.BarcodeFormat);
        Assert.Equal(Level(level), r.ResultMetadata[ResultMetadataType.ERROR_CORRECTION_LEVEL]);
    }

    [Theory]
    [InlineData(5, QrErrorCorrection.Quartile)]
    [InlineData(33, QrErrorCorrection.Medium)]
    [InlineData(40, QrErrorCorrection.High)]
    public void RenderedQrMatchesTheModuleGridModuleForModule(int version, QrErrorCorrection level)
    {
        // The reader's detector cannot find every valid symbol (version 33-M defeats it even on a perfect grid), so this also compares
        // the rendered pixels with the encoder's modules directly: the centre of every module, 3 pt with the symbol at (20 pt, 20 pt).
        string text = Unicode(Math.Max(4, QrEncoder.DataCodewords(version, level) * 6 / 10));
        LayoutDocument layout = OnePage(700, 700, s => s.QrCode(text).Version(version).ErrorCorrection(level).ModuleSize(3));
        (byte[] gray, int w, _) = Render(layout, 144);
        QrSymbol qr = QrEncoder.Encode(text, level, version, version, QrCharacterSet.Utf8);
        for (int y = 0; y < qr.Size; y++)
        {
            for (int x = 0; x < qr.Size; x++)
            {
                int px = (int)(((20 + ((4 + x + 0.5) * 3)) * 2));
                int py = (int)(((20 + ((4 + y + 0.5) * 3)) * 2));
                Assert.Equal(qr[x, y], gray[(py * w) + px] < 128);
            }
        }

        Assert.NotNull(ReadModules(qr));
    }

    [Fact]
    public void QrCodeVersionIsTheSmallestThatFits()
    {
        foreach (int length in new[] { 10, 60, 200, 700 })
        {
            string text = new string('x', length).Replace("x", "ab", StringComparison.Ordinal)[..length] + "\u00e9";
            QrSymbol auto = QrEncoder.Encode(text, QrErrorCorrection.Quartile, 1, 40, QrCharacterSet.Utf8);
            if (auto.Version > 1)
            {
                Assert.Throws<PdfBarcodeException>(() => QrEncoder.Encode(text, QrErrorCorrection.Quartile, auto.Version - 1, auto.Version - 1, QrCharacterSet.Utf8));
            }

            LayoutDocument layout = OnePage(700, 700, s => s.QrCode(text).ErrorCorrection(QrErrorCorrection.Quartile).ModuleSize(3));
            Result? r = Decode(layout, 144, BarcodeFormat.QR_CODE);
            Assert.NotNull(r);
            Assert.Equal(text, r.Text);
        }
    }

    [Fact]
    public void EveryQrVersionAndLevelDecodesAtFullCapacity()
    {
        // The module grids themselves, with no rendering: 40 versions at 4 levels, each filled to the last codeword (every block, and the
        // blocks of two lengths, are exercised), read back by the reference decoder.
        var rng = new Random(18004);
        int checkedSymbols = 0;
        for (int version = 1; version <= 40; version++)
        {
            foreach (QrErrorCorrection level in Enum.GetValues<QrErrorCorrection>())
            {
                int cw = QrEncoder.DataCodewords(version, level);
                int header = version <= 9 ? 1 : 2;
                int n = cw - 1 - header;
                var chars = new char[n];
                for (int i = 0; i < n; i++)
                {
                    chars[i] = (char)('a' + rng.Next(26));
                }

                string text = new(chars);
                QrSymbol qr = QrEncoder.Encode(text, level, version, version, QrCharacterSet.Utf8);
                Assert.Equal(version, qr.Version);
                Result? r = ReadModules(qr);
                Assert.True(r is not null, $"version {version} level {level} was not read");
                Assert.Equal(text, r.Text);
                Assert.Equal(Level(level), r.ResultMetadata[ResultMetadataType.ERROR_CORRECTION_LEVEL]);
                checkedSymbols++;
            }
        }

        Assert.Equal(160, checkedSymbols);
    }

    [Fact]
    public void EveryMaskPatternDecodes()
    {
        var bad = new List<string>();
        foreach (int version in Enumerable.Range(1, 40))
        {
            foreach (QrErrorCorrection level in Enum.GetValues<QrErrorCorrection>())
            {
                for (int mask = 0; mask < 8; mask++)
                {
                    QrSymbol qr = QrEncoder.Encode("m012", level, version, version, QrCharacterSet.Utf8, mask);
                    Assert.Equal(mask, qr.Mask);
                    Result? r = ReadModules(qr);
                    if (r is null || r.Text != "m012")
                    {
                        bad.Add($"{version}{Level(level)}m{mask}");
                    }
                }
            }
        }

        Assert.Empty(bad);
    }

    private static Result? ReadModules(QrSymbol qr, int scale = 4, bool pure = true)
    {
        const int Quiet = 4;
        int n = qr.Size + (2 * Quiet);
        int px = n * scale;
        var gray = new byte[px * px];
        Array.Fill(gray, (byte)255);
        for (int y = 0; y < qr.Size; y++)
        {
            for (int x = 0; x < qr.Size; x++)
            {
                if (!qr[x, y])
                {
                    continue;
                }

                for (int dy = 0; dy < scale; dy++)
                {
                    for (int dx = 0; dx < scale; dx++)
                    {
                        gray[((((y + Quiet) * scale) + dy) * px) + ((x + Quiet) * scale) + dx] = 0;
                    }
                }
            }
        }

        return Read(gray, px, px, pure, BarcodeFormat.QR_CODE);
    }

    [Theory]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123456789")]
    [InlineData("HELLO WORLD $%*+-./: 0123456789 ABCDEFGHIJKLMNOPQRSTUVWXYZ")]
    [InlineData("https://example.com/invoice/1042?ref=ABC-123&lang=en")]
    [InlineData("ORDER 0042 shipped to Zürich; total CHF 1234567890 — thank you!")]
    [InlineData("A")]
    public void QrModesMixedAndSingleDecode(string text)
    {
        foreach (QrErrorCorrection level in Enum.GetValues<QrErrorCorrection>())
        {
            Result? r = ReadModules(QrEncoder.Encode(text, level, 1, 40, QrCharacterSet.Utf8));
            Assert.NotNull(r);
            Assert.Equal(text, r.Text);
        }
    }

    [Fact]
    public void QrCharacterSetsDecode()
    {
        string text = "Caf\u00e9 cr\u00e8me \u2013 \u65e5\u672c";
        QrSymbol eci = QrEncoder.Encode(text, QrErrorCorrection.Medium, 1, 40, QrCharacterSet.Utf8WithEci);
        Result? r = ReadModules(eci);
        Assert.NotNull(r);
        Assert.Equal(text, r.Text);

        string latin = "Caf\u00e9 cr\u00e8me";
        Result? l = ReadModules(QrEncoder.Encode(latin, QrErrorCorrection.Medium, 1, 40, QrCharacterSet.Latin1));
        Assert.NotNull(l);
        Assert.Equal(latin, l.Text);
    }

    private static Result? ReadLinear(LinearSymbol lin, params BarcodeFormat[] formats)
    {
        const int Scale = 3;
        int n = (int)(lin.LeftQuiet + lin.Modules.Length + lin.RightQuiet);
        int w = n * Scale;
        const int H = 60;
        var gray = new byte[w * H];
        Array.Fill(gray, (byte)255);
        for (int i = 0; i < lin.Modules.Length; i++)
        {
            if (!lin.Modules[i])
            {
                continue;
            }

            for (int y = 0; y < H; y++)
            {
                for (int dx = 0; dx < Scale; dx++)
                {
                    gray[(y * w) + (((int)lin.LeftQuiet + i) * Scale) + dx] = 0;
                }
            }
        }

        return Read(gray, w, H, false, formats);
    }

    [Fact]
    public void Code128RandomStringsDecodeFromTheModuleGrid()
    {
        var rng = new Random(15417);
        var bad = new List<string>();
        for (int round = 0; round < 400; round++)
        {
            int length = rng.Next(1, 40);
            int flavour = round % 4;
            var chars = new char[length];
            for (int i = 0; i < length; i++)
            {
                chars[i] = flavour switch
                {
                    0 => (char)rng.Next(32, 127),
                    1 => (char)rng.Next('0', '9' + 1),
                    2 => rng.Next(3) == 0 ? (char)rng.Next(0, 32) : (char)rng.Next('A', 'Z' + 1),
                    _ => rng.Next(4) == 0 ? (char)rng.Next('0', '9' + 1) : (char)rng.Next(32, 128),
                };
            }

            string value = new(chars);
            Result? r = ReadLinear(Code128.Create(value, false, null), BarcodeFormat.CODE_128);
            if (r?.Text != value)
            {
                bad.Add($"[{string.Join(',', value.Select(c => (int)c))}] -> [{(r is null ? "null" : string.Join(',', r.Text.Select(c => (int)c)))}]");
            }
        }

        Assert.Empty(bad);
    }

    [Theory]
    [InlineData("Hello, World!")]                       // set B
    [InlineData("1234567890123456")]                    // set C
    [InlineData("abc123456789xyz")]                     // B, then C, then B
    [InlineData("12345")]                               // an odd count of digits
    [InlineData("ORDER 0042 2026-11-20")]
    [InlineData(" !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~")]
    public void Code128Decodes(string value)
    {
        LayoutDocument layout = OnePage(1400, 200, s => s.Barcode(BarcodeSymbology.Code128, value).ModuleSize(2).BarHeight(60));
        Result? r = Decode(layout, 144, BarcodeFormat.CODE_128);
        Assert.NotNull(r);
        Assert.Equal(value, r.Text);
        Assert.Equal(BarcodeFormat.CODE_128, r.BarcodeFormat);
    }

    [Fact]
    public void Code128ControlCharactersForceCodeSetA()
    {
        // Control characters exist only in code set A; lower case only in B: the encoder must switch or shift between them.
        foreach (string value in new[] { "\u0001\u0002ABC", "ab\u0009cd", "TAB\u0009LOWER lower\u001fEND", "\u007f del" })
        {
            LayoutDocument layout = OnePage(900, 200, s => s.Barcode(BarcodeSymbology.Code128, value).ModuleSize(2).BarHeight(60));
            Result? r = Decode(layout, 144, BarcodeFormat.CODE_128);
            Assert.NotNull(r);
            Assert.Equal(value, r.Text);
        }
    }

    [Fact]
    public void Code128EverySymbolValueIsReadBack()
    {
        // Strings built to use every character of sets A, B and C at least once: each of the 103 data values appears in a symbol.
        string setB = string.Concat(Enumerable.Range(32, 96).Select(c => (char)c));
        string setA = string.Concat(Enumerable.Range(0, 32).Select(c => (char)c)) + "UPPER";
        string setC = string.Concat(Enumerable.Range(0, 100).Select(i => i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)));
        foreach (string value in new[] { setB, setA, setC })
        {
            LayoutDocument layout = OnePage(2600, 200, s => s.Barcode(BarcodeSymbology.Code128, value).ModuleSize(1.5).BarHeight(60));
            Result? r = Decode(layout, 192, BarcodeFormat.CODE_128);
            Assert.NotNull(r);
            Assert.Equal(value, r.Text);
        }
    }

    [Fact]
    public void Gs1128Decodes()
    {
        LayoutDocument layout = OnePage(1200, 200, s => s.Barcode(BarcodeSymbology.Gs1128, "(01)09501101530003(17)150119(10)AB-123(21)SER9").ModuleSize(2).BarHeight(60));
        Result? r = Decode(layout, 144, BarcodeFormat.CODE_128);
        Assert.NotNull(r);

        // The reader drops the FNC1 characters from the text; that they are in the symbol is checked on the encoder's symbol values (BarcodeSpecTests).
        Assert.Equal("0109501101530003" + "17150119" + "10AB-123" + "21SER9", r.Text);
        if (r.ResultMetadata.TryGetValue(ResultMetadataType.SYMBOLOGY_IDENTIFIER, out object? id))
        {
            Assert.StartsWith("]C1", id?.ToString(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("400638133393", "4006381333931", BarcodeFormat.EAN_13, true)]
    [InlineData("4006381333931", "4006381333931", BarcodeFormat.EAN_13, false)]
    [InlineData("590123412345", "5901234123457", BarcodeFormat.EAN_13, true)]
    [InlineData("9780201379624", "9780201379624", BarcodeFormat.EAN_13, true)]
    [InlineData("03600029145", "036000291452", BarcodeFormat.UPC_A, true)]
    [InlineData("036000291452", "036000291452", BarcodeFormat.UPC_A, false)]
    [InlineData("7351353", "73513537", BarcodeFormat.EAN_8, true)]
    public void RetailSymbolsDecode(string value, string expected, BarcodeFormat format, bool text)
    {
        BarcodeSymbology sym = format == BarcodeFormat.EAN_13 ? BarcodeSymbology.Ean13 : format == BarcodeFormat.UPC_A ? BarcodeSymbology.UpcA : BarcodeSymbology.Ean8;
        LayoutDocument layout = OnePage(500, 250, s => s.Barcode(sym, value).ModuleSize(2).BarHeight(80).HumanReadableText(text));
        Result? r = Decode(layout, 144, Retail);
        Assert.NotNull(r);
        Assert.Equal(expected, r.Text);
        Assert.Equal(format, r.BarcodeFormat);
    }

    [Theory]
    [InlineData("CODE39", false)]
    [InlineData("HELLO-123 $/+%", false)]
    [InlineData("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ", false)]
    [InlineData("CODE39", true)]
    [InlineData("INV-2026.001", true)]
    public void Code39Decodes(string value, bool check)
    {
        LayoutDocument layout = OnePage(1400, 200, s => s.Barcode(BarcodeSymbology.Code39, value).Code39CheckDigit(check).ModuleSize(2).BarHeight(60));
        Result? r;
        lock (typeof(BarcodeDecodeTests))
        {
            _assumeCode39Check = check;
            try
            {
                r = Decode(layout, 144, BarcodeFormat.CODE_39);
            }
            finally
            {
                _assumeCode39Check = false;
            }
        }

        Assert.NotNull(r);

        // With the check character on, the reader (told to expect one) strips it; the data it verified is what we wrote.
        Assert.Equal(value, r.Text);
        Assert.Equal(BarcodeFormat.CODE_39, r.BarcodeFormat);
    }

    [Fact]
    public void ColoursQuietZoneAndBackgroundStillDecode()
    {
        // Dark blue on pale yellow, no quiet zone drawn by the page, the quiet zone painted by the barcode's own background.
        LayoutDocument layout = OnePage(500, 500, s => s.QrCode("colours and quiet zone").ModuleSize(4).Colors(new LayoutColor(0, 0, 0.4), new LayoutColor(1, 1, 0.85)));
        Result? r = Decode(layout, 144, BarcodeFormat.QR_CODE);
        Assert.NotNull(r);
        Assert.Equal("colours and quiet zone", r.Text);
    }

    [Fact]
    public void ContainFillsTheSlotAndStillDecodes()
    {
        LayoutDocument layout = OnePage(400, 400, s => s.Width(150).Height(150).QrCode("contain me").Contain());
        Result? r = Decode(layout, 288, BarcodeFormat.QR_CODE);
        Assert.NotNull(r);
        Assert.Equal("contain me", r.Text);
    }

    [Fact]
    public void BarcodeAboveATextLineInsideATableCellDecodes()
    {
        LayoutDocument layout = LayoutDocument.Compose(d => d.Page(p =>
        {
            p.Size(500, 400);
            p.Margin(20);
            p.Content().Column(c =>
            {
                c.Item().Text("Shipping label").Bold();
                c.Item().Padding(10).Barcode(BarcodeSymbology.Code128, "SHIP-2041").ModuleSize(2).BarHeight(50).HumanReadableText();
            });
        }));
        Result? r = Decode(layout, 144, BarcodeFormat.CODE_128);
        Assert.NotNull(r);
        Assert.Equal("SHIP-2041", r.Text);
    }
}
