namespace OnePdf.Layout;

public partial class Slot
{
    /// <summary>Stacks items top to bottom. The delegate adds items with <see cref="ColumnDescriptor.Item"/>; each item is a slot.</summary>
    /// <param name="compose">Adds the items.</param>
    /// <example>
    /// <code>
    /// slot.Column(col =>
    /// {
    ///     col.Spacing(6);
    ///     col.Item().Text("First");
    ///     col.Item().Text("Second");
    /// });
    /// </code>
    /// </example>
    public void Column(Action<ColumnDescriptor> compose)
    {
        ArgumentNullException.ThrowIfNull(compose);
        var d = new ColumnDescriptor();
        compose(d);
        Put(d.Build());
    }

    /// <summary>Places items side by side. The delegate adds items with <see cref="RowDescriptor.AutoItem"/>, <see cref="RowDescriptor.ConstantItem(double)"/> or <see cref="RowDescriptor.RelativeItem(double)"/>.</summary>
    /// <param name="compose">Adds the items.</param>
    /// <example>
    /// <code>
    /// slot.Row(row =>
    /// {
    ///     row.Spacing(8);
    ///     row.ConstantItem(60).Text("Total");
    ///     row.RelativeItem().Text("Everything");
    ///     row.AutoItem().Text("42");
    /// });
    /// </code>
    /// </example>
    public void Row(Action<RowDescriptor> compose)
    {
        ArgumentNullException.ThrowIfNull(compose);
        var d = new RowDescriptor();
        compose(d);
        Put(d.Build());
    }

    /// <summary>Overlaps layers, the first at the back. One layer, the primary, decides the size and may split across pages.</summary>
    /// <param name="compose">Adds the layers.</param>
    /// <example>
    /// <code>
    /// slot.Layers(layers =&gt;
    /// {
    ///     layers.Layer().Background(Colors.LightGrey);
    ///     layers.PrimaryLayer().Padding(10).Text("On top of grey");
    /// });
    /// </code>
    /// </example>
    public void Layers(Action<LayersDescriptor> compose)
    {
        ArgumentNullException.ThrowIfNull(compose);
        var d = new LayersDescriptor();
        compose(d);
        Put(d.Build());
    }

    /// <summary>Entries with bullets or numbers hanging in a column of their own.</summary>
    /// <param name="compose">Sets the list's look and adds entries with <see cref="ListDescriptor.Item(string?)"/>. Nest a list by calling <c>List</c> inside an entry.</param>
    /// <example>
    /// <code>
    /// slot.List(list =>
    /// {
    ///     list.Numbered();
    ///     list.Item().Text("Mix");
    ///     list.Item().Text("Bake");
    /// });
    /// </code>
    /// </example>
    public void List(Action<ListDescriptor> compose)
    {
        ArgumentNullException.ThrowIfNull(compose);
        var d = new ListDescriptor();
        compose(d);
        Put(d.Build());
    }

    /// <summary>A grid of cells with spans, repeated header and footer rows and rows that split across pages.</summary>
    /// <param name="compose">Defines columns and adds rows.</param>
    /// <example>
    /// <code>
    /// slot.Table(table =>
    /// {
    ///     table.Columns(c => { c.Relative(2); c.Constant(60); });
    ///     table.HeaderRow(r => { r.Cell().Text("Item").Bold(); r.Cell().Text("Qty").Bold(); });
    ///     table.Row(r => { r.Cell().Text("Pencil"); r.Cell().AlignEnd().Text("12"); });
    /// });
    /// </code>
    /// </example>
    public void Table(Action<TableDescriptor> compose)
    {
        ArgumentNullException.ThrowIfNull(compose);
        var d = new TableDescriptor();
        compose(d);
        Put(d.Build());
    }

    /// <summary>
    /// A table of contents of the document's headings and <see cref="Slot.TocEntry(string?, int)"/> entries, with page numbers, leaders and links. The page
    /// numbers are settled by laying the document out more than once (see <see cref="OnePdf.Layout.TableOfContents"/>).
    /// </summary>
    /// <param name="compose">Chooses the levels, leader and styles; null for the defaults (levels 1 to 3, dots).</param>
    /// <example>
    /// <code>
    /// page.Content().Column(c =>
    /// {
    ///     c.Item().Text("Contents").Bold().FontSize(20);
    ///     c.Item().TableOfContents(toc => toc.Levels(1, 2));
    ///     c.Item().PageBreak();
    ///     c.Item().Heading(1).Text("Introduction").Bold().FontSize(16);
    /// });
    /// </code>
    /// </example>
    public void TableOfContents(Action<TocDescriptor>? compose = null)
    {
        var d = new TocDescriptor();
        compose?.Invoke(d);
        Put(d.Build());
    }

