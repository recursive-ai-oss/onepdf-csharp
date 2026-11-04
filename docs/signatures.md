# Signatures: verifying, signing, two-phase signing and the signed bytes

OnePDF reads the signatures in a PDF as data, checks them, and writes new ones. It never runs anything in the file and never touches a
network: no certificate downloads, no OCSP or CRL fetches, no time-stamp requests (the time-stamp authority and any remote signer are
reached only through callbacks you supply). Verification is on demand; opening a document does no signature work.

- [Verifying](#verifying) (`VerifySignatures`): what is checked, changes after signing, known attacks, limits.
- [Signing](#signing) (`Sign`, `AddDocumentTimestamp`): PAdES and `adbe.pkcs7.detached`, certification, visible signatures.
- [Two-phase signing](#two-phase-and-external-cms-signing) (`AddSignaturePlaceholder`, `PdfSignatureHole`): you build the CMS.
- [The signed bytes and the whole file](#reading-the-signed-bytes) (`OpenSignedData`, `OpenRead`).

## Verifying

    using var doc = PdfDocument.Open("contract.pdf");
    var options = new PdfSignatureVerifyOptions { TrustAnchors = [rootCertificate] };
    PdfSignatureReport report = doc.VerifySignatures(options);
    foreach (PdfSignatureVerification v in report.Signatures)
    {
        Console.WriteLine($"{v.Signature.FieldName}: valid={v.IsValid} integrity={v.Integrity} signature={v.SignatureCheck} chain={v.Chain} coverage={v.Coverage}");
    }

`doc.GetSignatures()` lists `PdfSignature` objects without verifying (name, reason, location, contact, `/M`, `/SubFilter`, `/ByteRange`,
`Contents`, `CertificationLevel`, `FieldLock`, `CoveredRevision`). `PdfFormField.GetSignature()` gives one field's.

### What is checked

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

#### Byte range (ISO 32000-2 12.8.1)

The range must be four non-negative integers `[0 b c d]`; the bytes from `b` to `c` must be exactly the hex string `<...>` that is the value
of `/Contents` (a range that leaves out only the digits between the brackets is accepted with a warning); `c + d` must be where a revision
ends (an update's `%%EOF`), possibly followed only by white space, or the end of the file. All offsets are checked as `long` against the file
length before anything is read. Linearized files chain their first-page cross-reference section after the main one although it lies earlier
in the file; it counts as the same revision, not as an update. `CoveredRevision` is the index in `PdfDocument.Revisions`.

#### Algorithms

| Kind | Supported |
|---|---|
| Sub-filters | `adbe.pkcs7.detached`, `ETSI.CAdES.detached`, `ETSI.RFC3161`; `adbe.pkcs7.sha1` (weak); `adbe.x509.rsa_sha1` is reported unsupported and weak |
| Digests | SHA-1 (weak), SHA-256, SHA-384, SHA-512. SHA-224 is reported unsupported |
| Signatures | RSA PKCS#1 v1.5 (`rsaEncryption` or `shaNWithRSA`), RSASSA-PSS (hash and MGF1 hash equal, salt as long as the hash is what the platform checks), ECDSA (DER, any curve the platform has) |
| CAdES | the signing-certificate attribute (v1 and v2) must name the signing certificate |

The signature algorithm must agree with the digest algorithm of the signer info; if they differ the signature is `Invalid`. RSA keys below
2048 bits are flagged weak. The CMS reader accepts BER (including indefinite lengths) and checks every length before slicing; it is bounded
(nesting 32, 16 signers, 256 certificates, 64 attributes per set, 1 MiB of embedded content).

#### Time and chains

The validation time is, in order: `PdfSignatureVerifyOptions.ValidationTime` if set; the time of a verified time-stamp token
(`TimePolicy.Auto`); the `signingTime` attribute (or `/M`), flagged as the signer's unverified claim; otherwise now. `TimePolicy.Now` forces now.
The chain is built with the platform's `X509Chain` with downloads and revocation checking off, using the certificates in the signature, the
`ExtraCertificates` and the trust anchors. Without anchors (and without `UseSystemTrustStore`) no chain is trusted.

Revocation: pass `RevocationCheck`, a function that answers for a certificate and its issuer from CRLs or OCSP responses you already hold.
Without it the result says revocation was not checked. Revocation data embedded in a signature is not used.

### Changes after signing

The file is compared as revisions. For a signature covering revision *r*, every object written by later revisions is compared with its state
in *r* (structural equality, so rewriting an object unchanged is not a change). The rules are advisory; a viewer may be stricter.

- **Level.** The `/P` of the DocMDP transform of the certifying signature (`/Perms /DocMDP` in revision *r*): 1 no changes, 2 form fill and
  signing, 3 also annotations. A field lock's `/P` can lower it. With no certification the level is taken as **2**: form fill and signing pass,
  everything else is flagged, with one exception for approval signatures (next item).
- **Always allowed:** `/DSS` validation data; document time-stamps; cross-reference and object-stream objects; a changed document information
  dictionary or catalog `/Metadata` (reported as `Other`, allowed).
- **Level 2:** a field's `/V`, `/AP`, `/AS`, `/I`; adding fields to `/AcroForm` and widgets to a page's `/Annots`; adding a signature.
  Adding a signature is judged by its shape, not by which objects it touches. Allowed: the new signature field and widget (with its
  appearance stream and resources), the signature dictionary, an empty signature field gaining `/V` (and its `/Lock`), `/SigFlags` bits
  set, `/DR` that only gains resources, and an append at the end of the page's `/Annots` and of the form's `/Fields`. The lists may be
  absent, direct or indirect arrays before and after, and the form may be created by the update. Flagged: an entry removed, replaced or
  reordered in either list, an existing object appended to `/Annots`, a non-widget annotation appended (level 3), `/SigFlags` cleared,
  `/DR` entries changed or removed, any other key of the page, the form or the catalog. A signature widget with an appearance is permitted
  at every level above 1, even when it draws over the page; that is the specification's rule, so it is listed (`FormFieldValue`,
  allowed), not rejected.
- **Level 3:** also adding, removing and changing other annotations.
- **Approval signatures and text markup.** A signature with no DocMDP (no certification) also allows a revision that only appends new
  Highlight, StrikeOut, Underline, Squiggly or Caret annotations, plus Popup annotations whose `/Parent` is one of those new annotations,
  to a page's `/Annots` (with their appearance streams; category `Annotation`, allowed). ISO 32000-2 12.8.2.2 and Table 257 set the
  permitted changes through certification and an approval signature carries none, so this limit is the library's own conservative
  default. Every other annotation subtype (FreeText, Stamp, Ink, Link, Widget, Square, ...), a Popup whose parent is an existing
  annotation, an existing annotation appended or changed, and any other change are judged as at level 2. Certified documents are
  unaffected: P=1 and P=2 flag added markup, P=3 allows it. Field locks apply as before.
- **Field locks:** a `/Lock` dictionary on a signature field, or a FieldMDP transform in the signature's `/Reference`, of this or an earlier
  signature: `All`, `Include` or `Exclude` with field names. A change to a locked field is not allowed whatever the level.
- **Everything else** is not allowed: page content and resources, the page tree, other catalog entries, deleted objects.

New objects take the category of the changed object that refers to them. Each `PdfSignatureChange` has the object number, a
`PdfChangeCategory` (`FormFieldValue`, `NewSignature`, `Annotation`, `PageContent`, `ValidationData`, `Other`), whether it is allowed and a
reason. `Coverage` is `WholeDocument` (nothing follows), `EarlierRevisionAllowedChanges`, `EarlierRevisionDisallowedChanges`, or `Unknown`
(the byte range is not valid). Set `AnalyzeModifications = false` to skip the comparison.

### Known attacks and what the result says

The published attacks on PDF signature validation (universal signature forgery, incremental saving, signature wrapping, shadow and
certification attacks) are each reproduced by a crafted file in `tests/OnePdf.Tests/Signatures/AttackTests.cs`. **Detected** means
`IsValid` is false. **Advisory** means the permission level of the document lets the change through, so `IsValid` stays true and the
change is listed in `Changes` (with its category and `Allowed = true`); callers who show a document as "signed and unchanged" should
show those entries.

| Class | Variants tested | Outcome |
|---|---|---|
| Universal signature forgery | `/ByteRange` or `/Contents` absent, null, empty, all zeros, wrong type, `[0 0 0 0]`, negative, real numbers, wrong count, shifted gap, indirect object or indirect number | Detected. An indirect `/ByteRange` or `/Contents` is refused (`Malformed`) even when it is correct, because the specification requires direct values |
| Incremental saving | rewritten content, page, page tree, catalog, font; objects appended after `%%EOF` with no table, with a trailer only, or with a bad `startxref`; a trailer that points at a new catalog; an update stretched into the signed range; a rewritten signature dictionary or catalog to lower the certification | Detected. Data after the last `%%EOF` is reported as an unsigned change; a later `/Root` that differs from the signed one is a disallowed change |
| Signature wrapping | second range starting early or inside the first, extra objects or a new table hidden in the excluded area, range moved past them, wrong lengths, more than one gap | Detected: the gap must be exactly the `/Contents` string and the range must end at a revision boundary |
| Shadow: hide and replace | page content, page objects, font replaced by an update; xref switching to a second definition prepared before signing | Detected |
| Shadow: form values | a field's value or appearance replaced, or pointed at a prepared appearance stream, under P=2 or 3 | **Advisory**: allowed by the level and listed as `FormFieldValue`. Detected under P=1 |
| Evil annotation | annotation added under P=1 or 2 | Detected. Under P=3 **advisory**: listed as `Annotation`, allowed |
| Evil annotation as a form widget | a widget with an appearance added under P=2 | **Advisory**: a new form field is a permitted change at level 2, listed as `FormFieldValue`. Detected under P=1 |
| Sneaky signature | an unsigned signature field with an appearance added under P=2; a signature added under P=1 | The field is **advisory** at P=2 and detected at P=1; the added signature is detected at P=1 |
| Certificate | untrusted root, expired signer at the validation time, key usage without `digitalSignature`/`nonRepudiation`, extended key usage for TLS or time-stamping only, a time-stamp signed by a certificate without the time-stamping purpose | Detected (`Chain`, `KeyUsage`, `Timestamp`) |

`RequireSigningKeyUsage` (default true) makes a certificate that may not sign documents fail `IsValid`; with it off the problem is still
in `KeyUsage` and the warnings. A key usage extension needs `digitalSignature` or `nonRepudiation`; an extended key usage, when present,
must include document signing, e-mail protection, Adobe PDF signing or any purpose.

Two things the result does not do: it reads signatures from the newest catalog only, so a later `/Root` whose catalog has no `/AcroForm`
shows no signatures (the report says so and is never "all valid"); and a field whose `/FT` is not `/Sig` is not treated as a signature.

### Limits

A signature value larger than the file cannot occur; the excluded area is capped by `PdfLimits.MaxMetadataBytes`; object comparison is capped
at 2^18 nodes per object and 2^20 changed objects. Cancellation is checked while the byte ranges are hashed and between signatures.

### Not done in verification

The public-key security handler, long-term validation data (`/DSS`) beyond noting it, CRL/OCSP parsing, and Ed25519/Ed448 (the platform
has no such primitive) are not part of verification.

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

**Signing a certified document.** The level is read from the certifying signature's own revision, as verification does, so a later
update cannot lower it (the lowest level wins if several signatures declare one). At levels 2 and 3 signing is permitted
(ISO 32000-2 12.8.2.2, Table 257) and nothing changes. At level 1 (`PdfCertificationLevel.NoChanges`) every change invalidates the
certification, so `Sign` and `AddSignaturePlaceholder` refuse up front with `PdfSigningException` and
`PdfSigningError.CertificationForbidsChanges`, before anything is written or the document is marked used. Set
`PdfSigningOptions.AllowBreakingCertification = true` to sign anyway, knowing viewers will report the certification as broken.
`AddDocumentTimestamp` is **allowed** at every level: Table 257 says that updates holding only the data needed to add a document
time-stamp (or DSS) are not changes. `VerifySignatures` agrees: the signature-shaped changes (new signature field and widget, the
appends to `/Annots` and `/Fields`, `/SigFlags`) made by an update that holds a document time-stamp are allowed at every level, so the
certification stays valid. Only those changes: a form value, a non-signature field or an annotation in the same update is still flagged,
and a regular signature added at P=1 (with `AllowBreakingCertification`) is flagged.

**Documents with no pages.** A new signature field is a widget annotation on a page, even an invisible one, so a document whose page
tree is empty is refused with `PdfSigningError.NoPage` by `Sign`, `AddSignaturePlaceholder` and `AddDocumentTimestamp`. Signing an
existing unsigned signature field (`FieldName`) does not need a page and still works. A `PageIndex` outside a document that has
pages is an `ArgumentOutOfRangeException`.

### Visible signatures

A rectangle (or an existing field with one) gives a visible signature. Its normal appearance is a Form XObject built with the content
builder: an optional frame, an optional image on the left (a `PdfCreatedImage` of the same document, at most 40 percent of the width) and
text lines (default: signer name, date, reason, location), in Helvetica sized to fit up to 10 points. An existing field that already has an
appearance keeps it unless `Appearance` is given.

### Space for `/Contents`

The placeholder is `ContentsReserve` bytes (default: an estimate from the certificates, key size and, for a time-stamp, 8 KiB; 12 KiB for a
document time-stamp), written as twice as many hex digits and patched in place after the byte range is known. If the CMS does not fit,
`ContentsTooSmall` carries `RequiredBytes`; `PdfSigningResult.ContentsUsed` tells what an earlier signature of the same kind took. The
`/Contents` string of a signature dictionary is never encrypted (ISO 32000-2 7.6.2), and the reader does not decrypt it: the string of a dictionary with `/Type /Sig` or `/DocTimeStamp`, or one holding `/ByteRange`, is returned as stored. The rest of the update is encrypted like the document.

### Not done in signing

The time-stamp authority's own signature is not checked when signing. A time-stamp token is embedded as the callback returned it (a BER
token is not re-encoded to DER). Long-term validation data (`/DSS`, PAdES B-LT and B-LTA), several signers in one CMS, signature policies,
commitment types and `/Prop_Build` are not written. `Sign` does not support Ed25519 and Ed448 keys (the platform has no such primitive); build that CMS yourself with `AddSignaturePlaceholder`.

## Two-phase and external CMS signing

`Sign` builds the CMS itself and only knows RSA and ECDSA keys. When the CMS comes from somewhere else (an Ed25519, Ed448 or post-quantum
key, a hardware or remote signer that returns whole CMS structures, long-term-validation data added by another library, or a signer in
another process), use `AddSignaturePlaceholder`. It writes the same incremental update as `Sign`, with the same field handling (new or
existing field, appearance, `Certify`, `FieldLock`, `/Filter Adobe.PPKLite`) and the same one-attempt-per-instance and hardening rules, but
leaves `/Contents` as an empty reserved hex string and tells you where it is:

    using var doc = PdfDocument.Open("contract.pdf", new PdfOpenOptions { AllowEditing = true });
    PdfSignaturePlaceholder p = doc.AddSignaturePlaceholder("contract-signed.pdf", new PdfSigningOptions
    {
        FieldName = "Approval",
        SubFilter = "ETSI.CAdES.detached",     // or "adbe.pkcs7.detached"; any plain name is written as given
        Hash = HashAlgorithmName.SHA384,        // the digest returned in p.Digest
        SignerName = "Example Signer",
    }, contentsReserve: 20_000);

    byte[] cms = MyCmsBuilder.Build(p.Digest, p.HashAlgorithm);   // detached CMS over p.Digest, any algorithm
    p.Fill("contract-signed.pdf", cms);                           // or PdfSignatureHole.Fill(path, p.HoleStart, p.HoleEnd, cms)

The second step needs only the file and two numbers, so it can run in another process or on another machine: keep `p.HoleStart` and
`p.HoleEnd`, recompute the digest with `PdfSignatureHole.ComputeDigest(stream, start, end, hash)` if you did not keep `p.Digest`, and call
`PdfSignatureHole.Fill`. The signature of an existing document is read back with `PdfSignature.OpenSignedData()` (below).

**The hole.** `HoleStart` is the offset of the `<` that opens the `/Contents` hex string and `HoleEnd` is the offset just after the closing
`>`: both delimiters belong to the hole and are not signed. `/ByteRange` is `[0 HoleStart HoleEnd SignedLength-HoleEnd]` (ISO 32000-2
12.8.1, the form `VerifySignatures` accepts), so the signed bytes are the file with exactly the hole cut out. Between the delimiters are
`2 * contentsReserve` hex digits, all `0`; so `contentsReserve` counts **bytes of CMS**, the hole is `2 * contentsReserve + 2` bytes long,
and it can be 64 bytes to 4 MiB. `Fill` writes the CMS as uppercase hex from the first digit and leaves the rest `0`, which readers
ignore as trailing zero bytes after the DER. Nothing else in the file changes, so no offset moves.

`Fill` checks before it writes: the stream must seek, read and write; the offsets must be a hole of the right shape holding only
`0` digits (`PdfSigningError.BadHole`: wrong offsets, a file that is not the placeholder output, or a hole already filled); and the CMS must
fit, otherwise `PdfSigningException` with `ContentsTooSmall` and `RequiredBytes`. A failed `Fill` has written nothing and the stream
position is restored, so you can retry with a smaller CMS; if the CMS can never fit, run `AddSignaturePlaceholder` again on a freshly
opened document with a larger reserve. The output is not a valid signature until the hole is filled; `Sign`'s memory use applies (the update
is built in memory), while `Fill` and `ComputeDigest` stream.

`PdfSigningOptions.SubFilter` (placeholder only) overrides the sub-filter that `Format` implies; names starting `ETSI.` also declare
`/Extensions /ESIC` on a first signature. `Timestamp`, `RsaPadding` and `ContentsReserve` are not used by the placeholder; the CMS you
build decides them. Without a certificate the visible appearance and `/Name` use `SignerName` only.

## Reading the signed bytes

`PdfSignature.OpenSignedData()` returns a read-only, seekable `Stream` over the two byte ranges concatenated, the bytes the CMS digest
covers. It reads through the document's source on demand (nothing is copied into memory, however large the file), the ranges were
already checked against the file length, and it returns `null` when `/ByteRange` is missing, malformed, does not start at 0, overlaps or
reaches beyond the file. It does not say whether the signature verifies; `Verify` does. `PdfSignature.Contents` holds the raw
`/Contents` bytes (DER followed by the zero padding).

## The whole file as a stream

`PdfDocument.OpenRead()` returns a read-only, seekable `Stream` over the document's bytes exactly as they were when it was opened: the
file, the seekable stream, the copy made from a non-seekable stream, or the `ReadOnlyMemory<byte>`. In-memory edits and signing updates
are never in it, so it is the input a two-phase signer or a hash of the original needs. It reads on demand through the document's
source and holds no copy, so a large file is not loaded a second time. A file or stream that changed length since open throws
`PdfSourceChangedException` on the next read. After the document is disposed, reading or seeking throws `ObjectDisposedException`, as does
the stream's own disposal; disposing the stream does not affect the document. Memory the caller passed in is referenced, so changing
it while the document is open is the caller's error.
