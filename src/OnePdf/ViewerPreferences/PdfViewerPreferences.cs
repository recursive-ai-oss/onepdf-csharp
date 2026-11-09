namespace OnePdf;

/// <summary>The print scaling a viewer's print dialog starts with (<c>/PrintScaling</c>, ISO 32000-2 12.2 Table 147).</summary>
public enum PdfPrintScaling
{
    /// <summary><c>/None</c>: print at 100%, without fitting the page to the paper.</summary>
    None,

    /// <summary><c>/AppDefault</c>: the viewer's own default, usually shrink-to-fit. This is also what a file without the entry means.</summary>
    AppDefault,
}

/// <summary>The duplex mode a viewer's print dialog starts with (<c>/Duplex</c>, ISO 32000-2 12.2 Table 147).</summary>
public enum PdfDuplex
{
    /// <summary><c>/Simplex</c>: single-sided.</summary>
    Simplex,

    /// <summary><c>/DuplexFlipShortEdge</c>: double-sided, flipping on the short edge.</summary>
    DuplexFlipShortEdge,

    /// <summary><c>/DuplexFlipLongEdge</c>: double-sided, flipping on the long edge.</summary>
    DuplexFlipLongEdge,
}

/// <summary>A page box named by the <c>/ViewArea</c>, <c>/ViewClip</c>, <c>/PrintArea</c> and <c>/PrintClip</c> preferences.</summary>
public enum PdfViewerBox
{
    /// <summary><c>/MediaBox</c>.</summary>
    MediaBox,

    /// <summary><c>/CropBox</c>.</summary>
    CropBox,

    /// <summary><c>/BleedBox</c>.</summary>
    BleedBox,

    /// <summary><c>/TrimBox</c>.</summary>
    TrimBox,

    /// <summary><c>/ArtBox</c>.</summary>
    ArtBox,
}

/// <summary>
/// A range of pages for <c>/PrintPageRange</c>. Page numbers are <b>one-based and inclusive</b> here, as in the file (ISO 32000-2 12.2 Table 147:
/// "the first page of the PDF file shall be denoted by 1"); this differs from the zero-based <c>doc.Pages</c> index on purpose, so what you
/// write is what a file dump shows.
/// </summary>
/// <param name="FirstPage">The first page, at least 1.</param>
/// <param name="LastPage">The last page, not before <paramref name="FirstPage"/>.</param>
public readonly record struct PdfPrintPageRange(int FirstPage, int LastPage);

/// <summary>
/// The catalog's <c>/ViewerPreferences</c> dictionary (ISO 32000-2 12.2): how a viewer opens the file and what its print dialog starts with.
/// Every property is nullable; null means "absent": not written, and not changed when the object is applied to an existing document.
/// </summary>
/// <remarks>
/// <para>Use it with <see cref="PdfDocument.ViewerPreferences"/> / <see cref="PdfDocument.SetViewerPreferences"/> for an existing document and with
/// <see cref="PdfDocumentWriterOptions.ViewerPreferences"/> for a new one. Entries this class does not model are never dropped when an edited document is saved.</para>
/// <para>Viewers treat these as hints; OnePDF only stores them. <c>PrintArea</c>, <c>PrintClip</c>, <c>ViewArea</c> and <c>ViewClip</c> are
/// deprecated in PDF 2.0 (and ignored by most viewers) but are still read and written.</para>
/// </remarks>
public sealed class PdfViewerPreferences
{
    /// <summary><c>/PrintScaling</c>.</summary>
    public PdfPrintScaling? PrintScaling { get; set; }

    /// <summary><c>/Duplex</c>.</summary>
    public PdfDuplex? Duplex { get; set; }

    /// <summary>
    /// <c>/PrintPageRange</c>: the pages the print dialog selects (one-based). Ranges must be ascending and must not overlap or touch each
    /// other's pages. Null leaves the entry alone; on write, an empty list is rejected.
    /// </summary>
    public IReadOnlyList<PdfPrintPageRange>? PrintPageRange { get; set; }

    /// <summary><c>/NumCopies</c>: the number of copies the print dialog starts with, at least 1 (viewers cap it at their own maximum).</summary>
    public int? NumCopies { get; set; }

    /// <summary><c>/PickTrayByPDFSize</c>: choose the paper tray by the page size (PDF 1.7).</summary>
    public bool? PickTrayByPdfSize { get; set; }

    /// <summary><c>/PrintArea</c>: the box to print (deprecated in PDF 2.0).</summary>
    public PdfViewerBox? PrintArea { get; set; }

    /// <summary><c>/PrintClip</c>: the box to clip printing to (deprecated in PDF 2.0).</summary>
    public PdfViewerBox? PrintClip { get; set; }

    /// <summary><c>/ViewArea</c>: the box to view (deprecated in PDF 2.0).</summary>
    public PdfViewerBox? ViewArea { get; set; }

    /// <summary><c>/ViewClip</c>: the box to clip viewing to (deprecated in PDF 2.0).</summary>
    public PdfViewerBox? ViewClip { get; set; }

