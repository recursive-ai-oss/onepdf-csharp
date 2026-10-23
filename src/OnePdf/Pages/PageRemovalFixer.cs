namespace OnePdf;

/// <summary>
/// Repairs what pointed at pages that were deleted (ISO 32000-2 12.3.2 destinations, 12.3.3 outlines, 12.5.6.5 links,
/// 12.7.3 fields): destinations to them are dropped, bookmarks that led only there are removed (a heading with other
/// bookmarks below it stays, without a target), links become inert, form fields whose widgets were all on them are removed,
/// and the structures that cannot be repaired cheaply and would keep the deleted content in the file are dropped.
/// </summary>
internal static class PageRemovalFixer
{
    public static void Apply(PdfDocument doc, HashSet<int> removed, List<PdfDictionary> removedPages, List<PdfDictionary> remainingPages)
    {
        IReadOnlyDictionary<string, PdfObject> names = doc.NamedDestinationMap;
        PdfDictionary catalog = doc.Catalog;

        // Links on the pages that stay.
        foreach (PdfDictionary page in remainingPages)
        {
            if (doc.Resolve(page.GetRaw("Annots")) is not PdfArray annots)
            {
                continue;
            }

            for (int i = 0; i < annots.Count && i < doc.Limits.MaxObjects; i++)
            {
                if (annots[i] is PdfDictionary a && a.GetName("Subtype") == "Link")
                {
                    ClearDeadTargets(doc, names, removed, a);
                }
            }
        }

        // The document's open action.
        if (catalog.GetRaw("OpenAction") is { } open)
        {
            bool dead = doc.Resolve(open) switch
            {
                PdfArray => removed.Contains(PageOf(doc, names, open)),
                PdfDictionary d when d.GetName("S") == "GoTo" => removed.Contains(PageOf(doc, names, d.GetRaw("D"))),
                _ => false,
            };
            if (dead)
            {
                catalog.RemoveEntry("OpenAction");
            }
        }

        PruneOutline(doc, names, removed);
        PruneForm(doc, removedPages);
        PruneNames(doc, names, removed, catalog);

        // These would keep the deleted pages (and everything on them) reachable, and cannot be pruned in place.
        if (catalog.RemoveEntry("StructTreeRoot"))
        {
            catalog.RemoveEntry("MarkInfo");
            doc.ReportInfo("The structure tree was dropped because pages were deleted: its elements refer to them.");
        }

        if (catalog.RemoveEntry("Threads"))
        {
            doc.ReportInfo("The article threads were dropped because pages were deleted: their beads refer to them.");
        }
    }

    // The page object number a destination (explicit, dictionary with /D, or a name to look up) points at, or -1.
    internal static int PageOf(PdfDocument doc, IReadOnlyDictionary<string, PdfObject> names, PdfObject? value)
    {
        for (int hops = 0; hops < 8; hops++)
        {
            switch (doc.Resolve(value))
            {
                case PdfString s:
                    value = names.GetValueOrDefault(s.GetText());
                    break;
                case PdfName n:
                    value = names.GetValueOrDefault(n.Value);
                    break;
                case PdfDictionary d:
                    value = d.GetRaw("D");
                    break;
                case PdfArray a:
                    return a.Count > 0 && a.GetRaw(0) is PdfReference r ? r.Number : -1;
                default:
                    return -1;
            }
        }

        return -1;
    }

    private static bool IsDead(PdfDocument doc, IReadOnlyDictionary<string, PdfObject> names, HashSet<int> removed, PdfDictionary holder, out bool destination, out bool action)
    {
        destination = holder.GetRaw("Dest") is { } d && removed.Contains(PageOf(doc, names, d));
        action = holder.Get("A") is PdfDictionary a && a.GetName("S") == "GoTo" && removed.Contains(PageOf(doc, names, a.GetRaw("D")));
        return destination || action;
    }

    private static void ClearDeadTargets(PdfDocument doc, IReadOnlyDictionary<string, PdfObject> names, HashSet<int> removed, PdfDictionary holder)
    {
        if (IsDead(doc, names, removed, holder, out bool destination, out bool action))
        {
            if (destination)
            {
                holder.RemoveEntry("Dest");
            }

            if (action)
            {
                holder.RemoveEntry("A");
            }
        }
    }

