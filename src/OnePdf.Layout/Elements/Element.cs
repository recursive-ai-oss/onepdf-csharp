using OnePdf.Layout.Pagination;

namespace OnePdf.Layout;

/// <summary>Layout switches that apply to a whole document.</summary>
public sealed class LayoutOptions
{
    /// <summary>Draws every element's box and name over the page content. Off by default; meant for finding out why something sits where it does.</summary>
    public bool Debug { get; set; }

    /// <summary>The most pages one document may produce before layout stops with a <see cref="PdfLayoutException"/>. Default 100000.</summary>
    public int MaxPages { get; set; } = 100_000;

    /// <summary>Fewest lines of a split paragraph left at the bottom of a page. Default 2.</summary>
    public int Orphans { get; set; } = 2;

    /// <summary>Fewest lines of a split paragraph carried to the next page. Default 2.</summary>
    public int Widows { get; set; } = 2;
}

/// <summary>The space offered to an element.</summary>
/// <param name="Width">Width in points; infinity when unbounded.</param>
/// <param name="Height">Height in points; infinity when unbounded.</param>
/// <param name="AtTop">True when nothing sits above the element on its page, so moving it to a new page would not give it more room.</param>
internal readonly record struct Constraint(double Width, double Height, bool AtTop)
{
    public bool WidthBounded => double.IsFinite(Width);

    public bool HeightBounded => double.IsFinite(Height);
}

/// <summary>An element after layout: its box in the coordinates of its parent (top-left origin, y down) and the children it placed.</summary>
internal sealed class Placed(Element element, double x, double y, double width, double height)
{
    public Element Element { get; } = element;

    public double X { get; set; } = x;

    public double Y { get; set; } = y;

    public double Width { get; set; } = width;

    public double Height { get; set; } = height;

    public List<Placed> Children { get; } = [];

    /// <summary>What the element needs again when it draws (line ranges, resolved borders, image size).</summary>
    public object? Data { get; set; }
}

/// <summary>Everything an element needs while measuring: the document, fonts, text measurement, the element path for errors and notes.</summary>
internal sealed class LayoutContext : IDisposable
{
    private readonly List<string> _path = [];
    private readonly Dictionary<string, PdfCreatedFont> _fonts = new(StringComparer.Ordinal);
    private readonly Dictionary<Image, PdfCreatedImage> _images = [];
    private PdfContentBuilder? _measurer;
    private PdfCreatedFont? _debugFont;

    public LayoutContext(ILayoutTarget target, LayoutOptions options, ITextEngine text)
    {
        Target = target;
        Options = options;
        Text = text;
        Rules = new FlowRules(Math.Max(1, options.Orphans), Math.Max(1, options.Widows));
    }

    public ILayoutTarget Target { get; }

    public LayoutOptions Options { get; }

    public ITextEngine Text { get; }

    public FlowRules Rules { get; }

    public LayoutDirection Direction { get; set; }

    public List<LayoutDiagnostic> Diagnostics { get; } = [];

    public string PathText => string.Join(" > ", _path);

    public void Push(string segment) => _path.Add(segment);

    public void Pop() => _path.RemoveAt(_path.Count - 1);

    public void PushItem(int index) => _path.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Item {index}"));

    public void Note(string message) => Diagnostics.Add(new LayoutDiagnostic(PathText, message));

    /// <summary>A builder used only to measure text; nothing drawn into it is kept.</summary>
    public PdfContentBuilder Measurer => _measurer ??= Target.BeginMeasurer();

    public PdfCreatedFont Font(string name)
    {
        if (!_fonts.TryGetValue(name, out PdfCreatedFont? font))
        {
            font = Target.AddStandardFont(name);
            _fonts[name] = font;
        }

        return font;
    }

    public PdfCreatedFont DebugFont => _debugFont ??= Font("Helvetica");

    public PdfCreatedImage ImageFor(Image image)
    {
        if (image.Created is { } created)
        {
            return created;
        }

        if (!_images.TryGetValue(image, out PdfCreatedImage? result))
        {
            result = image.Png.IsEmpty ? Target.AddJpegImage(image.Jpeg) : Target.AddPngImage(image.Png);
            _images[image] = result;
        }

        return result;
    }

    /// <summary>
    /// The element cannot be placed in <paramref name="c"/>. When nothing sits above it on the page a new page would not help, so this throws
    /// with the path; otherwise it asks the container to try again on the next page.
    /// </summary>
    public FlowFit NoFit(in Constraint c, double requestedWidth, double requestedHeight, string reason = "The element does not fit an empty page")
    {
        if (c.AtTop)
        {
            throw new PdfLayoutException(PathText, c.Width, c.Height, requestedWidth, requestedHeight, reason);
        }

        return FlowFit.NoFit(requestedHeight);
    }

