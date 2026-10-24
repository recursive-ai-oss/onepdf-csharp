using OnePdf.Layout.Pagination;

namespace OnePdf.Layout;

/// <summary>One column of a <see cref="Table"/>: how its width is decided.</summary>
public sealed class TableColumn
{
    /// <summary>How the width is decided. Default <see cref="RowItemWidth.Auto"/>: as wide as the widest content, narrower when the table is too wide.</summary>
    public RowItemWidth WidthKind { get; set; }

    /// <summary>Points for <see cref="RowItemWidth.Constant"/>, weight for <see cref="RowItemWidth.Relative"/>.</summary>
    public double Value { get; set; }

    /// <summary>A column as wide as its content.</summary>
    public static TableColumn Auto() => new();

    /// <summary>A column of a fixed width in points.</summary>
    public static TableColumn Constant(double width) => new() { WidthKind = RowItemWidth.Constant, Value = width };

    /// <summary>A column that shares the width left over by the others, in proportion to the weight.</summary>
    public static TableColumn Relative(double weight) => new() { WidthKind = RowItemWidth.Relative, Value = weight };
}

/// <summary>One cell of a <see cref="TableRow"/>: content plus how it sits in the grid and how it looks.</summary>
public sealed class TableCell
{
    /// <summary>Creates a cell holding <paramref name="content"/>.</summary>
    /// <exception cref="ArgumentNullException">The content is null.</exception>
    public TableCell(Element content) => Content = content ?? throw new ArgumentNullException(nameof(content));

    /// <summary>The element in the cell.</summary>
    public Element Content { get; }

    /// <summary>How many columns the cell covers. Default 1; a value past the last column is cut back to it.</summary>
    public int ColumnSpan { get; set; } = 1;

    /// <summary>
    /// How many rows the cell covers. Default 1. Rows joined by spans are never split across pages: they move to the next page together,
    /// and a joined group taller than a page raises <see cref="PdfLayoutException"/>. A value past the end of the header, body or footer is cut back to it.
    /// </summary>
    public int RowSpan { get; set; } = 1;

    /// <summary>Space between the cell's borders and its content (physical sides). The table's <see cref="Table.CellPadding"/> when null.</summary>
    public Insets? Padding { get; set; }

    /// <summary>Fill colour of the cell, over the row's. None when null.</summary>
    public LayoutColor? Background { get; set; }

    /// <summary>Top border. The table's <see cref="Table.CellBorder"/> when null; use a default <see cref="Border"/> for none.</summary>
    public Border? BorderTop { get; set; }

    /// <summary>Bottom border. The table's <see cref="Table.CellBorder"/> when null.</summary>
    public Border? BorderBottom { get; set; }

    /// <summary>Border at the start edge (left in left-to-right content). The table's <see cref="Table.CellBorder"/> when null.</summary>
    public Border? BorderStart { get; set; }

    /// <summary>Border at the end edge. The table's <see cref="Table.CellBorder"/> when null.</summary>
    public Border? BorderEnd { get; set; }

    /// <summary>
    /// Marks a body or footer cell as a header for its row (a row header, <c>TH</c> with scope <c>Row</c> in the structure tree). Every cell of a
    /// header row is a column header whatever this says. Default false.
    /// </summary>
    public bool IsHeader { get; set; }

    /// <summary>Where content shorter than its cell sits. Only a cell on one page can be aligned; a cell that splits starts at the top. Default <see cref="VerticalAlignment.Top"/>.</summary>
    public VerticalAlignment VerticalAlignment { get; set; }
}

/// <summary>A row of cells. Cells fill the columns from the start, skipping the places taken by row spans from rows above.</summary>
public sealed class TableRow
{
    /// <summary>The cells in logical order.</summary>
    public List<TableCell> Cells { get; } = [];

    /// <summary>When true, the row is moved to the next page instead of being split. A row taller than a page splits anyway, with a note. Default false: rows split across pages.</summary>
    public bool KeepTogether { get; set; }

    /// <summary>Fill colour behind the whole row. For body rows it replaces the table's <see cref="Table.RowBackground"/>.</summary>
    public LayoutColor? Background { get; set; }
}

/// <summary>A cell as placed in the grid.</summary>
internal readonly record struct TableSlot(TableCell Cell, TableRow Row, int Top, int Col, int ColSpan, int RowSpan, int CellIndex);

