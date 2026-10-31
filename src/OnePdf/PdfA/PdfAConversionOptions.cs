namespace OnePdf;

/// <summary>What <see cref="PdfDocument.ConvertToPdfA(Stream, PdfAConversionOptions?, CancellationToken)"/> and <see cref="PdfAConverter"/> aim for and how far they may go.</summary>
public sealed class PdfAConversionOptions
{
    /// <summary>The part of ISO 19005 to produce. Default <see cref="PdfAPart.Part2"/>. Part 3 keeps embedded files (each is associated with the document through <c>/AF</c>); part 2 removes them.</summary>
    public PdfAPart Part { get; init; } = PdfAPart.Part2;

    /// <summary>
    /// The conformance level: <see cref="PdfAConformance.B"/> (default) or <see cref="PdfAConformance.U"/>, which also needs a Unicode mapping
    /// for every font. Level A needs a logical structure that conversion does not invent and is refused with <see cref="ArgumentException"/>.
    /// </summary>
    public PdfAConformance Conformance { get; init; } = PdfAConformance.B;

    /// <summary>
    /// The password used to open the file by the static helpers (<see cref="PdfAConverter.Convert(string, string, PdfAConversionOptions?, CancellationToken)"/>).
    /// Encryption is removed from the output; the password is not stored. Ignored by <see cref="PdfDocument.ConvertToPdfA(Stream, PdfAConversionOptions?, CancellationToken)"/>, where the document is already open.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Embed metric-compatible substitutes for fonts the file names without embedding (Arial, Times New Roman, Courier New, the standard 14 and
    /// their aliases), with the font provider's faces. Default true. Needs a provider: reference <c>RecursiveAI.OnePdf.Fonts</c> and call
    /// <c>FontsPackage.Register()</c> once, or set <see cref="PdfDocument.FontProvider"/>. Without one such fonts are listed as violations and the file is refused.
    /// The letter shapes in a viewer that had the real font change to the substitute's.
    /// </summary>
    public bool EmbedSubstituteFonts { get; init; } = true;

    /// <summary>
    /// For fonts that are not embedded and are not one of the standard faces (Verdana, Palatino, Univers...): embed the standard face of the same
    /// kind (sans, serif or monospaced, bold, italic) instead of refusing the file. Glyph positions stay as the file set them, because the
    /// substitute takes the widths of the font it replaces; only the letter shapes change. Fonts flagged symbolic are never replaced this way, since their codes
    /// do not mean letters. Default true; every such replacement is listed in <see cref="PdfAConversionResult.Warnings"/>. Needs <see cref="EmbedSubstituteFonts"/>.
    /// </summary>
    public bool SubstituteUnknownFonts { get; init; } = true;

    /// <summary>
    /// The font provider that supplies the substitute faces for <see cref="EmbedSubstituteFonts"/>, for example <c>OnePdf.Fonts.FontsPackage.Provider</c>.
    /// Default null: the document's own <see cref="PdfDocument.FontProvider"/>, then the providers registered process-wide.
    /// </summary>
    public IPdfFontProvider? FontProvider { get; init; }

    /// <summary>
    /// Remove what ISO 19005 forbids and a reader does not need to show the page: scripts and actions that are not allowed, hidden and
    /// forbidden annotations, embedded files (part 2), external stream references, extra metadata streams, transparency transfer functions.
    /// Default true. When false only the repairs that add or fix are made, and anything forbidden that remains is a violation.
    /// </summary>
    public bool RemoveForbiddenFeatures { get; init; } = true;

    /// <summary>Sixteen bytes to write as the file identifier, so the same input gives the same output. Default null: the existing identifier is kept, or a random one written.</summary>
    public byte[]? DocumentId { get; init; }

    /// <summary>The language written as the catalog's <c>/Lang</c> when the file has none. Default null: none is written (level B and U do not need one).</summary>
    public string? Language { get; init; }

    internal PdfAOptions ToPdfAOptions(bool embedSubstitutes = false) => new()
    {
        Part = Part,
        Conformance = Conformance,
        EmbedSubstituteFonts = embedSubstitutes,
        Language = Language,
    };
}

/// <summary>The kinds of change a conversion makes to a file.</summary>
public enum PdfAChangeKind
{
    /// <summary>Encryption was removed (the file was opened with the password).</summary>
    EncryptionRemoved,

    /// <summary>JavaScript, launch and other actions that ISO 19005 forbids were removed.</summary>
    ActionsRemoved,

    /// <summary>Annotations were removed: hidden ones, types PDF/A forbids, file attachments in part 2, and ones with no appearance that could be drawn.</summary>
    AnnotationsRemoved,

    /// <summary>Embedded files were removed (part 2).</summary>
    EmbeddedFilesRemoved,

    /// <summary>Embedded files were completed with the entries part 3 requires.</summary>
    EmbeddedFilesCompleted,

    /// <summary>References to external stream data were removed.</summary>
    ExternalReferencesRemoved,

    /// <summary>Streams that used a filter PDF/A forbids (LZW) were stored again with Flate.</summary>
    StreamsRecompressed,

    /// <summary>Metadata streams on pages, images and other objects were removed; only the document's own packet is kept.</summary>
    MetadataRemoved,

    /// <summary>Font programs were embedded for fonts the file named without embedding them.</summary>
    FontsEmbedded,

    /// <summary>A font dictionary was repaired so that it agrees with the embedded program.</summary>
    FontsRepaired,

    /// <summary>The output intent was added, or a profile was chosen for the device colours the file uses.</summary>
    OutputIntentAdded,

    /// <summary>Device colour spaces were put under a default colour space.</summary>
    ColourSpacesAdded,

    /// <summary>Graphics state entries or interactive form flags that PDF/A forbids were removed or reset.</summary>
    ForbiddenEntriesRemoved,

    /// <summary>The XMP packet was written or replaced.</summary>
    MetadataWritten,

    /// <summary>Another repair.</summary>
    Other,
}

/// <summary>One kind of change a conversion made, and how many times.</summary>
public sealed class PdfAChange
{
    internal PdfAChange(PdfAChangeKind kind, string description, int count)
    {
        Kind = kind;
        Description = description;
        Count = count;
    }

    /// <summary>The kind of change.</summary>
    public PdfAChangeKind Kind { get; }

    /// <summary>What was done, in plain words (the first occurrence's wording).</summary>
    public string Description { get; }

    /// <summary>How many objects or entries it was done to.</summary>
    public int Count { get; }

    /// <inheritdoc />
    public override string ToString() => Count > 1 ? $"{Description} (x{Count})" : Description;
}

/// <summary>What a conversion produced.</summary>
public sealed class PdfAConversionResult
{
    internal PdfAConversionResult(string profile, IReadOnlyList<PdfAChange> changes, IReadOnlyList<string> warnings)
    {
        Profile = profile;
        Changes = changes;
        Warnings = warnings;
    }

    /// <summary>The profile the output claims, such as <c>PDF/A-2b</c>.</summary>
    public string Profile { get; }

    /// <summary>The changes made to the file, one entry per kind. Empty when the file already conformed.</summary>
    public IReadOnlyList<PdfAChange> Changes { get; }

    /// <summary>Things that changed the look of the file or that a reader should know, such as substituted fonts or removed annotations.</summary>
    public IReadOnlyList<string> Warnings { get; }
}
