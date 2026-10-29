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
    /// <see cref="RemoveTags"/> and <see cref="ApplyTagging"/> replace the tree, and a <see cref="PdfStructureTree.IsStale"/> tree throws when its
    /// children are read or it is edited; read the property again. After raw edits to the tree's dictionaries call <see cref="RefreshStructureTree"/>.
    /// </remarks>
    public PdfStructureTree? StructureTree
    {
        get
        {
            ThrowIfDisposed();
            // A tree whose root is no longer the catalog's /StructTreeRoot (replaced by a raw edit, a tagging call or a save preparation) is stale.
            if (_structureTreeRead && RootChanged())
            {
                ForgetStructureTree();
            }

            if (!_structureTreeRead)
            {
                _structureTree = PdfStructureTree.Read(this);
                _structureTreeRead = true;
            }

            return _structureTree;
        }
    }

    /// <summary>
    /// Drops the cached <see cref="StructureTree"/> and reads it again from the document the next time it is asked for, and returns it.
    /// Call it after changing the structure tree through its dictionaries (<see cref="PdfStructureNode.Dictionary"/> or the catalog) instead of
    /// the typed edit methods. The old tree object and every node and reference read from it are then stale: reading their children or
    /// editing them throws <see cref="InvalidOperationException"/> (<see cref="PdfStructureTree.IsStale"/> is true), never old data. The
    /// property alone also re-reads when the catalog's <c>/StructTreeRoot</c> object was replaced, but it cannot see edits inside the same
    /// root; this method can.
    /// </summary>
    /// <returns>The freshly read tree, or null when the document has no <c>/StructTreeRoot</c>.</returns>
    public PdfStructureTree? RefreshStructureTree()
    {
        ThrowIfDisposed();
        ForgetStructureTree();
        return StructureTree;
    }

    private bool RootChanged()
    {
        // Compared by object number, not instance: the object cache may re-parse an unchanged root into a new instance.
        PdfObject? raw = Catalog.GetRaw("StructTreeRoot");
        if (_structureTree is null)
        {
            return Catalog.Get("StructTreeRoot") is PdfDictionary;
        }

        return raw is PdfReference r
            ? r.Number != _structureTree.RootDict.Reference?.Number
            : !ReferenceEquals(raw, _structureTree.RootDict);
    }

    internal void ForgetStructureTree()
    {
        _structureTree?.MarkStale();
        _structureTree = null;
        _structureTreeRead = false;
    }
}
