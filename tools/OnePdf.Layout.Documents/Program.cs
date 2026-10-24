using System.IO.Compression;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using OnePdf;
using OnePdf.Layout;

// Writes the layout reference documents: small PDFs that each exercise one family of layout features, built with the fluent API.
//
//   dotnet run --project tools/OnePdf.Layout.Documents -- <repository root>
//
// Output goes to testdata/layout-references/documents/ (the PDFs and index.csv). The files are deterministic: running this again gives
// the same bytes. tools/OnePdf.Layout.References renders them.
string root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
string outDir = Path.Combine(root, "testdata", "layout-references", "documents");
Directory.CreateDirectory(outDir);
string fonts = Path.Combine(root, "testdata", "fonts");
byte[] jpeg = File.ReadAllBytes(Path.Combine(root, "testdata", "generated", "images", "rgb16.jpg"));

var index = new StringBuilder("document,pages,description\n");
var options = new PdfDocumentWriterOptions { DocumentId = new byte[16], LeaveOpen = true };

void Write(string name, string description, LayoutDocument doc)
{
    byte[] pdf = doc.ToBytes(options);
    File.WriteAllBytes(Path.Combine(outDir, name + ".pdf"), pdf);
    using PdfDocument back = PdfDocument.Open(pdf);
    index.Append(CultureInfo.InvariantCulture, $"{name}.pdf,{back.Pages.Count},\"{description}\"\n");
    Console.WriteLine($"{name}.pdf  {back.Pages.Count} page(s)");
}

string[] heads = ["Item", "Qty", "Price", "Total"];
double[] opacities = [1.0, 0.7, 0.4, 0.15];
var ink = LayoutColor.FromHex("#1f3a5f");
var paper = LayoutColor.FromHex("#eef2f7");

Write("invoice", "One-page invoice: header band, address blocks, a table with a footer row, page x of y", LayoutDocument.Compose(doc =>
{
    doc.Title("Invoice 2026-0042").Language("en");
    doc.Page(page =>
    {
        page.Size(PageSize.A5);
        page.Margin(28);
        page.Header().Row(row =>
        {
            row.RelativeItem().Text("Northwind Traders").FontSize(16).Bold().FontColor(ink);
            row.AutoItem().AlignRight().Text("INVOICE\n2026-0042").FontSize(10).AlignRight();
        });
        page.Content().PaddingTop(14).Column(col =>
        {
            col.Spacing(10);
            col.Item().Row(row =>
            {
                row.Spacing(12);
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text("Bill to").FontSize(8).FontColor(Colors.Grey);
                    c.Item().Text("Ada Lovelace\n12 Analytical Way\nLondon").FontSize(10);
                });
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text("Dates").FontSize(8).FontColor(Colors.Grey);
                    c.Item().Text("Issued 2026-10-01\nDue 2026-10-31").FontSize(10);
                });
            });
            col.Item().Table(t =>
            {
                t.Columns(c => { c.Relative(3); c.Constant(36); c.Constant(52); c.Constant(56); });
                t.CellPadding(4);
                t.CellBorder(0.4, Colors.LightGrey);
                t.HeaderRow(r =>
                {
                    foreach (string h in heads)
                    {
                        r.Cell().CellBackground(ink).Text(h).FontSize(9).Bold().FontColor(Colors.White);
                    }
                });
                (string, int, double)[] lines = [("Pencils, box of 12", 4, 3.5), ("Notebook, A5", 10, 2.25), ("Desk lamp", 1, 24), ("Binder clips", 6, 1.1)];
                foreach ((string name, int qty, double price) in lines)
                {
                    t.Row(r =>
                    {
                        r.Cell().Text(name).FontSize(9);
                        r.Cell().AlignEnd().Text(qty.ToString(CultureInfo.InvariantCulture)).FontSize(9);
                        r.Cell().AlignEnd().Text(price.ToString("F2", CultureInfo.InvariantCulture)).FontSize(9);
                        r.Cell().AlignEnd().Text((qty * price).ToString("F2", CultureInfo.InvariantCulture)).FontSize(9);
                    });
                }

                t.FooterRow(r =>
                {
                    r.Cell().Span(3).CellBackground(paper).AlignEnd().Text("Total due").FontSize(9).Bold();
                    r.Cell().CellBackground(paper).AlignEnd().Text("77.60").FontSize(9).Bold();
                });
            });
        });
        page.Footer().AlignCenter().Text(t =>
        {
            t.FontSize(8);
            t.FontColor(Colors.Grey);
            t.Span("Page ");
            t.CurrentPageNumber();
            t.Span(" of ");
            t.TotalPages().ReservedDigits(1);
        });
    });
}));

