namespace OnePdf.Layout;

public partial class Slot
{
    /// <summary>
    /// Pours its items through equal columns: down the first, then the next, then onto the next page. Paragraphs, lists, tables and pictures
    /// break between columns as they break between pages. The delegate adds items with <see cref="MultiColumnDescriptor.Item"/>.
    /// </summary>
    /// <param name="compose">Sets the number of columns, the gap and rule, and adds the items.</param>
    /// <example>
    /// <code>
    /// slot.MultiColumn(cols =>
    /// {
    ///     cols.Count(2).Gap(18).Rule(0.5, Colors.Grey);
    ///     cols.Item().Heading(2).Text("Section");
    ///     cols.Item().Text(longText).Justify();
    ///     cols.ColumnBreak();
    ///     cols.Item().Text("Starts the second column");
    /// });
    /// </code>
    /// </example>
    public void MultiColumn(Action<MultiColumnDescriptor> compose)
    {
        ArgumentNullException.ThrowIfNull(compose);
        var d = new MultiColumnDescriptor();
        compose(d);
        Put(d.Build());
    }
}

/// <summary>Sets up a multi-column flow and adds its items. Given to the delegate of <see cref="Slot.MultiColumn(Action{MultiColumnDescriptor})"/>.</summary>
public sealed class MultiColumnDescriptor
{
    private readonly List<(Slot? Slot, bool Break)> _items = [];
    private readonly MultiColumn _flow = new();

    internal MultiColumnDescriptor()
    {
    }

    /// <summary>The number of columns, 1 to <see cref="MultiColumn.MaxColumns"/>. Default 2.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The count is outside that range.</exception>
    public MultiColumnDescriptor Count(int columns)
    {
        _flow.Count = columns;
        return this;
    }

    /// <summary>Space between columns in points. Default 12.</summary>
    public MultiColumnDescriptor Gap(double points)
    {
        _flow.Gap = points;
        return this;
    }

    /// <summary>Draws a line in the middle of each gap between columns that have content. Decoration only.</summary>
    /// <param name="width">Line width in points.</param>
    /// <param name="color">Line colour; black when null.</param>
    public MultiColumnDescriptor Rule(double width, LayoutColor? color = null)
    {
        _flow.RuleWidth = width;
        _flow.RuleColor = color ?? LayoutColor.Black;
        return this;
    }

    /// <summary>Balances the columns where the content ends. This is the default.</summary>
    public MultiColumnDescriptor Balanced()
    {
        _flow.Fill = ColumnFill.Balanced;
        return this;
    }

    /// <summary>Fills each column to the bottom before starting the next; the last column ends where the content does.</summary>
    public MultiColumnDescriptor Sequential()
    {
        _flow.Fill = ColumnFill.Sequential;
        return this;
    }

    /// <summary>Space between items within a column, in points. Not added at the top of a column. Default 0.</summary>
    public MultiColumnDescriptor Spacing(double points)
    {
        _flow.Spacing = points;
        return this;
    }

    /// <summary>Adds an item after the ones before it and returns its slot.</summary>
    public Slot Item()
    {
        var s = new Slot();
        _items.Add((s, false));
        return s;
    }

    /// <summary>Ends the current column here: the next item starts the next column. At the top of a column it does nothing.</summary>
    public MultiColumnDescriptor ColumnBreak()
    {
        _items.Add((null, true));
        return this;
    }

    internal MultiColumn Build()
    {
        foreach ((Slot? slot, bool isBreak) in _items)
        {
            if (isBreak)
            {
                _flow.Items.Add(new ColumnBreak());
                continue;
            }

            slot!.Seal();
            _flow.Items.Add(slot.Held ?? new Spacer());
        }

        return _flow;
    }
}
