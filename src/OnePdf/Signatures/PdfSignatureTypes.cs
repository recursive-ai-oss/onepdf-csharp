using System.Linq;
using System.Security.Cryptography.X509Certificates;

namespace OnePdf;

/// <summary>What a signature dictionary is: a signature over the document, or an RFC 3161 document time-stamp.</summary>
public enum PdfSignatureKind
{
    /// <summary>A CMS or CAdES signature (<c>/Type /Sig</c>, or no type).</summary>
    Signature,

    /// <summary>A document time-stamp (<c>/Type /DocTimeStamp</c>, sub-filter <c>ETSI.RFC3161</c>).</summary>
    DocumentTimestamp,
}

/// <summary>Outcome of one check. <see cref="NotChecked"/> means the check could not be run (unsupported algorithm, nothing to check).</summary>
public enum PdfCheckStatus
{
    /// <summary>The check ran and passed.</summary>
    Valid,

    /// <summary>The check ran and failed.</summary>
    Invalid,

    /// <summary>The check could not be run; a warning says why.</summary>
    NotChecked,
}

/// <summary>Result of validating a signature's <c>/ByteRange</c> against the file (ISO 32000-2 12.8.1).</summary>
public enum PdfByteRangeStatus
{
    /// <summary>The range starts at 0, leaves exactly the <c>/Contents</c> value out, has no gaps or overlaps and ends at the end of a revision.</summary>
    Valid,

    /// <summary>There is no <c>/ByteRange</c>, or no <c>/Contents</c>.</summary>
    Missing,

    /// <summary>The array is not four non-negative integers.</summary>
    Malformed,

    /// <summary>The first range does not start at offset 0.</summary>
    DoesNotStartAtZero,

    /// <summary>The ranges overlap, lie in the wrong order, or leave a gap that is not the <c>/Contents</c> value.</summary>
    OverlapOrGap,

    /// <summary>A range lies partly outside the file.</summary>
    OutsideFile,

    /// <summary>The bytes left out are not the hex string held in <c>/Contents</c>.</summary>
    ContentsMismatch,

    /// <summary>The ranges stop before the end of a revision, so part of the revision is not signed.</summary>
    DoesNotCoverRevision,
}

/// <summary>Result of building the signer's certificate chain.</summary>
public enum PdfChainStatus
{
    /// <summary>The chain leads to a supplied trust anchor and every certificate was valid at the validation time.</summary>
    Trusted,

    /// <summary>The chain ends in a certificate that is not a trust anchor.</summary>
    UntrustedRoot,

    /// <summary>A certificate had expired at the validation time.</summary>
    Expired,

    /// <summary>A certificate was not yet valid at the validation time.</summary>
    NotYetValid,

    /// <summary>The issuer of a certificate was not found among the embedded, extra and trusted certificates.</summary>
    Incomplete,

    /// <summary>A certificate in the chain was revoked according to the caller's revocation callback.</summary>
    Revoked,

    /// <summary>The chain is broken for another reason (bad certificate signature, constraints, name constraints).</summary>
    Invalid,

    /// <summary>There was no signer certificate to build a chain for.</summary>
    NotChecked,
}

/// <summary>What is known about revocation of the certificates in a chain. OnePDF never fetches revocation data.</summary>
public enum PdfRevocationStatus
{
    /// <summary>No revocation information was supplied; nothing was checked.</summary>
    NotChecked,

    /// <summary>The caller's callback reported every certificate as not revoked.</summary>
    Good,

    /// <summary>The callback reported a certificate as revoked.</summary>
    Revoked,

    /// <summary>The callback could not tell for at least one certificate.</summary>
    Unknown,
}

/// <summary>Where the time used to validate the certificates came from.</summary>
public enum PdfValidationTimeSource
{
    /// <summary>A verified time-stamp token.</summary>
    Timestamp,

    /// <summary>The <c>signingTime</c> attribute (or <c>/M</c>): the signer's own claim, unverified.</summary>
    SigningTime,

    /// <summary>The current time.</summary>
    Now,

    /// <summary>The time given in the options.</summary>
    Explicit,
}

/// <summary>The time policy for certificate validation.</summary>
public enum PdfValidationTimePolicy
{
    /// <summary>The verified time-stamp's time if there is one, else the signing time (flagged as unverified), else now.</summary>
    Auto,

