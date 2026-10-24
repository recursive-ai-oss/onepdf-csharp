using System;
using System.IO;
using System.Linq;
using OnePdf;
using OnePdf.Layout;

// Lays out a small document, reads it back and checks the text. With a page count as the first argument it instead writes that many
// one-line pages to a file given as the second argument (a memory probe for the streaming path).
if (args.Length >= 2 && int.TryParse(args[0], out int pages))
{
    LayoutDocument big = LayoutDocument.Compose(d => d.Page(p => p.Content().Column(col =>
    {
        for (int i = 0; i < pages; i++)
        {
            col.Item().Text("Page " + i).FontSize(14);
            if (i < pages - 1)
            {
                col.Item().PageBreak();
            }
        }
    })));
    using FileStream file = File.Create(args[1]);
    big.Save(file);
    Console.WriteLine("ok " + pages);
    return 0;
}

LayoutDocument doc = LayoutDocument.Compose(d => d.Page(p =>
{
    p.Size(PageSize.A5);
    p.Footer().Text("footer").FontSize(8);
    p.Content().Column(col =>
    {
        col.Spacing(6);
        col.Item().Text("Layout smoke").FontSize(18).FontFamily("Helvetica-Bold");
        col.Item().Row(row =>
        {
            row.Spacing(8);
            row.ConstantItem(80).Padding(4).Background(new LayoutColor(0.9, 0.9, 1)).Text("left");
            row.RelativeItem().Padding(4).BorderBottom(1, null, BorderStyle.Dashed).Text("right cell with a longer sentence that wraps over lines");
        });
        col.Item().Rotate(30).Spacer(10, 40);
        col.Item().Canvas(20, s => { s.Builder.Rectangle(0, 0, s.Width, s.Height); s.Builder.Stroke(); });
    });
}));
using var ms = new MemoryStream();
doc.Save(ms, new PdfDocumentWriterOptions { LeaveOpen = true });
using PdfDocument back = PdfDocument.Open(ms.ToArray());
string text = string.Concat(back.ExtractText().Select(t => t.Text));
if (back.Pages.Count != 1 || !text.Contains("Layout smoke", StringComparison.Ordinal) || !text.Contains("footer", StringComparison.Ordinal))
{
    Console.WriteLine("FAIL: " + text);
    return 1;
}

// Document structure: a table with repeated header and footer rows and a span, a numbered list, "page N of M", a link and a bookmark.
LayoutDocument structured = LayoutDocument.Compose(d => d.Page(p =>
{
    p.Size(PageSize.A5);
    p.Footer().Text(t =>
    {
        t.Span("Page ");
        t.CurrentPageNumber();
        t.Span(" of ");
        t.TotalPages();
    });
    p.Content().Column(col =>
    {
        col.Item().Bookmark("Start").Anchor("top").Heading(1).Text("Structure");
        col.Item().LinkToAnchor("top").Text("back to the top");
        col.Item().List(l =>
        {
            l.Numbered(NumberingStyle.LowerRoman);
            l.Item().Text("first entry");
            l.Item().Text("second entry");
        });
        col.Item().Table(t =>
        {
            t.CellBorder(0.5);
            t.Columns(c => { c.Constant(40); c.Auto(); c.Relative(); });
            t.HeaderRow(r => r.Cell().Span(3).Text("Head"));
            for (int i = 0; i < 60; i++)
            {
                int n = i;
                t.Row(r =>
                {
                    r.Cell().Text("r" + n);
                    r.Cell().Text("value " + n);
                    r.Cell().Text("a few words in the last column");
                });
            }

            t.FooterRow(r => r.Cell().Span(3).Text("Foot"));
        });
    });
}));
using var structuredOut = new MemoryStream();
structured.Save(structuredOut, new PdfDocumentWriterOptions { LeaveOpen = true });
using PdfDocument structuredBack = PdfDocument.Open(structuredOut.ToArray());
string[] structuredText = [.. structuredBack.ExtractText().Select(t => t.Text)];
string lastPage = structuredText[^1].Replace("\n", " ", StringComparison.Ordinal);
if (structuredBack.Pages.Count < 2
    || !structuredText[1].Contains("Head", StringComparison.Ordinal)
    || !structuredText[1].Contains("Foot", StringComparison.Ordinal)
    || !lastPage.Contains("Page " + structuredBack.Pages.Count + " of", StringComparison.Ordinal)
    || !lastPage.TrimEnd().EndsWith(structuredBack.Pages.Count.ToString(), StringComparison.Ordinal)
    || structuredBack.Outline.Count != 1
    || structuredBack.Pages[0].Links.Count != 1)
{
    Console.WriteLine("FAIL structure: " + lastPage);
    return 1;
}

