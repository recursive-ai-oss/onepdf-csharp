namespace OnePdf;

public sealed partial class PdfDocument
{
    private readonly object _structureGate = new();
    private Text.StructureOrder? _structureOrder;
    private bool _structureRead;

    /// <summary>The reading order of the logical structure tree, read once per document; null when the file is not tagged.</summary>
    internal Text.StructureOrder? GetStructureOrder(CancellationToken cancellationToken)
    {
        lock (_structureGate)
        {
            if (!_structureRead)
            {
                _structureOrder = Text.StructureOrder.Read(this, cancellationToken);
                _structureRead = true;
            }

            return _structureOrder;
        }
    }

    /// <summary>
    /// Extracts the text of every page: glyphs, words, lines and blocks in reading order, with headers and footers flagged by their
    /// repetition. Pages run in parallel, <c>min(4, processor count)</c> at a time unless
    /// <see cref="Text.PdfTextOptions.MaxDegreeOfParallelism"/> says otherwise (1 is sequential). Damaged content is skipped and
    /// recorded in <see cref="Diagnostics"/>; a page that cannot be read at all throws when it is reached.
    /// </summary>
    /// <param name="options">Options; null for the defaults (reading order, ligatures decomposed, hyphens kept).</param>
    /// <param name="cancellationToken">Cancels the extraction with <see cref="OperationCanceledException"/>.</param>
    /// <returns>One entry per page, in page order.</returns>
    public IReadOnlyList<Text.PdfPageText> ExtractText(Text.PdfTextOptions? options = null, CancellationToken cancellationToken = default) =>
        Text.PdfTextExtractor.Create(this, options).ExtractDocument(cancellationToken);
}
