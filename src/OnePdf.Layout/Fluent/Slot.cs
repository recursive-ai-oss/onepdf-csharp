namespace OnePdf.Layout;

/// <summary>A reusable piece of a document written in the fluent style. Implement it to name a part (an address block, an invoice line) and use it anywhere with <see cref="Slot.Component(IComponent)"/>.</summary>
/// <example>
/// <code>
/// sealed class Address(string name, string city) : IComponent
/// {
///     public void Compose(Slot slot) =&gt; slot.Column(c =&gt;
///     {
///         c.Item().Text(name).Bold();
///         c.Item().Text(city);
///     });
/// }
///
/// page.Content().Component(new Address("Ada Lovelace", "London"));
/// </code>
/// </example>
public interface IComponent
{
    /// <summary>Puts the component's content into <paramref name="slot"/>. Called once, when the component is placed.</summary>
    void Compose(Slot slot);
}

/// <summary>
/// A place that takes exactly one piece of content. Decorating methods (<see cref="Padding(double)"/>, <see cref="Border(double, LayoutColor?, BorderStyle)"/>,
/// <see cref="AlignCenter"/>, ...) wrap the content that will go in and give back the slot inside the wrapper, so calls chain from the outside in
/// and end in a content method (<see cref="Slot.Text(string)"/>, <see cref="Column(Action{ColumnDescriptor})"/>, <see cref="Image(ReadOnlyMemory{byte})"/>, ...).
/// </summary>
/// <remarks>
/// <para>A slot takes content once; a second content call throws <see cref="InvalidOperationException"/>. A slot that is never filled stays empty
/// and takes no space. Slots are filled while the composing delegate that handed them out is running: using one afterwards throws.</para>
/// <para>The facade builds the element objects of this package (<see cref="OnePdf.Layout.Box"/>, <see cref="OnePdf.Layout.Column"/>, <see cref="OnePdf.Layout.Text"/>, ...); nothing
/// here changes how they lay out. <see cref="Element(OnePdf.Layout.Element)"/> puts an element object you made yourself into a slot.</para>
/// </remarks>
/// <example>
/// <code>
/// page.Content()
///     .Padding(12)
///     .Border(0.5)
///     .AlignCenter()
///     .Text("Centred in a bordered, padded box");
/// </code>
/// </example>
public partial class Slot
{
    private readonly Action<Element>? _set;
    private bool _filled;
    private bool _sealed;

    internal Slot(Action<Element>? set = null)
    {
        _set = set;
    }

    /// <summary>What was put in this slot, or null.</summary>
    internal Element? Held { get; private set; }

    internal bool IsFilled => _filled;

    internal void Seal() => _sealed = true;

    internal void Put(Element element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (_sealed)
        {
            throw new InvalidOperationException("This slot can only be filled while the delegate that received it is running.");
        }

        if (_filled)
        {
            throw new InvalidOperationException("This slot already has content. Wrap several elements in Column, Row or Layers instead.");
        }

        _filled = true;
        Held = element;
        _set?.Invoke(element);
    }

    /// <summary>Replaces the content of a slot that was filled by a descriptor which rebuilds its element when its settings change.</summary>
    internal void Replace(Element element)
    {
        Held = element;
        _set?.Invoke(element);
    }

    /// <summary>Wraps what will be put in this slot in <paramref name="wrapper"/> and returns the slot inside it.</summary>
    internal Slot Wrap<T>(T wrapper)
        where T : SingleChildElement
    {
        Put(wrapper);
        return new Slot(c => wrapper.Child = c);
    }

    private Slot Box(Action<Box> configure)
    {
        var box = new Box();
        configure(box);
        return Wrap(box);
    }

    /// <summary>Puts an element object (from the lower layer) into this slot, for elements the facade has no method for or that you share between places.</summary>
    /// <param name="element">The element. One element may appear in several places.</param>
    public void Element(Element element) => Put(element ?? throw new ArgumentNullException(nameof(element)));

    /// <summary>Puts a component into this slot: its <see cref="IComponent.Compose(Slot)"/> runs now and fills a slot that stands for this one.</summary>
    /// <param name="component">The component.</param>
    public void Component(IComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        var inner = new Slot(Put);
        component.Compose(inner);
        inner.Seal();
    }

    /// <summary>Puts a component of type <typeparamref name="T"/>, made with its parameterless constructor, into this slot.</summary>
    public void Component<T>()
        where T : IComponent, new() => Component(new T());

