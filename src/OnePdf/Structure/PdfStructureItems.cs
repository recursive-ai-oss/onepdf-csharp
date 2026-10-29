namespace OnePdf;

/// <summary>What <see cref="PdfStructureItem.Remove"/> does with the item and what is below it.</summary>
public enum PdfStructureRemoval
{
    /// <summary>
    /// The element goes and its children take its place in the parent, in order. Marked content stays tagged, now by the parent. For a
    /// marked-content reference or an object reference this is the same as <see cref="ToArtifacts"/>, which is the only way to take
    /// a content item out of the tree without leaving it untagged.
    /// </summary>
    Unwrap,

    /// <summary>
    /// The item and everything below it goes. Marked content below it is turned into <c>/Artifact</c> in its content streams, so
    /// nothing becomes untagged, and annotations and XObjects it referenced lose their <c>/StructParent</c>.
    /// </summary>
    ToArtifacts,
}

/// <summary>
/// One child in the logical structure tree: a structure element (<see cref="PdfStructureNode"/>), a reference to marked content
/// (<see cref="PdfMarkedContentReference"/>) or a reference to an object such as an annotation (<see cref="PdfObjectReference"/>).
/// </summary>
/// <remarks>
/// The tree is read once and then edited in place: every edit changes the document at once (<c>/K</c>, <c>/P</c>, the parent tree and
/// <c>/StructParents</c> stay consistent) and needs <see cref="PdfOpenOptions.AllowEditing"/>. Instances are not thread-safe.
/// </remarks>
public abstract class PdfStructureItem
{
    internal PdfStructureItem(PdfStructureTree tree, PdfObject raw)
    {
        Tree = tree;
        Raw = raw;
    }

    /// <summary>The tree the item belongs to.</summary>
    public PdfStructureTree Tree { get; }

    /// <summary>The element that holds the item, or null for a child of the tree's root.</summary>
    public PdfStructureNode? Parent { get; internal set; }

    /// <summary>True once the item was removed from the tree; further edits throw <see cref="InvalidOperationException"/>.</summary>
    public bool IsRemoved { get; internal set; }

    /// <summary>The item as it stands in its parent's <c>/K</c>: a reference, a dictionary or an integer.</summary>
    internal PdfObject Raw { get; set; }

    /// <summary>The zero-based page the item belongs to, or -1 when no page is named (an element takes its <c>/Pg</c>, else its nearest ancestor's, else its first content item's).</summary>
    public abstract int PageIndex { get; }

    /// <summary>The position among the parent's (or the root's) children.</summary>
    public int Index => Tree.KidsOf(Parent).IndexOf(this);

    /// <summary>
    /// Moves the item under <paramref name="parent"/> (null for the root) at <paramref name="index"/> (counted after the item has left
    /// its old place; -1 or the child count appends). <c>/K</c>, <c>/P</c> and the parent tree follow.
    /// </summary>
    /// <exception cref="InvalidOperationException">The document is not editable, or an item is removed.</exception>
    /// <exception cref="ArgumentException">The parent is from another tree, or is the item or below it.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the parent's children.</exception>
    public void MoveTo(PdfStructureNode? parent, int index = -1) => Tree.Move(this, parent, index);

    /// <summary>Removes the item from the tree. See <see cref="PdfStructureRemoval"/>.</summary>
    /// <exception cref="InvalidOperationException">The document is not editable, or the item is removed already.</exception>
    public void Remove(PdfStructureRemoval mode = PdfStructureRemoval.Unwrap) => Tree.RemoveItem(this, mode);
}

/// <summary>A reference to marked content: the sequence with this <c>/MCID</c> on a page, or in a form XObject drawn on it.</summary>
public sealed class PdfMarkedContentReference : PdfStructureItem
{
    internal PdfMarkedContentReference(PdfStructureTree tree, PdfObject raw, int page, PdfStream? stream, int mcid)
        : base(tree, raw)
    {
        Page = page;
        Stream = stream;
        Mcid = mcid;
    }

    internal int Page { get; set; }

    /// <inheritdoc/>
    public override int PageIndex => Page;

    /// <summary>The form XObject (or appearance stream) the sequence is in (<c>/Stm</c>), or null when it is in the page's own content.</summary>
    public PdfStream? Stream { get; }

    /// <summary>The marked-content identifier.</summary>
    public int Mcid { get; }
}

/// <summary>A reference to an object, normally an annotation or an XObject (<c>OBJR</c>).</summary>
public sealed class PdfObjectReference : PdfStructureItem
{
    internal PdfObjectReference(PdfStructureTree tree, PdfObject raw, int page, PdfObject? target)
        : base(tree, raw)
    {
        Page = page;
        Target = target;
    }

    internal int Page { get; set; }

    /// <inheritdoc/>
    public override int PageIndex => Page;

    /// <summary>The referenced object (<c>/Obj</c>), resolved; null when it is missing.</summary>
    public PdfObject? Target { get; }

    /// <summary>True when the object is an annotation (a dictionary with a <c>/Subtype</c> that is not a stream).</summary>
    public bool IsAnnotation => Target is PdfDictionary d && d.ContainsKey("Subtype");
}
