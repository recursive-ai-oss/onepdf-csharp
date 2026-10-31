using System.Linq;

namespace OnePdf.PdfA;

/// <summary>The individual repairs of a conversion. Each one changes the in-memory document and counts what it did.</summary>
internal sealed partial class ConversionRepairs
{
    private static readonly HashSet<string> s_allowedActions = new(StringComparer.Ordinal) { "GoTo", "GoToR", "GoToE", "Thread", "URI", "Named", "SubmitForm" };
    private static readonly HashSet<string> s_allowedNamed = new(StringComparer.Ordinal) { "NextPage", "PrevPage", "FirstPage", "LastPage" };

    // ISO 19005-2 6.3.1: the annotation types of ISO 32000-1 minus 3D, Sound, Screen and Movie.
    private static readonly HashSet<string> s_annotationTypes = new(StringComparer.Ordinal)
    {
        "Text", "Link", "FreeText", "Line", "Square", "Circle", "Polygon", "PolyLine", "Highlight", "Underline", "Squiggly", "StrikeOut",
        "Stamp", "Caret", "Ink", "Popup", "FileAttachment", "Widget", "PrinterMark", "TrapNet", "Watermark", "Redact",
    };

    private static readonly HashSet<string> s_blendModes = new(StringComparer.Ordinal)
    {
        "Normal", "Compatible", "Multiply", "Screen", "Overlay", "Darken", "Lighten", "ColorDodge", "ColorBurn", "HardLight", "SoftLight",
        "Difference", "Exclusion", "Hue", "Saturation", "Color", "Luminosity",
    };

    private static readonly HashSet<string> s_renderingIntents = new(StringComparer.Ordinal) { "RelativeColorimetric", "AbsoluteColorimetric", "Perceptual", "Saturation" };

    private static readonly string[] s_formKeys = ["OPI", "PS", "Ref", "Subtype2"];
    private static readonly string[] s_catalogKeys = ["NeedsRendering", "AA"];
    private static readonly string[] s_imageKeys = ["Alternates", "OPI"];
    private static readonly string[] s_externalKeys = ["F", "FFilter", "FDecodeParms"];
    private static readonly string[] s_roots = ["Info", "Root"];
    private static readonly string[] s_actionKeys = ["A", "OpenAction", "Next"];

    private readonly PdfDocument _doc;
    private readonly PdfAConversionOptions _options;
    private readonly ChangeLog _log;
    private readonly CancellationToken _ct;
    private readonly HashSet<PdfDictionary> _removedAnnotations = new(ReferenceEqualityComparer.Instance);

    public ConversionRepairs(PdfDocument doc, PdfAConversionOptions options, ChangeLog log, CancellationToken ct)
    {
        _doc = doc;
        _options = options;
        _log = log;
        _ct = ct;
    }

    private bool Part2 => _options.Part == PdfAPart.Part2;

    // ---- traversal ----------------------------------------------------------------------------------------------------------------

    /// <summary>Calls <paramref name="visit"/> once for every dictionary (and stream dictionary) reachable from the trailer, with the dictionary that holds it and the key it sits under.</summary>
    private void Walk(Action<PdfDictionary, PdfStream?, PdfDictionary?, string?> visit)
    {
        var seenObjects = new HashSet<int>();
        var seenDirect = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(PdfObject Value, PdfDictionary? Parent, string? Key)>();
        foreach (string root in s_roots)
        {
            if (_doc.Trailer.GetRaw(root) is { } r)
            {
                stack.Push((r, null, root));
            }
        }

        while (stack.Count > 0)
        {
            _ct.ThrowIfCancellationRequested();
            (PdfObject value, PdfDictionary? parent, string? key) = stack.Pop();
            PdfObject o = value;
            if (o is PdfReference rf)
            {
                PdfObject target = _doc.ResolveChain(rf.Number, 0);
                int num = target.Reference?.Number ?? rf.Number;
                if (!seenObjects.Add(num))
                {
                    continue;
                }

                if (seenObjects.Count > _doc.Limits.MaxObjects)
                {
                    throw new PdfLimitException($"The document has more than {_doc.Limits.MaxObjects} objects.");
                }

                o = target;
            }

            switch (o)
            {
                case PdfArray a:
                    for (int i = a.Count - 1; i >= 0; i--)
                    {
                        stack.Push((a.GetRaw(i), parent, key));
                    }

                    break;
                case PdfStream st:
                    visit(st.Dictionary, st, parent, key);
                    PushChildren(st.Dictionary, stack);
                    break;
                case PdfDictionary d:
                    if (!seenDirect.Add(d))
                    {
                        break;
                    }

                    visit(d, null, parent, key);
                    PushChildren(d, stack);
                    break;
            }
        }
    }

