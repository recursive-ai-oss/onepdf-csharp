namespace OnePdf;

public sealed partial class PdfDocument
{
    /// <summary>
    /// The catalog's <c>/ViewerPreferences</c> (ISO 32000-2 12.2), or null when the catalog has none. Values of the wrong type or outside their
    /// range are left null and recorded as warnings in <see cref="Diagnostics"/>; this never throws for malformed input (with
    /// <see cref="PdfOpenOptions.Strict"/> a warning is an error, as everywhere). Each call reads the dictionary afresh.
    /// </summary>
    public PdfViewerPreferences? ViewerPreferences
    {
        get
        {
            ThrowIfDisposed();
            PdfDictionary catalog = Catalog;
            if (catalog.GetDictionary("ViewerPreferences") is not { } vp)
            {
                if (catalog.Get("ViewerPreferences") is not null)
                {
                    ReportWarning("The catalog's /ViewerPreferences is not a dictionary; ignored.", catalog.Reference?.Number ?? -1, -1);
                }

                return null;
            }

            int owner = vp.Reference?.Number ?? catalog.Reference?.Number ?? -1;
            return PdfViewerPreferences.Read(vp, m => ReportWarning(m, owner, -1));
        }
    }

    /// <summary>
    /// Writes the set properties of <paramref name="preferences"/> into the catalog's <c>/ViewerPreferences</c>, creating it when needed.
    /// Properties that are null and entries this library does not model are left as they are. Saved by
    /// <see cref="Save(Stream, PdfSaveOptions?, CancellationToken)"/> and <see cref="SaveIncremental(Stream, PdfSaveOptions?, CancellationToken)"/>.
    /// </summary>
    /// <param name="preferences">The values to set.</param>
    /// <exception cref="ArgumentException">A value cannot be written: <c>NumCopies</c> below 1, an empty, reversed, overlapping, descending or
    /// out-of-range (beyond <see cref="PageCount"/>) page range, or an undefined enum value. Nothing is changed then.</exception>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    public void SetViewerPreferences(PdfViewerPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ThrowIfDisposed();
        preferences.Validate(preferences.PrintPageRange is null ? null : PageCount);
        PdfDictionary catalog = Catalog;
        PdfDictionary? vp = catalog.GetDictionary("ViewerPreferences");
        if (vp is null)
        {
            catalog.SetEntry("ViewerPreferences", new PdfDictionary());
            vp = catalog.GetDictionary("ViewerPreferences")!;
        }

        preferences.ApplyTo(vp);
    }

    /// <summary>
    /// The happy path for print settings: sets <c>/PrintScaling</c>, <c>/Duplex</c>, <c>/NumCopies</c> and <c>/PrintPageRange</c> (the ones
    /// given) and leaves everything else in <c>/ViewerPreferences</c> alone. Equivalent to <see cref="SetViewerPreferences"/> with
    /// <see cref="PdfViewerPreferences.ForPrint"/>.
    /// </summary>
    /// <param name="scaling">The print scaling, or null to leave it.</param>
    /// <param name="duplex">The duplex mode, or null to leave it.</param>
    /// <param name="copies">The number of copies (at least 1), or null to leave it.</param>
    /// <param name="pageRanges">One-based page ranges, or null to leave it.</param>
    /// <exception cref="ArgumentException">See <see cref="SetViewerPreferences"/>.</exception>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    public void SetPrintPreferences(PdfPrintScaling? scaling = null, PdfDuplex? duplex = null, int? copies = null, IReadOnlyList<PdfPrintPageRange>? pageRanges = null) =>
        SetViewerPreferences(new PdfViewerPreferences { PrintScaling = scaling, Duplex = duplex, NumCopies = copies, PrintPageRange = pageRanges });
}
