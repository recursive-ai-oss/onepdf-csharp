namespace OnePdf.Layout.Tests;

/// <summary>
/// The code samples of README.md and docs/layout.md. Each sample sits between <c>// sample: name</c> and <c>// end sample</c> markers; the
/// tests below run them and check that the documentation contains the same text, so a sample cannot drift from what compiles.
/// </summary>
[Collection("DocSamples")]
public class DocSamples
{
    // The samples read and write files in the working directory, so each test runs in a directory of its own.
    private static readonly object Gate = new();

    private static string Work()
    {
        string dir = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "docsamples-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "logo.png"), FacadeTests.TinyPng());
        File.WriteAllBytes(Path.Combine(dir, "photo.jpg"), Support.Jpeg());
        File.Copy(Path.Combine(Support.Root(), "testdata", "fonts", "NotoSansArabic-Subset-Regular.ttf"), Path.Combine(dir, "NotoSansArabic-Regular.ttf"));
        return dir;
    }

    private static void InDirectory(Action run)
    {
        lock (Gate)
        {
            string saved = Environment.CurrentDirectory;
            string dir = Work();
            try
            {
                Environment.CurrentDirectory = dir;
                run();
                foreach (string pdf in Directory.GetFiles(dir, "*.pdf"))
                {
                    using PdfDocument d = PdfDocument.Open(pdf, new PdfOpenOptions { Password = "open sesame" });
                    Assert.True(d.Pages.Count >= 1, pdf);
                }
            }
            finally
            {
                Environment.CurrentDirectory = saved;
                Directory.Delete(dir, true);
            }
        }
    }

    // sample: hello
    private static void Hello()
    {
        LayoutDocument.Compose(doc =>
        {
            doc.Title("Hello");
            doc.Page(page =>
            {
                page.Size(PageSize.A4);
                page.Margin(36);
                page.Content().Text("Hello, world").FontSize(24);
            });
        }).Save("hello.pdf");
    }
    // end sample

    // sample: report
    private static void Report()
    {
        LayoutDocument.Compose(doc =>
        {
            doc.Title("Quarterly report").Language("en");
            doc.Page(page =>
            {
                page.Size(PageSize.A4);
                page.Margin(2.Cm());
                page.Header().Text("Acme Corp").FontSize(9).FontColor(Colors.Grey);
                page.Content().Column(col =>
                {
                    col.Spacing(10);
                    col.Item().Heading(1).Text("Quarterly report").FontSize(22).Bold();
                    col.Item().Text("Sales rose in every region.").Justify();
                    col.Item().Table(table =>
                    {
                        table.Columns(c => { c.Relative(2); c.Constant(60); });
                        table.CellBorder(0.5, Colors.LightGrey);
                        table.HeaderRow(r =>
                        {
                            r.Cell().Text("Region").Bold();
                            r.Cell().Text("Sales").Bold();
                        });
                        table.Row(r => { r.Cell().Text("North"); r.Cell().AlignEnd().Text("1,204"); });
                        table.Row(r => { r.Cell().Text("South"); r.Cell().AlignEnd().Text("987"); });
                    });
                });
                page.Footer().AlignCenter().Text(t =>
                {
                    t.FontSize(9);
                    t.Span("Page ");
                    t.CurrentPageNumber();
                    t.Span(" of ");
                    t.TotalPages().ReservedDigits(2);
                });
            });
        }).Save("report.pdf");
    }
    // end sample

    // sample: invoice
    private static void Invoice()
    {
        LayoutDocument.Compose(doc =>
        {
            doc.Title("Invoice 1042").Language("en");
            doc.Page(page =>
            {
                page.Size(PageSize.A4);
                page.Margin(2.Cm());
                page.Header().Text("Acme Corp").FontSize(9).FontColor(Colors.Grey);
                page.Content().Column(col =>
                {
                    col.Spacing(8);
                    col.Item().Heading(1).Text("Invoice 1042").FontSize(20).Bold();
                    col.Item().Table(table =>
                    {
                        table.Columns(c => { c.Relative(3); c.Constant(70); });
                        table.HeaderRow(r => { r.Cell().Text("Item").Bold(); r.Cell().AlignEnd().Text("Price").Bold(); });
                        table.Row(r => { r.Cell().Text("Widget"); r.Cell().AlignEnd().Text("12.00"); });
                    });
                });
                page.Footer().AlignCenter().Text(t =>
                {
                    t.CurrentPageNumber();
                    t.Span(" / ");
                    t.TotalPages().ReservedDigits(2);
                });
            });
        }).Save("invoice.pdf");
    }
    // end sample

