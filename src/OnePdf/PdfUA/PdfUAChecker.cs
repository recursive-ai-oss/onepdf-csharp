using OnePdf.Content;

namespace OnePdf.PdfUA;

/// <summary>
/// Checks a document against the requirements of ISO 14289-1 (PDF/UA-1) that can be verified from its structure: metadata and
/// identification, the tagged structure (types, headings, tables, lists, figures, links), that every painted mark is tagged content
/// or an artifact, annotations, fonts and security. It reports; it changes nothing. A clean result is not proof of conformance.
/// </summary>
internal sealed class PdfUAChecker
{
    private const int MaxViolations = 2000;
    private const int MaxIssuesPerKind = 20;

    // The structure types of ISO 32000-1 14.8.4 (Tables 333 to 340); PDF/UA-1 allows these and role-mapped custom types (7.1).
    private static readonly HashSet<string> s_standardTypes = new(StringComparer.Ordinal)
    {
        "Document", "Part", "Art", "Sect", "Div", "BlockQuote", "Caption", "TOC", "TOCI", "Index", "NonStruct", "Private",
        "P", "H", "H1", "H2", "H3", "H4", "H5", "H6", "L", "LI", "Lbl", "LBody",
        "Table", "TR", "TH", "TD", "THead", "TBody", "TFoot", "Span", "Quote", "Note", "Reference", "BibEntry", "Code", "Link", "Annot",
        "Ruby", "RB", "RT", "RP", "Warichu", "WT", "WP", "Figure", "Formula", "Form",
    };

    private readonly PdfDocument _doc;
    private readonly bool _part2;
    private readonly bool _saving;
    private readonly bool _structureOnly;
    private readonly CancellationToken _ct;
    private readonly List<PdfUAViolation> _violations = [];
    private readonly Dictionary<string, int> _perKind = new(StringComparer.Ordinal);
    private bool _truncated;

    private sealed class Node
    {
        public PdfDictionary Dict = null!;
        public string Type = string.Empty;
        public string? Role;
        public int Parent = -1;
        public List<int> Kids = [];
        public int PageObject = -1;
        public int Number = -1;
        public string Ns = string.Empty;
        public int ContentItems; // marked-content references and integer MCIDs held directly
    }

    private readonly List<Node> _nodes = [];
    private readonly HashSet<(int Page, int Mcid)> _treeMcids = [];
    private readonly HashSet<(int Stream, int Mcid)> _stmMcids = [];
    private readonly Dictionary<PdfStream, FormScan> _formScans = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, int> _objrParent = []; // annotation object number -> node index of its structure element

    /// <param name="doc">The document.</param>
    /// <param name="structureOnly">Only the findings PDF/A level A shares with PDF/UA (tagging, structure types, figures, language).</param>
    /// <param name="ct">Checked between pages and nodes.</param>
    /// <param name="part">The part of ISO 14289 to check against.</param>
    /// <param name="saving">True when the document is being prepared for a save, which writes the PDF 2.0 header that part 2 needs.</param>
    public PdfUAChecker(PdfDocument doc, bool structureOnly, CancellationToken ct, PdfUAPart part = PdfUAPart.Part1, bool saving = false)
    {
        _part2 = part == PdfUAPart.Part2;
        _saving = saving;
        _doc = doc;
        _structureOnly = structureOnly;
        _ct = ct;
    }

    public IReadOnlyList<PdfUAViolation> Run()
    {
        PdfDictionary catalog = _doc.Catalog;
        bool hasTree = Walk();
        CheckCatalog(catalog, hasTree);
        if (hasTree)
        {
            CheckStructure();
        }

        CheckPages(hasTree);
        if (_part2)
        {
            CheckDestinations();
        }

        if (!_structureOnly)
        {
            CheckSecurity();
            CheckContentLoss();
        }

        return _violations;
    }

    // A save must not drop what it cannot read (see ContentLossScan); the PDF/A checker reports the same findings.
    private void CheckContentLoss()
    {
        foreach (PdfA.ContentLoss loss in PdfA.ContentLossScan.Run(_doc, _ct))
        {
            if (loss.Kind == PdfA.LossKind.Font)
            {
                Add("31", "7.21.4.1", loss.Message, loss.ObjectNumber, loss.PageIndex);
            }
            else
            {
                Add("01", "7.1", loss.Message, loss.ObjectNumber, loss.PageIndex);
            }
        }
    }

    private bool _walked;

    // Reads the structure tree once; false when the document has none.
    private bool Walk()
    {
        PdfDictionary? root = _doc.Catalog.GetDictionary("StructTreeRoot");
        if (root is not null && !_walked)
        {
            WalkTree(root);
        }

        _walked = true;
        return root is not null;
    }

    /// <summary>The /Alt text of the structure element that owns the annotation with this object number, or null.</summary>
    public string? AlternateTextOf(int annotationObject)
    {
        Walk();
        return _objrParent.TryGetValue(annotationObject, out int node) ? Text(_nodes[node].Dict, "Alt") ?? Text(_nodes[node].Dict, "ActualText") : null;
    }

    private void Add(string checkpoint, string clause, string message, int owner = -1, int page = -1, string? aClause = null, string? c2 = null)
    {
        if (_violations.Count >= MaxViolations)
        {
            if (!_truncated)
            {
                _truncated = true;
                _violations.Add(new PdfUAViolation("-", "-", $"More than {MaxViolations} violations; the rest are not listed.", -1, -1));
            }

            return;
        }

        string shown = _part2 ? c2 ?? Part2Clause(checkpoint) : _structureOnly ? aClause ?? clause : clause;
        _violations.Add(new PdfUAViolation(checkpoint, shown, message, owner, page) { Part = _part2 ? 2 : 1 });
    }

    // The clause of ISO 14289-2 for a finding. Numbers come from the clause each rule is reported under by veraPDF's ua2 profile where it
    // checks the rule; a rule it does not check is filed under the nearest heading.
    private static string Part2Clause(string checkpoint) => checkpoint switch
    {
        "01" => "8.2.2",
        "02" or "09" => "8.2.4",
        "06" => "8.11.1",
        "07" => "8.11.2",
        "11" => "8.4.4",
        "13" => "8.2.5.28.2",
        "15" => "8.2.5.26",
        "16" => "Table 5",
        "26" => "8",
        "28" => "8.9",
        "31" => "8.4.5.5.1",
        "10" => "8.4.5",
        _ => "8",
    };

    // The same kind of problem a hundred times would bury the list: list the first few and count the rest.
    private bool Limited(string kind)
    {
        _perKind.TryGetValue(kind, out int n);
        _perKind[kind] = n + 1;
        return n >= MaxIssuesPerKind;
    }

    // ---- catalog and metadata ---------------------------------------------------------------------------------------------------

