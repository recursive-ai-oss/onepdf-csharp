namespace OnePdf;

public sealed partial class PdfDocument
{
    /// <summary>
    /// Reads every key/value pair of a name tree (<paramref name="numbers"/> false, keys are strings) or number tree (keys are
    /// integers), ISO 32000-2 7.9.6 and 7.9.7. Iterative; each node is visited once; node and entry counts are bounded by
    /// <see cref="PdfLimits.MaxObjects"/> and depth by <see cref="PdfLimits.MaxNestingDepth"/>. Keys are resolved, values are
    /// left as stored (possibly references). Malformed pairs are skipped without a diagnostic per pair; one is recorded per tree.
    /// </summary>
    internal List<KeyValuePair<PdfObject, PdfObject>> ReadTree(PdfDictionary? root, bool numbers, string what)
    {
        var result = new List<KeyValuePair<PdfObject, PdfObject>>();
        if (root is null)
        {
            return result;
        }

        string leafKey = numbers ? "Nums" : "Names";
        var visited = new HashSet<int>();
        var stack = new Stack<(PdfDictionary Node, int Depth)>();
        stack.Push((root, 0));
        long nodes = 0;
        bool bad = false;
        while (stack.Count > 0)
        {
            (PdfDictionary node, int depth) = stack.Pop();
            if (node.Reference is { } r && !visited.Add(r.Number))
            {
                bad = true;
                continue;
            }

            if (++nodes > Limits.MaxObjects || depth > Limits.MaxNestingDepth)
            {
                _diag.Warn($"The {what} tree is too large or too deep; the rest was ignored.", node.Reference?.Number ?? -1);
                break;
            }

            if (node.Get(leafKey) is PdfArray leaf)
            {
                for (int i = 0; i + 1 < leaf.Count; i += 2)
                {
                    PdfObject? key = leaf[i];
                    if (numbers ? key is PdfInteger : key is PdfString)
                    {
                        if (result.Count >= Limits.MaxObjects)
                        {
                            _diag.Warn($"The {what} tree has more entries than the object limit allows; the rest was ignored.");
                            return result;
                        }

                        result.Add(new KeyValuePair<PdfObject, PdfObject>(key, leaf.GetRaw(i + 1)));
                    }
                    else
                    {
                        bad = true;
                    }
                }
            }

            if (node.Get("Kids") is PdfArray kids)
            {
                for (int i = kids.Count - 1; i >= 0; i--)
                {
                    if (kids[i] is PdfDictionary kid)
                    {
                        stack.Push((kid, depth + 1));
                    }
                    else
                    {
                        bad = true;
                    }
                }
            }
        }

        if (bad)
        {
            _diag.Warn($"The {what} tree has malformed or repeated nodes or keys; they were skipped.", root.Reference?.Number ?? -1);
        }

        return result;
    }
}
