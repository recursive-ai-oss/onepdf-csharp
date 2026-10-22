namespace OnePdf.Text;

/// <summary>The marked-content order the logical structure tree gives one page.</summary>
internal sealed class PageStructure
{
    /// <summary>MCID to (rank in tree order, group). MCIDs of one structure element on the page share a group.</summary>
    internal Dictionary<int, (int Rank, int Group)> Map { get; } = [];
}

/// <summary>
/// Reads the reading order out of the logical structure tree (ISO 32000-2 14.7): a depth-first walk of <c>/K</c> from
/// <c>/StructTreeRoot</c> records, per page, the marked-content identifiers in the order the tree lists them. The walk uses an explicit
/// stack, visits each indirect element once and stops at a fixed node count.
/// </summary>
internal sealed class StructureOrder
{
    internal const int MaxNodes = 1 << 22;

    private readonly Dictionary<int, PageStructure> _pages = [];

    internal bool Truncated { get; private set; }

    internal PageStructure? ForPage(int pageIndex) => _pages.GetValueOrDefault(pageIndex);

    internal static StructureOrder? Read(PdfDocument doc, CancellationToken ct)
    {
        PdfDictionary? root = doc.StructureTreeRoot;
        if (root is null)
        {
            return null;
        }

        StructureOrder so = new();
        Dictionary<int, int> lastElem = [];
        Dictionary<int, int> groupCounter = [];
        HashSet<int> seen = [];
        Stack<(PdfObject? Node, int Page, int Elem)> stack = new();
        stack.Push((root.Get("K"), -1, 0));
        int elemCounter = 0;
        int visited = 0;

        while (stack.Count > 0)
        {
            if ((++visited & 1023) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            if (visited > MaxNodes)
            {
                so.Truncated = true;
                break;
            }

            (PdfObject? node, int page, int elem) = stack.Pop();
            switch (node)
            {
                case PdfInteger mc:
                    Add(mc.Value, page, elem);
                    break;
                case PdfArray arr:
                    for (int i = arr.Count - 1; i >= 0; i--)
                    {
                        stack.Push((arr[i], page, elem));
                    }

                    break;
                case PdfDictionary d:
                {
                    string? type = d.GetName("Type");
                    if (type == "MCR")
                    {
                        int pg = PageOf(doc, d) ?? page;
                        Add(d.GetInt("MCID", -1), pg, elem);
                    }
                    else if (type == "OBJR")
                    {
                        // an object reference (annotation, XObject): not marked content on the page
                    }
                    else
                    {
                        if (d.Reference is { } r && !seen.Add(r.Number))
                        {
                            break;
                        }

                        int pg = PageOf(doc, d) ?? page;
                        // Inline-level elements (ISO 32000-2 14.8.4.4) and the purely grouping NonStruct and Private (14.8.4.2) are runs inside a paragraph, so their content joins the enclosing element's group:
                        // laying them out apart would cut a line at every style change or link.
                        stack.Push((d.Get("K"), pg, IsInline(d.GetName("S")) ? elem : ++elemCounter));
                    }

                    break;
                }
            }
        }

        return so;

        void Add(long mcid, int page, int elem)
        {
            if (page < 0 || mcid < 0 || mcid > int.MaxValue)
            {
                return;
            }

            if (!so._pages.TryGetValue(page, out PageStructure? ps))
            {
                so._pages[page] = ps = new PageStructure();
            }

            if (ps.Map.ContainsKey((int)mcid))
            {
                return;
            }

            if (!lastElem.TryGetValue(page, out int le) || le != elem)
            {
                lastElem[page] = elem;
                groupCounter[page] = groupCounter.GetValueOrDefault(page) + 1;
            }

            ps.Map[(int)mcid] = (ps.Map.Count, groupCounter[page]);
        }
    }

    private static bool IsInline(string? type) =>
        type is "Span" or "NonStruct" or "Private" or "Quote" or "Note" or "Reference" or "BibEntry" or "Code" or "Link" or "Annot" or "Em" or "Strong" or "Sub" or "Sup" or "Lbl" or
            "Ruby" or "RB" or "RT" or "RP" or "Warichu" or "WT" or "WP";

    private static int? PageOf(PdfDocument doc, PdfDictionary d)
    {
        int n = d.GetRaw("Pg") is PdfReference r ? r.Number : d.GetDictionary("Pg")?.Reference?.Number ?? -1;
        if (n < 0)
        {
            return null;
        }

        int idx = doc.Pages.IndexOfObject(n);
        return idx >= 0 ? idx : null;
    }
}
