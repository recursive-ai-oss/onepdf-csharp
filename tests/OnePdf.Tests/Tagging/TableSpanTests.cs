using OnePdf.Tests.Creation;
using OnePdf.Tests.PdfA;
using OnePdf.Text;
using static OnePdf.Tests.Tagging.TagDocs;

namespace OnePdf.Tests.Tagging;

/// <summary>
/// Tables whose cells span columns or rows, tagged through the streaming writer and through the tagging plan, saved as PDF/UA-1.
/// The PDF/UA check and the veraPDF validator (black box) must agree: a table whose rows cover the same number of columns once
/// spans are counted is accepted, a ragged one is refused (ISO 14289-1 7.5).
/// </summary>
public class TableSpanTests
{
    internal sealed record Cell(string Role, int Col = 1, int Row = 1, string? Scope = null);

    // A row, optionally inside a THead/TBody/TFoot group (consecutive rows with the same group share the element), and the page it is drawn on.
    internal sealed record TableRow(string? Group, Cell[] Cells, int Page = 0);

    private static Cell TH(int col = 1, int row = 1, string scope = "Column") => new("TH", col, row, scope);

    private static Cell TD(int col = 1, int row = 1) => new("TD", col, row);

    private static TableRow R(params Cell[] cells) => new(null, cells);

    private static TableRow R(string group, params Cell[] cells) => new(group, cells);

    internal static readonly Dictionary<string, TableRow[]> Good = new()
    {
        ["plain"] = [R(TH(), TH()), R(TD(), TD())],
        ["colspan in the header row"] = [R(TH(2), TH(1)), R(TD(), TD(), TD())],
        ["colspan in the header row, narrow body"] = [R(TH(1), TH(1), TH(2)), R(TD(), TD(), TD(), TD())],
        ["colspan in body rows"] = [R(TH(), TH(), TH()), R(TD(2), TD()), R(TD(), TD(2))],
        ["rowspan at the start"] = [R(TH(), TH()), R(TD(1, 2), TD()), R(TD())],
        ["rowspan at the end"] = [R(TH(), TH()), R(TD(), TD(1, 2)), R(TD())],
        ["rowspan in the middle"] = [R(TH(), TH(), TH()), R(TD(), TD(1, 2), TD()), R(TD(), TD())],
        ["rowspan over three rows"] = [R(TH(), TH()), R(TD(), TD(1, 3)), R(TD()), R(TD())],
        ["two rowspans side by side"] = [R(TH(), TH(), TH()), R(TD(1, 2), TD(1, 2), TD()), R(TD())],
        ["rowspan in the header"] = [R(TH(1, 2, "Both"), TH(2)), R(TH(), TH()), R(TD(), TD(), TD())],
        ["colspan and rowspan on one cell"] = [R(TH(), TH(), TH()), R(TD(2, 2), TD()), R(TD())],
        ["both on a cell at the end"] = [R(TH(), TH(), TH()), R(TD(), TD(2, 2)), R(TD())],
        ["header scope row"] = [R(TH(1, 1, "Row"), TD(), TD()), R(TH(1, 2, "Row"), TD(), TD()), R(TD(), TD())],
        ["header scope both with spans"] = [R(TH(2, 2, "Both"), TH(1, 1, "Column")), R(TH(1, 1, "Column")), R(TD(), TD(), TD())],
        ["header scope column with colspan"] = [R(TH(2, 1, "Column"), TH(1, 1, "Column")), R(TD(), TD(), TD())],
        ["head body foot"] = [R("THead", TH(2), TH()), R("TBody", TD(), TD(), TD()), R("TBody", TD(1, 2), TD(2)), R("TBody", TD(2)), R("TFoot", TD(3))],
        ["rowspan across groups' rows"] = [R("THead", TH(), TH()), R("TBody", TD(), TD(1, 2)), R("TBody", TD())],
        ["rows shorter below a rowspan"] = [R(TH(), TH(), TH()), R(TD(), TD(), TD(1, 3)), R(TD(), TD()), R(TD(2))],
        ["split over two pages"] = [R(TH(2), TH()), R(TD(), TD(1, 2), TD()) with { Page = 0 }, R(TD(), TD()) with { Page = 1 }, R(TD(3)) with { Page = 1 }],
    };