    private void CheckCatalog(PdfDictionary catalog, bool hasTree)
    {
        PdfDictionary? mark = catalog.GetDictionary("MarkInfo");
        if (mark is null || !mark.GetBool("Marked"))
        {
            Add("06", "7.1", "The catalog has no /MarkInfo dictionary with /Marked true: the document is not declared as tagged.", aClause: "6.7.2.2", c2: "6.2");
        }
        else if (mark.GetBool("Suspects"))
        {
            Add("06", "7.1", "The /MarkInfo dictionary has /Suspects true.", c2: "6.2");
        }

        if (!hasTree)
        {
            Add("01", "7.1", "The document has no logical structure tree (/StructTreeRoot): its content is not tagged.", aClause: "6.7.3.3", c2: "8.2");
        }

        string? lang = catalog.GetString("Lang")?.GetText();
        if (string.IsNullOrWhiteSpace(lang))
        {
            Add("11", "7.2", "The catalog has no /Lang entry naming the document's natural language.", aClause: "6.7.4");
        }
        else if (!IsLanguageTag(lang))
        {
            Add("11", "7.2", $"The catalog's /Lang value '{lang}' is not a well-formed language tag.", aClause: "6.7.4");
        }

        if (_structureOnly)
        {
            return;
        }

        if (_part2 && !_saving && !IsPdf20(_doc.Version))
        {
            Add("-", "5", $"The file is PDF {_doc.Version ?? "?"}, but PDF/UA-2 builds on PDF 2.0 (ISO 32000-2).", c2: "-");
        }

        PdfStream? meta = catalog.GetStream("Metadata");
        if (meta is null)
        {
            Add("06", "7.1", "The catalog has no /Metadata stream, so the file carries no PDF/UA identification and no title.");
        }
        else
        {
            XmpMetadata? xmp = _doc.Xmp;
            if (xmp is not { IsWellFormed: true })
            {
                Add("06", "7.1", "The XMP metadata packet is missing or is not well-formed XML.", meta.Reference?.Number ?? -1);
            }
            else
            {
                int wantPart = _part2 ? 2 : 1;
                if (xmp.PdfUAPart != wantPart)
                {
                    Add("06", "7.1", $"The XMP packet does not carry pdfuaid:part {wantPart} (found {xmp.PdfUAPart?.ToString() ?? "none"}).", meta.Reference?.Number ?? -1, c2: "5");
                }
                else if (_part2 && xmp.PdfUARevision != PdfUA2.Revision)
                {
                    Add("06", "7.1", $"The XMP packet does not carry pdfuaid:rev {PdfUA2.Revision} (found {xmp.PdfUARevision?.ToString() ?? "none"}).", meta.Reference?.Number ?? -1, c2: "5");
                }

                if (string.IsNullOrWhiteSpace(xmp.Title))
                {
                    Add("06", "7.1", "The XMP packet has no dc:title: a PDF/UA file needs a title.", meta.Reference?.Number ?? -1);
                }
            }
        }

        if (!(catalog.GetDictionary("ViewerPreferences")?.GetBool("DisplayDocTitle") ?? false))
        {
            Add("07", "7.1", "The catalog's /ViewerPreferences does not set /DisplayDocTitle true, so viewers would show the file name instead of the title.");
        }
    }

    // ISO 14289-2 8.8: every destination inside the document is a structure destination (ISO 32000-2 12.3.2.3), whose first item is a
    // structure element rather than a page. Only the destinations the document uses are looked at: link annotations, GoTo actions and
    // outline items, named or explicit.
    private void CheckDestinations()
    {
        int reported = 0;

        void Check(PdfObject? dest, int owner, int pageIndex)
        {
            if (dest is null || reported >= MaxIssuesPerKind)
            {
                return;
            }

            PdfDestination? d = _doc.ResolveDestination(dest, 0);
            if (d is { IsRemotePage: false, PageIndex: >= 0, IsStructureDestination: false })
            {
                reported++;
                Add("28", "7.18.1", "A link or outline item goes to a page, but PDF/UA-2 needs a structure destination (the destination names the structure element, not the page).", owner, pageIndex, c2: "8.8");
            }
        }

        void CheckAction(PdfDictionary? action, int owner, int pageIndex)
        {
            for (int depth = 0; action is not null && depth < 8; depth++)
            {
                if (action.GetName("S") == "GoTo" && action.GetRaw("SD") is null)
                {
                    Check(action.GetRaw("D"), owner, pageIndex);
                }

                action = action.GetDictionary("Next");
            }
        }

        PdfPageCollection pages = _doc.Pages;
        for (int p = 0; p < pages.Count; p++)
        {
            _ct.ThrowIfCancellationRequested();
            PdfDictionary pd;
            try
            {
                pd = pages[p].Dictionary;
            }
            catch (PdfFormatException)
            {
                continue; // already reported by CheckPages
            }

            if (pd.Get("Annots") is not PdfArray annots)
            {
                continue;
            }

            for (int i = 0; i < annots.Count; i++)
            {
                if (annots[i] is PdfDictionary a && a.GetName("Subtype") == "Link")
                {
                    int num = a.Reference?.Number ?? -1;
                    Check(a.GetRaw("Dest"), num, p);
                    CheckAction(a.GetDictionary("A"), num, p);
                }
            }
        }

        foreach (PdfOutlineItem item in OutlineItems())
        {
            Check(item.Dictionary.GetRaw("Dest"), item.Dictionary.Reference?.Number ?? -1, -1);
            CheckAction(item.Dictionary.GetDictionary("A"), item.Dictionary.Reference?.Number ?? -1, -1);
        }
    }

    // Every outline item, parents before children, without recursion.
    private IEnumerable<PdfOutlineItem> OutlineItems()
    {
        var stack = new Stack<PdfOutlineItem>();
        IReadOnlyList<PdfOutlineItem> top = _doc.Outline;
        for (int i = top.Count - 1; i >= 0; i--)
        {
            stack.Push(top[i]);
        }

        while (stack.Count > 0)
        {
            PdfOutlineItem item = stack.Pop();
            yield return item;
            for (int i = item.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(item.Children[i]);
            }
        }
    }

    private static bool IsPdf20(string? version) => version is not null && version.StartsWith("2.", StringComparison.Ordinal);

    private static bool IsLanguageTag(string s)
    {
        // BCP 47 (RFC 5646) in the loose form viewers accept: subtags of 1 to 8 letters or digits separated by hyphens, the first being letters.
        if (s.Length == 0 || s.Length > 64)
        {
            return false;
        }

        int run = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '-')
            {
                if (run == 0)
                {
                    return false;
                }

                run = 0;
            }
            else if (char.IsAsciiLetterOrDigit(c))
            {
                if (++run > 8)
                {
                    return false;
                }

                if (i == run - 1 && !char.IsAsciiLetter(c))
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        return run > 0;
    }

