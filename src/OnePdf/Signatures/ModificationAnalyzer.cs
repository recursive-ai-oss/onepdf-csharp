using System.Linq;

namespace OnePdf.Internal;

/// <summary>
/// Compares the revision a signature covers with the newest one, object by object, and classifies what changed. The rules
/// follow ISO 32000-2 12.8.2.2 (DocMDP) and 12.7.5.5 (FieldMDP) and are advisory: a viewer may be stricter or looser.
/// <list type="bullet">
/// <item>Permission level: the <c>/P</c> of the DocMDP transform of the certifying signature recorded in <c>/Perms /DocMDP</c>
/// of the signed revision (1 no changes, 2 form fill and signing, 3 also annotations). A lock dictionary's <c>/P</c> lowers it.
/// With no certification the level is taken as 2, so form fill, signing and validation data pass and anything else is flagged.</item>
/// <item>Always allowed: <c>/DSS</c> validation data, document time-stamps, the cross-reference and object-stream objects
/// themselves, and a changed document information dictionary or catalog <c>/Metadata</c> (reported as "other", since tools
/// refresh both whenever they save).</item>
/// <item>Level 2 allows: changing <c>/V</c>, <c>/AP</c>, <c>/AS</c> or <c>/I</c> of a field, adding signature or form fields to
/// <c>/AcroForm</c> and to a page's <c>/Annots</c>, and adding a signature.</item>
/// <item>Level 3 also allows adding, removing and changing annotations.</item>
/// <item>A field covered by a lock (<c>/Action /All</c>, <c>/Include</c>, <c>/Exclude</c>) of this or an earlier signature may not
/// change at all.</item>
/// <item>Everything else (page content, resources, the page tree, the catalog, deleted objects) is a disallowed change.</item>
/// </list>
/// New objects take the category of the changed object that refers to them.
/// </summary>
internal static class ModificationAnalyzer
{
    private const int MaxChangedObjects = 1 << 20;
    private const int MaxCompareNodes = 1 << 18;

    private static readonly HashSet<string> FillKeys = ["V", "AP", "AS", "I"];
    private static readonly HashSet<string> AnnotKeys = ["Rect", "F", "C", "Border", "BS", "Contents", "NM", "M", "CA", "MK", "DA", "Q", "BE", "RC", "IC", "L", "LE", "QuadPoints", "Popup", "State", "StateModel", "Vertices", "InkList", "RD", "Subj", "T", "CreationDate", "IRT", "RT", "Name", "Open", "Popup"];
    private static readonly HashSet<string> AcroFormKeys = ["Fields", "SigFlags", "DA", "DR", "NeedAppearances", "Q"];

