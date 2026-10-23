namespace OnePdf;

/// <summary>A page on its way into a new page tree: its dictionary, the attributes it inherited, and its place in the page label scheme of its origin.</summary>
internal sealed class PageEntry(PdfDictionary dictionary, int origin)
{
    public PdfDictionary Dictionary { get; } = dictionary;

    /// <summary>The page's index in the document it came from (for labels of documents that define none).</summary>
    public int Origin { get; } = origin;

    public PdfObject? Resources { get; init; }

    public PdfObject? MediaBox { get; init; }

    public PdfObject? CropBox { get; init; }

    public PdfObject? Rotate { get; init; }

    public PageLabelDescription? Label { get; init; }
}

/// <summary>
/// Writes a page list as the document's page tree (ISO 32000-2 7.7.3): a balanced tree of at most <see cref="Fanout"/> kids per
/// node, every page carrying its inheritable attributes itself, plus the page labels for the new order.
/// </summary>
internal static class PageTreeBuilder
{
    // Wide enough to keep the tree shallow for thousands of pages, narrow enough that a node is read in one piece.
    private const int Fanout = 32;

    private static readonly string[] Inheritable = ["Resources", "MediaBox", "CropBox", "Rotate"];

    public static void Rebuild(PdfDocument doc, List<PageEntry> pages)
    {
        PdfDictionary catalog = doc.Catalog;
        PdfDictionary? root = catalog.GetDictionary("Pages");

        // A damaged file can have a page tree whose root has no /Kids and so reads as a page itself: it cannot also be the root.
        if (root?.Reference is null || pages.Exists(p => p.Dictionary.Reference?.Number == root.Reference.Number))
        {
            var fresh = new PdfDictionary();
            catalog.SetEntry("Pages", doc.AddObject(fresh));
            root = catalog.GetDictionary("Pages")!;
        }

        var promoted = new Dictionary<PdfObject, PdfReference>(ReferenceEqualityComparer.Instance);
        var level = new List<(PdfDictionary Dictionary, long Count)>(pages.Count);
        foreach (PageEntry e in pages)
        {
            PdfDictionary d = e.Dictionary;
            if (d.Reference is null)
            {
                doc.AddObject(d);
            }

            Materialize(doc, e, "Resources", e.Resources, promoted);
            Materialize(doc, e, "MediaBox", e.MediaBox, promoted);
            Materialize(doc, e, "CropBox", e.CropBox, promoted);
            Materialize(doc, e, "Rotate", e.Rotate, promoted);
            d.SetEntry("Type", new PdfName("Page"));
            level.Add((d, 1));
        }

        while (level.Count > Fanout)
        {
            var next = new List<(PdfDictionary, long)>((level.Count / Fanout) + 1);
            for (int start = 0; start < level.Count; start += Fanout)
            {
                int end = Math.Min(level.Count, start + Fanout);
                var node = new PdfDictionary();
                node.SetEntry("Type", new PdfName("Pages"));
                var kids = new PdfArray();
                long total = 0;
                for (int i = start; i < end; i++)
                {
                    kids.Append(level[i].Dictionary.Reference);
                    total += level[i].Count;
                }

                node.SetEntry("Kids", kids);
                node.SetEntry("Count", new PdfInteger(total));
                PdfReference nodeRef = doc.AddObject(node);
                for (int i = start; i < end; i++)
                {
                    level[i].Dictionary.SetEntry("Parent", nodeRef);
                }

                next.Add((node, total));
            }

            level = next;
        }

        var top = new PdfArray();
        foreach ((PdfDictionary d, _) in level)
        {
            top.Append(d.Reference);
            d.SetEntry("Parent", root.Reference);
        }

        root.SetEntry("Type", new PdfName("Pages"));
        root.SetEntry("Kids", top);
        root.SetEntry("Count", new PdfInteger(pages.Count));
        root.RemoveEntry("Parent");
        foreach (string key in Inheritable)
        {
            root.RemoveEntry(key);
        }

        WriteLabels(catalog, pages);
    }

    // 7.7.3.4: attributes that were inherited from a node that is no longer there are written into the page.
    private static void Materialize(PdfDocument doc, PageEntry e, string key, PdfObject? value, Dictionary<PdfObject, PdfReference> promoted)
    {
        if (value is null || e.Dictionary.ContainsKey(key))
        {
            return;
        }

        if (value.Reference is { } r)
        {
            e.Dictionary.SetEntry(key, r);
        }
        else if (value is PdfDictionary dict)
        {
            // Pages below one node share its direct dictionary: keep them sharing one object.
            if (!promoted.TryGetValue(dict, out PdfReference? p))
            {
                p = doc.AddObject(dict);
                promoted[dict] = p;
            }

            e.Dictionary.SetEntry(key, p);
        }
        else if (value is PdfArray arr)
        {
            var copy = new PdfArray();
            for (int i = 0; i < arr.Count; i++)
            {
                copy.Append(arr.GetRaw(i));
            }

            e.Dictionary.SetEntry(key, copy);
        }
        else
        {
            e.Dictionary.SetEntry(key, value);
        }
    }

    // 12.4.2: a range starts wherever the numbering does not simply continue from the page before.
    private static void WriteLabels(PdfDictionary catalog, List<PageEntry> pages)
    {
        if (pages.Count == 0 || !pages.Exists(static p => p.Label is not null))
        {
            catalog.RemoveEntry("PageLabels");
            return;
        }

        var nums = new PdfArray();
        PageLabelDescription? previous = null;
        for (int i = 0; i < pages.Count; i++)
        {
            PageLabelDescription d = pages[i].Label ?? new PageLabelDescription(PdfPageLabelStyle.Arabic, string.Empty, pages[i].Origin + 1L);
            bool continues = previous is { } p && p.Style == d.Style && p.Prefix == d.Prefix && (d.Style == PdfPageLabelStyle.None || d.Number == p.Number + 1);
            if (!continues)
            {
                nums.Append(new PdfInteger(i));
                var range = new PdfDictionary();
                string? style = d.Style switch
                {
                    PdfPageLabelStyle.Arabic => "D",
                    PdfPageLabelStyle.UpperRoman => "R",
                    PdfPageLabelStyle.LowerRoman => "r",
                    PdfPageLabelStyle.UpperLetters => "A",
                    PdfPageLabelStyle.LowerLetters => "a",
                    _ => null,
                };
                if (style is not null)
                {
                    range.SetEntry("S", new PdfName(style));
                }

                if (d.Prefix.Length > 0)
                {
                    range.SetEntry("P", PdfString.FromText(d.Prefix));
                }

                if (style is not null && d.Number != 1)
                {
                    range.SetEntry("St", new PdfInteger(Math.Clamp(d.Number, 1, int.MaxValue)));
                }

                nums.Append(range);
            }

            previous = d;
        }

        var labels = new PdfDictionary();
        labels.SetEntry("Nums", nums);
        catalog.SetEntry("PageLabels", labels);
    }
}
