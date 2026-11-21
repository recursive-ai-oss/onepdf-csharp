using OnePdf.Layout.Barcodes;

namespace OnePdf.Layout.Tests;

/// <summary>Barcodes as layout elements: sizing, tagging, errors, text and determinism. Decoding is in <see cref="BarcodeDecodeTests"/>.</summary>
public class BarcodeLayoutTests
{
    private static LayoutDocument Labels(bool tagged = true) => LayoutDocument.Compose(d =>
    {
        d.Title("Labels").Language("en").Tagged(tagged);
        d.Page(p =>
        {
            p.Size(400, 600);
            p.Margin(20);
            p.Content().Column(c =>
            {
                c.Spacing(8);
                c.Item().Text("Parcel 2041").Bold();
                c.Item().QrCode("https://example.com/parcel/2041").ModuleSize(2);
                c.Item().Barcode(BarcodeSymbology.Code128, "SHIP-2041").AltText("Shipping reference SHIP-2041").HumanReadableText();
                c.Item().Barcode(BarcodeSymbology.Ean13, "400638133393");
                c.Item().Barcode(BarcodeSymbology.UpcA, "03600029145");
                c.Item().Barcode(BarcodeSymbology.Code39, "INV-2026").Code39CheckDigit().HumanReadableText();
                c.Item().Barcode(BarcodeSymbology.QrCode, "decoration").Artifact();
            });
        });
    });

    private static byte[] SaveUa(LayoutDocument layout)
    {
        using var ms = new MemoryStream();
        layout.Save(ms, new PdfSaveOptions { DocumentId = new byte[16], PdfUA = new PdfUAOptions() });
        return ms.ToArray();
    }

    [Fact]
    public void TaggedBarcodesAreFiguresWhoseAltTextIsTheValueUnlessSet()
    {
        byte[] pdf = Support.Save(Labels());
        using PdfDocument doc = PdfDocument.Open(pdf);
        List<string?> alts = [.. TagSupport.ReadTree(doc).Descendants().Where(n => n.Type == "Figure").Select(n => n.Alt)];
        Assert.Equal(
            ["https://example.com/parcel/2041", "Shipping reference SHIP-2041", "400638133393", "03600029145", "INV-2026"],
            alts);
        Assert.Empty(TagSupport.Untagged(doc));
    }

    [Fact]
    public void HumanReadableTextIsRealTextAndDecoration()
    {
        byte[] pdf = Support.Save(Labels());
        using PdfDocument doc = PdfDocument.Open(pdf);
        string text = string.Concat(doc.ExtractText().Select(t => t.Text)).Replace(" ", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal).Replace("\t", string.Empty, StringComparison.Ordinal);

        // EAN-13 digits in the standard layout: the first outside the bars, then 6 and 6, check digit included.
        Assert.Contains("4006381333931", text, StringComparison.Ordinal);
        Assert.Contains("036000291452", text, StringComparison.Ordinal);
        Assert.Contains("SHIP-2041", text, StringComparison.Ordinal);
        Assert.Contains("INV-2026", text, StringComparison.Ordinal);

        // The text is not a structure element of its own; the figure carries the value.
        Assert.DoesNotContain(TagSupport.ReadTree(doc).Descendants(), n => n.Type == "P" && n.Content.Count > 0 && n.Parent?.Type == "Figure");
    }

    [Fact]
    public void UntaggedOutputHasNoStructure()
    {
        byte[] pdf = Support.Save(Labels(false));
        using PdfDocument doc = PdfDocument.Open(pdf);
        Assert.Null(doc.Catalog.GetDictionary("StructTreeRoot"));
        Assert.True(pdf.Length > 0);
    }

    [Fact]
    public void BarcodesPassVeraPdfUA1()
    {
        byte[] pdf = SaveUa(Labels());
        Assert.Empty(TagSupport.Vera(pdf, "ua1"));
    }

    [Fact]
    public void OutputIsDeterministic() => Assert.Equal(Support.Save(Labels()), Support.Save(Labels()));

