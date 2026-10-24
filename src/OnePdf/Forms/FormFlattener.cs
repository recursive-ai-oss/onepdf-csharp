using System.Globalization;
using System.Linq;
using System.Text;
using OnePdf.Appearance;
using OnePdf.Content;

namespace OnePdf.Forms;

/// <summary>Bakes annotation appearances into page content (ISO 32000-2 12.5.5) and removes the annotations and fields.</summary>
internal static class FormFlattener
{
    private readonly record struct Mat(double A, double B, double C, double D, double E, double F)
    {
        public static Mat Identity => new(1, 0, 0, 1, 0, 0);

        // Row-vector convention of ISO 32000-2 8.3.4: this matrix first, then <paramref name="m"/>.
        public Mat Then(Mat m) => new(
            (A * m.A) + (B * m.C), (A * m.B) + (B * m.D),
            (C * m.A) + (D * m.C), (C * m.B) + (D * m.D),
            (E * m.A) + (F * m.C) + m.E, (E * m.B) + (F * m.D) + m.F);

        public (double X, double Y) Apply(double x, double y) => ((x * A) + (y * C) + E, (x * B) + (y * D) + F);

        public bool IsFinite => double.IsFinite(A) && double.IsFinite(B) && double.IsFinite(C) && double.IsFinite(D) && double.IsFinite(E) && double.IsFinite(F);
    }

    private sealed class PageEdit
    {
        public readonly StringBuilder Content = new();
        public readonly List<(string Name, PdfReference Form)> Forms = [];
    }

