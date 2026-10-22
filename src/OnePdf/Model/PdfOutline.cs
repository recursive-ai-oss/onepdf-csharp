namespace OnePdf;

/// <summary>An entry of the document outline (bookmarks, ISO 32000-2 12.3.3).</summary>
public sealed class PdfOutlineItem
{
    private readonly PdfDocument _doc;
    private readonly List<PdfOutlineItem> _children;
    private Lazy<PdfDestination?> _destination = null!;
    private Lazy<PdfAction?> _action = null!;

    internal PdfOutlineItem(PdfDocument doc, PdfDictionary dictionary, List<PdfOutlineItem> children)
    {
        _doc = doc;
        Dictionary = dictionary;
        _children = children;
        _action = new Lazy<PdfAction?>(() => dictionary.GetDictionary("A") is { } a ? new PdfAction(doc, a) : null, LazyThreadSafetyMode.ExecutionAndPublication);
        _destination = new Lazy<PdfDestination?>(
            () => dictionary.ContainsKey("Dest") ? doc.ResolveDestination(dictionary.Get("Dest"), 0) : Action?.Destination,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The outline item dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The title text; empty when missing.</summary>
    public string Title => Dictionary.GetString("Title")?.GetText() ?? string.Empty;

    /// <summary>The destination (<c>/Dest</c>, else the destination of a GoTo action) with the page resolved to an index; null when there is none.</summary>
    public PdfDestination? Destination => _destination.Value;

    /// <summary>The action <c>/A</c>, or null. Data only.</summary>
    public PdfAction? Action => _action.Value;

    /// <summary>The text color <c>/C</c> as red, green, blue in 0..1; null when absent.</summary>
    public IReadOnlyList<double>? Color =>
        Dictionary.Get("C") is PdfArray { Count: >= 3 } a ? [a.GetDouble(0), a.GetDouble(1), a.GetDouble(2)] : null;

    /// <summary>True when the title is shown in italics (flag bit 1).</summary>
    public bool IsItalic => (Dictionary.GetInt("F") & 1) != 0;

    /// <summary>True when the title is shown in bold (flag bit 2).</summary>
    public bool IsBold => (Dictionary.GetInt("F") & 2) != 0;

    /// <summary>True when the item is shown expanded: <c>/Count</c> is positive.</summary>
    public bool IsOpen => Dictionary.GetInt("Count") > 0;

    /// <summary>The child items in order.</summary>
    public IReadOnlyList<PdfOutlineItem> Children => _children;

    internal PdfDocument Document => _doc;
}

public sealed partial class PdfDocument
{
    /// <summary>
    /// The top-level outline items, empty when the document has no outline. Built iteratively over the <c>/First</c> and
    /// <c>/Next</c> links: an item reached twice (a cycle) ends that branch with a diagnostic, nesting beyond
    /// <see cref="PdfLimits.MaxNestingDepth"/> and more than <see cref="PdfLimits.MaxObjects"/> items are cut off. Computed once.
    /// </summary>
    public IReadOnlyList<PdfOutlineItem> Outline => _outline.Value;

    private Lazy<IReadOnlyList<PdfOutlineItem>> _outline = null!;

    private List<PdfOutlineItem> LoadOutline()
    {
        var top = new List<PdfOutlineItem>();
        PdfDictionary? root = Catalog.GetDictionary("Outlines");
        if (root?.GetDictionary("First") is not { } first)
        {
            return top;
        }

        var visited = new HashSet<int>();
        var stack = new Stack<(PdfDictionary? Next, List<PdfOutlineItem> Sink, int Depth)>();
        stack.Push((first, top, 1));
        long count = 0;
        while (stack.Count > 0)
        {
            (PdfDictionary? cur, List<PdfOutlineItem> sink, int depth) = stack.Pop();
            if (cur is null)
            {
                continue;
            }

            if (cur.Reference is not { } r || !visited.Add(r.Number))
            {
                _diag.Warn("An outline item is visited twice or is not an indirect object; the rest of that branch was skipped.", cur.Reference?.Number ?? -1);
                continue;
            }

            if (++count > Limits.MaxObjects)
            {
                _diag.Warn("The outline has more items than the object limit allows; the rest was ignored.");
                break;
            }

            var children = new List<PdfOutlineItem>();
            sink.Add(new PdfOutlineItem(this, cur, children));

            // The sibling goes below the child branch so the whole subtree is built before the next sibling.
            stack.Push((cur.GetDictionary("Next"), sink, depth));
            if (cur.GetDictionary("First") is { } child)
            {
                if (depth >= Limits.MaxNestingDepth)
                {
                    _diag.Warn("Outline items are nested too deeply; the deeper items were ignored.", r.Number);
                }
                else
                {
                    stack.Push((child, children, depth + 1));
                }
            }
        }

        return top;
    }
}