// Shaped paragraphs: one Latin (Liberation, justified) and one Arabic (right to left, joined) through the text renderer.
string? arabicPath = FindFont("NotoSansArabic-Subset-Regular.ttf");
if (arabicPath is null)
{
    Console.WriteLine("FAIL: testdata/fonts/NotoSansArabic-Subset-Regular.ttf not found; run from inside the repository");
    return 1;
}

var fonts = TextFontRegistry.CreateDefault();
fonts.Add(File.ReadAllBytes(arabicPath), "Noto Arabic");
fonts.FallbackFamilies.Add("Noto Arabic");
const string arabic = "\u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645";
using PdfDocument shaped = PdfDocument.CreateEmpty();
PdfPage shapedPage = shaped.InsertBlankPage(0, 300, 200);
using (PdfPageContentBuilder pb = shapedPage.BeginContent())
{
    var renderer = new TextRenderer(shaped);
    ParagraphLayout latin = new TextParagraph("Shaped Latin text that is justified over several short lines of the page.", new TextStyle { Size = 12 }) { Alignment = TextAlignment.Justify }.Shape(fonts).Layout(200);
    ParagraphLayout rtl = new TextParagraph(arabic, new TextStyle { Size = 14 }).Shape(fonts).Layout(200);
    renderer.Draw(pb, latin, 20, 180);
    renderer.Draw(pb, rtl, 20, 180 - latin.Height - 10);
    pb.Commit();
}

using var shapedOut = new MemoryStream();
shaped.Save(shapedOut);
using PdfDocument shapedBack = PdfDocument.Open(shapedOut.ToArray());
string shapedText = string.Concat(shapedBack.ExtractText().Select(t => t.Text));
if (!shapedText.Contains("Shaped Latin text", StringComparison.Ordinal) || !shapedText.Contains(arabic, StringComparison.Ordinal))
{
    Console.WriteLine("FAIL shaped: " + shapedText);
    return 1;
}

// Tagging: the structure tree is written by default, and a right-to-left document mirrors its rows.
LayoutDocument tagged = LayoutDocument.Compose(d =>
{
    d.Title("Smoke").Language("ar").RightToLeft();
    d.Fonts.Add(File.ReadAllBytes(arabicPath), "Noto Arabic");
    d.Fonts.FallbackFamilies.Add("Noto Arabic");
    d.Page(p =>
    {
        p.Size(PageSize.A5);
        p.Content().Column(col =>
        {
            col.Item().Heading(2).Text("Tagged");
            col.Item().Row(r =>
            {
                r.ConstantItem(80).Text(arabic);
                r.ConstantItem(80).LeftToRight().Text("second");
            });
        });
    });
});
using var taggedOut = new MemoryStream();
tagged.Save(taggedOut, new PdfDocumentWriterOptions { LeaveOpen = true });
using PdfDocument taggedBack = PdfDocument.Open(taggedOut.ToArray());
PdfDictionary? taggedRoot = taggedBack.StructureTreeRoot;
string taggedFlat = taggedBack.ExtractText().Select(t => t.Text).First();
if (taggedRoot is null
    || !taggedBack.Catalog!.GetDictionary("MarkInfo")!.GetBool("Marked")
    || !taggedFlat.Contains(arabic, StringComparison.Ordinal)
    || taggedFlat.IndexOf("Tagged", StringComparison.Ordinal) > taggedFlat.IndexOf(arabic, StringComparison.Ordinal))
{
    Console.WriteLine("FAIL tagging: " + taggedFlat);
    return 1;
}

Console.WriteLine("ok");
return 0;

static string? FindFont(string name)
{
    for (string? dir = Directory.GetCurrentDirectory(); dir is not null; dir = Path.GetDirectoryName(dir))
    {
        string candidate = Path.Combine(dir, "testdata", "fonts", name);
        if (File.Exists(candidate))
        {
            return candidate;
        }
    }

    return null;
}
