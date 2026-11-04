namespace OnePdf;

/// <summary>The markup annotation subtypes <see cref="PdfPage.AddMarkupAnnotation"/> writes.</summary>
public enum PdfMarkupKind
{
    /// <summary>A Highlight (ISO 32000-2 12.5.6.10): the quadrilaterals are filled, multiplying with the text below.</summary>
    Highlight = 0,

    /// <summary>A StrikeOut: a line through the middle of each quadrilateral.</summary>
    StrikeOut = 1,

    /// <summary>An Underline: a line along the bottom edge of each quadrilateral.</summary>
    Underline = 2,

    /// <summary>A Squiggly: a wavy line along the bottom edge of each quadrilateral.</summary>
    Squiggly = 3,

    /// <summary>A Caret (12.5.6.11): an insertion mark in a rectangle. Carets have no quadrilaterals.</summary>
    Caret = 4,
}

/// <summary>What a Caret annotation is associated with (<c>/Sy</c>, ISO 32000-2 Table 183).</summary>
public enum PdfCaretSymbol
{
    /// <summary>No symbol (<c>/Sy /None</c>), the default.</summary>
    None = 0,

    /// <summary>A new paragraph symbol (<c>/Sy /P</c>).</summary>
    Paragraph = 1,
}

/// <summary>
/// One quadrilateral of a text markup annotation, in default user space (<c>/QuadPoints</c>, ISO 32000-2 12.5.6.10). The corners
/// are named as the text reads: "upper" is towards the ascenders of the marked text, which is not always towards the top of the
/// page when the text is rotated. They are written in the order viewers produce and read (upper left, upper right, lower left,
/// lower right), which is not the counterclockwise order Table 181 describes.
/// </summary>
/// <param name="UpperLeftX">x of the upper left corner.</param>
/// <param name="UpperLeftY">y of the upper left corner.</param>
/// <param name="UpperRightX">x of the upper right corner.</param>
/// <param name="UpperRightY">y of the upper right corner.</param>
/// <param name="LowerLeftX">x of the lower left corner.</param>
/// <param name="LowerLeftY">y of the lower left corner.</param>
/// <param name="LowerRightX">x of the lower right corner.</param>
/// <param name="LowerRightY">y of the lower right corner.</param>
public readonly record struct PdfMarkupQuad(
    double UpperLeftX, double UpperLeftY, double UpperRightX, double UpperRightY,
    double LowerLeftX, double LowerLeftY, double LowerRightX, double LowerRightY)
{
    /// <summary>The upright quadrilateral that covers <paramref name="rectangle"/>.</summary>
    public static PdfMarkupQuad FromRectangle(PdfRectangle rectangle) =>
        new(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Top, rectangle.Left, rectangle.Bottom, rectangle.Right, rectangle.Bottom);
}

/// <summary>The pop-up window of a markup annotation (ISO 32000-2 12.5.6.14).</summary>
/// <param name="Rectangle">Where the window sits on the page, in default user space. Must not be empty.</param>
/// <param name="Open">Whether the window is shown open.</param>
public readonly record struct PdfMarkupPopup(PdfRectangle Rectangle, bool Open = false);

/// <summary>
/// What to write for one text markup annotation. Every value comes from the caller: nothing is read from the clock or a random
/// source, so the same input and the same calls give the same bytes.
/// </summary>
public sealed class PdfMarkupAnnotationOptions
{
    /// <summary>The subtype. Required.</summary>
    public PdfMarkupKind Kind { get; init; }

    /// <summary>
    /// The marked areas (<c>/QuadPoints</c>). Required, at least one, for every kind except <see cref="PdfMarkupKind.Caret"/>, which
    /// has none and rejects them. At most <see cref="PdfLimits.MaxAppearanceItems"/>; coordinates must be finite and below 10^7 in magnitude.
    /// </summary>
    public IReadOnlyList<PdfMarkupQuad> Quads { get; init; } = [];

    /// <summary>
    /// The annotation rectangle. Required for <see cref="PdfMarkupKind.Caret"/>. For the other kinds it defaults to the bounds of
    /// the quadrilaterals. The written <c>/Rect</c> is always grown to hold the generated appearance, so it may be larger.
    /// </summary>
    public PdfRectangle? Rectangle { get; init; }

    /// <summary>
    /// For a Caret, the distances from <see cref="Rectangle"/> to the drawn caret (<c>/RD</c>: left, bottom, right, top); null writes none.
    /// Ignored by the other kinds.
    /// </summary>
    public PdfRectangle? RectangleDifferences { get; init; }

    /// <summary>For a Caret, the associated symbol (<c>/Sy</c>); written only when not <see cref="PdfCaretSymbol.None"/>.</summary>
    public PdfCaretSymbol CaretSymbol { get; init; }

    /// <summary>The colour <c>/C</c>: one (gray), three (RGB) or four (CMYK) numbers in 0..1. Null leaves it out; the appearance then uses yellow for a highlight and black otherwise.</summary>
    public IReadOnlyList<double>? Color { get; init; }

    /// <summary>The constant opacity <c>/CA</c>, above 0 and at most 1. Null leaves it out (opaque).</summary>
    public double? Opacity { get; init; }

    /// <summary>The text of the annotation (<c>/Contents</c>). Null leaves it out.</summary>
    public string? Contents { get; init; }

    /// <summary>The author (<c>/T</c>). Null leaves it out.</summary>
    public string? Title { get; init; }

    /// <summary>The unique name of the annotation on its page (<c>/NM</c>). Null leaves it out. Not checked for uniqueness.</summary>
    public string? Name { get; init; }

    /// <summary>The modification date (<c>/M</c>) written as given. Null leaves it out; it is never filled from the clock.</summary>
    public DateTimeOffset? ModifiedDate { get; init; }

    /// <summary>The flags <c>/F</c>. Defaults to <see cref="PdfAnnotationOptions.Print"/>, which viewers need to print the mark.</summary>
    public PdfAnnotationOptions Flags { get; init; } = PdfAnnotationOptions.Print;

    /// <summary>An attached pop-up window, linked both ways (<c>/Popup</c> on the markup, <c>/Parent</c> on the pop-up). Null adds none.</summary>
    public PdfMarkupPopup? Popup { get; init; }
}
