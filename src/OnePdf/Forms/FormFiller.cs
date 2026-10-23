using System.Globalization;
using System.Linq;
using System.Text;
using OnePdf.Appearance;

namespace OnePdf.Forms;

/// <summary>State kept per form while it is being filled: shared font objects and the bookkeeping behind <c>/NeedAppearances</c>.</summary>
internal sealed class FormSession
{
    public Dictionary<PdfDictionary, PdfReference> FontRefs { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>Widgets that still need an appearance; built the first time a field is filled while <c>/NeedAppearances</c> is set.</summary>
    public HashSet<PdfDictionary>? Lacking { get; set; }

    /// <summary>Widgets whose appearance was rebuilt by this session.</summary>
    public HashSet<PdfDictionary> Touched { get; } = new(ReferenceEqualityComparer.Instance);
}

/// <summary>Sets field values and stores the appearances (ISO 32000-2 12.7.4).</summary>
internal static class FormFiller
{
    private const long ReadOnlyFlag = 1, RadiosInUnison = 1 << 25, EditFlag = 1 << 18;

    private static PdfFormValueException Fail(PdfFormValueError reason, PdfFormField f, string message) =>
        new(reason, f.FullName, "Field '" + f.FullName + "': " + message);

    private static PdfAcroForm FormOf(PdfFormField f) => f.Doc.Form ?? throw new InvalidOperationException("The document has no form.");

    private static void Check(PdfFormField f, PdfFillOptions o, bool needsSupportedKind = true)
    {
        f.Doc.EnsureEditable(0);
        if (f.Children.Count > 0)
        {
            throw Fail(PdfFormValueError.NotTerminal, f, "it has child fields; set the values of the terminal fields.");
        }

        if (needsSupportedKind && f.Kind is PdfFieldKind.Unknown or PdfFieldKind.Signature or PdfFieldKind.PushButton)
        {
            throw Fail(PdfFormValueError.WrongType, f, "a " + f.Kind + " field does not hold a value.");
        }

        if (f.IsReadOnly && !o.OverrideReadOnly)
        {
            throw Fail(PdfFormValueError.ReadOnly, f, "it is read-only (set PdfFillOptions.OverrideReadOnly to fill it anyway).");
        }
    }

    // ---- values ----

    public static void SetValues(PdfFormField f, IReadOnlyList<string> values, PdfFillOptions o, bool lenient)
    {
        Check(f, o);
        PdfDocument doc = f.Doc;
        if (values.Count > doc.Limits.MaxObjects)
        {
            throw new PdfLimitException("Too many values for one field.");
        }

        switch (f.Kind)
        {
            case PdfFieldKind.Text:
                if (values.Count > 1)
                {
                    throw Fail(PdfFormValueError.NotMultiSelect, f, "a text field takes one value.");
                }

                SetText(f, values.Count == 0 ? null : values[0], o, lenient);
                break;
            case PdfFieldKind.ComboBox:
            case PdfFieldKind.ListBox:
                SetChoice(f, values, lenient);
                break;
            default:
                if (values.Count > 1)
                {
                    throw Fail(PdfFormValueError.NotMultiSelect, f, "a button field takes one value.");
                }

                SetButton(f, values.Count == 0 ? null : values[0], lenient);
                break;
        }

        Refresh(f, o);
    }

    private static void SetText(PdfFormField f, string? text, PdfFillOptions o, bool lenient)
    {
        if (text is not null)
        {
            if (text.Length > f.Doc.Limits.MaxMetadataBytes)
            {
                throw new PdfLimitException("The value is larger than the metadata limit (" + f.Doc.Limits.MaxMetadataBytes.ToString(CultureInfo.InvariantCulture) + ").");
            }

            // 12.7.4.3: /MaxLen counts characters; a value of 0 is read as "no limit" because no text could be stored at all.
            if (f.MaxLength is > 0 and int max)
            {
                int count = 0, cut = -1;
                foreach (Rune r in text.EnumerateRunes())
                {
                    if (count == max)
                    {
                        cut = 0;
                        break;
                    }

                    count++;
                }

                if (cut == 0)
                {
                    if (!o.TruncateToMaxLength && !lenient)
                    {
                        throw Fail(PdfFormValueError.TooLong, f, "the value is longer than /MaxLen " + max.ToString(CultureInfo.InvariantCulture) + ".");
                    }

                    text = Truncate(text, max);
                }
            }
        }

        PdfDictionary d = f.Dictionary;
        if (text is null)
        {
            d.RemoveEntry("V");
        }
        else
        {
            d.SetEntry("V", PdfString.FromText(text));
        }

        // A rich text value would now disagree with /V.
        d.RemoveEntry("RV");
    }