    /// <summary>A forced page break: whatever follows in the same column starts a new page. At the top of a page it does nothing.</summary>
    public void PageBreak() => Put(new PageBreak());

    /// <summary>Empty space of a given height (and optionally width) in points.</summary>
    public void Spacer(double height, double? width = null) => Put(new Spacer { Height = height, Width = width });

    /// <summary>A box drawn by a callback with OnePDF's content builder. The callback runs once per page the canvas is drawn on and draws clipped to the box.</summary>
    /// <param name="height">Height in points.</param>
    /// <param name="paint">Draws into the box; coordinates start at its lower-left corner.</param>
    /// <param name="width">Width in points; the offered width when null.</param>
    public void Canvas(double height, Action<CanvasSurface> paint, double? width = null) =>
        Put(new Canvas { Height = height, Width = width, Paint = paint ?? throw new ArgumentNullException(nameof(paint)) });

    /// <summary>A JPEG or PNG picture, chosen by the file's signature. The picture is added to the file once however often the bytes are used.</summary>
    /// <param name="data">The file's bytes.</param>
    /// <returns>A descriptor for the fit, resolution and description.</returns>
    /// <exception cref="ArgumentException">The bytes are neither JPEG nor PNG.</exception>
    /// <example>
    /// <code>
    /// slot.Width(200).Image(File.ReadAllBytes("logo.png")).Contain().AltText("Company logo");
    /// </code>
    /// </example>
    public ImageDescriptor Image(ReadOnlyMemory<byte> data)
    {
        var image = new Image();
        ReadOnlySpan<byte> s = data.Span;
        if (s.Length >= 8 && s[0] == 0x89 && s[1] == 0x50 && s[2] == 0x4E && s[3] == 0x47)
        {
            image.Png = data;
        }
        else if (s.Length >= 3 && s[0] == 0xFF && s[1] == 0xD8)
        {
            image.Jpeg = data;
        }
        else
        {
            throw new ArgumentException("The image is neither a JPEG nor a PNG file.", nameof(data));
        }

        Put(image);
        return new ImageDescriptor(image);
    }

    /// <summary>A JPEG or PNG picture read from a file now.</summary>
    /// <param name="path">A local file.</param>
    public ImageDescriptor Image(string path) => Image(File.ReadAllBytes(path ?? throw new ArgumentNullException(nameof(path))));

    /// <summary>A picture already added to the output with <see cref="PdfDocumentWriter.AddJpegImage(ReadOnlyMemory{byte})"/> or the document's equivalent, to share it between many places.</summary>
    public ImageDescriptor Image(PdfCreatedImage image)
    {
        var e = new Image { Created = image ?? throw new ArgumentNullException(nameof(image)) };
        Put(e);
        return new ImageDescriptor(e);
    }
}

/// <summary>Sets how a picture fills its box, its resolution and its description. Returned by <see cref="Slot.Image(ReadOnlyMemory{byte})"/>.</summary>
public sealed class ImageDescriptor
{
    private readonly Image _image;

    internal ImageDescriptor(Image image) => _image = image;

    /// <summary>The width fills the offered width and the height follows the proportions. This is the default.</summary>
    public ImageDescriptor FitWidth() => Set(i => i.Fit = ImageFit.FitWidth);

    /// <summary>The height fills the offered height (the natural height when unbounded) and the width follows.</summary>
    public ImageDescriptor FitHeight() => Set(i => i.Fit = ImageFit.FitHeight);

    /// <summary>The whole picture shows, proportions kept, inside the box it is offered. Put the picture under <see cref="Slot.Width(double)"/> and <see cref="Slot.Height(double)"/> to control the box.</summary>
    public ImageDescriptor Contain() => Set(i => i.Fit = ImageFit.Contain);

    /// <summary>The picture covers the box it is offered, proportions kept, and is clipped to it.</summary>
    public ImageDescriptor Cover() => Set(i => i.Fit = ImageFit.Cover);

    /// <summary>The picture is stretched to the box it is offered, proportions ignored.</summary>
    public ImageDescriptor Stretch() => Set(i => i.Fit = ImageFit.Stretch);

