using System.Globalization;
using OnePdf.Layout.Pagination;

namespace OnePdf.Layout;

/// <summary>
/// A grid of cells with column widths, row and column spans, header and footer rows that repeat on every page the table spans,
/// collapsed borders, backgrounds and rows that split across pages.
/// </summary>
/// <remarks>
/// <para><b>Columns.</b> <see cref="Columns"/> gives each column a constant width, a relative weight or automatic sizing from the widest
/// and narrowest content of its cells. Without columns every column is automatic, and the count is the widest row.</para>
/// <para><b>Rows.</b> <see cref="HeaderRows"/> come first and <see cref="FooterRows"/> last; both are drawn on every page the table
/// occupies, so their height is taken off every page, and they never split. A page never holds the header alone: it needs at least the start
/// of one body row. <see cref="Rows"/> split across pages between their lines (cell contents continue on the next page) unless a row is
/// marked <see cref="TableRow.KeepTogether"/>. Rows joined by row spans form one unit that moves between pages whole.</para>
/// <para><b>Borders</b> are collapsed: two cells that meet share one line, centred on the grid line. The wider of the two borders wins; at equal
/// width solid beats dashed beats dotted; if those are equal too, the cell before (above or at the start side) wins. Lines at the outer edge
/// of the table, and at the top and bottom of each page's piece of it, are drawn inside the table.</para>
/// <para><b>Cost.</b> The grid and the column widths (from the header, footer and the body rows <see cref="MeasuredRows"/> chooses) are worked out once, when the table starts; each page then costs the rows on it plus the
/// header and footer rows, so a 10,000-row table takes time in proportion to its rows.</para>
/// </remarks>
public sealed class Table : Element
{
    /// <summary>The columns. May be empty: then all columns are automatic.</summary>
    public List<TableColumn> Columns { get; } = [];

    /// <summary>Rows repeated at the top of every page the table spans.</summary>
    public List<TableRow> HeaderRows { get; } = [];

    /// <summary>The body rows, which split across pages.</summary>
    public List<TableRow> Rows { get; } = [];

    /// <summary>Rows repeated at the bottom of the table piece on every page it spans.</summary>
    public List<TableRow> FooterRows { get; } = [];

    /// <summary>Space around cell content unless the cell sets its own. Default 4 points on every side.</summary>
    public Insets CellPadding { get; set; } = Insets.All(4);

    /// <summary>
    /// How many body rows (counting from the first) are measured to size automatic and relative columns; the header and footer rows are
    /// always measured. Null, the default, measures every row when some column is automatic (its width is the widest content), and the
    /// first 100 body rows when every column is constant or relative: there the measuring only keeps a relative column from being narrower
    /// than its longest word, which a sample of a hundred rows settles in practice. Measuring a very large table costs about as much as
    /// laying it out. With a limit, content that turns out wider in a later row wraps within its column (a word that is too wide spills
    /// over the cell's edge).
    /// </summary>
    public int? MeasuredRows { get; set; }

    /// <summary>Border of every cell edge unless the cell sets its own. Default none.</summary>
    public Border CellBorder { get; set; }

    /// <summary>
    /// Chooses the fill of each body row from its zero-based index, for striping: <c>i =&gt; i % 2 == 1 ? grey : null</c>. A row's own
    /// <see cref="TableRow.Background"/> wins. Header and footer rows are not asked. Called once per row when it is laid out.
    /// </summary>
    public Func<int, LayoutColor?>? RowBackground { get; set; }

    internal sealed class State(TableGrid grid, double[] widths, double[] x, double width)
    {
        public TableGrid Grid { get; } = grid;

        public double[] ColumnWidths { get; } = widths;

        /// <summary>Logical start of each column, with one more entry for the end.</summary>
        public double[] ColumnX { get; } = x;

        public double Width { get; } = width;
    }

    internal sealed record Resume(State State, int Row, object?[]? Inner, bool[]? Done);

    /// <summary>A row on one page. <paramref name="Continued"/>: it carries on a row that split on the page before; <paramref name="Continues"/>: it splits again.</summary>
    internal readonly record struct RowGeometry(int Row, double Y, double Height, LayoutColor? Background, bool Continued = false, bool Continues = false);