    private static string Truncate(string text, int maxRunes)
    {
        int count = 0, i = 0;
        foreach (Rune r in text.EnumerateRunes())
        {
            if (count == maxRunes)
            {
                break;
            }

            count++;
            i += r.Utf16SequenceLength;
        }

        return text[..i];
    }

    private static void SetChoice(PdfFormField f, IReadOnlyList<string> values, bool lenient)
    {
        bool multi = f.Kind == PdfFieldKind.ListBox && f.IsMultiSelect;
        if (values.Count > 1 && !multi)
        {
            throw Fail(PdfFormValueError.NotMultiSelect, f, "several values need a multi-select list box.");
        }

        IReadOnlyList<PdfFieldOption> opts = f.Options;
        bool editable = f.Kind == PdfFieldKind.ComboBox && (f.Flags & EditFlag) != 0;
        Dictionary<string, int>? byExport = null, byDisplay = null;
        if (values.Count > 8)
        {
            byExport = new(StringComparer.Ordinal);
            byDisplay = new(StringComparer.Ordinal);
            for (int i = 0; i < opts.Count; i++)
            {
                byExport.TryAdd(opts[i].Export, i);
                byDisplay.TryAdd(opts[i].Display, i);
            }
        }

        var picked = new SortedDictionary<int, string>();
        var free = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string v in values)
        {
            ArgumentNullException.ThrowIfNull(v);
            if (v.Length > f.Doc.Limits.MaxMetadataBytes)
            {
                throw new PdfLimitException("A value is larger than the metadata limit.");
            }

            if (!seen.Add(v))
            {
                continue;
            }

            int idx = -1;
            if (byExport is not null)
            {
                idx = byExport.GetValueOrDefault(v, -1);
                if (idx < 0)
                {
                    idx = byDisplay!.GetValueOrDefault(v, -1);
                }
            }
            else
            {
                for (int i = 0; i < opts.Count && idx < 0; i++)
                {
                    if (opts[i].Export == v)
                    {
                        idx = i;
                    }
                }

                for (int i = 0; i < opts.Count && idx < 0; i++)
                {
                    if (opts[i].Display == v)
                    {
                        idx = i;
                    }
                }
            }

            if (idx >= 0)
            {
                picked[idx] = opts[idx].Export;
            }
            else if (editable || lenient)
            {
                free.Add(v);
            }
            else
            {
                throw Fail(PdfFormValueError.NotInOptions, f, "'" + (v.Length > 64 ? v[..64] + "..." : v) + "' is not one of its " + opts.Count.ToString(CultureInfo.InvariantCulture) + " options.");
            }
        }

        var exports = new List<string>(picked.Values);
        exports.AddRange(free);
        PdfDictionary d = f.Dictionary;
        if (exports.Count == 0)
        {
            d.RemoveEntry("V");
            d.RemoveEntry("I");
            return;
        }

        if (multi)
        {
            var arr = new PdfArray();
            foreach (string e in exports)
            {
                arr.Append(PdfString.FromText(e));
            }

            d.SetEntry("V", arr);
        }
        else
        {
            d.SetEntry("V", PdfString.FromText(exports[0]));
        }

        // /I lists the selected indices in ascending order (Table 230); a combo box has none.
        if (f.Kind == PdfFieldKind.ListBox && free.Count == 0)
        {
            var idxs = new PdfArray();
            foreach (int i in picked.Keys)
            {
                idxs.Append(new PdfInteger(i));
            }

            d.SetEntry("I", idxs);
        }
        else
        {
            d.RemoveEntry("I");
        }
    }

