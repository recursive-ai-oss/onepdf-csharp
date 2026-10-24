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

    /// <summary>The fonts text can use. Null, the default, is a shared registry of the Liberation faces and the standard fonts; system fonts are never used unless the registry is told to.</summary>
    public TextFontRegistry? Fonts { get; set; }

    /// <summary>
    /// Writes a structure tree (tagged PDF, ISO 32000-2 14.7) from the element tree and marks everything that is not content as an artifact.
    /// Default true. Off, the pages carry no marked content at all. Targets that cannot write a structure tree (a document edited in memory)
    /// are never tagged, whatever this says.
    /// </summary>
    public bool Tagged { get; set; } = true;
}

/// <summary>The narrowest and widest width of an element's content, in points.</summary>
internal readonly record struct WidthRange(double Min, double Max);

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

    /// <summary>True when this piece continues one drawn on an earlier page (the element was laid out with a resume point).</summary>
    public bool Continued { get; set; }

    /// <summary>True when the element carries on after this piece, on a later page.</summary>
    public bool Continues { get; set; }

    /// <summary>What the element needs again when it draws (line ranges, resolved borders, image size).</summary>
    public object? Data { get; set; }
}

/// <summary>Which page is being laid out, for elements that depend on it (page numbers).</summary>
/// <param name="Index">Zero-based index of the page in the document.</param>
/// <param name="Section">Zero-based index of the section.</param>
/// <param name="IndexInSection">Zero-based index of the page in its section.</param>
/// <param name="Number">The displayed page number.</param>
/// <param name="Style">How the number is written.</param>
internal readonly record struct PageInfo(int Index, int Section, int IndexInSection, long Number, NumberingStyle Style);

