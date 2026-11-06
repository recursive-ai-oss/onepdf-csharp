using OnePdf.Internal;

namespace OnePdf.Writing;

/// <summary>
/// Works out how a full save writes the page tree of a document whose tree lists entries that are not pages. A walk applies the
/// same rules as <see cref="PdfDocument.Pages"/> (ISO 32000-2 7.7.3): a <c>/Kids</c> entry that is null, missing, not a dictionary, a
/// dictionary that is not a page, or already visited (a cycle or a page listed twice) is left out, a page-tree node left with no
/// page is left out, and every <c>/Count</c> is the number of pages below the node as written. The nodes that need to change are
/// returned as replacement dictionaries keyed by object number; the document itself is not touched. Pages keep their own
/// dictionaries, so the attributes they inherit (7.7.3.4) still come from the same ancestors.
/// </summary>
internal static class PageTreeRepair
{
    private sealed class Frame(PdfDictionary node, PdfArray kids, PdfObject? rawInParent)
    {
        public PdfDictionary Node { get; } = node;

        public PdfArray Kids { get; } = kids;

        public PdfObject? RawInParent { get; } = rawInParent;

        public int Next { get; set; }

        public List<PdfObject> Kept { get; } = [];

        public long Count { get; set; }

        public bool Changed { get; set; }
    }

    /// <summary>The replacement dictionaries by object number, or null when the tree is sound and nothing needs to change.</summary>
    public static Dictionary<int, PdfDictionary>? Compute(PdfDocument doc)
    {
        PdfDictionary? root = doc.Catalog.GetDictionary("Pages");
        if (root?.Reference is not { } rootRef || root.GetArray("Kids") is not { } rootKids)
        {
            return null;
        }

        var overrides = new Dictionary<int, PdfDictionary>();
        var visited = new HashSet<int> { rootRef.Number };
        var stack = new List<Frame> { new(root, rootKids, null) };
        long nodes = 1;
        while (stack.Count > 0)
        {
            Frame f = stack[^1];
            if (f.Next < f.Kids.Count)
            {
                int i = f.Next++;
                PdfObject raw = f.Kids.GetRaw(i);
                if (f.Kids[i] is not PdfDictionary child)
                {
                    f.Changed = true;
                    continue;
                }

                if (child.Reference is { } r && !visited.Add(r.Number))
                {
                    f.Changed = true;
                    continue;
                }

                if (++nodes > doc.Limits.MaxObjects)
                {
                    throw new PdfLimitException("The page tree has more nodes than the object limit allows.");
                }

                if (child.GetArray("Kids") is { } childKids)
                {
                    if (stack.Count > doc.Limits.MaxNestingDepth)
                    {
                        f.Changed = true;
                    }
                    else
                    {
                        stack.Add(new Frame(child, childKids, raw));
                    }
                }
                else if (child.GetName("Type") != "Pages" && PdfDocument.IsPageLeaf(child))
                {
                    f.Kept.Add(raw);
                    f.Count++;
                }
                else
                {
                    f.Changed = true;
                }

                continue;
            }

            stack.RemoveAt(stack.Count - 1);
            bool mismatch = f.Changed || f.Node.GetInt("Count", -1) != f.Count;
            if (stack.Count == 0)
            {
                if (mismatch)
                {
                    overrides[rootRef.Number] = Fixed(f);
                }

                break;
            }

            Frame parent = stack[^1];
            if (f.Count == 0)
            {
                // A node with no page below it adds nothing to the file.
                parent.Changed = true;
                continue;
            }

            parent.Count += f.Count;
            if (!mismatch)
            {
                parent.Kept.Add(f.RawInParent!);
            }
            else if (f.Node.Reference is { } nodeRef)
            {
                overrides[nodeRef.Number] = Fixed(f);
                parent.Kept.Add(f.RawInParent!);
            }
            else
            {
                // A node written directly in its parent's array has no number to replace under; the parent holds the corrected copy.
                parent.Kept.Add(Fixed(f));
                parent.Changed = true;
            }
        }

        return overrides.Count > 0 ? overrides : null;
    }

    private static PdfDictionary Fixed(Frame f)
    {
        var copy = new PdfDictionary(null);
        PdfDictionary src = f.Node;
        for (int i = 0; i < src.RawCount; i++)
        {
            string key = src.KeyAt(i);
            if (key is not ("Kids" or "Count"))
            {
                copy.Set(key, src.ValueAt(i));
            }
        }

        var kids = new PdfArray(null);
        foreach (PdfObject k in f.Kept)
        {
            kids.Add(k);
        }

        copy.Set("Kids", kids);
        copy.Set("Count", new PdfInteger(f.Count));
        return copy;
    }
}