    private void CheckSecurity()
    {
        if (_doc.IsEncrypted && (_doc.Permissions & PdfPermissions.ExtractForAccessibility) == 0)
        {
            Add("26", "7.16", "The document is encrypted with the accessibility-extraction permission cleared, which blocks assistive technology.");
        }
    }

    // ---- the structure tree -----------------------------------------------------------------------------------------------------

    private void WalkTree(PdfDictionary root)
    {
        PdfDictionary? roleMap = root.GetDictionary("RoleMap");
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(PdfObject Item, int Parent, int PageObject)>();
        stack.Push((root.Get("K") ?? new PdfArray(), -1, -1));
        int limit = _doc.Limits.MaxObjects;
        long visited = 0;
        var roleCache = new Dictionary<string, string?>(StringComparer.Ordinal);
        var reportedType = new HashSet<string>(StringComparer.Ordinal);
        while (stack.Count > 0)
        {
            if ((++visited & 1023) == 0)
            {
                _ct.ThrowIfCancellationRequested();
            }

            if (visited > limit)
            {
                throw new PdfLimitException("The structure tree has too many nodes to check.");
            }

            (PdfObject item, int parent, int pageObject) = stack.Pop();
            switch (item)
            {
                case PdfArray arr:
                    for (int i = arr.Count - 1; i >= 0; i--)
                    {
                        if (arr[i] is { } k)
                        {
                            stack.Push((k, parent, pageObject));
                        }
                    }

                    break;
                case PdfInteger mcid:
                    if (parent >= 0)
                    {
                        _nodes[parent].ContentItems++;
                    }

                    if (pageObject >= 0 && mcid.Value is >= 0 and <= int.MaxValue)
                    {
                        _treeMcids.Add((pageObject, (int)mcid.Value));
                    }

                    break;
                case PdfDictionary d:
                    if (!seen.Add(d))
                    {
                        if (!Limited("cycle"))
                        {
                            Add("09", "7.1", "A structure element is reachable twice (a cycle or a shared element).", d.Reference?.Number ?? -1);
                        }

                        break;
                    }

                    string? kind = d.GetName("Type");
                    int pg = d.GetRaw("Pg") is PdfReference pr ? pr.Number : pageObject;
                    if (kind == "MCR" || (kind is null && !d.ContainsKey("S") && d.ContainsKey("MCID")))
                    {
                        if (parent >= 0)
                        {
                            _nodes[parent].ContentItems++;
                        }

                        int page = d.GetRaw("Pg") is PdfReference mr ? mr.Number : pageObject;
                        if (!d.ContainsKey("Stm") && page >= 0 && d.GetInt("MCID", -1) is >= 0 and <= int.MaxValue and long m)
                        {
                            _treeMcids.Add((page, (int)m));
                        }
                        else if (d.GetRaw("Stm") is PdfReference stm && d.GetInt("MCID", -1) is >= 0 and <= int.MaxValue and long sm)
                        {
                            _stmMcids.Add((stm.Number, (int)sm));
                        }

                        break;
                    }

                    if (kind == "OBJR")
                    {
                        if (d.Get("Obj") is PdfDictionary od && od.Reference is { } oref && parent >= 0)
                        {
                            _objrParent[oref.Number] = parent;
                        }

                        break;
                    }

                    string type = d.GetName("S") ?? string.Empty;
                    var node = new Node { Dict = d, Type = type, Parent = parent, PageObject = pg, Number = d.Reference?.Number ?? -1 };
                    node.Ns = _part2 ? PdfUA2.NamespaceOf(d) : string.Empty;
                    string ns = _part2 ? PdfUA2.NamespaceOf(d) : string.Empty;
                    node.Role = _part2 ? (PdfUA2.IsStandard(ns, type) ? type : null) : Resolve(type, roleMap, roleCache);
                    int index = _nodes.Count;
                    _nodes.Add(node);
                    if (parent >= 0)
                    {
                        _nodes[parent].Kids.Add(index);
                    }

                    if (node.Role is null && reportedType.Add(type) && !Limited("role"))
                    {
                        Add("02", "7.1", type.Length == 0
                            ? "A structure element has no /S structure type."
                            : _part2
                                ? ns.Length == 0
                                    ? $"The structure element of type '{type}' names no structure namespace (/NS); PDF/UA-2 needs the PDF 2.0 namespace (or the PDF 1.7 one for the types only it has)."
                                    : ns is PdfUA2.Namespace20 or PdfUA2.Namespace17
                                        ? $"The structure type '{type}' is not a standard type of the namespace {ns}."
                                        : $"The structure element of type '{type}' is in the namespace {ns}, which is not the PDF 2.0 or PDF 1.7 structure namespace."
                                : $"The structure type '{type}' is not a standard type and is not mapped to one by the /RoleMap (or its mapping is circular).", node.Number, aClause: "6.7.3.4");
                    }

                    if (d.Get("K") is { } kids)
                    {
                        stack.Push((kids, index, pg));
                    }

                    break;
            }
        }
    }

    // Follows the role map to a standard type; null when there is none or the chain is circular.
    private static string? Resolve(string type, PdfDictionary? roleMap, Dictionary<string, string?> cache)
    {
        if (cache.TryGetValue(type, out string? cached))
        {
            return cached;
        }

        string? result = null;
        string current = type;
        for (int hops = 0; hops < 32; hops++)
        {
            if (s_standardTypes.Contains(current))
            {
                // A standard type that the role map points elsewhere is not followed; CheckStructure reports it as remapped (7.1).
                result = current;
                break;
            }

            string? next = roleMap?.GetName(current);
            if (next is null)
            {
                break;
            }

            current = next;
        }

        cache[type] = result;
        return result;
    }