    /// <summary><c>/DisplayDocTitle</c>: show the title instead of the file name.</summary>
    public bool? DisplayDocTitle { get; set; }

    /// <summary><c>/HideToolbar</c>.</summary>
    public bool? HideToolbar { get; set; }

    /// <summary><c>/HideMenubar</c>.</summary>
    public bool? HideMenubar { get; set; }

    /// <summary><c>/HideWindowUI</c>.</summary>
    public bool? HideWindowUI { get; set; }

    /// <summary><c>/FitWindow</c>.</summary>
    public bool? FitWindow { get; set; }

    /// <summary><c>/CenterWindow</c>.</summary>
    public bool? CenterWindow { get; set; }

    /// <summary>
    /// The dictionary this was read from, with every entry including those not modelled here; null for an object you made yourself.
    /// Read-only unless the document was opened with <see cref="PdfOpenOptions.AllowEditing"/>.
    /// </summary>
    public PdfDictionary? Dictionary { get; private set; }

    /// <summary>
    /// Prints at full size, optionally with duplex and copies: the usual "print this as designed" set.
    /// </summary>
    /// <param name="scaling">The print scaling.</param>
    /// <param name="duplex">The duplex mode, or null to leave it out.</param>
    /// <param name="copies">The number of copies, or null to leave it out.</param>
    /// <param name="pageRanges">One-based page ranges, or null to leave them out.</param>
    /// <returns>A new preferences object.</returns>
    public static PdfViewerPreferences ForPrint(PdfPrintScaling scaling, PdfDuplex? duplex = null, int? copies = null, IReadOnlyList<PdfPrintPageRange>? pageRanges = null) =>
        new() { PrintScaling = scaling, Duplex = duplex, NumCopies = copies, PrintPageRange = pageRanges };

    private static readonly string[] BoxNames = ["MediaBox", "CropBox", "BleedBox", "TrimBox", "ArtBox"];
    private static readonly string[] ScalingNames = ["None", "AppDefault"];
    private static readonly string[] DuplexNames = ["Simplex", "DuplexFlipShortEdge", "DuplexFlipLongEdge"];

    /// <summary>Checks every set value and throws <see cref="ArgumentException"/> for one that cannot be written; <paramref name="pageCount"/> (when known) bounds the page ranges.</summary>
    internal void Validate(int? pageCount)
    {
        if (NumCopies is < 1)
        {
            throw new ArgumentException("NumCopies must be at least 1.");
        }

        CheckEnum(PrintScaling, ScalingNames.Length, nameof(PrintScaling));
        CheckEnum(Duplex, DuplexNames.Length, nameof(Duplex));
        CheckEnum(PrintArea, BoxNames.Length, nameof(PrintArea));
        CheckEnum(PrintClip, BoxNames.Length, nameof(PrintClip));
        CheckEnum(ViewArea, BoxNames.Length, nameof(ViewArea));
        CheckEnum(ViewClip, BoxNames.Length, nameof(ViewClip));

        if (PrintPageRange is { } ranges)
        {
            if (ranges.Count == 0)
            {
                throw new ArgumentException("PrintPageRange must hold at least one range, or be null.");
            }

            long previousLast = 0;
            foreach (PdfPrintPageRange r in ranges)
            {
                if (r.FirstPage < 1 || r.LastPage < r.FirstPage)
                {
                    throw new ArgumentException($"The print page range {r.FirstPage}-{r.LastPage} is not valid: pages are one-based and the last page must not precede the first.");
                }

                if (r.FirstPage <= previousLast)
                {
                    throw new ArgumentException("Print page ranges must be ascending and must not overlap.");
                }

                if (pageCount is { } n && r.LastPage > n)
                {
                    throw new ArgumentException($"The print page range {r.FirstPage}-{r.LastPage} lies beyond the document's {n} pages.");
                }

                previousLast = r.LastPage;
            }
        }
    }

    private static void CheckEnum<T>(T? value, int count, string name) where T : struct, Enum
    {
        if (value is { } v && ((int)(object)v < 0 || (int)(object)v >= count))
        {
            throw new ArgumentException($"{name} is not a defined value.");
        }
    }

    /// <summary>Writes the set values into <paramref name="target"/>, leaving other entries alone. Validate first.</summary>
    internal void ApplyTo(PdfDictionary target)
    {
        if (PrintScaling is { } ps)
        {
            target.SetEntry("PrintScaling", new PdfName(ScalingNames[(int)ps]));
        }

        if (Duplex is { } d)
        {
            target.SetEntry("Duplex", new PdfName(DuplexNames[(int)d]));
        }

        if (PrintPageRange is { } ranges)
        {
            // Table 147: an array of integer pairs, first page 1.
            var a = new PdfArray();
            foreach (PdfPrintPageRange r in ranges)
            {
                a.Append(new PdfInteger(r.FirstPage));
                a.Append(new PdfInteger(r.LastPage));
            }

            target.SetEntry("PrintPageRange", a);
        }

        if (NumCopies is { } c)
        {
            target.SetEntry("NumCopies", new PdfInteger(c));
        }

        SetBool(target, "PickTrayByPDFSize", PickTrayByPdfSize);
        SetBox(target, "PrintArea", PrintArea);
        SetBox(target, "PrintClip", PrintClip);
        SetBox(target, "ViewArea", ViewArea);
        SetBox(target, "ViewClip", ViewClip);
        SetBool(target, "DisplayDocTitle", DisplayDocTitle);
        SetBool(target, "HideToolbar", HideToolbar);
        SetBool(target, "HideMenubar", HideMenubar);
        SetBool(target, "HideWindowUI", HideWindowUI);
        SetBool(target, "FitWindow", FitWindow);
        SetBool(target, "CenterWindow", CenterWindow);
    }

