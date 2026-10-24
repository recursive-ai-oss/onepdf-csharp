namespace OnePdf.Layout;

/// <summary>Sets what a table of contents lists and how it looks. Given to the delegate of <see cref="Slot.TableOfContents(Action{TocDescriptor}?)"/>.</summary>
public sealed class TocDescriptor
{
    private readonly TableOfContents _toc = new();

    internal TocDescriptor()
    {
    }

    internal TableOfContents Build() => _toc;

    /// <summary>Lists headings and entries from level <paramref name="min"/> to <paramref name="max"/>. Default 1 to 3.</summary>
    public TocDescriptor Levels(int min, int max)
    {
        _toc.MinLevel = min;
        _toc.MaxLevel = max;
        return this;
    }

    /// <summary>The leader between title and number, repeated with spaces and lined up between entries. Null or empty for none. Default <c>.</c>.</summary>
    public TocDescriptor Leader(string? leader)
    {
        _toc.Leader = leader;
        return this;
    }

    /// <summary>How far each level is moved in from the level above, in points. Default 14.</summary>
    public TocDescriptor Indent(double points)
    {
        _toc.Indent = points;
        return this;
    }

    /// <summary>The least space between a title and its leader or number, in points. Default 6.</summary>
    public TocDescriptor Gap(double points)
    {
        _toc.Gap = points;
        return this;
    }

    /// <summary>Space between neighbouring entries, in points. Default 3.</summary>
    public TocDescriptor Spacing(double points)
    {
        _toc.Spacing = points;
        return this;
    }

    /// <summary>Sets the style every level starts from: font, size, colour, line spacing.</summary>
    public TocDescriptor Style(Action<TocLevelStyle> style)
    {
        ArgumentNullException.ThrowIfNull(style);
        style(_toc.Style);
        return this;
    }

    /// <summary>Sets the style of one level (1 or more), over the settings of <see cref="Style(Action{TocLevelStyle})"/>.</summary>
    public TocDescriptor Level(int level, Action<TocLevelStyle> style)
    {
        ArgumentNullException.ThrowIfNull(style);
        style(_toc.LevelStyle(level));
        return this;
    }
}