    private void CheckStructure()
    {
        // Standard types are not allowed to be remapped (ISO 14289-1 7.1).
        if (!_part2 && _doc.Catalog.GetDictionary("StructTreeRoot")?.GetDictionary("RoleMap") is { } map)
        {
            foreach (string key in map.Keys)
            {
                if (s_standardTypes.Contains(key) && map.GetName(key) != key && !Limited("remap"))
                {
                    Add("02", "7.1", $"The role map remaps the standard structure type '{key}' to '{map.GetName(key)}'.", aClause: "6.7.3.4");
                }
            }
        }

        int lastHeading = 0;
        bool sawPlainH = false;
        bool sawNumbered = false;
        for (int i = 0; i < _nodes.Count; i++)
        {
            Node n = _nodes[i];
            string? role = n.Role;
            if (role is null)
            {
                continue;
            }

            if (_structureOnly && role != "Figure")
            {
                continue;
            }

            string? parentRole = n.Parent >= 0 ? _nodes[n.Parent].Role : null;
            switch (role)
            {
                case "Figure":
                case "Formula":
                    if (string.IsNullOrWhiteSpace(Text(n.Dict, "Alt")) && string.IsNullOrWhiteSpace(Text(n.Dict, "ActualText")) && !Limited("alt"))
                    {
                        Add("13", role == "Figure" ? "7.3" : "7.7", $"A {role} structure element has neither alternate text (/Alt) nor replacement text (/ActualText).", n.Number, aClause: "6.7");
                    }

                    break;
                case "H":
                    if (_part2)
                    {
                        // ISO 14289-2 8.2.5.12: the unnumbered H is not used; headings are H1, H2, ... .
                        if (!Limited("plainH"))
                        {
                            Add("14", "7.4.4", "A heading uses the structure type H, which PDF/UA-2 does not allow; use H1 to H6.", n.Number, c2: "8.2.5.12");
                        }

                        break;
                    }

                    sawPlainH = true;
                    break;
                case "H1" or "H2" or "H3" or "H4" or "H5" or "H6":
                    sawNumbered = true;
                    int level = role[1] - '0';
                    if (_part2)
                    {
                        // The order of heading levels is a judgement the standard leaves to people; it is not checked for part 2.
                        break;
                    }

                    if (lastHeading == 0 && level != 1 && !Limited("heading"))
                    {
                        Add("14", "7.4.2", $"The first heading is {role}; the heading hierarchy has to start at H1.", n.Number);
                    }
                    else if (lastHeading > 0 && level > lastHeading + 1 && !Limited("heading"))
                    {
                        Add("14", "7.4.2", $"A {role} follows an H{lastHeading}: heading levels are skipped.", n.Number);
                    }

                    lastHeading = level;
                    break;
                case "TOCI":
                    if (_part2 && !PdfUA2.HasRef(n.Dict) && !Limited("toci"))
                    {
                        // ISO 14289-2 8.2.5.8: a table-of-contents item says what it points at with /Ref.
                        Add("-", "-", "A TOCI structure element, and nothing in it, has a /Ref entry naming the element it points at.", n.Number, c2: "8.2.5.8");
                    }

                    break;
                case "Table":
                    CheckTable(i);
                    break;
                case "TR":
                    if (parentRole is not ("Table" or "THead" or "TBody" or "TFoot") && !Limited("tr"))
                    {
                        Add("15", "7.5", "A TR structure element is not a child of Table, THead, TBody or TFoot.", n.Number, c2: "Table 5");
                    }

                    break;
                case "TH":
                case "TD":
                    if (parentRole != "TR" && !Limited("td"))
                    {
                        Add("15", "7.5", $"A {role} structure element is not a child of TR.", n.Number, c2: "Table 5");
                    }

                    break;
                case "THead" or "TBody" or "TFoot":
                    if (parentRole != "Table" && !Limited("tbody"))
                    {
                        Add("15", "7.5", $"A {role} structure element is not a child of Table.", n.Number, c2: "Table 5");
                    }

                    break;
                case "L":
                    if (_part2 && n.Kids.Exists(k => _nodes[k].Role == "LI" && _nodes[k].Kids.Exists(c => _nodes[c].Role == "Lbl")) && !HasListNumbering(n.Dict) && !Limited("listnumbering"))
                    {
                        // ISO 14289-2 8.2.5.25: a list with labels says how they are numbered, and not 'None'.
                        Add("16", "7.6", "A list with labels (Lbl) has no ListNumbering attribute, or it is None.", n.Number, c2: "8.2.5.25");
                    }

                    foreach (int k in n.Kids)
                    {
                        string? kr = _nodes[k].Role;
                        if (kr is not ("LI" or "Caption" or "L") && !Limited("list"))
                        {
                            Add("16", "7.6", $"A list (L) contains a {kr} element; only LI (and a Caption) belong directly in a list.", _nodes[k].Number);
                        }
                    }

                    break;
                case "LI":
                    if (parentRole != "L" && !Limited("li"))
                    {
                        Add("16", "7.6", "An LI structure element is not a child of L.", n.Number);
                    }

                    break;
                case "Lbl" or "LBody":
                    if (parentRole != "LI" && !Limited("lbl"))
                    {
                        Add("16", "7.6", $"A {role} structure element is not a child of LI.", n.Number);
                    }

                    break;
            }
        }

        if (_part2 && !_structureOnly)
        {
            CheckPart2Structure();
        }

        if (!_structureOnly && !_part2 && sawPlainH && sawNumbered)
        {
            Add("14", "7.4.4", "The document mixes the unnumbered heading H with the numbered headings H1 to H6.");
        }
    }

    // The part-2 structure rules for inline, caption, ruby, warichu and MathML elements, and the parent/child table they sit in
    // (ISO 14289-2 8.2.5 and its Table 5, from ISO/TS 32005). Only standard elements are compared; custom types are reported by the
    // namespace check. Which rules need a person's judgement is listed in docs/pdfua.md.
    private void CheckPart2Structure()
    {
        var kidRoles = new List<string>();
        for (int i = 0; i < _nodes.Count; i++)
        {
            Node n = _nodes[i];
            if (n.Role is null)
            {
                continue;
            }

            bool math = n.Ns == PdfUA2.NamespaceMathML;
            if (n.Parent < 0 && !math && PdfUA2Table.Forbids("StructTreeRoot", PdfUA2Table.Normalize(n.Role)) && !Limited("table5-root"))
            {
                Add("-", "-", $"The structure tree root contains a {n.Role} element, which the structure type table does not allow.", n.Number, c2: "Table 5");
            }

            if (math)
            {
                // ISO 14289-2 8.2.5.29: MathML elements occur only below a Formula element (or below another MathML element).
                bool ok = n.Parent >= 0 && (_nodes[n.Parent].Role == "Formula" || _nodes[n.Parent].Ns == PdfUA2.NamespaceMathML);
                if (!ok && !Limited("mathml-parent"))
                {
                    Add("-", "-", $"A MathML element '{n.Type}' is not a child of a Formula structure element (or of another MathML element).", n.Number, c2: "8.2.5.29");
                }

                continue;
            }

            string role = PdfUA2Table.Normalize(n.Role);

            // Kids that are standard elements of the PDF namespaces, in order.
            kidRoles.Clear();
            foreach (int k in n.Kids)
            {
                Node kid = _nodes[k];
                kidRoles.Add(kid.Role is null || kid.Ns == PdfUA2.NamespaceMathML ? "?" : kid.Role);
            }

            switch (role)
            {
                case "Ruby":
                    // ISO 14289-2 8.2.5.23: RB and RT, or RB, RP, RT, RP.
                    if (!Matches(kidRoles, "RB", "RT") && !Matches(kidRoles, "RB", "RP", "RT", "RP") && !Limited("ruby"))
                    {
                        Add("-", "-", $"A Ruby element has the children ({string.Join(", ", kidRoles)}); it needs RB and RT, or RB, RP, RT and RP, in that order.", n.Number, c2: "8.2.5.23");
                    }

                    break;
                case "Warichu":
                    // ISO 14289-2 8.2.5.24: WP, WT, WP.
                    if (!Matches(kidRoles, "WP", "WT", "WP") && !Limited("warichu"))
                    {
                        Add("-", "-", $"A Warichu element has the children ({string.Join(", ", kidRoles)}); it needs WP, WT and WP, in that order.", n.Number, c2: "8.2.5.24");
                    }

                    break;
            }

            // ISO 14289-2 8.2.5.27: a Caption is the first or the last child.
            for (int k = 1; k < kidRoles.Count - 1; k++)
            {
                if (kidRoles[k] == "Caption")
                {
                    if (!Limited("caption"))
                    {
                        Add("-", "-", $"A Caption is neither the first nor the last child of its {n.Role} element.", _nodes[n.Kids[k]].Number, c2: "8.2.5.27");
                    }

                    break;
                }
            }

            // Table 5: children a parent may not have, children it may have once, content it may not hold itself.
            bool grouping = n.ContentItems == 0;
            if (PdfUA2Table.ForbidsContent(role) && n.ContentItems > 0 && !Limited("table5-content"))
            {
                Add("-", "-", $"A {n.Role} element holds marked content directly; it may only hold other structure elements.", n.Number, c2: "Table 5");
            }

            var seenOnce = new HashSet<string>(StringComparer.Ordinal);
            for (int k = 0; k < n.Kids.Count; k++)
            {
                Node kid = _nodes[n.Kids[k]];
                if (kid.Role is null || kid.Ns == PdfUA2.NamespaceMathML)
                {
                    continue;
                }

                string child = PdfUA2Table.Normalize(kid.Role);
                if (PdfUA2Table.Forbids(role, child) && !Limited("table5-pair"))
                {
                    Add("-", "-", $"A {n.Role} element contains a {kid.Role} element, which the structure type table does not allow.", kid.Number, c2: "Table 5");
                }

                if (PdfUA2Table.AtMostOne(role, child, grouping) && !seenOnce.Add(child) && !Limited("table5-once"))
                {
                    Add("-", "-", $"A {n.Role} element contains more than one {kid.Role} element; at most one is allowed.", kid.Number, c2: "Table 5");
                }
            }

            if (role == "Formula")
            {
                CheckAssociatedFiles(n);
            }
        }

    }