    /// <summary>The current time, whatever the signature says.</summary>
    Now,
}

/// <summary>How much of the file a signature covers, and whether later changes are acceptable.</summary>
public enum PdfSignatureCoverage
{
    /// <summary>The signature covers the file up to its end; nothing was added afterwards.</summary>
    WholeDocument,

    /// <summary>The file has later revisions, and every change in them is allowed by the document's permission level.</summary>
    EarlierRevisionAllowedChanges,

    /// <summary>The file has later revisions with changes the permission level or a field lock does not allow.</summary>
    EarlierRevisionDisallowedChanges,

    /// <summary>The byte range is not valid, so no revision can be said to be covered.</summary>
    Unknown,
}

/// <summary>What kind of thing a change after signing touched.</summary>
public enum PdfChangeCategory
{
    /// <summary>The value or appearance of a form field was filled in or changed.</summary>
    FormFieldValue,

    /// <summary>A signature (or time-stamp) was added.</summary>
    NewSignature,

    /// <summary>An annotation was added, changed or removed.</summary>
    Annotation,

    /// <summary>A page, its content, resources or the page tree changed.</summary>
    PageContent,

    /// <summary>Validation-related information (<c>/DSS</c>) was added.</summary>
    ValidationData,

    /// <summary>Anything else.</summary>
    Other,
}

/// <summary>How a field lock selects fields (ISO 32000-2 12.7.5.5, Table 235).</summary>
public enum PdfLockAction
{
    /// <summary>Every field is locked.</summary>
    All,

    /// <summary>The listed fields are locked.</summary>
    Include,

    /// <summary>All fields except the listed ones are locked.</summary>
    Exclude,
}

/// <summary>A signature field lock (<c>/Lock</c>) or a FieldMDP transform: fields that may not change after signing.</summary>
/// <param name="Action">How <paramref name="Fields"/> selects.</param>
/// <param name="Fields">Fully qualified field names; empty for <see cref="PdfLockAction.All"/>.</param>
/// <param name="Permission">The <c>/P</c> access-permission level (1 to 3) given with a lock dictionary, if any.</param>
public sealed record PdfSignatureLock(PdfLockAction Action, IReadOnlyList<string> Fields, int? Permission);

/// <summary>One object that differs between the signed revision and the newest one.</summary>
/// <param name="ObjectNumber">The object number.</param>
/// <param name="Category">What it is.</param>
/// <param name="Allowed">Whether the document's permission level and the field locks allow it.</param>
/// <param name="Description">A short, human-readable account.</param>
public sealed record PdfSignatureChange(int ObjectNumber, PdfChangeCategory Category, bool Allowed, string Description);

/// <summary>One certificate of a built chain.</summary>
/// <param name="Certificate">The certificate (a copy owned by the result).</param>
/// <param name="Problems">Problems the chain builder found with this certificate; empty when none.</param>
public sealed record PdfChainElement(X509Certificate2 Certificate, IReadOnlyList<string> Problems);

/// <summary>Settings for signature verification. Nothing here makes OnePDF contact a network.</summary>
public sealed class PdfSignatureVerifyOptions
{
    /// <summary>Certificates trusted as roots (or intermediates that count as anchors). Empty and no system store: every chain is untrusted.</summary>
    public IReadOnlyList<X509Certificate2> TrustAnchors { get; init; } = [];

    /// <summary>Also trust the operating system's root store. Off by default.</summary>
    public bool UseSystemTrustStore { get; init; }

    /// <summary>Intermediate certificates to use besides the ones embedded in the signature.</summary>
    public IReadOnlyList<X509Certificate2> ExtraCertificates { get; init; } = [];

    /// <summary>How the validation time is chosen. Default <see cref="PdfValidationTimePolicy.Auto"/>.</summary>
    public PdfValidationTimePolicy TimePolicy { get; init; }

    /// <summary>When set, this exact time is used and <see cref="TimePolicy"/> is ignored.</summary>
    public DateTimeOffset? ValidationTime { get; init; }

    /// <summary>
    /// Supplies revocation knowledge from data the caller already holds (CRLs, OCSP responses): it receives a certificate and its
    /// issuer and answers. Without it revocation is reported as not checked. It is never called with a network in mind.
    /// </summary>
    public Func<X509Certificate2, X509Certificate2?, PdfRevocationStatus>? RevocationCheck { get; init; }