    public static PdfFlattenResult Run(PdfDocument doc, PdfFlattenOptions o, CancellationToken ct)
    {
        doc.EnsureEditable(0);
        if (o.Visibility == PdfAnnotationMode.None)
        {
            throw new ArgumentException("PdfAnnotationMode.None is not a flatten mode.", nameof(o));
        }

        var result = new PdfFlattenResult();
        doc.ResetEditViews();
        PdfAcroForm? form = doc.Form;
        var selectedFields = new HashSet<PdfFormField>(ReferenceEqualityComparer.Instance);
        var selectedWidgets = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var signedWidgets = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        bool allWidgets = o.Fields && o.FieldNames is null;
        if (form is not null && o.Fields)
        {
            var roots = new List<PdfFormField>();
            if (o.FieldNames is null)
            {
                roots.AddRange(form.Fields);
            }
            else
            {
                foreach (string name in o.FieldNames)
                {
                    roots.Add(form.FindField(name) ?? throw new PdfFormValueException(PdfFormValueError.UnknownField, name, "The form has no field named '" + name + "'."));
                }
            }

            var stack = new Stack<PdfFormField>(roots);
            var seen = new HashSet<PdfFormField>(ReferenceEqualityComparer.Instance);
            while (stack.Count > 0)
            {
                PdfFormField f = stack.Pop();
                if (!seen.Add(f))
                {
                    continue;
                }

                foreach (PdfFormField c in f.Children)
                {
                    stack.Push(c);
                }

                if (f.Children.Count > 0)
                {
                    continue;
                }

                if (f.IsSigned)
                {
                    // Removing a signed field would delete the signature; its widget stays as it is.
                    result.SignedKept++;
                    foreach (PdfDictionary w in f.Widgets)
                    {
                        signedWidgets.Add(w);
                    }

                    continue;
                }

                selectedFields.Add(f);
                foreach (PdfDictionary w in f.Widgets)
                {
                    selectedWidgets.Add(w);
                }
            }

            if (o.FieldNames is null)
            {
                foreach (PdfFormField f in form.AllFieldList)
                {
                    if (f.Children.Count == 0 && f.IsSigned)
                    {
                        foreach (PdfDictionary w in f.Widgets)
                        {
                            signedWidgets.Add(w);
                        }
                    }
                }
            }
        }

        FormSession session = form?.Session ?? new FormSession();
        OptionalContentState? oc = null;
        bool? needAppearances = null;
        PdfReference? qRef = null, bigQRef = null;
        var removed = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        bool tagged = doc.StructureTreeRoot is not null;
        PdfPageCollection pages = doc.Pages;
        for (int pi = 0; pi < pages.Count; pi++)
        {
            ct.ThrowIfCancellationRequested();
            PdfPage page = pages[pi];
            IReadOnlyList<PdfAnnotation> annots = page.Annotations;
            if (annots.Count == 0)
            {
                continue;
            }

            PageEdit? edit = null;
            int before = removed.Count;
            foreach (PdfAnnotation a in annots)
            {
                ct.ThrowIfCancellationRequested();
                string subtype = a.Subtype;
                bool flatten = subtype switch
                {
                    "Link" or "Popup" => false,
                    "Widget" => allWidgets ? !signedWidgets.Contains(a.Dictionary) : selectedWidgets.Contains(a.Dictionary),
                    _ => o.IncludeMarkup,
                };
                if (!flatten)
                {
                    continue;
                }

                removed.Add(a.Dictionary);
                PdfAnnotationOptions flags = a.Flags;
                bool hidden = (flags & PdfAnnotationOptions.Hidden) != 0;
                bool visible = o.Visibility == PdfAnnotationMode.Print ? !hidden && (flags & PdfAnnotationOptions.Print) != 0 : !hidden && (flags & PdfAnnotationOptions.NoView) == 0;
                if (visible && a.Dictionary.Get("OC") is { } ocEntry)
                {
                    oc ??= new OptionalContentState(doc, null);
                    visible = oc.IsVisible(ocEntry);
                }

                if (!visible)
                {
                    continue;
                }

                PdfStream? ap = a.GetAppearanceStream();
                PdfGeneratedAppearance? generated = null;
                if (o.GenerateAppearances && (ap is null || (subtype == "Widget" && (needAppearances ??= AppearanceGenerator.FormNeedsAppearances(doc))
                    && WidgetAppearance.RegeneratesUnderNeedAppearances(doc, a.Dictionary, true))))
                {
                    generated = a.GenerateAppearance(ct);
                }

                if (ap is null && generated is null)
                {
                    continue;
                }

                if (!TryPlace(page, a, flags, generated, ap, out Mat ctm))
                {
                    continue;
                }

                PdfReference formRef;
                if (generated is not null)
                {
                    formRef = FormFiller.StoreForm(doc, generated, session);
                }
                else
                {
                    if (ap!.Reference is not { } existing)
                    {
                        continue;
                    }

                    // An appearance stream is a form XObject whether or not it says so (12.5.5); Do wants the subtype.
                    if (ap.Dictionary.GetName("Subtype") != "Form")
                    {
                        ap.Dictionary.SetEntry("Subtype", new PdfName("Form"));
                    }

                    formRef = existing;
                }

                edit ??= new PageEdit();
                string name = "OnePdfFlat" + edit.Forms.Count.ToString(CultureInfo.InvariantCulture);
                edit.Forms.Add((name, formRef));
                // In a tagged document the baked-in art is not content of the structure tree: mark it as an artifact (ISO 32000-2 14.8.2.2).
                if (tagged)
                {
                    edit.Content.Append("/Artifact BMC ");
                }

                edit.Content.Append("q ")
                    .Append(Num(ctm.A)).Append(' ').Append(Num(ctm.B)).Append(' ').Append(Num(ctm.C)).Append(' ')
                    .Append(Num(ctm.D)).Append(' ').Append(Num(ctm.E)).Append(' ').Append(Num(ctm.F))
                    .Append(" cm /").Append(name).Append(" Do Q").Append(tagged ? " EMC\n" : "\n");
                result.Drawn++;
            }

            // A pop-up goes with its parent.
            foreach (PdfAnnotation a in annots)
            {
                if (a.Subtype == "Popup" && a.Dictionary.GetDictionary("Parent") is { } parent && removed.Contains(parent))
                {
                    removed.Add(a.Dictionary);
                }
            }

            if (edit is not null)
            {
                qRef ??= doc.AddObject(doc.CreateStream("q\n"u8, false));
                bigQRef ??= doc.AddObject(doc.CreateStream("\nQ\n"u8, false));
                ApplyContent(doc, page, edit, qRef, bigQRef);
            }

            if (removed.Count != before)
            {
                RemoveAnnots(page.Dictionary, removed);
            }
        }

        result.Removed = removed.Count;
        if (removed.Count > 0 && doc.StructureTreeRoot is { } structRoot)
        {
            DropFromStructureTree(doc, structRoot, removed);
        }

        if (form is not null)
        {
            RemoveFields(doc, form, selectedFields, result);
        }

        doc.ResetEditViews();
        return result;
    }

    // ---- placement ----

