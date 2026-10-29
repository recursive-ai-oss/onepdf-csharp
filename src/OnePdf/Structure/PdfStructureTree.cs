using System.Linq;
using System.Text;
using OnePdf.Text;

namespace OnePdf;

/// <summary>
/// The logical structure tree of a document (<c>/StructTreeRoot</c>, ISO 32000-2 14.7) read into typed objects, with edits that keep
/// <c>/K</c>, <c>/P</c>, the parent tree, <c>/StructParents</c> and the id tree consistent. Get it from <see cref="PdfDocument.StructureTree"/>.
/// </summary>
/// <remarks>
/// Reading is lenient and bounded: the walk is iterative, stops at <see cref="PdfLimits.MaxNestingDepth"/> levels and at the object limit,
/// reads each element once, and records what is wrong (cycles, shared elements, a <c>/P</c> that does not match, duplicate or missing
/// marked content) in <see cref="Diagnostics"/> instead of throwing. Edits need <see cref="PdfOpenOptions.AllowEditing"/>. The tree is a snapshot:
/// change the structure only through it, or get a new one after <see cref="PdfDocument.RemoveTags"/> or <see cref="PdfDocument.ApplyTagging"/>.
/// Instances are not thread-safe.
/// </remarks>
public sealed partial class PdfStructureTree
{
    private static readonly HashSet<string> StandardTypes = new(StringComparer.Ordinal)
    {
        "Document", "Part", "Art", "Sect", "Div", "BlockQuote", "Caption", "TOC", "TOCI", "Index", "NonStruct", "Private",
        "P", "H", "H1", "H2", "H3", "H4", "H5", "H6", "L", "LI", "Lbl", "LBody", "Table", "TR", "TH", "TD", "THead", "TBody", "TFoot",
        "Span", "Quote", "Note", "Reference", "BibEntry", "Code", "Link", "Annot", "Ruby", "RB", "RT", "RP", "Warichu", "WT", "WP",
        "Figure", "Formula", "Form",
    };

    private readonly List<PdfDiagnostic> _diagnostics = [];
    private readonly Dictionary<int, Dictionary<int, string>> _text = [];
    private int _elementCount;

    internal PdfStructureTree(PdfDocument doc, PdfDictionary root)
    {
        Document = doc;
        RootDict = root;
    }

    internal PdfDictionary RootDict { get; }

    internal List<PdfStructureItem> RootKids { get; } = [];

    /// <summary>The document the tree belongs to.</summary>
    public PdfDocument Document { get; }

    /// <summary>The children of the root, in order (normally one <c>Document</c> element).</summary>
    /// <exception cref="InvalidOperationException">The tree is stale (<see cref="IsStale"/>).</exception>
    public IReadOnlyList<PdfStructureItem> Children
    {
        get
        {
            ThrowIfStale();
            return RootKids;
        }
    }

    /// <summary>What was wrong in the tree when it was read, for example cycles, a <c>/P</c> that does not match, or duplicate marked content. Empty for a sound tree.</summary>
    public IReadOnlyList<PdfDiagnostic> Diagnostics => _diagnostics;

    /// <summary>The number of structure elements read.</summary>
    public int ElementCount => _elementCount;

    /// <summary>All structure elements, depth first in document order.</summary>
    /// <exception cref="InvalidOperationException">The tree is stale (<see cref="IsStale"/>).</exception>
    public IEnumerable<PdfStructureNode> Elements()
    {
        ThrowIfStale();
        return ElementsCore();
    }

    private IEnumerable<PdfStructureNode> ElementsCore()
    {
        var stack = new Stack<PdfStructureItem>();
        for (int i = RootKids.Count - 1; i >= 0; i--)
        {
            stack.Push(RootKids[i]);
        }

        while (stack.Count > 0)
        {
            PdfStructureItem item = stack.Pop();
            if (item is PdfStructureNode n)
            {
                yield return n;
                for (int i = n.Kids.Count - 1; i >= 0; i--)
                {
                    stack.Push(n.Kids[i]);
                }
            }
        }
    }