    private static bool Matches(List<string> kids, params string[] expected)
    {
        if (kids.Count != expected.Length)
        {
            return false;
        }

        for (int i = 0; i < kids.Count; i++)
        {
            if (kids[i] != expected[i])
            {
                return false;
            }
        }

        return true;
    }

    // ISO 32000-2 14.13 and 7.11.3 (Table 43): a file attached to a Formula as MathML (through /AF) is an associated file, so its file
    // specification names the relationship (/AFRelationship) and holds the data in an embedded file stream (/EF).
    private void CheckAssociatedFiles(Node formula)
    {
        if (formula.Dict.Get("AF") is not { } af)
        {
            return;
        }

        var specs = new List<PdfDictionary>();
        if (af is PdfDictionary one)
        {
            specs.Add(one);
        }
        else if (af is PdfArray arr)
        {
            for (int i = 0; i < arr.Count && i < 64; i++)
            {
                if (arr[i] is PdfDictionary d)
                {
                    specs.Add(d);
                }
            }
        }

        foreach (PdfDictionary spec in specs)
        {
            if (spec.GetName("AFRelationship") is null && !Limited("af-relationship"))
            {
                Add("-", "-", "A file associated with a Formula element has no /AFRelationship entry.", formula.Number, c2: "8.2.5.29");
            }

            if (spec.GetDictionary("EF")?.Get("F") is not PdfStream && spec.GetDictionary("EF")?.Get("UF") is not PdfStream && !Limited("af-embedded"))
            {
                Add("-", "-", "A file associated with a Formula element does not embed its data (no /EF stream).", formula.Number, c2: "8.2.5.29");
            }
        }
    }

    // The /ListNumbering of an L element's own attribute objects or of the classes it names (ISO 32000-2 14.8.5.4.2); true when it has a value other than None.
    private bool HasListNumbering(PdfDictionary element)
    {
        PdfDictionary? classMap = _doc.Catalog.GetDictionary("StructTreeRoot")?.GetDictionary("ClassMap");
        var owners = new List<PdfObject?>();
        void Collect(PdfObject? o)
        {
            if (o is PdfArray arr)
            {
                for (int i = 0; i < arr.Count && i < 64; i++)
                {
                    Collect(arr[i]);
                }
            }
            else if (o is PdfDictionary || o is PdfName)
            {
                owners.Add(o);
            }
        }

        Collect(element.Get("A"));
        if (classMap is not null)
        {
            Collect(element.Get("C") is PdfName cn ? classMap.Get(cn.Value) : null);
            if (element.Get("C") is PdfArray ca)
            {
                for (int i = 0; i < ca.Count && i < 64; i++)
                {
                    if (ca[i] is PdfName n)
                    {
                        Collect(classMap.Get(n.Value));
                    }
                }
            }
        }

        foreach (PdfObject? o in owners)
        {
            if (o is PdfDictionary d && d.GetName("O") == "List" && d.GetName("ListNumbering") is { } ln)
            {
                return ln != "None";
            }
        }

        return false;
    }

    private static string? Text(PdfDictionary d, string key) => d.GetString(key)?.GetText();

    // A table needs header cells, and its rows must cover the same number of columns once row and column spans are counted
    // (ISO 14289-1 7.5; Matterhorn checkpoint 15).
    private void CheckTable(int tableIndex)
    {
        var rows = new List<int>();
        var stack = new Stack<int>();
        Node table = _nodes[tableIndex];
        for (int k = table.Kids.Count - 1; k >= 0; k--)
        {
            stack.Push(table.Kids[k]);
        }

        bool header = false;
        while (stack.Count > 0)
        {
            int i = stack.Pop();
            Node n = _nodes[i];
            if (n.Role == "TR")
            {
                rows.Add(i);
            }
            else if (n.Role is "THead" or "TBody" or "TFoot")
            {
                for (int k = n.Kids.Count - 1; k >= 0; k--)
                {
                    stack.Push(n.Kids[k]);
                }
            }
        }

        var carry = new List<int>(); // rows still covered by a row span, per column
        int expected = -1;
        bool regular = true;
        foreach (int r in rows)
        {
            int col = 0;
            foreach (int c in _nodes[r].Kids)
            {
                Node cell = _nodes[c];
                if (cell.Role is not ("TH" or "TD"))
                {
                    continue;
                }

                header |= cell.Role == "TH";
                while (col < carry.Count && carry[col] > 0)
                {
                    col++;
                }

                (int colSpan, int rowSpan) = Spans(cell.Dict);
                for (int s = 0; s < colSpan && col < 4096; s++, col++)
                {
                    while (carry.Count <= col)
                    {
                        carry.Add(0);
                    }

                    carry[col] = rowSpan;
                }
            }

            // Columns still covered from earlier rows count towards this row's width.
            int width = col;
            for (int k = col; k < carry.Count; k++)
            {
                if (carry[k] > 1)
                {
                    width = k + 1;
                }
            }

            for (int k = 0; k < carry.Count; k++)
            {
                if (carry[k] > 0)
                {
                    carry[k]--;
                }
            }

            if (expected < 0)
            {
                expected = width;
            }
            else if (width != expected)
            {
                regular = false;
            }
        }

        if (!header && !_part2 && !Limited("th"))
        {
            Add("15", "7.5", "A table has no header cells (TH).", table.Number);
        }

        if (!regular && !Limited("regular"))
        {
            Add("15", "7.5", "The rows of a table do not all cover the same number of columns.", table.Number);
        }
    }

