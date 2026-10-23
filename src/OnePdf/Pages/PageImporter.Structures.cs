using OnePdf.Internal;
using System.Text;

namespace OnePdf;

// Destinations, form fields, optional content groups and bookmarks of imported pages.
internal sealed partial class PageImporter
{
    private readonly Dictionary<string, string?> _nameMap = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedNames = new(StringComparer.Ordinal);
    private readonly List<(string Name, PdfArray Destination)> _newNames = [];

    // ---- links and destinations ----

    /// <summary>
    /// A copied link keeps its destination only when the destination page came along: the others lose <c>/Dest</c> or their
    /// go-to action, and the link stays as an inert rectangle. Named destinations that are used are copied under a free name.
    /// </summary>
    private void FixLinks()
    {
        foreach (PdfDictionary link in _links)
        {
            FixHolder(link);
        }

        _links.Clear();
    }

    private void FixHolder(PdfDictionary holder)
    {
        if (holder.GetRaw("Dest") is { } dest)
        {
            PdfObject? mapped = RemapDestination(dest);
            if (mapped is null)
            {
                holder.Remove("Dest");
            }
            else
            {
                holder.Set("Dest", mapped);
            }
        }

        if (holder.Get("A") is PdfDictionary action && action.GetName("S") == "GoTo")
        {
            PdfObject? mapped = action.GetRaw("D") is { } d ? RemapDestination(d) : null;
            if (mapped is null)
            {
                holder.Remove("A");
            }
            else
            {
                action.Set("D", mapped);
            }
        }
    }

    // value is already a value of the target (page references converted, pages that did not come along null).
    private PdfObject? RemapDestination(PdfObject value)
    {
        PdfObject? resolved = _dst.Resolve(value);
        switch (resolved)
        {
            case PdfArray a:
                return FirstReference(a) is { } r && (_dstPages.Contains(r.Number) || (_same && _dst.Pages.IndexOfObject(r.Number) >= 0)) ? value : null;
            case PdfDictionary d:
                return d.GetRaw("D") is { } inner && RemapDestination(inner) is not null ? value : null;
            case PdfString s:
                return MapName(s.GetText()) is { } ns ? PdfString.FromText(ns) : null;
            case PdfName n:
                return MapName(n.Value) is { } nn ? new PdfName(nn) : null;
            default:
                return null;
        }
    }

    private string? MapName(string name)
    {
        if (_nameMap.TryGetValue(name, out string? known))
        {
            return known;
        }

        if (_same)
        {
            return name; // the name is already defined in this document
        }

        string? result = null;
        PdfArray? array = DestinationArray(_src, _src.NamedDestinationMap.GetValueOrDefault(name));
        if (FirstReference(array) is { } page && _pageMap.ContainsKey(page.Number) && array is not null && Convert(array, false, 0) is PdfArray converted)
        {
            string candidate = name;
            int k = 2;
            while (true)
            {
                if (_usedNames.Contains(candidate))
                {
                    candidate = name + "_" + k++;
                    continue;
                }

                if (!_dst.NamedDestinationMap.TryGetValue(candidate, out PdfObject? existing))
                {
                    _usedNames.Add(candidate);
                    _newNames.Add((candidate, converted));
                    result = candidate;
                    break;
                }

                // The same name for the same page in the target already means the same thing.
                if (FirstReference(DestinationArray(_dst, existing)) is { } ep && FirstReference(converted) is { } cp && ep.Number == cp.Number)
                {
                    result = candidate;
                    break;
                }

                candidate = name + "_" + k++;
            }
        }

        _nameMap[name] = result;
        return result;
    }

    /// <summary>The page reference a destination array starts with, or null (the array is empty or starts with something else).</summary>
    internal static PdfReference? FirstReference(PdfArray? array) => array is { Count: > 0 } && array.GetRaw(0) is PdfReference r ? r : null;

    /// <summary>The explicit destination array behind a destination value (an array, or a dictionary with <c>/D</c>), or null.</summary>
    internal static PdfArray? DestinationArray(PdfDocument doc, PdfObject? value)
    {
        for (int hops = 0; hops < 4; hops++)
        {
            switch (doc.Resolve(value))
            {
                case PdfArray a:
                    return a;
                case PdfDictionary d:
                    value = d.GetRaw("D");
                    break;
                default:
                    return null;
            }
        }

        return null;
    }