    /// <summary>Pixels per inch that give the natural size (default 72, one pixel per point).</summary>
    public ImageDescriptor Dpi(double dpi) => Set(i => i.Dpi = dpi);

    /// <summary>For <see cref="Contain"/>: where the picture sits in its box.</summary>
    public ImageDescriptor Align(HorizontalAlignment horizontal, VerticalAlignment vertical) => Set(i =>
    {
        i.HorizontalAlignment = horizontal;
        i.VerticalAlignment = vertical;
    });

    /// <summary>Describes the picture for readers who cannot see it. Tagged documents report a picture without one.</summary>
    public ImageDescriptor AltText(string description) => Set(i => i.AltText = description ?? throw new ArgumentNullException(nameof(description)));

    /// <summary>Marks the picture as decoration, so it needs no description and stays out of the structure tree.</summary>
    public ImageDescriptor Artifact() => Set(i => i.Artifact = true);

    private ImageDescriptor Set(Action<Image> change)
    {
        change(_image);
        return this;
    }
}

/// <summary>Adds the items of a column. Given to the delegate of <see cref="Slot.Column(Action{ColumnDescriptor})"/>.</summary>
public sealed class ColumnDescriptor
{
    private readonly List<Slot> _items = [];
    private double _spacing;
    private HorizontalAlignment _alignment;

    internal ColumnDescriptor()
    {
    }

    /// <summary>Space between neighbouring items on a page, in points. Not added at the top of a page. Default 0.</summary>
    public ColumnDescriptor Spacing(double points)
    {
        _spacing = points;
        return this;
    }

    /// <summary>Where an item narrower than the column sits. Default <see cref="HorizontalAlignment.Start"/>.</summary>
    public ColumnDescriptor ItemAlignment(HorizontalAlignment alignment)
    {
        _alignment = alignment;
        return this;
    }

    /// <summary>Adds an item below the ones before it and returns its slot.</summary>
    public Slot Item()
    {
        var s = new Slot();
        _items.Add(s);
        return s;
    }

    internal Column Build()
    {
        var c = new Column { Spacing = _spacing, Alignment = _alignment };
        foreach (Slot s in _items)
        {
            s.Seal();
            c.Items.Add(s.Held ?? new Spacer());
        }

        return c;
    }
}

/// <summary>Adds the items of a row. Given to the delegate of <see cref="Slot.Row(Action{RowDescriptor})"/>.</summary>
public sealed class RowDescriptor
{
    private readonly List<(Slot Slot, RowItemWidth Kind, double Value)> _items = [];
    private double _spacing;
    private VerticalAlignment _alignment;

    internal RowDescriptor()
    {
    }

    /// <summary>Space between items in points. Default 0.</summary>
    public RowDescriptor Spacing(double points)
    {
        _spacing = points;
        return this;
    }

    /// <summary>Aligns items shorter than the row to the top (the default).</summary>
    public RowDescriptor AlignTop() => Align(VerticalAlignment.Top);

    /// <summary>Centres items shorter than the row.</summary>
    public RowDescriptor AlignMiddle() => Align(VerticalAlignment.Middle);

    /// <summary>Aligns items shorter than the row to the bottom.</summary>
    public RowDescriptor AlignBottom() => Align(VerticalAlignment.Bottom);

    private RowDescriptor Align(VerticalAlignment a)
    {
        _alignment = a;
        return this;
    }

    /// <summary>An item as wide as its content.</summary>
    public Slot AutoItem() => Add(RowItemWidth.Auto, 0);

    /// <summary>An item of a fixed width in points.</summary>
    public Slot ConstantItem(double width) => Add(RowItemWidth.Constant, width);

    /// <summary>An item that shares the width left over by weight (1 by default).</summary>
    public Slot RelativeItem(double weight = 1) => Add(RowItemWidth.Relative, weight);

    private Slot Add(RowItemWidth kind, double value)
    {
        var s = new Slot();
        _items.Add((s, kind, value));
        return s;
    }

    internal Row Build()
    {
        var r = new Row { Spacing = _spacing, Alignment = _alignment };
        foreach ((Slot slot, RowItemWidth kind, double value) in _items)
        {
            slot.Seal();
            r.Items.Add(new RowItem(slot.Held ?? new Spacer()) { WidthKind = kind, Value = value });
        }

        return r;
    }
}

/// <summary>Adds the layers of a stack. Given to the delegate of <see cref="Slot.Layers(Action{LayersDescriptor})"/>.</summary>
public sealed class LayersDescriptor
{
    private readonly List<Slot> _layers = [];
    private int _primary;
    private bool _hasPrimary;