static string Para(int n) => string.Concat(Enumerable.Repeat("Layout turns a tree of boxes, rows and paragraphs into pages. Text wraps at the width it is given, splits between lines at a page end and keeps at least two lines together. ", n)).Trim();

Write("report", "Two-page report: headings with bookmarks, justified paragraphs that split across pages, a link and its anchor", LayoutDocument.Compose(doc =>
{
    doc.Title("Quarterly report").Language("en");
    doc.Page(page =>
    {
        page.Size(PageSize.A5);
        page.Margin(30);
        page.Header().PaddingBottom(4).BorderBottom(0.5, Colors.Grey).Text("Quarterly report").FontSize(8).FontColor(Colors.Grey);
        page.Footer().AlignEnd().Text(t => { t.FontSize(8); t.CurrentPageNumber(); });
        page.Content().Column(col =>
        {
            col.Spacing(8);
            col.Item().Bookmark("Summary").Anchor("summary").Heading(1).Text("Summary").FontSize(20).Bold().FontColor(ink);
            col.Item().Text(Para(3)).FontSize(10).Justify();
            col.Item().Bookmark("Details").Heading(2).Text("Details").FontSize(14).Bold();
            col.Item().Text(Para(5)).FontSize(10).Justify();
            col.Item().Text(Para(4)).FontSize(10).Justify();
            col.Item().Bookmark("Notes", 1).Heading(3).Text("Notes").FontSize(11).Bold();
            col.Item().Text(Para(3)).FontSize(10).Justify();
            col.Item().LinkToAnchor("summary").Text("Back to the summary").FontSize(10).FontColor(Colors.Blue).Underline();
        });
    });
}));

Write("table-pages", "A table over three pages: repeated header and footer, row and column spans, striped rows, collapsed borders", LayoutDocument.Compose(doc =>
{
    doc.Title("Table").Language("en");
    doc.Page(page =>
    {
        page.Size(PageSize.A5);
        page.Margin(24);
        page.Content().Table(t =>
        {
            t.Columns(c => { c.Constant(34); c.Relative(2); c.Relative(1); c.Auto(); });
            t.CellPadding(3);
            t.CellBorder(0.5, Colors.DarkGrey);
            t.RowBackground(i => i % 2 == 1 ? paper : null);
            t.HeaderRow(r =>
            {
                r.Cell().Text("No.").FontSize(9).Bold();
                r.Cell().Text("Name").FontSize(9).Bold();
                r.Cell().Text("Group").FontSize(9).Bold();
                r.Cell().Text("Score").FontSize(9).Bold();
            });
            for (int i = 1; i <= 38; i++)
            {
                int n = i;
                t.Row(r =>
                {
                    if (n % 10 == 0)
                    {
                        r.Cell().Span(3).CellBackground(LayoutColor.FromHex("#fff3c4")).Text($"Checkpoint {n / 10}: spans three columns").FontSize(9).Bold();
                        r.Cell().AlignEnd().Text($"{n * 7 % 100}").FontSize(9);
                        return;
                    }

                    r.Cell().Text($"{n}").FontSize(9);
                    r.Cell().Text($"Participant number {n}").FontSize(9);
                    r.Cell().Text(n % 3 == 0 ? "Blue" : n % 3 == 1 ? "Red" : "Green").FontSize(9);
                    r.Cell().AlignEnd().Text($"{n * 7 % 100}").FontSize(9);
                });
            }

            t.FooterRow(r => r.Cell().Span(4).AlignEnd().Text("End of list").FontSize(8).FontColor(Colors.Grey));
        });
    });
}));

Write("lists", "Bulleted, numbered, roman and lettered lists, a nested list and a custom marker", LayoutDocument.Compose(doc =>
{
    doc.Title("Lists").Language("en");
    doc.Page(page =>
    {
        page.Size(PageSize.A5);
        page.Margin(30);
        page.Content().Column(col =>
        {
            col.Spacing(10);
            col.Item().Text("Lists").FontSize(18).Bold();
            col.Item().List(l =>
            {
                l.Spacing(3);
                l.Item().Text("Flour and water").FontSize(10);
                l.Item().Column(c =>
                {
                    c.Item().Text("Anything that rises").FontSize(10);
                    c.Item().List(inner =>
                    {
                        inner.Item().Text("Yeast").FontSize(10);
                        inner.Item().Text("Baking powder").FontSize(10);
                    });
                });
                l.Item("\u00BB").Text("A custom marker").FontSize(10);
            });
            col.Item().List(l =>
            {
                l.Numbered();
                l.Start(8);
                l.Item().Text("Eighth").FontSize(10);
                l.Item().Text("Ninth").FontSize(10);
                l.Item().Text("Tenth, which has a number wider than the others").FontSize(10);
            });
            col.Item().List(l =>
            {
                l.Numbered(NumberingStyle.LowerRoman);
                l.Item().Text("First").FontSize(10);
                l.Item().Text("Second").FontSize(10);
                l.Item().Text("Third").FontSize(10);
            });
            col.Item().List(l =>
            {
                l.Numbered(NumberingStyle.UpperLetters);
                l.Suffix(")");
                l.Item().Text("Option A").FontSize(10);
                l.Item().Text("Option B").FontSize(10);
            });
        });
    });
}));