    /// <summary>Runs <paramref name="compose"/> with this slot: a way to branch or loop in the middle of a chain.</summary>
    /// <param name="compose">Fills the slot.</param>
    public void Content(Action<Slot> compose)
    {
        ArgumentNullException.ThrowIfNull(compose);
        compose(this);
    }

    // ---- Boxes: padding, margin, border, background, size, alignment ----

    /// <summary>Space inside the box, around the content, on all four sides, in points. Use <see cref="UnitExtensions.Mm(double)"/> and friends for other units.</summary>
    public Slot Padding(double value) => Box(b => b.Padding = Insets.All(value));

    /// <summary>Space inside the box: <paramref name="horizontal"/> on left and right, <paramref name="vertical"/> on top and bottom.</summary>
    public Slot Padding(double horizontal, double vertical) => Box(b => b.Padding = Insets.Symmetric(horizontal, vertical));

    /// <summary>Space inside the box, one value per side (physical sides).</summary>
    public Slot Padding(double top, double right, double bottom, double left) => Box(b => b.Padding = new Insets(top, right, bottom, left));

    /// <summary>Padding on the top.</summary>
    public Slot PaddingTop(double value) => Box(b => b.Padding = b.Padding with { Top = value });

    /// <summary>Padding on the bottom.</summary>
    public Slot PaddingBottom(double value) => Box(b => b.Padding = b.Padding with { Bottom = value });

    /// <summary>Padding on the left, whatever the direction.</summary>
    public Slot PaddingLeft(double value) => Box(b => b.Padding = b.Padding with { Left = value });

    /// <summary>Padding on the right, whatever the direction.</summary>
    public Slot PaddingRight(double value) => Box(b => b.Padding = b.Padding with { Right = value });

    /// <summary>Padding on the start side: the left in left-to-right documents, the right in right-to-left ones.</summary>
    public Slot PaddingStart(double value) => Box(b => b.PaddingStart = value);

    /// <summary>Padding on the end side: the right in left-to-right documents, the left in right-to-left ones.</summary>
    public Slot PaddingEnd(double value) => Box(b => b.PaddingEnd = value);

    /// <summary>Space outside the box (before its border and background), on all four sides, in points.</summary>
    public Slot Margin(double value) => Box(b => b.Margin = Insets.All(value));

    /// <summary>Space outside the box: <paramref name="horizontal"/> on left and right, <paramref name="vertical"/> on top and bottom.</summary>
    public Slot Margin(double horizontal, double vertical) => Box(b => b.Margin = Insets.Symmetric(horizontal, vertical));

    /// <summary>Space outside the box, one value per side (physical sides).</summary>
    public Slot Margin(double top, double right, double bottom, double left) => Box(b => b.Margin = new Insets(top, right, bottom, left));

    /// <summary>Margin on the top.</summary>
    public Slot MarginTop(double value) => Box(b => b.Margin = b.Margin with { Top = value });

    /// <summary>Margin on the bottom.</summary>
    public Slot MarginBottom(double value) => Box(b => b.Margin = b.Margin with { Bottom = value });

    /// <summary>Margin on the left, whatever the direction.</summary>
    public Slot MarginLeft(double value) => Box(b => b.Margin = b.Margin with { Left = value });

    /// <summary>Margin on the right, whatever the direction.</summary>
    public Slot MarginRight(double value) => Box(b => b.Margin = b.Margin with { Right = value });

    /// <summary>Margin on the start side.</summary>
    public Slot MarginStart(double value) => Box(b => b.MarginStart = value);

    /// <summary>Margin on the end side.</summary>
    public Slot MarginEnd(double value) => Box(b => b.MarginEnd = value);

    /// <summary>A line around the box on all sides.</summary>
    /// <param name="width">Line width in points; zero draws nothing.</param>
    /// <param name="color">Line colour; black when null.</param>
    /// <param name="style">Solid (default), dashed or dotted.</param>
    public Slot Border(double width, LayoutColor? color = null, BorderStyle style = BorderStyle.Solid) =>
        Box(b => b.SetBorders(new Border(width, color ?? LayoutColor.Black, style)));

    /// <summary>A line on the top edge.</summary>
    public Slot BorderTop(double width, LayoutColor? color = null, BorderStyle style = BorderStyle.Solid) =>
        Box(b => b.BorderTop = new Border(width, color ?? LayoutColor.Black, style));

    /// <summary>A line on the bottom edge.</summary>
    public Slot BorderBottom(double width, LayoutColor? color = null, BorderStyle style = BorderStyle.Solid) =>
        Box(b => b.BorderBottom = new Border(width, color ?? LayoutColor.Black, style));