    // Adds the copied named destinations to the target's /Names /Dests name tree, which is rewritten as one sorted node (7.9.6).
    private void FlushNames()
    {
        if (_newNames.Count == 0)
        {
            return;
        }

        PdfDictionary catalog = _dst.Catalog;
        PdfDictionary? names = catalog.GetDictionary("Names");
        if (names is null)
        {
            catalog.SetEntry("Names", new PdfDictionary());
            names = catalog.GetDictionary("Names")!;
        }

        var pairs = new List<(byte[] Key, PdfObject Value)>();
        foreach (KeyValuePair<PdfObject, PdfObject> kv in _dst.ReadTree(names.GetDictionary("Dests"), false, "named destinations"))
        {
            pairs.Add((((PdfString)kv.Key).Bytes.ToArray(), kv.Value));
        }

        foreach ((string name, PdfArray destination) in _newNames)
        {
            pairs.Add((Encoding.UTF8.GetBytes(name), destination));
        }

        pairs.Sort(static (a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));
        var list = new PdfArray();
        foreach ((byte[] key, PdfObject value) in pairs)
        {
            list.Append(new PdfString(key));
            list.Append(value);
        }

        var leaf = new PdfDictionary();
        leaf.SetEntry("Names", list);
        names.SetEntry("Dests", leaf);
        _newNames.Clear();
        _dst.ResetNamedDestinations();
    }

    // ---- form fields ----

    private sealed class FieldNode(PdfDictionary dictionary, PdfReference reference, PdfResolver root)
    {
        public PdfDictionary Dictionary { get; } = dictionary;

        public PdfReference Reference { get; } = reference;

        public PdfArray Kids { get; } = new(root);
    }

    /// <summary>
    /// Rebuilds the field tree above the copied widgets: each source field that has a widget on an imported page (or a descendant
    /// that does) is copied once, with only the kids that came along, and the top-level ones are added to the target's
    /// <c>/AcroForm</c> (ISO 32000-2 12.7.3). A top-level name already in use gets a numeric suffix.
    /// </summary>
    private void BuildFields(List<(PdfDictionary Source, PdfDictionary Target)> pages)
    {
        if (_widgets.Count == 0)
        {
            return;
        }

        var fields = new Dictionary<int, FieldNode>();
        var tops = new List<PdfDictionary>();
        var made = new List<FieldNode>();
        foreach ((PdfDictionary source, PdfDictionary widget) in _widgets)
        {
            if (source.GetRaw("Parent") is not PdfReference parent)
            {
                // A widget that is its own field.
                if (widget.ContainsKey("FT") || widget.ContainsKey("T"))
                {
                    DropSignatureValue(widget);
                    tops.Add(widget);
                }

                continue;
            }

            PdfDictionary child = widget;
            PdfReference childRef = widget.Reference!;
            for (int guard = 0; guard <= _limits.MaxNestingDepth; guard++)
            {
                if (fields.TryGetValue(parent.Number, out FieldNode? known))
                {
                    known.Kids.Add(childRef);
                    child.Set("Parent", known.Reference);
                    break;
                }

                if (_src.GetObject(parent.Number) is not PdfDictionary sourceField)
                {
                    tops.Add(child);
                    break;
                }

                var copy = new PdfDictionary(_root);
                CopyEntries(sourceField, copy, false, 0, skipParent: false);
                copy.Remove("Kids");
                copy.Remove("Parent");
                DropSignatureValue(copy);
                var node = new FieldNode(copy, _dst.AddObject(copy), _root);
                fields[parent.Number] = node;
                made.Add(node);
                node.Kids.Add(childRef);
                child.Set("Parent", node.Reference);
                if (sourceField.GetRaw("Parent") is PdfReference up)
                {
                    child = copy;
                    childRef = node.Reference;
                    parent = up;
                    continue;
                }

                tops.Add(copy);
                break;
            }
        }

        foreach (FieldNode node in made)
        {
            node.Dictionary.SetEntry("Kids", node.Kids);
        }

        Drain();
        bool needsAppearances = BakeAppearances(pages);
        var imported = new List<PdfDictionary>(tops);
        foreach (FieldNode node in made)
        {
            imported.Add(node.Dictionary);
        }

        foreach ((_, PdfDictionary widget) in _widgets)
        {
            imported.Add(widget);
        }

        AddToForm(tops, imported, needsAppearances);
    }