Write("boxes", "Borders (solid, dashed, dotted, per side), rounded corners, backgrounds, opacity, padding and nine-point alignment", LayoutDocument.Compose(doc =>
{
    doc.Title("Boxes").Language("en");
    doc.Page(page =>
    {
        page.Size(PageSize.A5);
        page.Margin(26);
        page.Content().Column(col =>
        {
            col.Spacing(8);
            col.Item().Border(1.5, ink).Rounded(8).Background(paper).Padding(8).Text("Rounded, padded, bordered").FontSize(11);
            col.Item().Border(1, Colors.Red, BorderStyle.Dashed).Padding(6).Text("Dashed border").FontSize(11);
            col.Item().Border(2, Colors.Blue, BorderStyle.Dotted).Padding(6).Text("Dotted border").FontSize(11);
            col.Item().BorderBottom(2, ink).BorderLeft(6, Colors.Orange).PaddingLeft(8).PaddingBottom(4).Text("Per-side borders").FontSize(11);
            col.Item().Row(row =>
            {
                row.Spacing(6);
                foreach (double o in opacities)
                {
                    double opacity = o;
                    row.RelativeItem().Opacity(opacity).Background(ink).Padding(6).Text($"{opacity:F2}").FontSize(10).FontColor(Colors.White);
                }
            });
            col.Item().Background(paper).Border(0.5, Colors.Grey).Layers(l =>
            {
                l.PrimaryLayer().Height(60).AlignCenter().AlignMiddle().Text("Centre").FontSize(10);
                l.Layer().AlignRight().AlignBottom().Padding(3).Text("bottom right").FontSize(8);
                l.Layer().AlignLeft().AlignTop().Padding(3).Text("top left").FontSize(8);
            });
            col.Item().Width(120).AspectRatio(2).Background(LayoutColor.FromHex("#d9ead3")).AlignCenter().AlignMiddle().Text("Aspect 2:1").FontSize(9);
        });
    });
}));

Write("transforms", "Rotated, scaled and translated content, a watermark layer behind and over the page, and a canvas", LayoutDocument.Compose(doc =>
{
    doc.Title("Transforms").Language("en");
    doc.Page(page =>
    {
        page.Size(PageSize.A5);
        page.Margin(30);
        page.Background().Background(LayoutColor.FromHex("#fbfbf4")).Text("");
        page.Foreground().AlignCenter().AlignMiddle().Rotate(-35).Text("SAMPLE").FontSize(64).FontColor(LayoutColor.Grey(0.85));
        page.Content().Column(col =>
        {
            col.Spacing(14);
            col.Item().Text("Transforms").FontSize(18).Bold();
            col.Item().Row(row =>
            {
                row.Spacing(10);
                row.AutoItem().Border(0.5).Rotate(90).Width(60).Padding(2).Text("Turned 90").FontSize(10);
                row.AutoItem().Border(0.5).Rotate(30).Padding(2).Text("Turned 30").FontSize(10);
                row.AutoItem().Border(0.5).Scale(1.6).Padding(2).Text("Scaled").FontSize(10);
                row.AutoItem().Border(0.5).Translate(6, 6).Padding(2).Text("Moved").FontSize(10);
            });
            col.Item().Canvas(70, s =>
            {
                PdfContentBuilder b = s.Builder;
                b.SetFillRgb(0.12, 0.23, 0.37);
                b.Rectangle(0, 0, s.Width * 0.4, s.Height);
                b.Fill();
                b.SetStrokeRgb(0.8, 0.2, 0.2);
                b.SetLineWidth(3);
                b.MoveTo(s.Width * 0.45, 5);
                b.LineTo(s.Width * 0.95, s.Height - 5);
                b.Stroke();
            });
        });
    });
}));