    /// <summary>A line on the left edge, whatever the direction.</summary>
    public Slot BorderLeft(double width, LayoutColor? color = null, BorderStyle style = BorderStyle.Solid) =>
        Box(b => b.BorderLeft = new Border(width, color ?? LayoutColor.Black, style));

    /// <summary>A line on the right edge, whatever the direction.</summary>
    public Slot BorderRight(double width, LayoutColor? color = null, BorderStyle style = BorderStyle.Solid) =>
        Box(b => b.BorderRight = new Border(width, color ?? LayoutColor.Black, style));

    /// <summary>A line on the start edge (left in left-to-right documents).</summary>
    public Slot BorderStart(double width, LayoutColor? color = null, BorderStyle style = BorderStyle.Solid) =>
        Box(b => b.BorderStart = new Border(width, color ?? LayoutColor.Black, style));

    /// <summary>A line on the end edge (right in left-to-right documents).</summary>
    public Slot BorderEnd(double width, LayoutColor? color = null, BorderStyle style = BorderStyle.Solid) =>
        Box(b => b.BorderEnd = new Border(width, color ?? LayoutColor.Black, style));

    /// <summary>Fills the box's area (inside the margin) with a colour.</summary>
    public Slot Background(LayoutColor color) => Box(b => b.Background = color);

    /// <summary>Rounds the corners of the background and border; the radius is in points.</summary>
    public Slot Rounded(double radius) => Box(b => b.CornerRadius = radius);

    /// <summary>Paints the box's background, border and content with this opacity, 0 (invisible) to 1 (the default).</summary>
    public Slot Opacity(double value) => Box(b => b.Opacity = value);

    /// <summary>A fixed width in points. The content is cut off if it cannot be narrower; text wraps inside it.</summary>
    public Slot Width(double value) => Box(b => b.Width = value);

    /// <summary>A fixed height in points. A box with a fixed height never splits across pages and cuts off taller content (a note is recorded).</summary>
    public Slot Height(double value) => Box(b => b.Height = value);

    /// <summary>A least width in points.</summary>
    public Slot MinWidth(double value) => Box(b => b.MinWidth = value);

    /// <summary>A least height in points.</summary>
    public Slot MinHeight(double value) => Box(b => b.MinHeight = value);

    /// <summary>A greatest width in points.</summary>
    public Slot MaxWidth(double value) => Box(b => b.MaxWidth = value);

    /// <summary>A greatest height in points. Makes the box atomic like <see cref="Height(double)"/>.</summary>
    public Slot MaxHeight(double value) => Box(b => b.MaxHeight = value);

    /// <summary>Keeps width divided by height at this ratio.</summary>
    public Slot AspectRatio(double ratio) => Box(b => b.AspectRatio = ratio);

    /// <summary>Makes the box as tall as the space it is offered (useful for backgrounds in <see cref="Layers(Action{LayersDescriptor})"/>).</summary>
    public Slot ExpandHeight() => Box(b => b.ExpandHeight = true);

    /// <summary>Aligns the content to the left edge whatever the direction.</summary>
    public Slot AlignLeft() => Box(b => b.HorizontalAlignment = HorizontalAlignment.Left);

    /// <summary>Aligns the content to the right edge whatever the direction.</summary>
    public Slot AlignRight() => Box(b => b.HorizontalAlignment = HorizontalAlignment.Right);

    /// <summary>Centres the content horizontally.</summary>
    public Slot AlignCenter() => Box(b => b.HorizontalAlignment = HorizontalAlignment.Center);

    /// <summary>Aligns the content to the start edge (left in left-to-right documents). This is the default.</summary>
    public Slot AlignStart() => Box(b => b.HorizontalAlignment = HorizontalAlignment.Start);

    /// <summary>Aligns the content to the end edge (right in left-to-right documents).</summary>
    public Slot AlignEnd() => Box(b => b.HorizontalAlignment = HorizontalAlignment.End);

    /// <summary>Aligns the content to the top. This is the default. Only visible when the box is taller than its content.</summary>
    public Slot AlignTop() => Box(b => b.VerticalAlignment = VerticalAlignment.Top);

    /// <summary>Centres the content vertically in a box taller than it.</summary>
    public Slot AlignMiddle() => Box(b => b.VerticalAlignment = VerticalAlignment.Middle);

    /// <summary>Aligns the content to the bottom of a box taller than it.</summary>
    public Slot AlignBottom() => Box(b => b.VerticalAlignment = VerticalAlignment.Bottom);

