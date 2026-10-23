using OnePdf.Forms;

namespace OnePdf;

public sealed partial class PdfDocument
{
    // Views built from the page tree and the form are made on first use; structural edits to forms and annotations drop them so the
    // next access reads the changed objects.
    internal void ResetEditViews()
    {
        _pages = null;
        _form = null;
    }

    /// <summary>
    /// Bakes the normal appearances of widgets (and, optionally, other annotations) into the page content and removes those
    /// annotations and their fields (ISO 32000-2 12.5.5 for the placement, 12.7 for the form). Missing appearances are built in
    /// memory as <see cref="PdfPage"/> interpretation does (<c>/NeedAppearances</c> included) and stored as Form XObjects.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>Each appearance becomes a Form XObject placed by <c>q a b c d e f cm /Name Do Q</c> in a content stream appended to the page, whose original content is wrapped in <c>q</c>/<c>Q</c>. The page then interprets like the original page with annotations (flattened art is drawn under annotations that are kept). NoRotate is honoured on rotated pages; NoZoom has no meaning in a page.</description></item>
    /// <item><description>Annotations that would not be drawn (hidden, NoView, or without the Print flag in print mode, or hidden by optional content) are removed without drawing. Links are never touched; pop-ups go with their parent.</description></item>
    /// <item><description>Fields: flattened terminal fields leave the field tree and <c>/CO</c>; parents left without children go too; <c>/AcroForm</c> is dropped when no field is left. Signed signature fields are kept, since removing them would delete the signature.</description></item>
    /// <item><description>Views taken earlier (<see cref="Pages"/>, <see cref="Form"/>) are stale afterwards; take them again. Structure tree entries that point at removed widgets are left as they are.</description></item>
    /// </list>
    /// </remarks>
    /// <param name="options">Options; null for all fields, no markup, view mode.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>What was drawn and removed.</returns>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    /// <exception cref="PdfFormValueException"><see cref="PdfFlattenOptions.FieldNames"/> names a field that does not exist.</exception>
    /// <exception cref="ArgumentException"><see cref="PdfFlattenOptions.Visibility"/> is <see cref="PdfAnnotationMode.None"/>.</exception>
    public PdfFlattenResult Flatten(PdfFlattenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return FormFlattener.Run(this, options ?? new PdfFlattenOptions(), cancellationToken);
    }
}