    // sample: chain
    private static void Chain()
    {
        LayoutDocument.Compose(doc => doc.Page(page =>
        {
            page.Content().Column(col =>
            {
                // Read from the outside in: a margin, then a rounded border with a background, then padding, then the text.
                col.Item()
                    .Margin(8)
                    .Border(1.5, Colors.Blue).Rounded(6).Background(Colors.LightGrey)
                    .Padding(10)
                    .Text("A rounded, padded box");
            });
        })).Save("chain.pdf");
    }
    // end sample

    // sample: gradient
    private static void Gradient()
    {
        LayoutDocument.Compose(doc => doc.Page(page =>
        {
            page.Content().Column(col =>
            {
                col.Spacing(10);

                // 90 degrees runs left to right; the first stop is reached in one corner and the last in the opposite one.
                col.Item().Height(60).Rounded(8)
                    .Background(LayoutGradient.Linear(90, Colors.Blue, Colors.LightGrey))
                    .Padding(10).Text("Linear").FontColor(Colors.White);

                // Several stops, each with a position from 0 to 1, here in DeviceCMYK (DeviceGray works too; one space per gradient).
                col.Item().Height(60).Background(LayoutGradient.Horizontal(
                    new LayoutGradientStop(0, PdfGradientColor.Cmyk(0, 1, 1, 0)),
                    new LayoutGradientStop(0.3, PdfGradientColor.Cmyk(0, 0, 1, 0)),
                    new LayoutGradientStop(1, PdfGradientColor.Cmyk(1, 0.6, 0, 0))));

                // Circles around a centre given as fractions of the box.
                col.Item().Height(60).Background(LayoutGradient.Circle(
                    [new LayoutGradientStop(0, Colors.White), new LayoutGradientStop(1, Colors.Blue)],
                    centerX: 0.25, centerY: 0.5, extent: RadialExtent.FarthestCorner));
            });
        })).Save("gradients.pdf");
    }
    // end sample

    // sample: columns
    private static void Columns()
    {
        string text = string.Concat(Enumerable.Repeat("Text poured through columns breaks between them the way it breaks between pages. ", 12));

        LayoutDocument.Compose(doc =>
        {
            doc.Title("Newsletter").Language("en");
            doc.Page(page =>
            {
                page.Size(PageSize.A5);
                page.Margin(28);
                page.Content().Column(col =>
                {
                    col.Spacing(8);
                    col.Item().Heading(1).Text("Harbour Gazette").FontSize(20).Bold();

                    // Two columns that flow down, then across, then onto the next page. The last page is balanced.
                    col.Item().MultiColumn(cols =>
                    {
                        cols.Count(2).Gap(16).Rule(0.5, Colors.Grey).Spacing(6);
                        cols.Item().Heading(2).Text("Tides").FontSize(12).Bold();
                        cols.Item().Text(text).FontSize(9).Justify();
                        cols.ColumnBreak();
                        cols.Item().Text("Starts the second column.").FontSize(9);
                        cols.Item().Text(text).FontSize(9).Justify();
                    });

                    col.Item().Text("Back to full width.").FontSize(8);
                });
            });
        }).Save("columns.pdf");
    }
    // end sample

    // sample: component
    sealed class Address(string name, string city) : IComponent
    {
        public void Compose(Slot slot) => slot.Column(column =>
        {
            column.Item().Text(name).Bold();
            column.Item().Text(city);
        });
    }

    private static void Components()
    {
        LayoutDocument.Compose(doc => doc.Page(page =>
            page.Content().Component(new Address("Ada Lovelace", "London"))))
            .Save("address.pdf");
    }
    // end sample

    // sample: navigation
    private static void Navigation()
    {
        LayoutDocument.Compose(doc => doc.Page(page =>
        {
            page.Size(PageSize.A5);
            page.Content().Column(col =>
            {
                col.Item().Bookmark("Contents").Anchor("top").Heading(1).Text("Contents");
                col.Item().List(list =>
                {
                    list.Numbered();
                    list.Item().LinkToAnchor("top").Text("Back to the top");
                    list.Item().Hyperlink("https://example.com/").Text("A web page");
                });
            });
        })).Save("navigation.pdf");
    }
    // end sample

    // sample: toc
    private static void TableOfContents()
    {
        LayoutDocument.Compose(doc =>
        {
            doc.Title("Field notes").Language("en");
            doc.Page(page =>
            {
                page.NumberStyle(NumberingStyle.LowerRoman);
                page.Content().Column(col =>
                {
                    col.Item().Text("Contents").FontSize(20).Bold();
                    col.Item().TableOfContents(toc => toc.Levels(1, 2).Level(1, s => s.Weight = 700));
                });
            });
            doc.Page(page =>
            {
                page.StartNumber(1);
                page.Content().Column(col =>
                {
                    col.Item().Heading(1).Text("The harbour").FontSize(16).Bold();
                    col.Item().Text("Gulls and ropes.");
                    col.Item().PageBreak();
                    col.Item().Heading(1).Text("The orchard").FontSize(16).Bold();
                    col.Item().Heading(2).Text("Windfalls").Bold();
                    col.Item().TocEntry("A note on cider", 2).Text("Pressed in October.");
                });
            });
        }).Save("toc.pdf");
    }
    // end sample

