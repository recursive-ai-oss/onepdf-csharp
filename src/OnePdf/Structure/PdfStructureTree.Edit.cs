using System.Linq;
using OnePdf.Structure;

namespace OnePdf;

public sealed partial class PdfStructureTree
{
    private PdfArray? _nums;
    private Dictionary<int, int>? _numsPos;

    private PdfDictionary DictOf(PdfStructureNode? node) => node?.Dictionary ?? RootDict;

    // ---- /K and /P ----

    private void WriteK(PdfStructureNode? owner)
    {
        PdfDictionary d = DictOf(owner);
        List<PdfStructureItem> kids = KidsOf(owner);
        if (kids.Count == 0)
        {
            d.RemoveEntry("K");
        }
        else if (kids.Count == 1)
        {
            d.SetEntry("K", kids[0].Raw);
        }
        else
        {
            var arr = new PdfArray();
            foreach (PdfStructureItem k in kids)
            {
                arr.Append(k.Raw);
            }

            d.SetEntry("K", arr);
        }
    }

    private void WriteParentLink(PdfStructureNode node, PdfStructureNode? parent)
    {
        PdfReference? r = DictOf(parent).Reference;
        if (r is not null)
        {
            node.Dictionary.SetEntry("P", r);
        }
    }

    // ---- the parent tree ----

    /// <summary>Flattens a number tree (or name tree) into its pairs; iterative, cycle-safe and bounded.</summary>
    private List<(PdfObject Key, PdfObject Value)> Flatten(PdfObject? root, string leafKey)
    {
        var pairs = new List<(PdfObject, PdfObject)>();
        var seen = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<PdfDictionary>();
        if (root is PdfDictionary r)
        {
            stack.Push(r);
        }

        int limit = (int)Math.Min(Document.Limits.MaxObjects, 1 << 22);
        while (stack.Count > 0 && pairs.Count < limit)
        {
            PdfDictionary d = stack.Pop();
            if (!seen.Add(d))
            {
                continue;
            }

            if (d.Get(leafKey) is PdfArray a)
            {
                for (int i = 0; i + 1 < a.Count && pairs.Count < limit; i += 2)
                {
                    if (a[i] is { } key)
                    {
                        pairs.Add((key, a.GetRaw(i + 1)));
                    }
                }
            }

            if (d.Get("Kids") is PdfArray kids)
            {
                for (int i = kids.Count - 1; i >= 0; i--)
                {
                    if (kids[i] is PdfDictionary kd)
                    {
                        stack.Push(kd);
                    }
                }
            }
        }

        return pairs;
    }

    private void EnsureParentTree()
    {
        if (_nums is not null)
        {
            return;
        }

        var entries = new SortedDictionary<long, PdfObject>();
        foreach ((PdfObject key, PdfObject value) in Flatten(RootDict.Get("ParentTree"), "Nums"))
        {
            if (key is PdfInteger i)
            {
                entries.TryAdd(i.Value, value);
            }
        }

        // The tree becomes one leaf, so a key can be found and changed in place; a large tree loses its balancing, not its meaning.
        var nums = new PdfArray();
        foreach ((long key, PdfObject value) in entries)
        {
            nums.Append(new PdfInteger(key));
            nums.Append(value);
        }

        var dict = new PdfDictionary();
        dict.SetEntry("Nums", nums);
        RootDict.SetEntry("ParentTree", Document.AddObject(dict));
        _nums = (PdfArray)dict.Get("Nums")!;
        IndexNums();
    }

    private void IndexNums()
    {
        _numsPos = [];
        for (int i = 0; i + 1 < _nums!.Count; i += 2)
        {
            if (_nums[i] is PdfInteger k && k.Value is >= 0 and <= int.MaxValue)
            {
                _numsPos[(int)k.Value] = i;
            }
        }
    }

