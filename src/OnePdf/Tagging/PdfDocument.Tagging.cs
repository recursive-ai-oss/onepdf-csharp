namespace OnePdf;

public sealed partial class PdfDocument
{
    /// <summary>
    /// Starts a plan for tagging this document. Fill it in from the text extraction (<see cref="Text.PdfTextOptions.RetainGlyphs"/> true):
    /// give blocks, lines, words, areas and annotations a structure role, then apply it with <see cref="ApplyTagging"/>.
    /// </summary>
    /// <returns>An empty plan bound to this document.</returns>
    public PdfTaggingPlan CreateTaggingPlan()
    {
        ThrowIfDisposed();
        return new PdfTaggingPlan(this);
    }

    /// <summary>
    /// Writes a tag structure into an untagged document: marked content in the page content streams, a structure tree with its parent
    /// tree and <c>/StructParents</c>, <c>/MarkInfo</c>, the language and title, link annotations as object references, and artifact
    /// marks on everything the plan does not give a role. What the pages show does not change, and extraction reads the same text. See
    /// <c>docs/tagging.md</c> for the rules.
    /// </summary>
    /// <param name="plan">A plan made by <see cref="CreateTaggingPlan"/> on this document.</param>
    /// <param name="cancellationToken">Cancels the call; the document is then unchanged.</param>
    /// <returns>What was written, with warnings.</returns>
    /// <remarks>
    /// OnePDF does not decide roles or invent alternate text: the structure is the plan's. The document must be opened with
    /// <see cref="PdfOpenOptions.AllowEditing"/>. Save the result with <c>Save</c> or <c>SaveIncremental</c>; saving with
    /// <c>PdfSaveOptions.PdfUA</c> then checks it like any tagged document. The call is atomic for content problems: when a
    /// page cannot be read completely, or the plan is inconsistent, it throws and the document is left as it was (new objects it added
    /// stay unreferenced).
    /// </remarks>
    /// <exception cref="InvalidOperationException">The document is not editable, or the plan was applied already.</exception>
    /// <exception cref="ArgumentException">The plan belongs to another document.</exception>
    /// <exception cref="PdfTaggingException">The document is tagged already, a reference is not from this document, the same content is referenced twice, the nesting is impossible, or a page cannot be read completely.</exception>
    /// <exception cref="PdfLimitException">A page would exceed the operator limit once tagged, or the tree is too large.</exception>
    public PdfTaggingResult ApplyTagging(PdfTaggingPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ThrowIfDisposed();
        return Tagging.TaggingPass.Run(this, plan, cancellationToken);
    }
}
