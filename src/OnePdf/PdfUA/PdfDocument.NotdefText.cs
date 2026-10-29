using OnePdf.PdfUA;

namespace OnePdf;

public sealed partial class PdfDocument
{
    /// <summary>
    /// Lists the text operators that show the <c>.notdef</c> glyph of an embedded font: a character code for which the font program has
    /// no glyph. ISO 14289-1 7.21.8 forbids that whatever the text rendering mode, and hidden text layers of scanned pages (rendering
    /// mode 3, often in optional content with a name such as an OCR layer) are where it is usually found. Nothing is changed.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// One entry per operator and page it is reached from, in page order: where it is, the font, the offending codes, the rendering
    /// mode, whether the text is drawn, the optional content groups it is in and the text it extracts as. Only fonts whose program
    /// is embedded and readable count; text in a font without a program has no <c>.notdef</c> to speak of. Text in Type 3 glyph
    /// procedures and tiling patterns is not examined.
    /// </returns>
    /// <remarks>Reads every page like the interpreter does (<see cref="PdfLimits"/> apply). Damaged content is skipped and left to the document's diagnostics.</remarks>
    /// <exception cref="PdfLimitException">A limit was exceeded.</exception>
    public IReadOnlyList<PdfNotdefRun> FindNotdefText(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return NotdefPass.Find(this, cancellationToken);
    }

    /// <summary>
    /// Takes the character codes that select <c>.notdef</c> out of the strings that show them, so ISO 14289-1 7.21.8 is met. By default
    /// only text that nobody sees is touched (<see cref="PdfNotdefScope.Invisible"/>), so nothing the page draws changes.
    /// </summary>
    /// <param name="options">Scope, dry run and a per-run filter; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the call; the document is then unchanged.</param>
    /// <returns>What was removed and what was kept, with the reason for each kept operator.</returns>
    /// <remarks>
    /// <para>A removed code is replaced by a <c>TJ</c> adjustment of its advance (the glyph width, character spacing and, for a space,
    /// word spacing), so the glyphs after it stay where they were and a hidden text layer still lines up with the scan. The operator
    /// becomes a <c>TJ</c> (a <c>'</c> or <c>"</c> operator becomes <c>T*</c>, with <c>Tw</c> and <c>Tc</c> set first, then <c>TJ</c>).</para>
    /// <para>Text extraction changes by exactly the removed codes' Unicode text (<see cref="PdfNotdefRun.NotdefText"/>): for example a
    /// hidden word separator mapped to a space is gone, so extractors that rely on the characters rather than on positions see the
    /// neighbouring words joined. Visible output is identical when the scope is <see cref="PdfNotdefScope.Invisible"/>.</para>
    /// <para>A form XObject is edited once, and only when every use of it agrees (same font, same text state, same visibility, and the
    /// operator is the same for all); otherwise its operators are kept and listed with <see cref="PdfNotdefKeepReason.SharedFormDisagrees"/>.
    /// Page content is rewritten as one new stream. Signatures are invalidated by a full save; an incremental save keeps the signed revision.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The document is not editable (<see cref="PdfOpenOptions.AllowEditing"/>); not thrown with <see cref="PdfNotdefRemoveOptions.DryRun"/>.</exception>
    /// <exception cref="PdfLimitException">A limit was exceeded.</exception>
    public PdfNotdefRemoval RemoveNotdefText(PdfNotdefRemoveOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return NotdefPass.Remove(this, options ?? new PdfNotdefRemoveOptions(), cancellationToken);
    }
}