    private static List<string> StatesOf(PdfDictionary widget)
    {
        var list = new List<string>();
        if (widget.GetDictionary("AP")?.Get("N") is PdfDictionary n)
        {
            foreach (string k in n.Keys)
            {
                if (k != "Off")
                {
                    list.Add(k);
                }
            }
        }

        return list;
    }

    private static void SetButton(PdfFormField f, string? token, bool lenient)
    {
        IReadOnlyList<PdfDictionary> widgets = f.Widgets;
        IReadOnlyList<PdfFieldOption> opts = f.Options;
        bool radio = f.Kind == PdfFieldKind.RadioButton;
        bool wide = widgets.Count > f.Doc.Limits.MaxObjects;
        if (wide)
        {
            throw new PdfLimitException("Too many widgets for one field.");
        }

        var states = new List<string>[widgets.Count];
        for (int i = 0; i < states.Length; i++)
        {
            states[i] = StatesOf(widgets[i]);
        }

        string target = "Off";
        if (!string.IsNullOrEmpty(token) && token != "Off")
        {
            string? found = null;
            for (int i = 0; i < states.Length && found is null; i++)
            {
                if (states[i].Contains(token))
                {
                    found = token;
                }
            }

            // 12.7.5.2.4: with /Opt the appearance states are index names, and the export value is the option text.
            for (int i = 0; i < states.Length && found is null; i++)
            {
                if (i < opts.Count && opts[i].Export == token && states[i].Count > 0)
                {
                    found = states[i][0];
                }
            }

            if (found is null)
            {
                bool anyStates = false;
                foreach (List<string> s in states)
                {
                    anyStates |= s.Count > 0;
                }

                if (!anyStates)
                {
                    // No appearance names the states, so the given name defines the on state.
                    found = token.Length <= 127 ? token : "Yes";
                }
                else if (lenient)
                {
                    found = "Off";
                }
                else
                {
                    throw Fail(PdfFormValueError.NotInOptions, f, "'" + (token.Length > 64 ? token[..64] + "..." : token) + "' is not an on state of its widgets.");
                }
            }

            target = found!;
        }

        PdfDictionary d = f.Dictionary;
        d.SetEntry("V", new PdfName(target));
        bool unison = !radio || (f.Flags & RadiosInUnison) != 0;
        bool assigned = false;
        for (int i = 0; i < widgets.Count; i++)
        {
            bool on = false;
            if (target != "Off" && (unison || !assigned))
            {
                on = states[i].Contains(target) || (states[i].Count == 0 && (i >= opts.Count || opts[i].Export == token || i.ToString(CultureInfo.InvariantCulture) == target));
            }

            assigned |= on;
            widgets[i].SetEntry("AS", new PdfName(on ? target : "Off"));
        }
    }

    public static void SetChecked(PdfFormField f, bool isChecked, PdfFillOptions o)
    {
        Check(f, o);
        if (f.Kind == PdfFieldKind.CheckBox)
        {
            string token = "Off";
            if (isChecked)
            {
                token = "Yes";
                foreach (PdfDictionary w in f.Widgets)
                {
                    if (StatesOf(w) is { Count: > 0 } s)
                    {
                        token = s[0];
                        break;
                    }
                }
            }

            SetValues(f, [token], o, lenient: false);
        }
        else if (f.Kind == PdfFieldKind.RadioButton && !isChecked)
        {
            SetValues(f, [], o, lenient: false);
        }
        else
        {
            throw Fail(PdfFormValueError.WrongType, f, f.Kind == PdfFieldKind.RadioButton ? "select a radio button by its state name with SetValue." : "SetChecked applies to check boxes.");
        }
    }

    public static void Reset(PdfFormField f, PdfFillOptions o)
    {
        Check(f, o);
        IReadOnlyList<string> dv = f.DefaultValues;
        if (f.Kind == PdfFieldKind.Text && dv.Count > 1)
        {
            dv = [dv[0]];
        }

        SetValues(f, dv, o, lenient: true);
    }

