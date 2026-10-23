using System.Linq;
namespace OnePdf;

public sealed partial class PdfDocument
{
    private volatile bool _redacted;

    /// <summary>True once <see cref="Redact"/> or <see cref="ApplyRedactAnnotations"/> has changed this document. Such a document can only be saved with <see cref="Save(Stream, PdfSaveOptions?, CancellationToken)"/>: an incremental save would keep the removed bytes in the file.</summary>
    public bool IsRedacted => _redacted;

    internal void MarkRedacted() => _redacted = true;

    /// <summary>
    /// Removes everything that is drawn inside the given areas, for real: the text, paths, shadings and image samples are taken out of the
    /// content, not covered over. See <c>docs/redaction.md</c> for the rules.
    /// </summary>
    /// <param name="regions">Areas to clear, each on one page, in the page's default user space.</param>
    /// <param name="options">Options; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the call with <see cref="OperationCanceledException"/>; the document is then unchanged.</param>
    /// <returns>What was removed, and warnings (for example that signatures are now invalid).</returns>
    /// <remarks>
    /// <para>Text: every glyph whose box meets an area is removed from the string that shows it (<c>Tj</c>, <c>TJ</c>, <c>'</c>, <c>"</c> become
    /// a <c>TJ</c> whose numbers keep the other glyphs where they were). The same holds inside form XObjects (a form is copied before it is
    /// changed, so other uses stay as they were) and annotation appearances that are kept. Type 3 glyphs go whole.</para>
    /// <para>The call needs a document opened with <see cref="PdfOpenOptions.AllowEditing"/>. Save the result with <c>Save</c>, which writes a new
    /// file without earlier revisions or unreferenced objects; <c>SaveIncremental</c> is refused afterwards.</para>
    /// <para>Atomic for content problems: when a page cannot be read completely (undecodable stream, a limit, content the rewrite cannot match to
    /// the interpreter's reading) the call throws and the document is left unchanged.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    /// <exception cref="ArgumentException">A rectangle is empty or not finite, or a colour is malformed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A page index is outside the document.</exception>
    /// <exception cref="PdfRedactionException">A page could not be redacted safely; the exception names the page.</exception>
    public PdfRedactionResult Redact(IEnumerable<PdfRedactionRegion> regions, PdfRedactionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ThrowIfDisposed();
        return Redaction.RedactionPass.Run(this, regions, options ?? new PdfRedactionOptions(), null, cancellationToken);
    }

    /// <summary>
    /// Applies the document's redaction annotations (ISO 32000-2 12.5.6.23): each annotation's <c>/QuadPoints</c> (or <c>/Rect</c>) is cleared as by
    /// <see cref="Redact"/>, drawn over with its <c>/IC</c> colour (or the options' overlay) and <c>/OverlayText</c>, and the annotation is removed.
    /// </summary>
    /// <param name="options">Options; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the call; the document is then unchanged.</param>
    /// <returns>The result, with <see cref="PdfRedactionResult.RedactAnnotationsApplied"/> set. Nothing happens when there are none.</returns>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    /// <exception cref="PdfRedactionException">A page could not be redacted safely.</exception>
    public PdfRedactionResult ApplyRedactAnnotations(PdfRedactionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureEditable(0);
        var regions = new List<PdfRedactionRegion>();
        var applied = new List<(int, PdfDictionary)>();
        for (int i = 0; i < Pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (PdfAnnotation a in Pages[i].Annotations)
            {
                if (a.Subtype != "Redact")
                {
                    continue;
                }

                applied.Add((i, a.Dictionary));
                IReadOnlyList<double>? colour = a.Dictionary.Get("IC") is PdfArray ic && ic.Count is 1 or 3 or 4 ? [.. Enumerable.Range(0, ic.Count).Select(k => Math.Clamp(ic.GetDouble(k), 0, 1))] : null;
                string? text = a.Dictionary.GetString("OverlayText")?.GetText();
                IReadOnlyList<double> quads = a.QuadPoints;
                var rects = new List<PdfRectangle>();
                for (int q = 0; q + 7 < quads.Count; q += 8)
                {
                    double xmin = Math.Min(Math.Min(quads[q], quads[q + 2]), Math.Min(quads[q + 4], quads[q + 6]));
                    double xmax = Math.Max(Math.Max(quads[q], quads[q + 2]), Math.Max(quads[q + 4], quads[q + 6]));
                    double ymin = Math.Min(Math.Min(quads[q + 1], quads[q + 3]), Math.Min(quads[q + 5], quads[q + 7]));
                    double ymax = Math.Max(Math.Max(quads[q + 1], quads[q + 3]), Math.Max(quads[q + 5], quads[q + 7]));
                    rects.Add(new PdfRectangle(xmin, ymin, xmax, ymax));
                }

                if (rects.Count == 0)
                {
                    rects.Add(a.Rect);
                }

                foreach (PdfRectangle r in rects)
                {
                    if (r.IsEmpty)
                    {
                        continue;
                    }

                    regions.Add(new PdfRedactionRegion(i, r) { FillColor = colour, OverlayText = text });
                }
            }
        }

        PdfRedactionResult result = Redaction.RedactionPass.Run(this, regions, options ?? new PdfRedactionOptions(), applied, cancellationToken);
        result.RedactAnnotationsApplied = applied.Count;
        return result;
    }
}
