using System;
using System.Collections.Generic;
using System.IO;
using OnePdf;
using OnePdf.Layout;
using LText = OnePdf.Layout.Text;

namespace LayoutBench;

/// <summary>
/// The elements the workloads need from the layout package, built in one place: the table and the "Page X of Y" footer. The flags say
/// whether a row still uses a plain stand-in; both are the real elements now.
/// </summary>
internal static class OnePdfSeams
{
    /// <summary>True when tables are a column of rows of boxes instead of <see cref="Table"/> (no repeated header).</summary>
    public const bool TableIsStandIn = false;

    /// <summary>True when the footer has no page number fields.</summary>
    public const bool PageNumbersAreStandIn = false;

    public static Element Table(string[] header, IEnumerable<string[]> rows, double[] weights, bool[] rightAligned, double size, bool repeatHeader)
    {
        _ = repeatHeader; // the header row repeats on every page the table spans
        var table = new Table { CellPadding = Insets.Symmetric(4, 3), RowBackground = i => (i & 1) == 1 ? new LayoutColor(0.94, 0.94, 0.94) : null };
        foreach (double w in weights)
        {
            table.Columns.Add(TableColumn.Relative(w));
        }

        table.HeaderRows.Add(MakeRow(header, rightAligned, size, bold: true, shade: true));
        foreach (string[] r in rows)
        {
            table.Rows.Add(MakeRow(r, rightAligned, size, bold: false, shade: false));
        }

        return table;
    }

    private static TableRow MakeRow(string[] cells, bool[] rightAligned, double size, bool bold, bool shade)
    {
        var row = new TableRow();
        if (shade)
        {
            row.Background = new LayoutColor(0.94, 0.94, 0.94);
        }

        for (int c = 0; c < cells.Length; c++)
        {
            row.Cells.Add(new TableCell(new LText
            {
                Content = cells[c],
                Size = size,
                Weight = bold ? 700 : 400,
                Alignment = rightAligned[c] ? HorizontalAlignment.End : HorizontalAlignment.Start,
            })
            { BorderBottom = new Border(0.5, new LayoutColor(0.75, 0.75, 0.75)) });
        }

        return row;
    }

    /// <summary>Centred "Page N of M" in grey 9 pt.</summary>
    public static Element PageFooter()
    {
        var grey = new LayoutColor(0.4, 0.4, 0.4);
        var row = new Row { Spacing = 3 };
        row.Items.Add(new RowItem(new LText { Content = "Page", Size = 9, Color = grey }));
        row.Items.Add(new RowItem(new PageNumber { Size = 9, Color = grey }));
        row.Items.Add(new RowItem(new LText { Content = "of", Size = 9, Color = grey }));
        row.Items.Add(new RowItem(new PageNumber { Kind = PageNumberKind.DocumentTotal, Size = 9, Color = grey }));
        return new Column { Alignment = HorizontalAlignment.Center, Items = { row } };
    }
}

/// <summary>OnePDF Layout: element objects, laid out and written through the streaming writer.</summary>
internal sealed class OnePdfEngine(LineBreakMode lineBreaks = LineBreakMode.Greedy) : IEngine
{
    public string Name => lineBreaks == LineBreakMode.Greedy ? "OnePDF" : "OnePDF (Knuth-Plass)";

    internal static readonly TextFontRegistry Fonts = MakeFonts();

    private static TextFontRegistry MakeFonts()
    {
        TextFontRegistry r = TextFontRegistry.CreateDefault();
        r.Add(Content.FontBytes("NotoSansArabic-Subset-Regular.ttf"), Content.Arabic);
        r.Add(Content.FontBytes("NotoSansDevanagari-Subset-Regular.ttf"), Content.Devanagari);
        r.FallbackFamilies.Add(Content.Arabic);
        r.FallbackFamilies.Add(Content.Devanagari);
        return r;
    }

    private static readonly PdfDocumentWriterOptions SaveOptions = new() { DocumentId = new byte[16] };

    public long Render(Workload row, Stream output)
    {
        LayoutDocument doc = row switch
        {
            Workload.Invoice => Invoice(),
            Workload.Report50 => Report(),
            Workload.Table10k => BigTable(),
            Workload.Book500 => Book(),
            Workload.Mixed20 => Mixed(),
            _ => throw new ArgumentOutOfRangeException(nameof(row)),
        };
        doc.Options.Fonts = Fonts;
        doc.Options.LineBreakMode = lineBreaks;
        doc.Save(output, SaveOptions);
        return 0;
    }