    /// <summary>The role map (<c>/RoleMap</c>): custom structure type to the type it stands for, as stored.</summary>
    public IReadOnlyDictionary<string, string> RoleMap
    {
        get
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (RootDict.Get("RoleMap") is PdfDictionary rm)
            {
                foreach (string k in rm.Keys)
                {
                    if (rm.GetName(k) is { } v)
                    {
                        map[k] = v;
                    }
                }
            }

            return map;
        }
    }

    /// <summary>
    /// Maps a custom structure type to the type it stands for (<c>/RoleMap</c>), for example <c>Strong</c> to <c>Span</c>. An existing mapping is replaced.
    /// </summary>
    /// <exception cref="ArgumentException">A name is empty, or both are the same.</exception>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    public void MapRole(string customType, string standardType)
    {
        ArgumentException.ThrowIfNullOrEmpty(customType);
        ArgumentException.ThrowIfNullOrEmpty(standardType);
        if (customType == standardType)
        {
            throw new ArgumentException("A type cannot be mapped to itself.", nameof(standardType));
        }

        Document.EnsureEditable(0);
        PdfDictionary rm = RootDict.Get("RoleMap") as PdfDictionary ?? new PdfDictionary();
        if (!ReferenceEquals(RootDict.Get("RoleMap"), rm))
        {
            RootDict.SetEntry("RoleMap", rm);
        }

        rm.SetEntry(customType, new PdfName(standardType));
    }

    /// <summary>
    /// Wraps <paramref name="count"/> children of the root from <paramref name="first"/> in a new element, as
    /// <see cref="PdfStructureNode.WrapChildren"/> does for an element.
    /// </summary>
    public PdfStructureNode WrapChildren(string type, int first, int count) => Wrap(null, type, first, count);

    /// <summary>
    /// Finds the marked-content references whose identifier no sequence on the page (or in the form) carries, by reading the content
    /// streams. A sound tree has none. Streams that cannot be read are skipped.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    public IReadOnlyList<PdfMarkedContentReference> FindMissingContent(CancellationToken cancellationToken = default)
    {
        var scopes = new Dictionary<(int, int), HashSet<int>?>();
        var refs = new List<PdfMarkedContentReference>();
        foreach (PdfStructureNode n in Elements())
        {
            foreach (PdfStructureItem k in n.Kids)
            {
                if (k is PdfMarkedContentReference m)
                {
                    refs.Add(m);
                }
            }
        }

        foreach (PdfStructureItem k in RootKids)
        {
            if (k is PdfMarkedContentReference m)
            {
                refs.Add(m);
            }
        }

        var missing = new List<PdfMarkedContentReference>();
        var rewriter = new Structure.MarkedContentRewriter(Document.Limits);
        foreach (PdfMarkedContentReference m in refs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (m.Page, m.Stream?.Reference?.Number ?? 0);
            if (!scopes.TryGetValue(key, out HashSet<int>? ids))
            {
                ids = ReadMcids(m, rewriter, cancellationToken);
                scopes[key] = ids;
            }

            if (ids is not null && !ids.Contains(m.Mcid))
            {
                missing.Add(m);
            }
        }

        return missing;
    }

    private HashSet<int>? ReadMcids(PdfMarkedContentReference m, Structure.MarkedContentRewriter rewriter, CancellationToken ct)
    {
        try
        {
            var ids = new HashSet<int>();
            ReadOnlyMemory<byte> data;
            PdfDictionary? resources;
            if (m.Stream is { } s)
            {
                data = s.GetDecodedBytes(ct);
                resources = s.Dictionary.Get("Resources") as PdfDictionary;
            }
            else if (m.Page >= 0 && m.Page < Document.Pages.Count)
            {
                data = Structure.StructureContent.PageData(Document, Document.Pages[m.Page], ct);
                resources = Document.Pages[m.Page].Resources;
            }
            else
            {
                return null;
            }

            rewriter.Rewrite(data, resources, (_, _, mcid) =>
            {
                if (mcid >= 0)
                {
                    ids.Add(mcid);
                }

                return Structure.McAction.Keep;
            }, out bool complete);
            return complete ? ids : null;
        }
        catch (PdfFormatException)
        {
            return null;
        }
    }

    // ---- reading ----

    internal static PdfStructureTree? Read(PdfDocument doc)
    {
        if (doc.Catalog.Get("StructTreeRoot") is not PdfDictionary root)
        {
            return null;
        }

        var tree = new PdfStructureTree(doc, root);
        tree.ReadAll();
        return tree;
    }

    internal void Diagnose(string message, PdfDictionary? at = null) =>
        _diagnostics.Add(new PdfDiagnostic(PdfSeverity.Warning, at?.Reference?.Number ?? -1, -1, message));

    private void ReadAll()
    {
        PdfLimits limits = Document.Limits;
        int maxNodes = (int)Math.Min(limits.MaxObjects, 1 << 22);
        var seen = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance) { RootDict };
        var contentSeen = new HashSet<(int, int, int)>();
        var work = new Stack<(PdfDictionary Owner, PdfStructureNode? Node, int Depth)>();
        work.Push((RootDict, null, 0));
        while (work.Count > 0)
        {
            (PdfDictionary owner, PdfStructureNode? node, int depth) = work.Pop();
            List<PdfStructureItem> kids = KidsOf(node);
            PdfObject? k = owner.Get("K");
            PdfObject? rawK = owner.GetRaw("K");
            int ownerPage = PageOf(owner.GetRaw("Pg"));
            if (k is PdfArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    AddKid(arr.GetRaw(i), arr[i]);
                }
            }
            else if (k is not null)
            {
                AddKid(rawK!, k);
            }

            void AddKid(PdfObject raw, PdfObject? value)
            {
                switch (value)
                {
                    case PdfInteger { Value: >= 0 and <= int.MaxValue } mc:
                        if (ownerPage < 0)
                        {
                            Diagnose("Marked content in the structure tree names no page: its element has no /Pg.", owner);
                        }

                        AddContent(new PdfMarkedContentReference(this, raw, ownerPage, null, (int)mc.Value));
                        break;
                    case PdfDictionary d when d.GetName("Type") == "MCR":
                    {
                        long id = d.GetInt("MCID", -1);
                        if (id is < 0 or > int.MaxValue)
                        {
                            Diagnose("A marked-content reference has no valid /MCID.", owner);
                            break;
                        }

                        int page = PageOf(d.GetRaw("Pg"));
                        if (page < 0)
                        {
                            page = ownerPage;
                        }

                        AddContent(new PdfMarkedContentReference(this, raw, page, d.Get("Stm") as PdfStream, (int)id));
                        break;
                    }

                    case PdfDictionary d when d.GetName("Type") == "OBJR":
                    {
                        int page = PageOf(d.GetRaw("Pg"));
                        var o = new PdfObjectReference(this, raw, page < 0 ? ownerPage : page, d.Get("Obj"));
                        o.Parent = node;
                        kids.Add(o);
                        break;
                    }

                    case PdfDictionary d when d.ContainsKey("S") || d.GetName("Type") == "StructElem":
                    {
                        if (!seen.Add(d))
                        {
                            Diagnose("A structure element is reachable more than once (a cycle or a shared element); the repeat is ignored.", d);
                            break;
                        }

                        if (_elementCount >= maxNodes)
                        {
                            Diagnose("The structure tree has more elements than the object limit allows; the rest is not read.", d);
                            break;
                        }

                        if (depth + 1 > limits.MaxNestingDepth)
                        {
                            Diagnose("The structure tree is deeper than the nesting limit; the deeper part is not read.", d);
                            break;
                        }

                        _elementCount++;
                        var child = new PdfStructureNode(this, raw, d) { Parent = node };
                        kids.Add(child);
                        PdfObject? p = d.GetRaw("P");
                        PdfReference? expected = owner.Reference;
                        if (p is PdfReference pr && expected is not null && pr.Number != expected.Number || p is null)
                        {
                            Diagnose(p is null ? "A structure element has no /P." : "The /P of a structure element is not the element that lists it in /K.", d);
                        }

                        work.Push((d, child, depth + 1));
                        break;
                    }

                    default:
                        Diagnose("An entry of /K is neither a structure element, marked-content reference nor object reference; it is ignored.", owner);
                        break;
                }
            }

            void AddContent(PdfMarkedContentReference m)
            {
                m.Parent = node;
                kids.Add(m);
                if (!contentSeen.Add((m.Page, m.Stream?.Reference?.Number ?? 0, m.Mcid)))
                {
                    Diagnose($"Marked content {m.Mcid} is referenced more than once.", owner);
                }
            }
        }
    }

    // ---- lookups ----

    internal List<PdfStructureItem> KidsOf(PdfStructureNode? node) => node is null ? RootKids : node.Kids;

    internal int PageOf(PdfObject? pg)
    {
        PdfReference? r = pg as PdfReference ?? pg?.Reference;
        return r is null ? -1 : Document.Pages.IndexOfObject(r.Number);
    }

    internal string? Resolve(string type)
    {
        if (type.Length == 0)
        {
            return null;
        }

        PdfDictionary? rm = RootDict.Get("RoleMap") as PdfDictionary;
        string current = type;
        for (int i = 0; i < 32; i++)
        {
            if (StandardTypes.Contains(current))
            {
                return current;
            }

            string? next = rm?.GetName(current);
            if (next is null)
            {
                return null;
            }

            current = next;
        }

        return null;
    }

    internal IEnumerable<PdfDictionary> ClassAttributes(PdfDictionary element)
    {
        if (RootDict.Get("ClassMap") is not PdfDictionary map)
        {
            yield break;
        }

        PdfObject? c = element.Get("C");
        var names = new List<string>();
        if (c is PdfName n)
        {
            names.Add(n.Value);
        }
        else if (c is PdfArray arr)
        {
            for (int i = 0; i < arr.Count && i < 256; i++)
            {
                if (arr[i] is PdfName an)
                {
                    names.Add(an.Value);
                }
            }
        }

        foreach (string name in names)
        {
            PdfObject? v = map.Get(name);
            if (v is PdfDictionary d)
            {
                yield return d;
            }
            else if (v is PdfArray va)
            {
                for (int i = 0; i < va.Count && i < 256; i++)
                {
                    if (va[i] is PdfDictionary vd)
                    {
                        yield return vd;
                    }
                }
            }
        }
    }

    /// <summary>True once the document dropped this tree (<see cref="PdfDocument.RefreshStructureTree"/>, <see cref="PdfDocument.RemoveTags"/>, tagging, PDF/UA-2 conversion): its nodes no longer describe the document. Reading their children or editing them throws <see cref="InvalidOperationException"/>; read <see cref="PdfDocument.StructureTree"/> again.</summary>
    public bool IsStale { get; private set; }

    internal void MarkStale() => IsStale = true;

    internal void ThrowIfStale()
    {
        if (IsStale)
        {
            throw new InvalidOperationException("The structure tree was replaced or refreshed after this object was read; read PdfDocument.StructureTree again.");
        }
    }

    internal void EnsureEditable(PdfStructureItem item)
    {
        ThrowIfStale();
        Document.EnsureEditable(0);
        if (item.IsRemoved)
        {
            throw new InvalidOperationException("The structure item was removed from the tree.");
        }
    }

    // ---- text ----

    internal string TextOf(PdfStructureNode node, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var stack = new Stack<PdfStructureItem>();
        stack.Push(node);
        int visited = 0;
        while (stack.Count > 0 && visited++ < (1 << 22))
        {
            PdfStructureItem item = stack.Pop();
            if (item is PdfStructureNode n)
            {
                for (int i = n.Kids.Count - 1; i >= 0; i--)
                {
                    stack.Push(n.Kids[i]);
                }
            }
            else if (item is PdfMarkedContentReference m && m.Page >= 0 && m.Page < Document.Pages.Count)
            {
                ct.ThrowIfCancellationRequested();
                if (!_text.TryGetValue(m.Page, out Dictionary<int, string>? byMcid))
                {
                    byMcid = BuildPageText(m.Page, ct);
                    _text[m.Page] = byMcid;
                }

                if (byMcid.TryGetValue(m.Mcid, out string? t) && t.Length > 0)
                {
                    if (sb.Length > 0 && sb[^1] != ' ')
                    {
                        sb.Append(' ');
                    }

                    sb.Append(t);
                }
            }
        }

        return sb.ToString().Trim();
    }

    private Dictionary<int, string> BuildPageText(int page, CancellationToken ct)
    {
        var result = new Dictionary<int, string>();
        var builders = new Dictionary<int, StringBuilder>();
        var last = new Dictionary<int, PdfTextGlyph>();
        var spans = new HashSet<int>();
        PdfPageText text = Document.Pages[page].ExtractText(new PdfTextOptions { RetainGlyphs = true, UseStructureTree = false, IncludeClippedAndOffPage = true }, ct);
        foreach (PdfTextGlyph g in text.ReadGlyphs(ct))
        {
            if (g.MarkedContentId < 0)
            {
                continue;
            }

            if (!builders.TryGetValue(g.MarkedContentId, out StringBuilder? sb))
            {
                builders[g.MarkedContentId] = sb = new StringBuilder();
            }

            if (g.ActualTextSpan > 0)
            {
                if (g.ActualText is not null)
                {
                    sb.Append(g.ActualText);
                }

                continue;
            }

            if (last.TryGetValue(g.MarkedContentId, out PdfTextGlyph prev) && sb.Length > 0 && sb[^1] != ' ' && g.Text != " ")
            {
                double size = Math.Max(prev.Size, 1e-3);
                PdfRectangle a = prev.Bounds, b = g.Bounds;
                bool newLine = Math.Abs(b.Bottom - a.Bottom) > size * 0.5 && !g.IsVertical;
                bool gap = b.Left - a.Right > size * 0.15;
                if (newLine || gap)
                {
                    sb.Append(' ');
                }
            }

            sb.Append(g.Text);
            last[g.MarkedContentId] = g;
        }

        foreach ((int id, StringBuilder sb) in builders)
        {
            result[id] = sb.ToString().Trim();
        }

        return result;
    }
}
