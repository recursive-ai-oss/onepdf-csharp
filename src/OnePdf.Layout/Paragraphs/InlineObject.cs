namespace OnePdf.Layout;

/// <summary>Where an inline object sits against the line it is on (the vertical alignment of an inline box).</summary>
public enum InlineAlignment
{
    /// <summary>The object's own baseline (its bottom edge unless <see cref="InlineObject.InnerBaseline"/> says otherwise) sits on the line's baseline, raised by <see cref="InlineObject.BaselineOffset"/>. The default.</summary>
    Baseline = 0,

    /// <summary>The top of the object is at the top of the line box. The line grows downward when the object is taller than the rest of the line.</summary>
    Top,

    /// <summary>The middle of the object is at the middle of the surrounding text: halfway between the font's ascent and descent above and below the baseline.</summary>
    Middle,

    /// <summary>The bottom of the object is at the bottom of the line box. The line grows upward when the object is taller than the rest of the line.</summary>
    Bottom,

    /// <summary>The top of the object is at the font's ascent above the baseline (the top of the text around it).</summary>
    TextTop,

    /// <summary>The bottom of the object is at the font's descent below the baseline (the bottom of the text around it).</summary>
    TextBottom,
}

/// <summary>
/// Something that sits in a line of text and flows with it: an icon, a small picture, a badge, a formula drawn by a callback. It is made
/// with <see cref="InlineBox"/> (a size and a drawing) or, in layout text, <see cref="InlineElement"/> (a subtree of elements), and goes
/// into a paragraph as a span (<see cref="TextSpan.ForInline"/>).
/// </summary>
/// <remarks>
/// <para>In the paragraph the object is one character, U+FFFC OBJECT REPLACEMENT CHARACTER, which has the line break class CB and the
/// bidi class ON (UAX #14 and UAX #9): a line may end before and after it, and it takes the direction of its position, so in a
/// right-to-left run it is placed right to left with its neighbours. It is one unbreakable unit as wide as the object; justification
/// spreads the extra width over the spaces around it as it does between words, and does not widen the object. An object wider than the
/// line sits alone on a line that overflows (<see cref="ParagraphLine.Overflows"/>). Underlines and strike-throughs of the surrounding
/// text stop at the object.</para>
/// <para>The character U+FFFC in ordinary span text is dropped, because it would stand for nothing.</para>
/// </remarks>
public abstract class InlineObject
{
    internal InlineObject()
    {
    }

    /// <summary>Where the object sits against the line. Default <see cref="InlineAlignment.Baseline"/>.</summary>
    public InlineAlignment Alignment { get; init; }

    /// <summary>For <see cref="InlineAlignment.Baseline"/>: how far the object's baseline is raised above the line's baseline, in points (negative lowers it). Default 0. Ignored by the other alignments.</summary>
    public double BaselineOffset { get; init; }

    /// <summary>
    /// The distance from the bottom edge up to the object's own baseline, in points. Default 0 for a <see cref="InlineBox"/> (a picture sits on
    /// the baseline); an <see cref="InlineElement"/> that does not set it takes the baseline of its first line of text, so that its text lines
    /// up with the text around it. Only <see cref="InlineAlignment.Baseline"/> looks at it.
    /// </summary>
    public double? InnerBaseline { get; init; }

    /// <summary>
    /// True, the default: the line grows to fit the object when it is taller than the text. False fixes the line height: the object is
    /// positioned the same way but may stick out of the line box into the lines around it.
    /// </summary>
    public bool GrowsLine { get; init; } = true;

    /// <summary>
    /// False, the default: lines may break before and after the object (UAX #14 class CB). True glues it to its neighbours as a word joiner
    /// would, so that it never starts or ends a line away from the characters next to it.
    /// </summary>
    public bool KeepWithNeighbours { get; init; }

    /// <summary>
    /// A description of the object for readers who cannot see it. In a tagged document the object becomes a <c>Figure</c> with this
    /// <c>/Alt</c>, and text extraction returns it in place of the object (<c>/ActualText</c>, ISO 32000-2 14.9.4). Default none.
    /// </summary>
    public string? AltText { get; init; }

    /// <summary>
    /// Marks the object as decoration: it stays out of the structure tree and extraction returns nothing for it. Default false, except that
    /// a <see cref="InlineBox"/> that only has a <see cref="InlineBox.Paint"/> callback and no <see cref="AltText"/> is decoration (as a canvas is).
    /// </summary>
    public bool Artifact { get; init; }

    /// <summary>
    /// A link target carried with the object, as for <see cref="TextStyle.Link"/>: a URI, or <c>#name</c> for a destination of the document.
    /// <see cref="TextRenderer.Draw(PdfContentBuilder, ParagraphLayout, double, double, int, int)"/> reports the object's box as a
    /// <see cref="TextLinkArea"/>; layout text adds the link annotation itself.
    /// </summary>
    public string? Link { get; init; }

    /// <summary>The width in points once known.</summary>
    internal abstract double ResolvedWidth { get; }

    /// <summary>The height in points once known.</summary>
    internal abstract double ResolvedHeight { get; }

    /// <summary>Distance from the bottom edge to the object's own baseline.</summary>
    internal virtual double ResolvedInnerBaseline => InnerBaseline ?? 0;

    /// <summary>True when the object is decoration.</summary>
    internal virtual bool IsDecoration => Artifact;

