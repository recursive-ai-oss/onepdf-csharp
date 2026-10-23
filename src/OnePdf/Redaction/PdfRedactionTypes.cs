namespace OnePdf;

/// <summary>An area of one page to remove content from, in the page's default user space (the space the page's boxes are written in, before <c>/Rotate</c>).</summary>
/// <param name="PageIndex">Zero-based page index.</param>
/// <param name="Rectangle">The area. An empty or non-finite rectangle is refused.</param>
public readonly record struct PdfRedactionRegion(int PageIndex, PdfRectangle Rectangle)
{
    /// <summary>Text drawn in the area after the content under it is gone (needs <see cref="PdfRedactionOptions.Overlay"/> or a fill of its own to show on a box). Null for none.</summary>
    public string? OverlayText { get; init; }

    /// <summary>The fill of this area as 1 (gray), 3 (RGB) or 4 (CMYK) components in 0..1, or null to use <see cref="PdfRedactionOptions.Overlay"/>. A region with a fill is covered even when the options have no overlay.</summary>
    public IReadOnlyList<double>? FillColor { get; init; }
}

/// <summary>What to do with vector paths that touch a redacted area.</summary>
public enum PdfRedactionVectorMode
{
    /// <summary>
    /// A path (fill, stroke or both) whose painted area meets the redacted area is removed whole, including its coordinates. The
    /// painted area is the bounding box of the path's points and control points under the current matrix, widened by half the
    /// line width for strokes, so the test errs on the side of removing. A clipping path is kept (it paints nothing). The default.
    /// </summary>
    RemovePaths = 0,

    /// <summary>
    /// The path stays in the file but is drawn inside a clip that excludes the redacted areas. The geometry is still readable in the
    /// file; choose this only when losing a whole background shape matters more than that.
    /// </summary>
    ClipOut = 1,
}

/// <summary>What to do with annotations that touch a redacted area.</summary>
public enum PdfRedactionAnnotationMode
{
    /// <summary>The annotation (and any pop-up that belongs to it) is removed from the page, and with it its contents, link target and appearance. The default.</summary>
    Remove = 0,

    /// <summary>The annotation stays, its appearance is redacted like page content, and its <c>/Contents</c>, <c>/RC</c> and form value are cleared.</summary>
    KeepRedacted = 1,
}

/// <summary>The fill (and optionally text) drawn over each redacted area.</summary>
public sealed class PdfRedactionOverlay
{
    /// <summary>The fill colour as 1 (gray), 3 (RGB) or 4 (CMYK) components in 0..1. Default black.</summary>
    public IReadOnlyList<double> Color { get; init; } = [0.0];

    /// <summary>The colour of <see cref="PdfRedactionRegion.OverlayText"/>. Default white.</summary>
    public IReadOnlyList<double> TextColor { get; init; } = [1.0];

    /// <summary>The size of overlay text in points; 0 picks one that fits the area (at most 12). Default 0.</summary>
    public double TextSize { get; init; }
}

/// <summary>Options of <see cref="PdfDocument.Redact"/> and <see cref="PdfDocument.ApplyRedactAnnotations"/>.</summary>
public sealed class PdfRedactionOptions
{
    /// <summary>When true only glyphs whose whole box lies inside an area are removed; glyphs that stick out stay. Default false: any overlap removes the glyph.</summary>
    public bool FullyContainedOnly { get; init; }

    /// <summary>What happens to vector paths. Default <see cref="PdfRedactionVectorMode.RemovePaths"/>.</summary>
    public PdfRedactionVectorMode VectorMode { get; init; }

    /// <summary>What happens to annotations. Default <see cref="PdfRedactionAnnotationMode.Remove"/>.</summary>
    public PdfRedactionAnnotationMode Annotations { get; init; }

    /// <summary>Drawn over every area after redaction. Null (default) draws nothing, unless the region carries its own fill.</summary>
    public PdfRedactionOverlay? Overlay { get; init; }

    /// <summary>Text to clear from the Info dictionary and the XMP packet (case-insensitive substring match). An Info entry that contains a term is removed; XMP elements and attributes that contain one are emptied. Default none.</summary>
    public IReadOnlyList<string> MetadataTerms { get; init; } = [];

    /// <summary>Remove document-level and page, annotation and field JavaScript and actions (data only; nothing runs). Default false.</summary>
    public bool RemoveJavaScript { get; init; }

    /// <summary>Remove the embedded-file name tree, <c>/AF</c> lists and file-attachment annotations. Default false.</summary>
    public bool RemoveEmbeddedFiles { get; init; }

    /// <summary>Remove the thumbnail images of every page. Thumbnails of redacted pages are always removed. Default false.</summary>
    public bool RemoveThumbnails { get; init; }

    /// <summary>
    /// Decoders for DCT, JPX and JBIG2 images, needed to blank the samples under an area. Null uses <see cref="PdfImageDecoderRegistry.Default"/>. An image whose codec has no decoder is removed whole.
    /// </summary>
    public PdfImageDecoderRegistry? Decoders { get; init; }
}

/// <summary>What <see cref="PdfDocument.Redact"/> did.</summary>
public sealed class PdfRedactionResult
{
    internal PdfRedactionResult()
    {
    }

    /// <summary>Pages whose content was examined.</summary>
    public int PagesProcessed { get; internal set; }

    /// <summary>Glyphs removed from page content, forms and annotation appearances.</summary>
    public int GlyphsRemoved { get; internal set; }

    /// <summary>Paths and shadings removed (or clipped out).</summary>
    public int PathsRemoved { get; internal set; }

    /// <summary>Images whose samples were blanked.</summary>
    public int ImagesBlanked { get; internal set; }

    /// <summary>Images removed whole (inline images, or images that could not be decoded).</summary>
    public int ImagesRemoved { get; internal set; }

    /// <summary>Annotations removed.</summary>
    public int AnnotationsRemoved { get; internal set; }

    /// <summary>Form fields whose value was cleared.</summary>
    public int FieldsCleared { get; internal set; }

    /// <summary>Structure elements and marked-content properties whose alternate text was removed.</summary>
    public int AlternateTextsRemoved { get; internal set; }

    /// <summary>Info entries and XMP values cleared.</summary>
    public int MetadataCleared { get; internal set; }

    /// <summary>Redaction annotations that were applied (and removed).</summary>
    public int RedactAnnotationsApplied { get; internal set; }

    /// <summary>The document had signatures; they are invalid once it is saved.</summary>
    public bool SignaturesInvalidated { get; internal set; }

    /// <summary>Notes about what was done that the caller may want to see (signatures, images removed whole, and so on).</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    internal List<string> _warnings = [];
}

/// <summary>
/// A page could not be redacted safely (its content could not be fully read, or a limit was hit), so the operation stopped and
/// the document was left as it was.
/// </summary>
public sealed class PdfRedactionException : PdfException
{
    /// <summary>Creates the exception.</summary>
    public PdfRedactionException(string message, int pageIndex)
        : base(message)
    {
        PageIndex = pageIndex;
    }

    /// <summary>Creates the exception with an inner exception.</summary>
    public PdfRedactionException(string message, int pageIndex, Exception? innerException)
        : base(message, innerException)
    {
        PageIndex = pageIndex;
    }

    /// <summary>The zero-based index of the page that could not be redacted.</summary>
    public int PageIndex { get; }
}