    private void InsertPair(int key, PdfObject value)
    {
        int lo = 0, hi = _nums!.Count / 2;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_nums[2 * mid] is PdfInteger k && k.Value < key)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        _nums.Insert(2 * lo, new PdfInteger(key));
        _nums.Insert(2 * lo + 1, value);
        IndexNums();
    }

    private void RemovePair(int key)
    {
        if (_numsPos!.TryGetValue(key, out int p))
        {
            _nums!.RemoveAt(p + 1);
            _nums.RemoveAt(p);
            IndexNums();
        }
    }

    private int AllocateKey()
    {
        long max = RootDict.GetInt("ParentTreeNextKey", 0);
        foreach (int k in _numsPos!.Keys)
        {
            max = Math.Max(max, k + 1L);
        }

        RootDict.SetEntry("ParentTreeNextKey", new PdfInteger(max + 1));
        return (int)max;
    }

    /// <summary>The parent-tree key of the content a marked-content reference points into (page or form), allocating one when the content has none.</summary>
    private int KeyFor(PdfDictionary? holder, string entry, bool create)
    {
        if (holder is null)
        {
            return -1;
        }

        long key = holder.GetInt(entry, -1);
        if (key is >= 0 and <= int.MaxValue)
        {
            return (int)key;
        }

        if (!create)
        {
            return -1;
        }

        EnsureParentTree();
        int k = AllocateKey();
        holder.SetEntry(entry, new PdfInteger(k));
        return k;
    }

    private PdfDictionary? ContentHolder(PdfMarkedContentReference m) =>
        m.Stream?.Dictionary ?? (m.Page >= 0 && m.Page < Document.Pages.Count ? Document.Pages[m.Page].Dictionary : null);

    private const int MaxMcidIndex = 1 << 20;

    private void SetMc(PdfMarkedContentReference m, PdfStructureNode? owner)
    {
        int key = KeyFor(ContentHolder(m), "StructParents", create: owner is not null);
        if (key < 0 || m.Mcid > MaxMcidIndex)
        {
            return;
        }

        EnsureParentTree();
        PdfObject? existing = _numsPos!.TryGetValue(key, out int p) ? _nums![p + 1] : null;
        PdfArray arr;
        if (existing is PdfArray a)
        {
            arr = a;
        }
        else if (owner is null)
        {
            return;
        }
        else
        {
            arr = new PdfArray();
            if (_numsPos.ContainsKey(key))
            {
                // The key is there with a value that is not an array (a null, or a reference to an element): the entry is replaced.
                RemovePair(key);
            }

            InsertPair(key, arr);
            arr = (PdfArray)_nums![_numsPos[key] + 1]!;
        }

        while (arr.Count <= m.Mcid)
        {
            arr.Append(PdfNull.Instance);
        }

        arr.SetItem(m.Mcid, owner?.Dictionary.Reference is { } r ? r : PdfNull.Instance);
    }

    private void SetObj(PdfObjectReference o, PdfStructureNode? owner)
    {
        PdfDictionary? holder = o.Target is PdfDictionary d ? d : (o.Target as PdfStream)?.Dictionary;
        if (holder is null)
        {
            return;
        }

        int key = KeyFor(holder, "StructParent", create: owner is not null);
        if (key < 0)
        {
            return;
        }

        EnsureParentTree();
        if (owner?.Dictionary.Reference is not { } r)
        {
            RemovePair(key);
            return;
        }

        if (_numsPos!.TryGetValue(key, out int p))
        {
            _nums!.SetItem(p + 1, r);
        }
        else
        {
            InsertPair(key, r);
        }
    }

    // ---- moving content items between owners ----

    /// <summary>Gives a content item its new owner: the explicit form of the reference when the owner's /Pg no longer implies the page, and the parent tree entry.</summary>
    private void Reown(PdfStructureItem item, PdfStructureNode? owner)
    {
        switch (item)
        {
            case PdfMarkedContentReference m:
            {
                int ownerPage = owner is null ? -1 : PageOf(owner.Dictionary.GetRaw("Pg"));
                if (m.Page >= 0)
                {
                    PdfReference? pageRef = Document.Pages[m.Page].Dictionary.Reference;
                    if (m.Raw is PdfInteger && (m.Stream is not null || ownerPage != m.Page) && pageRef is not null)
                    {
                        var d = new PdfDictionary();
                        d.SetEntry("Type", new PdfName("MCR"));
                        d.SetEntry("Pg", pageRef);
                        d.SetEntry("MCID", new PdfInteger(m.Mcid));
                        m.Raw = d;
                    }
                    else if (m.Raw is not PdfInteger && ResolveRaw(m.Raw) is PdfDictionary md && pageRef is not null && PageOf(md.GetRaw("Pg")) != m.Page)
                    {
                        md.SetEntry("Pg", pageRef);
                    }
                }

                SetMc(m, owner);
                break;
            }

            case PdfObjectReference o:
                if (o.Page >= 0 && ResolveRaw(o.Raw) is PdfDictionary od && od.GetRaw("Pg") is null && Document.Pages[o.Page].Dictionary.Reference is { } pr)
                {
                    od.SetEntry("Pg", pr);
                }

                SetObj(o, owner);
                break;
        }
    }

    private PdfObject? ResolveRaw(PdfObject raw) => raw is PdfReference r ? Document.GetObject(r.Number) : raw;

    private void CheckMoveTarget(PdfStructureItem item, PdfStructureNode? parent)
    {
        EnsureEditable(item);
        if (parent is not null)
        {
            EnsureEditable(parent);
            if (!ReferenceEquals(parent.Tree, this))
            {
                throw new ArgumentException("The parent belongs to another structure tree.", nameof(parent));
            }
        }

        if (item is not PdfStructureNode && parent is null)
        {
            throw new ArgumentException("A marked-content or object reference must be held by an element.", nameof(parent));
        }

        int guard = 0;
        for (PdfStructureNode? p = parent; p is not null && guard++ < 1_000_000; p = p.Parent)
        {
            if (ReferenceEquals(p, item))
            {
                throw new ArgumentException("An element cannot be moved into itself or into one of its descendants.", nameof(parent));
            }
        }
    }

    internal void Move(PdfStructureItem item, PdfStructureNode? parent, int index)
    {
        CheckMoveTarget(item, parent);
        PdfStructureNode? old = item.Parent;
        List<PdfStructureItem> from = KidsOf(old);
        List<PdfStructureItem> to = KidsOf(parent);
        int at = from.IndexOf(item);
        if (at < 0)
        {
            throw new InvalidOperationException("The item is not in its parent's children.");
        }

        int newCount = ReferenceEquals(old, parent) ? from.Count - 1 : to.Count;
        int target = index < 0 ? newCount : index;
        if (target > newCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        from.RemoveAt(at);
        to.Insert(target, item);
        item.Parent = parent;
        if (!ReferenceEquals(old, parent))
        {
            if (item is PdfStructureNode node)
            {
                WriteParentLink(node, parent);
            }
            else
            {
                Reown(item, parent);
            }

            WriteK(old);
        }

        WriteK(parent);
    }

    internal PdfStructureNode Wrap(PdfStructureNode? parent, string type, int first, int count)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        Document.EnsureEditable(0);
        if (parent is not null)
        {
            EnsureEditable(parent);
        }

        List<PdfStructureItem> kids = KidsOf(parent);
        if (first < 0 || count < 1 || first > kids.Count - count)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "The range is not within the children.");
        }

        if (parent is null && kids.GetRange(first, count).Any(static k => k is not PdfStructureNode))
        {
            throw new ArgumentException("The root holds elements only.", nameof(type));
        }

        var d = new PdfDictionary();
        d.SetEntry("Type", new PdfName("StructElem"));
        d.SetEntry("S", new PdfName(type));
        if (parent?.Dictionary.GetRaw("Pg") is { } pg)
        {
            d.SetEntry("Pg", pg);
        }

        PdfReference reference = Document.AddObject(d);
        var node = new PdfStructureNode(this, reference, d) { Parent = parent };
        _elementCount++;
        List<PdfStructureItem> moved = kids.GetRange(first, count);
        kids.RemoveRange(first, count);
        kids.Insert(first, node);
        node.Kids.AddRange(moved);
        WriteParentLink(node, parent);
        foreach (PdfStructureItem m in moved)
        {
            m.Parent = node;
        }

        foreach (PdfStructureItem m in moved)
        {
            if (m is PdfStructureNode mn)
            {
                WriteParentLink(mn, node);
            }
            else
            {
                Reown(m, node);
            }
        }

        WriteK(node);
        WriteK(parent);
        return node;
    }

    internal PdfStructureNode InsertEmpty(PdfStructureNode parent, string type, int index)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        EnsureEditable(parent);
        List<PdfStructureItem> kids = parent.Kids;
        int at = index < 0 ? kids.Count : index;
        if (at > kids.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var d = new PdfDictionary();
        d.SetEntry("Type", new PdfName("StructElem"));
        d.SetEntry("S", new PdfName(type));
        if (parent.Dictionary.GetRaw("Pg") is { } pg)
        {
            d.SetEntry("Pg", pg);
        }

        PdfReference reference = Document.AddObject(d);
        var node = new PdfStructureNode(this, reference, d) { Parent = parent };
        _elementCount++;
        kids.Insert(at, node);
        WriteParentLink(node, parent);
        WriteK(parent);
        return node;
    }

    // ---- removing ----

    internal void RemoveItem(PdfStructureItem item, PdfStructureRemoval mode)
    {
        EnsureEditable(item);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (mode == PdfStructureRemoval.Unwrap && item is PdfStructureNode node)
        {
            Unwrap(node);
        }
        else
        {
            RemoveToArtifacts(item);
        }
    }

    private void Unwrap(PdfStructureNode node)
    {
        PdfStructureNode? parent = node.Parent;
        List<PdfStructureItem> kids = KidsOf(parent);
        int at = kids.IndexOf(node);
        if (parent is null && node.Kids.Any(static k => k is not PdfStructureNode))
        {
            throw new InvalidOperationException("The element holds content that cannot be held by the root; wrap or move the content first.");
        }

        List<PdfStructureItem> moved = [.. node.Kids];
        kids.RemoveAt(at);
        kids.InsertRange(at, moved);
        foreach (PdfStructureItem m in moved)
        {
            m.Parent = parent;
        }

        node.Kids.Clear();
        foreach (PdfStructureItem m in moved)
        {
            if (m is PdfStructureNode mn)
            {
                WriteParentLink(mn, parent);
            }
            else
            {
                Reown(m, parent);
            }
        }

        WriteK(parent);

        node.IsRemoved = true;
        _elementCount--;
        DropIds([node]);
    }

    private void RemoveToArtifacts(PdfStructureItem item)
    {
        var nodes = new List<PdfStructureNode>();
        var contents = new List<PdfMarkedContentReference>();
        var objects = new List<PdfObjectReference>();
        var all = new List<PdfStructureItem>();
        var stack = new Stack<PdfStructureItem>();
        stack.Push(item);
        while (stack.Count > 0)
        {
            PdfStructureItem i = stack.Pop();
            all.Add(i);
            switch (i)
            {
                case PdfStructureNode n:
                    nodes.Add(n);
                    foreach (PdfStructureItem k in n.Kids)
                    {
                        stack.Push(k);
                    }

                    break;
                case PdfMarkedContentReference m:
                    contents.Add(m);
                    break;
                case PdfObjectReference o:
                    objects.Add(o);
                    break;
            }
        }

        // Everything that can fail is done before anything is changed: the content streams are read and rewritten in memory.
        var scopes = new Dictionary<(int Page, PdfStream? Stm), HashSet<int>>();
        foreach (PdfMarkedContentReference m in contents)
        {
            if (m.Page < 0 && m.Stream is null)
            {
                continue;
            }

            var key = (m.Page, m.Stream);
            if (!scopes.TryGetValue(key, out HashSet<int>? set))
            {
                scopes[key] = set = [];
            }

            set.Add(m.Mcid);
        }

        var rewriter = new MarkedContentRewriter(Document.Limits);
        var edits = new List<Action>();
        foreach (((int page, PdfStream? stm), HashSet<int> ids) in scopes)
        {
            StructureContent.PrepareArtifacts(Document, rewriter, page, stm, ids, edits);
        }

        foreach (Action edit in edits)
        {
            edit();
        }

        _text.Clear();
        PdfStructureNode? parent = item.Parent;
        List<PdfStructureItem> kids = KidsOf(parent);
        kids.Remove(item);
        WriteK(parent);
        foreach (PdfMarkedContentReference m in contents)
        {
            SetMc(m, null);
        }

        foreach (PdfObjectReference o in objects)
        {
            SetObj(o, null);
            PdfDictionary? holder = o.Target is PdfDictionary d ? d : (o.Target as PdfStream)?.Dictionary;
            holder?.RemoveEntry("StructParent");
        }

        foreach (PdfStructureItem i in all)
        {
            i.IsRemoved = true;
        }

        _elementCount -= nodes.Count;
        DropIds(nodes);
    }

    /// <summary>Takes removed elements out of the id tree, which maps identifiers to elements.</summary>
    private void DropIds(List<PdfStructureNode> removed)
    {
        if (RootDict.Get("IDTree") is not PdfDictionary tree)
        {
            return;
        }

        var gone = new HashSet<int>();
        foreach (PdfStructureNode n in removed)
        {
            if (n.Dictionary.ContainsKey("ID") && n.Dictionary.Reference is { } r)
            {
                gone.Add(r.Number);
            }
        }

        if (gone.Count == 0)
        {
            return;
        }

        List<(PdfObject Key, PdfObject Value)> pairs = Flatten(tree, "Names");
        var kept = pairs.Where(p => !(p.Value is PdfReference r && gone.Contains(r.Number))).ToList();
        if (kept.Count == pairs.Count)
        {
            return;
        }

        var names = new PdfArray();
        foreach ((PdfObject key, PdfObject value) in kept.OrderBy(static p => p.Key is PdfString s ? Convert.ToHexString(s.Bytes.Span) : string.Empty, StringComparer.Ordinal))
        {
            names.Append(key);
            names.Append(value);
        }

        var dict = new PdfDictionary();
        dict.SetEntry("Names", names);
        RootDict.SetEntry("IDTree", Document.AddObject(dict));
    }
}
