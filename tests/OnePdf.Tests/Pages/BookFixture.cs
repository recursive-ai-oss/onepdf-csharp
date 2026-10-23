using System.Text;

namespace OnePdf.Tests.Pages;

/// <summary>Small multi-page documents with bookmarks, links, named destinations, page labels and a form field, built object by object.</summary>
internal static class BookFixture
{
    /// <summary>
    /// A book of <paramref name="n"/> pages. Page i says "{prefix}{i}" in Helvetica, has a MediaBox and a font inherited from the page tree
    /// root, a link to the next page (explicit destination) and one to the next page by name ("{prefix}n{i}" ), a bookmark, and the first page has a text field.
    /// Labels: lower-case roman for the first two pages, then decimal from 5 with prefix "p-".
    /// </summary>
    public static byte[] Book(string prefix, int n, bool form = true)
    {
        int cat = 1, pages = 2;
        int Page(int i) => 3 + i;
        int Content(int i) => 3 + n + i;
        int Link(int i) => 3 + (2 * n) + i;
        int NLink(int i) => 3 + (3 * n) + i;
        int OutRoot = 3 + (4 * n);
        int Item(int i) => 4 + (4 * n) + i;
        int field = 4 + (5 * n);
        int helv = 5 + (5 * n);
        int size = helv + 1;

        var b = new PdfBuilder();
        var names = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            names.Append($"({prefix}n{i}) [{Page(i)} 0 R /Fit] ");
        }

        string acro = form ? $"/AcroForm << /Fields [{field} 0 R] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv {helv} 0 R >> >> /NeedAppearances true >>" : string.Empty;
        b.Obj(cat, $"<< /Type /Catalog /Pages {pages} 0 R /Outlines {OutRoot} 0 R /PageLabels << /Nums [0 << /S /r >> {Math.Min(2, n)} << /S /D /P (p-) /St 5 >>] >> /Names << /Dests << /Names [{names}] >> >> {acro} >>");
        var kids = string.Join(' ', Enumerable.Range(0, n).Select(i => $"{Page(i)} 0 R"));
        b.Obj(pages, $"<< /Type /Pages /Kids [{kids}] /Count {n} /MediaBox [0 0 300 300] /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>");
        for (int i = 0; i < n; i++)
        {
            string widget = form && i == 0 ? $" {field} 0 R" : string.Empty;
            b.Obj(Page(i), $"<< /Type /Page /Parent {pages} 0 R /Contents {Content(i)} 0 R /Annots [{Link(i)} 0 R {NLink(i)} 0 R{widget}] >>");
        }

        for (int i = 0; i < n; i++)
        {
            string text = $"BT /F1 24 Tf 20 200 Td ({prefix}{i}) Tj ET";
            b.Stream(Content(i), string.Empty, Encoding.ASCII.GetBytes(text));
        }

        for (int i = 0; i < n; i++)
        {
            b.Obj(Link(i), $"<< /Type /Annot /Subtype /Link /Rect [10 10 100 30] /Border [0 0 0] /Dest [{Page((i + 1) % n)} 0 R /Fit] >>");
        }

        for (int i = 0; i < n; i++)
        {
            b.Obj(NLink(i), $"<< /Type /Annot /Subtype /Link /Rect [110 10 200 30] /Border [0 0 0] /A << /S /GoTo /D ({prefix}n{(i + 1) % n}) >> >>");
        }

        b.Obj(OutRoot, $"<< /Type /Outlines /First {Item(0)} 0 R /Last {Item(n - 1)} 0 R /Count {n} >>");
        for (int i = 0; i < n; i++)
        {
            string prev = i > 0 ? $"/Prev {Item(i - 1)} 0 R" : string.Empty;
            string next = i + 1 < n ? $"/Next {Item(i + 1)} 0 R" : string.Empty;
            b.Obj(Item(i), $"<< /Title ({prefix} chapter {i}) /Parent {OutRoot} 0 R /Dest [{Page(i)} 0 R /Fit] {prev} {next} >>");
        }

        b.Obj(field, $"<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V ({prefix}-value) /Rect [10 100 150 120] /DA (/Helv 12 Tf 0 g) /P {Page(0)} 0 R /F 4 >>");
        b.Obj(helv, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        long x = b.XrefTable(size);
        b.Trailer($"/Size {size} /Root {cat} 0 R", x);
        return b.ToArray();
    }

    /// <summary>The text of page <paramref name="index"/> in reading order.</summary>
    public static string Text(PdfDocument doc, int index) =>
        doc.Pages[index].ExtractText().GetText(OnePdf.Text.PdfTextMode.ReadingOrder).Trim();

    public static string Label(PdfDocument doc, int index) => doc.PageLabels?.GetLabel(index) ?? (index + 1).ToString();

    /// <summary>The zero-based page a link, bookmark or action of <paramref name="doc"/> leads to (-1 when it leads nowhere).</summary>
    public static int Target(PdfDestination? d) => d is null ? -1 : d.PageIndex;
}