    internal virtual void Validate()
    {
        if (!double.IsFinite(BaselineOffset) || Math.Abs(BaselineOffset) > 1_000_000)
        {
            throw new ArgumentException("The baseline offset of an inline object must be finite.");
        }

        if (InnerBaseline is { } ib && (!double.IsFinite(ib) || Math.Abs(ib) > 1_000_000))
        {
            throw new ArgumentException("The inner baseline of an inline object must be finite.");
        }

        if (!Enum.IsDefined(Alignment))
        {
            throw new ArgumentException("Unknown inline alignment.");
        }
    }
}

/// <summary>An inline object of a given size that is drawn by a callback or is a picture: the standalone kind, usable with <see cref="TextRenderer"/>.</summary>
public sealed class InlineBox : InlineObject
{
    /// <summary>The most an inline box may measure on a side, in points.</summary>
    public const double MaxSize = 1_000_000;

    /// <summary>Creates a box.</summary>
    /// <param name="width">Width in points, 0 to <see cref="MaxSize"/>.</param>
    /// <param name="height">Height in points, 0 to <see cref="MaxSize"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">A size is not finite or is outside the range.</exception>
    public InlineBox(double width, double height)
    {
        if (!(width >= 0) || !(width <= MaxSize))
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "The width must be between 0 and 1000000 points.");
        }

        if (!(height >= 0) || !(height <= MaxSize))
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "The height must be between 0 and 1000000 points.");
        }

        Width = width;
        Height = height;
    }

    /// <summary>Width in points.</summary>
    public double Width { get; }

    /// <summary>Height in points.</summary>
    public double Height { get; }

    /// <summary>Draws into the box: the surface's origin is the box's lower-left corner and drawing is clipped to the box. Runs every time the box is drawn.</summary>
    public Action<CanvasSurface>? Paint { get; init; }

    /// <summary>A picture added to the output document, stretched over the box before <see cref="Paint"/> runs. Null for none.</summary>
    public PdfCreatedImage? Image { get; init; }

    internal override double ResolvedWidth => Width;

    internal override double ResolvedHeight => Height;

    internal override bool IsDecoration => Artifact || (Image is null && Paint is not null && AltText is null);

    /// <summary>Draws the box with its lower-left corner at (<paramref name="x"/>, <paramref name="y"/>) in the builder's coordinates.</summary>
    internal void Draw(PdfContentBuilder b, double x, double y)
    {
        if (Image is null && Paint is null)
        {
            return;
        }

        b.Save();
        b.Translate(x, y);
        b.Rectangle(0, 0, Width, Height);
        b.Clip();
        b.EndPath();
        if (Image is { } image)
        {
            b.DrawImage(image, 0, 0, Width, Height);
        }

        Paint?.Invoke(new CanvasSurface(b, Width, Height));
        b.Restore();
    }
}

/// <summary>
/// An inline object made of layout elements, such as a padded, bordered box with a short text, or a picture. It can only be used in a
/// <see cref="Text"/> element (<see cref="Text.Spans"/>), which lays it out when the text is shaped.
/// </summary>
/// <remarks>
/// The child is laid out once, at its width and height, and never splits. With no <see cref="Width"/> it takes its widest natural width (no
/// wrapping), at most <see cref="MaxWidth"/>; with no <see cref="Height"/> it takes the height that gives. A child that cannot be laid out
/// without a page break raises <see cref="PdfLayoutException"/> with the element path. Links, anchors and alternate text inside the child
/// work as elsewhere.
/// </remarks>
public sealed class InlineElement : InlineObject
{
    /// <summary>Creates an inline object around an element.</summary>
    /// <param name="child">The element. It may also be used elsewhere.</param>
    /// <exception cref="ArgumentNullException"><paramref name="child"/> is null.</exception>
    public InlineElement(Element child) => Child = child ?? throw new ArgumentNullException(nameof(child));

    /// <summary>The element.</summary>
    public Element Child { get; }

    /// <summary>The width in points; null, the default, takes the child's widest natural width up to <see cref="MaxWidth"/>.</summary>
    public double? Width { get; init; }

    /// <summary>The height in points; null, the default, takes the height of the child laid out at the width.</summary>
    public double? Height { get; init; }

    /// <summary>The widest the object may be when <see cref="Width"/> is not set; the child wraps within it. Null, the default, means the child's natural width.</summary>
    public double? MaxWidth { get; init; }

    internal override double ResolvedWidth => throw new InvalidOperationException("An InlineElement is measured by the text element that holds it.");

    internal override double ResolvedHeight => throw new InvalidOperationException("An InlineElement is measured by the text element that holds it.");

    internal override void Validate()
    {
        base.Validate();
        foreach (double? v in (double?[])[Width, Height, MaxWidth])
        {
            if (v is { } d && (!double.IsFinite(d) || d < 0 || d > InlineBox.MaxSize))
            {
                throw new ArgumentException("The size of an inline element must be between 0 and 1000000 points.");
            }
        }
    }
}

/// <summary>An <see cref="InlineElement"/> after layout: the placed element with its size and baseline.</summary>
internal sealed class ResolvedInline : InlineObject
{
    private readonly double _width;
    private readonly double _height;
    private readonly double _baseline;

    public ResolvedInline(Placed placed, double width, double height, double baseline, string? altText, string? link, InlineAlignment alignment, double offset, bool grows, bool keep, bool artifact)
    {
        Placed = placed;
        _width = width;
        _height = height;
        _baseline = baseline;
        AltText = altText;
        Link = link;
        Alignment = alignment;
        BaselineOffset = offset;
        GrowsLine = grows;
        KeepWithNeighbours = keep;
        Artifact = artifact;
    }

    public Placed Placed { get; }

    internal override double ResolvedWidth => _width;

    internal override double ResolvedHeight => _height;

    internal override double ResolvedInnerBaseline => _baseline;
}