    /// <summary>A cell on one page. <paramref name="Skipped"/>: its content finished on an earlier page, so nothing of it is here.</summary>
    internal readonly record struct CellGeometry(int Slot, double X, double Y, double Width, double Height, Placed? Content, bool Skipped = false, bool Continued = false, bool Continues = false);

    /// <summary>What one page shows of a table.</summary>
    internal sealed class Piece(State state)
    {
        public State State { get; } = state;

        public List<RowGeometry> Rows { get; } = [];

        public List<CellGeometry> Cells { get; } = [];
    }

    private sealed class Builder
    {
        public Piece Piece { get; init; } = null!;

        public List<Placed> Children { get; } = [];
    }

    private readonly record struct BlockResult(FlowKind Kind, double Height, double Requested, object?[]? Inner, bool[]? Done);

    internal override WidthRange WidthsCore(LayoutContext ctx)
    {
        TableGrid g = TableGrid.Build(this, ctx);
        var min = new double[g.Cols];
        var max = new double[g.Cols];
        TableWidths.Measure(ctx, this, g, min, max);
        double lo = 0;
        double hi = 0;
        for (int i = 0; i < g.Cols; i++)
        {
            lo += min[i];
            hi += max[i];
        }

        return new WidthRange(lo, hi);
    }

    private State Prepare(LayoutContext ctx, in Constraint c)
    {
        TableGrid g = TableGrid.Build(this, ctx);
        var min = new double[g.Cols];
        var max = new double[g.Cols];
        TableWidths.Measure(ctx, this, g, min, max);
        double[] w = TableWidths.Resolve(this, g.Cols, min, max, c.WidthBounded ? c.Width : double.PositiveInfinity);
        var x = new double[g.Cols + 1];
        for (int i = 0; i < g.Cols; i++)
        {
            x[i + 1] = x[i] + w[i];
        }

        if (c.WidthBounded && x[g.Cols] > c.Width + 1e-6)
        {
            ctx.Note("The table is wider than the space offered: its constant columns and narrowest contents need " + x[g.Cols].ToString("0.##", CultureInfo.InvariantCulture) + " points.");
        }

        return new State(g, w, x, x[g.Cols]);
    }

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        var r = (Resume?)resume;
        State st = r?.State ?? Prepare(ctx, c);
        TableGrid g = st.Grid;
        var main = new Builder { Piece = new Piece(st) };
        int bodyEnd = g.HeaderCount + g.BodyCount;

        // Header, then the footer's height (the footer goes below the body, which is not placed yet).
        double headerH = 0;
        if (g.HeaderCount > 0)
        {
            BlockResult hr = PlaceRun(ctx, c, st, main, 0, g.HeaderCount, 0, c.Height, c.AtTop);
            if (hr.Kind != FlowKind.Complete)
            {
                return ctx.NoFit(c, st.Width, double.IsNaN(hr.Requested) ? 0 : hr.Requested, "The header rows do not fit the space offered");
            }

            headerH = hr.Height;
        }

        var foot = new Builder { Piece = new Piece(st) };
        double footerH = 0;
        if (g.Rows > bodyEnd)
        {
            BlockResult fr = PlaceRun(ctx, c, st, foot, bodyEnd, g.Rows, 0, c.Height - headerH, c.AtTop);
            if (fr.Kind != FlowKind.Complete)
            {
                return ctx.NoFit(c, st.Width, headerH + (double.IsNaN(fr.Requested) ? 0 : fr.Requested), "The footer rows do not fit the space offered");
            }

            footerH = fr.Height;
        }

