# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

## [1.6.0] - 2026-10-23

### Added

- True redaction. `PdfDocument.Redact` removes glyphs, vector paths, shadings and image samples inside given areas by rewriting page, form and annotation-appearance content (remaining glyphs keep their places), blanks and re-encodes images as Flate, removes or redacts annotations and form values under the areas, strips alternate text and structure text of the removed content, and clears metadata (Info, XMP, outline titles) that matches given terms. `ApplyRedactAnnotations` applies the `/Redact` annotations in a file. A page that cannot be read completely fails the call with `PdfRedactionException` and leaves the document unchanged. A `redact` fuzz target covers it. See `docs/redaction.md`.
- `PdfSoftMaskInfo.TransferTable`: the soft mask's `/TR` transfer function (ISO 32000-2 11.6.5.2) evaluated by OnePDF into 256 bytes (entry `i` is the output for `i / 255`, rounded half up and clamped), null for no `/TR` or `/Identity`. Function types 0, 2, 3 and 4 are supported; an unusable function is ignored with a warning. The display-list text format is version 6: `begin-softmask` gains `trtable=`.

### Changed

- Documented that viewers disagree on whether `/NeedAppearances` regenerates check box, radio button and push button art, and pinned OnePDF's behaviour (stored art is kept) with a test. No behaviour change.

### Fixed

- Reading order: columns whose paragraph gaps fall at the same heights are no longer read band by band when something spanning both columns (a full-width note or caption) stops the vertical cut. The horizontal cut now keeps successive bands together when each holds multi-line columns and they share a gutter, and the columns are cut apart afterwards. On the committed and local real-world set the reading-order error is 13.8 % overall (was 15.2 %): multi-column 7.6 % (was 16.8 %), table 0.6 % (3.0 %), form 4.2 % (6.5 %), rotated 9.3 % (12.2 %); math moves from 11.9 % to 12.5 %. The generated set is unchanged. See `docs/text-layout.md`.
- Editable documents: a new object never takes the number of a reference that points at an undefined object, so a dangling reference no longer starts to resolve to unrelated content added later.
- Encrypted files: the `/Contents` string of a signature dictionary (`/Type /Sig` or `/DocTimeStamp`, or any dictionary with `/ByteRange`) is no longer decrypted on read (ISO 32000-2 7.6.2), so the raw bytes, `PdfSignature.Contents` and a full save of a signed encrypted file all carry the stored signature.
- CFF subsets (CID-keyed) write the leading ROS operands of the Top DICT in their shortest integer form, which strict readers need to recognise the Top DICT as CID-keyed (veraPDF warned "The Top DICT does not begin with ROS operator").
- The TrueType subsetter can write a (3,1) format 4 `cmap`, and PDF/A production now subsets the Liberation faces it embeds for standard fonts (the four-font test file went from 834 KB to 112 KB) instead of embedding them whole.

## [1.5.0] - 2026-10-23

### Added

- PDF/A production. `PdfSaveOptions.PdfA` (`PdfAOptions`: part 2 or 3, level B or U) writes ISO 19005-2/-3 files from a full save: it first adds an sRGB output intent (a version 2 ICC profile OnePDF generates from the published sRGB parameters) and an XMP packet built from the Info dictionary, sets the Print flag and stores generated appearances on annotations, clears `/NeedAppearances` once every widget has an appearance, and embeds Liberation faces (from `RecursiveAI.OnePdf.Fonts`) for standard fonts OnePDF itself added. What is still wrong is refused with `PdfAException`, whose `Violations` list every problem with its clause, object and page: non-embedded fonts, CMYK, encryption, JavaScript and other forbidden actions, LZW, embedded files in part 2, missing appearances, level U fonts without Unicode mapping and more. `PdfDocument.CheckPdfA` reports without changing anything, `PreparePdfA` makes the repairs, and `PdfDocument.AttachFile` adds attachments for part 3. Output is deterministic with `PdfSaveOptions.DocumentId`. A `pdfa` fuzz target covers it. See `docs/pdfa.md`.
- `XmpMetadata.PdfAPart` and `PdfAConformance` read the PDF/A identification of a packet.

