using OnePdf.Internal;
using System.Runtime.CompilerServices;

namespace OnePdf;

/// <summary>A page that was copied into a document, ready to go into its page tree.</summary>
internal sealed record ImportedPage(PdfDictionary Dictionary, PdfReference Reference, int Origin, PageLabelDescription? Label);

/// <summary>
/// Copies pages, with everything they depend on, from one document into another (ISO 32000-2 7.7.3 for the page tree
/// and 7.3.10 for indirect objects).
/// </summary>
/// <remarks>
/// The copy is a walk over the object graph with an explicit work stack, so graph depth and cycles cost nothing: every source
/// object is given its target number the first time a reference to it is met, and the work stack holds the objects still to
/// be converted. Nesting inside one object is converted by recursion bounded by <see cref="PdfLimits.MaxNestingDepth"/>.
/// The number of objects is bounded by the target's <see cref="PdfLimits.MaxObjects"/> (<see cref="PdfDocument.AddObject"/>).
/// <para>
/// What is not followed: <c>/Parent</c> of pages, other pages of the source and page tree nodes (a reference to a page that
/// was not imported becomes null), the structure tree (<c>/StructParents</c>, <c>/StructParent</c>), article beads and
/// thumbnails. Destinations to pages that came along are mapped; the others are neutralised.
/// </para>
/// <para>
/// An importer remembers what it copied, so a second import from the same source into the same target reuses fonts, images
/// and other shared objects instead of copying them again (only for a source that is not editable). Annotations are
/// the exception: they belong to one page, so each imported page gets its own.
/// </para>
/// </remarks>
internal sealed partial class PageImporter
{
    private readonly PdfDocument _src;
    private readonly PdfDocument _dst;
    private readonly PdfResolver _root;
    private readonly bool _same;
    private readonly PdfLimits _limits;

    // Source object number to target object number, for everything but annotations and pages (kept between imports).
    private readonly Dictionary<int, int> _map = [];

    // Per batch: annotations (each imported page needs its own), imported pages, and the work still to do.
    private readonly Dictionary<int, int> _local = [];
    private readonly Dictionary<int, PdfReference> _pageMap = [];
    private readonly HashSet<int> _dstPages = [];
    private readonly Stack<(PdfObject Source, int SourceNumber, int Target)> _work = new();
    private readonly Dictionary<PdfObject, PdfReference> _promoted = new(ReferenceEqualityComparer.Instance);
    private readonly List<int> _newGroups = [];
    private readonly List<PdfDictionary> _links = [];
    private readonly List<(PdfDictionary Source, PdfDictionary Target)> _widgets = [];
    private CancellationToken _ct;
    private bool _touched;

    private PageImporter(PdfDocument source, PdfDocument target)
    {
        _src = source;
        _dst = target;
        _root = target.RootResolver;
        _same = ReferenceEquals(source, target);
        _limits = target.Limits;
    }

    /// <summary>The importer for this pair; one is kept per pair when the source cannot change.</summary>
    public static PageImporter For(PdfDocument source, PdfDocument target)
    {
        if (ReferenceEquals(source, target) || source.IsEditable)
        {
            return new PageImporter(source, target);
        }

        return target.Importers.GetValue(source, s => new PageImporter(s, target));
    }

    /// <summary>
    /// Copies the pages at <paramref name="indexes"/> (zero-based, in the source), in that order. A page listed twice is copied twice.
    /// </summary>
    public List<ImportedPage> Import(IReadOnlyList<int> indexes, PdfImportOptions options, CancellationToken ct)
    {
        _ct = ct;
        int mark = _dst.ObjectMark;
        _touched = false;
        var result = new ImportedPage?[indexes.Count];
        var pending = new List<int>(indexes.Count);
        for (int i = 0; i < indexes.Count; i++)
        {
            pending.Add(i);
        }

        // One round copies each source page once; a repeat goes in a round of its own so it gets its own annotations.
        while (pending.Count > 0)
        {
            var round = new List<int>();
            var later = new List<int>();
            var seen = new HashSet<int>();
            foreach (int position in pending)
            {
                (seen.Add(indexes[position]) ? round : later).Add(position);
            }

            bool ok = false;
            try
            {
                RunBatch(indexes, round, result, options);
                ok = true;
            }
            finally
            {
                if (!ok)
                {
                    // Objects reserved for a copy that did not finish are empty placeholders: nothing may reuse them later.
                    _work.Clear();
                    _dst.Importers.Remove(_src);

                    // Until the copies are linked into existing objects nothing refers to them, so their numbers can be given back.
                    if (!_touched)
                    {
                        _dst.RollbackObjects(mark);
                    }
                }
            }

            pending = later;
        }

        if (_src.Version is { } v && string.CompareOrdinal(v, _dst.Version ?? "1.0") > 0)
        {
            _dst.RaiseVersion(v);
        }

        var all = new List<ImportedPage>(result.Length);
        foreach (ImportedPage? p in result)
        {
            all.Add(p!);
        }

        return all;
    }

