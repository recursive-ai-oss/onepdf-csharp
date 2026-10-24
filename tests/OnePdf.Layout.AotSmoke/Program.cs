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

Console.WriteLine("ok");
return 0;
