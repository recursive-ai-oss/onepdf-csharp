using System.Globalization;
using OnePdf.PdfA;
using OnePdf.Writing;

namespace OnePdf;

/// <summary>How an attached file relates to the document (ISO 32000-2 14.13.2, <c>/AFRelationship</c>).</summary>
public enum PdfAttachmentRelationship
{
    /// <summary>The file is the source the document was made from.</summary>
    Source,

    /// <summary>The file holds data the document presents, such as the table behind a chart or the XML of an invoice.</summary>
    Data,

    /// <summary>The file is an alternative representation of the document, such as the structured form of an invoice.</summary>
    Alternative,

    /// <summary>The file supplements the document without being part of its content.</summary>
    Supplement,

    /// <summary>The file is form data.</summary>
    FormData,

    /// <summary>The file is a schema for other data in the document.</summary>
    Schema,

    /// <summary>No relationship is stated. The default.</summary>
    Unspecified,
}

/// <summary>Options of <see cref="PdfDocument.AttachFile"/>.</summary>
public sealed class PdfAttachmentOptions
{
    /// <summary>The MIME type, such as <c>text/xml</c>. Default <c>application/octet-stream</c>.</summary>
    public string MimeType { get; init; } = "application/octet-stream";

    /// <summary>A description shown by viewers. Default none.</summary>
    public string? Description { get; init; }

    /// <summary>How the file relates to the document. Default <see cref="PdfAttachmentRelationship.Unspecified"/>.</summary>
    public PdfAttachmentRelationship Relationship { get; init; } = PdfAttachmentRelationship.Unspecified;

    /// <summary>The file's modification time, recorded in <c>/Params /ModDate</c>. Default null: not recorded (PDF/A-3 needs it; <see cref="PdfDocument.PreparePdfA"/> then uses the Info dates).</summary>
    public DateTimeOffset? ModificationDate { get; init; }

    /// <summary>The file's creation time, recorded in <c>/Params /CreationDate</c>. Default null: not recorded.</summary>
    public DateTimeOffset? CreationDate { get; init; }
}

public sealed partial class PdfDocument
{
    /// <summary>
    /// Checks the document against the requirements of ISO 19005-2 or -3 that OnePDF can verify from the file structure and returns
    /// every violation found (an empty list means none of the checks found a problem). Nothing is changed, so the result is the
    /// document as it stands: a missing output intent or XMP packet is reported here, whereas <see cref="PreparePdfA"/> and a save with
    /// <see cref="PdfSaveOptions.PdfA"/> add them. This is not a validator: <c>docs/pdfa.md</c> lists what is checked.
    /// </summary>
    /// <param name="options">Target part and level; null means PDF/A-2b.</param>
    /// <param name="cancellationToken">Checked between objects.</param>
    /// <exception cref="PdfLimitException">The document has more objects or content operators than <see cref="PdfLimits"/> allows.</exception>
    public IReadOnlyList<PdfAViolation> CheckPdfA(PdfAOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return new PdfAChecker(this, options ?? new PdfAOptions(), saving: false, decrypting: false, cancellationToken).Run();
    }