    private static void PruneOutline(PdfDocument doc, IReadOnlyDictionary<string, PdfObject> names, HashSet<int> removed)
    {
        List<OutlineNode> top = OutlineEditor.Read(doc);
        if (top.Count == 0)
        {
            return;
        }

        bool changed = false;
        List<OutlineNode> order = OutlineEditor.PreOrder(top);
        for (int i = order.Count - 1; i >= 0; i--)
        {
            OutlineNode n = order[i];
            int before = n.Kids.Count;
            n.Kids = n.Kids.FindAll(static k => k.Alive);
            bool dead = IsDead(doc, names, removed, n.Dictionary, out _, out _);
            n.Alive = !dead || n.Kids.Count > 0;
            if (dead && n.Alive)
            {
                ClearDeadTargets(doc, names, removed, n.Dictionary);
                changed = true;
            }

            changed |= !n.Alive || n.Kids.Count != before;
        }

        top = top.FindAll(static k => k.Alive);
        if (changed)
        {
            OutlineEditor.Write(doc, top);
            doc.ResetOutline();
        }
    }

    // A widget that sat on a deleted page goes with its page; a field with no widget left goes with it.
    private static void PruneForm(PdfDocument doc, List<PdfDictionary> removedPages)
    {
        if (doc.Catalog.GetDictionary("AcroForm") is not { } form || form.GetArray("Fields") is not { } fields)
        {
            return;
        }

        var gone = new HashSet<int>();
        foreach (PdfDictionary page in removedPages)
        {
            if (doc.Resolve(page.GetRaw("Annots")) is PdfArray annots)
            {
                for (int i = 0; i < annots.Count && i < doc.Limits.MaxObjects; i++)
                {
                    if (annots.GetRaw(i) is PdfReference r)
                    {
                        gone.Add(r.Number);
                    }
                }
            }
        }

        if (gone.Count == 0)
        {
            return;
        }

        // Pre-order over the field tree; walking it backwards visits kids before their parents.
        var order = new List<PdfDictionary>();
        var seen = new HashSet<int>();
        var stack = new Stack<PdfDictionary>();
        for (int i = fields.Count - 1; i >= 0; i--)
        {
            if (fields[i] is PdfDictionary f)
            {
                stack.Push(f);
            }
        }

        while (stack.Count > 0 && order.Count < doc.Limits.MaxObjects)
        {
            PdfDictionary f = stack.Pop();
            if (f.Reference is not { } r || !seen.Add(r.Number))
            {
                continue;
            }

            order.Add(f);
            if (f.GetArray("Kids") is { } kids)
            {
                for (int i = kids.Count - 1; i >= 0; i--)
                {
                    if (kids[i] is PdfDictionary k)
                    {
                        stack.Push(k);
                    }
                }
            }
        }

        var dead = new HashSet<int>(gone);
        for (int i = order.Count - 1; i >= 0; i--)
        {
            PdfDictionary f = order[i];
            if (f.GetArray("Kids") is { } kids)
            {
                for (int k = kids.Count - 1; k >= 0; k--)
                {
                    if (kids.GetRaw(k) is PdfReference kr && dead.Contains(kr.Number))
                    {
                        kids.RemoveAt(k);
                    }
                }

                if (kids.Count == 0)
                {
                    dead.Add(f.Reference!.Number);
                }
            }
        }

        for (int i = fields.Count - 1; i >= 0; i--)
        {
            if (fields.GetRaw(i) is PdfReference fr && dead.Contains(fr.Number))
            {
                fields.RemoveAt(i);
            }
        }

        doc.ResetForm();
    }

    private static void PruneNames(PdfDocument doc, IReadOnlyDictionary<string, PdfObject> names, HashSet<int> removed, PdfDictionary catalog)
    {
        // The old-style /Dests dictionary of the catalog.
        if (catalog.GetDictionary("Dests") is { } old)
        {
            foreach (string key in new List<string>(old.Keys))
            {
                if (removed.Contains(PageOf(doc, names, old.GetRaw(key))))
                {
                    old.RemoveEntry(key);
                }
            }
        }

        // The name tree is rewritten as one sorted node when any entry goes (7.9.6).
        PdfDictionary? nameDict = catalog.GetDictionary("Names");
        PdfDictionary? tree = nameDict?.GetDictionary("Dests");
        if (tree is null)
        {
            doc.ResetNamedDestinations();
            return;
        }

        var keep = new List<(byte[] Key, PdfObject Value)>();
        bool dropped = false;
        foreach (KeyValuePair<PdfObject, PdfObject> kv in doc.ReadTree(tree, false, "named destinations"))
        {
            if (removed.Contains(PageOf(doc, names, kv.Value)))
            {
                dropped = true;
            }
            else
            {
                keep.Add((((PdfString)kv.Key).Bytes.ToArray(), kv.Value));
            }
        }

        if (dropped)
        {
            keep.Sort(static (a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));
            var list = new PdfArray();
            foreach ((byte[] key, PdfObject value) in keep)
            {
                list.Append(new PdfString(key));
                list.Append(value);
            }

            var leaf = new PdfDictionary();
            leaf.SetEntry("Names", list);
            nameDict!.SetEntry("Dests", leaf);
        }

        doc.ResetNamedDestinations();
    }
}
