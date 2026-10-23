namespace OnePdf;

/// <summary>
/// Bounds that keep hostile or damaged files from exhausting memory or time. Each bound is checked against the declared
/// or running value before the allocation or work it guards. All defaults are untuned starting values.
/// </summary>
public sealed record PdfLimits
{
    /// <summary>The defaults, safe for untrusted input.</summary>
    public static PdfLimits Default { get; } = new();

    /// <summary>No practical bounds; for trusted input only. Sizes are still capped by what a .NET array can hold.</summary>
    public static PdfLimits Unrestricted { get; } = new()
    {
        MaxDecodedStreamBytes = int.MaxValue,
        MaxExpansionRatio = long.MaxValue / 2,
        MaxNestingDepth = 100_000,
        MaxRecursionDepth = 100_000,
        MaxObjects = int.MaxValue,
        MaxOperatorsPerPage = long.MaxValue,
        MaxType4Operations = long.MaxValue,
        MaxImagePixels = long.MaxValue,
        MaxMetadataBytes = int.MaxValue,
        MaxAppearanceText = 1 << 24,
        MaxAppearanceItems = 1 << 24,
    };

    /// <summary>Most characters of a field value or annotation text that a generated appearance shows; the rest is cut off with a diagnostic. Default 65,536.</summary>
    public int MaxAppearanceText { get; init; } = 1 << 16;

    /// <summary>Most items (quadrilaterals, vertices, ink points, list box rows, comb cells) that a generated appearance draws; the rest is left out with a diagnostic. Default 65,536.</summary>
    public int MaxAppearanceItems { get; init; } = 1 << 16;

    /// <summary>Largest decoded stream in bytes. Default 256 MiB.</summary>
    public long MaxDecodedStreamBytes { get; init; } = 256L << 20;

    /// <summary>Largest ratio of decoded to encoded bytes for expanding filters, applied once the output passes <see cref="ExpansionCheckThreshold"/>. Default 1100.</summary>
    public long MaxExpansionRatio { get; init; } = 1100;

    /// <summary>Output size in bytes below which the expansion ratio is not checked. Default 16 MiB.</summary>
    public long ExpansionCheckThreshold { get; init; } = 16L << 20;

    /// <summary>Deepest nesting of arrays and dictionaries inside one object. Default 256.</summary>
    public int MaxNestingDepth { get; init; } = 256;

    /// <summary>Deepest chain of form XObjects, patterns, Type 3 glyphs and annotations, and of indirect references followed in a row. Default 32.</summary>
    public int MaxRecursionDepth { get; init; } = 32;

    /// <summary>Most indirect objects a document may declare. Default 2^23.</summary>
    public int MaxObjects { get; init; } = 1 << 23;

    /// <summary>Most content operators on one page. Default 2^24.</summary>
    public long MaxOperatorsPerPage { get; init; } = 1L << 24;

    /// <summary>Most operations in one evaluation of a Type 4 function. Default 100,000.</summary>
    public long MaxType4Operations { get; init; } = 100_000;

    /// <summary>Most pixels in one image. Default 2^28.</summary>
    public long MaxImagePixels { get; init; } = 1L << 28;

    /// <summary>Most bytes of metadata read. Default 64 MiB.</summary>
    public int MaxMetadataBytes { get; init; } = 64 << 20;
}

/// <summary>Options for opening a document.</summary>
public sealed record PdfOpenOptions
{
    /// <summary>
    /// When true, every repair and every tolerated irregularity (a diagnostic of severity <see cref="PdfSeverity.Warning"/> or
    /// worse) throws <see cref="PdfFormatException"/> instead of being recorded. Default false.
    /// </summary>
    public bool Strict { get; init; }

    /// <summary>The bounds to apply. Default <see cref="PdfLimits.Default"/>.</summary>
    public PdfLimits Limits { get; init; } = PdfLimits.Default;

    /// <summary>
    /// Budget in bytes for parsed objects and decoded streams kept in memory. Least recently used entries are dropped
    /// beyond it and parsed again on demand. Default 64 MiB.
    /// </summary>
    public long ObjectCacheBytes { get; init; } = 64L << 20;

    /// <summary>
    /// The password to open an encrypted document with, tried as the user password and then as the owner password. Null or
    /// empty means the empty password, which opens documents that have no user password. A wrong password raises
    /// <see cref="PdfPasswordException"/>. For revisions 5 and 6 the text is prepared as the specification says (SASLprep-style
    /// normalization, UTF-8, at most 127 bytes); earlier revisions use the PDFDocEncoding / Latin-1 bytes.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>For stream sources: leave the stream open when the document is disposed. Default false.</summary>
    public bool LeaveOpen { get; init; }

    /// <summary>
    /// Opens the document for changing: its objects can then be modified, added and removed, and saved with
    /// <see cref="PdfDocument.Save(Stream, PdfSaveOptions?, CancellationToken)"/> or <see cref="PdfDocument.SaveIncremental(Stream, PdfSaveOptions?, CancellationToken)"/>.
    /// Every object that is read stays in memory for the life of the document, so the object cache budget no longer bounds memory.
    /// Default false: a document opened without it is read-only and thread-safe as always (it can still be saved unchanged).
    /// </summary>
    public bool AllowEditing { get; init; }
}

/// <summary>How serious a <see cref="PdfDiagnostic"/> is.</summary>
public enum PdfSeverity
{
    /// <summary>Worth knowing; nothing was wrong with the file.</summary>
    Info = 0,

    /// <summary>The file deviates from the specification and was repaired or tolerated.</summary>
    Warning = 1,

    /// <summary>Content could not be recovered and was dropped or replaced by an empty value.</summary>
    Error = 2,
}

/// <summary>A repair or irregularity found while reading a document.</summary>
/// <param name="Severity">How serious it is.</param>
/// <param name="ObjectNumber">The indirect object number concerned, or -1.</param>
/// <param name="Offset">The byte offset in the file concerned, or -1.</param>
/// <param name="Message">A plain-language description.</param>
public sealed record PdfDiagnostic(PdfSeverity Severity, int ObjectNumber, long Offset, string Message)
{
    /// <inheritdoc/>
    public override string ToString() =>
        $"{Severity}: {Message}" + (ObjectNumber >= 0 ? $" (object {ObjectNumber})" : "") + (Offset >= 0 ? $" (offset {Offset})" : "");
}