byte[] png = GradientPng(24, alpha: false);
byte[] pngAlpha = GradientPng(24, alpha: true);
Write("images", "JPEG and PNG pictures: contain, cover, stretch, fit width and height, and a PNG with alpha over a background", LayoutDocument.Compose(doc =>
{
    doc.Title("Images").Language("en");
    doc.Page(page =>
    {
        page.Size(PageSize.A5);
        page.Margin(30);
        page.Content().Column(col =>
        {
            col.Spacing(10);
            col.Item().Row(row =>
            {
                row.Spacing(8);
                row.RelativeItem().Height(70).Border(0.5).Image(jpeg).Contain().AltText("Contain");
                row.RelativeItem().Height(70).Border(0.5).Image(jpeg).Cover().AltText("Cover");
                row.RelativeItem().Height(70).Border(0.5).Image(jpeg).Stretch().AltText("Stretch");
            });
            col.Item().Width(90).Image(png).AltText("Fit width");
            col.Item().Height(60).Image(png).FitHeight().AltText("Fit height");
            col.Item().Background(LayoutColor.FromHex("#ffe9a8")).Padding(8).Width(80).Image(pngAlpha).AltText("PNG with alpha");
        });
    });
}));

var arabic = File.ReadAllBytes(Path.Combine(fonts, "NotoSansArabic-Subset-Regular.ttf"));
Write("rtl", "A right-to-left document: mirrored rows, start-aligned Arabic text, a mirrored list and table", LayoutDocument.Compose(doc =>
{
    doc.Title("RTL").Language("ar").RightToLeft();
    doc.Fonts.Add(arabic, "Noto Arabic");
    doc.Fonts.FallbackFamilies.Add("Noto Arabic");
    doc.Page(page =>
    {
        page.Size(PageSize.A5);
        page.Margin(30);
        string hello = "\u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645";
        page.Content().Column(col =>
        {
            col.Spacing(10);
            col.Item().Heading(1).Text(hello).FontSize(20);
            col.Item().Row(row =>
            {
                row.Spacing(8);
                row.ConstantItem(80).Background(paper).Padding(4).Text(hello).FontSize(11);
                row.RelativeItem().Border(0.5).Padding(4).Text("first item sits at the right").FontSize(10).Language("en");
            });
            col.Item().List(l =>
            {
                l.Numbered();
                l.Item().Text(hello).FontSize(11);
                l.Item().Text(hello + " " + hello).FontSize(11);
            });
            col.Item().Table(t =>
            {
                t.CellBorder(0.5);
                t.Columns(c => { c.Constant(60); c.Relative(); });
                t.Row(r => { r.Cell().Text("1").FontSize(10); r.Cell().Text(hello).FontSize(11); });
                t.Row(r => { r.Cell().Text("2").FontSize(10); r.Cell().Text(hello).FontSize(11); });
            });
        });
    });
}));

Write("scripts", "Latin, Hebrew, Devanagari, Thai and Japanese text in one document, with font fallback", LayoutDocument.Compose(doc =>
{
    doc.Title("Scripts").Language("en");
    foreach ((string file, string family) in new[]
    {
        ("NotoSansHebrew-Subset-Regular.ttf", "Noto Hebrew"), ("NotoSansDevanagari-Subset-Regular.ttf", "Noto Devanagari"),
        ("NotoSansThai-Subset-Regular.ttf", "Noto Thai"), ("NotoSansCJK-Subset.otf", "Noto CJK"),
    })
    {
        doc.Fonts.Add(File.ReadAllBytes(Path.Combine(fonts, file)), family);
        doc.Fonts.FallbackFamilies.Add(family);
    }

    doc.Page(page =>
    {
        page.Size(PageSize.A5);
        page.Margin(30);
        page.Content().Column(col =>
        {
            col.Spacing(10);
            col.Item().Text("Scripts").FontSize(18).Bold();
            col.Item().Text("Latin: the quick brown fox jumps over the lazy dog.").FontSize(11);
            col.Item().Text("Hebrew: \u05E9\u05DC\u05D5\u05DD \u05E2\u05D5\u05DC\u05DD 123 \u05D1\u05D3\u05D9\u05E7\u05D4").FontSize(11);
            col.Item().Text("Devanagari: \u0928\u092E\u0938\u094D\u0924\u0947 \u0915\u093F\u0924\u093E\u092C \u0939\u093F\u0928\u094D\u0926\u0940").FontSize(11);
            col.Item().Text("Thai: \u0E1B\u0E23\u0E30\u0E40\u0E17\u0E28\u0E44\u0E17\u0E22 \u0E01\u0E34 \u0E01\u0E35 \u0E01\u0E36").FontSize(11);
            col.Item().Text("Japanese: \u65E5\u672C\u3042\u3044\u3046\u3048\u304A\u304B\u304D\u304F\u3051\u3053").FontSize(11);
            col.Item().Text("Mixed: Hello \u05E9\u05DC\u05D5\u05DD \u0928\u092E\u0938\u094D\u0924\u0947 world").FontSize(11);
        });
    });
}));