    // The /ColSpan and /RowSpan of a cell's Table attribute object (ISO 32000-1 14.8.5.7), possibly one of several attribute objects.
    private static (int Col, int Row) Spans(PdfDictionary cell)
    {
        int col = 1;
        int row = 1;
        PdfObject? a = cell.Get("A");
        if (a is PdfDictionary single)
        {
            Read(single, ref col, ref row);
        }
        else if (a is PdfArray arr)
        {
            for (int i = 0; i < arr.Count && i < 16; i++)
            {
                if (arr[i] is PdfDictionary d)
                {
                    Read(d, ref col, ref row);
                }
            }
        }

        return (col, row);

        static void Read(PdfDictionary d, ref int col, ref int row)
        {
            if (d.GetName("O") is not (null or "Table"))
            {
                return;
            }

            if (d.GetInt("ColSpan", 1) is > 0 and < 4096 and long c)
            {
                col = (int)c;
            }

            if (d.GetInt("RowSpan", 1) is > 0 and < 4096 and long r)
            {
                row = (int)r;
            }
        }
    }

    // ---- pages: content, annotations, fonts -------------------------------------------------------------------------------------

    private void CheckPages(bool hasTree)
    {
        PdfPageCollection pages = _doc.Pages;
        var fontsSeen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        for (int p = 0; p < pages.Count; p++)
        {
            _ct.ThrowIfCancellationRequested();
            PdfDictionary pd;
            try
            {
                pd = pages[p].Dictionary;
            }
            catch (PdfFormatException ex)
            {
                Add("01", "7.1", $"Page {p + 1} cannot be read: {ex.Message}", ex.ObjectNumber, p);
                continue;
            }

            int pageNumber = pd.Reference?.Number ?? -1;
            ScanPageContent(pages[p], pd, pageNumber, p, hasTree);
            if (_structureOnly)
            {
                continue;
            }

            CheckAnnotations(pd, p);
            CheckFonts(pages[p].Resources, p, fontsSeen);
        }
    }

    private sealed class ContentState
    {
        public readonly List<byte> Stack = []; // 0 artifact, 1 tagged, 2 other marked content
        public int Artifact;
        public int Tagged;
        public bool Reported;
        public readonly HashSet<int> Mcids = [];
    }

    private void ScanPageContent(PdfPage page, PdfDictionary pd, int pageNumber, int pageIndex, bool hasTree)
    {
        var st = new ContentState();
        var streams = new List<PdfStream>();
        PdfObject? contents = pd.Get("Contents");
        if (contents is PdfStream one)
        {
            streams.Add(one);
        }
        else if (contents is PdfArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                if (arr[i] is PdfStream s)
                {
                    streams.Add(s);
                }
            }
        }

        PdfDictionary? properties = page.Resources.GetDictionary("Properties");
        long operators = 0;
        foreach (PdfStream s in streams)
        {
            ReadOnlyMemory<byte> data;
            try
            {
                data = s.GetDecodedBytes(_ct);
            }
            catch (PdfException ex)
            {
                Add("01", "7.1", "A content stream cannot be decoded: " + ex.Message, s.Reference?.Number ?? -1, pageIndex);
                continue;
            }

            var lexer = new ContentLexer(data, _doc.Limits.MaxNestingDepth);
            string? firstName = null;
            Operand last = default;
            while (true)
            {
                ContentToken tok = lexer.Next(out Operand operand, out ContentOp op);
                if (tok == ContentToken.End)
                {
                    break;
                }

                if (tok == ContentToken.Operand)
                {
                    if (operand.Kind == OperandKind.Name && firstName is null)
                    {
                        firstName = operand.Name;
                    }

                    last = operand;
                    continue;
                }

                if (++operators > _doc.Limits.MaxOperatorsPerPage)
                {
                    throw new PdfLimitException("A page has too many content operators to check.");
                }

                switch (op)
                {
                    case ContentOp.BeginMarked:
                        st.Stack.Add(firstName == "Artifact" ? (byte)0 : (byte)2);
                        if (firstName == "Artifact")
                        {
                            st.Artifact++;
                        }

                        break;
                    case ContentOp.BeginMarkedProps:
                        PdfDictionary? props = last.Kind == OperandKind.Object ? last.Obj as PdfDictionary : last.Kind == OperandKind.Name && last.Name is { } pn ? properties?.GetDictionary(pn) : null;
                        if (firstName == "Artifact")
                        {
                            st.Stack.Add(0);
                            st.Artifact++;
                        }
                        else if (props is not null && props.ContainsKey("MCID"))
                        {
                            st.Stack.Add(1);
                            st.Tagged++;
                            long mcid = props.GetInt("MCID", -1);
                            if (mcid is >= 0 and <= int.MaxValue)
                            {
                                st.Mcids.Add((int)mcid);
                            }

                            if (st.Artifact > 0 && !Limited("tagged-in-artifact"))
                            {
                                Add("01", "7.1", "Tagged content is nested inside content marked as an artifact.", -1, pageIndex);
                            }
                        }
                        else
                        {
                            st.Stack.Add(2);
                        }

                        break;
                    case ContentOp.EndMarked:
                        if (st.Stack.Count > 0)
                        {
                            byte k = st.Stack[^1];
                            st.Stack.RemoveAt(st.Stack.Count - 1);
                            if (k == 0)
                            {
                                st.Artifact--;
                            }
                            else if (k == 1)
                            {
                                st.Tagged--;
                            }
                        }

                        break;
                    case ContentOp.Stroke or ContentOp.CloseStroke or ContentOp.FillNz or ContentOp.FillNzOld or ContentOp.FillEo
                        or ContentOp.FillStroke or ContentOp.FillStrokeEo or ContentOp.CloseFillStroke or ContentOp.CloseFillStrokeEo
                        or ContentOp.Shading or ContentOp.Do or ContentOp.ShowText or ContentOp.ShowTextArray or ContentOp.NextLineShow
                        or ContentOp.SpacedNextLineShow or ContentOp.BeginInline:
                        // A form drawn outside any marked content is fine when everything it paints is marked inside it (ISO 32000-2 14.7.4.2:
                        // a form XObject may carry its own marked content, linked to the tree by an MCR with /Stm).
                        if (op == ContentOp.Do && st.Artifact == 0 && st.Tagged == 0 && firstName is not null
                            && page.Resources.GetDictionary("XObject")?.Get(firstName) is PdfStream formStream && formStream.Dictionary.GetName("Subtype") == "Form")
                        {
                            FormScan scan = ScanForm(formStream, page.Resources, 0);
                            if (scan.Untagged && !st.Reported)
                            {
                                st.Reported = true;
                                Add("01", "7.1", "The page paints content that is neither tagged as real content nor marked as an artifact.", formStream.Reference?.Number ?? -1, pageIndex, "6.7.2");
                            }

                            if (hasTree && scan.Mcids.Count > 0 && formStream.Reference is { } formRef)
                            {
                                foreach (int fm in scan.Mcids)
                                {
                                    if (!_stmMcids.Contains((formRef.Number, fm)) && !Limited("orphan-form-mcid"))
                                    {
                                        Add("01", "7.1", $"Marked content with MCID {fm} of a form XObject is not part of the structure tree.", formRef.Number, pageIndex);
                                    }
                                }

                                if (!formStream.Dictionary.ContainsKey("StructParents") && !Limited("form-structparents"))
                                {
                                    Add("01", "7.1", "A form XObject with tagged content has no /StructParents entry linking it to the parent tree.", formRef.Number, pageIndex);
                                }
                            }
                        }
                        else if (st.Artifact == 0 && st.Tagged == 0 && !st.Reported)
                        {
                            st.Reported = true;
                            Add("01", "7.1", "The page paints content that is neither tagged as real content nor marked as an artifact.", s.Reference?.Number ?? -1, pageIndex, "6.7.2");
                        }

                        if (op == ContentOp.BeginInline)
                        {
                            SkipInline(lexer);
                        }

                        break;
                }

                firstName = null;
                last = default;
            }
        }