/// <summary>Everything an element needs while measuring: the document, fonts, text measurement, the element path for errors and notes.</summary>
internal sealed class LayoutContext : IDisposable
{
    // Segments such as "Row 14" keep their number apart and are formatted only when a message needs the path.
    private readonly List<(string Text, int Number)> _path = [];
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
        Tag = new Tagger(this, target, options.Tagged);
    }

    /// <summary>Builds the structure tree while pages are drawn.</summary>
    public Tagger Tag { get; }

    /// <summary>How many artifact elements enclose the element being laid out: images there need no alternate text.</summary>
    public int LayoutArtifactDepth { get; set; }

    private readonly HashSet<object> _noted = [];

    /// <summary>Records a note once per element, however often the element is laid out.</summary>
    public void NoteOnce(Element element, string message)
    {
        if (_noted.Add(element))
        {
            Note(message);
        }
    }

    public ILayoutTarget Target { get; }

    public LayoutOptions Options { get; }

    public ITextEngine Text { get; }

    public FlowRules Rules { get; }

    public LayoutDirection Direction { get; set; }

    /// <summary>True in the run that only counts pages: nothing is drawn.</summary>
    public bool CountOnly { get; init; }

    /// <summary>How many lists enclose the element being laid out.</summary>
    public int ListDepth { get; set; }

    /// <summary>The page being laid out and drawn.</summary>
    public PageInfo Page { get; set; }

    /// <summary>The page being drawn, so elements can add link annotations to it; null while measuring.</summary>
    public TargetPage? OpenPage { get; set; }

    private readonly HashSet<string> _destinations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _linkedNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PdfFormXObject> _totals = new(StringComparer.Ordinal);
    private int _lastBookmarkLevel = -1;
    private bool _navigationNoted;

    public List<LayoutDiagnostic> Diagnostics { get; } = [];

    public string PathText
    {
        get
        {
            var parts = new string[_path.Count];
            for (int i = 0; i < parts.Length; i++)
            {
                (string text, int number) = _path[i];
                parts[i] = number == NoNumber ? text : text + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return string.Join(" > ", parts);
        }
    }

    private const int NoNumber = int.MinValue;

    public void Push(string segment) => _path.Add((segment, NoNumber));

    /// <summary>Adds a segment made of <paramref name="prefix"/> and a number ("Row ", 14), formatted only if the path is read.</summary>
    public void Push(string prefix, int number) => _path.Add((prefix, number));

    public void Pop() => _path.RemoveAt(_path.Count - 1);

    public void PushItem(int index) => _path.Add(("Item ", index));

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

    private bool CanNavigate(in DrawContext d)
    {
        if (d.Transformed)
        {
            Note("Links, anchors and bookmarks inside a rotated or scaled element are not recorded.");
            return false;
        }

        if (!Target.SupportsNavigation || OpenPage?.Stream is null)
        {
            if (!_navigationNoted)
            {
                _navigationNoted = true;
                Note("This target cannot record links, anchors, bookmarks or page labels; they are left out.");
            }

            return false;
        }

        return true;
    }

    /// <summary>Records a named destination at the top of the element being drawn.</summary>
    public void DefineAnchor(in DrawContext d, string name)
    {
        if (!CanNavigate(d))
        {
            return;
        }

        if (!_destinations.Add(name))
        {
            Note($"The anchor '{name}' is defined more than once; the first one is used.");
            return;
        }

        Target.AddNamedDestination(name, Page.Index, d.PdfY(0));
    }

    /// <summary>Adds an outline entry going to the top of the element being drawn. A level more than one below the previous entry is lifted.</summary>
    public void AddBookmark(in DrawContext d, string title, int level)
    {
        if (!CanNavigate(d))
        {
            return;
        }

        int clamped = Math.Clamp(level, 0, _lastBookmarkLevel + 1);
        if (clamped != level)
        {
            Note($"The bookmark '{title}' is at level {level} but the previous entry is at level {_lastBookmarkLevel}; it is placed at level {clamped}.");
        }

        _lastBookmarkLevel = clamped;
        Target.AddBookmark(title, clamped, Page.Index, d.PdfY(0));
    }

    /// <summary>Adds a link annotation over the box of the element being drawn.</summary>
    public void AddLink(in DrawContext d, double width, double height, string? uri, string? destination, PdfStructureElement? element = null, string? description = null)
    {
        if (!(width > 0) || !(height > 0) || !CanNavigate(d))
        {
            return;
        }

        var area = new PdfRectangle(d.X, d.PdfY(height), d.X + width, d.PdfY(0));
        if (uri is { Length: > 0 })
        {
            OpenPage!.Stream!.AddLink(area, uri, element, description ?? uri);
        }
        else if (destination is { Length: > 0 })
        {
            _linkedNames.Add(destination);
            OpenPage!.Stream!.AddLinkToDestination(area, destination, element, description);
        }
    }

    /// <summary>Notes the links whose target was never defined.</summary>
    public void CheckLinks()
    {
        var names = new List<string>(_linkedNames);
        names.Sort(StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (!_destinations.Contains(name))
            {
                Diagnostics.Add(new LayoutDiagnostic(string.Empty, $"A link goes to the anchor '{name}', which is not defined."));
            }
        }
    }

    /// <summary>The shared form for a page count, made on first use.</summary>
    public PdfFormXObject TotalForm(string key, PdfRectangle box, int section, Action<PdfFormContentBuilder, int> paint)
    {
        if (!_totals.TryGetValue(key, out PdfFormXObject? form))
        {
            form = Target.TotalForm(box, section, paint);
            _totals[key] = form;
        }

        return form;
    }

    public void Dispose()
    {
        _measurer?.Dispose();
        _measurer = null;
    }
}