    public void Dispose()
    {
        _measurer?.Dispose();
        _measurer = null;
    }
}

/// <summary>Where and on what an element draws. The origin is the element's own top-left corner in the frame (the page, or a transformed child frame).</summary>
internal readonly struct DrawContext(PdfContentBuilder builder, LayoutContext context, double frameHeight, double x, double y)
{
    public PdfContentBuilder B { get; } = builder;

    public LayoutContext Ctx { get; } = context;

    /// <summary>Height of the frame in points: converts the y-down layout space to PDF's y-up space.</summary>
    public double FrameHeight { get; } = frameHeight;

    public double X { get; } = x;

    public double Y { get; } = y;

    public DrawContext At(double dx, double dy) => new(B, Ctx, FrameHeight, X + dx, Y + dy);

    public DrawContext InFrame(double height) => new(B, Ctx, height, 0, 0);

    /// <summary>The PDF y of a point <paramref name="dy"/> below this element's top edge.</summary>
    public double PdfY(double dy) => FrameHeight - (Y + dy);

    public void FillRect(double lx, double ly, double w, double h)
    {
        B.Rectangle(X + lx, PdfY(ly + h), w, h);
        B.Fill();
    }
}

/// <summary>
/// A node of the layout tree. Elements are plain objects with properties; they hold no layout state, so one element may appear in a tree
/// several times and a tree may be laid out again. The members that do the layout are internal: elements are the ones this library defines.
/// </summary>
/// <remarks>The set of element types and their properties may change before the package is released.</remarks>
public abstract class Element
{
    /// <summary>A name shown in error paths and in the debug overlay. The type name when null.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// When true, this element is kept on the same page as the start of the next element of its container.
    /// Chains of such elements stay together. Ignored where the element is not in a column.
    /// </summary>
    public bool KeepWithNext { get; set; }

    /// <summary>The content direction for this element and everything inside it; inherited when null.</summary>
    public LayoutDirection? Direction { get; set; }

    internal virtual string TypeName => GetType().Name;

    internal string Label => Name ?? TypeName;

    internal FlowFit Layout(LayoutContext ctx, in Constraint c, object? resume)
    {
        ctx.Push(Label);
        LayoutDirection saved = ctx.Direction;
        if (Direction is { } d)
        {
            ctx.Direction = d;
        }

        try
        {
            return LayoutCore(ctx, c, resume);
        }
        finally
        {
            ctx.Direction = saved;
            ctx.Pop();
        }
    }

    internal void Draw(in DrawContext d, Placed p)
    {
        DrawCore(d, p);
        if (d.Ctx.Options.Debug)
        {
            DebugOverlay.Draw(d, p);
        }
    }

    internal abstract FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume);

    internal virtual void DrawCore(in DrawContext d, Placed p) => DrawChildren(d, p);

    internal static void DrawChildren(in DrawContext d, Placed p)
    {
        foreach (Placed child in p.Children)
        {
            child.Element.Draw(d.At(child.X, child.Y), child);
        }
    }

    /// <summary>The element's own direction wins over the one it inherited.</summary>
    internal static double AlignX(HorizontalAlignment a, LayoutDirection dir, double free) => a switch
    {
        HorizontalAlignment.Center => free / 2,
        HorizontalAlignment.Right => free,
        HorizontalAlignment.Left => 0,
        HorizontalAlignment.End => dir == LayoutDirection.LeftToRight ? free : 0,
        _ => dir == LayoutDirection.LeftToRight ? 0 : free,
    };

    internal static double AlignY(VerticalAlignment a, double free) => a switch
    {
        VerticalAlignment.Middle => free / 2,
        VerticalAlignment.Bottom => free,
        _ => 0,
    };
}

/// <summary>Draws the box and name of every placed element, for the debug option.</summary>
internal static class DebugOverlay
{
    public static void Draw(in DrawContext d, Placed p)
    {
        PdfContentBuilder b = d.B;
        b.Save();
        b.SetStrokeRgb(1, 0, 0);
        b.SetLineWidth(0.25);
        b.Rectangle(d.X, d.PdfY(p.Height), p.Width, p.Height);
        b.Stroke();
        b.SetFillRgb(1, 0, 0);
        b.SetFont(d.Ctx.DebugFont, 4);
        b.DrawText(p.Element.Label, d.X + 1, d.PdfY(0) - 4);
        b.Restore();
    }
}
