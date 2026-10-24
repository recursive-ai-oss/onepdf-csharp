using System;
using System.Collections.Generic;
using System.IO;
using iText.IO.Font;
using iText.Kernel.Colors;
using iText.Kernel.Pdf.Event;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Xobject;
using iText.IO.Image;
using iText.Layout;
using iText.Layout.Borders;
using iText.Layout.Element;
using iText.Layout.Properties;

namespace LayoutBench;


/// <summary>
/// iText layout (information row only; the package is AGPL-3.0 or commercial, see the README). Without its separate shaping add-on it
/// has no complex-script shaping, so the mixed-script row is drawn unshaped.
/// </summary>
internal sealed class ITextEngine : IEngine
{
    public string Name => "iText";

    private sealed class Fonts(PdfDocument pdf)
    {
        private readonly Dictionary<string, PdfFont> _cache = [];

        public PdfFont Get(string file)
        {
            if (!_cache.TryGetValue(file, out PdfFont? f))
            {
                f = PdfFontFactory.CreateFont(Content.FontBytes(file), PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED);
                _ = pdf;
                _cache[file] = f;
            }

            return f;
        }
    }

    // "Page X of Y" the way iText's documentation does it: the number so far is drawn per page, the total goes into a placeholder form
    // that is filled when the document is complete.
    private sealed class PageFooter(PdfFont font, PdfFormXObject total, string headerText) : AbstractPdfDocumentEventHandler
    {
        protected override void OnAcceptedEvent(AbstractPdfDocumentEvent e)
        {
            var ev = (PdfDocumentEvent)e;
            PdfPage page = ev.GetPage();
            int n = ev.GetDocument().GetPageNumber(page);
            Rectangle box = page.GetPageSize();
            var pc = new PdfCanvas(page.NewContentStreamAfter(), page.GetResources(), ev.GetDocument());
            pc.SaveState().SetFillColor(new DeviceGray(0.4f));
            pc.BeginText().SetFontAndSize(font, 9).MoveText((box.GetWidth() / 2) - 30, 30).ShowText("Page " + n + " of ").EndText();
            pc.AddXObjectAt(total, (box.GetWidth() / 2) + 10, 30 - 3);
            pc.RestoreState();
            if (headerText.Length > 0)
            {
                pc.SaveState().BeginText().SetFontAndSize(font, 9).MoveText(50, box.GetHeight() - 36).ShowText(headerText).EndText();
                pc.MoveTo(50, box.GetHeight() - 40).LineTo(box.GetWidth() - 50, box.GetHeight() - 40).SetLineWidth(0.5f).Stroke().RestoreState();
            }

            pc.Release();
        }
    }