/// <summary>
/// The cells of a table arranged on a grid, built once per table layout and shared by all its pages. Header rows come first, then body
/// rows, then footer rows, numbered together.
/// </summary>
internal sealed class TableGrid
{
    public required int Cols { get; init; }

    public required int Rows { get; init; }

    public required int HeaderCount { get; init; }

    public required int BodyCount { get; init; }

    public required TableSlot[] Slots { get; init; }

    /// <summary>Index into <see cref="Slots"/> of the first slot whose top row is <c>r</c>; one more entry marks the end.</summary>
    public required int[] RowStart { get; init; }

    /// <summary>For each grid square, the slot covering it, or -1.</summary>
    public required int[] Occ { get; init; }

    /// <summary>For each row, the last row of the group of rows joined to it by row spans (itself when none).</summary>
    public required int[] BlockEnd { get; init; }

    public required Border DefaultBorder { get; init; }

    public required Insets DefaultPadding { get; init; }

    /// <summary>The row objects in grid order, including rows without cells.</summary>
    public required TableRow[] RowObjects { get; init; }

    public int SectionEnd(int row) => row < HeaderCount ? HeaderCount : row < HeaderCount + BodyCount ? HeaderCount + BodyCount : Rows;

    public Border BorderTop(in TableSlot s) => s.Cell.BorderTop ?? DefaultBorder;

    public Border BorderBottom(in TableSlot s) => s.Cell.BorderBottom ?? DefaultBorder;

    public Border BorderStart(in TableSlot s) => s.Cell.BorderStart ?? DefaultBorder;

    public Border BorderEnd(in TableSlot s) => s.Cell.BorderEnd ?? DefaultBorder;

    /// <summary>
    /// The border drawn on the horizontal line above row <paramref name="row"/> in grid column <paramref name="col"/> (below the last row
    /// when <paramref name="row"/> equals the row count). Where two cells meet, the wider border wins; at equal widths solid beats dashed
    /// beats dotted; at equal style the cell above wins. A line inside a cell that spans rows has no border.
    /// </summary>
    public Border HorizontalEdge(int row, int col)
    {
        int above = row > 0 ? Occ[((row - 1) * Cols) + col] : -1;
        int below = row < Rows ? Occ[(row * Cols) + col] : -1;
        if (above >= 0 && above == below)
        {
            return default;
        }

        return Winner(above >= 0 ? BorderBottom(Slots[above]) : default, below >= 0 ? BorderTop(Slots[below]) : default);
    }

    /// <summary>The border on the vertical line before logical column <paramref name="col"/> in row <paramref name="row"/> (after the last column when equal to the column count). Same conflict rule; the cell before wins a tie.</summary>
    public Border VerticalEdge(int row, int col)
    {
        int before = col > 0 ? Occ[(row * Cols) + col - 1] : -1;
        int after = col < Cols ? Occ[(row * Cols) + col] : -1;
        if (before >= 0 && before == after)
        {
            return default;
        }

        return Winner(before >= 0 ? BorderEnd(Slots[before]) : default, after >= 0 ? BorderStart(Slots[after]) : default);
    }

    private static Border Winner(Border first, Border second)
    {
        double a = Math.Max(0, first.Width);
        double b = Math.Max(0, second.Width);
        if (b > a)
        {
            return second;
        }

        if (a > b)
        {
            return first;
        }

        return a > 0 && second.Style < first.Style ? second : first;
    }

    /// <summary>The space between a cell's grid rectangle and its content: padding plus half of each shared border and all of an outer one.</summary>
    public CellInsets InsetsOf(in TableSlot s, bool rtl)
    {
        Insets pad = s.Cell.Padding ?? DefaultPadding;
        int r0 = s.Top;
        int r1 = s.Top + s.RowSpan - 1;
        int c0 = s.Col;
        int c1 = s.Col + s.ColSpan - 1;
        double top = 0;
        double bottom = 0;
        for (int c = c0; c <= c1; c++)
        {
            top = Math.Max(top, Math.Max(0, HorizontalEdge(r0, c).Width));
            bottom = Math.Max(bottom, Math.Max(0, HorizontalEdge(r1 + 1, c).Width));
        }

        double start = 0;
        double end = 0;
        for (int r = r0; r <= r1; r++)
        {
            start = Math.Max(start, Math.Max(0, VerticalEdge(r, c0).Width));
            end = Math.Max(end, Math.Max(0, VerticalEdge(r, c1 + 1).Width));
        }

        top = Math.Max(0, pad.Top) + (r0 == 0 ? top : top / 2);
        bottom = Math.Max(0, pad.Bottom) + (r1 + 1 == Rows ? bottom : bottom / 2);
        start = (c0 == 0 ? start : start / 2) + Math.Max(0, rtl ? pad.Right : pad.Left);
        end = (c1 + 1 == Cols ? end : end / 2) + Math.Max(0, rtl ? pad.Left : pad.Right);
        return rtl ? new CellInsets(top, bottom, end, start) : new CellInsets(top, bottom, start, end);
    }