    internal static readonly Dictionary<string, TableRow[]> Ragged = new()
    {
        ["one row short"] = [R(TH(), TH()), R(TD())],
        ["one row long"] = [R(TH(), TH()), R(TD(), TD(), TD())],
        ["colspan too wide"] = [R(TH(), TH(), TH()), R(TD(2), TD(2))],
        ["rowspan leaves a gap"] = [R(TH(), TH()), R(TD(1, 2), TD()), R(TD(), TD())],
    };

    public static IEnumerable<object[]> GoodCases() => Good.Keys.Select(static k => new object[] { k });

    public static IEnumerable<object[]> RaggedCases() => Ragged.Keys.Select(static k => new object[] { k });

    // ---- the streaming writer -------------------------------------------------------------------------------------------------

    private static byte[] ViaWriter(TableRow[] rows)
    {
        using var ms = new MemoryStream();
        using (var w = new PdfDocumentWriter(ms, new PdfDocumentWriterOptions { DocumentId = new byte[16], LeaveOpen = true, Language = "en-US" }))
        {
            w.SetInfo("Title", "Spanned tables");
            PdfCreatedFont font = w.AddFont(CreationSupport.RequireFont(PdfADocs.Serif));
            PdfStructureElement doc = w.BeginStructureElement("Document");
            PdfStreamPageBuilder p = w.BeginPage(300, 400);
            p.SetFont(font, 11);
            PdfStructureElement h = w.BeginStructureElement("H1");
            p.BeginTaggedContent(h);
            p.DrawText("Table", 20, 370);
            p.EndMarkedContent();
            w.EndStructureElement();

            PdfStructureElement table = w.BeginStructureElement("Table");
            string? group = null;
            int page = 0, n = 0;
            double y = 340;
            foreach (TableRow row in rows)
            {
                if (row.Page != page)
                {
                    if (group is not null)
                    {
                        // A row group may not continue on the next page here; reopen it there.
                        w.EndStructureElement();
                    }

                    p.Commit();
                    p = w.BeginPage(300, 400);
                    p.SetFont(font, 11);
                    page = row.Page;
                    y = 370;
                    if (group is not null)
                    {
                        w.BeginStructureElement(group);
                    }
                }

                if (row.Group != group)
                {
                    if (group is not null)
                    {
                        w.EndStructureElement();
                    }

                    group = row.Group;
                    if (group is not null)
                    {
                        w.BeginStructureElement(group);
                    }
                }

                w.BeginStructureElement("TR");
                int x = 20;
                foreach (Cell c in row.Cells)
                {
                    PdfDictionary? a = c.Role == "TH"
                        ? PdfStructureAttributes.TableHeader(c.Scope!, c.Col, c.Row)
                        : c.Col > 1 || c.Row > 1 ? PdfStructureAttributes.Table(c.Col, c.Row) : null;
                    PdfStructureElement cell = w.BeginStructureElement(c.Role, a is null ? null : new PdfStructureElementOptions { Attributes = a });
                    p.BeginTaggedContent(cell);
                    p.DrawText("c" + n++, x, y);
                    p.EndMarkedContent();
                    w.EndStructureElement();
                    x += 40;
                }

                w.EndStructureElement();
                y -= 14;
            }

            if (group is not null)
            {
                w.EndStructureElement();
            }

            w.EndStructureElement(); // Table
            p.Commit();
            w.EndStructureElement(); // Document
            w.Finish();
        }

        using PdfDocument d = PdfDocument.Open(ms.ToArray(), new PdfOpenOptions { AllowEditing = true });
        using var o = new MemoryStream();
        d.Save(o, new PdfSaveOptions { DocumentId = new byte[16], PdfUA = new PdfUAOptions { Language = "en-US" } });
        return o.ToArray();
    }

