using System;
using System.Collections.Generic;
using System.IO;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace LayoutBench;


/// <summary>MigraDoc (PDFsharp-MigraDoc 6.x) through its document object model. It has no shaping or bidi, so the mixed-script row is drawn unshaped.</summary>
internal sealed class MigraDocEngine : IEngine
{
    public string Name => "MigraDoc";

    private sealed class Resolver : IFontResolver
    {
        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            if (familyName is Content.Arabic or Content.Devanagari or Content.Sans or Content.Serif)
            {
                return new FontResolverInfo(Content.FileFor(familyName, isBold, isItalic));
            }

            // Anything else (MigraDoc asks for a monospaced error font by name) gets the regular sans face.
            return new FontResolverInfo(Content.FileFor(Content.Sans, false, false));
        }

        public byte[]? GetFont(string faceName) => Content.FontBytes(faceName);
    }

    static MigraDocEngine()
    {
        GlobalFontSettings.FontResolver = new Resolver();
    }

    public long Render(Workload row, Stream output)
    {
        Document doc = row switch
        {
            Workload.Invoice => Invoice(),
            Workload.Report50 => Report(),
            Workload.Table10k => BigTable(),
            Workload.Book500 => Book(),
            Workload.Mixed20 => Mixed(),
            _ => throw new ArgumentOutOfRangeException(nameof(row)),
        };
        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        renderer.PdfDocument.Save(output, closeStream: false);
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

    private static Document NewDocument(PageFormat format, double left, double top, double right, double bottom, string font, double size)
    {
        var doc = new Document();
        Style normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = font;
        normal.Font.Size = Unit.FromPoint(size);
        Section sec = doc.AddSection();
        sec.PageSetup.PageFormat = format;
        sec.PageSetup.LeftMargin = Unit.FromPoint(left);
        sec.PageSetup.RightMargin = Unit.FromPoint(right);
        sec.PageSetup.TopMargin = Unit.FromPoint(top);
        sec.PageSetup.BottomMargin = Unit.FromPoint(bottom);
        sec.PageSetup.HeaderDistance = Unit.FromPoint(20);
        sec.PageSetup.FooterDistance = Unit.FromPoint(20);
        return doc;
    }

    private static Paragraph Para(Section sec, string text, double size, bool bold = false, ParagraphAlignment al = ParagraphAlignment.Left)
    {
        Paragraph p = sec.AddParagraph(text);
        p.Format.Font.Size = Unit.FromPoint(size);
        p.Format.Font.Bold = bold;
        p.Format.Alignment = al;
        return p;
    }

    private static Table BuildTable(Section sec, string[] header, IEnumerable<string[]> rows, double[] widths, bool[] right, double size)
    {
        Table t = sec.AddTable();
        t.LeftPadding = Unit.FromPoint(4);
        t.RightPadding = Unit.FromPoint(4);
        t.TopPadding = Unit.FromPoint(3);
        t.BottomPadding = Unit.FromPoint(3);
        t.Borders.Bottom.Width = 0.5;
        t.Borders.Bottom.Color = Colors.Gray;
        foreach (double w in widths)
        {
            t.AddColumn(Unit.FromPoint(w));
        }

        void Add(string[] cells, bool bold, bool shade, bool heading)
        {
            Row r = t.AddRow();
            r.HeadingFormat = heading;
            if (shade)
            {
                r.Shading.Color = Color.FromRgb(240, 240, 240);
            }

            for (int c = 0; c < cells.Length; c++)
            {
                Paragraph p = r.Cells[c].AddParagraph(cells[c]);
                p.Format.Font.Size = Unit.FromPoint(size);
                p.Format.Font.Bold = bold;
                p.Format.Alignment = right[c] ? ParagraphAlignment.Right : ParagraphAlignment.Left;
            }
        }

        Add(header, true, true, true);
        int n = 0;
        foreach (string[] r in rows)
        {
            Add(r, false, (n++ & 1) == 1, false);
        }

        return t;
    }

    private static double[] Widths(double total, double[] weights)
    {
        double sum = 0;
        foreach (double w in weights)
        {
            sum += w;
        }

        var o = new double[weights.Length];
        for (int i = 0; i < o.Length; i++)
        {
            o[i] = total * weights[i] / sum;
        }

        return o;
    }

    private static Document Invoice()
    {
        List<Content.Item> items = Content.InvoiceItems();
        decimal total = 0;
        foreach (Content.Item it in items)
        {
            total += it.Total;
        }

        IEnumerable<string[]> Rows()
        {
            foreach (Content.Item it in items)
            {
                yield return [it.Description, it.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture), Content.Money(it.UnitPrice), Content.Money(it.Total)];
            }
        }

        Document doc = NewDocument(PageFormat.A4, 40, 40, 40, 40, Content.Sans, 10);
        Section sec = doc.LastSection;
        double w = 595.28 - 80;
        // Two blocks side by side are a two-column table without borders.
        Table head = sec.AddTable();
        head.AddColumn(Unit.FromPoint(w / 2));
        head.AddColumn(Unit.FromPoint(w / 2));
        Row hr = head.AddRow();
        Paragraph a = hr.Cells[0].AddParagraph("Northwind Supply Ltd.");
        a.Format.Font.Size = 18;
        a.Format.Font.Bold = true;
        hr.Cells[0].AddParagraph("12 Harbour Road, Lakeside 4021");
        hr.Cells[0].AddParagraph("accounts@northwind.example");
        Paragraph b = hr.Cells[1].AddParagraph("INVOICE 2026-0417");
        b.Format.Font.Size = 18;
        b.Format.Font.Bold = true;
        b.Format.Alignment = ParagraphAlignment.Right;
        hr.Cells[1].AddParagraph("Issued 2026-10-01, due 2026-10-31").Format.Alignment = ParagraphAlignment.Right;
        Para(sec, "Bill to", 10, true).Format.SpaceBefore = 10;
        Para(sec, "Contoso Retail GmbH", 10);
        Para(sec, "Marktplatz 9, 80331 München", 10);
        sec.AddParagraph().Format.SpaceAfter = 6;
        BuildTable(sec, ["Description", "Qty", "Unit price", "Total"], Rows(), Widths(w, [5, 1, 1.5, 1.5]), [false, true, true, true], 10);
        Para(sec, "Subtotal  " + Content.Money(total), 10, al: ParagraphAlignment.Right).Format.SpaceBefore = 10;
        Para(sec, "Tax 19%  " + Content.Money(total * 0.19m), 10, al: ParagraphAlignment.Right);
        Para(sec, "Total  " + Content.Money(total * 1.19m), 12, true, ParagraphAlignment.Right);
        Para(sec, "Payment within 30 days to account 0420 1177 3356. Thank you for your business.", 9).Format.SpaceBefore = 10;
        return doc;
    }

    private static Document Report()
    {
        var rng = new Content.Rng(11);
        Document doc = NewDocument(PageFormat.A4, 50, 50, 50, 50, Content.Sans, 10);
        Section sec = doc.LastSection;
        Paragraph h = sec.Headers.Primary.AddParagraph("Quarterly report");
        h.Format.Font.Size = 9;
        h.Format.Borders.Bottom.Width = 0.5;
        Paragraph f = sec.Footers.Primary.AddParagraph();
        f.Format.Alignment = ParagraphAlignment.Center;
        f.Format.Font.Size = 9;
        f.Format.Font.Color = Color.FromRgb(102, 102, 102);
        f.AddText("Page ");
        f.AddPageField();
        f.AddText(" of ");
        f.AddNumPagesField();
        double w = 595.28 - 100;
        for (int s = 0; s < Content.ReportSections; s++)
        {
            if (s > 0)
            {
                sec.AddPageBreak();
            }

            Paragraph t = Para(sec, $"Section {s + 1}: " + Content.Words(rng, ["Operations", "Finance", "Outlook", "Supply", "Quality", "Risk"], 2), 16, true);
            t.Format.KeepWithNext = true;
            t.Format.SpaceAfter = 12;
            Para(sec, Content.Paragraph(rng, 110), 10, al: ParagraphAlignment.Justify).Format.SpaceAfter = 12;
            var rows = new List<string[]>();
            for (int i = 0; i < 10; i++)
            {
                rows.Add(Content.TableRow((s * 10) + i));
            }

            BuildTable(sec, ["Id", "Item", "Amount", "State"], rows, Widths(w, [1, 4, 1.5, 1.5]), [false, false, true, false], 9);
            MigraDoc.DocumentObjectModel.Shapes.Image img = sec.AddImage("base64:" + Convert.ToBase64String(Content.Picture));
            img.Width = Unit.FromPoint(240);
            Para(sec, Content.Paragraph(rng, 60), 10, al: ParagraphAlignment.Justify);
        }

        return doc;
    }

    private static Document BigTable()
    {
        IEnumerable<string[]> All()
        {
            for (int i = 0; i < Content.TableRows; i++)
            {
                yield return Content.TableRow(i);
            }
        }

        Document doc = NewDocument(PageFormat.A4, 40, 40, 40, 40, Content.Sans, 10);
        BuildTable(doc.LastSection, ["Id", "Item", "Amount", "State"], All(), Widths(595.28 - 80, [1, 4, 1.5, 1.5]), [false, false, true, false], 10);
        return doc;
    }

    private static Document Book()
    {
        var rng = new Content.Rng(21);
        Document doc = NewDocument(PageFormat.A5, 45, 50, 45, 50, Content.Serif, 11);
        Section sec = doc.LastSection;
        for (int i = 0; i < Content.BookParagraphs; i++)
        {
            Paragraph p = Para(sec, Content.Paragraph(rng, 90 + rng.Next(60)), 11, al: ParagraphAlignment.Justify);
            p.Format.FirstLineIndent = Unit.FromPoint(22);
            p.Format.SpaceAfter = 6;
        }

        return doc;
    }

    private static Document Mixed()
    {
        var rng = new Content.Rng(31);
        Document doc = NewDocument(PageFormat.A4, 50, 50, 50, 50, Content.Sans, 11);
        Section sec = doc.LastSection;
        for (int i = 0; i < Content.MixedParagraphs; i++)
        {
            Paragraph p;
            switch (i % 3)
            {
                case 0:
                    p = Para(sec, Content.Paragraph(rng, 70), 11, al: ParagraphAlignment.Justify);
                    break;
                case 1:
                    // No shaping or bidi: the characters are drawn in logical order with their nominal glyphs.
                    p = Para(sec, Content.ArabicParagraph(rng, 60), 12);
                    p.Format.Font.Name = Content.Arabic;
                    break;
                default:
                    p = Para(sec, Content.DevanagariParagraph(rng, 55), 12);
                    p.Format.Font.Name = Content.Devanagari;
                    break;
            }

            p.Format.SpaceAfter = 8;
        }

        return doc;
    }
}
