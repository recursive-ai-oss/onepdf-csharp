using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OnePdf;

/// <summary>Which CMS profile a signature is written in.</summary>
public enum PdfSignatureFormat
{
    /// <summary>
    /// PAdES baseline B-B (ETSI EN 319 142-1): sub-filter <c>ETSI.CAdES.detached</c>, signed attributes <c>contentType</c>,
    /// <c>messageDigest</c> and <c>signingCertificateV2</c>, no <c>signingTime</c> (the time is the dictionary's <c>/M</c>).
    /// With a timestamp callback the signature also carries a signature time-stamp token (B-T).
    /// </summary>
    PadesBaseline,

    /// <summary>
    /// <c>adbe.pkcs7.detached</c> for older consumers: <c>contentType</c>, <c>messageDigest</c> and <c>signingTime</c> are signed.
    /// </summary>
    Pkcs7Detached,
}

/// <summary>The RSA signature scheme to use when the signer's key is an RSA key. ECDSA keys ignore it.</summary>
public enum PdfRsaPadding
{
    /// <summary>RSASSA-PKCS1-v1_5 (RFC 8017 8.2), the most widely understood scheme. The default.</summary>
    Pkcs1,

    /// <summary>RSASSA-PSS with the digest also used for MGF1 and a salt as long as the digest (RFC 8017 8.1).</summary>
    Pss,
}

/// <summary>The permission level a certification signature gives the document (ISO 32000-2 12.8.2.2, Table 254, <c>/P</c>).</summary>
public enum PdfCertificationLevel
{
    /// <summary>1: no changes are permitted.</summary>
    NoChanges = 1,

    /// <summary>2: filling in forms and signing are permitted.</summary>
    FormFilling = 2,

    /// <summary>3: form filling, signing and adding or changing annotations are permitted.</summary>
    FormFillingAndAnnotations = 3,
}

/// <summary>The kind of key a signature request is for.</summary>
public enum PdfSigningKeyType
{
    /// <summary>An RSA key; the signature value is the RSA signature (RSASSA-PKCS1-v1_5 over the DigestInfo, or RSASSA-PSS).</summary>
    Rsa,

    /// <summary>An ECDSA key; the signature value is a DER <c>Ecdsa-Sig-Value</c> or the IEEE P1363 form (r followed by s).</summary>
    Ecdsa,
}

/// <summary>What an external signer is asked to sign.</summary>
public sealed class PdfSignRequest
{
    internal PdfSignRequest(ReadOnlyMemory<byte> hash, HashAlgorithmName hashAlgorithm, PdfSigningKeyType keyType, PdfRsaPadding padding)
    {
        Hash = hash;
        HashAlgorithm = hashAlgorithm;
        KeyType = keyType;
        RsaPadding = padding;
    }

    /// <summary>The digest of the DER-encoded signed attributes, computed with <see cref="HashAlgorithm"/>. Sign this value (do not hash it again).</summary>
    public ReadOnlyMemory<byte> Hash { get; }

    /// <summary>The digest algorithm that produced <see cref="Hash"/>.</summary>
    public HashAlgorithmName HashAlgorithm { get; }

    /// <summary>The kind of the certificate's key.</summary>
    public PdfSigningKeyType KeyType { get; }

    /// <summary>For RSA keys: the padding the signature must use. Ignored for ECDSA.</summary>
    public PdfRsaPadding RsaPadding { get; }
}

/// <summary>
/// Signs a digest with a key OnePDF cannot reach (a hardware module or a remote service) and returns the signature value.
/// OnePDF checks the value with the certificate's public key before it embeds it, so a wrong or damaged answer is refused.
/// </summary>
/// <param name="request">The digest and the algorithm to sign it with.</param>
/// <returns>The raw signature value.</returns>
public delegate byte[] PdfExternalSigner(PdfSignRequest request);