    // ---- the tagging plan -----------------------------------------------------------------------------------------------------

    private static byte[] ViaPlan(TableRow[] rows)
    {
        int pageCount = rows.Max(static r => r.Page) + 1;
        var contents = new string[pageCount];
        Array.Fill(contents, string.Empty);
        var labels = new List<(int Page, string Text)>();
        var y = new double[pageCount];
        Array.Fill(y, 700.0);
        int n = 0;
        foreach (TableRow row in rows)
        {
            foreach (Cell _ in row.Cells)
            {
                string label = "Cell" + (n++).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
                contents[row.Page] += T(72, y[row.Page], label);
                y[row.Page] -= 50;
                labels.Add((row.Page, label));
            }
        }

        byte[] source = Make(contents);
        byte[] tagged = Tag(source, (_, pages, plan) =>
        {
            PdfTagElement table = plan.Add(PdfTagRole.Table);
            PdfTagElement? grp = null;
            string? group = null;
            int i = 0;
            foreach (TableRow row in rows)
            {
                if (row.Group != group)
                {
                    group = row.Group;
                    grp = group is null ? null : table.Add(Enum.Parse<PdfTagRole>(group));
                }

                PdfTagElement tr = (grp ?? table).Add(PdfTagRole.TR);
                foreach (Cell c in row.Cells)
                {
                    (int pg, string text) = labels[i++];
                    PdfTagElement cell = tr.Add(Enum.Parse<PdfTagRole>(c.Role), PdfTagContent.Block(pages[pg], Blk(pages[pg], text + string.Empty)));
                    cell.ColSpan = c.Col;
                    cell.RowSpan = c.Row;
                    if (c.Scope is not null)
                    {
                        cell.Scope = Enum.Parse<PdfTagScope>(c.Scope);
                    }
                }
            }
        }, out _);
        return tagged;
    }

    // ---- the happy path -------------------------------------------------------------------------------------------------------

    // The happy path derives rows from line cell starts and never produces spans, so what it must do is stay regular.
    // Covered by the existing happy-path table tests; here only the plan and writer paths carry spans.

    // ---- checks ---------------------------------------------------------------------------------------------------------------

    // Only table-structure findings (ISO 14289-1 7.5): the plan documents use unembedded standard fonts, which the other rules report.
    private static List<string> TableFindings(byte[] pdf, bool vera)
    {
        var found = new List<string>();
        using PdfDocument d = PdfDocument.Open(pdf);
        foreach (PdfUAViolation v in d.CheckPdfUA())
        {
            if (v.Clause.StartsWith("7.5", StringComparison.Ordinal))
            {
                found.Add(v.ToString());
            }
        }

        if (vera && VeraPdf.Path is not null)
        {
            foreach (string f in VeraPdf.Validate(pdf, "ua1"))
            {
                if (IsTableRule(f))
                {
                    found.Add("veraPDF " + f);
                }
            }
        }

        return found;
    }

    // veraPDF files the regular-table rule under 7.2 (structure types); the header-cell rule is 7.5.
    private static bool IsTableRule(string failure) => failure.StartsWith("7.5-", StringComparison.Ordinal) || failure.Contains("same number of columns", StringComparison.Ordinal);

    private static int CheckFindings(byte[] pdf)
    {
        using PdfDocument d = PdfDocument.Open(pdf);
        return d.CheckPdfUA().Count(static v => v.Clause.StartsWith("7.5", StringComparison.Ordinal));
    }

    private static int VeraFindings(byte[] pdf) => VeraPdf.Validate(pdf, "ua1").Count(IsTableRule);

