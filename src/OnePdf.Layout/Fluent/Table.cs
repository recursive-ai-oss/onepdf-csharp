namespace OnePdf.Layout;

/// <summary>Defines the columns and adds the rows of a table. Given to the delegate of <see cref="Slot.Table(Action{TableDescriptor})"/>.</summary>
/// <remarks>
/// Without <see cref="Columns(Action{TableColumnsDescriptor})"/> every column is automatic and the column count is that of the widest row.
/// Cells of a header row are column headers in the structure tree; cells of other rows can be marked with <see cref="TableCellSlot.RowHeader"/>.
/// </remarks>
public sealed class TableDescriptor
{
    private readonly Table _table = new();
    private readonly List<(int Kind, TableRowDescriptor Row)> _rows = [];

    internal TableDescriptor()
    {
    }

    /// <summary>Defines the columns left to right (right to left in a right-to-left document).</summary>
    /// <param name="define">Adds one entry per column.</param>
    public TableDescriptor Columns(Action<TableColumnsDescriptor> define)
    {
        ArgumentNullException.ThrowIfNull(define);
        var d = new TableColumnsDescriptor(_table.Columns);
        define(d);
        return this;
    }

    /// <summary>Space around cell content unless the cell sets its own. Default 4 points on every side.</summary>
    public TableDescriptor CellPadding(double points)
    {
        _table.CellPadding = Insets.All(points);
        return this;
    }

    /// <summary>The border of every cell edge unless the cell sets its own. Neighbouring borders collapse into one line. Default none.</summary>
    public TableDescriptor CellBorder(double width, LayoutColor? color = null, BorderStyle style = BorderStyle.Solid)
    {
        _table.CellBorder = new Border(width, color ?? LayoutColor.Black, style);
        return this;
    }

    /// <summary>Chooses a fill for each body row from its zero-based index, for striping: <c>i =&gt; i % 2 == 1 ? Colors.LightGrey : null</c>. A row's own background wins.</summary>
    public TableDescriptor RowBackground(Func<int, LayoutColor?> choose)
    {
        _table.RowBackground = choose ?? throw new ArgumentNullException(nameof(choose));
        return this;
    }

    /// <summary>Limits how many body rows are measured to size automatic and relative columns. The default is every row when a column is automatic and the first 100 when all are constant or relative; a cap trades column fit for speed on very large tables (see <see cref="Table.MeasuredRows"/>).</summary>
    public TableDescriptor MeasuredRows(int count)
    {
        _table.MeasuredRows = count;
        return this;
    }

    /// <summary>Adds a header row, repeated at the top of every page the table spans. Header rows never split.</summary>
    public TableDescriptor HeaderRow(Action<TableRowDescriptor> define) => Add(0, define);

    /// <summary>Adds a body row. Body rows split across pages between lines of their cells unless <see cref="TableRowDescriptor.KeepTogether"/> is set.</summary>
    public TableDescriptor Row(Action<TableRowDescriptor> define) => Add(1, define);

    /// <summary>Adds a footer row, repeated at the bottom of every page the table spans. Footer rows never split.</summary>
    public TableDescriptor FooterRow(Action<TableRowDescriptor> define) => Add(2, define);

    private TableDescriptor Add(int kind, Action<TableRowDescriptor> define)
    {
        ArgumentNullException.ThrowIfNull(define);
        var r = new TableRowDescriptor();
        define(r);
        _rows.Add((kind, r.Seal()));
        return this;
    }

    internal Table Build()
    {
        foreach ((int kind, TableRowDescriptor row) in _rows)
        {
            (kind == 0 ? _table.HeaderRows : kind == 1 ? _table.Rows : _table.FooterRows).Add(row.Build());
        }

        return _table;
    }
}

/// <summary>Adds column definitions. Given to the delegate of <see cref="TableDescriptor.Columns(Action{TableColumnsDescriptor})"/>.</summary>
public sealed class TableColumnsDescriptor
{
    private readonly List<TableColumn> _columns;

    internal TableColumnsDescriptor(List<TableColumn> columns) => _columns = columns;

    /// <summary>A column as wide as its widest cell when there is room, narrower (down to its longest word) when not.</summary>
    public TableColumnsDescriptor Auto()
    {
        _columns.Add(TableColumn.Auto());
        return this;
    }

    /// <summary>A column of a fixed width in points.</summary>
    public TableColumnsDescriptor Constant(double width)
    {
        _columns.Add(TableColumn.Constant(width));
        return this;
    }

    /// <summary>A column that shares the width left over by weight (1 by default).</summary>
    public TableColumnsDescriptor Relative(double weight = 1)
    {
        _columns.Add(TableColumn.Relative(weight));
        return this;
    }
}

/// <summary>Adds the cells of one table row. Given to the delegates of <see cref="TableDescriptor.Row(Action{TableRowDescriptor})"/> and its header and footer forms.</summary>
public sealed class TableRowDescriptor
{
    private readonly List<TableCellSlot> _cells = [];
    private bool _keep;
    private LayoutColor? _background;
    private LayoutGradient? _gradient;

    internal TableRowDescriptor()
    {
    }

    /// <summary>Moves the row to the next page rather than splitting it (a row taller than a page splits anyway, with a note).</summary>
    public TableRowDescriptor KeepTogether()
    {
        _keep = true;
        return this;
    }

    /// <summary>Fills the row. A cell's own background wins.</summary>
    public TableRowDescriptor Background(LayoutColor color)
    {
        _background = color;
        _gradient = null;
        return this;
    }