    internal LayersDescriptor()
    {
    }

    /// <summary>Adds a layer on top of the ones before it. It is laid out at the primary layer's size on every page the primary occupies.</summary>
    public Slot Layer()
    {
        var s = new Slot();
        _layers.Add(s);
        return s;
    }

    /// <summary>Adds the layer that sets the size and may split across pages. At most one; the first layer is primary when none is declared.</summary>
    /// <exception cref="InvalidOperationException">A primary layer was already added.</exception>
    public Slot PrimaryLayer()
    {
        if (_hasPrimary)
        {
            throw new InvalidOperationException("Layers can have only one primary layer.");
        }

        _hasPrimary = true;
        _primary = _layers.Count;
        return Layer();
    }

    internal Layers Build()
    {
        var l = new Layers { PrimaryIndex = _primary };
        foreach (Slot s in _layers)
        {
            s.Seal();
            l.Items.Add(s.Held ?? new Spacer());
        }

        return l;
    }
}

/// <summary>Sets the look of a list and adds its entries. Given to the delegate of <see cref="Slot.List(Action{ListDescriptor})"/>.</summary>
public sealed class ListDescriptor
{
    private readonly List<(Slot Slot, string? Marker)> _items = [];
    private readonly ItemList _list = new();

    internal ListDescriptor()
    {
    }

    /// <summary>Marks entries with a bullet (the default). The bullet changes with nesting depth unless <see cref="Bullet(string)"/> sets one.</summary>
    public ListDescriptor Bulleted()
    {
        _list.Kind = ListKind.Bulleted;
        return this;
    }

    /// <summary>Marks entries with numbers.</summary>
    /// <param name="style">How numbers are written. Default decimal.</param>
    public ListDescriptor Numbered(NumberingStyle style = NumberingStyle.Arabic)
    {
        _list.Kind = ListKind.Numbered;
        _list.NumberStyle = style;
        return this;
    }

    /// <summary>The bullet text, such as <c>-</c>.</summary>
    public ListDescriptor Bullet(string bullet)
    {
        _list.Bullet = bullet;
        return this;
    }

    /// <summary>The number of the first entry. Default 1.</summary>
    public ListDescriptor Start(int number)
    {
        _list.Start = number;
        return this;
    }

    /// <summary>Text after a number, <c>.</c> by default.</summary>
    public ListDescriptor Suffix(string suffix)
    {
        _list.Suffix = suffix ?? throw new ArgumentNullException(nameof(suffix));
        return this;
    }

    /// <summary>Writes each marker from the entry's number, replacing the bullet or number and suffix.</summary>
    public ListDescriptor MarkerFormat(Func<int, string> format)
    {
        _list.MarkerFormat = format ?? throw new ArgumentNullException(nameof(format));
        return this;
    }

    /// <summary>Font family of the markers (as for <see cref="TextDescriptor.FontFamily(string[])"/>); the text default when not set.</summary>
    public ListDescriptor MarkerFont(string family)
    {
        _list.FontName = family;
        return this;
    }

    /// <summary>Size of the markers in points. Default 11.</summary>
    public ListDescriptor MarkerSize(double size)
    {
        _list.Size = size;
        return this;
    }

    /// <summary>Colour of the markers.</summary>
    public ListDescriptor MarkerColor(LayoutColor color)
    {
        _list.Color = color;
        return this;
    }

    /// <summary>Width of the marker column; the widest marker when 0 (the default).</summary>
    public ListDescriptor MarkerWidth(double points)
    {
        _list.MarkerWidth = points;
        return this;
    }

    /// <summary>Space between marker column and entry text. Default 6.</summary>
    public ListDescriptor MarkerGap(double points)
    {
        _list.MarkerGap = points;
        return this;
    }

    /// <summary>Space between entries, in points. Default 0.</summary>
    public ListDescriptor Spacing(double points)
    {
        _list.Spacing = points;
        return this;
    }

    /// <summary>Adds an entry and returns its slot.</summary>
    /// <param name="marker">A marker for this entry only, replacing the bullet or number.</param>
    public Slot Item(string? marker = null)
    {
        var s = new Slot();
        _items.Add((s, marker));
        return s;
    }

    internal ItemList Build()
    {
        foreach ((Slot slot, string? marker) in _items)
        {
            slot.Seal();
            _list.Items.Add(new ListItem(slot.Held ?? new Spacer()) { MarkerText = marker });
        }

        return _list;
    }
}
