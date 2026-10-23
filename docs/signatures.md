# Signature verification

OnePDF reads the signatures in a PDF as data and checks them. It never runs anything in the file and never touches a network: no
certificate downloads, no OCSP or CRL fetches, no time-stamp requests. Verification is on demand; opening a document does no signature work. Signing is described [below](#signing).

    using var doc = PdfDocument.Open("contract.pdf");
    var options = new PdfSignatureVerifyOptions { TrustAnchors = [rootCertificate] };
    PdfSignatureReport report = doc.VerifySignatures(options);
    foreach (PdfSignatureVerification v in report.Signatures)
    {
        Console.WriteLine($"{v.Signature.FieldName}: valid={v.IsValid} integrity={v.Integrity} signature={v.SignatureCheck} chain={v.Chain} coverage={v.Coverage}");
    }

`doc.GetSignatures()` lists `PdfSignature` objects without verifying (name, reason, location, contact, `/M`, `/SubFilter`, `/ByteRange`,
`Contents`, `CertificationLevel`, `FieldLock`, `CoveredRevision`). `PdfFormField.GetSignature()` gives one field's.

## What is checked

Each check has its own result; `IsValid` is true only when the byte range is valid, the digest matches, the signature verifies, the chain is
`Trusted` at the validation time, any time-stamp is valid, revocation is not `Revoked`, and no disallowed change follows. A weak algorithm and
unchecked revocation are warnings, not failures.

| Result | Meaning |
|---|---|
| `ByteRange` | `Valid`, or why not (`Missing`, `Malformed`, `DoesNotStartAtZero`, `OverlapOrGap`, `OutsideFile`, `ContentsMismatch`, `DoesNotCoverRevision`) |
| `Integrity` | the digest of the signed bytes equals the digest the signature commits to (`messageDigest`, or the signed content for the legacy forms) |
| `SignatureCheck` | the signature over the signed attributes verifies with the signer certificate's key |
| `Chain`, `ChainElements` | the chain from the signer certificate at `ValidationTime`: `Trusted`, `UntrustedRoot`, `Expired`, `NotYetValid`, `Incomplete`, `Revoked`, `Invalid` |
| `Timestamp` | the RFC 3161 token: imprint, the authority's signature, its chain at the token's time, the time itself |
| `Revocation` | `NotChecked` unless `RevocationCheck` was supplied |
| `Coverage`, `Changes` | see below |
| `Warnings` | everything that went wrong or could not be checked, in words |

A check that cannot run (an unsupported algorithm, no signer certificate) is `NotChecked` with a warning, never a crash.

### Byte range (ISO 32000-2 12.8.1)

The range must be four non-negative integers `[0 b c d]`; the bytes from `b` to `c` must be exactly the hex string `<...>` that is the value
of `/Contents` (a range that leaves out only the digits between the brackets is accepted with a warning); `c + d` must be where a revision
ends (an update's `%%EOF`), possibly followed only by white space, or the end of the file. All offsets are checked as `long` against the file
length before anything is read. Linearized files chain their first-page cross-reference section after the main one although it lies earlier
in the file; it counts as the same revision, not as an update. `CoveredRevision` is the index in `PdfDocument.Revisions`.

### Algorithms

| Kind | Supported |
|---|---|
| Sub-filters | `adbe.pkcs7.detached`, `ETSI.CAdES.detached`, `ETSI.RFC3161`; `adbe.pkcs7.sha1` (weak); `adbe.x509.rsa_sha1` is reported unsupported and weak |
| Digests | SHA-1 (weak), SHA-256, SHA-384, SHA-512. SHA-224 is reported unsupported |
| Signatures | RSA PKCS#1 v1.5 (`rsaEncryption` or `shaNWithRSA`), RSASSA-PSS (hash and MGF1 hash equal, salt as long as the hash is what the platform checks), ECDSA (DER, any curve the platform has) |
| CAdES | the signing-certificate attribute (v1 and v2) must name the signing certificate |

The signature algorithm must agree with the digest algorithm of the signer info; if they differ the signature is `Invalid`. RSA keys below
2048 bits are flagged weak. The CMS reader accepts BER (including indefinite lengths) and checks every length before slicing; it is bounded
(nesting 32, 16 signers, 256 certificates, 64 attributes per set, 1 MiB of embedded content).

### Time and chains

The validation time is, in order: `PdfSignatureVerifyOptions.ValidationTime` if set; the time of a verified time-stamp token
(`TimePolicy.Auto`); the `signingTime` attribute (or `/M`), flagged as the signer's unverified claim; otherwise now. `TimePolicy.Now` forces now.
The chain is built with the platform's `X509Chain` with downloads and revocation checking off, using the certificates in the signature, the
`ExtraCertificates` and the trust anchors. Without anchors (and without `UseSystemTrustStore`) no chain is trusted.

Revocation: pass `RevocationCheck`, a function that answers for a certificate and its issuer from CRLs or OCSP responses you already hold.
Without it the result says revocation was not checked. Revocation data embedded in a signature is not used.

## Changes after signing

The file is compared as revisions. For a signature covering revision *r*, every object written by later revisions is compared with its state
in *r* (structural equality, so rewriting an object unchanged is not a change). The rules are advisory; a viewer may be stricter.

- **Level.** The `/P` of the DocMDP transform of the certifying signature (`/Perms /DocMDP` in revision *r*): 1 no changes, 2 form fill and
  signing, 3 also annotations. A field lock's `/P` can lower it. With no certification the level is taken as **2**: form fill and signing pass,
  everything else is flagged.
- **Always allowed:** `/DSS` validation data; document time-stamps; cross-reference and object-stream objects; a changed document information
  dictionary or catalog `/Metadata` (reported as `Other`, allowed).
- **Level 2:** a field's `/V`, `/AP`, `/AS`, `/I`; adding fields to `/AcroForm` and widgets to a page's `/Annots`; adding a signature.
- **Level 3:** also adding, removing and changing other annotations.
- **Field locks:** a `/Lock` dictionary on a signature field, or a FieldMDP transform in the signature's `/Reference`, of this or an earlier
  signature: `All`, `Include` or `Exclude` with field names. A change to a locked field is not allowed whatever the level.
- **Everything else** is not allowed: page content and resources, the page tree, other catalog entries, deleted objects.

New objects take the category of the changed object that refers to them. Each `PdfSignatureChange` has the object number, a
`PdfChangeCategory` (`FormFieldValue`, `NewSignature`, `Annotation`, `PageContent`, `ValidationData`, `Other`), whether it is allowed and a
reason. `Coverage` is `WholeDocument` (nothing follows), `EarlierRevisionAllowedChanges`, `EarlierRevisionDisallowedChanges`, or `Unknown`
(the byte range is not valid). Set `AnalyzeModifications = false` to skip the comparison.

## Limits

A signature value larger than the file cannot occur; the excluded area is capped by `PdfLimits.MaxMetadataBytes`; object comparison is capped
at 2^18 nodes per object and 2^20 changed objects. Cancellation is checked while the byte ranges are hashed and between signatures.

## Signing

Signing is always an incremental update (ISO 32000-2 7.5.6), so earlier signatures stay valid and the original bytes are an exact prefix
of the output. The document must be opened with `PdfOpenOptions.AllowEditing`; changes made through the mutable view before the call (a
filled field, say) are part of the signed update.

    using var doc = PdfDocument.Open("contract.pdf", new PdfOpenOptions { AllowEditing = true });
    var credential = PdfSigningCredential.FromCertificate(signerWithKey, [intermediate]);
    doc.Sign("contract-signed.pdf", credential, new PdfSigningOptions
    {
        FieldName = "Approval",                           // an unsigned signature field, or a new name
        Rectangle = new PdfRectangle(72, 72, 272, 132),   // omit for an invisible signature
        Reason = "I approve this contract",
        Timestamp = request => MyTsaClient.Post(request.RequestDer.ToArray()),   // makes it PAdES B-T
    });

`Sign` takes a `Stream` or a path and writes nothing unless the whole signature succeeded. It holds the output in memory (about the
file size). After a call, successful or failed, the document holds the pending signature objects: reopen the output (or the file) to
sign again; a second call on the same instance throws `PdfSigningException` (`DocumentUsed`). To sign more than once, chain the outputs:
sign, open the result with `AllowEditing`, fill a field if you like, sign again, add a document time-stamp.

### Profiles

| `PdfSigningOptions.Format` | Sub-filter | Signed attributes | Notes |
|---|---|---|---|
| `PadesBaseline` (default) | `ETSI.CAdES.detached` | `contentType` (id-data), `messageDigest`, `signingCertificateV2` | No `signingTime` (ETSI EN 319 142-1 6.3: the time is `/M`). The signer certificate and the chain you gave are embedded. With `Timestamp`: B-T, the token is the `signatureTimeStampToken` unsigned attribute. |
| `Pkcs7Detached` | `adbe.pkcs7.detached` | `contentType`, `messageDigest`, `signingTime` | For older consumers. |

Both use `issuerAndSerialNumber`, an empty detached `encapContentInfo`, digest identifiers with absent parameters (RFC 5754), and
`sha*WithRSAEncryption`, RSASSA-PSS (hash = MGF1 hash, salt = hash length) or `ecdsa-with-SHA*`. A pre-2.0 file that has no signatures
yet also gets `/Extensions /ESIC` (level 2 for CAdES, 1 for a document time-stamp; EN 319 142-1 5.6); a file that is already signed is not
touched there, since a catalog change would count against the earlier signature.

### Keys and external signers

`PdfSigningCredential.FromCertificate(cert, chain)` needs an RSA or ECDSA private key the process can use (`NoPrivateKey` otherwise).
`FromExternalSigner(cert, signer, chain)` calls `signer(PdfSignRequest)` with the digest of the DER signed attributes, the hash algorithm,
the key type and the RSA padding; it returns the signature value (RSA: the raw signature; ECDSA: DER or IEEE P1363). OnePDF checks the
value with the certificate's public key and refuses a wrong one (`SignerFailed`). OnePDF does not build chains: `chain` is what gets
embedded after the signer certificate.

### Time-stamps

`PdfTimestampCallback` gets a `PdfTimestampRequest` (hash algorithm, message imprint, nonce, and the DER `TimeStampReq` with `certReq`
set) and returns the `TimeStampResp` or the bare token. OnePDF checks the status (granted or granted with modifications), that the token
is SignedData holding a TSTInfo, that its imprint equals the one sent and that it echoes the nonce; otherwise `TimestampRejected`. It does
not verify the authority's signature or trust at signing time; `VerifySignatures` does. `AddDocumentTimestamp` writes a `/DocTimeStamp`
field (`Timestamp1`, ...) whose token covers the whole file before it.

### Certification and locks

`PdfSigningOptions.Certify` adds the DocMDP transform (`/Reference`) and `/Perms /DocMDP`; only the first signature of a document may
certify (`CertificationNotFirst`). `FieldLock` writes a `/Lock` dictionary on the signature field.

### Visible signatures

A rectangle (or an existing field with one) gives a visible signature. Its normal appearance is a Form XObject built with the content
builder: an optional frame, an optional image on the left (a `PdfCreatedImage` of the same document, at most 40 percent of the width) and
text lines (default: signer name, date, reason, location), in Helvetica sized to fit up to 10 points. An existing field that already has an
appearance keeps it unless `Appearance` is given.

### Space for `/Contents`

The placeholder is `ContentsReserve` bytes (default: an estimate from the certificates, key size and, for a time-stamp, 8 KiB; 12 KiB for a
document time-stamp), written as twice as many hex digits and patched in place after the byte range is known. If the CMS does not fit,
`ContentsTooSmall` carries `RequiredBytes`; `PdfSigningResult.ContentsUsed` tells what an earlier signature of the same kind took. The
`/Contents` string of a signature dictionary is never encrypted (ISO 32000-2 7.6.2); the rest of the update is encrypted like the document.

### Not done in signing

The time-stamp authority's own signature is not checked when signing. A time-stamp token is embedded as the callback returned it (a BER
token is not re-encoded to DER). Long-term validation data (`/DSS`, PAdES B-LT and B-LTA), several signers in one CMS, signature policies,
commitment types and `/Prop_Build` are not written. Ed25519 and Ed448 keys are not supported (the platform has no such primitive).

## Not done in verification

The public-key security handler, long-term validation data (`/DSS`) beyond noting it, CRL/OCSP parsing, and Ed25519/Ed448 (the platform
has no such primitive) are not part of verification.
