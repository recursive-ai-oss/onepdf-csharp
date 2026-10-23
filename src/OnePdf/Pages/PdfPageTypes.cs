namespace OnePdf;

/// <summary>What is carried along when pages are copied from one document into another.</summary>
public sealed record PdfImportOptions
{
    /// <summary>
    /// Copy the bookmarks of the source that lead to an imported page (an item whose own target was not imported but which has
    /// such descendants is kept as a heading without a target). They are appended after the target's own top-level bookmarks.
    /// Default true.
    /// </summary>
    public bool ImportOutlines { get; init; } = true;

    /// <summary>
    /// Copy the widget annotations of the imported pages together with the form fields they belong to, merged under the
    /// target's <c>/AcroForm</c>. When false, widgets are left behind and the pages arrive without their form controls.
    /// Default true.
    /// </summary>
    public bool ImportFormFields { get; init; } = true;

    /// <summary>
    /// Copy the part of the source's structure tree (tagged PDF) that describes the imported pages, as new top-level elements of
    /// the target's tree, which is created when the target has none. When false the pages arrive untagged. Default true.
    /// </summary>
    public bool ImportStructure { get; init; } = true;
}

/// <summary>A run of consecutive pages, for <see cref="PdfDocument.Split(PdfDocument, IEnumerable{PdfPageRange}, CancellationToken)"/>.</summary>
/// <param name="Start">The zero-based index of the first page.</param>
/// <param name="Count">The number of pages (at least 1).</param>
public readonly record struct PdfPageRange(int Start, int Count)
{
    /// <summary>The index after the last page of the range.</summary>
    public int End => Start + Count;
}
