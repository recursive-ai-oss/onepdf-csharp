using System.Linq;
using System.Text;
using OnePdf.Content;
using OnePdf.FontData;

namespace OnePdf.PdfA;

/// <summary>
/// Looks at everything reachable from the catalog and the pages and lists the requirements of ISO 19005-2 (and -3) it can verify
/// from the file's structure. It reads only; <see cref="PdfAPreparer"/> makes the repairs. Each check cites the clause it comes
/// from; <c>docs/pdfa.md</c> lists what is checked, what is written, and what is left to a validator.
/// </summary>
internal sealed class PdfAChecker
{
    private const int MaxViolations = 2000;
    private const int MaxIssuesPerKind = 20;

    // ISO 32000-1 Table 118: the predefined CMaps a PDF/A file may name without embedding.
    private static readonly HashSet<string> s_predefinedCMaps = new(StringComparer.Ordinal)
    {
        "Identity-H", "Identity-V", "GB-EUC-H", "GB-EUC-V", "GBpc-EUC-H", "GBpc-EUC-V", "GBK-EUC-H", "GBK-EUC-V", "GBKp-EUC-H", "GBKp-EUC-V",
        "GBK2K-H", "GBK2K-V", "UniGB-UCS2-H", "UniGB-UCS2-V", "UniGB-UTF16-H", "UniGB-UTF16-V", "B5pc-H", "B5pc-V", "HKscs-B5-H", "HKscs-B5-V",
        "ETen-B5-H", "ETen-B5-V", "ETenms-B5-H", "ETenms-B5-V", "CNS-EUC-H", "CNS-EUC-V", "UniCNS-UCS2-H", "UniCNS-UCS2-V", "UniCNS-UTF16-H",
        "UniCNS-UTF16-V", "83pv-RKSJ-H", "90ms-RKSJ-H", "90ms-RKSJ-V", "90msp-RKSJ-H", "90msp-RKSJ-V", "90pv-RKSJ-H", "Add-RKSJ-H", "Add-RKSJ-V",
        "EUC-H", "EUC-V", "Ext-RKSJ-H", "Ext-RKSJ-V", "H", "V", "UniJIS-UCS2-H", "UniJIS-UCS2-V", "UniJIS-UCS2-HW-H", "UniJIS-UCS2-HW-V",
        "UniJIS-UTF16-H", "UniJIS-UTF16-V", "KSC-EUC-H", "KSC-EUC-V", "KSCms-UHC-H", "KSCms-UHC-V", "KSCms-UHC-HW-H", "KSCms-UHC-HW-V",
        "KSCpc-EUC-H", "UniKS-UCS2-H", "UniKS-UCS2-V", "UniKS-UTF16-H", "UniKS-UTF16-V",
    };

    // 6.3.1: the annotation types of ISO 32000-1 minus 3D, Sound, Screen and Movie.
    private static readonly HashSet<string> s_annotationTypes = new(StringComparer.Ordinal)
    {
        "Text", "Link", "FreeText", "Line", "Square", "Circle", "Polygon", "PolyLine", "Highlight", "Underline", "Squiggly", "StrikeOut",
        "Stamp", "Caret", "Ink", "Popup", "FileAttachment", "Widget", "PrinterMark", "TrapNet", "Watermark", "Redact",
    };

    // 6.5.1: the actions that remain (everything else, including Launch, JavaScript, Hide and ResetForm, is out).
    private static readonly HashSet<string> s_allowedActions = new(StringComparer.Ordinal) { "GoTo", "GoToR", "GoToE", "Thread", "URI", "Named", "SubmitForm" };

    private static readonly HashSet<string> s_allowedNamed = new(StringComparer.Ordinal) { "NextPage", "PrevPage", "FirstPage", "LastPage" };

    private static readonly HashSet<string> s_blendModes = new(StringComparer.Ordinal)
    {
        "Normal", "Compatible", "Multiply", "Screen", "Overlay", "Darken", "Lighten", "ColorDodge", "ColorBurn", "HardLight", "SoftLight",
        "Difference", "Exclusion", "Hue", "Saturation", "Color", "Luminosity",
    };

    private static readonly HashSet<string> s_relationships = new(StringComparer.Ordinal)
    {
        "Source", "Data", "Alternative", "Supplement", "EncryptedPayload", "FormData", "Schema", "Unspecified",
    };

    private readonly PdfDocument _doc;
    private readonly PdfAOptions _options;
    private readonly bool _saving;
    private readonly bool _decrypting;
    private readonly CancellationToken _ct;
    private readonly List<PdfAViolation> _violations = [];
    private readonly Dictionary<string, int> _perKind = new(StringComparer.Ordinal);
    private readonly HashSet<int> _seen = [];
    private readonly Stack<Item> _stack = new();
    private readonly HashSet<int> _afTargets = [];
    private readonly List<(int Owner, PdfDictionary Spec)> _embeddedSpecs = [];
    private readonly HashSet<PdfDictionary> _fontDicts = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<int> _profilesChecked = [];
    private bool _rgb, _gray, _cmyk;
    private int _rgbOwner = -1, _cmykOwner = -1, _grayOwner = -1;
    private int _rgbPage = -1, _cmykPage = -1, _grayPage = -1;
    private string? _intentSpace;
    private bool _truncated;
    private readonly SortedSet<int> _transparentPages = [];
    private readonly HashSet<int> _pageNumbers = [];

    private readonly record struct Item(PdfObject Value, int Owner, string? Key, PdfDictionary? Parent, int Page);

    public PdfAChecker(PdfDocument doc, PdfAOptions options, bool saving, bool decrypting, CancellationToken ct)
    {
        _doc = doc;
        _options = options;
        _saving = saving;
        _decrypting = decrypting;
        _ct = ct;
    }

