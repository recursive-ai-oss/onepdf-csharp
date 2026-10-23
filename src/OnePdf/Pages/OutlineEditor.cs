namespace OnePdf;

/// <summary>One bookmark of an outline being edited.</summary>
internal sealed class OutlineNode(PdfDictionary dictionary, bool open)
{
    public PdfDictionary Dictionary { get; } = dictionary;

    public List<OutlineNode> Kids { get; set; } = [];

    public bool Open { get; } = open;

    public bool Alive { get; set; } = true;

    public long Visible { get; set; }
}

/// <summary>Reads the outline tree into nodes and writes it back with consistent links and counts (ISO 32000-2 12.3.3, Tables 150 and 151).</summary>
internal static class OutlineEditor
{
    /// <summary>
    /// The top-level items of the document's outline with their descendants. Iterative; an item reached twice, an item that is not
    /// an indirect object and everything beyond the document's limits are left out.
    /// </summary>
    public static List<OutlineNode> Read(PdfDocument doc)
    {
        var top = new List<OutlineNode>();
        if (doc.Catalog.GetDictionary("Outlines")?.GetDictionary("First") is not { } first)
        {
            return top;
        }

        var visited = new HashSet<int>();
        var stack = new Stack<(PdfDictionary? Item, List<OutlineNode> Sink, int Depth)>();
        stack.Push((first, top, 1));
        long count = 0;
        while (stack.Count > 0)
        {
            (PdfDictionary? cur, List<OutlineNode> sink, int depth) = stack.Pop();
            if (cur?.Reference is not { } r || !visited.Add(r.Number) || ++count > doc.Limits.MaxObjects)
            {
                continue;
            }

            var node = new OutlineNode(cur, cur.GetInt("Count") > 0);
            sink.Add(node);
            stack.Push((cur.GetDictionary("Next"), sink, depth));
            if (cur.GetDictionary("First") is { } child && depth < doc.Limits.MaxNestingDepth)
            {
                stack.Push((child, node.Kids, depth + 1));
            }
        }

        return top;
    }

    /// <summary>The nodes in pre-order, so that walking the list backwards visits every item after its descendants.</summary>
    public static List<OutlineNode> PreOrder(List<OutlineNode> top)
    {
        var order = new List<OutlineNode>();
        var stack = new Stack<OutlineNode>();
        for (int i = top.Count - 1; i >= 0; i--)
        {
            stack.Push(top[i]);
        }

        while (stack.Count > 0)
        {
            OutlineNode n = stack.Pop();
            order.Add(n);
            for (int i = n.Kids.Count - 1; i >= 0; i--)
            {
                stack.Push(n.Kids[i]);
            }
        }

        return order;
    }

    /// <summary>
    /// Makes the document's outline exactly <paramref name="top"/>: <c>/Parent</c>, <c>/First</c>, <c>/Last</c>, <c>/Prev</c>,
    /// <c>/Next</c> and <c>/Count</c> are rewritten (a closed item keeps its negative count). Items that are not yet indirect
    /// objects are added. An empty list leaves an outline root with no items.
    /// </summary>
    public static void Write(PdfDocument doc, List<OutlineNode> top)
    {
        PdfDictionary catalog = doc.Catalog;
        PdfDictionary? root = catalog.GetDictionary("Outlines");
        if (root?.Reference is null)
        {
            if (top.Count == 0)
            {
                return;
            }

            var fresh = new PdfDictionary();
            fresh.SetEntry("Type", new PdfName("Outlines"));
            catalog.SetEntry("Outlines", doc.AddObject(fresh));
            root = catalog.GetDictionary("Outlines")!;
        }

        List<OutlineNode> order = PreOrder(top);
        foreach (OutlineNode n in order)
        {
            if (n.Dictionary.Reference is null)
            {
                doc.AddObject(n.Dictionary);
            }
        }

        // Counts first: a node's descendants come after it in pre-order.
        for (int i = order.Count - 1; i >= 0; i--)
        {
            OutlineNode n = order[i];
            long visible = 0;
            foreach (OutlineNode k in n.Kids)
            {
                visible += 1 + (k.Open ? k.Visible : 0);
            }

            n.Visible = visible;
        }

        Link(root, top, openRoot: true);
        foreach (OutlineNode n in order)
        {
            Link(n.Dictionary, n.Kids, n.Open);
        }
    }

    // Sets the links of a parent and of its children.
    private static void Link(PdfDictionary parent, List<OutlineNode> kids, bool openRoot)
    {
        if (kids.Count == 0)
        {
            parent.RemoveEntry("First");
            parent.RemoveEntry("Last");
            parent.RemoveEntry("Count");
            return;
        }

        long visible = 0;
        foreach (OutlineNode k in kids)
        {
            visible += 1 + (k.Open ? k.Visible : 0);
        }

        parent.SetEntry("First", kids[0].Dictionary.Reference);
        parent.SetEntry("Last", kids[^1].Dictionary.Reference);
        parent.SetEntry("Count", new PdfInteger(openRoot ? visible : -visible));
        for (int i = 0; i < kids.Count; i++)
        {
            PdfDictionary d = kids[i].Dictionary;
            d.SetEntry("Parent", parent.Reference);
            d.SetEntry("Prev", i > 0 ? kids[i - 1].Dictionary.Reference : null);
            d.SetEntry("Next", i + 1 < kids.Count ? kids[i + 1].Dictionary.Reference : null);
        }
    }
}
