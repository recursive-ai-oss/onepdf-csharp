using System;
using System.IO;
using System.Linq;
using OnePdf;
using OnePdf.Layout;

// Lays out a small document, reads it back and checks the text. With a page count as the first argument it instead writes that many
// one-line pages to a file given as the second argument (a memory probe for the streaming path).
if (args.Length >= 2 && int.TryParse(args[0], out int pages))
{
    var col = new Column();
    for (int i = 0; i < pages; i++)
    {
        col.Items.Add(new Text { Content = "Page " + i, Size = 14 });
        if (i < pages - 1)
        {
            col.Items.Add(new PageBreak());
        }
    }

    var big = new LayoutDocument();
    big.Pages.Add(new LayoutPage { Content = col });
    using FileStream file = File.Create(args[1]);
    big.Save(file);
    Console.WriteLine("ok " + pages);
    return 0;
}

var body = new Column { Spacing = 6 };
body.Items.Add(new Text { Content = "Layout smoke", Size = 18, FontName = "Helvetica-Bold" });
var row = new Row { Spacing = 8 };
row.Items.Add(RowItem.Constant(80, new Box { Padding = Insets.All(4), Background = new LayoutColor(0.9, 0.9, 1), Child = new Text { Content = "left" } }));
row.Items.Add(RowItem.Relative(1, new Box { Padding = Insets.All(4), BorderBottom = new Border(1, LayoutColor.Black, BorderStyle.Dashed), Child = new Text { Content = "right cell with a longer sentence that wraps over lines" } }));
body.Items.Add(row);
body.Items.Add(new Rotate { Degrees = 30, Child = new Spacer { Width = 40, Height = 10 } });
body.Items.Add(new Canvas { Height = 20, Paint = s => { s.Builder.Rectangle(0, 0, s.Width, s.Height); s.Builder.Stroke(); } });

var doc = new LayoutDocument();
doc.Pages.Add(new LayoutPage { Size = PageSize.A5, Content = body, Footer = new Text { Content = "footer", Size = 8 } });
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
var table = new Table { CellBorder = new Border(0.5, LayoutColor.Black) };
table.Columns.Add(TableColumn.Constant(40));
table.Columns.Add(TableColumn.Auto());
table.Columns.Add(TableColumn.Relative(1));
table.HeaderRows.Add(new TableRow { Cells = { new TableCell(new Text { Content = "Head" }) { ColumnSpan = 3 } } });
for (int i = 0; i < 60; i++)
{
    table.Rows.Add(new TableRow { Cells = { new TableCell(new Text { Content = "r" + i }), new TableCell(new Text { Content = "value " + i }), new TableCell(new Text { Content = "a few words in the last column" }) } });
}

table.FooterRows.Add(new TableRow { Cells = { new TableCell(new Text { Content = "Foot" }) { ColumnSpan = 3 } } });
var list = new ItemList { Kind = ListKind.Numbered, NumberStyle = NumberingStyle.LowerRoman };
list.Items.Add(new ListItem(new Text { Content = "first entry" }));
list.Items.Add(new ListItem(new Text { Content = "second entry" }));
var footer = new Row { Spacing = 3 };
footer.Items.Add(new RowItem(new Text { Content = "Page" }));
footer.Items.Add(new RowItem(new PageNumber()));
footer.Items.Add(new RowItem(new Text { Content = "of" }));
footer.Items.Add(new RowItem(new PageNumber { Kind = PageNumberKind.DocumentTotal }));
var structured = new LayoutDocument();
structured.Pages.Add(new LayoutPage
{
    Size = PageSize.A5,
    Footer = footer,
    Content = new Column
    {
        Items =
        {
            new Anchor { Id = "top", Child = new Bookmark { Title = "Start", Child = new Heading { Child = new Text { Content = "Structure" } } } },
            new Link { Destination = "top", Child = new Text { Content = "back to the top" } },
            list,
            table,
        },
    },
});
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
var tagged = new LayoutDocument { Title = "Smoke", Language = "ar", Direction = LayoutDirection.RightToLeft };
tagged.Options.Fonts = fonts;
var taggedRow = new Row();
taggedRow.Items.Add(RowItem.Constant(80, new Text { Content = arabic }));
taggedRow.Items.Add(RowItem.Constant(80, new Text { Content = "second", Direction = LayoutDirection.LeftToRight }));
tagged.Pages.Add(new LayoutPage { Size = PageSize.A5, Content = new Column { Items = { new Heading { Level = 2, Child = new Text { Content = "Tagged" } }, taggedRow } } });
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
