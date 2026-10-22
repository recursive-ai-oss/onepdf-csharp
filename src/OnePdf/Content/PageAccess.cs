namespace OnePdf.Content;

/// <summary>Finds a page dictionary by index with the page tree walk the interpreter needs until the document model offers it.</summary>
internal static class PageAccess
{
    /// <summary>The page dictionary at zero-based <paramref name="index"/>, or null when the document has fewer pages.</summary>
    public static PdfDictionary? GetPage(PdfDocument doc, int index)
    {
        PdfDictionary? root = doc.Catalog.GetDictionary("Pages");
        if (root is null || index < 0)
        {
            return null;
        }

        var visited = new HashSet<int>();
        var stack = new Stack<PdfDictionary>();
        stack.Push(root);
        int seen = 0;
        long nodes = 0;
        while (stack.Count > 0)
        {
            PdfDictionary node = stack.Pop();
            if (node.Reference is { } r && !visited.Add(r.Number))
            {
                continue;
            }

            if (++nodes > doc.Limits.MaxObjects)
            {
                return null;
            }

            if (node.GetArray("Kids") is not { } kids)
            {
                if (node.GetName("Type") != "Pages")
                {
                    if (seen++ == index)
                    {
                        return node;
                    }
                }

                continue;
            }

            for (int i = kids.Count - 1; i >= 0; i--)
            {
                if (kids[i] is PdfDictionary child)
                {
                    stack.Push(child);
                }
            }
        }

        return null;
    }
}
