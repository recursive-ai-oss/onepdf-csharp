namespace OnePdf;

// The logical structure (tagged PDF) of imported pages.
internal sealed partial class PageImporter
{
    private sealed class StructElement(PdfDictionary source, StructElement? parent, int page)
    {
        public PdfDictionary Source { get; } = source;

        public StructElement? Parent { get; } = parent;

        /// <summary>The object number of the source page the element's marked content is on (its own <c>/Pg</c>, else its parent's), or -1.</summary>
        public int Page { get; } = page;

        /// <summary>Content kept (marked-content ids, MCR and OBJR dictionaries of the target) and child elements, in the source's order.</summary>
        public List<(PdfObject? Content, StructElement? Element)> Kids { get; } = [];

        public bool Keep { get; set; }

        public PdfDictionary? Target { get; set; }

        public PdfReference? Reference { get; set; }
    }

    /// <summary>
    /// Copies the part of the source's structure tree that describes the imported pages (ISO 32000-2 14.7): an element is kept when
    /// it, or an element below it, holds marked content of an imported page or an annotation that came along. The kept elements
    /// become new top-level children of the target's tree, <c>/Pg</c> and <c>/Obj</c> are mapped, and <c>/StructParents</c> and
    /// <c>/StructParent</c> get fresh keys in the target's parent tree. Role and class maps are merged (the target's entry wins
    /// on a clash). The tree is walked with an explicit stack and bounded by the object limit.
    /// </summary>
    private void ImportStructure(List<(PdfDictionary Source, PdfDictionary Target)> pages)
    {
        if (_same || _src.StructureTreeRoot is not { } sourceRoot)
        {
            return;
        }

        var order = new List<StructElement>();
        var tops = new List<StructElement>();
        var seen = new HashSet<int>();
        var stack = new Stack<(PdfObject? Node, StructElement? Parent, int Page)>();
        stack.Push((sourceRoot.GetRaw("K"), null, -1));
        long visited = 0;
        while (stack.Count > 0)
        {
            (PdfObject? raw, StructElement? parent, int page) = stack.Pop();
            if (++visited > _limits.MaxObjects)
            {
                throw new PdfLimitException("The structure tree has more nodes than the object limit allows.");
            }

            if ((visited & 1023) == 0)
            {
                _ct.ThrowIfCancellationRequested();
            }

            switch (_src.Resolve(raw))
            {
                case PdfArray a:
                    for (int i = Math.Min(a.Count, _limits.MaxObjects) - 1; i >= 0; i--)
                    {
                        stack.Push((a.GetRaw(i), parent, page));
                    }

                    break;
                case PdfInteger mcid:
                    if (parent is not null && _pageMap.ContainsKey(page))
                    {
                        parent.Kids.Add((new PdfInteger(mcid.Value), null));
                    }

                    break;
                case PdfDictionary d:
                    AddStructureNode(d, parent, page, order, tops, seen, stack);
                    break;
            }
        }

        // An element stays when anything below it does; children come after their parent in the list, so walking back sees them first.
        for (int i = order.Count - 1; i >= 0; i--)
        {
            StructElement e = order[i];
            foreach ((PdfObject? content, StructElement? child) in e.Kids)
            {
                if (content is not null || child!.Keep)
                {
                    e.Keep = true;
                    break;
                }
            }
        }

        var kept = new List<StructElement>();
        var elements = new Dictionary<int, PdfReference>();
        foreach (StructElement e in order)
        {
            if (!e.Keep)
            {
                continue;
            }

            var dict = new PdfDictionary(_root);
            for (int i = 0; i < e.Source.RawCount; i++)
            {
                string key = e.Source.KeyAt(i);
                if (key is "K" or "P" or "Pg" or "ID" or "Type" or "Parent")
                {
                    continue;
                }

                if (Convert(e.Source.ValueAt(i), false, 1) is { } v)
                {
                    dict.Set(key, v);
                }
            }

            dict.Set("Type", new PdfName("StructElem"));
            if (e.Source.GetRaw("Pg") is PdfReference pg && _pageMap.TryGetValue(pg.Number, out PdfReference? np))
            {
                dict.Set("Pg", new PdfReference(np.Number, 0));
            }

            e.Target = dict;
            e.Reference = _dst.AddObject(dict);
            if (e.Source.Reference is { } sr)
            {
                elements[sr.Number] = e.Reference;
            }

            kept.Add(e);
        }

        if (kept.Count == 0)
        {
            return;
        }

        foreach (StructElement e in kept)
        {
            var kids = new PdfArray(_root);
            foreach ((PdfObject? content, StructElement? child) in e.Kids)
            {
                if (content is not null)
                {
                    kids.Add(content);
                }
                else if (child!.Keep)
                {
                    kids.Add(child.Reference!);
                }
            }

            e.Target!.SetEntry("K", kids);
            if (e.Parent is not null)
            {
                e.Target.SetEntry("P", e.Parent.Reference);
            }
        }

        PdfDictionary root = EnsureStructureRoot();
        long next = NextParentKey(root);
        var entries = new List<(long Key, PdfObject Value)>();
        Dictionary<long, PdfObject> parentTree = [];
        foreach (KeyValuePair<PdfObject, PdfObject> kv in _src.ReadTree(sourceRoot.GetDictionary("ParentTree"), true, "parent tree"))
        {
            parentTree.TryAdd(((PdfInteger)kv.Key).Value, kv.Value);
        }

        // A page's marked content maps back to elements by its /StructParents key (14.7.4.4).
        foreach ((PdfDictionary sourcePage, PdfDictionary targetPage) in pages)
        {
            long k = sourcePage.GetInt("StructParents", -1);
            if (k < 0 || !parentTree.TryGetValue(k, out PdfObject? value) || _src.Resolve(value) is not PdfArray byMcid)
            {
                continue;
            }

            var mapped = new PdfArray(_root);
            for (int i = 0; i < Math.Min(byMcid.Count, _limits.MaxObjects); i++)
            {
                mapped.Add(byMcid.GetRaw(i) is PdfReference r && elements.TryGetValue(r.Number, out PdfReference? er) ? new PdfReference(er.Number, 0) : PdfNull.Instance);
            }

            long key = next++;
            targetPage.SetEntry("StructParents", new PdfInteger(key));
            entries.Add((key, mapped));
        }

        // An annotation or form XObject maps to its element directly through /StructParent.
        foreach (KeyValuePair<int, int> pair in _local)
        {
            if (_src.GetObject(pair.Key) is not PdfDictionary sa || sa.GetInt("StructParent", -1) < 0
                || !parentTree.TryGetValue(sa.GetInt("StructParent", -1), out PdfObject? value) || value is not PdfReference vr || !elements.TryGetValue(vr.Number, out PdfReference? element)
                || _dst.GetObject(pair.Value) is not PdfDictionary ta)
            {
                continue;
            }

            long key = next++;
            ta.SetEntry("StructParent", new PdfInteger(key));
            entries.Add((key, new PdfReference(element.Number, 0)));
        }

        var all = new List<(long Key, PdfObject Value)>();
        foreach (KeyValuePair<PdfObject, PdfObject> kv in _dst.ReadTree(root.GetDictionary("ParentTree"), true, "parent tree"))
        {
            all.Add((((PdfInteger)kv.Key).Value, kv.Value));
        }

        all.AddRange(entries);
        all.Sort(static (a, b) => a.Key.CompareTo(b.Key));
        var nums = new PdfArray();
        foreach ((long key, PdfObject value) in all)
        {
            nums.Append(new PdfInteger(key));
            nums.Append(value);
        }

        var leaf = new PdfDictionary();
        leaf.SetEntry("Nums", nums);
        root.SetEntry("ParentTree", leaf);
        root.SetEntry("ParentTreeNextKey", new PdfInteger(next));

        // The kept top elements join whatever the target's tree already holds.
        var rootKids = new PdfArray();
        switch (root.Get("K"))
        {
            case PdfArray existing:
                for (int i = 0; i < existing.Count; i++)
                {
                    rootKids.Append(existing.GetRaw(i));
                }

                break;
            case PdfDictionary single when single.Reference is { } sref:
                rootKids.Append(sref);
                break;
        }

        foreach (StructElement t in tops)
        {
            if (t.Keep)
            {
                t.Target!.SetEntry("P", root.Reference);
                rootKids.Append(t.Reference);
            }
        }

        root.SetEntry("K", rootKids);
        MergeNameMap(root, sourceRoot, "RoleMap");
        MergeNameMap(root, sourceRoot, "ClassMap");

        PdfDictionary catalog = _dst.Catalog;
        if (catalog.GetDictionary("MarkInfo") is null)
        {
            catalog.SetEntry("MarkInfo", new PdfDictionary());
        }

        catalog.GetDictionary("MarkInfo")!.SetEntry("Marked", new PdfBoolean(true));
        Drain();
    }