    private static void PushChildren(PdfDictionary d, Stack<(PdfObject Value, PdfDictionary? Parent, string? Key)> stack)
    {
        var children = new List<KeyValuePair<string, PdfObject>>(d.Count);
        foreach (KeyValuePair<string, PdfObject> e in d.Entries)
        {
            if (e.Key is "Parent" or "P" or "Prev" or "Last")
            {
                continue;
            }

            children.Add(e);
        }

        for (int k = children.Count - 1; k >= 0; k--)
        {
            stack.Push((children[k].Value, d, children[k].Key));
        }
    }

    // ---- forbidden features -------------------------------------------------------------------------------------------------------

    public void RemoveForbiddenFeatures()
    {
        PdfDictionary catalog = _doc.Catalog;
        RepairCatalog(catalog);
        RemoveAnnotations(afterPrepare: false);

        Walk((d, stream, parent, key) =>
        {
            RemoveActions(d, stream);
            RepairFontDictionary(d, stream);
            RemoveSignature(d, stream);
            if (stream is not null)
            {
                RepairStream(stream);
            }

            if (!ReferenceEquals(d, catalog) && d.ContainsKey("Metadata") && !ReferenceEquals(parent, catalog))
            {
                d.RemoveEntry("Metadata");
                _log.Add(PdfAChangeKind.MetadataRemoved, "Metadata streams other than the document's were removed.");
            }

            if (stream is null && key == "ExtGState" && d.GetName("Type") != "ExtGState")
            {
                foreach (KeyValuePair<string, PdfObject> e in d.Entries.ToArray())
                {
                    if (_doc.Resolve(e.Value) is PdfDictionary gs)
                    {
                        RepairGraphicsState(gs);
                    }
                }
            }
            else if (d.GetName("Type") == "ExtGState")
            {
                RepairGraphicsState(d);
            }

            if (stream is not null)
            {
                string? subtype = d.GetName("Subtype");
                if (subtype == "Image")
                {
                    RepairImage(d);
                }
                else if (subtype == "Form")
                {
                    foreach (string k in s_formKeys)
                    {
                        if (d.RemoveEntry(k))
                        {
                            _log.Add(PdfAChangeKind.ForbiddenEntriesRemoved, "Forbidden form XObject entries (/OPI, /PS, /Ref) were removed.");
                        }
                    }
                }
            }

            if (stream is null && key == "SMask" && d.ContainsKey("S") && d.Get("TR") is { } tr && tr is not PdfName { Value: "Identity" })
            {
                d.RemoveEntry("TR");
                _log.Add(PdfAChangeKind.ForbiddenEntriesRemoved, "Transfer functions of soft masks were removed.");
            }

            if (stream is null && key == "OCProperties")
            {
                RepairOptionalContent(d);
            }
        });
    }