    public static TableGrid Build(Table t, LayoutContext ctx)
    {
        int header = t.HeaderRows.Count;
        int body = t.Rows.Count;
        int rows = header + body + t.FooterRows.Count;
        int cols = t.Columns.Count;
        if (cols == 0)
        {
            for (int r = 0; r < rows; r++)
            {
                int sum = 0;
                foreach (TableCell cell in SourceRow(t, r, header, body).Cells)
                {
                    sum += Math.Max(1, cell.ColumnSpan);
                }

                cols = Math.Max(cols, sum);
            }
        }

        cols = Math.Max(1, cols);
        if ((long)rows * cols > 1L << 26)
        {
            throw new PdfLayoutException(ctx.PathText, double.NaN, double.NaN, double.NaN, double.NaN, "The table has more than 67 million grid squares");
        }

        var occ = new int[rows * cols];
        Array.Fill(occ, -1);
        var slots = new List<TableSlot>();
        var rowStart = new int[rows + 1];
        var reach = new int[rows];
        var rowObjects = new TableRow[rows];
        for (int r = 0; r < rows; r++)
        {
            rowStart[r] = slots.Count;
            reach[r] = r;
            int sectionEnd = r < header ? header : r < header + body ? header + body : rows;
            TableRow row = SourceRow(t, r, header, body);
            rowObjects[r] = row;
            int c = 0;
            for (int ci = 0; ci < row.Cells.Count; ci++)
            {
                TableCell cell = row.Cells[ci];
                while (c < cols && occ[(r * cols) + c] >= 0)
                {
                    c++;
                }

                if (c >= cols)
                {
                    throw new PdfLayoutException(RowPath(ctx, t, r, header, body), double.NaN, double.NaN, double.NaN, double.NaN, $"The row has more cells than the table has columns ({cols})");
                }

                int cs = Math.Clamp(cell.ColumnSpan, 1, cols - c);
                int rs = Math.Clamp(cell.RowSpan, 1, sectionEnd - r);
                int index = slots.Count;
                for (int rr = r; rr < r + rs; rr++)
                {
                    for (int cc = c; cc < c + cs; cc++)
                    {
                        if (occ[(rr * cols) + cc] >= 0)
                        {
                            throw new PdfLayoutException(RowPath(ctx, t, r, header, body) + $" > Cell {ci}", double.NaN, double.NaN, double.NaN, double.NaN, "The cell overlaps a cell that spans into this place");
                        }

                        occ[(rr * cols) + cc] = index;
                    }
                }

                slots.Add(new TableSlot(cell, row, r, c, cs, rs, ci));
                reach[r] = Math.Max(reach[r], r + rs - 1);
                c += cs;
            }
        }

        rowStart[rows] = slots.Count;

        var blockEnd = new int[rows];
        int start = 0;
        while (start < rows)
        {
            int end = reach[start];
            for (int i = start; i <= end; i++)
            {
                end = Math.Max(end, reach[i]);
            }

            for (int i = start; i <= end; i++)
            {
                blockEnd[i] = end;
            }

            start = end + 1;
        }

        return new TableGrid
        {
            Cols = cols,
            Rows = rows,
            HeaderCount = header,
            BodyCount = body,
            Slots = [.. slots],
            RowObjects = rowObjects,
            RowStart = rowStart,
            Occ = occ,
            BlockEnd = blockEnd,
            DefaultBorder = t.CellBorder,
            DefaultPadding = t.CellPadding,
        };
    }

    public static TableRow SourceRow(Table t, int row, int header, int body) =>
        row < header ? t.HeaderRows[row] : row < header + body ? t.Rows[row - header] : t.FooterRows[row - header - body];