/// <summary>An RFC 3161 time-stamp request, handed to the callback that talks to the time-stamp authority.</summary>
public sealed class PdfTimestampRequest
{
    internal PdfTimestampRequest(HashAlgorithmName hashAlgorithm, ReadOnlyMemory<byte> imprint, ReadOnlyMemory<byte> nonce, byte[] requestDer)
    {
        HashAlgorithm = hashAlgorithm;
        MessageImprint = imprint;
        Nonce = nonce;
        RequestDer = requestDer;
    }

    /// <summary>The hash algorithm of <see cref="MessageImprint"/>.</summary>
    public HashAlgorithmName HashAlgorithm { get; }

    /// <summary>The hash of the data to be time-stamped.</summary>
    public ReadOnlyMemory<byte> MessageImprint { get; }

    /// <summary>The random nonce put in the request (big-endian magnitude); the token must echo it.</summary>
    public ReadOnlyMemory<byte> Nonce { get; }

    /// <summary>
    /// The DER <c>TimeStampReq</c> (RFC 3161 2.4.1) with the imprint, the nonce and <c>certReq</c> set, ready to send with content
    /// type <c>application/timestamp-query</c>. A callback may instead build its own request from <see cref="MessageImprint"/>.
    /// </summary>
    public ReadOnlyMemory<byte> RequestDer { get; }
}

/// <summary>
/// Gets a time-stamp from an authority. OnePDF never opens a connection itself: this callback does the transport. It returns either
/// the DER <c>TimeStampResp</c> (RFC 3161 2.4.2) or the bare token (a CMS <c>ContentInfo</c>). The token is checked (status, imprint,
/// nonce) before it is embedded.
/// </summary>
/// <param name="request">The request.</param>
/// <returns>The response or token bytes.</returns>
public delegate byte[] PdfTimestampCallback(PdfTimestampRequest request);

/// <summary>The key and certificates a document is signed with. Create one with <see cref="FromCertificate"/> or <see cref="FromExternalSigner"/>.</summary>
public sealed class PdfSigningCredential
{
    private PdfSigningCredential(X509Certificate2 certificate, IReadOnlyList<X509Certificate2> chain, PdfExternalSigner? external)
    {
        Certificate = certificate;
        Chain = chain;
        External = external;
    }

    /// <summary>The signer's certificate.</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>The certificates embedded in the signature after the signer's: intermediates and, if wanted, the root.</summary>
    public IReadOnlyList<X509Certificate2> Chain { get; }

    internal PdfExternalSigner? External { get; }

    /// <summary>A credential for a certificate that has an RSA or ECDSA private key available to this process.</summary>
    /// <param name="certificate">The signer's certificate with its private key. Checked when signing.</param>
    /// <param name="chain">Certificates to embed besides the signer's (intermediates; the root is optional). OnePDF does not build or fetch a chain itself.</param>
    /// <exception cref="ArgumentNullException"><paramref name="certificate"/> is null.</exception>
    public static PdfSigningCredential FromCertificate(X509Certificate2 certificate, IEnumerable<X509Certificate2>? chain = null)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return new PdfSigningCredential(certificate, chain is null ? [] : [.. chain], null);
    }

    /// <summary>A credential whose signatures are made by <paramref name="signer"/>, for keys held in a hardware module or by a remote service.</summary>
    /// <param name="certificate">The signer's certificate (public part only is needed).</param>
    /// <param name="signer">Called once per signature with the digest to sign.</param>
    /// <param name="chain">Certificates to embed besides the signer's.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static PdfSigningCredential FromExternalSigner(X509Certificate2 certificate, PdfExternalSigner signer, IEnumerable<X509Certificate2>? chain = null)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(signer);
        return new PdfSigningCredential(certificate, chain is null ? [] : [.. chain], signer);
    }
}

/// <summary>The look of a visible signature. All members are optional.</summary>
public sealed class PdfSignatureAppearance
{
    /// <summary>
    /// The text lines to show. Null means: "Digitally signed by" and the signer's name, the date, then the reason and location when
    /// they are set. Lines are wrapped to the box; those that do not fit are cut.
    /// </summary>
    public IReadOnlyList<string>? Lines { get; init; }