    private void RepairCatalog(PdfDictionary catalog)
    {
        foreach (string k in s_catalogKeys)
        {
            if (catalog.RemoveEntry(k))
            {
                _log.Add(PdfAChangeKind.ForbiddenEntriesRemoved, $"Catalog entry /{k} was removed.");
            }
        }

        if (catalog.GetDictionary("AcroForm") is { } form && form.RemoveEntry("XFA"))
        {
            _log.Add(PdfAChangeKind.ForbiddenEntriesRemoved, "The XFA form data was removed; the AcroForm fields remain.");
        }

        if (catalog.GetDictionary("Names") is { } names)
        {
            if (names.RemoveEntry("JavaScript"))
            {
                _log.Add(PdfAChangeKind.ActionsRemoved, "The document-level JavaScript was removed.");
            }

            if (Part2 && names.RemoveEntry("EmbeddedFiles"))
            {
                _log.Add(PdfAChangeKind.EmbeddedFilesRemoved, "Embedded files were removed (PDF/A-2 output; use part 3 to keep them).");
            }
        }

        if (Part2 && catalog.RemoveEntry("AF"))
        {
            _log.Add(PdfAChangeKind.EmbeddedFilesRemoved, "Embedded files were removed (PDF/A-2 output; use part 3 to keep them).");
        }

        // A conversion rewrites the file, so signatures and the usage rights they grant stop being valid; ISO 19005-2 6.4.3 and 6.1.12 would
        // otherwise find a byte range that does not cover the new file.
        if (catalog.RemoveEntry("Perms"))
        {
            _log.Add(PdfAChangeKind.ForbiddenEntriesRemoved, "The permissions dictionary (usage rights, certification) was removed.");
        }

        if (catalog.GetDictionary("AcroForm") is { } acro)
        {
            acro.RemoveEntry("SigFlags");
        }
    }

    private static bool IsForbidden(PdfDictionary action)
    {
        string? s = action.GetName("S");
        if (s is null || !s_allowedActions.Contains(s))
        {
            return true;
        }

        return s == "Named" && !s_allowedNamed.Contains(action.GetName("N") ?? string.Empty);
    }