    [Theory]
    [MemberData(nameof(GoodCases))]
    public void WriterTableIsAccepted(string name)
    {
        byte[] pdf = ViaWriter(Good[name]);
        Assert.True(TableFindings(pdf, true).Count == 0, string.Join(" | ", TableFindings(pdf, true)));
        using PdfDocument d = PdfDocument.Open(pdf);
        Assert.Empty(d.CheckPdfUA());
    }

    [Theory]
    [MemberData(nameof(GoodCases))]
    public void PlanTableIsAccepted(string name)
    {
        byte[] pdf = ViaPlan(Good[name]);
        Assert.True(TableFindings(pdf, true).Count == 0, string.Join(" | ", TableFindings(pdf, true)));
    }

    [Theory]
    [MemberData(nameof(RaggedCases))]
    public void WriterRaggedTableIsRefusedByBoth(string name)
    {
        // The save refuses it, so build the file without the PDF/UA save to compare the two checkers.
        byte[] pdf = RaggedViaWriter(Ragged[name]);
        Assert.True(CheckFindings(pdf) > 0, "CheckPdfUA accepts " + name);
        if (VeraPdf.Path is not null)
        {
            Assert.True(VeraFindings(pdf) > 0, "veraPDF accepts " + name);
        }
    }

    [Theory]
    [MemberData(nameof(RaggedCases))]
    public void PlanRaggedTableIsRefused(string name)
    {
        byte[] pdf = ViaPlan(Ragged[name]);
        Assert.True(CheckFindings(pdf) > 0, "CheckPdfUA accepts " + name);
        if (VeraPdf.Path is not null)
        {
            Assert.True(VeraFindings(pdf) > 0, "veraPDF accepts " + name);
        }
    }

    [Fact]
    public void SavingARaggedTableAsPdfUAIsRefused()
    {
        var ex = Assert.Throws<PdfUAException>(() => ViaWriter(Ragged["one row short"]));
        Assert.Contains("same number of columns", ex.Message, StringComparison.Ordinal);
    }

    // The writer's file as written, saved without the PDF/UA refusal, so both validators can look at a ragged table.
    private static byte[] RaggedViaWriter(TableRow[] rows)
    {
        byte[] pdf;
        try
        {
            pdf = ViaWriter(rows);
        }
        catch (PdfUAException)
        {
            return ViaWriterPlain(rows);
        }

        return pdf;
    }

    private static byte[] ViaWriterPlain(TableRow[] rows)
    {
        using var ms = new MemoryStream();
        using (var w = new PdfDocumentWriter(ms, new PdfDocumentWriterOptions { DocumentId = new byte[16], LeaveOpen = true, Language = "en-US" }))
        {
            w.SetInfo("Title", "Ragged");
            PdfCreatedFont font = w.AddFont(CreationSupport.RequireFont(PdfADocs.Serif));
            w.BeginStructureElement("Document");
            PdfStreamPageBuilder p = w.BeginPage(300, 400);
            p.SetFont(font, 11);
            w.BeginStructureElement("Table");
            double y = 340;
            int n = 0;
            foreach (TableRow row in rows)
            {
                w.BeginStructureElement("TR");
                int x = 20;
                foreach (Cell c in row.Cells)
                {
                    PdfDictionary? a = c.Role == "TH"
                        ? PdfStructureAttributes.TableHeader(c.Scope!, c.Col, c.Row)
                        : c.Col > 1 || c.Row > 1 ? PdfStructureAttributes.Table(c.Col, c.Row) : null;
                    PdfStructureElement cell = w.BeginStructureElement(c.Role, a is null ? null : new PdfStructureElementOptions { Attributes = a });
                    p.BeginTaggedContent(cell);
                    p.DrawText("c" + n++, x, y);
                    p.EndMarkedContent();
                    w.EndStructureElement();
                    x += 40;
                }

                w.EndStructureElement();
                y -= 14;
            }

            w.EndStructureElement();
            p.Commit();
            w.EndStructureElement();
            w.Finish();
        }

        return ms.ToArray();
    }
}
