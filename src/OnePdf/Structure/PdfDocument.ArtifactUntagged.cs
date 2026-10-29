namespace OnePdf;

public sealed partial class PdfDocument
{
    /// <summary>
    /// Marks painted content that is neither tagged nor an artifact as an artifact, in place, in a document that is already tagged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A tagged file can still paint paths, images, shadings, inline images and Form XObjects outside any marked-content sequence;
    /// ISO 14289-1 7.1 requires such content to be real content or an artifact, and the PDF/UA check reports it. This call wraps each
    /// of those painting operations in an <c>/Artifact</c> sequence (<c>BMC … EMC</c>) in the page content, together with the path
    /// construction that belongs to a path. Only the two operators are inserted, around whole operations, so the page paints,
    /// extracts and interprets the same. Content inside any existing marked-content sequence is left alone.
    /// </para>
    /// <para>
    /// A Form XObject drawn outside marked content is wrapped where it is drawn, never inside, so a form that is also drawn within tagged
    /// content keeps working there. A form is left alone (counted in <see cref="PdfArtifactResult.FormsLeft"/>) when it shows text
    /// outside marked content, has tagged content of its own, or cannot be read. Text outside marked content is never wrapped: whether it
    /// is decoration is the caller's decision, so it is only counted per page. Annotation appearances, and content reached only from
    /// patterns or from inside other forms, are not edited.
    /// </para>
    /// <para>Pages whose content cannot be decoded or ends inside an inline image are left as they are and named in <see cref="PdfArtifactResult.Warnings"/>.</para>
    /// </remarks>
    /// <param name="cancellationToken">Cancels the scan; the document is unchanged then.</param>
    /// <returns>What was wrapped and what was left, with figures per page.</returns>
    /// <exception cref="InvalidOperationException">The document is not editable (<see cref="PdfOpenOptions.AllowEditing"/>).</exception>
    /// <exception cref="PdfLimitException">A limit was exceeded.</exception>
    public PdfArtifactResult ArtifactUntaggedGraphics(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Structure.ArtifactUntaggedPass.Run(this, cancellationToken);
    }
}
