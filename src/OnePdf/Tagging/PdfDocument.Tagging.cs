using OnePdf.Text;

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

    /// <summary>
    /// The happy path, step one: builds a tagging plan from the role a delegate gives each block of an extraction, following the
    /// mechanical rules in <c>docs/tagging.md</c> (list items grouped into one L with a Lbl and a LBody, table rows and cells from
    /// the lines and their cells, headers and footers as pagination artifacts, links as Link elements with an object reference, everything
    /// else an artifact). Nothing is changed in the document; change the plan if you like, then pass it to <see cref="ApplyTagging"/>.
    /// </summary>
    /// <param name="text">The extraction of this document, read with <see cref="PdfTextOptions.RetainGlyphs"/> true. Extract the whole document, so running headers and footers are found.</param>
    /// <param name="role">Called once for each block that is not a header or footer (unless <see cref="PdfTagOptions.KeepHeadersAndFooters"/>); returns the block's role, or null for an artifact. OnePDF never guesses a role.</param>
    /// <param name="options">Title, language, figures and the tunable defaults; null for the defaults.</param>
    /// <returns>The plan, with the warnings (for example a link that covers no text) in <see cref="PdfTaggingPlan.Warnings"/>.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="PdfLimitException">The tree would be too deep or too large.</exception>
    public PdfTaggingPlan PlanTagging(IReadOnlyList<PdfPageText> text, Func<PdfTagBlock, PdfBlockRole?> role, PdfTagOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(role);
        ThrowIfDisposed();
        PdfTaggingPlan plan = Tagging.HappyPath.Build(this, text, role, options ?? new PdfTagOptions(), out IReadOnlyList<string> warnings);
        plan.AddWarnings(warnings);
        return plan;
    }

    /// <summary>
    /// The happy path: tags the document from the roles a delegate gives the blocks of an extraction. It is
    /// <see cref="PlanTagging"/> followed by <see cref="ApplyTagging"/>, and nothing more. Save afterwards with <c>Save</c>,
    /// <c>SaveIncremental</c> or <c>Save</c> with <c>PdfSaveOptions.PdfUA</c>.
    /// </summary>
    /// <param name="text">The extraction of this document, read with <see cref="PdfTextOptions.RetainGlyphs"/> true and the structure tree not used.</param>
    /// <param name="role">Gives each block its role, or null for an artifact.</param>
    /// <param name="options">Title, language, figures and the tunable defaults; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the call; the document is then unchanged.</param>
    /// <returns>What was written; its warnings include those of the plan.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    /// <exception cref="PdfTaggingException">See <see cref="ApplyTagging"/>.</exception>
    /// <exception cref="PdfLimitException">A limit was exceeded.</exception>
    public PdfTaggingResult Tag(IReadOnlyList<PdfPageText> text, Func<PdfTagBlock, PdfBlockRole?> role, PdfTagOptions? options = null, CancellationToken cancellationToken = default)
    {
        PdfTaggingPlan plan = PlanTagging(text, role, options);
        PdfTaggingResult result = ApplyTagging(plan, cancellationToken);
        result.WarningList.InsertRange(0, plan.Warnings);
        return result;
    }
}