/// <summary>Where and on what an element draws. The origin is the element's own top-left corner in the frame (the page, or a transformed child frame).</summary>
internal readonly struct DrawContext(PdfContentBuilder builder, LayoutContext context, double frameHeight, double x, double y, bool transformed = false)
{
    /// <summary>True inside a rotated or scaled frame, where positions are no longer page positions.</summary>
    public bool Transformed { get; } = transformed;

    public PdfContentBuilder B { get; } = builder;

    public LayoutContext Ctx { get; } = context;

    /// <summary>Height of the frame in points: converts the y-down layout space to PDF's y-up space.</summary>
    public double FrameHeight { get; } = frameHeight;

    public double X { get; } = x;

    public double Y { get; } = y;

    public DrawContext At(double dx, double dy) => new(B, Ctx, FrameHeight, X + dx, Y + dy, Transformed);

    public DrawContext InFrame(double height) => new(B, Ctx, height, 0, 0, true);

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

    /// <summary>
    /// A BCP 47 language tag for this element and everything inside it, such as <c>ar</c> or <c>de-CH</c>; inherited when null. Text uses it for
    /// language-specific glyph forms and hyphenation, and the structure tree records it (<c>/Lang</c>, ISO 32000-2 14.9.2) wherever it
    /// differs from the surrounding content. Default none.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// An alternate description of the element's content for readers that cannot see it (<c>/Alt</c>, ISO 32000-2 14.9.3). On an
    /// <see cref="Image"/> it describes the picture; on any other element it turns the element, and everything inside it, into one
    /// <c>Figure</c> with this description. A <see cref="Link"/> uses it as the link's description. Default none.
    /// </summary>
    public string? AltText { get; set; }

    /// <summary>
    /// Marks the element and everything inside it as decoration (an artifact, ISO 32000-2 14.8.2.2): it stays out of the structure tree and
    /// assistive technology skips it. Default false. A <see cref="Canvas"/> is decoration unless it has <see cref="AltText"/>.
    /// </summary>
    public bool Artifact { get; set; }

    /// <summary>True for elements that are decoration unless they are given alternate text.</summary>
    internal virtual bool ArtifactByDefault => false;

    /// <summary>False where <see cref="AltText"/> means something else (a link's description).</summary>
    internal virtual bool AltTextIsFigure => true;

    /// <summary>True for elements that are figures even without alternate text (which is then reported).</summary>
    internal virtual bool FigureByDefault => false;

    /// <summary>True when this element, or the chain of transparent wrappers around the real content, asks to stay with what follows it.</summary>
    internal virtual bool EffectiveKeepWithNext => KeepWithNext;

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

        if (Artifact)
        {
            ctx.LayoutArtifactDepth++;
        }

        try
        {
            FlowFit fit = LayoutCore(ctx, c, resume);
            if (fit.Placed is Placed placed && ReferenceEquals(placed.Element, this))
            {
                placed.Continued = resume is not null;
                placed.Continues = fit.Kind == FlowKind.Partial;
            }

            return fit;
        }
        finally
        {
            if (Artifact)
            {
                ctx.LayoutArtifactDepth--;
            }

            ctx.Direction = saved;
            ctx.Pop();
        }
    }

    /// <summary>
    /// The narrowest and the widest width the element can take: the width it needs when every possible line break is taken, and when none is.
    /// Tables use it for automatic column widths.
    /// </summary>
    internal WidthRange MeasureWidths(LayoutContext ctx)
    {
        ctx.Push(Label);
        LayoutDirection saved = ctx.Direction;
        if (Direction is { } d)
        {
            ctx.Direction = d;
        }

        try
        {
            return WidthsCore(ctx);
        }
        finally
        {
            ctx.Direction = saved;
            ctx.Pop();
        }
    }

    /// <summary>Elements that cannot be made narrower by wrapping take the width of an unbounded layout for both ends.</summary>
    internal virtual WidthRange WidthsCore(LayoutContext ctx) => UnboundedWidth(ctx);

    internal WidthRange UnboundedWidth(LayoutContext ctx)
    {
        FlowFit f = LayoutCore(ctx, new Constraint(double.PositiveInfinity, double.PositiveInfinity, false), null);
        double w = f.Placed is Placed p ? p.Width : 0;
        return new WidthRange(w, w);
    }

    internal void Draw(in DrawContext d, Placed p)
    {
        Tagger tag = d.Ctx.Tag;
        if (!tag.Enabled)
        {
            DrawCore(d, p);
            if (d.Ctx.Options.Debug)
            {
                DebugOverlay.Draw(d, p);
            }

            return;
        }

        string? savedLanguage = tag.Language;
        if (Language is { Length: > 0 })
        {
            tag.Language = Language;
        }

        bool artifact = false;
        TagScope figure = default;
        if (Artifact || (ArtifactByDefault && AltText is null))
        {
            artifact = tag.BeginArtifact(d.B);
        }
        else if ((AltText is not null && AltTextIsFigure) || FigureByDefault)
        {
            figure = tag.Open(this, p.Continued, p.Continues, "Figure", AltText);
        }

        DrawCore(d, p);
        tag.EndArtifact(d.B, artifact);
        tag.Close(figure);
        tag.Language = savedLanguage;
        if (d.Ctx.Options.Debug)
        {
            bool began = tag.BeginArtifact(d.B);
            DebugOverlay.Draw(d, p);
            tag.EndArtifact(d.B, began);
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
