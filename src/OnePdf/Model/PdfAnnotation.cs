namespace OnePdf;

/// <summary>Annotation flags (ISO 32000-2 Table 167).</summary>
[Flags]
public enum PdfAnnotationOptions
{
    /// <summary>No flag.</summary>
    None = 0,

    /// <summary>Do not display a non-standard annotation without an appearance.</summary>
    Invisible = 1,

    /// <summary>Do not display or print.</summary>
    Hidden = 2,

    /// <summary>Print when the page is printed.</summary>
    Print = 4,

    /// <summary>Do not scale with the page.</summary>
    NoZoom = 8,

    /// <summary>Do not rotate with the page.</summary>
    NoRotate = 16,

    /// <summary>Do not display on screen (it may still print).</summary>
    NoView = 32,

    /// <summary>Do not allow interaction.</summary>
    ReadOnly = 64,

    /// <summary>Do not allow deletion or modification.</summary>
    Locked = 128,

    /// <summary>Invert the NoView flag for some events.</summary>
    ToggleNoView = 256,

    /// <summary>Do not allow the contents to change.</summary>
    LockedContents = 512,
}

/// <summary>Which appearance of an annotation to select (ISO 32000-2 12.5.5).</summary>
public enum PdfAppearanceKind
{
    /// <summary>The normal appearance <c>/N</c>.</summary>
    Normal = 0,

    /// <summary>The rollover appearance <c>/R</c>.</summary>
    Rollover = 1,

    /// <summary>The down appearance <c>/D</c>.</summary>
    Down = 2,
}

/// <summary>
/// An annotation of a page, including links and form widgets. Everything is data: actions and URIs are never performed or
/// fetched, and appearance streams are only located.
/// </summary>
public sealed class PdfAnnotation
{
    private readonly PdfDocument _doc;
    private Lazy<PdfAction?> _action = null!;
    private Lazy<PdfDestination?> _destination = null!;