    private void AddStructureNode(PdfDictionary d, StructElement? parent, int page, List<StructElement> order, List<StructElement> tops, HashSet<int> seen, Stack<(PdfObject? Node, StructElement? Parent, int Page)> stack)
    {
        string? type = d.GetName("Type");
        if (type == "MCR")
        {
            int pg = PageNumber(d) ?? page;
            if (parent is not null && _pageMap.TryGetValue(pg, out PdfReference? target) && d.GetInt("MCID", -1) is { } mcid && mcid >= 0)
            {
                var mcr = new PdfDictionary(_root);
                mcr.Set("Type", new PdfName("MCR"));
                mcr.Set("Pg", new PdfReference(target.Number, 0));
                mcr.Set("MCID", new PdfInteger(mcid));
                parent.Kids.Add((mcr, null));
            }

            return;
        }

        if (type == "OBJR")
        {
            // Only an annotation or XObject that was copied with the pages can be referred to.
            if (parent is not null && d.GetRaw("Obj") is PdfReference o && (_local.TryGetValue(o.Number, out int t) || _map.TryGetValue(o.Number, out t)))
            {
                var objr = new PdfDictionary(_root);
                objr.Set("Type", new PdfName("OBJR"));
                objr.Set("Obj", new PdfReference(t, 0));
                if ((PageNumber(d) ?? page) is var pn && _pageMap.TryGetValue(pn, out PdfReference? pt))
                {
                    objr.Set("Pg", new PdfReference(pt.Number, 0));
                }

                parent.Kids.Add((objr, null));
            }

            return;
        }

        if (d.Reference is { } r && !seen.Add(r.Number))
        {
            return;
        }

        var element = new StructElement(d, parent, PageNumber(d) ?? page);
        order.Add(element);
        if (parent is null)
        {
            tops.Add(element);
        }
        else
        {
            parent.Kids.Add((null, element));
        }

        stack.Push((d.GetRaw("K"), element, element.Page));
    }