    /// <summary>
    /// A source form that sets <c>/NeedAppearances</c> has its text and choice fields drawn from their values rather than from
    /// stored appearances. That flag is a property of the whole document, so setting it in the target would redraw every field there
    /// too. Instead the appearance is generated now and stored in the copied widget. Returns true when some widget could not be
    /// given one, in which case the target gets the flag after all.
    /// </summary>
    private bool BakeAppearances(List<(PdfDictionary Source, PdfDictionary Target)> pages)
    {
        if (!Appearance.AppearanceGenerator.FormNeedsAppearances(_src))
        {
            return false;
        }

        var byNumber = new Dictionary<int, PdfDictionary>();
        foreach ((PdfDictionary source, PdfDictionary widget) in _widgets)
        {
            if (source.Reference is { } r)
            {
                byNumber[r.Number] = widget;
            }
        }

        bool failed = false;
        foreach ((PdfDictionary sourcePage, _) in pages)
        {
            int index = sourcePage.Reference is { } pr ? _src.Pages.IndexOfObject(pr.Number) : -1;
            if (index < 0)
            {
                continue;
            }

            foreach (PdfAnnotation a in _src.Pages[index].Annotations)
            {
                if (a.Subtype != "Widget" || a.Dictionary.Reference is not { } ar || !byNumber.TryGetValue(ar.Number, out PdfDictionary? target)
                    || !Appearance.WidgetAppearance.RegeneratesUnderNeedAppearances(_src, a.Dictionary, a.GetAppearanceStream() is not null))
                {
                    continue;
                }

                PdfGeneratedAppearance? generated = a.GenerateAppearance(_ct);
                if (generated is null || Convert(generated.Dictionary, false, 0) is not PdfDictionary form)
                {
                    failed = true;
                    continue;
                }

                var stream = new PdfStream(form, _root, 0);
                stream.SetData(generated.Content.Span);
                var ap = new PdfDictionary();
                ap.SetEntry("N", _dst.AddObject(stream));
                target.SetEntry("AP", ap);
            }
        }

        Drain();
        return failed;
    }

    // A signature value is bound to the bytes of the file it signed; copied into another file it would only mislead.
    private static void DropSignatureValue(PdfDictionary field)
    {
        if (field.GetName("FT") == "Sig")
        {
            field.Remove("V");
        }
    }

    private void AddToForm(List<PdfDictionary> tops, List<PdfDictionary> imported, bool needsAppearances)
    {
        if (tops.Count == 0)
        {
            return;
        }

        PdfDictionary catalog = _dst.Catalog;
        if (catalog.GetDictionary("AcroForm") is null)
        {
            catalog.SetEntry("AcroForm", new PdfDictionary());
        }

        PdfDictionary form = catalog.GetDictionary("AcroForm")!;
        if (form.GetArray("Fields") is null)
        {
            form.SetEntry("Fields", new PdfArray());
        }

        PdfArray fieldList = form.GetArray("Fields")!;
        // Copying within one document: the form already has its defaults and resources.
        PdfDictionary? sourceForm = _same ? null : _src.Catalog.GetDictionary("AcroForm");
        Dictionary<string, string>? fontRenames = sourceForm is null ? null : MergeResources(form, sourceForm);

        // Names of the fields already at the top of the target: two fields of one name would be one field to a viewer.
        var taken = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < fieldList.Count && i < _limits.MaxObjects; i++)
        {
            if (fieldList[i] is PdfDictionary f && f.GetString("T") is { } t)
            {
                taken.Add(t.GetText());
            }
        }