    public static void Analyze(PdfSignature sig, PdfSignatureVerification v, List<string> warnings, CancellationToken ct)
    {
        PdfDocument doc = sig.Document;
        if (v.ByteRange != PdfByteRangeStatus.Valid)
        {
            v.Coverage = PdfSignatureCoverage.Unknown;
            return;
        }

        IReadOnlyList<PdfRevision> revs = doc.Revisions;
        int r = v.CoveredRevision;
        int last = revs.Count - 1;
        // The level is read as the signed revision had it: a later update must not be able to raise it by rewriting the dictionary.
        int? level = DocMdpLevel(revs[r]) ?? sig.CertificationLevel;
        v.DocMdpPermission = level;
        long fileLength = doc.FileLength;
        long signedEnd = sig.Range.SecondStart + sig.Range.SecondLength;
        bool unsignedTail = signedEnd < fileLength && revs[last].EndOffset < fileLength && HasNonWhitespace(doc, revs[last].EndOffset, fileLength, ct);
        if (r >= last && !unsignedTail)
        {
            v.Coverage = PdfSignatureCoverage.WholeDocument;
            return;
        }

        var locks = new List<PdfSignatureLock>();
        foreach (PdfSignature other in doc.GetSignatures())
        {
            if (other.FieldLock is { } l && other.Range.Status == PdfByteRangeStatus.Valid && other.Range.Revision <= r)
            {
                locks.Add(l);
                if (l.Permission is { } lp)
                {
                    level = Math.Min(level ?? 3, lp);
                }
            }
        }

        int effective = level ?? 2;
        PdfRevision signed = revs[r];
        PdfRevision newest = revs[last];
        var numbers = new SortedSet<int>();
        for (int k = r + 1; k <= last; k++)
        {
            foreach (int n in revs[k].ChangedObjectNumbers)
            {
                if (numbers.Count >= MaxChangedObjects)
                {
                    warnings.Add("Too many objects changed after signing; the analysis was cut short.");
                    break;
                }

                numbers.Add(n);
            }
        }

        var ctx = new Context(doc, signed, newest, effective, locks);
        var changes = new List<PdfSignatureChange>();
        if (unsignedTail)
        {
            // Bytes after the last %%EOF belong to no revision. A reader that rebuilds the cross-reference table by scanning (as
            // repair does) would take objects from them, so they count as an unsigned change, not as padding.
            changes.Add(new PdfSignatureChange(-1, PdfChangeCategory.Other, false, "data follows the last %%EOF and is not covered by any revision"));
        }

        PdfObject? signedRoot = RootOf(revs, r);
        for (int k = r + 1; k <= last; k++)
        {
            if (revs[k].Trailer.GetRaw("Root") is PdfReference moved && !SameReference(signedRoot, moved))
            {
                changes.Add(new PdfSignatureChange(moved.Number, PdfChangeCategory.Other, false, "a later trailer points at a different catalog"));
            }
        }
        var fresh = new List<(int Number, PdfObject Value)>();
        var rootCategory = new Dictionary<int, (PdfChangeCategory Cat, bool Allowed, string Why)>();
        foreach (int n in numbers)
        {
            ct.ThrowIfCancellationRequested();
            PdfObject? before = Read(signed, n, warnings);
            PdfObject? after = Read(newest, n, warnings);
            if (before is null && after is null)
            {
                continue;
            }

            if (before is null)
            {
                fresh.Add((n, after!));
                continue;
            }

            if (after is not null && DeepEqual(before, after))
            {
                continue;
            }

            Verdict verdict = after is null ? ctx.Deleted(n, before) : ctx.Classify(n, before, after);
            changes.Add(new PdfSignatureChange(n, verdict.Category, verdict.Allowed, verdict.Description));
            if (after is not null)
            {
                rootCategory[n] = (verdict.Category, verdict.Allowed, verdict.Description);
            }
        }

        ClassifyNewObjects(ctx, fresh, rootCategory, changes, newest, ct);
        changes.Sort(static (a, b) => a.ObjectNumber.CompareTo(b.ObjectNumber));
        v.Changes = changes;
        v.Coverage = changes.Exists(static c => !c.Allowed) ? PdfSignatureCoverage.EarlierRevisionDisallowedChanges : PdfSignatureCoverage.EarlierRevisionAllowedChanges;
    }

    private static PdfObject? RootOf(IReadOnlyList<PdfRevision> revs, int from)
    {
        for (int i = from; i >= 0; i--)
        {
            if (revs[i].Trailer.GetRaw("Root") is { } root)
            {
                return root;
            }
        }

        return null;
    }

    private static bool SameReference(PdfObject? a, PdfReference b) => a is PdfReference ra && ra.Number == b.Number && ra.Generation == b.Generation;