        double bodyAvail = c.Height - headerH - footerH;
        double used = 0;
        int row = r?.Row ?? g.HeaderCount;
        object?[]? inner = r?.Inner;
        bool[]? done = r?.Done;
        bool partial = false;
        int blocks = 0;
        Resume? next = null;
        while (row < bodyEnd)
        {
            bool top = c.AtTop && blocks == 0;
            BlockResult br = g.BlockEnd[row] == row
                ? PlaceSingle(ctx, st, main, row, inner, done, headerH + used, bodyAvail - used, top, whole: false)
                : PlaceGroup(ctx, st, main, row, headerH + used, bodyAvail - used, top);
            if (br.Kind == FlowKind.NoFit)
            {
                if (blocks == 0)
                {
                    return ctx.NoFit(c, st.Width, headerH + footerH + (double.IsNaN(br.Requested) ? 0 : br.Requested), "The table row does not fit an empty page");
                }

                break;
            }

            used += br.Height;
            blocks++;
            if (br.Kind == FlowKind.Partial)
            {
                partial = true;
                next = new Resume(st, row, br.Inner, br.Done);
                break;
            }

            row = g.BlockEnd[row] + 1;
            inner = null;
            done = null;
        }

        if (!partial && row < bodyEnd)
        {
            next = new Resume(st, row, null, null);
        }

        // The footer sits right below what was placed.
        double footY = headerH + used;
        foreach (RowGeometry rg in foot.Piece.Rows)
        {
            main.Piece.Rows.Add(rg with { Y = rg.Y + footY });
        }

        foreach (CellGeometry cg in foot.Piece.Cells)
        {
            main.Piece.Cells.Add(cg with { Y = cg.Y + footY });
        }

        foreach (Placed child in foot.Children)
        {
            child.Y += footY;
            main.Children.Add(child);
        }