        foreach (PdfDictionary top in tops)
        {
            if (top.GetString("T") is { } partial)
            {
                string name = partial.GetText();
                string candidate = name;
                for (int k = 2; taken.Contains(candidate); k++)
                {
                    candidate = name + "_" + k;
                }

                if (candidate != name)
                {
                    top.Set("T", PdfString.FromText(candidate));
                    _dst.ReportInfo($"The form field '{name}' was renamed '{candidate}' because the target already has a field of that name.", top.Reference?.Number ?? -1);
                }

                taken.Add(candidate);
            }

            // The default appearance and quadding come from the source form unless the field says otherwise (12.7.3.1).
            foreach (string key in (ReadOnlySpan<string>)["DA", "Q"])
            {
                if (!top.ContainsKey(key) && sourceForm?.GetRaw(key) is { } inherited && Convert(inherited, false, 0) is { } copy)
                {
                    top.Set(key, copy);
                }
            }

            fieldList.Append(top.Reference);
        }

        if (fontRenames is { Count: > 0 })
        {
            foreach (PdfDictionary field in imported)
            {
                if (field.GetString("DA") is { } da && RenameFonts(da.GetText(), fontRenames) is { } renamed)
                {
                    field.Set("DA", PdfString.FromText(renamed));
                }
            }
        }

        if (needsAppearances)
        {
            form.SetEntry("NeedAppearances", new PdfBoolean(true));
        }

        if (form.RemoveEntry("XFA"))
        {
            _dst.ReportInfo("The XFA form data of the target was dropped: it no longer describes the fields.");
        }