    public static string RowPath(LayoutContext ctx, Table t, int row, int header, int body) =>
        ctx.PathText + " > " + (row < header ? $"Header Row {row}" : row < header + body ? $"Row {row - header}" : $"Footer Row {row - header - body}");
}

/// <summary>Physical insets of a cell's content inside its grid rectangle, in points.</summary>
internal readonly record struct CellInsets(double Top, double Bottom, double Left, double Right);

/// <summary>Column widths worked out from content and the width on offer.</summary>
internal static class TableWidths
{
    /// <summary>Body rows measured when every column is constant or relative and the table does not say.</summary>
    private const int SampledRows = 100;

    private static bool HasAutomaticColumn(Table t, int cols)
    {
        for (int c = 0; c < cols; c++)
        {
            if (c >= t.Columns.Count || t.Columns[c].WidthKind == RowItemWidth.Auto)
            {
                return true;
            }
        }

        return false;
    }

    public static void Measure(LayoutContext ctx, Table t, TableGrid g, double[] min, double[] max)
    {
        bool rtl = ctx.Direction == LayoutDirection.RightToLeft;
        int header = g.HeaderCount;
        int body = g.BodyCount;
        int limit = t.MeasuredRows ?? (HasAutomaticColumn(t, g.Cols) ? int.MaxValue : SampledRows);
        for (int pass = 0; pass < 2; pass++)
        {
            for (int s = 0; s < g.Slots.Length; s++)
            {
                TableSlot slot = g.Slots[s];
                if (slot.Top >= header && slot.Top < header + body && slot.Top - header >= limit)
                {
                    continue;
                }

                bool single = slot.ColSpan == 1;
                if (single != (pass == 0))
                {
                    continue;
                }

                bool allFixed = true;
                for (int c = slot.Col; c < slot.Col + slot.ColSpan; c++)
                {
                    allFixed &= t.Columns.Count > c && t.Columns[c].WidthKind == RowItemWidth.Constant;
                }

                if (allFixed)
                {
                    continue;
                }

                ctx.Push(slot.Top < header ? $"Header Row {slot.Top}" : slot.Top < header + body ? $"Row {slot.Top - header}" : $"Footer Row {slot.Top - header - body}");
                ctx.Push($"Cell {slot.CellIndex}");
                WidthRange w;
                try
                {
                    w = slot.Cell.Content.MeasureWidths(ctx);
                }
                finally
                {
                    ctx.Pop();
                    ctx.Pop();
                }

                CellInsets ins = g.InsetsOf(slot, rtl);
                double lo = w.Min + ins.Left + ins.Right;
                double hi = w.Max + ins.Left + ins.Right;
                if (single)
                {
                    min[slot.Col] = Math.Max(min[slot.Col], lo);
                    max[slot.Col] = Math.Max(max[slot.Col], hi);
                }
                else
                {
                    Spread(t, slot, lo, min);
                    Spread(t, slot, hi, max);
                }
            }
        }

        for (int c = 0; c < g.Cols; c++)
        {
            if (t.Columns.Count > c && t.Columns[c].WidthKind == RowItemWidth.Constant)
            {
                min[c] = max[c] = Math.Max(0, t.Columns[c].Value);
            }

            max[c] = Math.Max(max[c], min[c]);
        }
    }

    // A cell over several columns that needs more than they have asks the shortfall of the columns that can still grow, evenly.
    private static void Spread(Table t, TableSlot slot, double need, double[] sizes)
    {
        double have = 0;
        int growable = 0;
        for (int c = slot.Col; c < slot.Col + slot.ColSpan; c++)
        {
            have += sizes[c];
            if (!(t.Columns.Count > c && t.Columns[c].WidthKind == RowItemWidth.Constant))
            {
                growable++;
            }
        }

        if (need <= have || growable == 0)
        {
            return;
        }

        double share = (need - have) / growable;
        for (int c = slot.Col; c < slot.Col + slot.ColSpan; c++)
        {
            if (!(t.Columns.Count > c && t.Columns[c].WidthKind == RowItemWidth.Constant))
            {
                sizes[c] += share;
            }
        }
    }