    [Fact]
    public void InvalidValuesFailWithATypedErrorWhenLaidOut()
    {
        static void Lay(BarcodeSymbology sym, string value, Action<BarcodeDescriptor>? tune = null)
        {
            LayoutDocument layout = LayoutDocument.Compose(d => d.Page(p =>
            {
                p.Size(400, 400);
                BarcodeDescriptor b = p.Content().Barcode(sym, value);
                tune?.Invoke(b);
            }));
            Support.Save(layout);
        }

        PdfBarcodeException wrongCheck = Assert.Throws<PdfBarcodeException>(() => Lay(BarcodeSymbology.Ean13, "4006381333932"));
        Assert.Contains("check digit", wrongCheck.Message, StringComparison.Ordinal);
        Assert.Throws<PdfBarcodeException>(() => Lay(BarcodeSymbology.Code128, "naïve \u20ac"));
        Assert.Throws<PdfBarcodeException>(() => Lay(BarcodeSymbology.Code39, "lowercase"));
        Assert.Throws<PdfBarcodeException>(() => Lay(BarcodeSymbology.UpcA, "1234"));
        Assert.Throws<PdfBarcodeException>(() => Lay(BarcodeSymbology.QrCode, new string('x', 3000), b => b.ErrorCorrection(QrErrorCorrection.Low)));
        Assert.Throws<PdfBarcodeException>(() => Lay(BarcodeSymbology.QrCode, new string('x', 200), b => b.Version(3)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Lay(BarcodeSymbology.QrCode, "x", b => b.ModuleSize(0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Lay(BarcodeSymbology.QrCode, "x", b => b.Version(41)));
    }

    private static Placed Only(Action<Slot> content, out List<LayoutDiagnostic> notes, double width = 300, double height = 400)
    {
        LayoutDocument layout = LayoutDocument.Compose(d => d.Page(p =>
        {
            p.Size(width, height);
            p.Margin(0);
            content(p.Content());
        }));
        List<Placed> pages = Support.Pages(layout, out notes);
        return Support.Walk(pages[0]).First(n => n.Element is Barcode);
    }

    [Fact]
    public void SizeIsAWholeNumberOfModulesAndNeverRounded()
    {
        // Version 1 is 21 modules, plus a quiet zone of 4 on each side: 29 modules.
        Placed qr = Only(s => s.QrCode("A").Version(1).ModuleSize(2.5), out _);
        Assert.Equal(29 * 2.5, qr.Width, 9);
        Assert.Equal(qr.Width, qr.Height, 9);

        // Default module: 2 pt for QR, 1 pt for linear symbols; zero quiet zone is allowed.
        Assert.Equal(21 * 2.0, Only(s => s.QrCode("A").Version(1).QuietZone(0), out _).Width, 9);
        Placed ean = Only(s => s.Barcode(BarcodeSymbology.Ean13, "400638133393").HumanReadableText(false).QuietZone(0), out _);
        Assert.Equal(95, ean.Width, 9);
    }

    [Fact]
    public void ContainTakesTheLargestModuleThatFits()
    {
        Placed qr = Only(s => s.Width(150).Height(120).QrCode("contain").Version(2).Contain(), out List<LayoutDiagnostic> notes);

        // Version 2 is 25 modules, plus 8 for the quiet zone: 33. Height limits: 120 / 33.
        Assert.Equal(120, qr.Width, 6);
        Assert.Equal(120, qr.Height, 6);
        Assert.DoesNotContain(notes, n => n.Message.Contains("reduced", StringComparison.Ordinal));
    }

    [Fact]
    public void ASymbolTooWideForItsSpaceIsShrunkWithANote()
    {
        Placed code = Only(s => s.Width(120).Barcode(BarcodeSymbology.Code128, "A LONG REFERENCE 123456789").ModuleSize(2), out List<LayoutDiagnostic> notes);
        Assert.True(code.Width <= 120 + 1e-6, code.Width.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains(notes, n => n.Message.Contains("module size was reduced", StringComparison.Ordinal));
    }

    [Fact]
    public void LinearBarHeightIsSetOrFortyModules()
    {
        Placed a = Only(s => s.Barcode(BarcodeSymbology.Code128, "A").HumanReadableText(false).ModuleSize(1.5), out _);
        Assert.Equal(60, a.Height, 6);
        Placed b = Only(s => s.Barcode(BarcodeSymbology.Code128, "A").HumanReadableText(false).BarHeight(25), out _);
        Assert.Equal(25, b.Height, 6);

        // Too tall for the page: the usual layout error rather than a clipped symbol.
        Assert.Throws<PdfLayoutException>(() => Only(s => s.Barcode(BarcodeSymbology.Code128, "A").BarHeight(900), out _));
    }

    [Fact]
    public void QrRectanglesCoverExactlyTheDarkModules()
    {
        QrSymbol qr = QrEncoder.Encode("rectangles cover the dark modules exactly", QrErrorCorrection.Medium, 1, 40, QrCharacterSet.Utf8);
        var seen = new bool[qr.Size * qr.Size];
        foreach ((int x, int y, int w, int h) in Barcode.QrRectangles(qr))
        {
            for (int j = y; j < y + h; j++)
            {
                for (int i = x; i < x + w; i++)
                {
                    Assert.False(seen[(j * qr.Size) + i], "no module is covered twice");
                    seen[(j * qr.Size) + i] = true;
                }
            }
        }

        Assert.Equal(qr.Modules, seen);
    }

    [Fact]
    public void RetailDigitsShrinkToTheirPlaceRatherThanOverlap()
    {
        // At a 0.5 pt module the six digits under each half have 21 pt; 9 pt text would not fit, so the whole line shrinks.
        Placed ean = Only(s => s.Barcode(BarcodeSymbology.Ean13, "400638133393").ModuleSize(0.5), out _);
        double[] widths = [.. ean.Children.Select(c => c.Width)];
        Assert.Equal(3, widths.Length);
        Assert.All(ean.Children, c => Assert.True(c.X >= 0 && c.X + c.Width <= ean.Width + 1e-6));
    }

    [Fact]
    public void PageDefaultFontReachesTheBarcodeText()
    {
        LayoutDocument layout = LayoutDocument.Compose(d => d.Page(p =>
        {
            p.Size(300, 200);
            p.DefaultTextStyle(t => t.FontFamily("Serif"));
            p.Content().Barcode(BarcodeSymbology.Code128, "FONT-1").HumanReadableText();
        }));
        using PdfDocument back = PdfDocument.Open(Support.Save(layout));
        string text = string.Concat(back.ExtractText().Select(t => t.Text));
        Assert.Contains("FONT-1", text, StringComparison.Ordinal);
    }
}