    public void Throughput(int documents, Func<Stream> sink)
    {
        System.Threading.Tasks.Parallel.For(0, documents, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = documents }, i =>
        {
            using Stream s = sink();
            Render(Workload.Invoice, s);
        });
    }

    private static LText Txt(string s, double size = 10, bool bold = false, HorizontalAlignment al = HorizontalAlignment.Start, string? font = null, bool justify = false) =>
        new() { Content = s, Size = size, Weight = bold ? 700 : 400, Alignment = al, FontName = font, Justify = justify };

    private static LayoutDocument Invoice() => InvoiceDocument();

    internal static LayoutDocument InvoiceDocument()
    {
        List<Content.Item> items = Content.InvoiceItems();
        decimal total = 0;
        foreach (Content.Item it in items)
        {
            total += it.Total;
        }

        var body = new Column { Spacing = 10 };
        var head = new Row { Spacing = 20 };
        head.Items.Add(RowItem.Relative(1, new Column
        {
            Items = { Txt("Northwind Supply Ltd.", 18, true), Txt("12 Harbour Road, Lakeside 4021", 10), Txt("accounts@northwind.example", 10) },
        }));
        head.Items.Add(RowItem.Relative(1, new Column
        {
            Alignment = HorizontalAlignment.End,
            Items = { Txt("INVOICE 2026-0417", 18, true, HorizontalAlignment.End), Txt("Issued 2026-10-01, due 2026-10-31", 10, al: HorizontalAlignment.End) },
        }));
        body.Items.Add(head);
        body.Items.Add(new Column { Items = { Txt("Bill to", 10, true), Txt("Contoso Retail GmbH", 10), Txt("Marktplatz 9, 80331 München", 10) } });
        body.Items.Add(OnePdfSeams.Table(
            ["Description", "Qty", "Unit price", "Total"],
            Rows(items),
            [5, 1, 1.5, 1.5],
            [false, true, true, true],
            10,
            repeatHeader: false));
        body.Items.Add(new Column
        {
            Alignment = HorizontalAlignment.End,
            Items = { Txt("Subtotal  " + Content.Money(total), 10, al: HorizontalAlignment.End), Txt("Tax 19%  " + Content.Money(total * 0.19m), 10, al: HorizontalAlignment.End), Txt("Total  " + Content.Money(total * 1.19m), 12, true, HorizontalAlignment.End) },
        });
        body.Items.Add(Txt("Payment within 30 days to account 0420 1177 3356. Thank you for your business.", 9));
        var doc = new LayoutDocument();
        doc.Pages.Add(new LayoutPage { Size = PageSize.A4, Margin = Insets.All(40), Content = body });
        return doc;
    }

    private static IEnumerable<string[]> Rows(List<Content.Item> items)
    {
        foreach (Content.Item it in items)
        {
            yield return [it.Description, it.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture), Content.Money(it.UnitPrice), Content.Money(it.Total)];
        }
    }

    private static LayoutDocument Report()
    {
        var rng = new Content.Rng(11);
        var col = new Column { Spacing = 12 };
        // One element object used on every page: the picture is added to the file once.
        var picture = new Image { Png = Content.Picture, Fit = ImageFit.FitWidth };
        for (int s = 0; s < Content.ReportSections; s++)
        {
            if (s > 0)
            {
                col.Items.Add(new PageBreak());
            }

            col.Items.Add(new LText { Content = $"Section {s + 1}: " + Content.Words(rng, ["Operations", "Finance", "Outlook", "Supply", "Quality", "Risk"], 2), Size = 16, Weight = 700, KeepWithNext = true });
            col.Items.Add(Txt(Content.Paragraph(rng, 110), 10, justify: true));
            var tableRows = new List<string[]>();
            for (int i = 0; i < 10; i++)
            {
                tableRows.Add(Content.TableRow((s * 10) + i));
            }

            col.Items.Add(OnePdfSeams.Table(["Id", "Item", "Amount", "State"], tableRows, [1, 4, 1.5, 1.5], [false, false, true, false], 9, repeatHeader: true));
            col.Items.Add(picture);
            col.Items.Add(Txt(Content.Paragraph(rng, 60), 10, justify: true));
        }

        var doc = new LayoutDocument();
        doc.Pages.Add(new LayoutPage
        {
            Size = PageSize.A4,
            Margin = new Insets(50, 50, 50, 50),
            Header = new Box { Padding = Insets.Symmetric(0, 4), BorderBottom = new Border(0.5, LayoutColor.Black), Child = Txt("Quarterly report", 9) },
            Footer = OnePdfSeams.PageFooter(),
            Content = col,
        });
        return doc;
    }

    private static LayoutDocument BigTable()
    {
        IEnumerable<string[]> All()
        {
            for (int i = 0; i < Content.TableRows; i++)
            {
                yield return Content.TableRow(i);
            }
        }

        var doc = new LayoutDocument();
        doc.Pages.Add(new LayoutPage
        {
            Size = PageSize.A4,
            Margin = Insets.All(40),
            Content = OnePdfSeams.Table(["Id", "Item", "Amount", "State"], All(), [1, 4, 1.5, 1.5], [false, false, true, false], 10, repeatHeader: true),
        });
        return doc;
    }

    private static LayoutDocument Book()
    {
        var rng = new Content.Rng(21);
        var col = new Column { Spacing = 6 };
        for (int i = 0; i < Content.BookParagraphs; i++)
        {
            col.Items.Add(new LText { Content = Content.Paragraph(rng, 90 + rng.Next(60)), Size = 11, FontName = "Liberation Serif", Justify = true, FirstLineIndent = 22 });
        }

        var doc = new LayoutDocument();
        doc.Pages.Add(new LayoutPage { Size = PageSize.A5, Margin = new Insets(50, 45, 50, 45), Content = col });
        return doc;
    }

    private static LayoutDocument Mixed()
    {
        var rng = new Content.Rng(31);
        var col = new Column { Spacing = 8 };
        for (int i = 0; i < Content.MixedParagraphs; i++)
        {
            switch (i % 3)
            {
                case 0:
                    col.Items.Add(Txt(Content.Paragraph(rng, 70), 11, justify: true));
                    break;
                case 1:
                    col.Items.Add(new LText { Content = Content.ArabicParagraph(rng, 60), Size = 12, FontName = Content.Arabic, Alignment = HorizontalAlignment.Start, Direction = LayoutDirection.RightToLeft });
                    break;
                default:
                    col.Items.Add(new LText { Content = Content.DevanagariParagraph(rng, 55), Size = 12, FontName = Content.Devanagari });
                    break;
            }
        }

        var doc = new LayoutDocument();
        doc.Pages.Add(new LayoutPage { Size = PageSize.A4, Margin = Insets.All(50), Content = col });
        return doc;
    }
}