    /// <summary>
    /// Column widths for a table in <paramref name="available"/> points (infinity: natural widths). Constant columns keep their width. Automatic
    /// columns get their widest content when there is room, and are squeezed towards their narrowest content when there is not.
    /// Relative columns share what the automatic columns leave, in proportion to their weights, but never get less than their narrowest
    /// content. When even the narrowest contents do not fit, every column but the constant ones is scaled down and words may spill.
    /// </summary>
    public static double[] Resolve(Table t, int cols, double[] min, double[] max, double available)
    {
        var w = new double[cols];
        bool Fixed(int c) => t.Columns.Count > c && t.Columns[c].WidthKind == RowItemWidth.Constant;
        bool Rel(int c) => t.Columns.Count > c && t.Columns[c].WidthKind == RowItemWidth.Relative;
        double fixedSum = 0;
        for (int c = 0; c < cols; c++)
        {
            if (Fixed(c))
            {
                w[c] = min[c];
                fixedSum += w[c];
            }
        }

        if (!double.IsFinite(available))
        {
            for (int c = 0; c < cols; c++)
            {
                if (!Fixed(c))
                {
                    w[c] = max[c];
                }
            }

            return w;
        }

        double free = Math.Max(0, available - fixedSum);
        double autoMax = 0;
        double autoMin = 0;
        double relMin = 0;
        double weights = 0;
        for (int c = 0; c < cols; c++)
        {
            if (Fixed(c))
            {
                continue;
            }

            if (Rel(c))
            {
                relMin += min[c];
                weights += Math.Max(0, t.Columns[c].Value);
            }
            else
            {
                autoMax += max[c];
                autoMin += min[c];
            }
        }

        if (free < autoMin + relMin)
        {
            double scale = autoMin + relMin > 0 ? free / (autoMin + relMin) : 0;
            for (int c = 0; c < cols; c++)
            {
                if (!Fixed(c))
                {
                    w[c] = min[c] * scale;
                }
            }

            return w;
        }

        bool anyRel = false;
        for (int c = 0; c < cols; c++)
        {
            anyRel |= Rel(c);
        }

        if (!anyRel)
        {
            double grow = Math.Min(free - autoMin, autoMax - autoMin);
            double span = autoMax - autoMin;
            for (int c = 0; c < cols; c++)
            {
                if (!Fixed(c))
                {
                    w[c] = min[c] + (span > 0 ? (max[c] - min[c]) * grow / span : 0);
                }
            }

            return w;
        }

        double space = free - autoMax;
        if (space >= relMin)
        {
            for (int c = 0; c < cols; c++)
            {
                if (!Fixed(c) && !Rel(c))
                {
                    w[c] = max[c];
                }
            }
        }
        else
        {
            // The relative columns get at least their narrowest content; the automatic ones give way down to theirs.
            double left = free - relMin - autoMin;
            double span = autoMax - autoMin;
            for (int c = 0; c < cols; c++)
            {
                if (!Fixed(c) && !Rel(c))
                {
                    w[c] = min[c] + (span > 0 ? (max[c] - min[c]) * left / span : 0);
                }
            }

            space = relMin;
        }

        // Share `space` among relative columns by weight; one that would get less than its minimum is pinned there and the rest re-shared.
        var pinned = new bool[cols];
        double remaining = space;
        for (int round = 0; round <= cols; round++)
        {
            double totalWeight = 0;
            int active = 0;
            for (int c = 0; c < cols; c++)
            {
                if (Rel(c) && !pinned[c])
                {
                    totalWeight += Math.Max(0, t.Columns[c].Value);
                    active++;
                }
            }

            bool changed = false;
            for (int c = 0; c < cols && active > 0; c++)
            {
                if (!Rel(c) || pinned[c])
                {
                    continue;
                }

                double share = totalWeight > 0 ? remaining * Math.Max(0, t.Columns[c].Value) / totalWeight : remaining / active;
                if (share < min[c])
                {
                    pinned[c] = true;
                    w[c] = min[c];
                    remaining -= min[c];
                    changed = true;
                    break;
                }
            }

            if (!changed)
            {
                for (int c = 0; c < cols; c++)
                {
                    if (Rel(c) && !pinned[c])
                    {
                        w[c] = totalWeight > 0 ? remaining * Math.Max(0, t.Columns[c].Value) / totalWeight : (active > 0 ? remaining / active : 0);
                    }
                }

                break;
            }
        }

        return w;
    }
}