    private static void SetBool(PdfDictionary target, string key, bool? value)
    {
        if (value is { } b)
        {
            target.SetEntry(key, new PdfBoolean(b));
        }
    }

    private static void SetBox(PdfDictionary target, string key, PdfViewerBox? value)
    {
        if (value is { } b)
        {
            target.SetEntry(key, new PdfName(BoxNames[(int)b]));
        }
    }

    /// <summary>Reads a dictionary leniently: a value of the wrong type or outside its range is left null and reported through <paramref name="warn"/>.</summary>
    internal static PdfViewerPreferences Read(PdfDictionary dict, Action<string> warn)
    {
        var p = new PdfViewerPreferences { Dictionary = dict };
        p.PrintScaling = ReadName(dict, "PrintScaling", ScalingNames, warn) is { } s ? (PdfPrintScaling)s : null;
        p.Duplex = ReadName(dict, "Duplex", DuplexNames, warn) is { } du ? (PdfDuplex)du : null;
        p.PrintArea = ReadBox(dict, "PrintArea", warn);
        p.PrintClip = ReadBox(dict, "PrintClip", warn);
        p.ViewArea = ReadBox(dict, "ViewArea", warn);
        p.ViewClip = ReadBox(dict, "ViewClip", warn);
        p.PickTrayByPdfSize = ReadBool(dict, "PickTrayByPDFSize", warn);
        p.DisplayDocTitle = ReadBool(dict, "DisplayDocTitle", warn);
        p.HideToolbar = ReadBool(dict, "HideToolbar", warn);
        p.HideMenubar = ReadBool(dict, "HideMenubar", warn);
        p.HideWindowUI = ReadBool(dict, "HideWindowUI", warn);
        p.FitWindow = ReadBool(dict, "FitWindow", warn);
        p.CenterWindow = ReadBool(dict, "CenterWindow", warn);

        if (dict.ContainsKey("NumCopies"))
        {
            if (dict.Get("NumCopies") is PdfInteger { Value: >= 1 and <= int.MaxValue } n)
            {
                p.NumCopies = (int)n.Value;
            }
            else
            {
                warn("/ViewerPreferences /NumCopies is not an integer of at least 1; ignored.");
            }
        }

        if (dict.ContainsKey("PrintPageRange"))
        {
            p.PrintPageRange = ReadRanges(dict.Get("PrintPageRange"));
            if (p.PrintPageRange is null)
            {
                warn("/ViewerPreferences /PrintPageRange is not a list of ascending, non-overlapping integer pairs; ignored.");
            }
        }

        return p;
    }

    private static PdfPrintPageRange[]? ReadRanges(PdfObject? value)
    {
        // Bounded by the array's own length, which the parser already limited.
        if (value is not PdfArray a || a.Count == 0 || a.Count % 2 != 0)
        {
            return null;
        }

        var result = new PdfPrintPageRange[a.Count / 2];
        long previousLast = 0;
        for (int i = 0; i < result.Length; i++)
        {
            if (a[2 * i] is not PdfInteger f || a[2 * i + 1] is not PdfInteger l ||
                f.Value < 1 || l.Value < f.Value || l.Value > int.MaxValue || f.Value <= previousLast)
            {
                return null;
            }

            result[i] = new PdfPrintPageRange((int)f.Value, (int)l.Value);
            previousLast = l.Value;
        }

        return result;
    }

    private static int? ReadName(PdfDictionary dict, string key, string[] names, Action<string> warn)
    {
        if (!dict.ContainsKey(key))
        {
            return null;
        }

        if (dict.Get(key) is PdfName n)
        {
            int i = Array.IndexOf(names, n.Value);
            if (i >= 0)
            {
                return i;
            }
        }

        warn($"/ViewerPreferences /{key} is not a known name; ignored.");
        return null;
    }

    private static PdfViewerBox? ReadBox(PdfDictionary dict, string key, Action<string> warn) =>
        ReadName(dict, key, BoxNames, warn) is { } i ? (PdfViewerBox)i : null;

    private static bool? ReadBool(PdfDictionary dict, string key, Action<string> warn)
    {
        if (!dict.ContainsKey(key))
        {
            return null;
        }

        if (dict.Get(key) is PdfBoolean b)
        {
            return b.Value;
        }

        warn($"/ViewerPreferences /{key} is not a boolean; ignored.");
        return null;
    }
}
