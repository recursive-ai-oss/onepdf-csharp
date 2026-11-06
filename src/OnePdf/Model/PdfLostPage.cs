namespace OnePdf;

/// <summary>Why an entry of the page tree is not a page of the document (see <see cref="PdfLostPage"/>).</summary>
public enum PdfLostPageReason
{
    /// <summary>The <c>/Kids</c> entry is the null object.</summary>
    Null,

    /// <summary>The <c>/Kids</c> entry is a reference to an object the file does not contain (or could not be recovered).</summary>
    MissingObject,

    /// <summary>The <c>/Kids</c> entry is neither a dictionary nor null (a number, a name, an array, a stream, …).</summary>
    NotADictionary,

    /// <summary>The entry is a dictionary but not a page or a page-tree node (for example a font or an annotation).</summary>
    NotAPage,

    /// <summary>The entry was already visited: a page listed twice, or a node that is its own ancestor (a cycle). Its first occurrence is kept.</summary>
    Repeated,

    /// <summary>The page tree is nested deeper than <see cref="PdfLimits.MaxNestingDepth"/> allows at this entry.</summary>
    TooDeep,
}

/// <summary>
/// A place in the page tree that does not lead to a page. The page tree of a damaged file can list pages that the file no
/// longer holds; <see cref="PdfDocument.Pages"/> skips them and <see cref="PdfDocument.LostPages"/> lists them, so a caller can
/// tell that the document it opened has fewer pages than it declared. A full save leaves these entries out and writes
/// <c>/Count</c> values that match (ISO 32000-2 7.7.3.2).
/// </summary>
public sealed class PdfLostPage
{
    internal PdfLostPage(int index, PdfLostPageReason reason, int objectNumber, int parentObjectNumber)
    {
        Index = index;
        Reason = reason;
        ObjectNumber = objectNumber > 0 ? objectNumber : null;
        ParentObjectNumber = parentObjectNumber > 0 ? parentObjectNumber : null;
    }

    /// <summary>
    /// The zero-based position this entry would have had if every entry of the page tree were a page, counted in the order
    /// the tree declares them. With pages 0, 1 and 3 intact and a lost entry between 1 and 3, the lost entry is index 2 and the
    /// page after it keeps index 3 here although it is <c>doc.Pages[2]</c>. A lost page-tree node whose size is unknown counts
    /// as one entry.
    /// </summary>
    public int Index { get; }

    /// <summary>Why the entry is lost.</summary>
    public PdfLostPageReason Reason { get; }

    /// <summary>The object number the entry referred to, or null when it was a direct object or a null.</summary>
    public int? ObjectNumber { get; }

    /// <summary>The object number of the page-tree node that listed the entry, or null when that node is a direct object.</summary>
    public int? ParentObjectNumber { get; }

    /// <summary>A one-line description for logs.</summary>
    public override string ToString() =>
        $"Page tree entry {Index}: {Reason}" + (ObjectNumber is { } n ? $" (object {n})" : string.Empty);
}