        double height = footY + footerH;
        var node = new Placed(this, 0, 0, st.Width, height) { Data = main.Piece };
        node.Children.AddRange(main.Children);
        return next is null ? FlowFit.Complete(height, node) : FlowFit.Partial(height, node, next);
    }

    private static void PushRow(LayoutContext ctx, TableGrid g, int row)
    {
        if (row < g.HeaderCount)
        {
            ctx.Push("Header Row ", row);
        }
        else if (row < g.HeaderCount + g.BodyCount)
        {
            ctx.Push("Row ", row - g.HeaderCount);
        }
        else
        {
            ctx.Push("Footer Row ", row - g.HeaderCount - g.BodyCount);
        }
    }

    private static double SpanWidth(State st, in TableSlot s) => st.ColumnX[s.Col + s.ColSpan] - st.ColumnX[s.Col];

    private static double CellX(State st, in TableSlot s, bool rtl)
    {
        double w = SpanWidth(st, s);
        return rtl ? st.Width - st.ColumnX[s.Col] - w : st.ColumnX[s.Col];
    }

    private LayoutColor? RowFill(TableGrid g, int row)
    {
        TableRow tr = g.RowObjects[row];
        if (tr.Background is { } own)
        {
            return own;
        }

        int bodyIndex = row - g.HeaderCount;
        return bodyIndex >= 0 && bodyIndex < g.BodyCount ? RowBackground?.Invoke(bodyIndex) : null;
    }

    // Header and footer: every row (or group of joined rows) whole.
    private BlockResult PlaceRun(LayoutContext ctx, in Constraint c, State st, Builder b, int from, int to, double y, double avail, bool atTop)
    {
        double used = 0;
        int row = from;
        while (row < to)
        {
            BlockResult br = st.Grid.BlockEnd[row] == row
                ? PlaceSingle(ctx, st, b, row, null, null, y + used, avail - used, atTop && used == 0, whole: true)
                : PlaceGroup(ctx, st, b, row, y + used, avail - used, atTop && used == 0);
            if (br.Kind != FlowKind.Complete)
            {
                return br with { Requested = used + (double.IsNaN(br.Requested) ? 0 : br.Requested) };
            }

            used += br.Height;
            row = st.Grid.BlockEnd[row] + 1;
        }

        return new BlockResult(FlowKind.Complete, used, 0, null, null);
    }

    // One row on its own. Cells may split; `inner` and `done` say where each cell resumes.
    private BlockResult PlaceSingle(LayoutContext ctx, State st, Builder b, int row, object?[]? inner, bool[]? done, double y, double avail, bool atTop, bool whole)
    {
        TableGrid g = st.Grid;
        bool rtl = ctx.Direction == LayoutDirection.RightToLeft;
        int s0 = g.RowStart[row];
        int n = g.RowStart[row + 1] - s0;
        var fits = new FlowFit[n];
        var insets = new CellInsets[n];
        double height = 0;
        bool anyPartial = false;
        PushRow(ctx, g, row);
        try
        {
            for (int i = 0; i < n; i++)
            {
                if (done is not null && done[i])
                {
                    continue;
                }

                TableSlot slot = g.Slots[s0 + i];
                insets[i] = g.InsetsOf(slot, rtl);
                double vertical = insets[i].Top + insets[i].Bottom;
                double cw = Math.Max(0, SpanWidth(st, slot) - insets[i].Left - insets[i].Right);
                ctx.Push("Cell ", slot.CellIndex);
                try
                {
                    fits[i] = slot.Cell.Content.Layout(ctx, new Constraint(cw, Math.Max(0, avail - vertical), atTop), inner?[i]);
                }
                finally
                {
                    ctx.Pop();
                }

                if (fits[i].Kind == FlowKind.NoFit)
                {
                    return new BlockResult(FlowKind.NoFit, 0, double.IsNaN(fits[i].Requested) ? double.NaN : fits[i].Requested + vertical, null, null);
                }

                if (fits[i].Kind == FlowKind.Partial)
                {
                    anyPartial = true;
                    if (whole)
                    {
                        return Fail(ctx, atTop, cw, avail - vertical, "A header or footer cell is taller than the space offered; these rows cannot split");
                    }
                }

                height = Math.Max(height, fits[i].Height + vertical);
            }

            if (anyPartial && g.RowObjects[row].KeepTogether)
            {
                if (!atTop)
                {
                    return new BlockResult(FlowKind.NoFit, 0, height, null, null);
                }

                ctx.Note("The row is taller than a page, so it is split although it should be kept together.");
            }
        }
        finally
        {
            ctx.Pop();
        }

        bool continued = inner is not null;
        var nextInner = anyPartial ? new object?[n] : null;
        var nextDone = anyPartial ? new bool[n] : null;
        LayoutColor? rowFill = RowFill(g, row);
        b.Piece.Rows.Add(new RowGeometry(row, y, height, rowFill, continued, anyPartial));
        for (int i = 0; i < n; i++)
        {
            TableSlot slot = g.Slots[s0 + i];
            double x = CellX(st, slot, rtl);
            double w = SpanWidth(st, slot);
            Placed? content = fits[i].Placed as Placed;
            if (content is not null)
            {
                double free = height - insets[i].Top - insets[i].Bottom - content.Height;
                content.X = x + insets[i].Left;
                content.Y = y + insets[i].Top + (anyPartial || continued ? 0 : AlignY(slot.Cell.VerticalAlignment, free));
                b.Children.Add(content);
            }

            bool skipped = done is not null && done[i];
            b.Piece.Cells.Add(new CellGeometry(s0 + i, x, y, w, height, content, skipped, continued && !skipped, fits[i].Kind == FlowKind.Partial));
            if (nextInner is not null)
            {
                bool wasDone = done is not null && done[i];
                nextDone![i] = wasDone || fits[i].Kind != FlowKind.Partial;
                nextInner[i] = fits[i].Kind == FlowKind.Partial ? fits[i].Resume : null;
            }
        }

        return new BlockResult(anyPartial ? FlowKind.Partial : FlowKind.Complete, height, 0, nextInner, nextDone);
    }

    // Rows joined by row spans: laid out together, never split.
    private BlockResult PlaceGroup(LayoutContext ctx, State st, Builder b, int first, double y, double avail, bool atTop)
    {
        TableGrid g = st.Grid;
        bool rtl = ctx.Direction == LayoutDirection.RightToLeft;
        int last = g.BlockEnd[first];
        int rows = last - first + 1;
        int s0 = g.RowStart[first];
        int s1 = g.RowStart[last + 1];
        var heights = new double[rows];
        var fits = new FlowFit[s1 - s0];
        var insets = new CellInsets[s1 - s0];
        for (int pass = 0; pass < 2; pass++)
        {
            for (int s = s0; s < s1; s++)
            {
                TableSlot slot = g.Slots[s];
                if ((slot.RowSpan == 1) != (pass == 0))
                {
                    continue;
                }

                insets[s - s0] = g.InsetsOf(slot, rtl);
                CellInsets ins = insets[s - s0];
                double vertical = ins.Top + ins.Bottom;
                double cw = Math.Max(0, SpanWidth(st, slot) - ins.Left - ins.Right);
                PushRow(ctx, g, slot.Top);
                ctx.Push("Cell ", slot.CellIndex);
                try
                {
                    FlowFit f = slot.Cell.Content.Layout(ctx, new Constraint(cw, Math.Max(0, avail - vertical), atTop), null);
                    fits[s - s0] = f;
                    if (f.Kind == FlowKind.NoFit)
                    {
                        return new BlockResult(FlowKind.NoFit, 0, double.IsNaN(f.Requested) ? double.NaN : f.Requested + vertical, null, null);
                    }

                    if (f.Kind == FlowKind.Partial)
                    {
                        return Fail(ctx, atTop, cw, avail - vertical, "Rows joined by a row span cannot be split across pages");
                    }

                    double need = f.Height + vertical;
                    if (slot.RowSpan == 1)
                    {
                        heights[slot.Top - first] = Math.Max(heights[slot.Top - first], need);
                    }
                    else
                    {
                        double have = 0;
                        for (int rr = slot.Top; rr < slot.Top + slot.RowSpan; rr++)
                        {
                            have += heights[rr - first];
                        }

                        if (need > have)
                        {
                            heights[slot.Top + slot.RowSpan - 1 - first] += need - have;
                        }
                    }
                }
                finally
                {
                    ctx.Pop();
                    ctx.Pop();
                }
            }
        }

        double total = 0;
        foreach (double h in heights)
        {
            total += h;
        }

        if (total > avail + 1e-9)
        {
            PushRow(ctx, g, first);
            try
            {
                return Fail(ctx, atTop, st.Width, avail, "Rows joined by a row span are taller than the space offered; they cannot be split", total);
            }
            finally
            {
                ctx.Pop();
            }
        }

        var rowY = new double[rows + 1];
        rowY[0] = y;
        for (int i = 0; i < rows; i++)
        {
            rowY[i + 1] = rowY[i] + heights[i];
            b.Piece.Rows.Add(new RowGeometry(first + i, rowY[i], heights[i], RowFill(g, first + i)));
        }

        for (int s = s0; s < s1; s++)
        {
            TableSlot slot = g.Slots[s];
            CellInsets ins = insets[s - s0];
            double x = CellX(st, slot, rtl);
            double top = rowY[slot.Top - first];
            double h = rowY[slot.Top + slot.RowSpan - first] - top;
            Placed? content = fits[s - s0].Placed as Placed;
            if (content is not null)
            {
                content.X = x + ins.Left;
                content.Y = top + ins.Top + AlignY(slot.Cell.VerticalAlignment, h - ins.Top - ins.Bottom - content.Height);
                b.Children.Add(content);
            }

            b.Piece.Cells.Add(new CellGeometry(s, x, top, SpanWidth(st, slot), h, content));
        }

        return new BlockResult(FlowKind.Complete, total, 0, null, null);
    }

    private static BlockResult Fail(LayoutContext ctx, bool atTop, double width, double height, string reason, double requested = double.NaN)
    {
        FlowFit f = ctx.NoFit(new Constraint(width, Math.Max(0, height), atTop), width, double.IsNaN(requested) ? height + 1 : requested, reason);
        return new BlockResult(FlowKind.NoFit, 0, f.Requested, null, null);
    }

    internal override void DrawCore(in DrawContext d, Placed p)
    {
        var piece = (Piece)p.Data!;
        TableGrid g = piece.State.Grid;
        double width = piece.State.Width;
        PdfContentBuilder b = d.B;
        Tagger tag = d.Ctx.Tag;
        bool artifact = tag.BeginArtifact(b);
        foreach (RowGeometry rg in piece.Rows)
        {
            if (rg.Background is { } fill)
            {
                b.SetFillRgb(fill.R, fill.G, fill.B);
                d.FillRect(0, rg.Y, width, rg.Height);
            }
        }

        foreach (CellGeometry cg in piece.Cells)
        {
            if (g.Slots[cg.Slot].Cell.Background is { } fill)
            {
                b.SetFillRgb(fill.R, fill.G, fill.B);
                d.FillRect(cg.X, cg.Y, cg.Width, cg.Height);
            }
        }

        tag.EndArtifact(b, artifact);
        DrawContent(d, p, piece);
        artifact = tag.BeginArtifact(b);
        DrawBorders(d, piece);
        tag.EndArtifact(b, artifact);
    }

    private static readonly object HeadKey = new();

    private static readonly object BodyKey = new();

    private static readonly object FootKey = new();

    private static readonly object RowKey = new();

    private sealed record CellKey(int Slot);

    // The structure: Table > THead, TBody, TFoot > TR > TH, TD (ISO 32000-2 14.8.4.3.4). Header rows are drawn again on every page the table
    // spans and footer rows on every page too; only the first occurrence of the header and the last of the footer are structure, the repeats
    // are artifacts, so a reader that follows the tree meets each row once.
    private static void DrawContent(in DrawContext d, Placed p, Piece piece)
    {
        Tagger tag = d.Ctx.Tag;
        PdfContentBuilder b = d.B;
        TableGrid g = piece.State.Grid;
        TagScope table = tag.Open(p.Element, p.Continued, p.Continues, "Table");
        List<RowGeometry> rows = piece.Rows;
        int cell = 0;
        int i = 0;
        while (i < rows.Count)
        {
            int kind = rows[i].Row < g.HeaderCount ? 0 : rows[i].Row < g.HeaderCount + g.BodyCount ? 1 : 2;
            int j = i;
            while (j + 1 < rows.Count && SectionOf(g, rows[j + 1].Row) == kind)
            {
                j++;
            }

            bool real = kind switch { 0 => !p.Continued, 1 => true, _ => !p.Continues };
            bool artifact = !real && tag.BeginArtifact(b);
            TagScope section = real
                ? kind switch
                {
                    0 => tag.Open(HeadKey, false, false, "THead"),
                    1 => tag.Open(BodyKey, p.Continued, p.Continues, "TBody"),
                    _ => tag.Open(FootKey, false, false, "TFoot"),
                }
                : default;
            for (int k = i; k <= j; k++)
            {
                RowGeometry rg = rows[k];
                TagScope row = tag.Open(RowKey, rg.Continued, rg.Continues, "TR");
                while (cell < piece.Cells.Count && g.Slots[piece.Cells[cell].Slot].Top == rg.Row)
                {
                    CellGeometry cg = piece.Cells[cell++];
                    if (cg.Skipped)
                    {
                        continue;
                    }

                    TableSlot slot = g.Slots[cg.Slot];
                    bool header = kind == 0 || slot.Cell.IsHeader;
                    PdfDictionary? attributes = null;
                    if (!cg.Continued && tag.Enabled)
                    {
                        if (header)
                        {
                            attributes = PdfStructureAttributes.TableHeader(kind == 0 ? "Column" : "Row", slot.ColSpan, slot.RowSpan);
                        }
                        else if (slot.ColSpan > 1 || slot.RowSpan > 1)
                        {
                            attributes = PdfStructureAttributes.Table(slot.ColSpan, slot.RowSpan);
                        }
                    }

                    object owner = cg.Continued || cg.Continues ? new CellKey(cg.Slot) : RowKey;
                    TagScope cellScope = tag.Open(owner, cg.Continued, cg.Continues, header ? "TH" : "TD", null, attributes);
                    if (cg.Content is { } content)
                    {
                        content.Element.Draw(d.At(content.X, content.Y), content);
                    }

                    tag.Close(cellScope);
                }

                tag.Close(row);
            }

            tag.Close(section);
            tag.EndArtifact(b, artifact);
            i = j + 1;
        }

        tag.Close(table);
    }

    private static int SectionOf(TableGrid g, int row) => row < g.HeaderCount ? 0 : row < g.HeaderCount + g.BodyCount ? 1 : 2;

    private static void DrawBorders(in DrawContext d, Piece piece)
    {
        TableGrid g = piece.State.Grid;
        State st = piece.State;
        bool rtl = d.Ctx.Direction == LayoutDirection.RightToLeft;
        List<RowGeometry> rows = piece.Rows;
        int i = 0;
        bool firstRun = true;
        while (i < rows.Count)
        {
            int j = i;
            while (j + 1 < rows.Count && rows[j + 1].Row == rows[j].Row + 1 && g.SectionEnd(rows[j + 1].Row) == g.SectionEnd(rows[i].Row))
            {
                j++;
            }

            for (int k = i; k <= j; k++)
            {
                if (k > i || firstRun)
                {
                    HorizontalLine(d, st, g, rows[k].Row, rows[k].Y, k == i ? 1 : 0, rtl);
                }

                for (int col = 0; col <= g.Cols; col++)
                {
                    Border v = g.VerticalEdge(rows[k].Row, col);
                    if (v.Width <= 0)
                    {
                        continue;
                    }

                    double x = rtl ? st.Width - st.ColumnX[col] : st.ColumnX[col];
                    int inward = col == 0 ? (rtl ? -1 : 1) : col == g.Cols ? (rtl ? 1 : -1) : 0;
                    Line(d, v, x, rows[k].Y, x, rows[k].Y + rows[k].Height, inward);
                }
            }

            bool lastRun = j == rows.Count - 1;
            HorizontalLine(d, st, g, rows[j].Row + 1, rows[j].Y + rows[j].Height, lastRun ? -1 : 0, rtl);
            firstRun = false;
            i = j + 1;
        }
    }

    // The horizontal line above grid row `edgeRow`, merged over neighbouring columns with the same border. `inward`: +1 draws it below
    // y (a piece's top), -1 above it (a piece's bottom), 0 centred.
    private static void HorizontalLine(in DrawContext d, State st, TableGrid g, int edgeRow, double y, int inward, bool rtl)
    {
        int col = 0;
        while (col < g.Cols)
        {
            Border bd = g.HorizontalEdge(edgeRow, col);
            int end = col + 1;
            while (end < g.Cols && g.HorizontalEdge(edgeRow, end) == bd)
            {
                end++;
            }

            if (bd.Width > 0)
            {
                double x0 = rtl ? st.Width - st.ColumnX[end] : st.ColumnX[col];
                double x1 = rtl ? st.Width - st.ColumnX[col] : st.ColumnX[end];
                Line(d, bd, x0, y, x1, y, inward);
            }

            col = end;
        }
    }

    // A line of the border's width centred on (x0,y0)-(x1,y1), which is horizontal or vertical. `inward` moves it half its width to the
    // side: for a horizontal line +1 is down and -1 up; for a vertical one +1 is right and -1 left.
    private static void Line(in DrawContext d, Border border, double x0, double y0, double x1, double y1, int inward)
    {
        PdfContentBuilder b = d.B;
        double w = border.Width;
        double shift = inward * w / 2;
        bool horizontal = y0 == y1;
        if (border.Style == BorderStyle.Solid)
        {
            b.SetFillRgb(border.Color.R, border.Color.G, border.Color.B);
            if (horizontal)
            {
                d.FillRect(x0, y0 + shift - (w / 2), x1 - x0, w);
            }
            else
            {
                d.FillRect(x0 + shift - (w / 2), y0, w, y1 - y0);
            }

            return;
        }

        b.Save();
        Box.ApplyStrokeStyle(b, border);
        if (horizontal)
        {
            b.MoveTo(d.X + x0, d.PdfY(y0 + shift));
            b.LineTo(d.X + x1, d.PdfY(y0 + shift));
        }
        else
        {
            b.MoveTo(d.X + x0 + shift, d.PdfY(y0));
            b.LineTo(d.X + x0 + shift, d.PdfY(y1));
        }

        b.Stroke();
        b.Restore();
    }
}
