namespace OnePdf;

/// <summary>Builds name trees and number trees (ISO 32000-2 7.9.6, 7.9.7) with bounded fan-out, so a large tree does not become one huge array.</summary>
internal static class NumberTree
{
    private const int Fanout = 64;

    /// <summary>
    /// Builds the root of a tree over <paramref name="entries"/>, which must be sorted by key. Leaf and intermediate nodes are stored
    /// with <paramref name="store"/> and referenced; the root dictionary is returned for the caller to store or embed.
    /// Keys are <see cref="PdfInteger"/> for a number tree and <see cref="PdfString"/> for a name tree.
    /// </summary>
    public static PdfDictionary Build(IReadOnlyList<(PdfObject Key, PdfObject Value)> entries, bool names, Func<PdfDictionary, PdfReference> store)
    {
        string leafKey = names ? "Names" : "Nums";
        if (entries.Count <= Fanout)
        {
            var only = new PdfDictionary();
            only.SetEntry(leafKey, Pairs(entries, 0, entries.Count));
            return only;
        }

        // Leaves of up to 64 pairs, each with /Limits.
        var level = new List<(PdfObject First, PdfObject Last, PdfReference Ref)>();
        for (int i = 0; i < entries.Count; i += Fanout)
        {
            int n = Math.Min(Fanout, entries.Count - i);
            var leaf = new PdfDictionary();
            leaf.SetEntry("Limits", Limits(entries[i].Key, entries[i + n - 1].Key));
            leaf.SetEntry(leafKey, Pairs(entries, i, n));
            level.Add((entries[i].Key, entries[i + n - 1].Key, store(leaf)));
        }

        // Intermediate levels until one node can hold all that is left.
        while (level.Count > Fanout)
        {
            var next = new List<(PdfObject, PdfObject, PdfReference)>();
            for (int i = 0; i < level.Count; i += Fanout)
            {
                int n = Math.Min(Fanout, level.Count - i);
                var node = new PdfDictionary();
                node.SetEntry("Limits", Limits(level[i].First, level[i + n - 1].Last));
                node.SetEntry("Kids", Refs(level, i, n));
                next.Add((level[i].First, level[i + n - 1].Last, store(node)));
            }

            level = next;
        }

        var root = new PdfDictionary();
        root.SetEntry("Kids", Refs(level, 0, level.Count));
        return root;
    }

    private static PdfArray Pairs(IReadOnlyList<(PdfObject Key, PdfObject Value)> entries, int start, int count)
    {
        var a = new PdfArray();
        for (int i = start; i < start + count; i++)
        {
            a.Append(entries[i].Key);
            a.Append(entries[i].Value);
        }

        return a;
    }

    private static PdfArray Limits(PdfObject first, PdfObject last)
    {
        var a = new PdfArray();
        a.Append(first);
        a.Append(last);
        return a;
    }

    private static PdfArray Refs(List<(PdfObject First, PdfObject Last, PdfReference Ref)> level, int start, int count)
    {
        var a = new PdfArray();
        for (int i = start; i < start + count; i++)
        {
            a.Append(level[i].Ref);
        }

        return a;
    }
}
