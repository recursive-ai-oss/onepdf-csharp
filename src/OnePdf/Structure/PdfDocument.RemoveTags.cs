namespace OnePdf;

public sealed partial class PdfDocument
{
    /// <summary>
    /// Takes the tags out of the document: the structure tree (<c>/StructTreeRoot</c> with its parent tree, id tree, role map and
    /// class map), <c>/MarkInfo</c>, every <c>/StructParents</c> and <c>/StructParent</c> (pages, form and image XObjects,
    /// annotations), and the marked-content sequences that carry an <c>/MCID</c> in the content of pages, form XObjects (nested,
    /// shared or used on several pages), patterns, Type 3 glyph procedures, soft-mask groups and annotation appearances. What the pages
    /// paint does not change. Afterwards the file counts as untagged, so <see cref="Tag"/> and <see cref="ApplyTagging"/> accept it.
    /// </summary>
    /// <param name="options">What happens to the sequences; null for the defaults (drop them, keep artifact, optional-content and other marked content).</param>
    /// <param name="cancellationToken">Cancels the call; the document is then unchanged.</param>
    /// <returns>What was removed, with warnings for content that could not be rewritten.</returns>
    /// <remarks>
    /// The call also works on files that are only half tagged: a tree without <c>/MarkInfo</c>, or marked content with identifiers
    /// and no tree. A stream that cannot be decoded, or an inline image whose end is not found, is left as it is and reported in the
    /// result; the <c>/MCID</c> sequences in it then stay, and <see cref="ApplyTagging"/> will refuse the document. Sequences with an
    /// <c>/ActualText</c> keep a <c>BDC</c> with the text alone, so extraction reads the same. Signatures are invalidated by a full
    /// save; an incremental save keeps the signed revision. Save with <c>Save</c> or <c>SaveIncremental</c>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The document is not editable (<see cref="PdfOpenOptions.AllowEditing"/>).</exception>
    /// <exception cref="PdfLimitException">A limit was exceeded.</exception>
    public PdfRemoveTagsResult RemoveTags(PdfRemoveTagsOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Structure.RemoveTagsPass.Run(this, options ?? new PdfRemoveTagsOptions(), cancellationToken);
    }
}