    public IReadOnlyList<PdfAViolation> Run()
    {
        PdfDictionary catalog = _doc.Catalog;
        CheckFile();
        CheckIntent(catalog);

        // Pages first, so a problem inside a page is attributed to it; shared objects are reported once, at the first page using them.
        PdfPageCollection pages = _doc.Pages;
        for (int i = 0; i < pages.Count; i++)
        {
            _ct.ThrowIfCancellationRequested();
            PdfDictionary pd;
            try
            {
                pd = pages[i].Dictionary;
            }
            catch (PdfFormatException ex)
            {
                Add("6.1", $"Page {i + 1} cannot be read: {ex.Message}", ex.ObjectNumber, i);
                continue;
            }

            if (pd.Reference is { } pr && _seen.Contains(pr.Number))
            {
                continue;
            }

            if (pd.Reference is { } pref)
            {
                _pageNumbers.Add(pref.Number);
            }

            _stack.Push(new Item(pd.Reference is { } r ? (PdfObject)r : pd, pd.Reference?.Number ?? -1, null, null, i));
            Drain();
        }

        _stack.Push(new Item(_doc.Trailer.GetRaw("Root") ?? catalog, -1, null, null, -1));
        if (_doc.Trailer.GetRaw("Info") is { } info)
        {
            _stack.Push(new Item(info, -1, "Info", null, -1));
        }

        Drain();
        CheckEmbeddedSpecs();
        CheckColourUse();
        CheckCreatedFonts();
        CheckTransparency();
        if (_seen.Count > 8388607)
        {
            Add("6.1.13", "The file has more than 8388607 indirect objects.", -1, -1);
        }

        return _violations;
    }

    private void Add(string clause, string message, int owner, int page)
    {
        if (_violations.Count >= MaxViolations)
        {
            if (!_truncated)
            {
                _truncated = true;
                _violations.Add(new PdfAViolation("-", $"More than {MaxViolations} violations; the rest are not listed.", -1, -1));
            }

            return;
        }

        _violations.Add(new PdfAViolation(clause, message, owner, page));
    }

    // The same kind of problem on every glyph or operator would bury the list; list the first few and count the rest.
    private bool Limited(string kind)
    {
        _perKind.TryGetValue(kind, out int n);
        _perKind[kind] = n + 1;
        return n >= MaxIssuesPerKind;
    }

    // ---- file level -------------------------------------------------------------------------------------------------------------

    private void CheckFile()
    {
        if (_doc.IsEncrypted && !_decrypting)
        {
            Add("6.1.3", "The file is encrypted. PDF/A forbids the /Encrypt entry; save with PdfSaveOptions.Encryption set to Remove.", -1, -1);
        }

        if (!_saving && _doc.Version is { } v && (v.Length < 3 || !v.StartsWith("1.", StringComparison.Ordinal) || v[2] > '7'))
        {
            Add("6.1.2", $"The file header says PDF {v}; PDF/A-{(int)_options.Part} requires %PDF-1.n with n from 0 to 7.", -1, -1);
        }

        if (!_saving && _doc.Trailer.Get("ID") is not PdfArray { Count: > 0 })
        {
            Add("6.1.3", "The trailer has no /ID.", -1, -1);
        }
    }

    private void CheckIntent(PdfDictionary catalog)
    {
        PdfReference? firstProfile = null;
        if (catalog.Get("OutputIntents") is PdfArray intents)
        {
            for (int i = 0; i < intents.Count; i++)
            {
                if (intents[i] is not PdfDictionary oi)
                {
                    continue;
                }

                int owner = (intents.GetRaw(i) as PdfReference)?.Number ?? -1;
                PdfObject? profileRaw = oi.GetRaw("DestOutputProfile");
                if (profileRaw is PdfReference pr)
                {
                    if (firstProfile is not null && pr.Number != firstProfile.Number)
                    {
                        Add("6.2.3", "The output intents name different destination profiles; all must share one profile object.", owner, -1);
                    }

                    firstProfile ??= pr;
                }

                if (oi.GetName("S") != "GTS_PDFA1")
                {
                    continue;
                }

                if (_doc.Resolve(profileRaw) is not PdfStream profile)
                {
                    Add("6.2.3", "The PDF/A output intent has no DestOutputProfile stream.", owner, -1);
                    continue;
                }

                if (oi.GetString("OutputConditionIdentifier") is null)
                {
                    Add("6.2.3", "The PDF/A output intent has no OutputConditionIdentifier.", owner, -1);
                }

                ReadOnlyMemory<byte> data;
                try
                {
                    data = profile.GetDecodedBytes(_ct);
                }
                catch (PdfException)
                {
                    Add("6.2.3", "The destination profile of the output intent cannot be decoded.", profile.Reference?.Number ?? owner, -1);
                    continue;
                }

                ReadOnlySpan<byte> h = data.Span;
                if (h.Length < 128 || !h.Slice(36, 4).SequenceEqual("acsp"u8))
                {
                    Add("6.2.3", "The destination profile of the output intent is not an ICC profile.", profile.Reference?.Number ?? owner, -1);
                    continue;
                }

                string cls = Encoding.ASCII.GetString(h.Slice(12, 4));
                string space = Encoding.ASCII.GetString(h.Slice(16, 4));
                if (cls is not ("prtr" or "mntr") || space is not ("RGB " or "CMYK" or "GRAY") || h[8] >= 5)
                {
                    Add("6.2.3", $"The destination profile is not usable as an output profile (class '{cls}', space '{space}', version {h[8]}); it must be a printer or monitor profile in RGB, CMYK or GRAY, below ICC version 5.", profile.Reference?.Number ?? owner, -1);
                    continue;
                }

                _intentSpace ??= space;
            }
        }
    }

    // ---- traversal --------------------------------------------------------------------------------------------------------------

    private void Drain()
    {
        while (_stack.Count > 0)
        {
            _ct.ThrowIfCancellationRequested();
            Item it = _stack.Pop();
            PdfObject o = it.Value;
            int owner = it.Owner;
            if (o is PdfReference r)
            {
                PdfObject target = _doc.ResolveChain(r.Number, 0);
                int num = target.Reference?.Number ?? r.Number;
                if (!_seen.Add(num))
                {
                    continue;
                }

                if (_seen.Count > _doc.Limits.MaxObjects)
                {
                    throw new PdfLimitException($"The document has more than {_doc.Limits.MaxObjects} objects.");
                }

                o = target;
                owner = num;
            }

            Visit(o, owner, it);
        }
    }