    internal PdfAnnotation(PdfDocument doc, PdfPage page, PdfDictionary dictionary)
    {
        _doc = doc;
        Page = page;
        Dictionary = dictionary;
        _action = new Lazy<PdfAction?>(() => dictionary.GetDictionary("A") is { } a ? new PdfAction(doc, a) : null, LazyThreadSafetyMode.ExecutionAndPublication);
        _destination = new Lazy<PdfDestination?>(
            () => dictionary.ContainsKey("Dest") ? doc.ResolveDestination(dictionary.Get("Dest"), 0) : Action?.Destination,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The page the annotation is on.</summary>
    public PdfPage Page { get; }

    /// <summary>The annotation dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The subtype without the slash (<c>Link</c>, <c>Text</c>, <c>Highlight</c>, <c>Widget</c>, <c>FileAttachment</c> and so on); empty when missing.</summary>
    public string Subtype => Dictionary.GetName("Subtype") ?? string.Empty;

    /// <summary>The annotation rectangle in default user space; an empty rectangle at the origin when missing or malformed.</summary>
    public PdfRectangle Rect => PdfRectangle.From(Dictionary.Get("Rect")) ?? default;

    /// <summary>The text of <c>/Contents</c>, or null.</summary>
    public string? Contents => Dictionary.GetString("Contents")?.GetText();

    /// <summary>The unique name <c>/NM</c>, or null.</summary>
    public string? Name => Dictionary.GetString("NM")?.GetText();

    /// <summary>The author or title <c>/T</c> of a markup annotation, or null.</summary>
    public string? Title => Dictionary.GetString("T")?.GetText();

    /// <summary>The modification date <c>/M</c> when it parses as a date, otherwise null.</summary>
    public DateTimeOffset? ModifiedDate => Dictionary.GetString("M") is { } m && PdfDate.TryParse(m.GetText(), out DateTimeOffset d) ? d : null;

    /// <summary>The flags <c>/F</c>.</summary>
    public PdfAnnotationOptions Flags => (PdfAnnotationOptions)(int)Math.Clamp(Dictionary.GetInt("F"), 0, int.MaxValue);

    /// <summary>The color <c>/C</c> components (0, 1, 3 or 4 numbers), or null when absent.</summary>
    public IReadOnlyList<double>? Color
    {
        get
        {
            if (Dictionary.Get("C") is not PdfArray a)
            {
                return null;
            }

            var list = new List<double>(a.Count);
            for (int i = 0; i < Math.Min(a.Count, 4); i++)
            {
                list.Add(a.GetDouble(i));
            }

            return list;
        }
    }

    /// <summary>The <c>/QuadPoints</c> of a markup or link annotation as groups of eight numbers; empty when absent. A trailing partial group is dropped.</summary>
    public IReadOnlyList<double> QuadPoints
    {
        get
        {
            if (Dictionary.Get("QuadPoints") is not PdfArray a)
            {
                return [];
            }

            int n = Math.Min(a.Count, _doc.Limits.MaxObjects) / 8 * 8;
            var list = new List<double>(n);
            for (int i = 0; i < n; i++)
            {
                list.Add(a.GetDouble(i));
            }

            return list;
        }
    }

    /// <summary>The action <c>/A</c>, or null.</summary>
    public PdfAction? Action => _action.Value;

    /// <summary>The destination: <c>/Dest</c> if present, otherwise the destination of a GoTo action; null if neither leads to one.</summary>
    public PdfDestination? Destination => _destination.Value;

    /// <summary>The target address of a link whose action is a URI action, else null. Never fetched.</summary>
    public string? Uri => Action?.Uri;

    /// <summary>True when the annotation has an appearance dictionary <c>/AP</c>.</summary>
    public bool HasAppearance => Dictionary.GetDictionary("AP") is not null;

    /// <summary>
    /// The appearance stream to draw: the stream of the chosen kind, or for a dictionary of states the one named by <c>/AS</c>.
    /// Null when there is none. Only located here; drawing belongs to the interpreter.
    /// </summary>
    public PdfStream? GetAppearanceStream(PdfAppearanceKind kind = PdfAppearanceKind.Normal)
    {
        string key = kind switch { PdfAppearanceKind.Rollover => "R", PdfAppearanceKind.Down => "D", _ => "N" };
        PdfObject? entry = Dictionary.GetDictionary("AP")?.Get(key);
        if (entry is PdfStream s)
        {
            return s;
        }

        if (entry is PdfDictionary states)
        {
            string? state = Dictionary.GetName("AS");
            return state is not null ? states.Get(state) as PdfStream : null;
        }

        return null;
    }

    /// <summary>
    /// Synthesises a normal appearance for this annotation from its own data (ISO 32000-2 12.5.5, 12.7.4.3, 12.5.6), ignoring any
    /// stored one. Supported: widgets (text, choice, check box, radio button, push button; signature fields get none) and the
    /// Square, Circle, Line, Polygon, PolyLine, Ink, Highlight, Underline, StrikeOut, Squiggly, FreeText and Text annotations.
    /// Returns null for other subtypes or when the geometry is unusable. The result lives in memory only; the document is not
    /// changed. Problems (unencodable characters, missing fonts, cut-off input) are recorded in <see cref="PdfDocument.Diagnostics"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    public PdfGeneratedAppearance? GenerateAppearance(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int obj = Dictionary.Reference?.Number ?? -1;
        return Appearance.AppearanceGenerator.Generate(_doc, this, (sev, msg) =>
        {
            if (sev == PdfSeverity.Info)
            {
                _doc.ReportInfo(msg, obj, -1);
            }
            else
            {
                _doc.ReportWarning(msg, obj, -1);
            }
        });
    }

    /// <summary>For a file attachment annotation, the attached file; otherwise null. Its content is read only on request.</summary>
    public PdfEmbeddedFile? AttachedFile =>
        Subtype == "FileAttachment" && Dictionary.Get("FS") is PdfDictionary fs ? new PdfEmbeddedFile(_doc, fs, null, Page.Index) : null;

    /// <summary>For a pop-up related annotation, <c>/Popup</c>'s dictionary; otherwise null.</summary>
    public PdfDictionary? Popup => Dictionary.GetDictionary("Popup");
}