    // ---- Transforms ----

    /// <summary>Turns the content clockwise by an angle in degrees. The space taken is the bounding box of the turned content; it never splits.</summary>
    public Slot Rotate(double degrees) => Wrap(new Rotate { Degrees = degrees });

    /// <summary>Scales the content by <paramref name="x"/> and <paramref name="y"/> (both greater than zero). The content is laid out in the space divided by the factors.</summary>
    public Slot Scale(double x, double y) => Wrap(new Scale { X = x, Y = y });

    /// <summary>Scales the content by the same factor in both directions.</summary>
    public Slot Scale(double factor) => Scale(factor, factor);

    /// <summary>Moves the drawing of the content (right and down, in points) without changing the space it takes.</summary>
    public Slot Translate(double x, double y) => Wrap(new Translate { X = x, Y = y });

    // ---- Page-flow rules ----

    /// <summary>Keeps the content on one page: if it does not fit in what is left of a page it moves to the next. Content taller than a page splits anyway (a note is recorded).</summary>
    public Slot KeepTogether() => Wrap(new KeepTogether());

    /// <summary>Moves the content to the next page unless at least <paramref name="height"/> points are left on this one.</summary>
    public Slot EnsureSpace(double height) => Wrap(new EnsureSpace { Height = height });

    /// <summary>Keeps the content on the same page as the start of the item after it in its column. Chains of such items stay together.</summary>
    public Slot KeepWithNext() => Box(b => b.KeepWithNext = true);

    // ---- Navigation ----

    /// <summary>Makes the content a link to an address. The link is data: nothing is opened or fetched. Characters outside ASCII are percent-encoded.</summary>
    /// <param name="uri">An absolute address such as <c>https://example.com/</c>.</param>
    public Slot Hyperlink(string uri) => Wrap(new Link { Uri = uri ?? throw new ArgumentNullException(nameof(uri)) });

    /// <summary>Makes the content a link to an anchor of this document (see <see cref="Anchor(string)"/>), which may come later in the document.</summary>
    public Slot LinkToAnchor(string anchorId) => Wrap(new Link { Destination = anchorId ?? throw new ArgumentNullException(nameof(anchorId)) });

    /// <summary>Names the top of the content so links can go there. The name must be unique in the document.</summary>
    public Slot Anchor(string id) => Wrap(new Anchor { Id = id ?? throw new ArgumentNullException(nameof(id)) });

    /// <summary>Adds an entry to the document outline (the bookmarks panel) going to the top of the content.</summary>
    /// <param name="title">The text of the entry.</param>
    /// <param name="level">Nesting: 0 is top level.</param>
    public Slot Bookmark(string title, int level = 0) => Wrap(new Bookmark { Title = title ?? throw new ArgumentNullException(nameof(title)), Level = level });

    // ---- Semantics ----

    /// <summary>Makes the content a heading of the given level (1 to 6; it becomes the structure type <c>H1</c> to <c>H6</c>). A heading is kept with what follows it.</summary>
    public Slot Heading(int level) => Wrap(new Heading { Level = level });

    /// <summary>Describes the content for readers who cannot see it. The content, and everything in it, becomes one figure with this description.</summary>
    public Slot AltText(string description) => Box(b => b.AltText = description ?? throw new ArgumentNullException(nameof(description)));

    /// <summary>Marks the content as decoration: it stays out of the structure tree and assistive technology skips it.</summary>
    public Slot Artifact() => Box(b => b.Artifact = true);

    /// <summary>Sets the language of the content, a BCP 47 tag such as <c>ar</c> or <c>de-CH</c>.</summary>
    public Slot Language(string tag) => Box(b => b.Language = tag ?? throw new ArgumentNullException(nameof(tag)));

    /// <summary>Sets the content direction for the content and everything in it.</summary>
    public Slot Direction(LayoutDirection direction) => Box(b => b.Direction = direction);

    /// <summary>Sets right-to-left direction for the content: rows, table columns and list markers mirror, and start/end alignment flip.</summary>
    public Slot RightToLeft() => Direction(LayoutDirection.RightToLeft);

    /// <summary>Sets left-to-right direction for the content.</summary>
    public Slot LeftToRight() => Direction(LayoutDirection.LeftToRight);

    /// <summary>Names the content in error paths and in the debug overlay.</summary>
    public Slot Name(string name) => Box(b => b.Name = name ?? throw new ArgumentNullException(nameof(name)));
}