    // ISO 32000-2 12.5.5: the transformed appearance box is fitted onto the annotation rectangle. NoRotate (Table 167) keeps the
    // appearance upright on a rotated page, pinned at the upper left corner of the rectangle; NoZoom needs no change here because
    // the flattened page has no zoom of its own.
    private static bool TryPlace(PdfPage page, PdfAnnotation a, PdfAnnotationOptions flags, PdfGeneratedAppearance? generated, PdfStream? ap, out Mat ctm)
    {
        ctm = Mat.Identity;
        PdfDictionary apDict = generated?.Dictionary ?? ap!.Dictionary;
        PdfRectangle r = generated?.Rect ?? a.Rect;
        if (r.IsEmpty && a.Dictionary.Get("Rect") is not PdfArray)
        {
            return false;
        }

        Mat fm = Mat.Identity;
        if (apDict.GetArray("Matrix") is { Count: >= 6 } ma)
        {
            fm = new Mat(ma.GetDouble(0), ma.GetDouble(1), ma.GetDouble(2), ma.GetDouble(3), ma.GetDouble(4), ma.GetDouble(5));
        }

        double bl = 0, bb = 0, br = 0, bt = 0;
        if (apDict.GetArray("BBox") is { Count: >= 4 } ba)
        {
            double x0 = ba.GetDouble(0), y0 = ba.GetDouble(1), x1 = ba.GetDouble(2), y1 = ba.GetDouble(3);
            bl = Math.Min(x0, x1);
            br = Math.Max(x0, x1);
            bb = Math.Min(y0, y1);
            bt = Math.Max(y0, y1);
        }

        (double X, double Y)[] corners = [fm.Apply(bl, bb), fm.Apply(br, bb), fm.Apply(bl, bt), fm.Apply(br, bt)];
        double tl = corners.Min(static c => c.X), tr = corners.Max(static c => c.X);
        double tbm = corners.Min(static c => c.Y), tt = corners.Max(static c => c.Y);
        double sx = tr - tl > 0 ? r.Width / (tr - tl) : 1;
        double sy = tt - tbm > 0 ? r.Height / (tt - tbm) : 1;
        var fit = new Mat(sx, 0, 0, sy, r.Left - (tl * sx), r.Bottom - (tbm * sy));
        if (!fit.IsFinite)
        {
            return false;
        }

        ctm = fit;
        if ((flags & PdfAnnotationOptions.NoRotate) != 0 && page.Rotate != 0)
        {
            // Undo the page rotation (clockwise by /Rotate) about the rectangle's upper left corner.
            Mat undo = page.Rotate switch
            {
                90 => new Mat(0, 1, -1, 0, 0, 0),
                180 => new Mat(-1, 0, 0, -1, 0, 0),
                270 => new Mat(0, -1, 1, 0, 0, 0),
                _ => Mat.Identity,
            };
            double ax = r.Left, ay = r.Top;
            ctm = fit.Then(new Mat(1, 0, 0, 1, -ax, -ay)).Then(undo).Then(new Mat(1, 0, 0, 1, ax, ay));
        }

        return ctm.IsFinite;
    }

    private static string Num(double v)
    {
        string s = v.ToString("0.###############", CultureInfo.InvariantCulture);
        return s is "-0" ? "0" : s;
    }

    // ---- page edits ----

    private static void ApplyContent(PdfDocument doc, PdfPage page, PageEdit edit, PdfReference q, PdfReference bigQ)
    {
        PdfDictionary pd = page.Dictionary;

        // Resources: the page's own direct dictionary is changed in place; shared, indirect or inherited ones are copied first so
        // other pages are not touched.
        PdfDictionary res;
        if (pd.GetRaw("Resources") is PdfDictionary own && own.Reference is null)
        {
            res = own;
        }
        else
        {
            var copy = new PdfDictionary();
            foreach (string k in page.Resources.Keys.ToArray())
            {
                if (page.Resources.GetRaw(k) is { } raw)
                {
                    copy.SetEntry(k, FormFiller.Clone(doc, raw));
                }
            }

            pd.SetEntry("Resources", copy);
            res = pd.GetDictionary("Resources")!;
        }

        PdfDictionary xo;
        if (res.GetRaw("XObject") is PdfDictionary xd && xd.Reference is null)
        {
            xo = xd;
        }
        else
        {
            var copy = new PdfDictionary();
            if (res.GetDictionary("XObject") is { } oldX)
            {
                foreach (string k in oldX.Keys.ToArray())
                {
                    if (oldX.GetRaw(k) is { } raw)
                    {
                        copy.SetEntry(k, FormFiller.Clone(doc, raw));
                    }
                }
            }

            res.SetEntry("XObject", copy);
            xo = res.GetDictionary("XObject")!;
        }

        var contents = new PdfArray();
        contents.Append(q);
        PdfObject? c = pd.GetRaw("Contents");
        if (pd.Get("Contents") is PdfArray existing)
        {
            for (int i = 0; i < existing.Count; i++)
            {
                if (existing.GetRaw(i) is { } item)
                {
                    contents.Append(item is PdfStream { Reference: { } sr } ? sr : item);
                }
            }
        }
        else if (pd.Get("Contents") is PdfStream)
        {
            contents.Append(c is PdfStream { Reference: { } cr } ? cr : c);
        }

        contents.Append(bigQ);

        var content = new StringBuilder();
        for (int i = 0; i < edit.Forms.Count; i++)
        {
            string name = edit.Forms[i].Name;
            string unique = name;
            for (int n = 1; xo.ContainsKey(unique); n++)
            {
                unique = name + "_" + n.ToString(CultureInfo.InvariantCulture);
            }

            xo.SetEntry(unique, edit.Forms[i].Form);
            if (unique != name)
            {
                edit.Content.Replace("/" + name + " Do", "/" + unique + " Do");
            }
        }

        content.Append(edit.Content);
        PdfStream s = doc.CreateStream(Encoding.ASCII.GetBytes(content.ToString()), compress: false);
        contents.Append(doc.AddObject(s));
        pd.SetEntry("Contents", contents);
    }