    /// <summary>
    /// Makes the repairs that need no judgement and returns what is still wrong: adds the sRGB output intent and the XMP packet,
    /// sets the Print flag of annotations, stores generated appearances for annotations and widgets that have none, clears
    /// <c>/NeedAppearances</c> once every widget has an appearance, and (when <see cref="PdfAOptions.EmbedLibraryFonts"/> is set and the
    /// fonts package is registered) embeds Liberation faces for the standard fonts OnePDF itself added. Existing content is not
    /// rewritten: fonts, colours and actions that came with the file are only reported. The changes are made to the document in memory;
    /// calling it again changes nothing more.
    /// </summary>
    /// <param name="options">Target part and level; null means PDF/A-2b.</param>
    /// <param name="cancellationToken">Checked between objects.</param>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    public IReadOnlyList<PdfAViolation> PreparePdfA(PdfAOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        options ??= new PdfAOptions();
        PdfAPreparer.Prepare(this, options, cancellationToken);
        return new PdfAChecker(this, options, saving: true, decrypting: false, cancellationToken).Run();
    }

    /// <summary>
    /// Embeds <paramref name="data"/> as a file attachment: a file specification with an embedded file stream (Flate-compressed),
    /// entered in the <c>EmbeddedFiles</c> name tree and in the catalog's <c>/AF</c> array so that it is associated with the document.
    /// Needed for PDF/A-3, where attachments may be of any type.
    /// </summary>
    /// <param name="fileName">The name shown for the file (also its key in the name tree); must not be empty.</param>
    /// <param name="data">The file content.</param>
    /// <param name="options">MIME type, description, relationship and dates; null means defaults.</param>
    /// <returns>The reference of the file specification.</returns>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    /// <exception cref="PdfFormatException">The document's <c>EmbeddedFiles</c> name tree has several levels, which this method does not extend.</exception>
    /// <exception cref="ArgumentException">The name is empty.</exception>
    public PdfReference AttachFile(string fileName, ReadOnlyMemory<byte> data, PdfAttachmentOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        EnsureEditable(0);
        options ??= new PdfAttachmentOptions();

        PdfStream stream = CreateStream(data.Span, compress: true);
        PdfDictionary sd = stream.Dictionary;
        sd.SetEntry("Type", new PdfName("EmbeddedFile"));
        sd.SetEntry("Subtype", new PdfName(options.MimeType));
        var pars = new PdfDictionary();
        pars.SetEntry("Size", new PdfInteger(data.Length));
        if (options.CreationDate is { } cd)
        {
            pars.SetEntry("CreationDate", PdfString.FromText(FormatDate(cd)));
        }

        if (options.ModificationDate is { } md)
        {
            pars.SetEntry("ModDate", PdfString.FromText(FormatDate(md)));
        }

        sd.SetEntry("Params", pars);
        PdfReference streamRef = AddObject(stream);

        var spec = new PdfDictionary();
        spec.SetEntry("Type", new PdfName("Filespec"));
        spec.SetEntry("F", PdfString.FromText(fileName));
        spec.SetEntry("UF", PdfString.FromText(fileName));
        if (options.Description is { } desc)
        {
            spec.SetEntry("Desc", PdfString.FromText(desc));
        }

        spec.SetEntry("AFRelationship", new PdfName(options.Relationship.ToString()));
        var ef = new PdfDictionary();
        ef.SetEntry("F", streamRef);
        ef.SetEntry("UF", streamRef);
        spec.SetEntry("EF", ef);
        PdfReference specRef = AddObject(spec);

        PdfDictionary catalog = Catalog;
        PdfDictionary names = catalog.GetDictionary("Names") ?? EnsureCatalogDictionary(catalog, "Names");
        PdfDictionary tree = names.GetDictionary("EmbeddedFiles") ?? EnsureCatalogDictionary(names, "EmbeddedFiles");
        if (tree.ContainsKey("Kids"))
        {
            throw new PdfFormatException("The document's EmbeddedFiles name tree has several levels, which AttachFile does not extend.");
        }

        PdfArray pairs = tree.GetArray("Names") ?? EnsureCatalogArray(tree, "Names");
        // Keys must stay in lexical order (ISO 32000-2 7.9.6).
        int at = pairs.Count;
        for (int i = 0; i + 1 < pairs.Count; i += 2)
        {
            if (pairs[i] is PdfString key && string.CompareOrdinal(key.GetText(), fileName) > 0)
            {
                at = i;
                break;
            }
        }

        pairs.Insert(at, PdfString.FromText(fileName));
        pairs.Insert(at + 1, specRef);

        PdfArray af = catalog.GetArray("AF") ?? EnsureCatalogArray(catalog, "AF");
        af.Append(specRef);
        return specRef;
    }

    private static PdfDictionary EnsureCatalogDictionary(PdfDictionary parent, string key)
    {
        parent.SetEntry(key, new PdfDictionary());
        return parent.GetDictionary(key)!;
    }

    private static PdfArray EnsureCatalogArray(PdfDictionary parent, string key)
    {
        parent.SetEntry(key, new PdfArray());
        return parent.GetArray(key)!;
    }

    internal static string FormatDate(DateTimeOffset d)
    {
        TimeSpan o = d.Offset;
        string zone = o == TimeSpan.Zero ? "Z00'00'" : $"{(o < TimeSpan.Zero ? '-' : '+')}{Math.Abs(o.Hours):00}'{Math.Abs(o.Minutes):00}'";
        return "D:" + d.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + zone;
    }
}

internal static class PdfAProcessor
{
    /// <summary>Prepares and checks the document for a save with <see cref="PdfSaveOptions.PdfA"/> and returns the options the saver should use.</summary>
    public static PdfSaveOptions PrepareForSave(PdfDocument doc, PdfSaveOptions options, CancellationToken ct)
    {
        PdfAOptions pa = options.PdfA!;
        if (options.Encryption == PdfEncryptionAction.Set)
        {
            throw new ArgumentException("PDF/A files cannot be encrypted (ISO 19005-2, 6.1.3).", nameof(options));
        }

        PdfAPreparer.Prepare(doc, pa, ct);
        IReadOnlyList<PdfAViolation> violations = new PdfAChecker(doc, pa, saving: true, decrypting: options.Encryption == PdfEncryptionAction.Remove, ct).Run();
        if (violations.Count > 0)
        {
            throw new PdfAException(pa.Name, violations);
        }

        return new PdfSaveOptions
        {
            XrefFormat = options.XrefFormat,
            UseObjectStreams = options.UseObjectStreams,
            CompressStreams = options.CompressStreams,
            MinimumVersion = options.MinimumVersion,
            ExactVersion = "1.7",
            DocumentId = options.DocumentId,
            Encryption = options.Encryption,
            NewEncryption = null,
            PdfA = pa,
        };
    }
}
