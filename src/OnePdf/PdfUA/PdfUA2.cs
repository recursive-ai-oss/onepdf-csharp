namespace OnePdf.PdfUA;

/// <summary>
/// What PDF/UA-2 (ISO 14289-2, built on PDF 2.0) adds to the PDF/UA-1 model: structure elements live in the PDF 2.0 structure
/// namespace (ISO 32000-2 14.7.4.2), and the file says which revision of the standard it follows.
/// </summary>
internal static class PdfUA2
{
    /// <summary>The namespace of the PDF 2.0 standard structure types (ISO 32000-2 14.8.6, Table 365).</summary>
    public const string Namespace20 = "http://iso.org/pdf2/ssn";

    /// <summary>The namespace of the PDF 1.7 standard structure types, which is the default when an element has no <c>/NS</c>.</summary>
    public const string Namespace17 = "http://iso.org/pdf/ssn";

    /// <summary>The revision written to <c>pdfuaid:rev</c>: the year ISO 14289-2 was published.</summary>
    public const int Revision = 2024;

    // The structure types that the PDF 2.0 namespace defines (ISO 32000-2 14.8.6). Hn (any n >= 1) is handled by IsHeading.
    private static readonly HashSet<string> s_types20 = new(StringComparer.Ordinal)
    {
        "Document", "DocumentFragment", "Part", "Sect", "Div", "Aside", "NonStruct", "Caption", "Title", "H", "P", "L", "LI", "Lbl", "LBody",
        "Table", "TR", "TH", "TD", "THead", "TBody", "TFoot", "Span", "Em", "Strong", "Sub", "Link", "Annot", "FENote", "Ruby", "RB", "RT", "RP",
        "Warichu", "WT", "WP", "Figure", "Formula", "Form",
    };

    // Types of the PDF 1.7 namespace that the 2.0 namespace does not define. A PDF 2.0 file may still use them by putting the element in
    // the 1.7 namespace (ISO 14289-2 8.2.4 allows either), which is how they are written here.
    private static readonly HashSet<string> s_only17 = new(StringComparer.Ordinal)
    {
        "Art", "BibEntry", "BlockQuote", "Code", "Index", "Private", "Quote", "Reference", "TOC", "TOCI",
    };

    /// <summary>True when <paramref name="type"/> is a structure type the PDF 2.0 namespace defines.</summary>
    public static bool IsStandardType20(string type) => s_types20.Contains(type) || IsHeading(type, out _);

    /// <summary>True when <paramref name="type"/> is a PDF 1.7 type that the PDF 2.0 namespace lacks, so an element of it belongs in the 1.7 namespace.</summary>
    public static bool IsOnly17(string type) => s_only17.Contains(type);

    /// <summary>True when an element of <paramref name="type"/> in namespace <paramref name="ns"/> is a standard structure element of that namespace.</summary>
    public static bool IsStandard(string ns, string type) =>
        ns == Namespace20 ? IsStandardType20(type) : ns == Namespace17 && (IsOnly17(type) || IsStandardType20(type));

    /// <summary>True for <c>H1</c>, <c>H2</c>, ... (ISO 32000-2 14.8.4.3.3 allows any level); <paramref name="level"/> is the number.</summary>
    public static bool IsHeading(string type, out int level)
    {
        level = 0;
        return type.Length is >= 2 and <= 6 && type[0] == 'H' && type[1] is >= '1' and <= '9'
            && int.TryParse(type.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out level) && level >= 1;
    }