    /// <summary>
    /// Require the signer certificate to be usable for signing documents: key usage with digitalSignature or nonRepudiation, and an extended
    /// key usage (when present) naming a document-signing, e-mail or any purpose (RFC 5280 4.2.1.3 and 4.2.1.12). Default true; when false the
    /// problem is still reported in <see cref="PdfSignatureVerification.KeyUsage"/> and the warnings but does not make <c>IsValid</c> false.
    /// </summary>
    public bool RequireSigningKeyUsage { get; init; } = true;

    /// <summary>Compare the signed revision with later ones. Default true; only costs anything when later revisions exist.</summary>
    public bool AnalyzeModifications { get; init; } = true;
}

/// <summary>An RFC 3161 time-stamp token found in a signature (or a document time-stamp) and what was checked on it.</summary>
public sealed class PdfTimestampResult
{
    internal PdfTimestampResult()
    {
    }

    /// <summary>The time the authority put in the token (<c>genTime</c>), if the token parsed.</summary>
    public DateTimeOffset? Time { get; internal set; }

    /// <summary>The token's hash of the signed data (or byte ranges) matches that data.</summary>
    public PdfCheckStatus Imprint { get; internal set; } = PdfCheckStatus.NotChecked;

    /// <summary>The authority's signature over the token.</summary>
    public PdfCheckStatus Signature { get; internal set; } = PdfCheckStatus.NotChecked;

    /// <summary>The authority's certificate chain at <see cref="Time"/>.</summary>
    public PdfChainStatus Chain { get; internal set; } = PdfChainStatus.NotChecked;

    /// <summary>The certificates of that chain, leaf first.</summary>
    public IReadOnlyList<PdfChainElement> ChainElements { get; internal set; } = [];

    /// <summary>The authority's certificate, if found.</summary>
    public X509Certificate2? Certificate { get; internal set; }

    /// <summary>True when the imprint and signature are valid. The authority's trust is in <see cref="Chain"/>.</summary>
    public bool IsValid => Imprint == PdfCheckStatus.Valid && Signature == PdfCheckStatus.Valid;

    /// <summary>Problems and notes about the token.</summary>
    public IReadOnlyList<string> Warnings { get; internal set; } = [];
}

/// <summary>Everything found out about one signature. Check the individual properties; <see cref="IsValid"/> is only a convenience.</summary>
public sealed class PdfSignatureVerification
{
    internal PdfSignatureVerification(PdfSignature signature)
    {
        Signature = signature;
    }

    internal bool RequireKeyUsage { get; set; } = true;

    /// <summary>The signature this result describes.</summary>
    public PdfSignature Signature { get; }

    /// <summary>Whether <c>/ByteRange</c> is well-formed and covers a whole revision.</summary>
    public PdfByteRangeStatus ByteRange { get; internal set; }

    /// <summary>The digest of the byte ranges equals the digest the signature commits to.</summary>
    public PdfCheckStatus Integrity { get; internal set; } = PdfCheckStatus.NotChecked;

    /// <summary>The cryptographic signature (over the signed attributes, or the content digest) verifies with the signer certificate's key.</summary>
    public PdfCheckStatus SignatureCheck { get; internal set; } = PdfCheckStatus.NotChecked;

    /// <summary>
    /// Whether the signer certificate's key usage and extended key usage allow signing documents. <see cref="PdfCheckStatus.Invalid"/> when
    /// they do not; <see cref="PdfCheckStatus.NotChecked"/> when there is no signer certificate (or for a document time-stamp).
    /// </summary>
    public PdfCheckStatus KeyUsage { get; internal set; } = PdfCheckStatus.NotChecked;

    /// <summary>The signer's certificate chain at <see cref="ValidationTime"/>.</summary>
    public PdfChainStatus Chain { get; internal set; } = PdfChainStatus.NotChecked;

    /// <summary>The chain from the signer certificate up, leaf first.</summary>
    public IReadOnlyList<PdfChainElement> ChainElements { get; internal set; } = [];

    /// <summary>The signer's certificate, when it was found in the signature or the extra certificates.</summary>
    public X509Certificate2? SignerCertificate { get; internal set; }

    /// <summary>What is known about revocation. Always <see cref="PdfRevocationStatus.NotChecked"/> unless a callback was given.</summary>
    public PdfRevocationStatus Revocation { get; internal set; }