Write("text-styles", "Spans in different weights, sizes, colours and decorations; superscripts; letter spacing; alignment; justification and indent", LayoutDocument.Compose(doc =>
{
    doc.Title("Text styles").Language("en");
    doc.Page(page =>
    {
        page.Size(PageSize.A5);
        page.Margin(30);
        page.Content().Column(col =>
        {
            col.Spacing(8);
            col.Item().Text(t =>
            {
                t.FontSize(11);
                t.Span("Regular, ");
                t.Span("bold, ").Bold();
                t.Span("italic, ").Italic();
                t.Span("underlined, ").Underline();
                t.Span("struck, ").Strike();
                t.Span("red, ").FontColor(Colors.Red);
                t.Span("big, ").FontSize(16);
                t.Span("serif, ").FontFamily("Serif");
                t.Span("mono").FontFamily("Mono");
                t.Span(" and E = mc");
                t.Span("2").Superscript();
                t.Span(" with H");
                t.Span("2").Subscript();
                t.Span("O.");
            });
            col.Item().Text("Letter spacing").FontSize(11).LetterSpacing(2);
            col.Item().Text("Left").FontSize(10).AlignLeft();
            col.Item().Text("Centre").FontSize(10).AlignCenter();
            col.Item().Text("Right").FontSize(10).AlignRight();
            col.Item().Text(Para(2)).FontSize(10).Justify().FirstLineIndent(18);
            col.Item().Text(Para(1)).FontSize(10).LineHeight(1.8);
            col.Item().Text("Helvetica, a standard font").FontSize(11).FontFamily("Helvetica");
        });
    });
}));

Write("sections", "Two sections: roman-numbered front matter with a first-page header, then a landscape body numbered from 1", LayoutDocument.Compose(doc =>
{
    doc.Title("Sections").Language("en");
    doc.Page(page =>
    {
        page.Size(PageSize.A5);
        page.Margin(30);
        page.NumberStyle(NumberingStyle.LowerRoman);
        page.FirstPageHeader().Text("Front matter \u2013 first page").FontSize(9).FontColor(Colors.Grey);
        page.Header().Text("Front matter").FontSize(9).FontColor(Colors.Grey);
        page.Footer().AlignCenter().Text(t => { t.FontSize(9); t.CurrentPageNumber(); });
        page.Content().Column(col =>
        {
            col.Item().Text("Preface").FontSize(18).Bold();
            col.Item().Text(Para(4)).FontSize(10).Justify();
            col.Item().PageBreak();
            col.Item().Text("Acknowledgements").FontSize(14).Bold();
            col.Item().Text(Para(1)).FontSize(10);
        });
    });
    doc.Page(page =>
    {
        page.Size(PageSize.A5);
        page.Landscape();
        page.Margin(30);
        page.StartNumber(1);
        page.Header().Text("Body").FontSize(9).FontColor(Colors.Grey);
        page.Footer().AlignCenter().Text(t =>
        {
            t.FontSize(9);
            t.Span("Page ");
            t.CurrentPageNumber();
            t.Span(" of ");
            t.SectionPages().ReservedDigits(1);
        });
        page.Content().Column(col =>
        {
            col.Item().Text("Chapter one").FontSize(18).Bold();
            col.Item().Text(Para(3)).FontSize(10).Justify();
        });
    });
}));

File.WriteAllText(Path.Combine(outDir, "index.csv"), index.ToString());
return 0;

static byte[] GradientPng(int size, bool alpha)
{
    int channels = alpha ? 4 : 3;
    var raw = new byte[(1 + (size * channels)) * size];
    for (int y = 0; y < size; y++)
    {
        int row = y * (1 + (size * channels));
        for (int x = 0; x < size; x++)
        {
            int p = row + 1 + (x * channels);
            raw[p] = (byte)(x * 255 / (size - 1));
            raw[p + 1] = (byte)(y * 255 / (size - 1));
            raw[p + 2] = 140;
            if (alpha)
            {
                raw[p + 3] = (byte)(255 - (x * 255 / (size - 1)));
            }
        }
    }

    using var z = new MemoryStream();
    using (var zs = new ZLibStream(z, CompressionLevel.SmallestSize, true))
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

static void Chunk(Stream s, string type, byte[] data)
{
    var len = new byte[4];
    BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
    s.Write(len);
    var body = new byte[4 + data.Length];
    Encoding.ASCII.GetBytes(type).CopyTo(body, 0);
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
