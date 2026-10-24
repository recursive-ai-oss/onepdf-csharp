namespace OnePdf.Layout;

/// <summary>
/// The settings of an inline object: size, where it sits against the line, how it is described and linked. Returned by
/// <see cref="TextDescriptor.Inline(Action{Slot})"/>, <see cref="TextDescriptor.InlineImage(ReadOnlyMemory{byte}, double, double)"/> and
/// <see cref="TextDescriptor.InlineCanvas(double, double, Action{CanvasSurface})"/>. The font settings it inherits from
/// <see cref="TextFormatDescriptor{TSelf}"/> are those of the text around the object: its size and font give the metrics that
/// <see cref="AlignMiddle"/>, <see cref="AlignTextTop"/> and <see cref="AlignTextBottom"/> use, and the minimum height of its line.
/// </summary>
/// <remarks>See <see cref="InlineObject"/> for how an object breaks, justifies and orders with the text around it.</remarks>
public sealed class InlineDescriptor : TextFormatDescriptor<InlineDescriptor>
{
    private readonly Element _element;
    private readonly Action _changed;
    private readonly bool _owned;
    private double? _width;
    private double? _height;
    private double? _maxWidth;
    private InlineAlignment _alignment;
    private double _offset;
    private double? _innerBaseline;
    private bool _fixedLine;
    private bool _keep;
    private string? _alt;
    private bool _artifact;
    private string? _link;

    internal InlineDescriptor(Element element, Action changed, bool owned = false)
        : base(new SpanFormat())
    {
        _element = element;
        _changed = changed;
        _owned = owned;
    }

    private protected override void Changed() => _changed();

    private InlineDescriptor Edit(Action change)
    {
        change();
        _changed();
        return this;
    }

    internal InlineDescriptor WithSize(double width, double height)
    {
        _width = width;
        _height = height;
        return this;
    }

    /// <summary>A fixed width in points. Without one the object takes its content's widest natural width (see <see cref="MaxWidth(double)"/>).</summary>
    public InlineDescriptor Width(double points) => Edit(() => _width = points);

    /// <summary>A fixed height in points. Without one the object takes the height of its content.</summary>
    public InlineDescriptor Height(double points) => Edit(() => _height = points);

    /// <summary>The widest the object may be when it has no fixed width; its content wraps within it.</summary>
    public InlineDescriptor MaxWidth(double points) => Edit(() => _maxWidth = points);

    /// <summary>The object's own baseline sits on the line's baseline, raised by <paramref name="offset"/> points (negative lowers it). This is the default, with no offset.</summary>
    /// <param name="offset">Points above the baseline.</param>
    public InlineDescriptor Baseline(double offset = 0) => Edit(() =>
    {
        _alignment = InlineAlignment.Baseline;
        _offset = offset;
    });

    /// <summary>The distance from the object's bottom edge up to its own baseline, for <see cref="Baseline(double)"/>. By default 0, or the baseline of the first line of text inside the object.</summary>
    public InlineDescriptor InnerBaseline(double points) => Edit(() => _innerBaseline = points);

    /// <summary>The top of the object is at the top of the line.</summary>
    public InlineDescriptor AlignTop() => Edit(() => _alignment = InlineAlignment.Top);

    /// <summary>The middle of the object is at the middle of the surrounding text.</summary>
    public InlineDescriptor AlignMiddle() => Edit(() => _alignment = InlineAlignment.Middle);

    /// <summary>The bottom of the object is at the bottom of the line.</summary>
    public InlineDescriptor AlignBottom() => Edit(() => _alignment = InlineAlignment.Bottom);

    /// <summary>The top of the object is at the top of the surrounding text (the font's ascent).</summary>
    public InlineDescriptor AlignTextTop() => Edit(() => _alignment = InlineAlignment.TextTop);

    /// <summary>The bottom of the object is at the bottom of the surrounding text (the font's descent).</summary>
    public InlineDescriptor AlignTextBottom() => Edit(() => _alignment = InlineAlignment.TextBottom);

    /// <summary>The line keeps the height its text gives it; an object taller than that sticks out into the lines around it.</summary>
    public InlineDescriptor FixedLineHeight() => Edit(() => _fixedLine = true);

    /// <summary>Lines never break between the object and the characters next to it.</summary>
    public InlineDescriptor KeepWithNeighbours() => Edit(() => _keep = true);

    /// <summary>Describes the object for readers who cannot see it: it becomes a figure with this description, and extraction returns it in place of the object.</summary>
    public InlineDescriptor AltText(string description) => Edit(() => _alt = description ?? throw new ArgumentNullException(nameof(description)));

    /// <summary>Marks the object as decoration: it stays out of the structure tree and extraction returns nothing for it.</summary>
    public InlineDescriptor Artifact() => Edit(() => _artifact = true);

    /// <summary>Makes the object a link to an address (data only; nothing is fetched).</summary>
    public InlineDescriptor Hyperlink(string uri) => Edit(() => _link = uri ?? throw new ArgumentNullException(nameof(uri)));

    /// <summary>Makes the object a link to an anchor of this document (see <see cref="Slot.Anchor(string)"/>).</summary>
    public InlineDescriptor LinkToAnchor(string anchorId) => Edit(() => _link = "#" + (anchorId ?? throw new ArgumentNullException(nameof(anchorId))));

    internal InlineObject Create()
    {
        if (_owned)
        {
            // A picture or canvas of this descriptor's own: the description belongs to it, so the figure holds the picture's content.
            _element.AltText = _alt;
            _element.Artifact = _artifact;
        }

        return new InlineElement(_element)
        {
            Width = _width,
            Height = _height,
            MaxWidth = _maxWidth,
            Alignment = _alignment,
            BaselineOffset = _offset,
            InnerBaseline = _innerBaseline,
            GrowsLine = !_fixedLine,
            KeepWithNeighbours = _keep,
            AltText = _alt,
            Artifact = _artifact,
            Link = _link,
        };
    }
}