        if (hasTree)
        {
            foreach (int mcid in st.Mcids)
            {
                if (pageNumber >= 0 && !_treeMcids.Contains((pageNumber, mcid)) && !Limited("orphan-mcid"))
                {
                    Add("01", "7.1", $"Marked content with MCID {mcid} is not part of the structure tree.", -1, pageIndex);
                }
            }

            if (st.Mcids.Count > 0 && !pd.ContainsKey("StructParents") && !Limited("structparents"))
            {
                Add("01", "7.1", "The page has tagged content but no /StructParents entry linking it to the parent tree.", pageNumber, pageIndex);
            }
        }
    }

    private sealed class FormScan
    {
        public bool Untagged;
        public readonly List<int> Mcids = [];
    }

    private const int MaxFormDepth = 32;

    // What a form XObject paints outside marked content, and the MCIDs it tags itself. Nested forms are followed with a depth bound and a
    // cycle guard; results are memoized per stream. A form that cannot be read counts as untagged.
    private FormScan ScanForm(PdfStream form, PdfDictionary? parentResources, int depth)
    {
        if (_formScans.TryGetValue(form, out FormScan? known))
        {
            return known;
        }

        var result = new FormScan();
        _formScans[form] = result; // also the cycle guard: a form met again while being scanned adds nothing
        if (depth >= MaxFormDepth)
        {
            result.Untagged = true;
            return result;
        }

        ReadOnlyMemory<byte> data;
        try
        {
            data = form.GetDecodedBytes(_ct);
        }
        catch (PdfException)
        {
            result.Untagged = true;
            return result;
        }

        PdfDictionary? resources = form.Dictionary.GetDictionary("Resources") ?? parentResources;
        PdfDictionary? properties = resources?.GetDictionary("Properties");
        var stack = new List<byte>();
        int artifact = 0, tagged = 0;
        var lexer = new ContentLexer(data, _doc.Limits.MaxNestingDepth);
        string? firstName = null;
        Operand last = default;
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
                if (operand.Kind == OperandKind.Name && firstName is null)
                {
                    firstName = operand.Name;
                }

                last = operand;
                continue;
            }

            if (++operators > _doc.Limits.MaxOperatorsPerPage)
            {
                throw new PdfLimitException("A form has too many content operators to check.");
            }

            switch (op)
            {
                case ContentOp.BeginMarked:
                    stack.Add(firstName == "Artifact" ? (byte)0 : (byte)2);
                    if (firstName == "Artifact")
                    {
                        artifact++;
                    }

                    break;
                case ContentOp.BeginMarkedProps:
                    PdfDictionary? props = last.Kind == OperandKind.Object ? last.Obj as PdfDictionary : last.Kind == OperandKind.Name && last.Name is { } pn ? properties?.GetDictionary(pn) : null;
                    if (firstName == "Artifact")
                    {
                        stack.Add(0);
                        artifact++;
                    }
                    else if (props is not null && props.ContainsKey("MCID"))
                    {
                        stack.Add(1);
                        tagged++;
                        if (props.GetInt("MCID", -1) is >= 0 and <= int.MaxValue and long mcid)
                        {
                            result.Mcids.Add((int)mcid);
                        }
                    }
                    else
                    {
                        stack.Add(2);
                    }

                    break;
                case ContentOp.EndMarked:
                    if (stack.Count > 0)
                    {
                        byte k = stack[^1];
                        stack.RemoveAt(stack.Count - 1);
                        if (k == 0)
                        {
                            artifact--;
                        }
                        else if (k == 1)
                        {
                            tagged--;
                        }
                    }

                    break;
                case ContentOp.Stroke or ContentOp.CloseStroke or ContentOp.FillNz or ContentOp.FillNzOld or ContentOp.FillEo
                    or ContentOp.FillStroke or ContentOp.FillStrokeEo or ContentOp.CloseFillStroke or ContentOp.CloseFillStrokeEo
                    or ContentOp.Shading or ContentOp.Do or ContentOp.ShowText or ContentOp.ShowTextArray or ContentOp.NextLineShow
                    or ContentOp.SpacedNextLineShow or ContentOp.BeginInline:
                    if (artifact == 0 && tagged == 0)
                    {
                        if (op == ContentOp.Do && firstName is not null && resources?.GetDictionary("XObject")?.Get(firstName) is PdfStream inner
                            && inner.Dictionary.GetName("Subtype") == "Form")
                        {
                            if (ScanForm(inner, resources, depth + 1).Untagged)
                            {
                                result.Untagged = true;
                            }
                        }
                        else
                        {
                            result.Untagged = true;
                        }
                    }

                    if (op == ContentOp.BeginInline)
                    {
                        SkipInline(lexer);
                    }

                    break;
            }

            firstName = null;
            last = default;
        }

        return result;
    }

    // After BI the lexer is positioned at the image dictionary; read it and the data so the next operator is the one after EI.
    private static void SkipInline(ContentLexer lexer)
    {
        if (lexer.ReadInlineImageDictionary() is not null)
        {
            lexer.ReadInlineImageData(-1, out _);
        }
    }

    private void CheckAnnotations(PdfDictionary page, int pageIndex)
    {
        if (page.Get("Annots") is not PdfArray annots || annots.Count == 0)
        {
            return;
        }

        bool any = false;
        for (int i = 0; i < annots.Count; i++)
        {
            if (annots[i] is not PdfDictionary a)
            {
                continue;
            }

            string subtype = a.GetName("Subtype") ?? string.Empty;
            if (subtype is "Popup" or "TrapNet" or "PrinterMark")
            {
                continue;
            }

            any = true;
            int num = a.Reference?.Number ?? -1;
            bool inTree = num >= 0 && _objrParent.ContainsKey(num);
            string? parentRole = inTree ? _nodes[_objrParent[num]].Role : null;
            if (subtype == "Link")
            {
                if (!inTree || (_part2 ? parentRole is not ("Link" or "Reference") : parentRole != "Link"))
                {
                    if (!Limited("link"))
                    {
                        Add("28", "7.18.5", inTree ? $"A link annotation is tagged as {parentRole}, not as a Link structure element." : "A link annotation is not part of a Link structure element (no object reference in the structure tree).", num, pageIndex);
                    }
                }
                else if (string.IsNullOrWhiteSpace(a.GetString("Contents")?.GetText()) && string.IsNullOrWhiteSpace(Text(_nodes[_objrParent[num]].Dict, "Alt")) && !Limited("link-contents"))
                {
                    if (!_part2)
                    {
                        Add("28", "7.18.5", "A link annotation has no /Contents describing it.", num, pageIndex);
                    }
                }
            }
            else if (subtype == "Widget")
            {
                if (!inTree || parentRole != "Form")
                {
                    if (!Limited("widget"))
                    {
                        Add("28", "7.18.4", "A form widget is not part of a Form structure element.", num, pageIndex);
                    }
                }

                if (string.IsNullOrWhiteSpace(FieldText(a, "TU")) && !Limited("tu"))
                {
                    Add("28", "7.18.4", "A form field has no /TU description for assistive technology.", num, pageIndex);
                }
            }
            else
            {
                if (!inTree && !Limited("annot"))
                {
                    Add("28", "7.18.1", $"A {subtype} annotation is not part of the structure tree.", num, pageIndex);
                }

                if (string.IsNullOrWhiteSpace(a.GetString("Contents")?.GetText()) && !Limited("annot-contents"))
                {
                    Add("28", "7.18.1", $"A {subtype} annotation has no /Contents describing it.", num, pageIndex);
                }
            }

            if (num >= 0 && !a.ContainsKey("StructParent") && !Limited("structparent"))
            {
                Add("28", "7.18.1", "An annotation has no /StructParent, so it cannot be found from the parent tree.", num, pageIndex);
            }
        }

        if (any && (_part2 ? page.GetName("Tabs") is not ("S" or "A" or "W") : page.GetName("Tabs") != "S"))
        {
            Add("28", "7.18.3", _part2 ? "The page has annotations but no /Tabs entry (A, W or S)." : "The page has annotations but its /Tabs entry is not /S (structure order).", page.Reference?.Number ?? -1, pageIndex, c2: "8.9.3.3");
        }
    }

    private static string? FieldText(PdfDictionary field, string key)
    {
        PdfDictionary? d = field;
        for (int depth = 0; d is not null && depth < 32; depth++)
        {
            if (d.GetString(key) is { } s)
            {
                return s.GetText();
            }

            d = d.GetDictionary("Parent");
        }

        return null;
    }

    private void CheckFonts(PdfDictionary resources, int pageIndex, HashSet<PdfDictionary> seen)
    {
        var stack = new Stack<(PdfDictionary Resources, int Depth)>();
        stack.Push((resources, 0));
        var visitedResources = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        while (stack.Count > 0)
        {
            (PdfDictionary res, int depth) = stack.Pop();
            if (!visitedResources.Add(res) || depth > _doc.Limits.MaxRecursionDepth)
            {
                continue;
            }

            if (res.GetDictionary("Font") is { } fonts)
            {
                foreach (string key in fonts.Keys)
                {
                    if (fonts.Get(key) is PdfDictionary f && seen.Add(f))
                    {
                        CheckFont(f, pageIndex);
                    }
                }
            }

            if (res.GetDictionary("XObject") is { } xobjects)
            {
                foreach (string key in xobjects.Keys)
                {
                    if (xobjects.Get(key) is PdfStream { } xs && xs.Dictionary.GetName("Subtype") == "Form" && xs.Dictionary.GetDictionary("Resources") is { } inner)
                    {
                        stack.Push((inner, depth + 1));
                    }
                }
            }
        }
    }

    private void CheckFont(PdfDictionary f, int pageIndex)
    {
        string subtype = f.GetName("Subtype") ?? string.Empty;
        if (subtype == "Type3")
        {
            return;
        }

        string name = f.GetName("BaseFont") ?? "(unnamed)";
        int num = f.Reference?.Number ?? -1;
        PdfDictionary? descriptor = f.GetDictionary("FontDescriptor");
        if (subtype == "Type0" && f.Get("DescendantFonts") is PdfArray { Count: > 0 } desc && desc[0] is PdfDictionary cid)
        {
            descriptor = cid.GetDictionary("FontDescriptor");
        }

        if (descriptor is null || !(descriptor.ContainsKey("FontFile") || descriptor.ContainsKey("FontFile2") || descriptor.ContainsKey("FontFile3")))
        {
            if (!Limited("font"))
            {
                Add("31", "7.21.4.1", $"The font {name} is not embedded: all fonts have to be embedded.", num, pageIndex);
            }
        }

        if (f.Get("ToUnicode") is PdfStream)
        {
            return;
        }

        bool unicodeKnown;
        if (subtype == "Type0")
        {
            unicodeKnown = f.Get("DescendantFonts") is PdfArray { Count: > 0 } d2 && d2[0] is PdfDictionary c2
                && c2.GetDictionary("CIDSystemInfo") is { } info
                && info.GetString("Registry")?.GetText() == "Adobe"
                && info.GetString("Ordering")?.GetText() is "GB1" or "CNS1" or "Japan1" or "Korea1";
        }
        else
        {
            PdfObject? enc = f.Get("Encoding");
            unicodeKnown = enc is PdfName { Value: "WinAnsiEncoding" or "MacRomanEncoding" or "StandardEncoding" }
                || (enc is PdfDictionary ed && ed.GetName("BaseEncoding") is "WinAnsiEncoding" or "MacRomanEncoding" or "StandardEncoding" && !ed.ContainsKey("Differences"));
        }

        if (!unicodeKnown && !Limited("tounicode"))
        {
            Add("10", "7.21.7", $"The font {name} has no /ToUnicode map and its encoding does not determine Unicode values.", num, pageIndex);
        }
    }
}