    private void Visit(PdfObject o, int owner, Item it)
    {
        switch (o)
        {
            case PdfInteger i:
                if (i.Value is > int.MaxValue or < int.MinValue)
                {
                    Add("6.1.13", $"The integer {i.Value} is outside the 32-bit range.", owner, it.Page);
                }

                break;
            case PdfReal real:
                if (Math.Abs(real.Value) > 3.403e38 || (real.Value != 0 && Math.Abs(real.Value) < 1.175e-38))
                {
                    Add("6.1.13", "A real number is outside the range PDF/A allows.", owner, it.Page);
                }

                break;
            case PdfString s:
                if (s.Bytes.Length > 32767)
                {
                    Add("6.1.13", $"A string has {s.Bytes.Length} bytes; the limit is 32767.", owner, it.Page);
                }

                break;
            case PdfName n:
                if (Encoding.UTF8.GetByteCount(n.Value) > 127)
                {
                    Add("6.1.13", "A name is longer than 127 bytes.", owner, it.Page);
                }

                if (it.Key is "ColorSpace" or "CS")
                {
                    UseColourSpace(n, owner, it.Page, 0);
                }

                break;
            case PdfArray a:
                if (it.Key is "ColorSpace" or "CS")
                {
                    UseColourSpace(a, owner, it.Page, 0);
                }

                if (it.Key == "AF")
                {
                    for (int k = 0; k < a.Count; k++)
                    {
                        if (a.GetRaw(k) is PdfReference ar)
                        {
                            _afTargets.Add(ar.Number);
                        }
                    }
                }

                for (int k = a.Count - 1; k >= 0; k--)
                {
                    _stack.Push(new Item(a.GetRaw(k), owner, it.Key, it.Parent, it.Page));
                }

                break;
            case PdfStream st:
                VisitDictionary(st.Dictionary, st, owner, it);
                break;
            case PdfDictionary d:
                VisitDictionary(d, null, owner, it);
                break;
        }
    }

