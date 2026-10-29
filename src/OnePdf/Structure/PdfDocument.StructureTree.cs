namespace OnePdf;

public sealed partial class PdfDocument
{
    private PdfStructureTree? _structureTree;
    private bool _structureTreeRead;

    /// <summary>
    /// The logical structure tree read into typed objects (<see cref="PdfStructureTree"/>), or null when the document has no
    /// <c>/StructTreeRoot</c>. It is read on first access and kept; reading never changes the document, never throws on a malformed
    /// tree and records what is wrong in <see cref="PdfStructureTree.Diagnostics"/>. Edits need <see cref="PdfOpenOptions.AllowEditing"/>.
    /// See <c>docs/structure.md</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="RemoveTags"/> and <see cref="ApplyTagging"/> replace the tree, so a tree object read before them is stale; read the property again.
    /// </remarks>
    public PdfStructureTree? StructureTree
    {
        get
        {
            ThrowIfDisposed();
            if (!_structureTreeRead)
            {
                _structureTree = PdfStructureTree.Read(this);
                _structureTreeRead = true;
            }

            return _structureTree;
        }
    }

    internal void ForgetStructureTree()
    {
        _structureTree = null;
        _structureTreeRead = false;
    }
}
