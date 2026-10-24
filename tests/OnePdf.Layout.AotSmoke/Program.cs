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