    public static int ResetAll(PdfAcroForm form, PdfFillOptions o, CancellationToken ct)
    {
        form.Doc.EnsureEditable(0);
        int n = 0;
        foreach (PdfFormField f in form.AllFieldList)
        {
            ct.ThrowIfCancellationRequested();
            if (f.Children.Count > 0 || f.Kind is PdfFieldKind.Unknown or PdfFieldKind.Signature or PdfFieldKind.PushButton || (f.IsReadOnly && !o.OverrideReadOnly))
            {
                continue;
            }

            Reset(f, o);
            n++;
        }

        return n;
    }

    // ---- appearances ----

    private static void Refresh(PdfFormField f, PdfFillOptions o)
    {
        if (!o.GenerateAppearances)
        {
            return;
        }

        PdfAcroForm form = FormOf(f);
        FormSession session = form.Session;
        foreach (PdfDictionary w in f.Widgets)
        {
            if (f.Kind is PdfFieldKind.CheckBox or PdfFieldKind.RadioButton)
            {
                EnsureButtonState(f.Doc, w, session);
            }
            else
            {
                StoreNormal(f.Doc, w, session);
            }

            session.Touched.Add(w);
        }

        if (!form.NeedAppearances)
        {
            return;
        }

        session.Lacking ??= ComputeLacking(form);
        foreach (PdfDictionary w in f.Widgets)
        {
            if (!Lacks(f, w))
            {
                session.Lacking.Remove(w);
            }
        }

        if (session.Lacking.Count == 0)
        {
            RebuildStale(form, session);
            form.Dictionary.RemoveEntry("NeedAppearances");
        }
    }

    // A widget needs an appearance when it has no normal one; a button that is off draws nothing and needs none.
    private static bool Lacks(PdfFormField f, PdfDictionary w)
    {
        switch (f.Kind)
        {
            case PdfFieldKind.Text:
            case PdfFieldKind.ComboBox:
            case PdfFieldKind.ListBox:
            case PdfFieldKind.PushButton:
                return w.GetDictionary("AP")?.Get("N") is null;
            case PdfFieldKind.CheckBox:
            case PdfFieldKind.RadioButton:
                string? s = w.GetName("AS");
                if (s is null or "Off")
                {
                    return false;
                }

                PdfObject? n = w.GetDictionary("AP")?.Get("N");
                return n is null || (n is PdfDictionary nd && nd.Get(s) is null);
            default:
                return false;
        }
    }

    private static HashSet<PdfDictionary> ComputeLacking(PdfAcroForm form)
    {
        var set = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (PdfFormField f in form.AllFieldList)
        {
            if (f.Children.Count > 0)
            {
                continue;
            }

            foreach (PdfDictionary w in f.Widgets)
            {
                if (Lacks(f, w))
                {
                    set.Add(w);
                }
            }
        }

        return set;
    }

    // With /NeedAppearances set, text and choice fields not filled in this session may hold stale appearances: rebuild them before
    // the flag goes, so that clearing it does not make old art authoritative.
    private static void RebuildStale(PdfAcroForm form, FormSession session)
    {
        foreach (PdfFormField f in form.AllFieldList)
        {
            if (f.Children.Count > 0 || f.Kind is not (PdfFieldKind.Text or PdfFieldKind.ComboBox or PdfFieldKind.ListBox))
            {
                continue;
            }

            foreach (PdfDictionary w in f.Widgets)
            {
                if (session.Touched.Add(w))
                {
                    StoreNormal(form.Doc, w, session);
                }
            }
        }
    }