    public long Render(Workload row, Stream output)
    {
        var writer = new PdfWriter(output);
        writer.SetCloseStream(false);
        using var pdf = new PdfDocument(writer);
        var fonts = new Fonts(pdf);
        PageSize size = row == Workload.Book500 ? PageSize.A5 : PageSize.A4;
        var doc = new iText.Layout.Document(pdf, size);
        switch (row)
        {
            case Workload.Invoice: Invoice(doc, fonts); break;
            case Workload.Report50: Report(pdf, doc, fonts); break;
            case Workload.Table10k: BigTable(doc, fonts); break;
            case Workload.Book500: Book(doc, fonts); break;
            case Workload.Mixed20: Mixed(doc, fonts); break;
            default: throw new ArgumentOutOfRangeException(nameof(row));
        }

        doc.Close();
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

    private static Paragraph P(string text, PdfFont font, double size, TextAlignment al = TextAlignment.LEFT) =>
        new Paragraph(text).SetFont(font).SetFontSize((float)size).SetTextAlignment(al).SetMargin(0).SetMultipliedLeading(1.15f);

    private static iText.Layout.Element.Table Tbl(Fonts fonts, string[] header, IEnumerable<string[]> rows, float[] weights, bool[] right, double size)
    {
        PdfFont reg = fonts.Get("LiberationSans-Regular.ttf");
        PdfFont bold = fonts.Get("LiberationSans-Bold.ttf");
        var t = new iText.Layout.Element.Table(UnitValue.CreatePercentArray(weights)).UseAllAvailableWidth();
        Cell Make(string s, PdfFont f, bool r, bool shade)
        {
            var c = new Cell().Add(P(s, f, size, r ? TextAlignment.RIGHT : TextAlignment.LEFT)).SetPadding(0).SetPaddingTop(3).SetPaddingBottom(3).SetPaddingLeft(4).SetPaddingRight(4)
                .SetBorder(Border.NO_BORDER).SetBorderBottom(new SolidBorder(ColorConstants.GRAY, 0.5f));
            if (shade)
            {
                c.SetBackgroundColor(new DeviceGray(0.94f));
            }

            return c;
        }

        for (int i = 0; i < header.Length; i++)
        {
            t.AddHeaderCell(Make(header[i], bold, right[i], true));
        }

        int n = 0;
        foreach (string[] r in rows)
        {
            bool shade = (n++ & 1) == 1;
            for (int i = 0; i < r.Length; i++)
            {
                t.AddCell(Make(r[i], reg, right[i], shade));
            }
        }

        return t;
    }

    private static void Invoice(iText.Layout.Document doc, Fonts fonts)
    {
        PdfFont reg = fonts.Get("LiberationSans-Regular.ttf");
        PdfFont bold = fonts.Get("LiberationSans-Bold.ttf");
        doc.SetMargins(40, 40, 40, 40);
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

        var head = new iText.Layout.Element.Table(UnitValue.CreatePercentArray(new float[] { 1, 1 })).UseAllAvailableWidth();
        head.AddCell(new Cell().SetBorder(Border.NO_BORDER).SetPadding(0)
            .Add(P("Northwind Supply Ltd.", bold, 18)).Add(P("12 Harbour Road, Lakeside 4021", reg, 10)).Add(P("accounts@northwind.example", reg, 10)));
        head.AddCell(new Cell().SetBorder(Border.NO_BORDER).SetPadding(0)
            .Add(P("INVOICE 2026-0417", bold, 18, TextAlignment.RIGHT)).Add(P("Issued 2026-10-01, due 2026-10-31", reg, 10, TextAlignment.RIGHT)));
        doc.Add(head);
        doc.Add(P("Bill to", bold, 10).SetMarginTop(10));
        doc.Add(P("Contoso Retail GmbH", reg, 10));
        doc.Add(P("Marktplatz 9, 80331 München", reg, 10));
        doc.Add(Tbl(fonts, ["Description", "Qty", "Unit price", "Total"], Rows(), [5, 1, 1.5f, 1.5f], [false, true, true, true], 10).SetMarginTop(10));
        doc.Add(P("Subtotal  " + Content.Money(total), reg, 10, TextAlignment.RIGHT).SetMarginTop(10));
        doc.Add(P("Tax 19%  " + Content.Money(total * 0.19m), reg, 10, TextAlignment.RIGHT));
        doc.Add(P("Total  " + Content.Money(total * 1.19m), bold, 12, TextAlignment.RIGHT));
        doc.Add(P("Payment within 30 days to account 0420 1177 3356. Thank you for your business.", reg, 9).SetMarginTop(10));
    }

    private static void Report(PdfDocument pdf, iText.Layout.Document doc, Fonts fonts)
    {
        PdfFont reg = fonts.Get("LiberationSans-Regular.ttf");
        PdfFont bold = fonts.Get("LiberationSans-Bold.ttf");
        doc.SetMargins(50, 50, 50, 50);
        var placeholder = new PdfFormXObject(new Rectangle(0, 0, 30, 12));
        pdf.AddEventHandler(PdfDocumentEvent.END_PAGE, new PageFooter(reg, placeholder, "Quarterly report"));
        var rng = new Content.Rng(11);
        for (int s = 0; s < Content.ReportSections; s++)
        {
            if (s > 0)
            {
                doc.Add(new AreaBreak(AreaBreakType.NEXT_PAGE));
            }

            doc.Add(P($"Section {s + 1}: " + Content.Words(rng, ["Operations", "Finance", "Outlook", "Supply", "Quality", "Risk"], 2), bold, 16).SetKeepWithNext(true).SetMarginBottom(12));
            doc.Add(P(Content.Paragraph(rng, 110), reg, 10, TextAlignment.JUSTIFIED).SetMarginBottom(12));
            var rows = new List<string[]>();
            for (int i = 0; i < 10; i++)
            {
                rows.Add(Content.TableRow((s * 10) + i));
            }

            doc.Add(Tbl(fonts, ["Id", "Item", "Amount", "State"], rows, [1, 4, 1.5f, 1.5f], [false, false, true, false], 9));
            doc.Add(new iText.Layout.Element.Image(ImageDataFactory.Create(Content.Picture)).SetWidth(240).SetMarginTop(12).SetMarginBottom(12));
            doc.Add(P(Content.Paragraph(rng, 60), reg, 10, TextAlignment.JUSTIFIED));
        }

        int pages = pdf.GetNumberOfPages();
        using var canvas = new iText.Layout.Canvas(placeholder, pdf);
        canvas.ShowTextAligned(P(pages.ToString(System.Globalization.CultureInfo.InvariantCulture), reg, 9), 0, 3, TextAlignment.LEFT);
    }

    private static void BigTable(iText.Layout.Document doc, Fonts fonts)
    {
        doc.SetMargins(40, 40, 40, 40);
        IEnumerable<string[]> All()
        {
            for (int i = 0; i < Content.TableRows; i++)
            {
                yield return Content.TableRow(i);
            }
        }

        doc.Add(Tbl(fonts, ["Id", "Item", "Amount", "State"], All(), [1, 4, 1.5f, 1.5f], [false, false, true, false], 10));
    }

    private static void Book(iText.Layout.Document doc, Fonts fonts)
    {
        PdfFont serif = fonts.Get("LiberationSerif-Regular.ttf");
        doc.SetMargins(50, 45, 50, 45);
        var rng = new Content.Rng(21);
        for (int i = 0; i < Content.BookParagraphs; i++)
        {
            doc.Add(P(Content.Paragraph(rng, 90 + rng.Next(60)), serif, 11, TextAlignment.JUSTIFIED).SetFirstLineIndent(22).SetMarginBottom(6));
        }
    }

    private static void Mixed(iText.Layout.Document doc, Fonts fonts)
    {
        PdfFont reg = fonts.Get("LiberationSans-Regular.ttf");
        PdfFont ar = fonts.Get("NotoSansArabic-Subset-Regular.ttf");
        PdfFont dv = fonts.Get("NotoSansDevanagari-Subset-Regular.ttf");
        doc.SetMargins(50, 50, 50, 50);
        var rng = new Content.Rng(31);
        for (int i = 0; i < Content.MixedParagraphs; i++)
        {
            Paragraph p = (i % 3) switch
            {
                0 => P(Content.Paragraph(rng, 70), reg, 11, TextAlignment.JUSTIFIED),
                1 => P(Content.ArabicParagraph(rng, 60), ar, 12),
                _ => P(Content.DevanagariParagraph(rng, 55), dv, 12),
            };
            doc.Add(p.SetMarginBottom(8));
        }
    }
}
