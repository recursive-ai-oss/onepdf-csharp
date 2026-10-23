using System.Globalization;

namespace OnePdf;

/// <summary>
/// A signature dictionary found in a document: its metadata as data, and <see cref="Verify"/> to check it.
/// Reading these properties never verifies anything and never leaves the document.
/// </summary>
public sealed class PdfSignature
{
    private readonly Lazy<Internal.ByteRangeInfo> _range;

    internal PdfSignature(PdfDocument document, PdfDictionary dictionary, PdfFormField? field)
    {
        Document = document;
        Dictionary = dictionary;
        Field = field;
        FieldName = field?.FullName;
        SubFilter = dictionary.GetName("SubFilter");
        Filter = dictionary.GetName("Filter");
        Name = dictionary.GetString("Name")?.GetText();
        Reason = dictionary.GetString("Reason")?.GetText();
        Location = dictionary.GetString("Location")?.GetText();
        ContactInfo = dictionary.GetString("ContactInfo")?.GetText();
        string? type = dictionary.GetName("Type");
        Kind = type == "DocTimeStamp" || SubFilter == "ETSI.RFC3161" ? PdfSignatureKind.DocumentTimestamp : PdfSignatureKind.Signature;
        if (dictionary.GetString("M") is { } m && PdfDate.TryParse(m.GetText(), out DateTimeOffset when))
        {
            SigningTime = when;
        }

        RawContents = dictionary.GetString("Contents")?.Bytes ?? ReadOnlyMemory<byte>.Empty;
        if (dictionary.Get("ByteRange") is PdfArray br && br.Count <= 64)
        {
            var list = new List<long>(br.Count);
            foreach (PdfObject? o in br.Items)
            {
                if (o is PdfInteger i)
                {
                    list.Add(i.Value);
                }
                else
                {
                    list = [];
                    break;
                }
            }

            ByteRange = list.Count > 0 ? list : null;
        }

        ReadReferences();
        _range = new Lazy<Internal.ByteRangeInfo>(() => Internal.ByteRangeInfo.Analyze(this), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The document this signature was found in.</summary>
    public PdfDocument Document { get; }

    /// <summary>Signature or document time-stamp.</summary>
    public PdfSignatureKind Kind { get; }

    /// <summary>The signature dictionary (the field's <c>/V</c>).</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The signature field that holds it, or null when found only through <c>/Perms</c>.</summary>
    public PdfFormField? Field { get; }

    /// <summary>The fully qualified field name, if there is a field.</summary>
    public string? FieldName { get; }

    /// <summary>The <c>/SubFilter</c> name, e.g. <c>adbe.pkcs7.detached</c>, <c>ETSI.CAdES.detached</c> or <c>ETSI.RFC3161</c>.</summary>
    public string? SubFilter { get; }

    /// <summary>The <c>/Filter</c> name (the signature handler, e.g. <c>Adobe.PPKLite</c>).</summary>
    public string? Filter { get; }

    /// <summary>The signer's name as written in <c>/Name</c>; not authenticated. The certificate subject is in the verification result.</summary>
    public string? Name { get; }

    /// <summary>The <c>/Reason</c> text, or null.</summary>
    public string? Reason { get; }

    /// <summary>The <c>/Location</c> text, or null.</summary>
    public string? Location { get; }

    /// <summary>The <c>/ContactInfo</c> text, or null.</summary>
    public string? ContactInfo { get; }

    /// <summary>The <c>/M</c> signing time as written in the dictionary; not authenticated. Null when absent or unparsable.</summary>
    public DateTimeOffset? SigningTime { get; }

    /// <summary>
    /// The <c>/ByteRange</c> values as written (normally four), or null when absent or not made of integers. Use
    /// <see cref="PdfSignatureVerification.ByteRange"/> for a verdict.
    /// </summary>
    public IReadOnlyList<long>? ByteRange { get; }

    /// <summary>
    /// The raw <c>/Contents</c> bytes (the CMS or time-stamp token, followed by zero padding). Read from the file between the
    /// byte ranges when they are valid, so encryption of the document does not matter.
    /// </summary>
    public ReadOnlyMemory<byte> Contents
    {
        get
        {
            return _range.Value.HoleContents ?? RawContents;
        }
    }

    internal ReadOnlyMemory<byte> RawContents { get; }

    /// <summary>
    /// The document permission level this signature declares when it certifies the document (<c>/DocMDP</c> transform or
    /// <c>/Perms /DocMDP</c>): 1 no changes, 2 form fill and signing, 3 also annotations. Null for an approval signature.
    /// </summary>
    public int? CertificationLevel { get; private set; }

    /// <summary>The field lock that applies to changes after this signature: the field's <c>/Lock</c>, else a FieldMDP transform.</summary>
    public PdfSignatureLock? FieldLock { get; private set; }

    /// <summary>
    /// The index in <see cref="PdfDocument.Revisions"/> of the revision this signature covers (the one that ends where the
    /// byte ranges end), or -1 when the byte range is not valid.
    /// </summary>
    public int CoveredRevision => _range.Value.Status == PdfByteRangeStatus.Valid ? _range.Value.Revision : -1;

    /// <summary>True when the file has revisions after the covered one. False when the byte range is not valid.</summary>
    public bool HasLaterRevisions => CoveredRevision >= 0 && CoveredRevision < Document.Revisions.Count - 1;

    internal Internal.ByteRangeInfo Range => _range.Value;

    /// <summary>
    /// Checks the signature: byte range, digest, cryptographic signature, certificate chain, time-stamp and changes after signing.
    /// Never contacts a network and never runs anything in the document.
    /// </summary>
    /// <param name="options">Trust anchors and policies; null means no trust anchors, so chains report <see cref="PdfChainStatus.UntrustedRoot"/>.</param>
    /// <param name="cancellationToken">Checked while the byte ranges are hashed.</param>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public PdfSignatureVerification Verify(PdfSignatureVerifyOptions? options = null, CancellationToken cancellationToken = default) =>
        Internal.SignatureVerifier.Verify(this, options ?? new PdfSignatureVerifyOptions(), cancellationToken);

    private void ReadReferences()
    {
        PdfObject? refs = Dictionary.Get("Reference");
        if (refs is PdfArray arr)
        {
            for (int i = 0; i < Math.Min(arr.Count, 16); i++)
            {
                if (arr[i] is not PdfDictionary r)
                {
                    continue;
                }

                string? method = r.GetName("TransformMethod");
                PdfDictionary? p = r.GetDictionary("TransformParams");
                if (method == "DocMDP" && CertificationLevel is null)
                {
                    CertificationLevel = p is null ? 2 : ClampLevel(p.GetInt("P", 2));
                }
                else if (method == "FieldMDP" && FieldLock is null && p is not null)
                {
                    FieldLock = ReadLock(p, false);
                }
            }
        }

        if (Field?.Dictionary.GetDictionary("Lock") is { } lockDict)
        {
            FieldLock = ReadLock(lockDict, true);
        }

        if (CertificationLevel is null && Document.Catalog.GetDictionary("Perms") is { } perms && perms.GetRaw("DocMDP") is PdfReference pr && Dictionary.Reference is { } mine
            && pr.Number == mine.Number)
        {
            CertificationLevel = 2;
        }
    }

    internal static int ClampLevel(long p) => p is >= 1 and <= 3 ? (int)p : 2;

    internal static PdfSignatureLock? ReadLock(PdfDictionary d, bool withPermission)
    {
        string? action = d.GetName("Action");
        PdfLockAction a = action switch
        {
            "Include" => PdfLockAction.Include,
            "Exclude" => PdfLockAction.Exclude,
            "All" => PdfLockAction.All,
            _ => (PdfLockAction)(-1),
        };
        if ((int)a < 0)
        {
            return null;
        }

        var names = new List<string>();
        if (d.Get("Fields") is PdfArray f)
        {
            for (int i = 0; i < Math.Min(f.Count, 100_000); i++)
            {
                if (f[i] is PdfString s)
                {
                    names.Add(s.GetText());
                }
            }
        }

        int? perm = withPermission && d.Get("P") is PdfInteger pi ? ClampLevel(pi.Value) : null;
        return new PdfSignatureLock(a, names, perm);
    }

    /// <summary>A short description for logs.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Kind} {FieldName ?? "(no field)"} {SubFilter}");
}

public sealed partial class PdfDocument
{
    /// <summary>
    /// The signatures and document time-stamps in the newest revision: every signature field that has a value, plus the
    /// <c>/Perms /DocMDP</c> signature if no field refers to it. In file order. Nothing is verified.
    /// </summary>
    /// <returns>An empty list when the document has none.</returns>
    public IReadOnlyList<PdfSignature> GetSignatures()
    {
        ThrowIfDisposed();
        var found = new List<PdfSignature>();
        var seen = new HashSet<int>();
        if (Form is { } form)
        {
            foreach (PdfFormField f in form.AllFields)
            {
                if (f.Kind == PdfFieldKind.Signature && f.RawValue is PdfDictionary d && Add(d))
                {
                    found.Add(new PdfSignature(this, d, f));
                }
            }
        }

        if (Catalog.GetDictionary("Perms")?.GetDictionary("DocMDP") is { } cert && Add(cert))
        {
            found.Add(new PdfSignature(this, cert, null));
        }

        found.Sort(static (a, b) =>
        {
            int c = EndOf(a).CompareTo(EndOf(b));
            return c != 0 ? c : (a.Dictionary.Reference?.Number ?? 0).CompareTo(b.Dictionary.Reference?.Number ?? 0);
        });
        return found;

        bool Add(PdfDictionary d) => d.Reference is not { } r || seen.Add(r.Number);

        static long EndOf(PdfSignature s) => s.ByteRange is { Count: 4 } r && r[2] >= 0 && r[3] >= 0 && r[2] <= long.MaxValue - r[3] ? r[2] + r[3] : long.MaxValue;
    }

    /// <summary>Verifies every signature (see <see cref="PdfSignature.Verify"/>) and returns the results with a summary.</summary>
    /// <param name="options">Trust anchors and policies.</param>
    /// <param name="cancellationToken">Checked while hashing.</param>
    public PdfSignatureReport VerifySignatures(PdfSignatureVerifyOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new PdfSignatureVerifyOptions();
        var results = new List<PdfSignatureVerification>();
        foreach (PdfSignature s in GetSignatures())
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(s.Verify(options, cancellationToken));
        }

        return new PdfSignatureReport(results);
    }
}

public sealed partial class PdfFormField
{
    /// <summary>The signature this field holds, or null when it is not a signed signature field.</summary>
    public PdfSignature? GetSignature() => Kind == PdfFieldKind.Signature && RawValue is PdfDictionary d ? new PdfSignature(_doc, d, this) : null;
}