    /// <summary>Fills the row with a gradient, scaled to the row (to each piece of a row that splits over pages). A cell's own background wins.</summary>
    public TableRowDescriptor Background(LayoutGradient gradient)
    {
        _gradient = gradient ?? throw new ArgumentNullException(nameof(gradient));
        return this;
    }

    /// <summary>Adds a cell after the ones before it (skipping places taken by row spans from above) and returns it. Set the cell's look first, then fill it like any slot.</summary>
    public TableCellSlot Cell()
    {
        var c = new TableCellSlot();
        _cells.Add(c);
        return c;
    }

    internal TableRowDescriptor Seal()
    {
        foreach (TableCellSlot c in _cells)
        {
            c.Seal();
        }

        return this;
    }

    internal TableRow Build()
    {
        var row = new TableRow { KeepTogether = _keep, Background = _background, BackgroundGradient = _gradient };
        foreach (TableCellSlot c in _cells)
        {
            row.Cells.Add(c.Build());
        }

        return row;
    }
}

/// <summary>
/// One table cell: a slot for its content that also says how the cell sits in the grid and looks. Set the cell's own properties
/// (<see cref="Span(int, int)"/>, <see cref="CellPadding(double)"/>, ...) first, then fill it like any slot.
/// </summary>
/// <example>
/// <code>
/// row.Cell().Span(2).CellBackground(Colors.LightGrey).Text("Spans two columns");
/// </code>
/// </example>
public sealed class TableCellSlot : Slot
{
    private int _columnSpan = 1;
    private int _rowSpan = 1;
    private Insets? _padding;
    private LayoutColor? _background;
    private LayoutGradient? _gradient;
    private Border? _top;
    private Border? _bottom;
    private Border? _start;
    private Border? _end;
    private bool _rowHeader;
    private VerticalAlignment _vertical;

    internal TableCellSlot()
        : base(null)
    {
    }

    /// <summary>Makes the cell cover several columns and rows (past the table's edge they are cut back).</summary>
    public TableCellSlot Span(int columns, int rows = 1)
    {
        _columnSpan = columns;
        _rowSpan = rows;
        return this;
    }

    /// <summary>Space between the cell's borders and its content, instead of the table's cell padding.</summary>
    public TableCellSlot CellPadding(double points)
    {
        _padding = Insets.All(points);
        return this;
    }

    /// <summary>Space between the cell's borders and its content, per physical side.</summary>
    public TableCellSlot CellPadding(double top, double right, double bottom, double left)
    {
        _padding = new Insets(top, right, bottom, left);
        return this;
    }

    /// <summary>Fills the cell.</summary>
    public TableCellSlot CellBackground(LayoutColor color)
    {
        _background = color;
        _gradient = null;
        return this;
    }

    /// <summary>Fills the cell with a gradient, scaled to the cell (to each piece of a cell that splits over pages).</summary>
    public TableCellSlot CellBackground(LayoutGradient gradient)
    {
        _gradient = gradient ?? throw new ArgumentNullException(nameof(gradient));
        return this;
    }

    /// <summary>A border on all four edges of the cell, instead of the table's cell border.</summary>
    public TableCellSlot CellBorder(double width, LayoutColor? color = null, BorderStyle style = BorderStyle.Solid)
    {
        var b = new Border(width, color ?? LayoutColor.Black, style);
        _top = b;
        _bottom = b;
        _start = b;
        _end = b;
        return this;
    }

    /// <summary>A border on the top edge of the cell.</summary>
    public TableCellSlot CellBorderTop(double width, LayoutColor? color = null, BorderStyle style = BorderStyle.Solid)
    {
        _top = new Border(width, color ?? LayoutColor.Black, style);
        return this;
    }

    /// <summary>A border on the bottom edge of the cell.</summary>
    public TableCellSlot CellBorderBottom(double width, LayoutColor? color = null, BorderStyle style = BorderStyle.Solid)
    {
        _bottom = new Border(width, color ?? LayoutColor.Black, style);
        return this;
    }

    /// <summary>A border on the start edge of the cell (left in left-to-right documents).</summary>
    public TableCellSlot CellBorderStart(double width, LayoutColor? color = null, BorderStyle style = BorderStyle.Solid)
    {
        _start = new Border(width, color ?? LayoutColor.Black, style);
        return this;
    }

    /// <summary>A border on the end edge of the cell.</summary>
    public TableCellSlot CellBorderEnd(double width, LayoutColor? color = null, BorderStyle style = BorderStyle.Solid)
    {
        _end = new Border(width, color ?? LayoutColor.Black, style);
        return this;
    }

    /// <summary>Places content shorter than the cell at the top (the default), middle or bottom. A cell that splits across pages starts at the top.</summary>
    public TableCellSlot CellAlign(VerticalAlignment alignment)
    {
        _vertical = alignment;
        return this;
    }

    /// <summary>Marks a body or footer cell as the header of its row (a row header in the structure tree). Cells of header rows are column headers already.</summary>
    public TableCellSlot RowHeader()
    {
        _rowHeader = true;
        return this;
    }

        internal TableCell Build() => new(Held ?? new Spacer())
    {
        ColumnSpan = _columnSpan,
        RowSpan = _rowSpan,
        Padding = _padding,
        Background = _background,
        BackgroundGradient = _gradient,
        BorderTop = _top,
        BorderBottom = _bottom,
        BorderStart = _start,
        BorderEnd = _end,
        IsHeader = _rowHeader,
        VerticalAlignment = _vertical,
    };
}