    /// <summary>An image (a logo or handwritten signature) placed on the left, filling at most the left 40 percent of the box, keeping its aspect ratio. It must belong to the document being signed.</summary>
    public PdfCreatedImage? Image { get; init; }

    /// <summary>The font size of the text; 0, the default, picks the largest size up to 10 points at which the lines fit.</summary>
    public double FontSize { get; init; }

    /// <summary>Draw a thin frame around the box. Default true.</summary>
    public bool Border { get; init; } = true;
}

/// <summary>Settings for <see cref="PdfDocument.Sign(Stream, PdfSigningCredential, PdfSigningOptions?, CancellationToken)"/>.</summary>
public sealed class PdfSigningOptions
{
    /// <summary>
    /// The signature field to sign: an existing unsigned signature field's fully qualified name, or the name of a new field
    /// (no dots). Null picks the first free name of the form <c>Signature1</c>, <c>Signature2</c>, ...
    /// </summary>
    public string? FieldName { get; init; }

    /// <summary>The page a new field is placed on (zero-based). Default 0. Ignored for an existing field.</summary>
    public int PageIndex { get; init; }

    /// <summary>
    /// Where a new field sits on the page, in default user space. Null or empty makes an invisible signature. For an existing
    /// field its own rectangle is used and this is ignored.
    /// </summary>
    public PdfRectangle? Rectangle { get; init; }

    /// <summary>The look of a visible signature; null draws the default text.</summary>
    public PdfSignatureAppearance? Appearance { get; init; }

    /// <summary>The CMS profile. Default <see cref="PdfSignatureFormat.PadesBaseline"/>.</summary>
    public PdfSignatureFormat Format { get; init; }

    /// <summary>The digest algorithm: SHA-256 (default), SHA-384 or SHA-512.</summary>
    public HashAlgorithmName Hash { get; init; } = HashAlgorithmName.SHA256;

    /// <summary>The RSA scheme for RSA keys. Default <see cref="PdfRsaPadding.Pkcs1"/>.</summary>
    public PdfRsaPadding RsaPadding { get; init; }

    /// <summary>The <c>/Name</c> of the signer. Null uses the common name of the certificate's subject.</summary>
    public string? SignerName { get; init; }

    /// <summary>The <c>/Reason</c>, or null.</summary>
    public string? Reason { get; init; }

    /// <summary>The <c>/Location</c>, or null.</summary>
    public string? Location { get; init; }

    /// <summary>The <c>/ContactInfo</c>, or null.</summary>
    public string? ContactInfo { get; init; }

    /// <summary>The signing time written to <c>/M</c> (and to <c>signingTime</c> for <see cref="PdfSignatureFormat.Pkcs7Detached"/>). Null means now.</summary>
    public DateTimeOffset? SigningTime { get; init; }

    /// <summary>
    /// Makes this a certification signature with this DocMDP level (ISO 32000-2 12.8.2.2). Only the first signature of a
    /// document can certify it.
    /// </summary>
    public PdfCertificationLevel? Certify { get; init; }

    /// <summary>A <c>/Lock</c> dictionary for the signature field (FieldMDP, ISO 32000-2 12.7.5.5): fields that may not change after this signature.</summary>
    public PdfSignatureLock? FieldLock { get; init; }

    /// <summary>
    /// Gets a signature time-stamp for the signature, which makes the result PAdES B-T (or adds an unsigned time-stamp attribute to
    /// a <see cref="PdfSignatureFormat.Pkcs7Detached"/> signature). Null signs without one.
    /// </summary>
    public PdfTimestampCallback? Timestamp { get; init; }

    /// <summary>
    /// The number of bytes reserved for the CMS in <c>/Contents</c> (the file holds twice as many hex digits). Null estimates it
    /// from the certificates, the key size and the time-stamp (8 KiB reserved for a token). Raise it when
    /// <see cref="PdfSigningError.ContentsTooSmall"/> is thrown. At most 4 MiB.
    /// </summary>
    public int? ContentsReserve { get; init; }
}