    private void VisitDictionary(PdfDictionary d, PdfStream? stream, int owner, Item it)
    {
        int page = it.Page;
        string? type = d.GetName("Type");
        string? subtype = d.GetName("Subtype");

        if (stream is not null)
        {
            CheckStream(stream, owner, page);
        }

        if (it.Key == "ColorSpace" && stream is null && type is null && subtype is null && d.GetName("ShadingType") is null)
        {
            // A Resources /ColorSpace dictionary: each value is a colour space.
            foreach (KeyValuePair<string, PdfObject> e in d.Entries)
            {
                UseColourSpace(_doc.Resolve(e.Value), owner, page, 0);
            }
        }

        if (it.Key == "ExtGState" && stream is null)
        {
            foreach (KeyValuePair<string, PdfObject> e in d.Entries)
            {
                if (_doc.Resolve(e.Value) is PdfDictionary gs)
                {
                    CheckExtGState(gs, e.Value is PdfReference er ? er.Number : owner, page);
                }
            }
        }

        if (it.Key == "SMask" && stream is null && d.ContainsKey("S") && d.Get("TR") is { } tr && !(tr is PdfName { Value: "Identity" }))
        {
            Add("6.4", "A soft-mask dictionary has a /TR entry other than Identity.", owner, page);
        }

        if (it.Key == "AA" && stream is null)
        {
            CheckAdditionalActions(d, owner, it.Parent, page);
        }
        else if (it.Key is "A" or "OpenAction" or "Next" && stream is null && d.ContainsKey("S") && subtype is null)
        {
            CheckAction(d, owner, page);
        }

        if (type == "Catalog")
        {
            CheckCatalog(d, owner);
        }
        else if (type == "Page")
        {
            CheckPage(d, owner, page);
        }
        else if (type == "Font" || (stream is null && subtype is "Type1" or "TrueType" or "Type0" or "Type3" or "MMType1" && d.ContainsKey("BaseFont")))
        {
            if (_fontDicts.Add(d))
            {
                CheckFont(d, owner, page);
            }
        }
        else if (type == "ExtGState")
        {
            // Reached directly (not through a resource dictionary).
            if (it.Key != "ExtGState")
            {
                CheckExtGState(d, owner, page);
            }
        }

        // Annotations count when they are on a page of the page tree; a stray /Annots elsewhere (a damaged tree) is not a page's.
        if (it.Key == "Annots" && stream is null && subtype is not null && it.Parent?.Reference is { } pr && _pageNumbers.Contains(pr.Number))
        {
            CheckAnnotation(d, owner, page);
        }
        else if (it.Key == "Popup" && stream is null && subtype == "Popup")
        {
            CheckAnnotation(d, owner, page);
        }

        if (stream is not null)
        {
            if (subtype == "Image")
            {
                CheckImage(stream, owner, page);
            }
            else if (subtype == "Form")
            {
                CheckForm(stream, owner, page);
            }
            else if (subtype == "PS")
            {
                Add("6.2.9", "A PostScript XObject is present.", owner, page);
            }
            else if (d.GetInt("PatternType") == 1)
            {
                ScanContent(stream, d.GetDictionary("Resources") is not null, owner, page);
            }
        }

        if (d.ContainsKey("EF") && stream is null)
        {
            _embeddedSpecs.Add((owner, d));
        }

        if (it.Key == "OCProperties")
        {
            CheckOptionalContent(d, owner);
        }

        if (stream is null && d.ContainsKey("FT") && (d.ContainsKey("A") || d.ContainsKey("AA")) && subtype != "Widget")
        {
            Add("6.4.1", "A form field has an /A or /AA entry.", owner, page);
        }

        // Children: everything except the back pointers that would walk the page tree upwards.
        int count = d.Count;
        var children = new List<KeyValuePair<string, PdfObject>>(count);
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
            string key = children[k].Key;
            _stack.Push(new Item(children[k].Value, owner, key, d, page));
        }
    }

    // ---- streams ----------------------------------------------------------------------------------------------------------------

    private void CheckStream(PdfStream s, int owner, int page)
    {
        PdfDictionary d = s.Dictionary;
        if (d.ContainsKey("F") || d.ContainsKey("FFilter") || d.ContainsKey("FDecodeParms"))
        {
            Add("6.1.7.1", "A stream refers to an external file (/F, /FFilter or /FDecodeParms).", owner, page);
        }

        IReadOnlyList<string> filters = s.FilterNames;
        for (int i = 0; i < filters.Count; i++)
        {
            string f = filters[i];
            if (f == "LZWDecode")
            {
                Add("6.1.7.2", "A stream uses the LZWDecode filter.", owner, page);
            }
            else if (f == "Crypt")
            {
                PdfObject? dp = d.Get("DecodeParms");
                PdfDictionary? p = dp as PdfDictionary ?? (dp is PdfArray dpa ? At(dpa, i) as PdfDictionary : null);
                if (p?.GetName("Name") is not (null or "Identity"))
                {
                    Add("6.1.7.2", "A stream uses the Crypt filter with a name other than Identity.", owner, page);
                }
            }
            else if (f is not ("FlateDecode" or "ASCIIHexDecode" or "ASCII85Decode" or "RunLengthDecode" or "CCITTFaxDecode" or "JBIG2Decode" or "DCTDecode" or "JPXDecode"))
            {
                Add("6.1.7.2", $"A stream uses the filter {f}, which PDF/A does not allow.", owner, page);
            }
        }
    }

    private void CheckImage(PdfStream s, int owner, int page)
    {
        PdfDictionary d = s.Dictionary;
        if (d.ContainsKey("Alternates"))
        {
            Add("6.2.8", "An image has an /Alternates entry.", owner, page);
        }

        if (d.ContainsKey("OPI"))
        {
            Add("6.2.8", "An image has an /OPI entry.", owner, page);
        }

        if (d.Get("Interpolate") is PdfBoolean { Value: true })
        {
            Add("6.2.8", "An image sets /Interpolate to true.", owner, page);
        }

        if (d.ContainsKey("BitsPerComponent") && !(d.GetBool("ImageMask") ? d.GetInt("BitsPerComponent") == 1 : d.GetInt("BitsPerComponent") is 1 or 2 or 4 or 8 or 16))
        {
            Add("6.2.8", $"An image has an unusable /BitsPerComponent {d.GetInt("BitsPerComponent")}.", owner, page);
        }

        CheckIntentName(d, owner, page);
        if (page >= 0 && d.Get("SMask") is PdfStream)
        {
            _transparentPages.Add(page);
        }
    }

    private void CheckIntentName(PdfDictionary d, int owner, int page)
    {
        if (d.GetName("Intent") is { } intent && intent is not ("RelativeColorimetric" or "AbsoluteColorimetric" or "Perceptual" or "Saturation"))
        {
            Add("6.2.6", $"The rendering intent {intent} is not one of the four standard values.", owner, page);
        }
    }

    private void CheckForm(PdfStream s, int owner, int page)
    {
        PdfDictionary d = s.Dictionary;
        if (d.ContainsKey("OPI") || d.ContainsKey("PS") || d.GetName("Subtype2") == "PS")
        {
            Add("6.2.9", "A form XObject has an /OPI or /PS entry, or a PostScript /Subtype2.", owner, page);
        }

        if (d.ContainsKey("Ref"))
        {
            Add("6.2.9", "A form XObject is a reference XObject (/Ref).", owner, page);
        }

        ScanContent(s, d.Get("Resources") is PdfDictionary, owner, page);
    }

    private void CheckExtGState(PdfDictionary gs, int owner, int page)
    {
        if (gs.GetName("RI") is { } ri && ri is not ("RelativeColorimetric" or "AbsoluteColorimetric" or "Perceptual" or "Saturation"))
        {
            Add("6.2.6", $"The rendering intent {ri} is not one of the four standard values.", owner, page);
        }

        if (page >= 0 && (gs.GetDouble("ca", 1) < 1 || gs.GetDouble("CA", 1) < 1 || (gs.Get("BM") is PdfName { Value: not ("Normal" or "Compatible") })
            || (gs.Get("SMask") is { } sm && !(sm is PdfName { Value: "None" }))))
        {
            _transparentPages.Add(page);
        }

        if (gs.ContainsKey("TR"))
        {
            Add("6.2.5", "A graphics state parameter dictionary has a /TR entry.", owner, page);
        }

        if (gs.Get("TR2") is { } tr2 && !(tr2 is PdfName { Value: "Default" }))
        {
            Add("6.2.5", "A graphics state parameter dictionary has a /TR2 entry other than Default.", owner, page);
        }

        if (gs.ContainsKey("HTP"))
        {
            Add("6.2.5", "A graphics state parameter dictionary has an /HTP entry.", owner, page);
        }

        if (gs.Get("HT") is PdfDictionary ht)
        {
            if (ht.GetInt("HalftoneType") is not (1 or 5) || ht.ContainsKey("HalftoneName"))
            {
                Add("6.2.5", "A halftone has a type other than 1 or 5, or a /HalftoneName.", owner, page);
            }
        }

        object? bm = gs.Get("BM");
        if (bm is PdfName bn && !s_blendModes.Contains(bn.Value))
        {
            Add("6.2.10", $"The blend mode {bn.Value} is not defined in ISO 32000-1.", owner, page);
        }
        else if (bm is PdfArray ba)
        {
            for (int i = 0; i < ba.Count; i++)
            {
                if (ba.GetName(i) is { } m && !s_blendModes.Contains(m))
                {
                    Add("6.2.10", $"The blend mode {m} is not defined in ISO 32000-1.", owner, page);
                }
            }
        }
    }

    // ---- content ----------------------------------------------------------------------------------------------------------------

    private void ScanContent(PdfStream s, bool hasResources, int owner, int page)
    {
        ReadOnlyMemory<byte> data;
        try
        {
            data = s.GetDecodedBytes(_ct);
        }
        catch (PdfException ex)
        {
            Add("6.1.7", "A content stream cannot be decoded: " + ex.Message, owner, page);
            return;
        }

        var lexer = new ContentLexer(data, _doc.Limits.MaxNestingDepth);
        int depth = 0;
        bool usesResources = false;
        bool reportedDepth = false;
        string? lastName = null;
        long operators = 0;
        while (true)
        {
            ContentToken tok = lexer.Next(out Operand operand, out ContentOp op);
            if (tok == ContentToken.End)
            {
                break;
            }

            if (tok == ContentToken.Operand)
            {
                if (operand.Kind == OperandKind.Name)
                {
                    lastName = operand.Name;
                }

                continue;
            }

            if (++operators > _doc.Limits.MaxOperatorsPerPage)
            {
                throw new PdfLimitException("A content stream has too many operators to check.");
            }

            switch (op)
            {
                case ContentOp.Save:
                    if (++depth > 28 && !reportedDepth)
                    {
                        reportedDepth = true;
                        Add("6.1.13", "Graphics states are nested deeper than 28 levels (q operators).", owner, page);
                    }

                    break;
                case ContentOp.Restore:
                    if (depth > 0)
                    {
                        depth--;
                    }

                    break;
                case ContentOp.FillGray:
                case ContentOp.StrokeGray:
                    NoteGray(owner, page);
                    break;
                case ContentOp.FillRgb:
                case ContentOp.StrokeRgb:
                    NoteRgb(owner, page);
                    break;
                case ContentOp.FillCmyk:
                case ContentOp.StrokeCmyk:
                    NoteCmyk(owner, page);
                    break;
                case ContentOp.FillSpaceName:
                case ContentOp.StrokeSpaceName:
                    if (lastName is "DeviceRGB" or "DeviceGray" or "DeviceCMYK" or "Pattern")
                    {
                        UseColourSpace(new PdfName(lastName), owner, page, 0);
                    }
                    else
                    {
                        usesResources = true;
                    }

                    break;
                case ContentOp.Do:
                case ContentOp.TextFont:
                case ContentOp.SetExtGState:
                case ContentOp.Shading:
                    usesResources = true;
                    break;
                case ContentOp.BeginInline:
                    ScanInlineImage(lexer, owner, page);
                    break;
                case ContentOp.Unknown:
                    if (!Limited("op"))
                    {
                        Add("6.2.2", $"The content stream contains the operator '{lexer.LastKeyword}', which ISO 32000-1 does not define.", owner, page);
                    }

                    break;
            }
        }

        if (usesResources && !hasResources)
        {
            Add("6.2.2", "A content stream uses resources but has no /Resources dictionary of its own.", owner, page);
        }
    }

    private void ScanInlineImage(ContentLexer lexer, int owner, int page)
    {
        PdfDictionary? dict = lexer.ReadInlineImageDictionary();
        if (dict is null)
        {
            return;
        }

        if (dict.Get("ColorSpace") is { } cs)
        {
            UseColourSpace(cs, owner, page, 0);
        }

        if (dict.Get("Interpolate") is PdfBoolean { Value: true })
        {
            Add("6.2.8", "An inline image sets /Interpolate (I) to true.", owner, page);
        }

        PdfObject? f = dict.Get("Filter");
        IEnumerable<string> names = f is PdfName fn ? [fn.Value] : f is PdfArray fa ? fa.Items.OfType<PdfName>().Select(static x => x.Value) : [];
        foreach (string name in names)
        {
            if (name is "LZWDecode" or "Crypt")
            {
                Add("6.1.10", $"An inline image uses the filter {name}.", owner, page);
            }
        }

        lexer.ReadInlineImageData(-1, out _);
    }

    // ---- colour -----------------------------------------------------------------------------------------------------------------

    private void NoteRgb(int owner, int page)
    {
        if (!_rgb)
        {
            _rgb = true;
            _rgbOwner = owner;
            _rgbPage = page;
        }
    }

    private void NoteGray(int owner, int page)
    {
        if (!_gray)
        {
            _gray = true;
            _grayOwner = owner;
            _grayPage = page;
        }
    }

    private void NoteCmyk(int owner, int page)
    {
        if (!_cmyk)
        {
            _cmyk = true;
            _cmykOwner = owner;
            _cmykPage = page;
        }
    }

    private void UseColourSpace(PdfObject? cs, int owner, int page, int depth)
    {
        if (cs is null || depth > 8)
        {
            return;
        }

        switch (cs)
        {
            case PdfName n:
                switch (n.Value)
                {
                    case "DeviceRGB" or "RGB":
                        NoteRgb(owner, page);
                        break;
                    case "DeviceGray" or "G":
                        NoteGray(owner, page);
                        break;
                    case "DeviceCMYK" or "CMYK":
                        NoteCmyk(owner, page);
                        break;
                }

                break;
            case PdfArray a when a.Count > 0 && a.GetName(0) is { } family:
                switch (family)
                {
                    case "Indexed" or "I":
                        UseColourSpace(At(a, 1), owner, page, depth + 1);
                        break;
                    case "Separation":
                        UseColourSpace(At(a, 2), owner, page, depth + 1);
                        break;
                    case "DeviceN":
                        UseColourSpace(At(a, 2), owner, page, depth + 1);
                        break;
                    case "Pattern" when a.Count > 1:
                        UseColourSpace(At(a, 1), owner, page, depth + 1);
                        break;
                    case "ICCBased":
                        CheckIccInput(At(a, 1) as PdfStream, owner, page);
                        break;
                    case "DeviceRGB" or "DeviceGray" or "DeviceCMYK":
                        UseColourSpace(new PdfName(family), owner, page, depth + 1);
                        break;
                }

                break;
        }
    }

    private static PdfObject? At(PdfArray a, int i) => i >= 0 && i < a.Count ? a[i] : null;

    private void CheckIccInput(PdfStream? profile, int owner, int page)
    {
        if (profile is null)
        {
            Add("6.2.4.2", "An ICCBased colour space has no profile stream.", owner, page);
            return;
        }

        int num = profile.Reference?.Number ?? owner;
        if (!_profilesChecked.Add(num))
        {
            return;
        }

        ReadOnlyMemory<byte> data;
        try
        {
            data = profile.GetDecodedBytes(_ct);
        }
        catch (PdfException)
        {
            Add("6.2.4.2", "An ICC profile cannot be decoded.", num, page);
            return;
        }

        ReadOnlySpan<byte> h = data.Span;
        if (h.Length < 128 || !h.Slice(36, 4).SequenceEqual("acsp"u8))
        {
            Add("6.2.4.2", "An ICCBased colour space holds data that is not an ICC profile.", num, page);
            return;
        }

        string cls = Encoding.ASCII.GetString(h.Slice(12, 4));
        string space = Encoding.ASCII.GetString(h.Slice(16, 4));
        if (cls is not ("prtr" or "mntr" or "scnr" or "spac") || space is not ("RGB " or "CMYK" or "GRAY" or "Lab ") || h[8] >= 5)
        {
            Add("6.2.4.2", $"An ICC profile is not usable (class '{cls}', space '{space}', version {h[8]}).", num, page);
        }
    }

    // 6.2.10: without an output intent, a page that uses transparency needs a blending colour space in its page group.
    private void CheckTransparency()
    {
        if (_intentSpace is not null)
        {
            return;
        }

        PdfPageCollection pages = _doc.Pages;
        foreach (int i in _transparentPages)
        {
            if (i < pages.Count && pages[i].Dictionary.GetDictionary("Group")?.Get("CS") is null)
            {
                Add("6.2.10", "The page uses transparency but the file has no PDF/A output intent and the page group has no /CS.", pages[i].Dictionary.Reference?.Number ?? -1, i);
            }
        }
    }

    private void CheckColourUse()
    {
        if (_rgb && _intentSpace != "RGB ")
        {
            Add("6.2.4.3", _intentSpace is null
                ? "DeviceRGB is used but the file has no PDF/A output intent."
                : $"DeviceRGB is used but the output intent profile is {_intentSpace.Trim()}, not RGB.", _rgbOwner, _rgbPage);
        }

        if (_cmyk && _intentSpace != "CMYK")
        {
            Add("6.2.4.3", _intentSpace is null
                ? "DeviceCMYK is used but the file has no PDF/A output intent."
                : $"DeviceCMYK is used but the output intent profile is {_intentSpace.Trim()}, not CMYK. OnePDF has no CMYK profile to offer.", _cmykOwner, _cmykPage);
        }

        if (_gray && _intentSpace is null)
        {
            Add("6.2.4.3", "DeviceGray is used but the file has no PDF/A output intent.", _grayOwner, _grayPage);
        }
    }

    // ---- fonts ------------------------------------------------------------------------------------------------------------------

    private void CheckFont(PdfDictionary f, int owner, int page)
    {
        string? subtype = f.GetName("Subtype");
        string baseFont = f.GetName("BaseFont") ?? "(unnamed)";
        if (subtype is null or not ("Type1" or "MMType1" or "TrueType" or "Type3" or "Type0"))
        {
            if (subtype is not ("CIDFontType0" or "CIDFontType2"))
            {
                Add("6.2.11.2", $"The font {baseFont} has a missing or unknown /Subtype.", owner, page);
            }

            return;
        }

        if (subtype != "Type3" && f.GetName("BaseFont") is null)
        {
            Add("6.2.11.2", "A font dictionary has no /BaseFont.", owner, page);
        }

        if (subtype == "Type0")
        {
            CheckType0(f, baseFont, owner, page);
        }
        else if (subtype == "Type3")
        {
            CheckType3(f, owner, page);
        }
        else
        {
            CheckSimpleFont(f, subtype, baseFont, owner, page);
        }

        if (_options.Conformance == PdfAConformance.U)
        {
            CheckUnicode(f, subtype, baseFont, owner, page);
        }
    }

    private void CheckSimpleFont(PdfDictionary f, string subtype, string baseFont, int owner, int page)
    {
        PdfDictionary? fd = f.GetDictionary("FontDescriptor");
        bool embedded = fd is not null && HasFontFile(fd);
        if (!embedded)
        {
            Add("6.2.11.4.1", $"The font {baseFont} is not embedded.", owner, page);
        }
        else
        {
            CheckFontFileSubtype(fd!, owner, page);
            if (!f.ContainsKey("FirstChar") || !f.ContainsKey("LastChar") || f.Get("Widths") is not PdfArray w || w.Count != f.GetInt("LastChar") - f.GetInt("FirstChar") + 1)
            {
                Add("6.2.11.2", $"The font {baseFont} has a missing or wrong-sized /FirstChar, /LastChar or /Widths.", owner, page);
            }
        }

        if (subtype == "TrueType" && fd is not null)
        {
            long flags = fd.GetInt("Flags");
            bool symbolic = (flags & 4) != 0;
            PdfObject? enc = f.Get("Encoding");
            if (symbolic && enc is not null)
            {
                Add("6.2.11.6", $"The symbolic TrueType font {baseFont} has an /Encoding entry.", owner, page);
            }
            else if (!symbolic)
            {
                string? baseEnc = enc is PdfName en ? en.Value : (enc as PdfDictionary)?.GetName("BaseEncoding");
                if (baseEnc is not ("WinAnsiEncoding" or "MacRomanEncoding"))
                {
                    Add("6.2.11.6", $"The non-symbolic TrueType font {baseFont} needs WinAnsiEncoding or MacRomanEncoding.", owner, page);
                }
            }
        }
    }

    // A reference that leads nowhere reads as null and is dropped when the file is written, so only a real stream counts.
    private static bool HasFontFile(PdfDictionary fd) =>
        fd.GetStream("FontFile") is not null || fd.GetStream("FontFile2") is not null || fd.GetStream("FontFile3") is not null;

    private void CheckFontFileSubtype(PdfDictionary fd, int owner, int page)
    {
        if (fd.GetStream("FontFile3") is { } ff3 && ff3.Dictionary.GetName("Subtype") is not ("Type1C" or "CIDFontType0C" or "OpenType"))
        {
            Add("6.2.11.2", "An embedded font program of type FontFile3 has an /Subtype other than Type1C, CIDFontType0C or OpenType.", owner, page);
        }
    }

    private void CheckType0(PdfDictionary f, string baseFont, int owner, int page)
    {
        PdfObject? enc = f.Get("Encoding");
        if (enc is PdfName en && !s_predefinedCMaps.Contains(en.Value))
        {
            Add("6.2.11.3", $"The font {baseFont} uses the CMap {en.Value}, which is neither predefined nor embedded.", owner, page);
        }
        else if (enc is null)
        {
            Add("6.2.11.3", $"The font {baseFont} has no /Encoding.", owner, page);
        }

        if (f.Get("DescendantFonts") is not PdfArray { Count: 1 } desc || desc[0] is not PdfDictionary cid)
        {
            Add("6.2.11.2", $"The Type 0 font {baseFont} has no single descendant font.", owner, page);
            return;
        }

        PdfDictionary? fd = cid.GetDictionary("FontDescriptor");
        bool embedded = fd is not null && HasFontFile(fd);
        if (!embedded)
        {
            Add("6.2.11.4.1", $"The font {baseFont} is not embedded.", owner, page);
        }
        else
        {
            CheckFontFileSubtype(fd!, owner, page);
        }

        if (cid.GetName("Subtype") == "CIDFontType2" && embedded && cid.Get("CIDToGIDMap") is null)
        {
            Add("6.2.11.3", $"The CIDFontType2 font {baseFont} has no /CIDToGIDMap.", owner, page);
        }
    }

    private void CheckType3(PdfDictionary f, int owner, int page)
    {
        if (f.Get("CharProcs") is PdfDictionary procs)
        {
            foreach (KeyValuePair<string, PdfObject> e in procs.Entries)
            {
                if (_doc.Resolve(e.Value) is PdfStream ps)
                {
                    ScanContent(ps, true, e.Value is PdfReference r ? r.Number : owner, page);
                }
            }
        }
    }

    // 6.2.11.7: level U needs a Unicode mapping for every font, unless the font is one of the kinds the clause exempts.
    private void CheckUnicode(PdfDictionary f, string subtype, string baseFont, int owner, int page)
    {
        if (f.Get("ToUnicode") is PdfStream)
        {
            return;
        }

        if (subtype == "Type0")
        {
            if (f.Get("DescendantFonts") is PdfArray { Count: > 0 } d && d[0] is PdfDictionary cid
                && cid.GetDictionary("CIDSystemInfo") is { } info
                && info.GetString("Registry")?.GetText() == "Adobe"
                && info.GetString("Ordering")?.GetText() is "GB1" or "CNS1" or "Japan1" or "Korea1")
            {
                return;
            }
        }
        else
        {
            PdfObject? enc = f.Get("Encoding");
            string? baseEnc = enc is PdfName en ? en.Value : (enc as PdfDictionary)?.GetName("BaseEncoding");
            if (baseEnc is "WinAnsiEncoding" or "MacRomanEncoding" or "MacExpertEncoding")
            {
                return;
            }

            long flags = f.GetDictionary("FontDescriptor")?.GetInt("Flags") ?? 0;
            bool symbolic = (flags & 4) != 0;
            if (subtype == "TrueType" && !symbolic)
            {
                return;
            }

            if (subtype is "Type1" or "MMType1" or "Type3" && !symbolic && DifferencesInGlyphList(enc as PdfDictionary))
            {
                return;
            }
        }

        Add("6.2.11.7", $"The font {baseFont} has no /ToUnicode map and is not one of the kinds that need none, so PDF/A level U cannot be claimed.", owner, page);
    }

    private static bool DifferencesInGlyphList(PdfDictionary? enc)
    {
        if (enc is null)
        {
            return true; // the built-in StandardEncoding names are all in the Adobe Glyph List
        }

        if (enc.Get("Differences") is not PdfArray diff)
        {
            return true;
        }

        for (int i = 0; i < diff.Count; i++)
        {
            if (diff.GetName(i) is { } name && !GlyphList.TryGetUnicode(name, out _))
            {
                return false;
            }
        }

        return true;
    }

    private void CheckCreatedFonts()
    {
        foreach ((int number, string name) in CreationState.NotdefFonts(_doc))
        {
            Add("6.2.11.8", $"Text drawn with the font {name} uses glyph 0 (.notdef) for characters the font lacks.", number, -1);
        }
    }

    // ---- annotations and actions ------------------------------------------------------------------------------------------------

    private void CheckAnnotation(PdfDictionary a, int owner, int page)
    {
        string subtype = a.GetName("Subtype") ?? string.Empty;
        if (!s_annotationTypes.Contains(subtype))
        {
            Add("6.3.1", $"The annotation type '{subtype}' is not allowed.", owner, page);
            return;
        }

        if (subtype != "Popup")
        {
            if (!a.ContainsKey("F"))
            {
                Add("6.3.2", "An annotation has no /F flags.", owner, page);
            }
            else
            {
                long f = a.GetInt("F");
                if ((f & 1) != 0 || (f & 2) != 0 || (f & 4) == 0 || (f & 32) != 0 || (f & 256) != 0)
                {
                    Add("6.3.2", "An annotation's /F flags must have Print set and Invisible, Hidden, NoView and ToggleNoView clear.", owner, page);
                }
            }
        }

        PdfRectangle? rect = PdfRectangle.From(a.Get("Rect"));
        bool empty = rect is { } r0 && r0.Width == 0 && r0.Height == 0;
        PdfDictionary? ap = a.GetDictionary("AP");
        if (!empty && subtype is not ("Popup" or "Link") && ap is null)
        {
            Add("6.3.3", $"The {subtype} annotation has no appearance dictionary.", owner, page);
        }

        if (ap is not null)
        {
            foreach (string key in ap.Keys)
            {
                if (key != "N")
                {
                    Add("6.3.3", $"An appearance dictionary has the entry /{key}; only /N is allowed.", owner, page);
                }
            }

            PdfObject? n = ap.Get("N");
            bool button = subtype == "Widget" && InheritedName(a, "FT") == "Btn";
            if (button)
            {
                if (n is not PdfDictionary || n is PdfStream)
                {
                    Add("6.3.3", "A button widget's /N must be a dictionary of appearance states.", owner, page);
                }
            }
            else if (n is not PdfStream)
            {
                Add("6.3.3", "An annotation's /N appearance must be a stream.", owner, page);
            }
        }

        if (subtype == "Widget" && (a.ContainsKey("A") || a.ContainsKey("AA")))
        {
            Add("6.4.1", "A widget annotation has an /A or /AA entry.", owner, page);
        }

        if (subtype == "FileAttachment" && _options.Part == PdfAPart.Part2)
        {
            Add("6.8", "A file attachment annotation is present; PDF/A-2 files written by OnePDF carry no embedded files.", owner, page);
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

    private void CheckAction(PdfDictionary action, int owner, int page)
    {
        string? s = action.GetName("S");
        if (s is null || !s_allowedActions.Contains(s))
        {
            Add("6.5.1", $"The action type {s ?? "(missing)"} is not allowed.", owner, page);
        }
        else if (s == "Named" && !s_allowedNamed.Contains(action.GetName("N") ?? string.Empty))
        {
            Add("6.5.1", $"The named action {action.GetName("N") ?? "(missing)"} is not allowed.", owner, page);
        }
    }

    private void CheckAdditionalActions(PdfDictionary aa, int owner, PdfDictionary? parent, int page)
    {
        string? ptype = parent?.GetName("Type");
        bool widgetOrField = parent is not null && (parent.GetName("Subtype") == "Widget" || parent.ContainsKey("FT"));
        if (aa.Count > 0 && (ptype is "Catalog" or "Page" || widgetOrField))
        {
            Add("6.5.2", "An additional-actions (/AA) dictionary is present on the catalog, a page, a widget or a form field.", owner, page);
        }

        foreach (KeyValuePair<string, PdfObject> e in aa.Entries)
        {
            if (_doc.Resolve(e.Value) is PdfDictionary act)
            {
                CheckAction(act, e.Value is PdfReference r ? r.Number : owner, page);
            }
        }
    }

    // ---- catalog and pages ------------------------------------------------------------------------------------------------------

    private void CheckCatalog(PdfDictionary catalog, int owner)
    {
        PdfStream? meta = catalog.GetStream("Metadata");
        if (meta is null || meta.Dictionary.GetName("Type") != "Metadata" || meta.Dictionary.GetName("Subtype") != "XML")
        {
            Add("6.6.2.1", "The catalog has no /Metadata stream with /Type /Metadata and /Subtype /XML.", -1, -1);
        }
        else
        {
            CheckXmp(meta);
        }

        if (catalog.ContainsKey("NeedsRendering"))
        {
            Add("6.4.2", "The catalog has a /NeedsRendering entry.", owner, -1);
        }

        if (catalog.ContainsKey("AA"))
        {
            Add("6.5.2", "The catalog has an /AA entry.", owner, -1);
        }

        if (catalog.GetDictionary("AcroForm") is { } form)
        {
            if (form.GetBool("NeedAppearances"))
            {
                Add("6.4.1", "The interactive form has /NeedAppearances true.", -1, -1);
            }

            if (form.ContainsKey("XFA"))
            {
                Add("6.4.2", "The interactive form has an /XFA entry.", -1, -1);
            }
        }

        if (catalog.GetDictionary("Names") is { } names && names.ContainsKey("JavaScript"))
        {
            Add("6.5.1", "The document has a JavaScript name tree.", -1, -1);
        }

        if (catalog.GetDictionary("Perms") is { } perms)
        {
            foreach (string key in perms.Keys)
            {
                if (key is not ("UR3" or "DocMDP"))
                {
                    Add("6.1.12", $"The permissions dictionary has the entry /{key}; only /UR3 and /DocMDP are allowed.", -1, -1);
                }
            }
        }

        if (catalog.GetDictionary("Names")?.GetDictionary("EmbeddedFiles") is not null && _options.Part == PdfAPart.Part2)
        {
            Add("6.8", "The document has embedded files; PDF/A-2 files written by OnePDF carry none. Use PDF/A-3 for attachments.", -1, -1);
        }
    }

    private void CheckXmp(PdfStream meta)
    {
        int num = meta.Reference?.Number ?? -1;
        if (meta.FilterNames.Count > 0)
        {
            Add("6.6.2.1", "The metadata stream has a filter; the packet is written unfiltered.", num, -1);
        }

        XmpMetadata xmp;
        try
        {
            xmp = new XmpMetadata(meta.GetDecodedBytes(_ct));
        }
        catch (PdfException ex)
        {
            Add("6.6.2.1", "The metadata stream cannot be read: " + ex.Message, num, -1);
            return;
        }

        if (!xmp.IsWellFormed)
        {
            Add("6.6.2.1", "The XMP packet is not well-formed XML.", num, -1);
            return;
        }

        string wantConf = _options.Conformance == PdfAConformance.B ? "B" : "U";
        if (xmp.PdfAPart != (int)_options.Part || xmp.PdfAConformance != wantConf)
        {
            Add("6.6.4", $"The XMP packet does not identify the file as {_options.Name} (pdfaid:part={xmp.PdfAPart?.ToString() ?? "-"}, pdfaid:conformance={xmp.PdfAConformance ?? "-"}).", num, -1);
        }
    }

    private void CheckPage(PdfDictionary page, int owner, int pageIndex)
    {
        foreach (string box in new[] { "MediaBox", "CropBox", "BleedBox", "TrimBox", "ArtBox" })
        {
            if (page.Get(box) is PdfArray && PdfRectangle.From(page.Get(box)) is { } r && (r.Width < 3 || r.Width > 14400 || r.Height < 3 || r.Height > 14400))
            {
                Add("6.1.13", $"The {box} is {r.Width:0.##} by {r.Height:0.##} units; PDF/A allows 3 to 14400.", owner, pageIndex);
            }
        }

        if (page.ContainsKey("AA"))
        {
            Add("6.5.2", "The page has an /AA entry.", owner, pageIndex);
        }

        PdfObject? contents = page.Get("Contents");
        var streams = new List<PdfStream>();
        if (contents is PdfStream cs)
        {
            streams.Add(cs);
        }
        else if (contents is PdfArray ca)
        {
            for (int i = 0; i < ca.Count; i++)
            {
                if (ca[i] is PdfStream s)
                {
                    streams.Add(s);
                }
            }
        }

        foreach (PdfStream s in streams)
        {
            ScanContent(s, true, s.Reference?.Number ?? owner, pageIndex);
        }
    }

    private void CheckOptionalContent(PdfDictionary ocp, int owner)
    {
        var configs = new List<PdfDictionary>();
        if (ocp.GetDictionary("D") is { } d)
        {
            configs.Add(d);
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

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (PdfDictionary c in configs)
        {
            if (c.GetString("Name") is not { } nm)
            {
                Add("6.9", "An optional content configuration has no /Name.", owner, -1);
            }
            else if (!names.Add(nm.GetText()))
            {
                Add("6.9", $"The optional content configuration name '{nm.GetText()}' is used twice.", owner, -1);
            }

            if (c.ContainsKey("AS"))
            {
                Add("6.9", "An optional content configuration has an /AS entry.", owner, -1);
            }
        }
    }

    // ---- embedded files (part 3) ------------------------------------------------------------------------------------------------

    private void CheckEmbeddedSpecs()
    {
        foreach ((int owner, PdfDictionary spec) in _embeddedSpecs)
        {
            if (_options.Part == PdfAPart.Part2)
            {
                Add("6.8", "The file contains an embedded file; PDF/A-2 files written by OnePDF carry none. Use PDF/A-3 for attachments.", owner, -1);
                continue;
            }

            if (spec.GetString("F") is null || spec.GetString("UF") is null)
            {
                Add("6.8", "A file specification needs both /F and /UF.", owner, -1);
            }

            if (spec.GetName("AFRelationship") is not { } rel || !s_relationships.Contains(rel))
            {
                Add("6.8", "A file specification needs a valid /AFRelationship.", owner, -1);
            }

            PdfDictionary? ef = spec.GetDictionary("EF");
            PdfStream? file = ef?.GetStream("F");
            if (file is null)
            {
                Add("6.8", "A file specification's /EF has no /F stream.", owner, -1);
            }
            else
            {
                if (file.Dictionary.GetName("Subtype") is null)
                {
                    Add("6.8", "An embedded file stream has no /Subtype (MIME type).", file.Reference?.Number ?? owner, -1);
                }

                if (file.Dictionary.GetDictionary("Params")?.GetString("ModDate") is null)
                {
                    Add("6.8", "An embedded file stream has no /Params /ModDate.", file.Reference?.Number ?? owner, -1);
                }
            }

            if (owner >= 0 && !_afTargets.Contains(owner))
            {
                Add("6.8", "An embedded file is not associated with the document through an /AF array.", owner, -1);
            }
        }
    }
}
