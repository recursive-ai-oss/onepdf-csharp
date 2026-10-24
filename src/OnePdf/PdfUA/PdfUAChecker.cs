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
    }

    private readonly List<Node> _nodes = [];
    private readonly HashSet<(int Page, int Mcid)> _treeMcids = [];
    private readonly Dictionary<int, int> _objrParent = []; // annotation object number -> node index of its structure element

    /// <param name="doc">The document.</param>
    /// <param name="structureOnly">Only the findings PDF/A level A shares with PDF/UA (tagging, structure types, figures, language).</param>
    /// <param name="ct">Checked between pages and nodes.</param>
    public PdfUAChecker(PdfDocument doc, bool structureOnly, CancellationToken ct)
    {
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
        if (!_structureOnly)
        {
            CheckSecurity();
        }

        return _violations;
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

    private void Add(string checkpoint, string clause, string message, int owner = -1, int page = -1, string? aClause = null)
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

        _violations.Add(new PdfUAViolation(checkpoint, _structureOnly ? aClause ?? clause : clause, message, owner, page));
    }

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
            Add("06", "7.1", "The catalog has no /MarkInfo dictionary with /Marked true: the document is not declared as tagged.", aClause: "6.7.2.2");
        }
        else if (mark.GetBool("Suspects"))
        {
            Add("06", "7.1", "The /MarkInfo dictionary has /Suspects true.");
        }

        if (!hasTree)
        {
            Add("01", "7.1", "The document has no logical structure tree (/StructTreeRoot): its content is not tagged.", aClause: "6.7.3.3");
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
                if (xmp.PdfUAPart != 1)
                {
                    Add("06", "7.1", $"The XMP packet does not carry pdfuaid:part 1 (found {xmp.PdfUAPart?.ToString() ?? "none"}).", meta.Reference?.Number ?? -1);
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
                        int page = d.GetRaw("Pg") is PdfReference mr ? mr.Number : pageObject;
                        if (!d.ContainsKey("Stm") && page >= 0 && d.GetInt("MCID", -1) is >= 0 and <= int.MaxValue and long m)
                        {
                            _treeMcids.Add((page, (int)m));
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
                    node.Role = Resolve(type, roleMap, roleCache);
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
        if (_doc.Catalog.GetDictionary("StructTreeRoot")?.GetDictionary("RoleMap") is { } map)
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
                    sawPlainH = true;
                    break;
                case "H1" or "H2" or "H3" or "H4" or "H5" or "H6":
                    sawNumbered = true;
                    int level = role[1] - '0';
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
                case "Table":
                    CheckTable(i);
                    break;
                case "TR":
                    if (parentRole is not ("Table" or "THead" or "TBody" or "TFoot") && !Limited("tr"))
                    {
                        Add("15", "7.5", "A TR structure element is not a child of Table, THead, TBody or TFoot.", n.Number);
                    }

                    break;
                case "TH":
                case "TD":
                    if (parentRole != "TR" && !Limited("td"))
                    {
                        Add("15", "7.5", $"A {role} structure element is not a child of TR.", n.Number);
                    }

                    break;
                case "THead" or "TBody" or "TFoot":
                    if (parentRole != "Table" && !Limited("tbody"))
                    {
                        Add("15", "7.5", $"A {role} structure element is not a child of Table.", n.Number);
                    }

                    break;
                case "L":
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

        if (!_structureOnly && sawPlainH && sawNumbered)
        {
            Add("14", "7.4.4", "The document mixes the unnumbered heading H with the numbered headings H1 to H6.");
        }
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

        if (!header && !Limited("th"))
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
                        if (st.Artifact == 0 && st.Tagged == 0 && !st.Reported)
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
                if (!inTree || parentRole != "Link")
                {
                    if (!Limited("link"))
                    {
                        Add("28", "7.18.5", inTree ? $"A link annotation is tagged as {parentRole}, not as a Link structure element." : "A link annotation is not part of a Link structure element (no object reference in the structure tree).", num, pageIndex);
                    }
                }
                else if (string.IsNullOrWhiteSpace(a.GetString("Contents")?.GetText()) && string.IsNullOrWhiteSpace(Text(_nodes[_objrParent[num]].Dict, "Alt")) && !Limited("link-contents"))
                {
                    Add("28", "7.18.5", "A link annotation has no /Contents describing it.", num, pageIndex);
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

        if (any && page.GetName("Tabs") != "S")
        {
            Add("28", "7.18.3", "The page has annotations but its /Tabs entry is not /S (structure order).", page.Reference?.Number ?? -1, pageIndex);
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