    private void RunBatch(IReadOnlyList<int> indexes, List<int> round, ImportedPage?[] result, PdfImportOptions options)
    {
        _local.Clear();
        _pageMap.Clear();
        _dstPages.Clear();
        _links.Clear();
        _widgets.Clear();
        PdfPageCollection pages = _src.Pages;
        var targets = new PdfReference[round.Count];
        for (int i = 0; i < round.Count; i++)
        {
            PdfDictionary sd = pages.NodeAt(indexes[round[i]]).Dictionary;
            targets[i] = _dst.AddObject(new PdfDictionary());
            _dstPages.Add(targets[i].Number);
            if (sd.Reference is { } sr)
            {
                _pageMap.TryAdd(sr.Number, targets[i]);
            }
        }

        var pairs = new List<(PdfDictionary Source, PdfDictionary Target)>(round.Count);
        for (int i = 0; i < round.Count; i++)
        {
            int index = indexes[round[i]];
            PdfDictionary page = BuildPage(pages.NodeAt(index), options);
            _dst.SetObject(targets[i].Number, page);
            pairs.Add((pages.NodeAt(index).Dictionary, page));
            result[round[i]] = new ImportedPage(page, targets[i], index, PageLabelDescription.Of(_src, index));
        }

        Drain();
        _touched = true;
        FixLinks();
        if (options.ImportFormFields)
        {
            BuildFields(pairs);
        }

        if (options.ImportStructure)
        {
            ImportStructure(pairs);
        }

        AddOptionalContent();
        if (options.ImportOutlines && !_same)
        {
            ImportOutlineItems();
        }

        FlushNames();
    }

    private PdfDictionary BuildPage(PdfPageNode node, PdfImportOptions options)
    {
        PdfDictionary s = node.Dictionary;
        var d = new PdfDictionary(_root);
        for (int i = 0; i < s.RawCount; i++)
        {
            string key = s.KeyAt(i);
            if (key is "Parent" or "StructParents" or "B" or "Thumb" or "Annots" or "Type")
            {
                continue;
            }

            // A copy within one document can keep pointing at the page's own content and resources: both are immutable once written.
            PdfObject? v = Convert(s.ValueAt(i), _same && key is "Contents" or "Resources", 0);
            if (v is not null)
            {
                d.Set(key, v);
            }
        }

        d.Set("Type", new PdfName("Page"));

        // 7.7.3.4: the attributes a page inherits are written into the page, since the new page tree is not the old one.
        CopyInherited(d, s, "Resources", node.Resources);
        CopyInherited(d, s, "MediaBox", node.MediaBox);
        CopyInherited(d, s, "CropBox", node.CropBox);
        CopyInherited(d, s, "Rotate", node.Rotate);

        if (_src.Resolve(s.GetRaw("Annots")) is PdfArray annots)
        {
            PdfArray? copied = CopyAnnotations(annots, options);
            if (copied is not null)
            {
                d.Set("Annots", copied);
            }
        }

        return d;
    }