        Drain();
        _dst.ResetForm();
    }

    // /DR: add the default resources the source's fields use. A name the target already uses for another object is given a free
    // name instead, and the returned map (old name to new, fonts only) is applied to the default appearance strings of the copied
    // fields, which name their font through /DR.
    private Dictionary<string, string>? MergeResources(PdfDictionary form, PdfDictionary sourceForm)
    {
        if (sourceForm.Get("DR") is not PdfDictionary sourceDr)
        {
            return null;
        }

        if (form.GetDictionary("DR") is null)
        {
            form.SetEntry("DR", new PdfDictionary());
        }

        PdfDictionary dr = form.GetDictionary("DR")!;
        Dictionary<string, string>? renames = null;
        for (int i = 0; i < sourceDr.RawCount; i++)
        {
            string category = sourceDr.KeyAt(i);
            if (sourceDr.Get(category) is not PdfDictionary sourceCategory)
            {
                continue;
            }

            if (dr.GetDictionary(category) is null)
            {
                dr.SetEntry(category, new PdfDictionary());
            }

            PdfDictionary targetCategory = dr.GetDictionary(category)!;
            for (int j = 0; j < sourceCategory.RawCount; j++)
            {
                string name = sourceCategory.KeyAt(j);
                if (Convert(sourceCategory.ValueAt(j), false, 1) is not { } copy)
                {
                    continue;
                }

                string use = name;
                if (targetCategory.GetRaw(name) is { } existing)
                {
                    if (existing is PdfReference er && copy is PdfReference cr && er.Number == cr.Number)
                    {
                        continue;
                    }

                    for (int k = 2; targetCategory.ContainsKey(use); k++)
                    {
                        use = name + "_" + k;
                    }

                    if (category == "Font")
                    {
                        (renames ??= [])[name] = use;
                    }
                }

                targetCategory.SetEntry(use, copy);
            }
        }

        return renames;
    }

    // Replaces font names in a default appearance string (/Name size Tf ...); null when nothing changes.
    private static string? RenameFonts(string da, Dictionary<string, string> renames)
    {
        var sb = new StringBuilder(da.Length + 8);
        bool changed = false;
        for (int i = 0; i < da.Length;)
        {
            if (da[i] != '/')
            {
                sb.Append(da[i++]);
                continue;
            }

            int end = i + 1;
            while (end < da.Length && !char.IsWhiteSpace(da[end]) && da[end] is not ('/' or '(' or '[' or '<'))
            {
                end++;
            }

            string name = da.Substring(i + 1, end - i - 1);
            if (renames.TryGetValue(name, out string? use))
            {
                sb.Append('/').Append(use);
                changed = true;
            }
            else
            {
                sb.Append(da, i, end - i);
            }

            i = end;
        }

        return changed ? sb.ToString() : null;
    }

    // ---- optional content ----

    /// <summary>Registers the optional content groups that came along in the target's <c>/OCProperties</c>, with the visibility they had (ISO 32000-2 8.11.4.3).</summary>
    private void AddOptionalContent()
    {
        if (_newGroups.Count == 0)
        {
            return;
        }

        Dictionary<int, bool> visibility = [];
        foreach (PdfOptionalContentGroup g in _src.OptionalContentGroups)
        {
            if (g.Dictionary.Reference is { } r)
            {
                visibility[r.Number] = g.IsVisibleByDefault;
            }
        }

        PdfDictionary catalog = _dst.Catalog;
        if (catalog.GetDictionary("OCProperties") is null)
        {
            catalog.SetEntry("OCProperties", new PdfDictionary());
        }

        PdfDictionary props = catalog.GetDictionary("OCProperties")!;
        EnsureArray(props, "OCGs");
        if (props.GetDictionary("D") is null)
        {
            props.SetEntry("D", new PdfDictionary());
        }

        PdfDictionary config = props.GetDictionary("D")!;
        bool baseOn = config.GetName("BaseState") != "OFF";
        foreach (int n in _newGroups)
        {
            PdfReference copy = new(_map[n], 0);
            props.GetArray("OCGs")!.Append(copy);
            bool visible = visibility.GetValueOrDefault(n, true);
            if (baseOn != visible)
            {
                string list = baseOn ? "OFF" : "ON";
                EnsureArray(config, list);
                config.GetArray(list)!.Append(copy);
            }
        }

        _newGroups.Clear();
        _dst.ResetOptionalContent();
    }

    private static void EnsureArray(PdfDictionary d, string key)
    {
        if (d.GetArray(key) is null)
        {
            d.SetEntry(key, new PdfArray());
        }
    }

    // ---- bookmarks ----

    private void ImportOutlineItems()
    {
        if (_src.Catalog.GetDictionary("Outlines")?.GetDictionary("First") is not { } first)
        {
            return;
        }

        var topNodes = new List<OutlineNode>();
        var visited = new HashSet<int>();
        var stack = new Stack<(PdfDictionary? Item, List<OutlineNode> Sink, int Depth)>();
        stack.Push((first, topNodes, 1));
        long count = 0;
        while (stack.Count > 0)
        {
            (PdfDictionary? cur, List<OutlineNode> sink, int depth) = stack.Pop();
            if (cur?.Reference is not { } r || !visited.Add(r.Number) || ++count > _limits.MaxObjects)
            {
                continue;
            }

            _ct.ThrowIfCancellationRequested();
            var item = new PdfDictionary(_root);
            foreach (string key in (ReadOnlySpan<string>)["Title", "C", "F", "Dest", "A"])
            {
                if (cur.GetRaw(key) is { } v && Convert(v, false, 0) is { } c)
                {
                    item.Set(key, c);
                }
            }

            var node = new OutlineNode(item, cur.GetInt("Count") > 0);
            sink.Add(node);
            stack.Push((cur.GetDictionary("Next"), sink, depth));
            if (cur.GetDictionary("First") is { } child && depth < _limits.MaxNestingDepth)
            {
                stack.Push((child, node.Kids, depth + 1));
            }
        }

        // Actions and destinations are fixed once the objects they refer to are copied.
        Drain();
        List<OutlineNode> order = OutlineEditor.PreOrder(topNodes);
        foreach (OutlineNode n in order)
        {
            FixHolder(n.Dictionary);
        }

        // Items that lead nowhere that came along are dropped; a heading with live descendants stays.
        for (int i = order.Count - 1; i >= 0; i--)
        {
            OutlineNode n = order[i];
            n.Kids = n.Kids.FindAll(static k => k.Alive);
            n.Alive = n.Kids.Count > 0 || n.Dictionary.ContainsKey("Dest") || n.Dictionary.ContainsKey("A");
        }

        topNodes = topNodes.FindAll(static k => k.Alive);
        if (topNodes.Count == 0)
        {
            return;
        }

        List<OutlineNode> existing = OutlineEditor.Read(_dst);
        existing.AddRange(topNodes);
        OutlineEditor.Write(_dst, existing);
        _dst.ResetOutline();
    }
}