/// <summary>Settings for <see cref="PdfDocument.AddDocumentTimestamp(Stream, PdfTimestampCallback, PdfTimestampOptions?, CancellationToken)"/>.</summary>
public sealed class PdfTimestampOptions
{
    /// <summary>The name of the new (invisible) signature field. Null picks the first free <c>Timestamp1</c>, <c>Timestamp2</c>, ...</summary>
    public string? FieldName { get; init; }

    /// <summary>The page the invisible field is placed on (zero-based). Default 0.</summary>
    public int PageIndex { get; init; }

    /// <summary>The imprint hash: SHA-256 (default), SHA-384 or SHA-512.</summary>
    public HashAlgorithmName Hash { get; init; } = HashAlgorithmName.SHA256;

    /// <summary>Bytes reserved for the token in <c>/Contents</c>; null reserves 12 KiB. At most 4 MiB.</summary>
    public int? ContentsReserve { get; init; }
}

/// <summary>What <see cref="PdfDocument.Sign(Stream, PdfSigningCredential, PdfSigningOptions?, CancellationToken)"/> wrote.</summary>
/// <param name="FieldName">The fully qualified name of the signature field.</param>
/// <param name="ContentsCapacity">The bytes reserved for the CMS in <c>/Contents</c>.</param>
/// <param name="ContentsUsed">The bytes the CMS actually took; a good <see cref="PdfSigningOptions.ContentsReserve"/> for the next signature of the same kind.</param>
/// <param name="SignedLength">The length of the whole output, which is where the signature's coverage ends.</param>
/// <param name="HasTimestamp">True when a time-stamp token is embedded (a document time-stamp, or a B-T signature).</param>
public sealed record PdfSigningResult(string FieldName, int ContentsCapacity, int ContentsUsed, long SignedLength, bool HasTimestamp);

/// <summary>Why signing was refused.</summary>
public enum PdfSigningError
{
    /// <summary>The certificate has no usable private key and no external signer was given.</summary>
    NoPrivateKey = 0,

    /// <summary>The key type (not RSA or ECDSA), the digest or the combination is not supported.</summary>
    UnsupportedAlgorithm,

    /// <summary>The external signer or the key produced a value that does not verify with the certificate's public key, or failed.</summary>
    SignerFailed,

    /// <summary>The time-stamp callback failed or returned a response that is not an acceptable token (refused status, wrong imprint, wrong nonce, malformed).</summary>
    TimestampRejected,

    /// <summary>The CMS (with its time-stamp) is longer than the space reserved in <c>/Contents</c>. Nothing was written; raise <see cref="PdfSigningOptions.ContentsReserve"/>.</summary>
    ContentsTooSmall,

    /// <summary>The field is unusable: it exists but is not an unsigned signature field, or the name is not allowed.</summary>
    BadField,

    /// <summary>A certification signature was requested but the document already has signatures.</summary>
    CertificationNotFirst,

    /// <summary>The document was already used for a signing attempt; reopen the output (or the file) to sign again.</summary>
    DocumentUsed,
}

/// <summary>Signing failed; <see cref="Reason"/> says why. No output was written to the stream or file.</summary>
public sealed class PdfSigningException : PdfException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="reason">Why signing was refused.</param>
    /// <param name="message">What went wrong, in words.</param>
    public PdfSigningException(PdfSigningError reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>Creates the exception with an inner exception.</summary>
    /// <param name="reason">Why signing was refused.</param>
    /// <param name="message">What went wrong, in words.</param>
    /// <param name="innerException">The underlying failure.</param>
    public PdfSigningException(PdfSigningError reason, string message, Exception? innerException)
        : base(message, innerException)
    {
        Reason = reason;
    }

    /// <summary>Why signing was refused.</summary>
    public PdfSigningError Reason { get; }

    /// <summary>For <see cref="PdfSigningError.ContentsTooSmall"/>: the number of bytes that would have been needed; 0 otherwise.</summary>
    public int RequiredBytes { get; init; }
}