    private void CopyInherited(PdfDictionary page, PdfDictionary source, string key, PdfObject? inherited)
    {
        if (inherited is null || source.ContainsKey(key))
        {
            return;
        }

        PdfObject? v;
        if (inherited.Reference is { } r)
        {
            v = Convert(r, _same && key == "Resources", 0);
        }
        else if (inherited is PdfDictionary dict)
        {
            // One direct dictionary is shared by every page below the node that holds it: make it one object here too.
            if (!_promoted.TryGetValue(dict, out PdfReference? promoted))
            {
                PdfObject? converted = Convert(dict, _same && key == "Resources", 0);
                promoted = converted is null ? null : _dst.AddObject(converted);
                if (promoted is not null)
                {
                    _promoted[dict] = promoted;
                }
            }

            v = promoted;
        }
        else
        {
            v = Convert(inherited, false, 0);
        }

        if (v is not null)
        {
            page.Set(key, v);
        }
    }

    private PdfArray? CopyAnnotations(PdfArray annots, PdfImportOptions options)
    {
        var copied = new PdfArray(_root);
        int n = Math.Min(annots.Count, _limits.MaxObjects);
        for (int i = 0; i < n; i++)
        {
            PdfObject raw = annots.GetRaw(i);
            if (raw is PdfReference r)
            {
                if (_src.Resolve(r) is not PdfDictionary a || (!options.ImportFormFields && a.GetName("Subtype") == "Widget"))
                {
                    continue;
                }

                if (!_local.TryGetValue(r.Number, out int target))
                {
                    target = _dst.AddObject(new PdfDictionary()).Number;
                    _local[r.Number] = target;
                    _work.Push((a, r.Number, target));
                }

                copied.Add(new PdfReference(target, 0));
            }
            else if (raw is PdfDictionary direct && (options.ImportFormFields || direct.GetName("Subtype") != "Widget"))
            {
                PdfObject? c = Convert(direct, false, 0);
                if (c is PdfDictionary cd)
                {
                    copied.Add(cd);
                    NoteDictionary(direct, cd);
                }
            }
        }

        return copied.Count == 0 ? null : copied;
    }

    /// <summary>
    /// Converts a value of the source into a value of the target. A reference gets a target object (copied later by
    /// <see cref="Drain"/>); null means the value is dropped (a reference to nothing, or to a page that was not imported).
    /// </summary>
    private PdfObject? Convert(PdfObject value, bool shared, int depth)
    {
        switch (value)
        {
            case PdfReference r:
                return MapReference(r, shared);
            case PdfDictionary d:
                return ConvertDictionary(d, shared, depth);
            case PdfArray a:
                if (depth > _limits.MaxNestingDepth)
                {
                    throw new PdfLimitException("A value is nested more deeply than PdfLimits.MaxNestingDepth allows.");
                }

                var copy = new PdfArray(_root);
                for (int i = 0; i < a.Count; i++)
                {
                    copy.Add(Convert(a.GetRaw(i), shared, depth + 1) ?? PdfNull.Instance);
                }

                return copy;
            case PdfStream:
                return null; // a stream is always an indirect object; reaching one directly means the file is damaged
            default:
                return Simple(value);
        }
    }

    /// <summary>A new object with the same value: an object that was indirect in the source must not carry that identity into the target.</summary>
    private static PdfObject Simple(PdfObject v) => v switch
    {
        PdfInteger i => new PdfInteger(i.Value),
        PdfReal r => new PdfReal(r.Value),
        PdfBoolean b => new PdfBoolean(b.Value),
        PdfName n => new PdfName(n.Value),
        PdfString s => new PdfString(s.Bytes.ToArray()),
        _ => PdfNull.Instance,
    };

    private PdfDictionary ConvertDictionary(PdfDictionary d, bool shared, int depth)
    {
        if (depth > _limits.MaxNestingDepth)
        {
            throw new PdfLimitException("A value is nested more deeply than PdfLimits.MaxNestingDepth allows.");
        }

        var copy = new PdfDictionary(_root);
        CopyEntries(d, copy, shared, depth, skipParent: d.GetName("Subtype") == "Widget");
        return copy;
    }

    private void CopyEntries(PdfDictionary from, PdfDictionary to, bool shared, int depth, bool skipParent, bool isStream = false)
    {
        for (int i = 0; i < from.RawCount; i++)
        {
            string key = from.KeyAt(i);
            if (key is "StructParent" or "StructParents" || (skipParent && key == "Parent"))
            {
                continue;
            }

            // The stream's bytes are stored in memory in the copy; an external file is never opened, so it is left behind.
            if (isStream && key is "Length" or "F" or "FFilter" or "FDecodeParms")
            {
                continue;
            }

            PdfObject? v = Convert(from.ValueAt(i), shared, depth + 1);
            if (v is not null)
            {
                to.Set(key, v);
            }
        }
    }