    /// <summary>
    /// Puts every structure element into a namespace (ISO 32000-2 14.7.4.2): the structure tree root lists the namespaces in
    /// <c>/Namespaces</c> and each element names its own in <c>/NS</c>. Types the PDF 2.0 namespace defines go into it; the PDF 1.7 types it
    /// lacks (<c>TOC</c>, <c>Reference</c>, <c>BlockQuote</c> and the like) go into the 1.7 namespace. Elements that already name a namespace,
    /// and those of other types (custom ones, reached through a role map), are left alone for the checker to report. Calling it again
    /// changes nothing more.
    /// </summary>
    public static void ConvertStructure(PdfDocument doc, CancellationToken ct)
    {
        if (doc.Catalog.GetDictionary("StructTreeRoot") is not { } root)
        {
            return;
        }

        PdfReference? ns20 = FindNamespace(root, Namespace20);
        PdfReference? ns17 = FindNamespace(root, Namespace17);
        PdfReference Declare(string uri)
        {
            var dict = new PdfDictionary();
            dict.SetEntry("Type", new PdfName("Namespace"));
            dict.SetEntry("NS", PdfString.FromText(uri));
            PdfReference r = doc.AddObject(dict);
            PdfArray list = root.GetArray("Namespaces") ?? new PdfArray();
            if (root.GetArray("Namespaces") is null)
            {
                root.SetEntry("Namespaces", list);
            }

            list.Append(r);
            return r;
        }

        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var tocItems = new List<PdfDictionary>();
        var stack = new Stack<PdfObject>();
        if (root.Get("K") is { } top)
        {
            stack.Push(top);
        }

        int visited = 0;
        while (stack.Count > 0)
        {
            PdfObject current = stack.Pop();
            if (current is PdfArray arr)
            {
                for (int i = arr.Count - 1; i >= 0; i--)
                {
                    if (arr[i] is PdfDictionary or PdfArray)
                    {
                        stack.Push(arr[i]!);
                    }
                }

                continue;
            }

            if (current is not PdfDictionary d || !seen.Add(d))
            {
                continue;
            }

            if ((++visited & 1023) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            if (d.GetName("Type") is "OBJR" or "MCR")
            {
                continue;
            }

            string type = d.GetName("S") ?? string.Empty;
            if (type == "Note" && d.GetRaw("NS") is null)
            {
                // ISO 32000-2 14.8.4.4.2 replaces Note by FENote; the 2.0 namespace has no Note.
                type = "FENote";
                d.SetEntry("S", new PdfName(type));
            }

            if (d.GetRaw("NS") is null)
            {
                if (IsOnly17(type))
                {
                    d.SetEntry("NS", ns17 ??= Declare(Namespace17));
                }
                else if (IsStandardType20(type))
                {
                    d.SetEntry("NS", ns20 ??= Declare(Namespace20));
                }
            }

            if (type == "TOCI" && !HasRef(d))
            {
                tocItems.Add(d);
            }

            if (d.Get("K") is { } kids)
            {
                stack.Push(kids);
            }
        }

        foreach (PdfDictionary item in tocItems)
        {
            ct.ThrowIfCancellationRequested();
            if (TargetOfLink(doc, item) is { } target)
            {
                var refs = new PdfArray();
                refs.Append(target);
                item.SetEntry("Ref", refs);
            }
        }
    }

    /// <summary>True when the element, or one of the elements below it, has a <c>/Ref</c> entry (ISO 32000-2 14.7.2, Table 355; ISO 14289-2 8.2.5.8).</summary>
    public static bool HasRef(PdfDictionary element)
    {
        var stack = new Stack<PdfDictionary>();
        stack.Push(element);
        for (int visited = 0; stack.Count > 0 && visited < 256; visited++)
        {
            PdfDictionary d = stack.Pop();
            if (d.GetRaw("Ref") is not null)
            {
                return true;
            }

            foreach (PdfDictionary kid in StructureKids(d))
            {
                stack.Push(kid);
            }
        }

        return false;
    }

    // The structure element a table-of-contents item points at, read from the structure destination of the link annotation inside it: the
    // item names its target through the link, so no guessing is involved. Null when the item holds no such link.
    private static PdfReference? TargetOfLink(PdfDocument doc, PdfDictionary item)
    {
        var stack = new Stack<PdfDictionary>();
        stack.Push(item);
        for (int visited = 0; stack.Count > 0 && visited < 256; visited++)
        {
            PdfDictionary d = stack.Pop();
            if (d.GetName("Type") == "OBJR" && d.GetDictionary("Obj") is { } annotation && annotation.GetName("Subtype") == "Link")
            {
                PdfObject? dest = annotation.GetRaw("Dest") ?? annotation.GetDictionary("A")?.GetRaw("D");
                if (doc.ResolveDestination(dest, 0) is { IsRemotePage: false, PageIndex: < 0, Page: { } element } && element.GetRaw("S") is not null && element.Reference is { } r)
                {
                    return r;
                }

                continue;
            }

            foreach (PdfDictionary kid in StructureKids(d))
            {
                stack.Push(kid);
            }
        }

        return null;
    }

    // The dictionaries among an element's kids: child elements, object references and marked-content references.
    private static IEnumerable<PdfDictionary> StructureKids(PdfDictionary d)
    {
        switch (d.Get("K"))
        {
            case PdfDictionary one:
                yield return one;
                break;
            case PdfArray many:
                for (int i = 0; i < many.Count && i < 4096; i++)
                {
                    if (many[i] is PdfDictionary kid)
                    {
                        yield return kid;
                    }
                }

                break;
        }
    }

    private static PdfReference? FindNamespace(PdfDictionary root, string uri)
    {
        if (root.GetArray("Namespaces") is not { } list)
        {
            return null;
        }

        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is PdfDictionary d && d.GetString("NS")?.GetText() == uri && d.Reference is { } r)
            {
                return r;
            }
        }

        return null;
    }

    /// <summary>The namespace URI an element names in <c>/NS</c>, or the empty string when it names none.</summary>
    public static string NamespaceOf(PdfDictionary element) =>
        element.GetDictionary("NS")?.GetString("NS")?.GetText() ?? string.Empty;
}