### Fixed

- Importing pages from a document whose fonts were added with `AddFont` copied the font before its subset had been built; the imported text drew with an empty font program. The source's fonts are now finished before pages are copied.

## [1.4.0] - 2026-10-23

### Added

- Signature verification, read-only. `PdfDocument.GetSignatures()` lists signature fields and document time-stamps with signer name, reason, location, contact, `/M`, sub-filter, `/ByteRange`, the CMS bytes, the `/DocMDP` certification level and the field lock; `PdfFormField.GetSignature()` returns one field's signature. `PdfSignature.Verify` and `PdfDocument.VerifySignatures` check, with the platform's hashes, RSA (PKCS#1 v1.5 and PSS) and ECDSA and its X.509 chain builder (no `System.Security.Cryptography.Pkcs`):
  - the byte range (starts at 0, leaves out exactly `/Contents`, no gaps or overlaps, ends at a revision boundary) and which revision it covers;
  - the digest, the signed attributes and the signature, for `adbe.pkcs7.detached`, `ETSI.CAdES.detached` (with the signing-certificate attribute), `ETSI.RFC3161` document time-stamps, and the legacy `adbe.pkcs7.sha1` (flagged weak); `adbe.x509.rsa_sha1` is reported as unsupported and weak;
  - an RFC 3161 signature time-stamp, itself verified, and the certificate chain against `TrustAnchors` (or, if asked, the system store) at a validation time chosen from the time-stamp, the signing time or now, or given explicitly;
  - revocation, which is never fetched: reported as not checked unless a `RevocationCheck` callback answers from data the caller holds;
  - changes after signing, object by object and classified (form field value, new signature, annotation, page content, validation data, other) against the DocMDP level and FieldMDP locks.
  The result (`PdfSignatureVerification`) keeps every check separate; `IsValid` is a convenience. See `docs/signatures.md`.
- A `signature` fuzz target (ASN.1/CMS parsing, certificates, chain building, byte ranges and change analysis; also time-stamp responses).
- Signing. `PdfDocument.Sign` writes an incremental update holding an approval or certification signature (`PdfCertificationLevel` 1 to 3, an optional field `/Lock`), and `PdfDocument.AddDocumentTimestamp` adds an `ETSI.RFC3161` document time-stamp; both need a document opened with `AllowEditing`, keep the original bytes as an exact prefix and keep encryption (the `/Contents` string is never encrypted, ISO 32000-2 7.6.2).
  - Formats: PAdES baseline B-B (`ETSI.CAdES.detached` with `contentType`, `messageDigest` and `signingCertificateV2`, no `signingTime`, and the ESIC extensions entry for pre-2.0 files), B-T when `PdfSigningOptions.Timestamp` is set, or `adbe.pkcs7.detached`. Digests SHA-256, SHA-384, SHA-512; RSA PKCS#1 or PSS, and ECDSA.
  - Keys: `PdfSigningCredential.FromCertificate` (a certificate with a private key) or `FromExternalSigner`, a callback that receives the digest of the signed attributes and returns the signature value, for HSMs and remote signing. Every signature value is checked with the certificate's public key before it is embedded.
  - Time-stamps only through a `PdfTimestampCallback` that receives the RFC 3161 request and returns a response or token; OnePDF opens no connection. The token's status, imprint and nonce are checked before it is embedded.
  - Visible signatures get an appearance built with the content builder (name, date, reason, location, optional image). Failures throw `PdfSigningException` with a `PdfSigningError` reason and write nothing. Our own DER writer builds the CMS; no `System.Security.Cryptography.Pkcs`.
  - `PdfSigningResult` reports the reserved and used `/Contents` size. See `docs/signatures.md`.

### Fixed

- Signature verification checked too little in several attack cases found by crafting the published PDF signature attacks (see `docs/signatures.md`, "Known attacks"):
  - Data appended after the last `%%EOF` (objects with no cross-reference section, or a bare trailer) is now an unsigned change; before, a file whose signature covered its last revision still counted as the whole document.
  - An update whose trailer points `/Root` at a different catalog is a disallowed change.
  - The certification level is read from the signed revision, not from the newest dictionary.
  - An indirect `/ByteRange` or `/Contents` is refused.
  - A signer certificate whose key usage lacks `digitalSignature`/`nonRepudiation`, or whose extended key usage is only for other purposes, now fails `IsValid` (`PdfSignatureVerification.KeyUsage`; `PdfSignatureVerifyOptions.RequireSigningKeyUsage = false` allows it). A time-stamp signed by a certificate without the time-stamping purpose is invalid.
- Signature verification no longer throws on a damaged embedded certificate on macOS, where the platform accepts certificate bytes and fails only when a property is read. Every certificate from the file, the extra certificates and the trust anchors is read once on load; one that fails is left out with a warning (the signer then counts as missing). Signing checks the credential's certificates the same way and reports `PdfSigningException` instead of a platform exception.

## [1.3.0] - 2026-10-23

### Added

- Form filling. `PdfFormField.SetValue`, `SetValues`, `SetChecked` and `Reset`, and `PdfAcroForm.SetValue`, `SetValues`, `SetChecked`, `ResetAll` and `GenerateMissingAppearances`, for documents opened with `AllowEditing`: text (with `/MaxLen`), combo and list boxes (export or display values, multi-select), check boxes and radio groups. `/V` and `/AS` are updated, generated normal appearances are stored in `/AP`, and `/NeedAppearances` is cleared once every widget has one. Refused values throw `PdfFormValueException` (an `ArgumentException`) with a `PdfFormValueError` reason; `PdfFillOptions` controls read-only fields, truncation and appearance generation. Scripts never run. See `docs/forms.md`.
- Flattening. `PdfDocument.Flatten(PdfFlattenOptions?)` bakes widget appearances (and optionally other annotations) into page content as Form XObjects, removes the annotations and fields, and drops an empty `/AcroForm`; it returns a `PdfFlattenResult`.
- The `appearance` fuzz target also fills, flattens, saves and reopens.
- Page operations on editable documents: `RemovePage`/`RemovePages`, `MovePage`, `ReorderPages`, `RotatePage`/`SetPageRotation`, `InsertBlankPage`, `DuplicatePage`, and `PdfDocument.CreateEmpty`. Deleting a page repairs the bookmarks, links, named destinations, open action, form fields and page labels that referred to it.
- Pages across documents: `PdfDocument.ImportPages` copies pages with their whole dependency graph (resources, fonts, images, annotations with appearances; inherited `/Resources`, `/MediaBox`, `/CropBox` and `/Rotate` written into the page), each shared object once. Bookmarks, named destinations used by links, form fields (clashing names renamed), optional content, page labels and the structure tree of tagged files come along (`PdfImportOptions`). `PdfDocument.Merge` joins documents; `PdfDocument.Split` cuts one into several (`PdfPageRange`, or a number of pages per part). See `docs/page-operations.md`.
- Content creation. `PdfPage.BeginContent` returns a `PdfPageContentBuilder` that adds content over or under a page's own (the existing content is wrapped so its graphics state cannot leak in); `PdfDocument.BeginForm` builds a reusable Form XObject. `PdfContentBuilder` writes paths (fill, stroke, clip, dashes, caps, joins), DeviceGray/RGB/CMYK colours, opacity and blend modes (ExtGState), a transform stack, text (character and word spacing, render modes, `MeasureText`, `WrapText`, `DrawTextAligned`, `DrawTextBox`), JPEG bytes embedded as they are, raw 8-bit images with an alpha plane, and images and forms already in the document. Fonts: `PdfDocument.AddStandardFont` (WinAnsi, Adobe metrics) and `AddFont` for TrueType and OpenType (CFF) files, written as Type 0 fonts with Identity-H, a `/W` array and a `/ToUnicode` map, subset to the glyphs used (own `glyf`/`loca` and CFF subsetters; ISO 32000-2 9.6.4 tags; the `OS/2` `fsType` embedding and no-subsetting bits are honoured, with `PdfFontEmbeddingException` and an explicit override). `AddTextStamp` and `AddImageStamp` watermark chosen pages through one shared form. `PdfSaveOptions.DocumentId` fixes the trailer `/ID` so saves are reproducible. See `docs/content-creation.md`.
- A `fontsubset` fuzz target.
- `docs/migrating-from-pdfsharp.md`, a guide for moving from PDFsharp.
- The `save` fuzz target also runs page operations, merge and split.
- `PdfImageDecodeRequest.TargetWidth` and `TargetHeight`: the size in device pixels the image covers on the page (the unit square under the CTM and output transform, rounded up, never above the image's own size), or 0 when unknown. A JPEG 2000 decoder can use it to decode at a reduced resolution. `PdfImage.TargetWidth`/`TargetHeight` expose the same values to devices. Decoding outside a page leaves them 0; existing decoders can ignore them.
- A decoder may return fewer pixels than the dictionary declares. OnePDF passes the reduced size through (`PdfImageData.Width`/`Height`) without resampling; placement is unchanged because the image fills the unit square. No warning is recorded when a target size was given and the result is not larger than declared.

### Fixed

- TrueType instructions (fonts whose composites need their programs): ISECT now uses exact 128-bit arithmetic instead of treating a discriminant of -1 as degenerate. A glyph program that fails on its own now leaves only its own component unhinted; the whole glyph is drawn from its plain outline only when the shared per-glyph instruction budget is spent.

## [1.2.0] - 2026-10-23

### Added

- Knockout data for devices. `PdfGraphicsState.TextKnockout` carries the ExtGState `/TK` flag (default true; saved and restored with `q`/`Q`). `IPdfDevice.FillStrokePath` (a default interface method, also virtual on `PdfDevice`) receives `B`, `B*`, `b` and `b*` as one call; its default calls `FillPath` then `StrokePath`, so existing devices behave as before. `PdfDisplayList` records it as one item and replays it as `FillStrokePath`. The display-list text format is version 5 (`fillstroke` record, `tk=0` in `gs`).
- Writing. `PdfOpenOptions.AllowEditing` opens a document for changing; `PdfDictionary.SetEntry`/`RemoveEntry`, `PdfArray.Append`/`Insert`/`SetItem`/`RemoveAt`/`Clear`, `PdfStream.SetData`/`SetStoredBytes`, `PdfDocument.AddObject`/`SetObject`/`RemoveObject`/`CreateStream`/`SetInfo`/`SetXmp`, and public constructors for the simple COS types. See `docs/writing.md`.
- `PdfDocument.Save` writes a complete new file (classic table, or cross-reference and object streams; optional Flate for unfiltered streams; unreachable objects dropped, numbers compact) and can keep, remove or set encryption (AES-128 revision 4, AES-256 revision 6). `PdfDocument.SaveIncremental` appends an update that leaves the original bytes untouched. Options are in `PdfSaveOptions` and `PdfEncryptionOptions`.
- A `save` fuzz target (open, change, save, reopen).

### Fixed

- `PdfImage` encoded data of an encrypted document (`GetEncodedData`) was the ciphertext; it is now the stored bytes with the encryption removed.
- A `startxref` that pointed exactly at a cross-reference section was passed over for a reading that landed on white space in front of another object, in files with junk before the header; an exact hit now wins.
- Composite glyphs of MingLiU, PMingLiU and DFKai-SB fonts (and subsets of them) were drawn with displaced or missing strokes, because their component placement is done by the font's own instructions. For fonts recognised from their names or program-table checksums, the glyph instructions now run (at one pixel per font unit, without grid fitting), in a budgeted interpreter; a failing program falls back to the plain outline with a diagnostic. All other fonts are unchanged and never execute font code. See `docs/pdf-fonts.md`.

## [1.1.0] - 2026-10-23

### Added

- Appearance generation. An annotation with no usable normal appearance, and every text or choice field of a form that sets `/NeedAppearances true`, is now drawn from its own data instead of being skipped: widgets (text, multi-line, comb, password, combo box, list box, check box, radio button, push button, with `/MK` and `/BS` borders and rotation) and the Square, Circle, Line, Polygon, PolyLine, Ink, Highlight, Underline, StrikeOut, Squiggly, FreeText and Text annotations. See `docs/appearance-generation.md`.
- `PdfAnnotation.GenerateAppearance()` returns the synthesised appearance as data (`PdfGeneratedAppearance`: form dictionary, content bytes, rectangle) without touching the document.
- `PdfInterpretOptions.GenerateAppearances` (default true); set it to false for the 1.0 behaviour. Each generated appearance is recorded as an Info diagnostic.
- `PdfLimits.MaxAppearanceText` and `PdfLimits.MaxAppearanceItems` bound the text and the item counts of a generated appearance.

### Changed

- Pages whose annotations lacked an appearance now interpret to more content than before; pages with stored appearances (and no `/NeedAppearances`) are unchanged. Text extraction is unchanged.

## [1.0.1] - 2026-10-23

### Fixed

- The package README's links to documentation, benchmarks, licenses and notices are absolute, so they work on the nuget.org package page.

## [1.0.0] - 2026-10-23

First release. Two packages, `RecursiveAI.OnePdf` (the library, `net10.0`, no dependencies beyond .NET itself) and `RecursiveAI.OnePdf.Fonts` (substitute fonts for the standard 14), tested on Windows x64, Linux x64 and arm64, and macOS arm64, with NativeAOT and trimming supported.

### Added

- Reading: cross-reference tables and streams, object streams, hybrid and incremental files, and a documented repair chain for damaged files (trust the xref, fix offsets and lengths by scanning, rebuild by scanning for object headers). Repairs are reported as diagnostics. Lenient by default, `PdfOpenOptions.Strict` for errors.
- Filters: Flate, LZW, ASCIIHex, ASCII85, RunLength, PNG and TIFF predictors, CCITT fax and Crypt; DCT, JPX and JBIG2 through the `IPdfImageDecoder` registry.
- Encryption: the standard security handler, revisions 2 to 6 (RC4 40/128, AES-128, AES-256), with user or owner password.
- Document model: public COS objects and typed wrappers for pages, Info and XMP metadata, outlines, page labels, links, annotations, embedded files, AcroForm field values and permissions. Objects parse lazily into a byte-budgeted cache; any number of threads can read one document.
- Fonts: Type 1, CFF and CID-keyed CFF, TrueType, OpenType-CFF and Type 3, encodings and `/Differences`, ToUnicode, predefined CMaps for the Adobe CJK collections, the standard 14 metrics and a font-provider interface.
- Text extraction: glyphs, words, lines and blocks in reading order (structure tree for tagged files, geometry otherwise), right-to-left text, vertical text, three plain-text modes and a versioned JSON form.
- Content interpreter: `page.Interpret(device)` drives `IPdfDevice` with paths, glyph runs, images, shadings, patterns, transparency groups, soft masks, optional content and annotation appearances; `PdfDisplayList` records and replays calls, with a versioned text format.
- Limits on by default (`PdfLimits`) and fuzzed parsers, filters and font readers; no `unsafe` code. OnePDF never runs code or touches the network.
- `RecursiveAI.OnePdf.Fonts`: Liberation and Noto substitute outlines under the SIL Open Font License 1.1, registered with `FontsPackage.Register()`.

[1.6.0]: https://github.com/recursive-ai-oss/onepdf-csharp/releases/tag/v1.6.0
[1.5.0]: https://github.com/recursive-ai-oss/onepdf-csharp/releases/tag/v1.5.0
[1.4.0]: https://github.com/recursive-ai-oss/onepdf-csharp/releases/tag/v1.4.0
[1.3.0]: https://github.com/recursive-ai-oss/onepdf-csharp/releases/tag/v1.3.0
[1.2.0]: https://github.com/recursive-ai-oss/onepdf-csharp/releases/tag/v1.2.0
[1.1.0]: https://github.com/recursive-ai-oss/onepdf-csharp/releases/tag/v1.1.0
[1.0.1]: https://github.com/recursive-ai-oss/onepdf-csharp/releases/tag/v1.0.1
[1.0.0]: https://github.com/recursive-ai-oss/onepdf-csharp/releases/tag/v1.0.0