    private PdfReference? MapReference(PdfReference r, bool shared)
    {
        int n = r.Number;
        if (shared)
        {
            return new PdfReference(n, r.Generation);
        }

        if (_pageMap.TryGetValue(n, out PdfReference? page))
        {
            return new PdfReference(page.Number, 0);
        }

        if (_local.TryGetValue(n, out int local))
        {
            return new PdfReference(local, 0);
        }

        if (_map.TryGetValue(n, out int known))
        {
            return new PdfReference(known, 0);
        }

        PdfObject? o = _src.GetObject(n);
        if (o is null)
        {
            return null;
        }

        if (o is PdfDictionary d)
        {
            // Other pages and the nodes of the page tree stay behind; reaching them through /Parent, /P or a destination must not pull in the document.
            string? type = d.GetName("Type");
            if (type is "Page" or "Pages" || _src.Pages.IndexOfObject(n) >= 0)
            {
                // Copying within one document: the other pages are still there to point at.
                return _same ? new PdfReference(n, r.Generation) : null;
            }

            if (type == "OCG")
            {
                _newGroups.Add(n);
            }
        }

        int target = _dst.AddObject(new PdfDictionary()).Number;
        _map[n] = target;
        _work.Push((o, n, target));
        return new PdfReference(target, 0);
    }

    /// <summary>Converts the objects waiting on the work stack (which may queue more) and stores them under their reserved numbers.</summary>
    private void Drain()
    {
        while (_work.TryPop(out (PdfObject Source, int SourceNumber, int Target) w))
        {
            _ct.ThrowIfCancellationRequested();
            PdfObject converted = ConvertIndirect(w.Source);
            _dst.SetObject(w.Target, converted);
            if (w.Source is PdfDictionary sd && converted is PdfDictionary cd)
            {
                NoteDictionary(sd, cd);
            }
        }
    }

    private PdfObject ConvertIndirect(PdfObject o)
    {
        switch (o)
        {
            case PdfStream s:
                var dict = new PdfDictionary(_root);
                CopyEntries(s.Dictionary, dict, false, 0, skipParent: false, isStream: true);
                var copy = new PdfStream(dict, _root, 0);
                ReadOnlyMemory<byte> stored = s.IsExternal ? ReadOnlyMemory<byte>.Empty : s.GetStoredBytes(s.FilterNames);
                copy.SetStoredBytes(stored.Span);
                return copy;
            case PdfDictionary d:
                return ConvertDictionary(d, false, 0);
            default:
                return Convert(o, false, 0) ?? PdfNull.Instance;
        }
    }

    // Links and widgets need work once everything they point at has its target number.
    private void NoteDictionary(PdfDictionary source, PdfDictionary copy)
    {
        switch (copy.GetName("Subtype"))
        {
            case "Link":
                _links.Add(copy);
                break;
            case "Widget" when copy.Reference is not null:
                _widgets.Add((source, copy));
                break;
        }
    }
}

/// <summary>The page label of one page in the scheme of its source document (ISO 32000-2 12.4.2).</summary>
internal readonly record struct PageLabelDescription(PdfPageLabelStyle Style, string Prefix, long Number)
{
    /// <summary>The label scheme at a page of <paramref name="doc"/>, or null when the document defines no labels.</summary>
    public static PageLabelDescription? Of(PdfDocument doc, int index)
    {
        if (doc.PageLabels is not { } labels)
        {
            return null;
        }

        PdfPageLabelRange? range = null;
        foreach (PdfPageLabelRange r in labels.Ranges)
        {
            if (r.FirstPage <= index)
            {
                range = r;
            }
            else
            {
                break;
            }
        }

        // Pages before the first range count as decimal numbers (the specification requires a range at page 0).
        return range is null
            ? new PageLabelDescription(PdfPageLabelStyle.Arabic, string.Empty, index + 1L)
            : new PageLabelDescription(range.Style, range.Prefix, range.Start + (long)(index - range.FirstPage));
    }
}