    // sample: images
    private static void Images()
    {
        LayoutDocument.Compose(doc => doc.Page(page =>
        {
            page.Content().Column(col =>
            {
                col.Spacing(8);
                col.Item().Width(120).Image(File.ReadAllBytes("logo.png")).AltText("Company logo");
                col.Item().Width(200).Height(80).Image("photo.jpg").Cover().AltText("A photo");
            });
        })).Save("images.pdf");
    }
    // end sample

    // sample: rtl
    private static void RightToLeft()
    {
        LayoutDocument.Compose(doc =>
        {
            doc.RightToLeft().Language("ar");
            doc.Fonts.Add(File.ReadAllBytes("NotoSansArabic-Regular.ttf"), "Noto Sans Arabic");
            doc.Fonts.FallbackFamilies.Add("Noto Sans Arabic");
            doc.Page(page => page.Content().Row(row =>
            {
                row.ConstantItem(80).Text("\u0645\u0631\u062D\u0628\u0627");
                row.RelativeItem().Text("first item sits at the right");
            }));
        }).Save("rtl.pdf");
    }
    // end sample

    // sample: save
    private static void SaveOptions()
    {
        LayoutDocument layout = LayoutDocument.Compose(doc =>
        {
            doc.Title("Archive copy").Language("en");
            doc.Page(page => page.Content().Text("Archive copy"));
        });

        layout.Save("plain.pdf");                                // streamed page by page

        using (var archive = File.Create("archive.pdf"))         // laid out, then rewritten by the core library
        {
            layout.Save(archive, new PdfSaveOptions
            {
                PdfA = new PdfAOptions { Part = PdfAPart.Part2, Conformance = PdfAConformance.B },
            });
        }

        using (var secret = File.Create("secret.pdf"))
        {
            layout.Save(secret, new PdfSaveOptions
            {
                Encryption = PdfEncryptionAction.Set,
                NewEncryption = new PdfEncryptionOptions { UserPassword = "open sesame" },
            });
        }
    }
    // end sample

    [Fact]
    public void SamplesRun()
    {
        InDirectory(() =>
        {
            Hello();
            Invoice();
            Report();
            Chain();
            Gradient();
            Columns();
            Components();
            Navigation();
            TableOfContents();
            Images();
            RightToLeft();
            SaveOptions();
        });
    }

    // Each sample must appear, unchanged apart from its indentation, in the document that shows it.
    [Theory]
    [InlineData("hello", "README.md")]
    [InlineData("report", "docs/layout.md")]
    [InlineData("invoice", "docs/migrating-from-questpdf.md")]
    [InlineData("chain", "docs/layout.md")]
    [InlineData("gradient", "docs/layout.md")]
    [InlineData("columns", "docs/layout.md")]
    [InlineData("component", "docs/layout.md")]
    [InlineData("navigation", "docs/layout.md")]
    [InlineData("toc", "docs/layout.md")]
    [InlineData("images", "docs/layout.md")]
    [InlineData("rtl", "docs/layout.md")]
    [InlineData("save", "docs/layout.md")]
    public void DocumentsShowTheSamples(string name, string document)
    {
        string sample = Extract(name);
        string doc = File.ReadAllText(Path.Combine(Support.Root(), document)).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(sample, doc, StringComparison.Ordinal);
    }

    private static string Extract(string name, [System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        string[] lines = File.ReadAllLines(here);
        int start = Array.FindIndex(lines, l => l.Trim() == "// sample: " + name);
        Assert.True(start >= 0, "no sample " + name);
        int end = Array.FindIndex(lines, start, l => l.Trim() == "// end sample");
        string[] body = lines[(start + 1)..end];

        // A sample that is a single method shows the statements inside it; one that also holds a type shows the type, then the statements of the method after it.
        bool IsMethod(string l) => l.Trim().StartsWith("private static void ", StringComparison.Ordinal);
        int first = Array.FindIndex(body, IsMethod);
        if (first < 0)
        {
            return Dedent(body);
        }

        string[] statements = body[(first + 2)..Array.FindLastIndex(body, l => l.Trim() == "}")];
        return first == 0 ? Dedent(statements) : Dedent(body[..first]) + "\n\n" + Dedent(statements);
    }

    private static string Dedent(string[] block)
    {
        string[] text = [.. block.SkipWhile(l => l.Trim().Length == 0)];
        int indent = text.Where(l => l.Trim().Length > 0).Min(l => l.Length - l.TrimStart().Length);
        return string.Join("\n", text.Select(l => l.Length >= indent ? l[indent..] : l)).TrimEnd();
    }
}