    private static void RemoveAnnots(PdfDictionary pageDict, HashSet<PdfDictionary> removed)
    {
        if (pageDict.Get("Annots") is not PdfArray arr)
        {
            return;
        }

        var keep = new List<PdfObject>(arr.Count);
        bool changed = false;
        for (int i = 0; i < arr.Count; i++)
        {
            PdfObject raw = arr.GetRaw(i);
            if (arr[i] is PdfDictionary d && removed.Contains(d))
            {
                changed = true;
            }
            else
            {
                keep.Add(raw);
            }
        }

        if (!changed)
        {
            return;
        }

        if (keep.Count == 0)
        {
            pageDict.RemoveEntry("Annots");
            return;
        }

        arr.Clear();
        foreach (PdfObject k in keep)
        {
            arr.Append(k);
        }
    }

    // ---- structure tree ----

    // A removed annotation must not stay reachable from the structure tree (ISO 32000-2 14.7.5.3): its object reference (OBJR) in the
    // element that held it goes, and so does its parent tree entry (14.7.4.4, keyed by the annotation's /StructParent). Elements are
    // visited once each with an explicit stack; an element left without content simply has no /K.
    private static void DropFromStructureTree(PdfDocument doc, PdfDictionary root, HashSet<PdfDictionary> removed)
    {
        var gone = new HashSet<PdfDictionary>(removed, ReferenceEqualityComparer.Instance);
        var keys = new HashSet<long>();
        foreach (PdfDictionary a in removed)
        {
            if (a.Get("StructParent") is PdfInteger k)
            {
                keys.Add(k.Value);
            }
        }

        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var emptied = new Queue<PdfDictionary>();
        var stack = new Stack<PdfDictionary>();
        stack.Push(root);
        long budget = doc.Limits.MaxObjects;
        while (stack.Count > 0 && budget-- > 0)
        {
            PdfDictionary el = stack.Pop();
            if (!visited.Add(el))
            {
                continue;
            }

            switch (el.Get("K"))
            {
                case PdfDictionary single:
                    if (IsGoneObjr(single, gone))
                    {
                        el.RemoveEntry("K");
                        emptied.Enqueue(el);
                    }
                    else if (single.GetName("Type") is not ("OBJR" or "MCR"))
                    {
                        stack.Push(single);
                    }

                    break;
                case PdfArray kids:
                    var keep = new List<PdfObject>(kids.Count);
                    for (int i = 0; i < kids.Count; i++)
                    {
                        if (kids[i] is PdfDictionary kd)
                        {
                            if (IsGoneObjr(kd, gone))
                            {
                                continue;
                            }

                            if (kd.GetName("Type") is not ("OBJR" or "MCR"))
                            {
                                stack.Push(kd);
                            }
                        }

                        keep.Add(kids.GetRaw(i));
                    }

                    if (keep.Count != kids.Count)
                    {
                        kids.Clear();
                        foreach (PdfObject k in keep)
                        {
                            kids.Append(k);
                        }

                        if (keep.Count == 0)
                        {
                            el.RemoveEntry("K");
                            emptied.Enqueue(el);
                        }
                    }

                    break;
                default:
                    break;
            }
        }

        // An element that only held a removed field has no content left: it goes too (PDF/UA-1 7.18.4 wants a Form element to hold its
        // widget), and so does a parent it leaves empty.
        while (emptied.Count > 0 && budget-- > 0)
        {
            PdfDictionary el = emptied.Dequeue();
            if (el == root || el.GetDictionary("P") is not { } parent)
            {
                continue;
            }

            switch (parent.Get("K"))
            {
                case PdfDictionary only when ReferenceEquals(only, el):
                    parent.RemoveEntry("K");
                    emptied.Enqueue(parent);
                    break;
                case PdfArray siblings:
                    int at = -1;
                    for (int i = 0; i < siblings.Count; i++)
                    {
                        if (ReferenceEquals(siblings[i], el))
                        {
                            at = i;
                            break;
                        }
                    }

                    if (at >= 0)
                    {
                        siblings.RemoveAt(at);
                        if (siblings.Count == 0)
                        {
                            parent.RemoveEntry("K");
                            emptied.Enqueue(parent);
                        }
                    }

                    break;
                default:
                    break;
            }
        }

        if (keys.Count == 0 || root.GetDictionary("ParentTree") is not { } tree)
        {
            return;
        }

        var entries = new List<(long Key, PdfObject Value)>();
        bool dropped = false;
        foreach (KeyValuePair<PdfObject, PdfObject> kv in doc.ReadTree(tree, true, "parent tree"))
        {
            long key = ((PdfInteger)kv.Key).Value;
            if (keys.Contains(key))
            {
                dropped = true;
                continue;
            }

            entries.Add((key, kv.Value));
        }

        if (!dropped)
        {
            return;
        }

        entries.Sort(static (x, y) => x.Key.CompareTo(y.Key));
        var nums = new PdfArray();
        foreach ((long key, PdfObject value) in entries)
        {
            nums.Append(new PdfInteger(key));
            nums.Append(value);
        }

        var leaf = new PdfDictionary();
        leaf.SetEntry("Nums", nums);
        root.SetEntry("ParentTree", leaf);
    }