    /// <summary>The signature time-stamp token (or, for a document time-stamp, the token itself), if present.</summary>
    public PdfTimestampResult? Timestamp { get; internal set; }

    /// <summary>The time at which certificates were validated.</summary>
    public DateTimeOffset ValidationTime { get; internal set; }

    /// <summary>Where <see cref="ValidationTime"/> came from.</summary>
    public PdfValidationTimeSource ValidationTimeSource { get; internal set; }

    /// <summary>The <c>signingTime</c> signed attribute (or <c>/M</c> when absent); the signer's unverified claim.</summary>
    public DateTimeOffset? ClaimedSigningTime { get; internal set; }

    /// <summary>The digest algorithm, e.g. "SHA-256".</summary>
    public string? DigestAlgorithm { get; internal set; }

    /// <summary>The signature algorithm, e.g. "RSASSA-PSS".</summary>
    public string? SignatureAlgorithm { get; internal set; }

    /// <summary>True for SHA-1 digests, the legacy sub-filters and RSA keys below 2048 bits.</summary>
    public bool UsesWeakAlgorithm { get; internal set; }

    /// <summary>How much of the file is covered and whether later changes are acceptable.</summary>
    public PdfSignatureCoverage Coverage { get; internal set; } = PdfSignatureCoverage.Unknown;

    /// <summary>The index in <see cref="PdfDocument.Revisions"/> of the revision the signature covers, or -1.</summary>
    public int CoveredRevision { get; internal set; } = -1;

    /// <summary>True when the file has revisions after the covered one.</summary>
    public bool HasLaterRevisions { get; internal set; }

    /// <summary>The objects that changed after signing, classified; empty when nothing changed or analysis was off.</summary>
    public IReadOnlyList<PdfSignatureChange> Changes { get; internal set; } = [];

    /// <summary>The document permission level (1 to 3) in force for this signature's revision, or null when the document is not certified.</summary>
    public int? DocMdpPermission { get; internal set; }

    /// <summary>Everything that went wrong or could not be checked, in plain words.</summary>
    public IReadOnlyList<string> Warnings { get; internal set; } = [];

    /// <summary>
    /// True when the byte range is valid, the digest matches, the signature verifies, the signer certificate may sign documents (unless
    /// <see cref="PdfSignatureVerifyOptions.RequireSigningKeyUsage"/> is off), the chain is trusted at the validation time
    /// (and any time-stamp is valid), and no disallowed change follows. Revocation is not required; a weak algorithm is only a
    /// warning. Use the individual properties for anything finer.
    /// </summary>
    public bool IsValid =>
        ByteRange == PdfByteRangeStatus.Valid && Integrity == PdfCheckStatus.Valid && SignatureCheck == PdfCheckStatus.Valid
        && Chain == PdfChainStatus.Trusted && (KeyUsage != PdfCheckStatus.Invalid || !RequireKeyUsage) && (Timestamp is null || Timestamp.IsValid) && Revocation != PdfRevocationStatus.Revoked
        && Coverage != PdfSignatureCoverage.EarlierRevisionDisallowedChanges;
}

/// <summary>The verification of every signature in a document.</summary>
public sealed class PdfSignatureReport
{
    internal PdfSignatureReport(IReadOnlyList<PdfSignatureVerification> signatures)
    {
        Signatures = signatures;
    }

    /// <summary>One result per signature, in file order.</summary>
    public IReadOnlyList<PdfSignatureVerification> Signatures { get; }

    /// <summary>True when the document has at least one signature.</summary>
    public bool HasSignatures => Signatures.Count > 0;

    /// <summary>True when there is at least one signature and every one has <see cref="PdfSignatureVerification.IsValid"/>.</summary>
    public bool AllValid => Signatures.Count > 0 && Signatures.All(static s => s.IsValid);

    /// <summary>A one-line summary such as "3 signatures: 2 valid, 1 invalid".</summary>
    public string Summary
    {
        get
        {
            int valid = Signatures.Count(static s => s.IsValid);
            return Signatures.Count == 0
                ? "No signatures."
                : $"{Signatures.Count} signature{(Signatures.Count == 1 ? string.Empty : "s")}: {valid} valid, {Signatures.Count - valid} not valid.";
        }
    }
}