    public static int GenerateMissing(PdfAcroForm form, CancellationToken ct)
    {
        PdfDocument doc = form.Doc;
        doc.EnsureEditable(0);
        FormSession session = form.Session;
        bool needs = form.NeedAppearances;
        int n = 0;
        foreach (PdfFormField f in form.AllFieldList)
        {
            ct.ThrowIfCancellationRequested();
            if (f.Children.Count > 0)
            {
                continue;
            }

            foreach (PdfDictionary w in f.Widgets)
            {
                bool lacks = Lacks(f, w);
                bool textLike = f.Kind is PdfFieldKind.Text or PdfFieldKind.ComboBox or PdfFieldKind.ListBox;
                if (!lacks && !(needs && textLike))
                {
                    continue;
                }

                if (f.Kind is PdfFieldKind.Unknown or PdfFieldKind.Signature)
                {
                    continue;
                }

                if (f.Kind is PdfFieldKind.CheckBox or PdfFieldKind.RadioButton)
                {
                    n += EnsureButtonState(doc, w, session) ? 1 : 0;
                }
                else
                {
                    n += StoreNormal(doc, w, session) ? 1 : 0;
                }

                session.Touched.Add(w);
            }
        }

        if (needs)
        {
            session.Lacking = ComputeLacking(form);
            if (session.Lacking.Count == 0)
            {
                form.Dictionary.RemoveEntry("NeedAppearances");
            }
        }

        return n;
    }

    internal static PdfGeneratedAppearance? Generate(PdfDocument doc, PdfDictionary widget)
    {
        if (PdfRectangle.From(widget.Get("Rect")) is not { } rect)
        {
            return null;
        }

        int obj = widget.Reference?.Number ?? -1;
        var c = new GenContext(doc, widget, (sev, msg) =>
        {
            if (sev == PdfSeverity.Info)
            {
                doc.ReportInfo(msg, obj, -1);
            }
            else
            {
                doc.ReportWarning(msg, obj, -1);
            }
        });
        return WidgetAppearance.Generate(c, rect);
    }

    private static PdfDictionary EnsureDict(PdfDictionary parent, string key)
    {
        if (parent.GetDictionary(key) is { } d)
        {
            return d;
        }

        parent.SetEntry(key, new PdfDictionary());
        return parent.GetDictionary(key)!;
    }

    private static bool StoreNormal(PdfDocument doc, PdfDictionary widget, FormSession session)
    {
        PdfGeneratedAppearance? gen = Generate(doc, widget);
        if (gen is null)
        {
            return false;
        }

        PdfReference r = StoreForm(doc, gen, session);
        EnsureDict(widget, "AP").SetEntry("N", r);
        return true;
    }

    private static bool EnsureButtonState(PdfDocument doc, PdfDictionary widget, FormSession session)
    {
        string? state = widget.GetName("AS");
        if (state is null or "Off")
        {
            return false;
        }

        PdfDictionary ap = EnsureDict(widget, "AP");
        PdfObject? n = ap.Get("N");
        if (n is PdfStream || (n is PdfDictionary existing && existing.Get(state) is PdfStream))
        {
            return false;
        }

        PdfGeneratedAppearance? gen = Generate(doc, widget);
        if (gen is null)
        {
            return false;
        }

        PdfReference r = StoreForm(doc, gen, session);
        if (n is not PdfDictionary states)
        {
            ap.SetEntry("N", new PdfDictionary());
            states = ap.GetDictionary("N")!;
        }

        states.SetEntry(state, r);
        return true;
    }

    /// <summary>Stores a generated appearance as a Form XObject stream of the document and returns its reference.</summary>
    internal static PdfReference StoreForm(PdfDocument doc, PdfGeneratedAppearance gen, FormSession session)
    {
        PdfStream s = doc.CreateStream(gen.Content.Span, compress: false);
        PdfReference r = doc.AddObject(s);
        PdfDictionary target = s.Dictionary;
        foreach (string key in gen.Dictionary.Keys.ToArray())
        {
            if (key is "Length" or "Filter" or "DecodeParms")
            {
                continue;
            }

            PdfObject? v = gen.Dictionary.GetRaw(key);
            if (v is null)
            {
                continue;
            }

            if (key == "Resources" && v is PdfDictionary res)
            {
                v = Detach(doc, res, session);
            }
            else if (key is "BBox" or "Matrix" && v is PdfArray nums)
            {
                // The generated content has four decimals; the box gets the same, so that a number that sits on a rounding tie
                // reads back the same after the file is written and read again.
                var rounded = new PdfArray();
                for (int i = 0; i < nums.Count; i++)
                {
                    rounded.Append(new PdfReal(Math.Round(nums.GetDouble(i), 4, MidpointRounding.AwayFromZero)));
                }

                v = rounded;
            }

            target.SetEntry(key, v);
        }

        return r;
    }

