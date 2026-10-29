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