    private static bool HasNonWhitespace(PdfDocument doc, long from, long to, CancellationToken ct)
    {
        const int chunk = 1 << 16;
        for (long pos = from; pos < to; pos += chunk)
        {
            ct.ThrowIfCancellationRequested();
            foreach (byte b in doc.ReadRange(pos, (int)Math.Min(chunk, to - pos)).Span)
            {
                if (b is not (0 or 9 or 10 or 12 or 13 or 32))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static PdfObject? Read(PdfRevision rev, int n, List<string> warnings)
    {
        try
        {
            return rev.GetObject(n);
        }
        catch (PdfFormatException e)
        {
            warnings.Add($"Object {n} could not be read in revision {rev.Index}: {e.Message}");
            return new PdfName("unreadable " + n);
        }
    }

    private static int? DocMdpLevel(PdfRevision rev)
    {
        if (rev.Catalog?.GetDictionary("Perms")?.GetDictionary("DocMDP") is not { } sd || sd.Get("Reference") is not PdfArray refs)
        {
            return null;
        }

        for (int i = 0; i < Math.Min(refs.Count, 16); i++)
        {
            if (refs[i] is PdfDictionary t && t.GetName("TransformMethod") == "DocMDP")
            {
                return t.GetDictionary("TransformParams") is { } p ? PdfSignature.ClampLevel(p.GetInt("P", 2)) : 2;
            }
        }

        return 2;
    }

    private readonly record struct Verdict(PdfChangeCategory Category, bool Allowed, string Description);

    private static int Severity(PdfChangeCategory c) => c switch
    {
        PdfChangeCategory.Other => 5,
        PdfChangeCategory.PageContent => 4,
        PdfChangeCategory.Annotation => 3,
        PdfChangeCategory.FormFieldValue => 2,
        PdfChangeCategory.NewSignature => 1,
        _ => 0,
    };

    // New objects: signatures stand on their own; the rest take the verdict of the changed object that reaches them.
    private static void ClassifyNewObjects(Context ctx, List<(int Number, PdfObject Value)> fresh, Dictionary<int, (PdfChangeCategory Cat, bool Allowed, string Why)> roots,
        List<PdfSignatureChange> changes, PdfRevision newest, CancellationToken ct)
    {
        var freshByNumber = new Dictionary<int, PdfObject>();
        foreach ((int n, PdfObject o) in fresh)
        {
            freshByNumber[n] = o;
        }

        var assigned = new Dictionary<int, (PdfChangeCategory Cat, bool Allowed, string Why)>();
        var work = new Queue<(int From, PdfObject Value, (PdfChangeCategory Cat, bool Allowed, string Why) V)>();
        foreach ((int n, (PdfChangeCategory, bool, string) verdict) in roots.Select(static kv => (kv.Key, kv.Value)))
        {
            if (newest.GetObject(n) is { } value)
            {
                work.Enqueue((n, value, verdict));
            }
        }

        // The document information dictionary may be replaced by an update.
        if (newest.Trailer.GetRaw("Info") is PdfReference info && freshByNumber.TryGetValue(info.Number, out PdfObject? infoObj))
        {
            assigned[info.Number] = (PdfChangeCategory.Other, true, "new document information dictionary");
            work.Enqueue((info.Number, infoObj, assigned[info.Number]));
        }

        int budget = 1 << 20;
        while (work.Count > 0 && budget-- > 0)
        {
            ct.ThrowIfCancellationRequested();
            (_, PdfObject value, (PdfChangeCategory Cat, bool Allowed, string Why) verdict) = work.Dequeue();
            foreach (int target in References(value))
            {
                if (!freshByNumber.TryGetValue(target, out PdfObject? obj))
                {
                    continue;
                }

                (PdfChangeCategory Cat, bool Allowed, string Why) mine = verdict;
                if (IsSignatureDictionary(obj))
                {
                    mine = ctx.SignatureVerdict(obj);
                }
                else if (verdict.Cat == PdfChangeCategory.FormFieldValue && obj is PdfDictionary && ctx.Annotation(obj) is { } annot)
                {
                    mine = annot;
                }

                if (assigned.TryGetValue(target, out var existing))
                {
                    if (Severity(existing.Cat) >= Severity(mine.Cat) && (!existing.Allowed || mine.Allowed))
                    {
                        continue;
                    }
                }

                assigned[target] = mine;
                work.Enqueue((target, obj, mine));
            }
        }

        foreach ((int n, PdfObject o) in fresh)
        {
            if (IsStructural(o))
            {
                continue;
            }

            if (assigned.TryGetValue(n, out var a))
            {
                changes.Add(new PdfSignatureChange(n, a.Cat, a.Allowed, "new object: " + a.Why));
            }
            else if (IsSignatureDictionary(o))
            {
                (PdfChangeCategory cat, bool allowed, string why) = ctx.SignatureVerdict(o);
                changes.Add(new PdfSignatureChange(n, cat, allowed, "new object: " + why));
            }
            else
            {
                changes.Add(new PdfSignatureChange(n, PdfChangeCategory.Other, true, "new object that nothing refers to"));
            }
        }

    }

    private static bool IsStructural(PdfObject o) =>
        o is PdfStream s && s.Dictionary.GetName("Type") is "XRef" or "ObjStm";

    private static bool IsSignatureDictionary(PdfObject o) =>
        o is PdfDictionary d && (d.GetName("Type") is "Sig" or "DocTimeStamp" || (d.ContainsKey("ByteRange") && d.ContainsKey("Contents")));

    private static IEnumerable<int> References(PdfObject root)
    {
        var stack = new Stack<PdfObject>();
        stack.Push(root is PdfStream st ? st.Dictionary : root);
        int budget = 1 << 16;
        while (stack.Count > 0 && budget-- > 0)
        {
            PdfObject o = stack.Pop();
            switch (o)
            {
                case PdfReference r:
                    yield return r.Number;
                    break;
                case PdfDictionary d:
                    foreach (KeyValuePair<string, PdfObject> kv in d.Entries)
                    {
                        if (kv.Key is not ("Parent" or "P"))
                        {
                            stack.Push(kv.Value);
                        }
                    }

                    break;
                case PdfArray a:
                    for (int i = 0; i < a.Count; i++)
                    {
                        stack.Push(a.GetRaw(i));
                    }

                    break;
            }
        }
    }

    // ---- Structural equality --------------------------------------------------------------------------------------------

    internal static bool DeepEqual(PdfObject a, PdfObject b)
    {
        var stack = new Stack<(PdfObject A, PdfObject B)>();
        stack.Push((a, b));
        int nodes = 0;
        while (stack.Count > 0)
        {
            (PdfObject x, PdfObject y) = stack.Pop();
            if (++nodes > MaxCompareNodes)
            {
                return false;
            }

            if (ReferenceEquals(x, y))
            {
                continue;
            }

            switch (x)
            {
                case PdfReference rx when y is PdfReference ry:
                    if (rx.Number != ry.Number || rx.Generation != ry.Generation)
                    {
                        return false;
                    }

                    break;
                case PdfInteger ix when y is PdfInteger iy:
                    if (ix.Value != iy.Value)
                    {
                        return false;
                    }

                    break;
                case PdfReal fx when y is PdfReal fy:
                    if (fx.Value != fy.Value)
                    {
                        return false;
                    }

                    break;
                case PdfBoolean bx when y is PdfBoolean by:
                    if (bx.Value != by.Value)
                    {
                        return false;
                    }

                    break;
                case PdfName nx when y is PdfName ny:
                    if (nx.Value != ny.Value)
                    {
                        return false;
                    }

                    break;
                case PdfString sx when y is PdfString sy:
                    if (!sx.Bytes.Span.SequenceEqual(sy.Bytes.Span))
                    {
                        return false;
                    }

                    break;
                case PdfNull when y is PdfNull:
                    break;
                case PdfArray ax when y is PdfArray ay:
                    if (ax.Count != ay.Count)
                    {
                        return false;
                    }

                    for (int i = 0; i < ax.Count; i++)
                    {
                        stack.Push((ax.GetRaw(i), ay.GetRaw(i)));
                    }

                    break;
                case PdfDictionary dx when y is PdfDictionary dy:
                    if (!PushDictionary(dx, dy, stack))
                    {
                        return false;
                    }

                    break;
                case PdfStream tx when y is PdfStream ty:
                    if (!PushDictionary(tx.Dictionary, ty.Dictionary, stack))
                    {
                        return false;
                    }

                    try
                    {
                        if (!tx.GetRawBytes().Span.SequenceEqual(ty.GetRawBytes().Span))
                        {
                            return false;
                        }
                    }
                    catch (PdfLimitException)
                    {
                        return false;
                    }

                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private static bool PushDictionary(PdfDictionary a, PdfDictionary b, Stack<(PdfObject, PdfObject)> stack)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, PdfObject> kv in a.Entries)
        {
            if (b.GetRaw(kv.Key) is not { } other)
            {
                return false;
            }

            stack.Push((kv.Value, other));
        }

        return true;
    }

    private static List<string> DiffKeys(PdfDictionary a, PdfDictionary b)
    {
        var keys = new List<string>();
        foreach (KeyValuePair<string, PdfObject> kv in a.Entries)
        {
            if (b.GetRaw(kv.Key) is not { } other || !DeepEqual(kv.Value, other))
            {
                keys.Add(kv.Key);
            }
        }

        foreach (string k in b.Keys)
        {
            if (!a.ContainsKey(k))
            {
                keys.Add(k);
            }
        }

        return keys;
    }

    // ---- Classification -------------------------------------------------------------------------------------------------

    private sealed class Context
    {
        private readonly PdfDocument _doc;
        private readonly PdfRevision _signed;
        private readonly PdfRevision _newest;
        private readonly int _level;
        private readonly List<PdfSignatureLock> _locks;
        private HashSet<int>? _pageOwned;
        private HashSet<int>? _annotArrays;

        public Context(PdfDocument doc, PdfRevision signed, PdfRevision newest, int level, List<PdfSignatureLock> locks)
        {
            _doc = doc;
            _signed = signed;
            _newest = newest;
            _level = level;
            _locks = locks;
        }

        public Verdict Deleted(int n, PdfObject before)
        {
            PdfDictionary? d = Dict(before);
            if (d is not null && (d.GetName("Type") == "Page" || d.GetName("Type") == "Pages"))
            {
                return new Verdict(PdfChangeCategory.PageContent, false, "a page or page-tree node was deleted");
            }

            if (d is not null && d.ContainsKey("Subtype") && d.ContainsKey("Rect"))
            {
                return new Verdict(PdfChangeCategory.Annotation, _level >= 3, "an annotation was deleted");
            }

            return new Verdict(PdfChangeCategory.Other, false, "an object was deleted");
        }

        public Verdict Classify(int n, PdfObject before, PdfObject after)
        {
            PdfDictionary? b = Dict(before);
            PdfDictionary? a = Dict(after);
            if (b is null || a is null)
            {
                return ClassifyNonDictionary(n, before, after);
            }

            if (IsSignatureDictionary(b))
            {
                return new Verdict(PdfChangeCategory.Other, false, "a signature dictionary was changed");
            }

            if (n == InfoNumber())
            {
                return new Verdict(PdfChangeCategory.Other, true, "document information changed");
            }

            List<string> keys = DiffKeys(b, a);
            string type = a.GetName("Type") ?? b.GetName("Type") ?? string.Empty;
            if (type == "Catalog" || n == CatalogNumber())
            {
                return ClassifyCatalog(b, a, keys);
            }

            if (n == AcroFormNumber() && keys.TrueForAll(AcroFormKeys.Contains))
            {
                return Gate(PdfChangeCategory.FormFieldValue, 2, "the form's field list or flags changed", null);
            }

            if (type == "Page")
            {
                return ClassifyPage(b, a, keys);
            }

            if (type == "Pages")
            {
                return new Verdict(PdfChangeCategory.PageContent, false, "the page tree changed");
            }

            if (IsFieldOrWidget(a))
            {
                string? name = FieldName(a);
                bool isSig = FieldType(a) == "Sig";
                if (keys.TrueForAll(FillKeys.Contains))
                {
                    bool newSig = isSig && b.Get("V") is null && a.Get("V") is PdfDictionary;
                    return Gate(newSig ? PdfChangeCategory.NewSignature : PdfChangeCategory.FormFieldValue, 2, newSig ? "signature field signed" : "form field value or appearance changed", name);
                }

                if (keys.TrueForAll(static k => FillKeys.Contains(k) || AnnotKeys.Contains(k)))
                {
                    return Gate(PdfChangeCategory.Annotation, 3, "widget annotation changed", name);
                }

                return new Verdict(PdfChangeCategory.Other, false, "form field definition changed (" + string.Join(", ", keys) + ")");
            }

            if (a.ContainsKey("Subtype") && (a.ContainsKey("Rect") || b.ContainsKey("Rect")))
            {
                return Gate(PdfChangeCategory.Annotation, 3, "annotation changed", null);
            }

            if (IsPageOwned(n))
            {
                return new Verdict(PdfChangeCategory.PageContent, false, "page content or resources changed");
            }

            return new Verdict(PdfChangeCategory.Other, false, "object changed (" + string.Join(", ", keys) + ")");
        }

        private Verdict ClassifyNonDictionary(int n, PdfObject before, PdfObject after)
        {
            if (before is PdfArray && after is PdfArray && IsAnnotArray(n))
            {
                return Gate(PdfChangeCategory.Annotation, 3, "the page's annotation list changed", null);
            }

            if (IsPageOwned(n))
            {
                return new Verdict(PdfChangeCategory.PageContent, false, "page content or resources changed");
            }

            return new Verdict(PdfChangeCategory.Other, false, "object changed");
        }

        private Verdict ClassifyCatalog(PdfDictionary b, PdfDictionary a, List<string> keys)
        {
            PdfChangeCategory cat = PdfChangeCategory.ValidationData;
            bool allowed = true;
            var why = new List<string>();
            foreach (string k in keys)
            {
                switch (k)
                {
                    case "DSS":
                        why.Add("validation data");
                        break;
                    case "Metadata":
                        why.Add("metadata");
                        if (Severity(PdfChangeCategory.Other) > Severity(cat))
                        {
                            cat = PdfChangeCategory.Other;
                        }

                        break;
                    case "AcroForm":
                        {
                            PdfObject? ar = b.GetRaw("AcroForm");
                            PdfObject? nr = a.GetRaw("AcroForm");
                            bool ok = ar is null || (ar is PdfDictionary x && nr is PdfDictionary y && DiffKeys(x, y).TrueForAll(AcroFormKeys.Contains)) || (ar is PdfReference rx && nr is PdfReference ry && rx.Number == ry.Number);
                            if (nr is PdfDictionary nd && ar is null && !nd.Keys.All(AcroFormKeys.Contains))
                            {
                                ok = false;
                            }

                            why.Add("form definition");
                            if (Severity(PdfChangeCategory.FormFieldValue) > Severity(cat))
                            {
                                cat = PdfChangeCategory.FormFieldValue;
                            }

                            allowed &= ok && _level >= 2;
                            break;
                        }

                    default:
                        why.Add("/" + k);
                        cat = PdfChangeCategory.Other;
                        allowed = false;
                        break;
                }
            }

            return new Verdict(cat, allowed, "catalog changed: " + string.Join(", ", why));
        }

        private Verdict ClassifyPage(PdfDictionary b, PdfDictionary a, List<string> keys)
        {
            if (keys.Count == 0 || !keys.TrueForAll(static k => k == "Annots"))
            {
                return new Verdict(PdfChangeCategory.PageContent, false, "page dictionary changed (" + string.Join(", ", keys.Where(static k => k != "Annots")) + ")");
            }

            // Which annotations came or went, and are the new ones form widgets?
            var oldRefs = new HashSet<int>();
            if (b.GetArray("Annots") is { } oldArr)
            {
                for (int i = 0; i < Math.Min(oldArr.Count, 1 << 16); i++)
                {
                    if (oldArr.GetRaw(i) is PdfReference r)
                    {
                        oldRefs.Add(r.Number);
                    }
                }
            }

            bool removed = false;
            bool onlyWidgets = true;
            bool anyAdded = false;
            var newRefs = new HashSet<int>();
            if (a.GetArray("Annots") is { } newArr)
            {
                for (int i = 0; i < Math.Min(newArr.Count, 1 << 16); i++)
                {
                    if (newArr.GetRaw(i) is PdfReference r)
                    {
                        newRefs.Add(r.Number);
                        if (!oldRefs.Contains(r.Number))
                        {
                            anyAdded = true;
                            onlyWidgets &= newArr[i] is PdfDictionary d && d.GetName("Subtype") == "Widget";
                        }
                    }
                    else
                    {
                        onlyWidgets = false;
                    }
                }
            }

            removed = oldRefs.Any(o => !newRefs.Contains(o));
            if (anyAdded && onlyWidgets && !removed)
            {
                return Gate(PdfChangeCategory.FormFieldValue, 2, "form widgets were added to the page", null);
            }

            return Gate(PdfChangeCategory.Annotation, 3, removed ? "annotations were removed from the page" : "annotations were added to the page", null);
        }

        // An annotation that is a new object, judged as the annotation it is.
        public (PdfChangeCategory Cat, bool Allowed, string Why)? Annotation(PdfObject o)
        {
            if (o is PdfDictionary d && d.ContainsKey("Subtype") && (d.GetName("Type") == "Annot" || d.ContainsKey("Rect")))
            {
                if (d.GetName("Subtype") == "Widget")
                {
                    Verdict w = Gate(PdfChangeCategory.FormFieldValue, 2, "form widget", FieldName(d));
                    return (w.Category, w.Allowed, w.Description);
                }

                Verdict v = Gate(PdfChangeCategory.Annotation, 3, "annotation", null);
                return (v.Category, v.Allowed, v.Description);
            }

            return null;
        }

        public (PdfChangeCategory Cat, bool Allowed, string Why) SignatureVerdict(PdfObject o)
        {
            bool timestamp = o is PdfDictionary d && d.GetName("Type") == "DocTimeStamp";
            if (timestamp)
            {
                return (PdfChangeCategory.NewSignature, true, "document time-stamp");
            }

            return (PdfChangeCategory.NewSignature, _level >= 2, _level >= 2 ? "signature added" : "signature added, which the permission level forbids");
        }

        private Verdict Gate(PdfChangeCategory category, int minLevel, string description, string? field)
        {
            bool allowed = _level >= minLevel;
            if (!allowed)
            {
                description += $" (not allowed at permission level {_level})";
            }

            if (field is not null && IsLocked(field))
            {
                allowed = false;
                description += $" (field {field} is locked)";
            }

            return new Verdict(category, allowed, description);
        }

        private bool IsLocked(string field)
        {
            foreach (PdfSignatureLock l in _locks)
            {
                bool listed = l.Fields.Contains(field);
                if (l.Action == PdfLockAction.All || (l.Action == PdfLockAction.Include && listed) || (l.Action == PdfLockAction.Exclude && !listed))
                {
                    return true;
                }
            }

            return false;
        }

        private static PdfDictionary? Dict(PdfObject o) => o as PdfDictionary ?? (o as PdfStream)?.Dictionary;

        private int CatalogNumber() => _newest.Trailer.GetRaw("Root") is PdfReference r ? r.Number : -1;

        private int InfoNumber() => _newest.Trailer.GetRaw("Info") is PdfReference r ? r.Number : -1;

        private int AcroFormNumber() => _newest.Catalog?.GetRaw("AcroForm") is PdfReference r ? r.Number : -1;

        private static bool IsFieldOrWidget(PdfDictionary d) =>
            d.ContainsKey("FT") || d.ContainsKey("T") || (d.GetName("Subtype") == "Widget");

        private static string? FieldType(PdfDictionary d)
        {
            PdfDictionary? cur = d;
            for (int i = 0; i < 32 && cur is not null; i++)
            {
                if (cur.GetName("FT") is { } ft)
                {
                    return ft;
                }

                cur = cur.GetDictionary("Parent");
            }

            return null;
        }

        private static string? FieldName(PdfDictionary d)
        {
            var parts = new List<string>();
            PdfDictionary? cur = d;
            for (int i = 0; i < 32 && cur is not null; i++)
            {
                if (cur.GetString("T") is { } t)
                {
                    parts.Add(t.GetText());
                }

                cur = cur.GetDictionary("Parent");
            }

            if (parts.Count == 0)
            {
                return null;
            }

            parts.Reverse();
            return string.Join('.', parts);
        }

        private bool IsAnnotArray(int n)
        {
            EnsureOwnership();
            return _annotArrays!.Contains(n);
        }

        private bool IsPageOwned(int n)
        {
            EnsureOwnership();
            return _pageOwned!.Contains(n);
        }

        // Objects reachable from the signed revision's pages (content, resources, fonts, images), without the tree around them.
        private void EnsureOwnership()
        {
            if (_pageOwned is not null)
            {
                return;
            }

            _pageOwned = [];
            _annotArrays = [];
            var stack = new Stack<PdfObject>();
            foreach (PdfPageNode page in _doc.WalkPages(_signed.Catalog, "signed revision"))
            {
                if (page.Dictionary.Reference is { } pr)
                {
                    _pageOwned.Add(pr.Number);
                }

                if (page.Dictionary.GetRaw("Annots") is PdfReference ar)
                {
                    _annotArrays.Add(ar.Number);
                }

                foreach (KeyValuePair<string, PdfObject> kv in page.Dictionary.Entries)
                {
                    if (kv.Key is not ("Parent" or "Annots" or "Thumb" or "B" or "StructParents" or "Dummy"))
                    {
                        stack.Push(kv.Value);
                    }
                }

                if (page.Resources is { } res)
                {
                    stack.Push(res);
                }
            }

            int budget = 1 << 18;
            while (stack.Count > 0 && budget-- > 0)
            {
                PdfObject o = stack.Pop();
                switch (o)
                {
                    case PdfReference r:
                        if (_pageOwned.Add(r.Number) && _signed.GetObject(r.Number) is { } target)
                        {
                            stack.Push(target);
                        }

                        break;
                    case PdfDictionary d:
                        foreach (KeyValuePair<string, PdfObject> kv in d.Entries)
                        {
                            if (kv.Key is not ("Parent" or "P"))
                            {
                                stack.Push(kv.Value);
                            }
                        }

                        break;
                    case PdfStream s:
                        stack.Push(s.Dictionary);
                        break;
                    case PdfArray a:
                        for (int i = 0; i < a.Count; i++)
                        {
                            stack.Push(a.GetRaw(i));
                        }

                        break;
                }
            }
        }
    }
}