    // Font dictionaries of a generated appearance are the document's own (from /DR) or shared built-in ones: store each once as an
    // indirect object and refer to it, so that no object ends up in two containers.
    private static PdfDictionary Detach(PdfDocument doc, PdfDictionary res, FormSession session)
    {
        var copy = new PdfDictionary();
        foreach (string key in res.Keys.ToArray())
        {
            PdfObject? raw = res.GetRaw(key);
            if (raw is null)
            {
                continue;
            }

            if (key == "Font" && raw is PdfDictionary fonts)
            {
                var fc = new PdfDictionary();
                foreach (string fk in fonts.Keys.ToArray())
                {
                    PdfObject? fv = fonts.GetRaw(fk);
                    if (fv is PdfDictionary fd)
                    {
                        fc.SetEntry(fk, FontRef(doc, fd, session));
                    }
                    else if (fv is PdfReference)
                    {
                        fc.SetEntry(fk, fv);
                    }
                }

                copy.SetEntry("Font", fc);
            }
            else
            {
                copy.SetEntry(key, Clone(doc, raw));
            }
        }

        return copy;
    }

    private static PdfReference FontRef(PdfDocument doc, PdfDictionary fd, FormSession session)
    {
        if (fd.Reference is { } r)
        {
            return r;
        }

        if (session.FontRefs.TryGetValue(fd, out PdfReference? known))
        {
            return known;
        }

        PdfDictionary own = fd.OwnerPlusOne == 0 ? fd : (PdfDictionary)Clone(doc, fd);
        PdfReference added = doc.AddObject(own);
        session.FontRefs[fd] = added;
        return added;
    }

    /// <summary>
    /// A deep copy of a direct object that belongs to no document container, so it can be stored somewhere else. Indirect children
    /// become references. Iterative; bounded by <see cref="PdfLimits.MaxObjects"/> containers.
    /// </summary>
    internal static PdfObject Clone(PdfDocument doc, PdfObject src)
    {
        PdfObject? Leaf(PdfObject? o) => o switch
        {
            null => null,
            PdfDictionary { Reference: { } r } => r,
            PdfStream { Reference: { } r } => r,
            PdfArray { Reference: { } r } => r,
            PdfStream => null,
            _ => o,
        };

        if (Leaf(src) is { } direct && direct is not (PdfDictionary or PdfArray))
        {
            return direct;
        }

        PdfObject root = src is PdfDictionary ? new PdfDictionary() : new PdfArray();
        var stack = new Stack<(PdfObject From, PdfObject To)>();
        stack.Push((src, root));
        int budget = doc.Limits.MaxObjects;
        while (stack.Count > 0)
        {
            (PdfObject from, PdfObject to) = stack.Pop();
            if (--budget < 0)
            {
                throw new PdfLimitException("A form object is too large to copy.");
            }

            if (from is PdfDictionary fd)
            {
                var td = (PdfDictionary)to;
                foreach (string k in fd.Keys.ToArray())
                {
                    PdfObject? child = fd.GetRaw(k);
                    Add(child, v => td.SetEntry(k, v));
                }
            }
            else if (from is PdfArray fa)
            {
                var ta = (PdfArray)to;
                for (int i = 0; i < fa.Count; i++)
                {
                    Add(fa.GetRaw(i), v => ta.Append(v));
                }
            }
        }

        return root;

        void Add(PdfObject? child, Action<PdfObject> set)
        {
            PdfObject? l = Leaf(child);
            if (l is PdfDictionary d)
            {
                var nd = new PdfDictionary();
                set(nd);
                stack.Push((d, nd));
            }
            else if (l is PdfArray a)
            {
                var na = new PdfArray();
                set(na);
                stack.Push((a, na));
            }
            else if (l is not null)
            {
                set(l);
            }
        }
    }
}