    private static bool IsGoneObjr(PdfDictionary d, HashSet<PdfDictionary> gone) =>
        d.GetName("Type") == "OBJR" && d.GetDictionary("Obj") is { } target && gone.Contains(target);

    // ---- fields ----

    private static void RemoveFields(PdfDocument doc, PdfAcroForm form, HashSet<PdfFormField> selected, PdfFlattenResult result)
    {
        result.FieldsRemoved = selected.Count;
        if (selected.Count == 0)
        {
            return;
        }

        var gone = new HashSet<PdfFormField>(selected, ReferenceEqualityComparer.Instance);
        IReadOnlyList<PdfFormField> all = form.AllFieldList;
        for (int i = all.Count - 1; i >= 0; i--)
        {
            PdfFormField f = all[i];
            if (f.Children.Count > 0 && f.Widgets.Count == 0 && f.Children.All(gone.Contains))
            {
                gone.Add(f);
            }
        }

        var perArray = new Dictionary<PdfArray, HashSet<PdfDictionary>>(ReferenceEqualityComparer.Instance);
        foreach (PdfFormField f in gone)
        {
            if (f.Parent is not null && gone.Contains(f.Parent))
            {
                continue;
            }

            PdfArray? host = f.Parent is null ? form.Dictionary.GetArray("Fields") : f.Parent.Dictionary.GetArray("Kids");
            if (host is null)
            {
                continue;
            }

            if (!perArray.TryGetValue(host, out HashSet<PdfDictionary>? set))
            {
                perArray[host] = set = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            }

            set.Add(f.Dictionary);
        }

        foreach ((PdfArray host, HashSet<PdfDictionary> drop) in perArray)
        {
            Filter(host, drop);
        }

        // The calculation order lists fields by reference; drop the ones that are gone.
        if (form.Dictionary.GetArray("CO") is { } co)
        {
            var dropCo = new HashSet<PdfDictionary>(gone.Select(static f => f.Dictionary), ReferenceEqualityComparer.Instance);
            Filter(co, dropCo);
        }

        PdfArray? fields = form.Dictionary.GetArray("Fields");
        bool empty = true;
        if (fields is not null)
        {
            for (int i = 0; i < fields.Count && empty; i++)
            {
                empty = fields[i] is not PdfDictionary;
            }
        }

        if (empty)
        {
            doc.Catalog.RemoveEntry("AcroForm");
            result.FormRemoved = true;
        }
    }

    private static void Filter(PdfArray arr, HashSet<PdfDictionary> drop)
    {
        var keep = new List<PdfObject>(arr.Count);
        for (int i = 0; i < arr.Count; i++)
        {
            if (arr[i] is PdfDictionary d && drop.Contains(d))
            {
                continue;
            }

            keep.Add(arr.GetRaw(i));
        }

        if (keep.Count == arr.Count)
        {
            return;
        }

        arr.Clear();
        foreach (PdfObject k in keep)
        {
            arr.Append(k);
        }
    }
}
