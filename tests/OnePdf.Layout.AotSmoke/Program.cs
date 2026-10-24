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
        col.Item().Height(30).Rounded(6).Background(LayoutGradient.Linear(60, new LayoutColor(1, 0, 0), new LayoutColor(0, 0, 1))).Text("gradient");
        col.Item().Height(30).Background(LayoutGradient.Circle([new LayoutGradientStop(0, PdfGradientColor.Gray(1)), new LayoutGradientStop(0.5, PdfGradientColor.Gray(0.5)), new LayoutGradientStop(1, PdfGradientColor.Gray(0))])).Text("radial");
        col.Item().Rotate(30).Spacer(10, 40);
        col.Item().Canvas(20, s => { s.Builder.Rectangle(0, 0, s.Width, s.Height); s.Builder.Stroke(); });
        col.Item().Text(t =>
        {
            t.Span("inline ");
            t.InlineCanvas(8, 8, s => { s.Builder.Rectangle(0, 0, s.Width, s.Height); s.Builder.Fill(); }).AltText("smoke box").AlignMiddle();
            t.Span(" and ");
            t.Inline(b => b.Padding(2).Text("badge").FontSize(8)).AltText("smoke badge");
            t.Span(" item");
        });
    });
}));
using var ms = new MemoryStream();
doc.Save(ms, new PdfDocumentWriterOptions { LeaveOpen = true });
using PdfDocument back = PdfDocument.Open(ms.ToArray());
string text = string.Concat(back.ExtractText().Select(t => t.Text));
if (back.Pages.Count != 1 || !text.Contains("Layout smoke", StringComparison.Ordinal) || !text.Contains("footer", StringComparison.Ordinal)
    || !text.Contains("smoke box", StringComparison.Ordinal)
    || !text.Contains("smoke badge", StringComparison.Ordinal)
    || text.Contains('\uFFFC', StringComparison.Ordinal))
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

