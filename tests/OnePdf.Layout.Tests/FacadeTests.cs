using System.Buffers.Binary;
using System.IO.Compression;

namespace OnePdf.Layout.Tests;

/// <summary>The fluent facade builds the same element trees as hand-written element objects, and every feature group is reachable through it.</summary>
public class FacadeTests
{
    private static string AllText(PdfDocument doc) =>
        Support.Normalize(string.Join(" ", doc.ExtractText().Select(t => t.Text)));

    private static PdfDocument Open(byte[] pdf) => PdfDocument.Open(pdf);

    private static byte[] Save(LayoutDocument doc) => Support.Save(doc);

    internal static byte[] TinyPng(int size = 8, bool alpha = false)
    {
        int channels = alpha ? 4 : 3;
        var raw = new byte[(1 + (size * channels)) * size];
        for (int y = 0; y < size; y++)
        {
            int row = y * (1 + (size * channels));
            for (int x = 0; x < size; x++)
            {
                raw[row + 1 + (x * channels)] = (byte)(x * 255 / size);
                raw[row + 2 + (x * channels)] = (byte)(y * 255 / size);
                raw[row + 3 + (x * channels)] = 128;
                if (alpha)
                {
                    raw[row + 4 + (x * channels)] = (byte)(255 - (x * 20));
                }
            }
        }

        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, true))
        {
            zs.Write(raw);
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, size);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), size);
        ihdr[8] = 8;
        ihdr[9] = (byte)(alpha ? 6 : 2);
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", z.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        byte[] body = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(body, 0);
        data.CopyTo(body, 4);
        s.Write(body);
        uint crc = 0xFFFFFFFF;
        foreach (byte b in body)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        var c = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, ~crc);
        s.Write(c);
    }

    [Fact]
    public void BoxesAndTextReachThePage()
    {
        LayoutDocument layout = LayoutDocument.Compose(doc => doc.Page(page =>
        {
            page.Size(300, 400);
            page.Margin(20);
            page.Content().Padding(8).Border(1, Colors.Red).Background(Colors.LightGrey).Rounded(4).AlignCenter().Width(200).Text("Boxed hello").FontSize(14).Bold();
        }));
        using PdfDocument d = Open(Save(layout));
        Assert.Single(d.Pages);
        Assert.Contains("Boxed hello", AllText(d), StringComparison.Ordinal);
        Assert.Empty(layout.Diagnostics);
    }

    [Fact]
    public void ColumnsRowsAndLayersCompose()
    {
        LayoutDocument layout = LayoutDocument.Compose(doc => doc.Page(page =>
        {
            page.Size(300, 400);
            page.Content().Column(col =>
            {
                col.Spacing(5);
                col.Item().Row(row =>
                {
                    row.Spacing(4);
                    row.ConstantItem(50).Text("Fixed");
                    row.RelativeItem().Text("Relative");
                    row.AutoItem().Text("Auto");
                });
                col.Item().Layers(l =>
                {
                    l.Layer().Background(Colors.LightGrey).ExpandHeight().Text("");
                    l.PrimaryLayer().Padding(4).Text("Primary layer");
                });
                col.Item().Text("Last");
            });
        }));
        using PdfDocument d = Open(Save(layout));
        string text = AllText(d);
        Assert.Matches(@"Fixed\s+Relative\s+Auto", text);
        Assert.Contains("Primary layer", text, StringComparison.Ordinal);
        Assert.EndsWith("Last", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TransformsAndCanvasDrawWithoutDisturbingLayout()
    {
        LayoutDocument layout = LayoutDocument.Compose(doc => doc.Page(page =>
        {
            page.Size(300, 400);
            page.Content().Column(col =>
            {
                col.Item().Rotate(90).Text("Turned");
                col.Item().Scale(2).Text("Big");
                col.Item().Translate(5, 5).Text("Moved");
                col.Item().Canvas(40, s =>
                {
                    s.Builder.SetFillRgb(1, 0, 0);
                    s.Builder.Rectangle(0, 0, s.Width / 2, s.Height);
                    s.Builder.Fill();
                });
                col.Item().Spacer(10);
            });
        }));
        using PdfDocument d = Open(Save(layout));
        string text = AllText(d);
        Assert.Contains("Turned", text, StringComparison.Ordinal);
        Assert.Contains("Big", text, StringComparison.Ordinal);
        Assert.Contains("Moved", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ImagesFitAndPngAndJpegAreDetected()
    {
        byte[] jpeg = Support.Jpeg();
        byte[] png = TinyPng();
        byte[] pngAlpha = TinyPng(8, alpha: true);
        LayoutDocument layout = LayoutDocument.Compose(doc => doc.Page(page =>
        {
            page.Size(300, 500);
            page.Content().Column(col =>
            {
                col.Item().Width(80).Height(60).Image(jpeg).Contain().AltText("A square");
                col.Item().Width(80).Height(60).Image(png).Cover().AltText("Gradient");
                col.Item().Width(80).Image(pngAlpha).FitWidth().Dpi(72).AltText("Gradient with alpha");
                col.Item().Width(80).Height(40).Image(png).Stretch().Artifact();
                col.Item().Height(50).Image(png).FitHeight().AltText("Tall");
            });
        }));
        byte[] pdf = Save(layout);
        using PdfDocument d = Open(pdf);
        Assert.Single(d.Pages);
        SNode tree = TagSupport.ReadTree(d);
        Assert.Equal(["A square", "Gradient", "Gradient with alpha", "Tall"], tree.Descendants().Where(n => n.Type == "Figure").Select(n => n.Alt).ToArray());
        Assert.Throws<ArgumentException>(() => LayoutDocument.Compose(doc => doc.Page(p => p.Content().Image(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }))));
    }

    [Fact]
    public void TablesSpanAndRepeatTheirHeader()
    {
        LayoutDocument layout = LayoutDocument.Compose(doc => doc.Page(page =>
        {
            page.Size(300, 300);
            page.Margin(20);
            page.Content().Table(t =>
            {
                t.Columns(c => { c.Constant(60); c.Relative(); c.Auto(); });
                t.CellPadding(3);
                t.CellBorder(0.5, Colors.Grey);
                t.RowBackground(i => i % 2 == 1 ? Colors.LightGrey : null);
                t.HeaderRow(r =>
                {
                    r.Cell().Text("Id").Bold();
                    r.Cell().Text("Name").Bold();
                    r.Cell().Text("Qty").Bold();
                });
                for (int i = 0; i < 40; i++)
                {
                    int n = i;
                    t.Row(r =>
                    {
                        r.Cell().Text($"{n}");
                        r.Cell().Text($"Item number {n}");
                        r.Cell().CellAlign(VerticalAlignment.Middle).AlignEnd().Text($"{n * 3}");
                    });
                }

                t.Row(r =>
                {
                    r.Cell().Span(2).CellBackground(Colors.Yellow).Text("Spanning two columns");
                    r.Cell().Text("x");
                });
                t.FooterRow(r => r.Cell().Span(3).Text("Footer note"));
            });
        }));
        byte[] pdf = Save(layout);
        using PdfDocument d = Open(pdf);
        Assert.True(d.Pages.Count >= 3);
        int headers = d.ExtractText().Count(p => p.Text.Contains("Name", StringComparison.Ordinal));
        Assert.Equal(d.Pages.Count, headers);
        Assert.Contains("Spanning two columns", AllText(d), StringComparison.Ordinal);
        SNode tree = TagSupport.ReadTree(d);
        Assert.Contains(tree.Descendants(), n => n.Type == "TH");
    }

    [Fact]
    public void ListsNumberAndNest()
    {
        LayoutDocument layout = LayoutDocument.Compose(doc => doc.Page(page =>
        {
            page.Size(300, 300);
            page.Content().List(l =>
            {
                l.Numbered(NumberingStyle.LowerLetters);
                l.MarkerGap(4);
                l.Item().Text("First");
                l.Item().Column(c =>
                {
                    c.Item().Text("Second");
                    c.Item().List(inner =>
                    {
                        inner.Bullet("-");
                        inner.Item().Text("Nested");
                    });
                });
                l.Item("»").Text("Custom marker");
            });
        }));
        using PdfDocument d = Open(Save(layout));
        string text = AllText(d);
        Assert.Contains("a.", text, StringComparison.Ordinal);
        Assert.Contains("b.", text, StringComparison.Ordinal);
        Assert.Contains("Nested", text, StringComparison.Ordinal);
        SNode tree = TagSupport.ReadTree(d);
        Assert.Equal(2, tree.Descendants().Count(n => n.Type == "L"));
    }

    [Fact]
    public void BreaksAndKeepsBehave()
    {
        LayoutDocument layout = LayoutDocument.Compose(doc => doc.Page(page =>
        {
            page.Size(300, 200);
            page.Margin(20);
            page.Content().Column(col =>
            {
                col.Item().Text("Page one");
                col.Item().PageBreak();
                col.Item().Text("Page two");
                col.Item().Height(100).Text("filler");
                col.Item().KeepTogether().Height(70).Background(Colors.Grey).Text("kept whole");
                col.Item().Heading(2).Text("Heading stays with next");
                col.Item().EnsureSpace(120).Text("Needs room");
            });
        }));
        using PdfDocument d = Open(Save(layout));
        string[] pages = [.. d.ExtractText().Select(t => Support.Normalize(t.Text))];
        Assert.Contains("Page one", pages[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Page two", pages[0], StringComparison.Ordinal);
        Assert.True(pages.Length >= 3);
        int keptPage = Array.FindIndex(pages, p => p.Contains("kept whole", StringComparison.Ordinal));
        int headingPage = Array.FindIndex(pages, p => p.Contains("Heading stays", StringComparison.Ordinal));
        int needsPage = Array.FindIndex(pages, p => p.Contains("Needs room", StringComparison.Ordinal));
        Assert.True(keptPage >= 1 && headingPage >= keptPage && needsPage >= headingPage);
    }

    [Fact]
    public void HeadersFootersAndPageNumbers()
    {
        LayoutDocument layout = LayoutDocument.Compose(doc => doc.Page(page =>
        {
            page.Size(300, 200);
            page.Margin(20);
            page.Header().Text("Running head").FontSize(9);
            page.Footer().AlignCenter().Text(t =>
            {
                t.FontSize(9);
                t.Span("Page ");
                t.CurrentPageNumber();
                t.Span(" / ");
                t.TotalPages();
            });
            page.Content().Column(col =>
            {
                for (int i = 0; i < 60; i++)
                {
                    col.Item().Text($"Line {i}");
                }
            });
        }));
        using PdfDocument d = Open(Save(layout));
        int n = d.Pages.Count;
        Assert.True(n >= 3, "pages " + n + ": " + string.Join(" | ", d.ExtractText().Select(t => Support.Normalize(t.Text))));
        string[] pages = [.. d.ExtractText().Select(t => Support.Normalize(t.Text))];
        for (int i = 0; i < n; i++)
        {
            Assert.Matches($@"Page {i + 1} / {n}$", pages[i]);
            Assert.StartsWith("Running head", pages[i], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SectionsFirstPageHeadersAndLabels()
    {
        LayoutDocument layout = LayoutDocument.Compose(doc =>
        {
            doc.Page(page =>
            {
                page.Size(200, 150);
                page.NumberStyle(NumberingStyle.LowerRoman);
                page.FirstPageHeader().Text("Preface head");
                page.Header().Text("Other head");
                page.Footer().Text(t => t.CurrentPageNumber());
                page.Content().Column(c => { c.Item().Text("Preface"); c.Item().PageBreak(); c.Item().Text("More preface"); });
            });
            doc.Page(page =>
            {
                page.Size(200, 150);
                page.StartNumber(1);
                page.Content().Text("Body");
            });
        });
        using PdfDocument d = Open(Save(layout));
        Assert.Equal(3, d.Pages.Count);
        string[] pages = [.. d.ExtractText().Select(t => Support.Normalize(t.Text))];
        Assert.Contains("Preface head", pages[0], StringComparison.Ordinal);
        Assert.Contains("Other head", pages[1], StringComparison.Ordinal);
        Assert.Contains("ii", pages[1], StringComparison.Ordinal);
        Assert.Equal("i", d.Pages[0].Label);
        Assert.Equal("1", d.Pages[2].Label);
    }

    [Fact]
    public void LinksAnchorsAndBookmarks()
    {
        LayoutDocument layout = LayoutDocument.Compose(doc => doc.Page(page =>
        {
            page.Size(300, 200);
            page.Content().Column(col =>
            {
                col.Item().Bookmark("Start").Anchor("top").Text("Start here");
                col.Item().Hyperlink("https://example.com/").Text("Web link");
                col.Item().LinkToAnchor("end").Text("Go to the end");
                col.Item().Height(200).Text("");
                col.Item().Bookmark("End", 0).Bookmark("Sub", 1).Anchor("end").Text("The end");
            });
        }));
        using PdfDocument d = Open(Save(layout));
        var links = d.Pages.SelectMany(p => p.Links).ToList();
        Assert.Contains(links, l => l.Action?.Uri == "https://example.com/");
        Assert.Contains(links, l => l.Action?.Destination is { PageIndex: 1 });
        Assert.Equal(["Start", "End"], d.Outline.Select(o => o.Title).ToArray());
    }

    [Fact]
    public void SemanticsReachTheStructureTree()
    {
        LayoutDocument layout = LayoutDocument.Compose(doc =>
        {
            doc.Title("Semantics").Language("en-US");
            doc.Page(page =>
            {
                page.Size(300, 300);
                page.Content().Column(col =>
                {
                    col.Item().Heading(1).Text("Top heading").FontSize(18);
                    col.Item().Heading(2).Text("Second heading");
                    col.Item().Text("Deutsch").Language("de");
                    col.Item().AltText("A decorative bar that is described").Height(20).Background(Colors.Blue).Text("");
                    col.Item().Artifact().Height(20).Background(Colors.Red).Text("");
                });
            });
        });
        byte[] pdf = Save(layout);
        using PdfDocument d = Open(pdf);
        Assert.Equal("en-US", d.Catalog.GetString("Lang")?.ToString());
        SNode tree = TagSupport.ReadTree(d);
        Assert.Equal(["H1", "H2"], tree.Descendants().Where(n => n.Type is "H1" or "H2").Select(n => n.Type).ToArray());
        Assert.Contains(tree.Descendants(), n => n.Type == "Figure" && n.Alt == "A decorative bar that is described");
        Assert.Contains(tree.Descendants(), n => n.Lang == "de");
    }

    [Fact]
    public void TaggingCanBeSwitchedOffAndDebugDrawsBoxes()
    {
        LayoutDocument untagged = LayoutDocument.Compose(doc =>
        {
            doc.Tagged(false);
            doc.Page(p => p.Content().Text("Plain"));
        });
        using (PdfDocument d = Open(Save(untagged)))
        {
            Assert.Null(d.Catalog.GetDictionary("StructTreeRoot"));
        }

        LayoutDocument debug = LayoutDocument.Compose(doc =>
        {
            doc.Debug();
            doc.Page(p => p.Content().Padding(5).Text("Plain"));
        });
        LayoutDocument normal = LayoutDocument.Compose(doc => doc.Page(p => p.Content().Padding(5).Text("Plain")));
        Assert.True(Save(debug).Length > Save(normal).Length);
    }

    [Fact]
    public void RightToLeftMirrorsRows()
    {
        LayoutDocument layout = LayoutDocument.Compose(doc =>
        {
            doc.RightToLeft();
            doc.Page(page =>
            {
                page.Size(300, 200);
                page.Content().Row(r =>
                {
                    r.ConstantItem(100).Text("first");
                    r.ConstantItem(100).Text("second");
                });
            });
        });
        using PdfDocument d = Open(Save(layout));
        var words = d.ExtractText()[0].Words.ToList();
        double first = words.First(w => w.Text == "first").Bounds.Left;
        double second = words.First(w => w.Text == "second").Bounds.Left;
        Assert.True(first > second, "in a right-to-left row the first item sits at the right");
    }

    private sealed class Badge(string label) : IComponent
    {
        public void Compose(Slot slot) => slot.Border(0.5).Padding(2).Text(label).FontSize(8);
    }

    [Fact]
    public void ComponentsAndElementObjectsMix()
    {
        LayoutDocument layout = LayoutDocument.Compose(doc => doc.Page(page =>
        {
            page.Size(300, 200);
            page.Content().Column(col =>
            {
                col.Item().Component(new Badge("one"));
                col.Item().Component<Badge2>();
                col.Item().Element(new Text { Content = "from an element object" });
            });
        }));
        using PdfDocument d = Open(Save(layout));
        string text = AllText(d);
        Assert.Contains("one", text, StringComparison.Ordinal);
        Assert.Contains("two", text, StringComparison.Ordinal);
        Assert.Contains("from an element object", text, StringComparison.Ordinal);
    }

    private sealed class Badge2 : IComponent
    {
        public void Compose(Slot slot) => slot.Component(new Badge("two"));
    }

    private sealed class EveryThree : IHyphenator
    {
        public IReadOnlyList<int> GetBreaks(string word, string? language) => [.. Enumerable.Range(1, word.Length - 1).Where(i => i % 3 == 0)];
    }

    [Fact]
    public void TextSpansFontsJustifyAndHyphenation()
    {
        LayoutDocument layout = LayoutDocument.Compose(doc =>
        {
            doc.Page(page =>
            {
                page.Size(200, 300);
                page.Content().Text(t =>
                {
                    t.Justify();
                    t.Hyphenator(new EveryThree());
                    t.FontSize(10);
                    t.Span("Plain, ");
                    t.Span("bold, ").Bold();
                    t.Span("italic, ").Italic();
                    t.Span("underlined").Underline().FontColor(Colors.Blue);
                    t.Span(" and 10").Bold();
                    t.Span("2").Superscript();
                    t.Line("");
                    t.Span("Extraordinarily extraordinarily long words wrap here.").FontFamily("Serif");
                });
            });
        });
        using PdfDocument d = Open(Save(layout));
        string text = AllText(d);
        Assert.Contains("Plain, bold, italic, underlined and 10", text, StringComparison.Ordinal);
        Assert.Contains("words wrap here.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisteredFontsAndStandardFontsAreReachable()
    {
        string? noto = Directory.GetFiles(Path.Combine(Support.Root(), "testdata", "fonts"), "*.ttf").FirstOrDefault();
        LayoutDocument layout = LayoutDocument.Compose(doc =>
        {
            if (noto is not null)
            {
                doc.Fonts.Add(File.ReadAllBytes(noto), "Test Face");
                doc.Fonts.FallbackFamilies.Add("Test Face");
            }

            doc.Page(page =>
            {
                page.Size(300, 200);
                page.Content().Column(c =>
                {
                    c.Item().Text("Standard Helvetica").FontFamily("Helvetica");
                    c.Item().Text("Serif and mono").FontFamily("Serif", "Mono");
                });
            });
        });
        byte[] pdf = Save(layout);
        using PdfDocument d = Open(pdf);
        Assert.Contains("Standard Helvetica", AllText(d), StringComparison.Ordinal);
    }

    [Fact]
    public void SlotMisuseThrows()
    {
        Slot? kept = null;
        Assert.Throws<InvalidOperationException>(() => LayoutDocument.Compose(doc => doc.Page(p =>
        {
            Slot c = p.Content();
            c.Text("a");
            c.Text("b");
        })));
        LayoutDocument.Compose(doc => doc.Page(p =>
        {
            kept = p.Content();
            kept.Text("x");
        }));
        Assert.Throws<InvalidOperationException>(() => kept!.Text("late"));
        Assert.Throws<ArgumentNullException>(() => LayoutDocument.Compose(null!));
    }

    [Fact]
    public void PdfDocumentComposeIsAnEntryPoint()
    {
        byte[] pdf = PdfDocument.Compose(doc => doc.Page(p => p.Content().Text("via PdfDocument"))).ToBytes(new PdfDocumentWriterOptions { DocumentId = new byte[16] });
        using PdfDocument d = Open(pdf);
        Assert.Contains("via PdfDocument", AllText(d), StringComparison.Ordinal);
    }

    [Fact]
    public void EncryptionAndPdfAGoThroughCoreSaveOptions()
    {
        LayoutDocument layout = LayoutDocument.Compose(doc =>
        {
            doc.Title("Secret").Language("en");
            doc.Page(p => p.Content().Text("Confidential text"));
        });
        using var ms = new MemoryStream();
        layout.Save(ms, new PdfSaveOptions
        {
            Encryption = PdfEncryptionAction.Set,
            NewEncryption = new PdfEncryptionOptions { UserPassword = "pw", Cipher = PdfCipher.Aes256 },
        });
        Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(ms.ToArray()));
        using PdfDocument d = PdfDocument.Open(ms.ToArray(), new PdfOpenOptions { Password = "pw" });
        Assert.Contains("Confidential text", AllText(d), StringComparison.Ordinal);
    }

    [Fact]
    public void FileSaveWritesTheFile()
    {
        string dir = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "facade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "out.pdf");
            LayoutDocument.Compose(doc => doc.Page(p => p.Content().Text("To a file"))).Save(path);
            using PdfDocument d = PdfDocument.Open(path);
            Assert.Contains("To a file", AllText(d), StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TextInAnAlignedSlotTakesTheAlignment()
    {
        LayoutDocument layout = LayoutDocument.Compose(doc => doc.Page(page =>
        {
            page.Size(300, 400);
            page.Margin(20);
            page.Content().Column(c =>
            {
                c.Item().AlignEnd().Text("flush end");
                c.Item().AlignCenter().Padding(2).Text("centred");
                c.Item().Text("flush start");
                c.Item().AlignEnd().Text("overridden").AlignStart();
            });
        }));
        using PdfDocument d = Open(Save(layout));
        var words = d.ExtractText()[0].Words.ToList();
        double Left(string w) => words.First(x => x.Text == w).Bounds.Left;
        Assert.True(Left("flush") > 150);
        Assert.InRange(Left("centred"), 100, 160);
        Assert.True(Left("overridden") < 30);
    }

    [Fact]
    public void DecoratorsShareABoxOnlyWhenTheOrderAllows()
    {
        Element? root = null;
        new Slot(e => root = e).Border(1).Rounded(8).Background(Colors.Red).Padding(6).Width(50).Text("a");
        // Border, rounding and background paint inside the margin and outside the padding: one box. Width is outside the padding, so it
        // cannot join a box that already has padding and wraps it instead.
        var box = Assert.IsType<Box>(root);
        Assert.Equal(8, box.CornerRadius);
        Assert.NotNull(box.Background);
        Assert.Equal(6, box.Padding.Top);
        Assert.Null(box.Width);
        var outer = Assert.IsType<Box>(box.Child);
        Assert.Equal(50, outer.Width);
        Assert.IsType<Text>(outer.Child);

        Element? padded = null;
        new Slot(e => padded = e).Padding(8).Border(1).Text("b");
        var space = Assert.IsType<Box>(padded);
        Assert.Equal(8, space.Padding.Left);
        Assert.Equal(0, space.BorderTop.Width);
        Assert.Equal(1, Assert.IsType<Box>(space.Child).BorderTop.Width);

        Element? twice = null;
        new Slot(e => twice = e).Border(1).Border(3).Text("c");
        Assert.IsType<Box>(Assert.IsType<Box>(twice).Child);
    }

    [Fact]
    public void UnitsAndColors()
    {
        Assert.Equal(72.0, 1.Inch(), 9);
        Assert.Equal(72.0, Units.Mm(25.4), 9);
        Assert.Equal(72.0, 2.54.Cm(), 9);
        Assert.Equal(new LayoutColor(1, 0, 0), LayoutColor.FromHex("#f00"));
        Assert.Equal(LayoutColor.FromBytes(0x33, 0x66, 0x99), LayoutColor.FromHex("336699"));
        Assert.Throws<FormatException>(() => LayoutColor.FromHex("#12"));
    }

    // ---- parity: the facade and hand-written element objects give the same file ----

    private static readonly LayoutColor Tint = new(0.9, 0.9, 1);

    private static LayoutDocument ReportByFacade(int rows, byte[] jpeg) => LayoutDocument.Compose(doc =>
    {
        doc.Title("Report").Language("en");
        doc.Page(page =>
        {
            page.Size(PageSize.Letter);
            page.Margin(54);
            page.Header().PaddingBottom(6).BorderBottom(0.5).Text("Acme Corp").FontSize(9);
            page.Footer().AlignCenter().Text(t =>
            {
                t.FontSize(9);
                t.Span("Page ");
                t.CurrentPageNumber();
                t.Span(" of ");
                t.TotalPages();
            });
            page.Content().Column(col =>
            {
                col.Spacing(8);
                col.Item().Heading(1).Text("Quarterly report").FontSize(20).Bold();
                for (int i = 0; i < rows; i++)
                {
                    int n = i;
                    col.Item().Row(row =>
                    {
                        row.Spacing(10);
                        row.ConstantItem(60).Padding(4).Background(Tint).Text($"Item {n}").FontSize(10);
                        row.RelativeItem().Padding(4).Border(0.5, Colors.DarkGrey, BorderStyle.Dashed).Text($"Paragraph {n} explains the figures for item {n} in a few plain words.").FontSize(10);
                    });
                }

                col.Item().Table(t =>
                {
                    t.Columns(c => { c.Constant(80); c.Relative(); });
                    t.HeaderRow(r => { r.Cell().Text("Key"); r.Cell().Text("Value"); });
                    for (int i = 0; i < 40; i++)
                    {
                        int n = i;
                        t.Row(r => { r.Cell().Text($"k{n}"); r.Cell().Text($"value {n}"); });
                    }
                });
                col.Item().List(l =>
                {
                    l.Numbered();
                    l.Item().Text("one");
                    l.Item().Text("two");
                });
                col.Item().Width(64).Height(64).Image(jpeg).Contain().AltText("Square");
                col.Item().Hyperlink("https://example.com/").Text("The end").FontSize(12);
            });
        });
    });

    private static LayoutDocument ReportByElements(int rows, byte[] jpeg)
    {
        var body = new Column { Spacing = 8 };
        body.Items.Add(new Heading { Level = 1, Child = new Text { Content = "Quarterly report", Size = 20, Weight = 700 } });
        for (int i = 0; i < rows; i++)
        {
            var row = new Row { Spacing = 10 };
            row.Items.Add(RowItem.Constant(60, new Box { Padding = Insets.All(4), Child = new Box { Background = Tint, Child = new Text { Content = $"Item {i}", Size = 10 } } }));
            var bordered = new Box { Child = new Text { Content = $"Paragraph {i} explains the figures for item {i} in a few plain words.", Size = 10 } };
            bordered.SetBorders(new Border(0.5, Colors.DarkGrey, BorderStyle.Dashed));
            row.Items.Add(RowItem.Relative(1, new Box { Padding = Insets.All(4), Child = bordered }));
            body.Items.Add(row);
        }

        var table = new Table();
        table.Columns.Add(TableColumn.Constant(80));
        table.Columns.Add(TableColumn.Relative(1));
        table.HeaderRows.Add(new TableRow { Cells = { new TableCell(new Text { Content = "Key" }), new TableCell(new Text { Content = "Value" }) } });
        for (int i = 0; i < 40; i++)
        {
            table.Rows.Add(new TableRow { Cells = { new TableCell(new Text { Content = $"k{i}" }), new TableCell(new Text { Content = $"value {i}" }) } });
        }

        body.Items.Add(table);
        var list = new ItemList { Kind = ListKind.Numbered };
        list.Items.Add(new ListItem(new Text { Content = "one" }));
        list.Items.Add(new ListItem(new Text { Content = "two" }));
        body.Items.Add(list);
        body.Items.Add(new Box { Width = 64, Height = 64, Child = new Image { Jpeg = jpeg, Fit = ImageFit.Contain, AltText = "Square" } });
        body.Items.Add(new Link { Uri = "https://example.com/", Child = new Text { Content = "The end", Size = 12 } });

        double space = 0.278 * 9;
        var footerRow = new Row { Compact = true };
        footerRow.Items.Add(new RowItem(new Text { Content = "Page", Size = 9 }));
        footerRow.Items.Add(RowItem.Constant(space, new Spacer()));
        footerRow.Items.Add(new RowItem(new PageNumber { Size = 9 }));
        footerRow.Items.Add(RowItem.Constant(space, new Spacer()));
        footerRow.Items.Add(new RowItem(new Text { Content = "of", Size = 9 }));
        footerRow.Items.Add(RowItem.Constant(space, new Spacer()));
        footerRow.Items.Add(new RowItem(new PageNumber { Size = 9, Kind = PageNumberKind.DocumentTotal }));

        var doc = new LayoutDocument { Title = "Report", Language = "en" };
        doc.Pages.Add(new LayoutPage
        {
            Size = PageSize.Letter,
            Margin = Insets.All(54),
            Header = new Box { Padding = new Insets(0, 0, 6, 0), Child = new Box { BorderBottom = new Border(0.5, LayoutColor.Black), Child = new Text { Content = "Acme Corp", Size = 9 } } },
            Footer = new Box { HorizontalAlignment = HorizontalAlignment.Center, Child = footerRow },
            Content = body,
        });
        return doc;
    }

    [Fact]
    public void FiftyPageReportIsByteIdenticalFromFacadeAndElementObjects()
    {
        byte[] jpeg = Support.Jpeg();
        byte[] viaFacade = Support.Save(ReportByFacade(700, jpeg), "parity-facade");
        byte[] viaElements = Support.Save(ReportByElements(700, jpeg), "parity-elements");
        using PdfDocument d = Open(viaFacade);
        Assert.InRange(d.Pages.Count, 20, 120);
        TestContext.Current.SendDiagnosticMessage($"report pages: {d.Pages.Count}, bytes: {viaFacade.Length}");
        Assert.True(viaFacade.AsSpan().SequenceEqual(viaElements), "facade and element objects must give the same bytes");
    }

    // ---- totals use the embedded face ----

    [Fact]
    public void PageTotalsUseTheEmbeddedFaceAndPassPdfA()
    {
        LayoutDocument layout = LayoutDocument.Compose(doc =>
        {
            doc.Title("Totals").Language("en");
            doc.Page(page =>
            {
                page.Size(300, 200);
                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span("Page ");
                    t.CurrentPageNumber();
                    t.Span(" of ");
                    t.TotalPages();
                });
                page.Content().Column(c => { for (int i = 0; i < 40; i++) { c.Item().Text($"Line {i}"); } });
            });
        });
        byte[] pdf = Save(layout);
        using (PdfDocument d = Open(pdf))
        {
            string raw = System.Text.Encoding.Latin1.GetString(pdf);
            Assert.DoesNotContain("/BaseFont /Helvetica", raw, StringComparison.Ordinal);
            string[] pages = [.. d.ExtractText().Select(t => Support.Normalize(t.Text))];
            Assert.EndsWith($"of {d.Pages.Count}", pages[^1], StringComparison.Ordinal);
        }

        using var ms = new MemoryStream();
        layout.Save(ms, new PdfSaveOptions { PdfA = new PdfAOptions { Part = PdfAPart.Part2, Conformance = PdfAConformance.B } });
        List<string> failures = TagSupport.Vera(ms.ToArray(), "2b");
        Assert.Empty(failures);
        List<string> ua = TagSupport.Vera(pdf, "ua1");
        TestContext.Current.SendDiagnosticMessage("ua1: " + (ua.Count == 0 ? "none" : string.Join("; ", ua)));
        Assert.DoesNotContain(ua, f => f.StartsWith("7.21.4.1", StringComparison.Ordinal));
    }
}
