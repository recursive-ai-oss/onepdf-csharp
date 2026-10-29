using OnePdf.PdfA;

namespace OnePdf.PdfUA;

/// <summary>
/// Makes the repairs that need no judgement for PDF/UA-1 and for PDF/A level A: it marks a document that has a structure tree as
/// tagged, writes the language the caller named when the catalog has none, asks viewers to show the title, sets the tab order of pages
/// with annotations to the structure order, gives link annotations a description from their structure element or their address, and
/// writes the XMP identification. It never invents structure, titles or alternate text: what is missing stays for the checker to report.
/// </summary>
internal static class PdfUAPreparer
{
    /// <param name="doc">An editable document.</param>
    /// <param name="ua">The PDF/UA options, or null when only PDF/A level A is being prepared.</param>
    /// <param name="pdfA">The PDF/A options when PDF/A is also requested (its XMP is then written by the PDF/A preparer), else null.</param>
    /// <param name="ct">Checked between pages.</param>
    public static void Prepare(PdfDocument doc, PdfUAOptions? ua, PdfAOptions? pdfA, CancellationToken ct)
    {
        doc.EnsureEditable(0);
        CreationState.FinishFonts(doc);
        PdfDictionary catalog = doc.Catalog;
        if (catalog.GetDictionary("StructTreeRoot") is not null)
        {
            PdfDictionary? mark = catalog.GetDictionary("MarkInfo");
            if (mark is null)
            {
                mark = new PdfDictionary();
                catalog.SetEntry("MarkInfo", mark);
                mark = catalog.GetDictionary("MarkInfo")!;
            }

            if (!mark.GetBool("Marked"))
            {
                mark.SetEntry("Marked", new PdfBoolean(true));
            }
        }

        string? language = ua?.Language ?? pdfA?.Language;
        if (string.IsNullOrWhiteSpace(catalog.GetString("Lang")?.GetText()) && !string.IsNullOrWhiteSpace(language))
        {
            catalog.SetEntry("Lang", PdfString.FromText(language));
        }

        if (ua is null)
        {
            return;
        }

        if (ua.EmbedLibraryFonts && pdfA is null)
        {
            PdfAPreparer.EmbedLibraryFonts(doc, ct);
        }

        if (ua.EmbedSubstituteFonts && pdfA is null)
        {
            PdfAPreparer.RequireProvider(doc.EmbedSubstituteFonts(cancellationToken: ct));
        }

        if (!string.IsNullOrWhiteSpace(doc.Info.Title) || !string.IsNullOrWhiteSpace(doc.Xmp?.Title))
        {
            PdfDictionary? vp = catalog.GetDictionary("ViewerPreferences");
            if (vp is null)
            {
                catalog.SetEntry("ViewerPreferences", new PdfDictionary());
                vp = catalog.GetDictionary("ViewerPreferences")!;
            }

            if (!vp.GetBool("DisplayDocTitle"))
            {
                vp.SetEntry("DisplayDocTitle", new PdfBoolean(true));
            }
        }

        PrepareAnnotations(doc, ct);
        PrepareOptionalContentNames(doc, removeAutoState: ua.Part == PdfUAPart.Part1);
        if (ua.Part == PdfUAPart.Part1)
        {
            PrepareTableHeaders(doc, ct);
            if (ua.RemoveInvisibleNotdefText)
            {
                doc.RemoveNotdefText(new PdfNotdefRemoveOptions { Scope = PdfNotdefScope.Invisible }, ct);
            }
        }

        if (ua.Part == PdfUAPart.Part2)
        {
            PdfUA2.ConvertStructure(doc, ct);
            // The conversion edits the tree's dictionaries in place, so nodes read before it no longer match them.
            doc.ForgetStructureTree();
        }

        if (pdfA is null)
        {
            // With PDF/A the packet is written by the PDF/A preparer, which knows both identifications.
            PdfAPreparer.WriteMetadata(doc, null, pdfUA: true, (int)ua.Part);
        }
    }

    // A configuration without a /Name gets one that says what it is: the default one is "Default", the others "Configuration n".
    // Nothing else about optional content changes (ISO 14289-1 7.10).
    private static void PrepareOptionalContentNames(PdfDocument doc, bool removeAutoState)
    {
        if (doc.Catalog.GetDictionary("OCProperties") is not { } props)
        {
            return;
        }

        int n = 0;
        foreach ((PdfDictionary config, string where) in PdfUAChecker.OptionalContentConfigurations(props))
        {
            n++;
            // /AS (automatic state changes by event) is not allowed in Part 1; the groups keep the states /D gives them.
            if (removeAutoState && config.ContainsKey("AS"))
            {
                config.RemoveEntry("AS");
            }

            if (string.IsNullOrWhiteSpace(config.GetString("Name")?.GetText()))
            {
                config.SetEntry("Name", PdfString.FromText(where.Contains("/D)", StringComparison.Ordinal) ? "Default" : "Configuration " + n));
            }
        }
    }

    // A header cell without a scope leaves the data cells of its table with no header unless the answer is plain from the layout of its
    // row (ISO 14289-1 7.5): a row of header cells only heads its columns, and the first cell of a row that also has data cells heads the
    // row. Anything less clear is left for the checker to report, because it takes a person to say what the cell heads.
    private static void PrepareTableHeaders(PdfDocument doc, CancellationToken ct)
    {
        if (doc.Catalog.GetDictionary("StructTreeRoot") is null)
        {
            return;
        }

        List<(int Element, string Scope)> repairs = new PdfUAChecker(doc, structureOnly: false, ct).HeaderScopeRepairs();
        if (repairs.Count == 0)
        {
            return;
        }

        PdfStructureTree? tree = doc.StructureTree;
        if (tree is null)
        {
            return;
        }

        var wanted = new Dictionary<int, string>();
        foreach ((int element, string scope) in repairs)
        {
            wanted[element] = scope;
        }

        foreach (PdfStructureNode node in tree.Elements())
        {
            if (node.Dictionary.Reference is { } r && wanted.TryGetValue(r.Number, out string? scope) && node.Scope is null)
            {
                node.Scope = scope == "Row" ? PdfTagScope.Row : PdfTagScope.Column;
            }
        }
    }

    private static void PrepareAnnotations(PdfDocument doc, CancellationToken ct)
    {
        PdfUAChecker? analysis = null;
        PdfPageCollection pages = doc.Pages;
        for (int p = 0; p < pages.Count; p++)
        {
            ct.ThrowIfCancellationRequested();
            PdfDictionary page = pages[p].Dictionary;
            if (page.Get("Annots") is not PdfArray annots || annots.Count == 0)
            {
                continue;
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
                if (subtype == "Link" && string.IsNullOrWhiteSpace(a.GetString("Contents")?.GetText()))
                {
                    analysis ??= new PdfUAChecker(doc, structureOnly: false, ct);
                    string? text = a.Reference is { } r ? analysis.AlternateTextOf(r.Number) : null;
                    if (string.IsNullOrWhiteSpace(text) && a.GetDictionary("A") is { } action && action.GetName("S") == "URI" && action.GetString("URI") is { } uri)
                    {
                        // The address is the only description there is; assistive technology reads it as the link's name.
                        text = uri.GetText();
                    }

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        a.SetEntry("Contents", PdfString.FromText(text));
                    }
                }
            }

            if (any && page.GetName("Tabs") != "S")
            {
                page.SetEntry("Tabs", new PdfName("S"));
            }
        }
    }
}
