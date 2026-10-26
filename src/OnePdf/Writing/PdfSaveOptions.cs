namespace OnePdf;

/// <summary>Which cross-reference form a saved file uses.</summary>
public enum PdfXrefFormat
{
    /// <summary>Follow the document: a cross-reference stream when its newest revision has one, otherwise a classic table.</summary>
    Auto = 0,

    /// <summary>A classic <c>xref</c> table (ISO 32000-2 7.5.4). Objects are not packed into object streams.</summary>
    Table = 1,

    /// <summary>A cross-reference stream (7.5.8), and unless <see cref="PdfSaveOptions.UseObjectStreams"/> is false, object streams (7.5.7). Needs PDF 1.5.</summary>
    Stream = 2,
}

/// <summary>What a full save does about the document's encryption.</summary>
public enum PdfEncryptionAction
{
    /// <summary>Keep what the document has: the same key and passwords, every string and stream re-encrypted under the new object numbers. A plain document stays plain.</summary>
    Keep = 0,

    /// <summary>Write the document without encryption.</summary>
    Remove = 1,

    /// <summary>Encrypt with the new settings in <see cref="PdfSaveOptions.NewEncryption"/>, replacing any existing encryption.</summary>
    Set = 2,
}

/// <summary>New standard-security-handler encryption for a saved file (ISO 32000-2 7.6.4).</summary>
public sealed class PdfEncryptionOptions
{
    /// <summary>The user password. Default empty: the file opens without a password.</summary>
    public string UserPassword { get; init; } = string.Empty;

    /// <summary>The owner password. Null or empty means the user password is used for both.</summary>
    public string? OwnerPassword { get; init; }

    /// <summary>The permission flags to record (reported to readers; OnePDF never enforces them). Default <see cref="PdfPermissions.All"/>.</summary>
    public PdfPermissions Permissions { get; init; } = PdfPermissions.All;

    /// <summary>
    /// <see cref="PdfCipher.Aes256"/> (revision 6, PDF 2.0; the default) or <see cref="PdfCipher.Aes128"/> (revision 4, PDF 1.6).
    /// RC4 is not offered for new encryption.
    /// </summary>
    public PdfCipher Cipher { get; init; } = PdfCipher.Aes256;

    /// <summary>False leaves the XMP metadata stream unencrypted so tools can read it without a password. Default true.</summary>
    public bool EncryptMetadata { get; init; } = true;
}

/// <summary>Options of <see cref="PdfDocument.Save(Stream, PdfSaveOptions?, CancellationToken)"/> and <see cref="PdfDocument.SaveIncremental(Stream, PdfSaveOptions?, CancellationToken)"/>.</summary>
public sealed class PdfSaveOptions
{
    /// <summary>The cross-reference form. Default <see cref="PdfXrefFormat.Auto"/>. An incremental save always continues the form of the file's newest revision.</summary>
    public PdfXrefFormat XrefFormat { get; init; }

    /// <summary>With a cross-reference stream, pack every non-stream object into object streams. Default true. Ignored for tables and for incremental saves.</summary>
    public bool UseObjectStreams { get; init; } = true;

    /// <summary>
    /// Flate-compress streams that have no filter (not metadata). A stream that does not get smaller is written as it was.
    /// Default false, so a save changes as little as possible.
    /// </summary>
    public bool CompressStreams { get; init; }

    /// <summary>
    /// The lowest PDF version to write, such as <c>1.7</c>. A full save writes the highest of this, the document's version and
    /// what its features need (1.5 for cross-reference streams, 1.6 for AES-128, 2.0 for AES-256). An incremental save keeps the header.
    /// </summary>
    public string? MinimumVersion { get; init; }

    /// <summary>
    /// Sixteen bytes to write as both strings of the trailer's <c>/ID</c> in a full save, replacing any existing ID, so that the same
    /// document always saves to the same bytes. Default null: an existing ID is kept and a document without one gets random strings.
    /// Encrypted output is still not reproducible (random IVs and salts).
    /// </summary>
    public byte[]? DocumentId { get; init; }

    /// <summary>What a full save does about encryption. Default <see cref="PdfEncryptionAction.Keep"/>. An incremental save only supports Keep.</summary>
    public PdfEncryptionAction Encryption { get; init; }

    /// <summary>The settings for <see cref="PdfEncryptionAction.Set"/>.</summary>
    public PdfEncryptionOptions? NewEncryption { get; init; }

    /// <summary>
    /// Write the file as PDF/A (ISO 19005-2 or -3) as <see cref="PdfAOptions"/> says. A full save then first repairs what it can
    /// (<see cref="PdfDocument.PreparePdfA"/>: output intent, XMP, annotation flags and appearances, library fonts) and checks the rest;
    /// if anything is still wrong it throws <see cref="PdfAException"/> listing every violation and writes nothing. The document must be
    /// editable and the changes stay in it. The header version is at least 1.7. Not supported with <see cref="PdfDocument.SaveIncremental(Stream, PdfSaveOptions?, CancellationToken)"/>,
    /// which would append to a file that may not conform. Default null: an ordinary save.
    /// </summary>
    public PdfAOptions? PdfA { get; init; }

    /// <summary>
    /// Write the file as PDF/UA-1 (ISO 14289-1), alone or together with <see cref="PdfA"/>. A full save then first repairs what needs no
    /// judgement (<see cref="PdfDocument.PreparePdfUA"/>: tagged marker, language, title display, tab order, link descriptions, XMP
    /// identification, library fonts) and checks the rest; if anything is still wrong it throws <see cref="PdfUAException"/> (or
    /// <see cref="PdfAException"/> when PDF/A is also requested) listing every violation, and writes nothing. The document must be
    /// editable and the changes stay in it. This is offered for documents OnePDF laid out or created with tags and for existing files tagged through
    /// <see cref="PdfDocument.ApplyTagging(PdfTaggingPlan, CancellationToken)"/>; it never tags on its own judgement,
    /// and a clean result is not proof of conformance. Not supported with <see cref="PdfDocument.SaveIncremental(Stream, PdfSaveOptions?, CancellationToken)"/>.
    /// Default null.
    /// </summary>
    public PdfUAOptions? PdfUA { get; init; }

    // The header version a PDF/A save writes whatever the source says (ISO 19005-2 6.1.2 allows 1.0 to 1.7).
    internal string? ExactVersion { get; init; }
}