    // ISO 19005-2 6.5.1 and 6.5.2: only some action types remain, and widgets, fields, the catalog and pages have no additional actions.
    private void RemoveActions(PdfDictionary d, PdfStream? stream)
    {
        if (stream is not null)
        {
            return;
        }

        foreach (string key in s_actionKeys)
        {
            PdfObject? v = d.Get(key);
            if (v is PdfDictionary act && act.ContainsKey("S") && d.GetName("Subtype") is null or "Widget" or "Link")
            {
                if (IsForbidden(act))
                {
                    d.RemoveEntry(key);
                    _log.Add(PdfAChangeKind.ActionsRemoved, "Scripts and actions that PDF/A forbids were removed (JavaScript, Launch, Hide, ResetForm, ...).");
                }
                else if (act.GetName("S") is "GoToR" or "GoToE" && act.Get("F") is PdfDictionary target && !target.ContainsKey("EF")
                    && (target.GetString("UF") ?? target.GetString("F")) is { } path)
                {
                    // ISO 19005-3 6.8 treats every file specification dictionary as an attachment; the plain string form names the same file.
                    act.SetEntry("F", path);
                    _log.Add(PdfAChangeKind.ForbiddenEntriesRemoved, "File specification dictionaries of remote go-to actions were written as plain file names.");
                }
            }
            else if (v is PdfArray list && key == "Next")
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i] is PdfDictionary a2 && IsForbidden(a2))
                    {
                        list.RemoveAt(i);
                        _log.Add(PdfAChangeKind.ActionsRemoved, "Scripts and actions that PDF/A forbids were removed (JavaScript, Launch, Hide, ResetForm, ...).");
                    }
                }
            }
        }

        string? type = d.GetName("Type");
        bool widgetOrField = d.GetName("Subtype") == "Widget" || d.ContainsKey("FT");
        if (type is "Catalog" or "Page" || widgetOrField)
        {
            if (d.RemoveEntry("AA"))
            {
                _log.Add(PdfAChangeKind.ActionsRemoved, "Scripts and actions that PDF/A forbids were removed (JavaScript, Launch, Hide, ResetForm, ...).");
            }

            if (widgetOrField && d.RemoveEntry("A"))
            {
                _log.Add(PdfAChangeKind.ActionsRemoved, "Scripts and actions that PDF/A forbids were removed (JavaScript, Launch, Hide, ResetForm, ...).");
            }
        }
    }

    private void RepairStream(PdfStream s)
    {
        PdfDictionary d = s.Dictionary;
        foreach (string k in s_externalKeys)
        {
            if (d.RemoveEntry(k))
            {
                _log.Add(PdfAChangeKind.ExternalReferencesRemoved, "References to external stream data were removed.");
            }
        }

        // ISO 19005-2 6.1.7.2: LZW is not allowed. The data is decoded and stored again with Flate; the picture does not change.
        IReadOnlyList<string> filters = s.FilterNames;
        int lzw = -1;
        for (int i = 0; i < filters.Count; i++)
        {
            if (filters[i] == "LZWDecode")
            {
                lzw = i;
            }
        }

        if (lzw < 0)
        {
            return;
        }

        if (s.UndecodedFilter is null)
        {
            ReadOnlyMemory<byte> decoded;
            try
            {
                decoded = s.GetDecodedBytes(_ct);
            }
            catch (PdfException)
            {
                return; // unreadable data stays and is reported by the check
            }

            s.SetData(decoded.Span, compress: true);
            _log.Add(PdfAChangeKind.StreamsRecompressed, "Streams that used LZW were stored again with Flate.");
            return;
        }

        // An image codec follows the LZW layer (LZW then DCT): only the leading filters are replaced by one Flate layer.
        ReadOnlyMemory<byte>? lead = s.DecodeLeadingFilters(lzw + 1, _ct);
        if (lead is null)
        {
            return;
        }

        PdfObject? oldParms = d.Get("DecodeParms");
        var newFilters = new PdfArray();
        var newParms = new PdfArray();
        newFilters.Append(new PdfName("FlateDecode"));
        newParms.Append(null);
        bool anyParms = false;
        for (int i = lzw + 1; i < filters.Count; i++)
        {
            newFilters.Append(new PdfName(filters[i]));
            PdfObject? p = oldParms is PdfArray pa && i < pa.Count ? pa.GetRaw(i) : null;
            newParms.Append(p);
            anyParms |= p is PdfDictionary;
        }

        s.SetStoredBytes(Writing.FlateWriter.Compress(lead.Value.Span));
        d.SetEntry("Filter", newFilters);
        if (anyParms)
        {
            d.SetEntry("DecodeParms", newParms);
        }
        else
        {
            d.RemoveEntry("DecodeParms");
        }

        _log.Add(PdfAChangeKind.StreamsRecompressed, "Streams that used LZW were stored again with Flate.");
    }

    private void RepairGraphicsState(PdfDictionary gs)
    {
        void Drop(string k, string what)
        {
            if (gs.RemoveEntry(k))
            {
                _log.Add(PdfAChangeKind.ForbiddenEntriesRemoved, what);
            }
        }

        Drop("TR", "Transfer functions (/TR, /TR2, /HTP) were removed from graphics states.");
        if (gs.Get("TR2") is { } tr2 && tr2 is not PdfName { Value: "Default" })
        {
            Drop("TR2", "Transfer functions (/TR, /TR2, /HTP) were removed from graphics states.");
        }

        Drop("HTP", "Transfer functions (/TR, /TR2, /HTP) were removed from graphics states.");
        if (gs.Get("HT") is PdfDictionary ht && (ht.GetInt("HalftoneType") is not 1 || ht.ContainsKey("HalftoneName") || HasTransferFunction(ht)))
        {
            Drop("HT", "A halftone that PDF/A forbids was removed from a graphics state.");
        }

        if (gs.GetName("RI") is { } ri && !s_renderingIntents.Contains(ri))
        {
            Drop("RI", "A rendering intent that is not one of the four standard ones was removed.");
        }

        PdfObject? bm = gs.Get("BM");
        if (bm is PdfName bn && !s_blendModes.Contains(bn.Value))
        {
            gs.SetEntry("BM", new PdfName("Normal"));
            _log.Add(PdfAChangeKind.ForbiddenEntriesRemoved, "A blend mode that ISO 32000-1 does not define was replaced by Normal.");
        }
        else if (bm is PdfArray ba)
        {
            bool bad = false;
            for (int i = 0; i < ba.Count; i++)
            {
                if (ba.GetName(i) is { } m && !s_blendModes.Contains(m))
                {
                    bad = true;
                }
            }

            if (bad)
            {
                gs.SetEntry("BM", new PdfName("Normal"));
                _log.Add(PdfAChangeKind.ForbiddenEntriesRemoved, "A blend mode that ISO 32000-1 does not define was replaced by Normal.");
            }
        }
    }

    // 6.2.5: a halftone may not carry a /TransferFunction. Halftones only matter to an imagesetter, so the whole entry goes.
    private static bool HasTransferFunction(PdfDictionary halftone)
    {
        var stack = new Stack<(PdfDictionary D, int Depth)>();
        stack.Push((halftone, 0));
        int steps = 0;
        while (stack.Count > 0 && steps++ < 10_000)
        {
            (PdfDictionary d, int depth) = stack.Pop();
            if (d.ContainsKey("TransferFunction"))
            {
                return true;
            }

            if (depth < 4)
            {
                foreach (KeyValuePair<string, PdfObject> e in d.Entries)
                {
                    PdfObject? v = d.Get(e.Key);
                    if (v is PdfStream vs)
                    {
                        stack.Push((vs.Dictionary, depth + 1));
                    }
                    else if (v is PdfDictionary child)
                    {
                        stack.Push((child, depth + 1));
                    }
                }
            }
        }

        return false;
    }

    private void RepairImage(PdfDictionary d)
    {
        foreach (string k in s_imageKeys)
        {
            if (d.RemoveEntry(k))
            {
                _log.Add(PdfAChangeKind.ForbiddenEntriesRemoved, "Image entries that PDF/A forbids (/Alternates, /OPI) were removed.");
            }
        }

        if (d.Get("Interpolate") is PdfBoolean { Value: true })
        {
            d.SetEntry("Interpolate", new PdfBoolean(false));
            _log.Add(PdfAChangeKind.ForbiddenEntriesRemoved, "/Interpolate true was set to false on images.");
        }

        if (d.GetName("Intent") is { } intent && !s_renderingIntents.Contains(intent))
        {
            d.RemoveEntry("Intent");
            _log.Add(PdfAChangeKind.ForbiddenEntriesRemoved, "A rendering intent that is not one of the four standard ones was removed.");
        }
    }

    private void RepairOptionalContent(PdfDictionary ocp)
    {
        var configs = new List<PdfDictionary>();
        if (ocp.GetDictionary("D") is { } def)
        {
            configs.Add(def);
        }

        if (ocp.Get("Configs") is PdfArray ca)
        {
            for (int i = 0; i < ca.Count; i++)
            {
                if (ca[i] is PdfDictionary c)
                {
                    configs.Add(c);
                }
            }
        }

        CompleteOrder(ocp, configs);
        var names = new HashSet<string>(StringComparer.Ordinal);
        int n = 0;
        foreach (PdfDictionary c in configs)
        {
            if (c.RemoveEntry("AS"))
            {
                _log.Add(PdfAChangeKind.ForbiddenEntriesRemoved, "Optional content /AS entries were removed.");
            }

            string name = c.GetString("Name")?.GetText() ?? string.Empty;
            if (c.GetString("Name") is null || !names.Add(name))
            {
                string fresh;
                do
                {
                    fresh = (name.Length > 0 ? name : "Configuration") + " " + (++n + 1);
                }
                while (!names.Add(fresh));
                c.SetEntry("Name", PdfString.FromText(fresh));
                _log.Add(PdfAChangeKind.ForbiddenEntriesRemoved, "Optional content configurations were given unique names.");
            }
        }
    }

    // ---- annotations --------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Removes annotations that PDF/A does not allow: types outside the allowed list, file attachments in part 2, ones a viewer would not show
    /// (Hidden, NoView, Invisible), and, once appearances have been generated, those that still have no appearance stream.
    /// </summary>
    private void RemoveAnnotations(bool afterPrepare)
    {
        PdfPageCollection pages = _doc.Pages;
        for (int p = 0; p < pages.Count; p++)
        {
            _ct.ThrowIfCancellationRequested();
            if (pages[p].Dictionary.Get("Annots") is not PdfArray annots)
            {
                continue;
            }

            for (int i = annots.Count - 1; i >= 0; i--)
            {
                if (annots[i] is not PdfDictionary a)
                {
                    if (!afterPrepare && annots.GetRaw(i) is not PdfReference)
                    {
                        annots.RemoveAt(i);
                    }

                    continue;
                }

                string subtype = a.GetName("Subtype") ?? string.Empty;
                string? why = null;
                if (!s_annotationTypes.Contains(subtype))
                {
                    why = $"{(subtype.Length > 0 ? subtype : "Unknown")} annotations are not allowed";
                }
                else if (Part2 && subtype == "FileAttachment")
                {
                    why = "file attachment annotations are not allowed in PDF/A-2";
                }
                else if (!afterPrepare && subtype != "Popup" && a.ContainsKey("F") && (a.GetInt("F") & (1 | 2 | 32 | 256)) != 0)
                {
                    why = "hidden annotations are not allowed";
                }
                else if (afterPrepare && subtype is not ("Popup" or "Link"))
                {
                    PdfRectangle? rect = PdfRectangle.From(a.Get("Rect"));
                    bool empty = rect is { } r && r.Width == 0 && r.Height == 0;
                    PdfDictionary? ap = a.GetDictionary("AP");
                    bool button = subtype == "Widget" && InheritedName(a, "FT") == "Btn";
                    if (!empty && (ap is null || (button ? ap.Get("N") is not PdfDictionary || ap.Get("N") is PdfStream : ap.Get("N") is not PdfStream)))
                    {
                        why = "no appearance stream could be made for them";
                    }
                }

                if (why is null)
                {
                    continue;
                }

                annots.RemoveAt(i);
                _removedAnnotations.Add(a);
                _log.Add(PdfAChangeKind.AnnotationsRemoved, "Annotations that PDF/A does not allow were removed.");
                _log.Warn($"Page {p + 1}: a {subtype} annotation was removed because {why}.");
            }
        }

        if (_removedAnnotations.Count > 0)
        {
            RemoveFromFields();
        }
    }

    private static string? InheritedName(PdfDictionary field, string key)
    {
        PdfDictionary? cur = field;
        for (int depth = 0; cur is not null && depth < 32; depth++)
        {
            if (cur.GetName(key) is { } v)
            {
                return v;
            }

            cur = cur.GetDictionary("Parent");
        }

        return null;
    }

    // A removed widget must not stay listed in the form, where a viewer would find a field with nothing to draw.
    private void RemoveFromFields()
    {
        if (_doc.Catalog.GetDictionary("AcroForm")?.GetArray("Fields") is not { } fields)
        {
            return;
        }

        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<PdfArray>();
        stack.Push(fields);
        while (stack.Count > 0)
        {
            PdfArray list = stack.Pop();
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] is not PdfDictionary f)
                {
                    continue;
                }

                if (_removedAnnotations.Contains(f))
                {
                    list.RemoveAt(i);
                }
                else if (seen.Add(f) && f.Get("Kids") is PdfArray kids)
                {
                    stack.Push(kids);
                }
            }
        }
    }

    public void AfterPrepare()
    {
        RemoveAnnotations(afterPrepare: true);
        if (_doc.Catalog.GetDictionary("AcroForm") is { } form && form.RemoveEntry("NeedAppearances"))
        {
            _log.Add(PdfAChangeKind.ForbiddenEntriesRemoved, "/NeedAppearances was cleared; fields keep their stored appearances.");
        }
    }

    // ---- embedded files -----------------------------------------------------------------------------------------------------------

    public void RepairEmbeddedFiles()
    {
        if (Part2)
        {
            if (!_options.RemoveForbiddenFeatures)
            {
                return;
            }

            Walk((d, stream, parent, key) =>
            {
                if (stream is null && d.ContainsKey("EF"))
                {
                    d.RemoveEntry("EF");
                    d.RemoveEntry("RF");
                    _log.Add(PdfAChangeKind.EmbeddedFilesRemoved, "Embedded files were removed (PDF/A-2 output; use part 3 to keep them).");
                }
            });
            return;
        }

        var specs = new List<PdfDictionary>();
        Walk((d, stream, parent, key) =>
        {
            if (stream is null && d.ContainsKey("EF"))
            {
                specs.Add(d);
            }
        });

        if (specs.Count == 0)
        {
            return;
        }

        PdfArray? af = _doc.Catalog.GetArray("AF");
        string? date = (_doc.Info.ModificationDate ?? _doc.Info.CreationDate) is { } dt ? PdfDocument.FormatDate(dt) : null;
        foreach (PdfDictionary spec in specs)
        {
            if (spec.GetString("F") is null && spec.GetString("UF") is { } uf)
            {
                spec.SetEntry("F", uf);
            }
            else if (spec.GetString("UF") is null && spec.GetString("F") is { } f)
            {
                spec.SetEntry("UF", f);
            }
            else if (spec.GetString("F") is null)
            {
                spec.SetEntry("F", PdfString.FromText("attachment"));
                spec.SetEntry("UF", PdfString.FromText("attachment"));
            }

            if (spec.GetName("AFRelationship") is not { } rel || !IsRelationship(rel))
            {
                spec.SetEntry("AFRelationship", new PdfName("Unspecified"));
                _log.Add(PdfAChangeKind.EmbeddedFilesCompleted, "Embedded files were completed with the entries PDF/A-3 requires.");
            }

            if (spec.GetDictionary("EF")?.GetStream("F") is { } file)
            {
                if (file.Dictionary.GetName("Subtype") is null)
                {
                    file.Dictionary.SetEntry("Subtype", new PdfName("application/octet-stream"));
                    _log.Add(PdfAChangeKind.EmbeddedFilesCompleted, "Embedded files were completed with the entries PDF/A-3 requires.");
                }

                PdfDictionary pars = file.Dictionary.GetDictionary("Params") ?? new PdfDictionary();
                if (!pars.ContainsKey("ModDate"))
                {
                    pars.SetEntry("ModDate", PdfString.FromText(date ?? PdfDocument.FormatDate(DateTimeOffset.UtcNow)));
                    file.Dictionary.SetEntry("Params", pars);
                    _log.Add(PdfAChangeKind.EmbeddedFilesCompleted, "Embedded files were completed with the entries PDF/A-3 requires.");
                }
            }
        }

        // The association: every file specification must be listed in the catalog's /AF.
        var listed = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        if (af is not null)
        {
            for (int i = 0; i < af.Count; i++)
            {
                if (af[i] is PdfDictionary x)
                {
                    listed.Add(x);
                }
            }
        }

        foreach (PdfDictionary spec in specs)
        {
            if (listed.Contains(spec) || spec.Reference is not { } sr)
            {
                continue;
            }

            if (af is null)
            {
                af = new PdfArray();
                _doc.Catalog.SetEntry("AF", af);
            }

            af.Append(sr);
            _log.Add(PdfAChangeKind.EmbeddedFilesCompleted, "Embedded files were completed with the entries PDF/A-3 requires.");
        }
    }

    private static bool IsRelationship(string r) =>
        r is "Source" or "Data" or "Alternative" or "Supplement" or "EncryptedPayload" or "FormData" or "Schema" or "Unspecified";

    // ---- fonts and colour ---------------------------------------------------------------------------------------------------------

    public void EmbedFonts()
    {
        if (!_options.EmbedSubstituteFonts)
        {
            return;
        }

        PdfSubstituteEmbedReport report = _doc.EmbedSubstituteFonts(new PdfSubstituteEmbedOptions
        {
            Provider = _options.FontProvider,
            FaceForFont = _options.SubstituteUnknownFonts ? FallbackFace : null,
        }, _ct);
        int embedded = 0;
        foreach (PdfSubstituteEntry e in report.Entries)
        {
            if (e.Outcome != PdfSubstituteOutcome.Embedded)
            {
                continue;
            }

            embedded++;
            if (e.FromCaller)
            {
                _log.Warn($"Font {e.BaseFont} is not embedded in the file and has no metric-compatible substitute; {e.Substitute} was embedded in its place, so the letter shapes differ.");
            }

            if (e.MissingCodes.Count > 0)
            {
                _findings.Add(("6.2.11.8", $"The substitute {e.Substitute} for font {e.BaseFont} has no glyph for {e.MissingCodes.Count} code(s) the pages show (they would draw .notdef, which PDF/A forbids)."));
            }
        }

        _log.Add(PdfAChangeKind.FontsEmbedded, "Substitute fonts were embedded for fonts the file did not embed.", embedded);
    }

    private PdfFontFile? FallbackFace(PdfSubstituteRequest req)
    {
        if (req.IsSymbolic)
        {
            return null;
        }

        string n = req.FontName;
        bool mono = req.IsFixedPitch || ContainsAny(n, "Courier", "Mono", "Typewriter", "LetterGothic", "Consolas", "LucidaConsole", "Fixed");
        bool serif = !mono && (req.IsSerif || ContainsAny(n, "Times", "Palatino", "Bookman", "Century", "Schlbk", "Garamond", "Georgia", "Minion", "Cheltenham", "Caslon", "Baskerville", "Antiqua", "Roman") && !ContainsAny(n, "Sans"));
        string standard = mono
            ? (req.IsBold ? (req.IsItalic ? "Courier-BoldOblique" : "Courier-Bold") : (req.IsItalic ? "Courier-Oblique" : "Courier"))
            : serif
                ? (req.IsBold ? (req.IsItalic ? "Times-BoldItalic" : "Times-Bold") : (req.IsItalic ? "Times-Italic" : "Times-Roman"))
                : (req.IsBold ? (req.IsItalic ? "Helvetica-BoldOblique" : "Helvetica-Bold") : (req.IsItalic ? "Helvetica-Oblique" : "Helvetica"));
        var request = new PdfFontRequest
        {
            FontName = standard,
            FamilyName = standard.Split('-')[0],
            StandardName = standard,
            IsBold = req.IsBold,
            IsItalic = req.IsItalic,
            IsSerif = serif,
            IsFixedPitch = mono,
        };

        PdfSubstituteFont? sub = null;
        IPdfFontProvider? chosen = _options.FontProvider ?? _doc.FontProvider;
        if (chosen is not null)
        {
            sub = chosen.FindFont(request);
        }
        else
        {
            foreach (IPdfFontProvider p in PdfFontProviders.RegisteredProviders)
            {
                sub = p.FindFont(request);
                if (sub is not null)
                {
                    break;
                }
            }
        }

        if (sub is null)
        {
            return null;
        }

        try
        {
            return PdfFontFile.Open(sub.Data.ToArray());
        }
        catch (PdfFormatException)
        {
            return null;
        }
    }

    private static bool ContainsAny(string name, params string[] parts)
    {
        foreach (string p in parts)
        {
            if (name.Contains(p, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
