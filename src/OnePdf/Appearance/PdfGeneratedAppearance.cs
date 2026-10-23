namespace OnePdf;

/// <summary>
/// An appearance that OnePDF synthesised for an annotation that had none (or whose form field asked for a new one), kept in
/// memory. It is the same data a stored normal appearance would be: a form XObject dictionary and its decoded content bytes,
/// so a writer can persist it later. Nothing is ever written back to the source file.
/// </summary>
public sealed class PdfGeneratedAppearance
{
    internal PdfGeneratedAppearance(PdfDictionary dictionary, ReadOnlyMemory<byte> content, PdfRectangle rect, string description)
    {
        Dictionary = dictionary;
        Content = content;
        Rect = rect;
        Description = description;
    }

    /// <summary>
    /// The form XObject dictionary: <c>/Type /XObject</c>, <c>/Subtype /Form</c>, <c>/BBox</c>, <c>/Matrix</c> where the widget is
    /// rotated, and <c>/Resources</c> holding the fonts and graphics states the content uses. Fonts are the field's own
    /// resource dictionaries when it has them, otherwise direct standard 14 font dictionaries. There is no <c>/Length</c>.
    /// </summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The content stream, unfiltered. Every number is written with at most four decimals, so the same input always gives the same bytes.</summary>
    public ReadOnlyMemory<byte> Content { get; }

    /// <summary>
    /// The rectangle, in default user space, the appearance is meant to fill: the annotation's <c>/Rect</c> for widgets, and
    /// for markup the <c>/Rect</c> grown to hold the generated geometry (ISO 32000-2 12.5.5 maps the transformed
    /// <c>/BBox</c> onto the rectangle, so generating the box and rectangle together keeps the drawing at its true size).
    /// </summary>
    public PdfRectangle Rect { get; }

    /// <summary>A short human-readable label of what was generated, for example <c>text field</c> or <c>Highlight</c>.</summary>
    public string Description { get; }
}