// Hyphenation: a narrow column breaks long words at pattern points, and joined extraction gives the words back whole.
const string hyphText = "Internationalization of hyphenation algorithms characterizes extraordinarily comprehensive documentation.";
LayoutDocument hyph = LayoutDocument.Compose(d => d.Page(p =>
{
    p.Size(130, 300);
    p.Margin(10);
    p.Content().Text(t =>
    {
        t.Language("en-US");
        t.Hyphenator(OnePdf.Hyphenation.Hyphenators.Auto());
        t.Span(hyphText);
    });
}));
using var hyphStream = new MemoryStream();
hyph.Save(hyphStream, new PdfDocumentWriterOptions { LeaveOpen = true });
using PdfDocument hyphBack = PdfDocument.Open(hyphStream.ToArray());
string hyphRaw = string.Concat(hyphBack.ExtractText().Select(t => t.Text));
string hyphJoined = string.Join(" ", hyphBack.ExtractText(new OnePdf.Text.PdfTextOptions { JoinHyphenatedWords = true }).Select(t => t.Text).SelectMany(s => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
if (!hyphRaw.Contains('­', StringComparison.Ordinal) && !hyphRaw.Contains('-', StringComparison.Ordinal) || hyphJoined != hyphText)
{
    Console.WriteLine("FAIL hyphenation: " + hyphRaw + " | " + hyphJoined);
    return 1;
}

// Table of contents: a two-section document whose table of roman-numbered front pages lists chapters of the body, with the numbers read back.
LayoutDocument tocDoc = LayoutDocument.Compose(d =>
{
    d.Page(p =>
    {
        p.Size(200, 200);
        p.NumberStyle(NumberingStyle.LowerRoman);
        p.Content().Column(c =>
        {
            c.Item().Text("Contents");
            c.Item().TableOfContents(t => t.Levels(1, 1));
        });
    });
    d.Page(p =>
    {
        p.Size(200, 200);
        p.StartNumber(1);
        p.Content().Column(c =>
        {
            c.Item().Heading(1).Text("Alpha");
            c.Item().PageBreak();
            c.Item().Heading(1).Text("Beta");
        });
    });
});
using var tocStream = new MemoryStream();
tocDoc.Save(tocStream, new PdfDocumentWriterOptions { LeaveOpen = true });
using PdfDocument tocBack = PdfDocument.Open(tocStream.ToArray());
string tocText = string.Join(" ", tocBack.Pages[0].ExtractText().Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
if (tocBack.Pages.Count != 3 || !tocText.Contains("Alpha", StringComparison.Ordinal) || !tocText.Contains(" 1 Beta", StringComparison.Ordinal) || !tocText.EndsWith(" 2", StringComparison.Ordinal))
{
    Console.WriteLine("FAIL table of contents: " + tocText);
    return 1;
}

// Knuth-Plass line breaking: one justified paragraph with a hyphenator, broken by total fit, reads back whole.
const string kpText = "Typesetting a paragraph well means looking at all of it at once, so that a loose line is not the price of a tight one that comes later in the same paragraph.";
LayoutDocument kp = LayoutDocument.Compose(d => d.Page(p =>
{
    p.Size(190, 300);
    p.Margin(10);
    p.Content().Text(t =>
    {
        t.Language("en-US");
        t.Justify();
        t.Hyphenator(OnePdf.Hyphenation.Hyphenators.Auto());
        t.OptimalLineBreaks(new KnuthPlassOptions { Looseness = 0 });
        t.Span(kpText);
    });
}));
using var kpStream = new MemoryStream();
kp.Save(kpStream, new PdfDocumentWriterOptions { LeaveOpen = true });
using PdfDocument kpBack = PdfDocument.Open(kpStream.ToArray());
string kpJoined = string.Join(" ", kpBack.ExtractText(new OnePdf.Text.PdfTextOptions { JoinHyphenatedWords = true }).Select(t => t.Text).SelectMany(s => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
if (kpJoined != kpText)
{
    Console.WriteLine("FAIL knuth-plass: " + kpJoined);
    return 1;
}

// Multi-column flow: one page in two columns, balanced, with a rule; extraction returns column 1 and then column 2.
LayoutDocument columns = LayoutDocument.Compose(d => d.Page(p =>
{
    p.Size(300, 300);
    p.Margin(20);
    p.Content().MultiColumn(c =>
    {
        c.Count(2).Gap(16).Rule(0.5);
        for (int i = 0; i < 6; i++)
        {
            c.Item().Text("Para" + i + " some words that wrap inside a narrow column of the page").FontSize(10);
        }
    });
}));
using var columnsStream = new MemoryStream();
columns.Save(columnsStream, new PdfDocumentWriterOptions { LeaveOpen = true });
using PdfDocument columnsBack = PdfDocument.Open(columnsStream.ToArray());
string columnsText = string.Join(" ", columnsBack.ExtractText().Select(t => t.Text).SelectMany(s => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
int columnsAt = -1;
for (int i = 0; i < 6; i++)
{
    int found = columnsText.IndexOf("Para" + i, columnsAt + 1, StringComparison.Ordinal);
    if (found <= columnsAt)
    {
        Console.WriteLine("FAIL columns: " + columnsText);
        return 1;
    }

    columnsAt = found;
}

if (columnsBack.Pages.Count != 1)
{
    Console.WriteLine("FAIL columns: " + columnsBack.Pages.Count + " pages");
    return 1;
}

// Dynamic values: a footer carries forward the sum of the amounts on the pages so far, read back from the last page.
LayoutDocument carried = LayoutDocument.Compose(d => d.Page(p =>
{
    p.Size(200, 120);
    p.Footer().DynamicText(v => "Carried " + v.Total("amount").ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
    p.Content().Column(c =>
    {
        for (int i = 1; i <= 30; i++)
        {
            c.Item().Accumulate("amount", i * 1.5m).Text("Row " + i);
        }
    });
}));
using var carriedStream = new MemoryStream();
carried.Save(carriedStream, new PdfDocumentWriterOptions { LeaveOpen = true });
using PdfDocument carriedBack = PdfDocument.Open(carriedStream.ToArray());
string carriedText = carriedBack.Pages[carriedBack.Pages.Count - 1].ExtractText().Text;
if (carriedBack.Pages.Count < 2 || !carriedText.Contains("Carried 697.50", StringComparison.Ordinal))
{
    Console.WriteLine("FAIL carried forward: " + carriedText);
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