    private static int? PageNumber(PdfDictionary d) => d.GetRaw("Pg") is PdfReference r ? r.Number : null;

    private PdfDictionary EnsureStructureRoot()
    {
        PdfDictionary catalog = _dst.Catalog;
        if (catalog.GetDictionary("StructTreeRoot") is null)
        {
            var fresh = new PdfDictionary();
            fresh.SetEntry("Type", new PdfName("StructTreeRoot"));
            catalog.SetEntry("StructTreeRoot", _dst.AddObject(fresh));
        }

        return catalog.GetDictionary("StructTreeRoot")!;
    }

    // The first parent tree key that no page or annotation of the target uses (14.7.4.4).
    private long NextParentKey(PdfDictionary root)
    {
        long next = Math.Max(0, root.GetInt("ParentTreeNextKey"));
        foreach (KeyValuePair<PdfObject, PdfObject> kv in _dst.ReadTree(root.GetDictionary("ParentTree"), true, "parent tree"))
        {
            next = Math.Max(next, ((PdfInteger)kv.Key).Value + 1);
        }

        return next;
    }

    private void MergeNameMap(PdfDictionary root, PdfDictionary sourceRoot, string key)
    {
        if (sourceRoot.Get(key) is not PdfDictionary from)
        {
            return;
        }

        if (root.GetDictionary(key) is null)
        {
            root.SetEntry(key, new PdfDictionary());
        }

        PdfDictionary to = root.GetDictionary(key)!;
        for (int i = 0; i < from.RawCount; i++)
        {
            string name = from.KeyAt(i);
            if (!to.ContainsKey(name) && Convert(from.ValueAt(i), false, 1) is { } v)
            {
                to.SetEntry(name, v);
            }
        }
    }
}
