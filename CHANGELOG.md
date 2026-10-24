# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- SVG pictures in layout, as vector PDF content. `SvgImage` (`Parse` from bytes or text, `Width`, `Height`, `Title`, `Description`, `Draw` into any `PdfContentBuilder`), `SvgDrawOptions`, `SvgLimits`, the `SvgPicture` element, the fluent `Slot.Svg(...)` with `SvgDescriptor` (fit modes, DPI, alignment, `AltText`, `Artifact`) and `CanvasSurface.DrawSvg`. Output is paths, strokes with dashes, caps and joins, linear and radial gradients as PDF shadings (spread methods unrolled into stops), gradients and patterns on fills and strokes, clip paths, masks as soft masks, group opacity, blend modes, `use`/`symbol`/nested `svg`, markers, `viewBox` and `preserveAspectRatio`, a CSS subset (presentation attributes, `style`, `<style>` sheets with selectors), PNG and JPEG data URIs, and text shaped through OneFont with the layout's fonts, so it extracts as text. A tagged document writes the picture as a `Figure` whose alternate text is the author's, else the SVG `<title>`, else its `<desc>`; PDF/UA-1 output with an SVG passes veraPDF. Scripts, animation, `foreignObject`, external references and filters are dropped (filters draw the unfiltered content), each with a diagnostic; nothing is fetched or run. The XML reader refuses entity expansion and every size, depth, instance, path, stop, CSS, data-URI and text bound is a `SvgLimits` setting that raises `PdfLimitException`. There is an `svg` fuzz target. See `docs/layout-svg.md`.
- Core: transparency groups and soft masks on the content builder. `PdfFormContentBuilder.SetTransparencyGroup(isolated, knockout)` marks a form as a transparency group, `PdfContentBuilder.SetSoftMask(form, PdfSoftMaskKind)` and `ClearSoftMask()` apply and remove a soft mask made from a form, and `PdfSoftMaskKind` chooses alpha or luminosity. The SVG renderer needs them for group opacity and masks; existing output does not change.
- PDF/UA-2 (ISO 14289-2, on PDF 2.0) output and checking. `PdfUAOptions.Part` (`PdfUAPart.Part1`, the default and unchanged, or `Part2`), `PdfDocument.CheckPdfUA(PdfUAPart, CancellationToken)`, `PdfUAViolation.Part`, `XmpMetadata.PdfUARevision`. A part-2 save writes PDF 2.0, lists the PDF 2.0 and PDF 1.7 structure namespaces on the structure tree root and names one in `/NS` on every element (`Note` becomes `FENote`), fills `/Ref` on table-of-contents items from the link inside them, and identifies the file with `pdfuaid:part` 2 and `pdfuaid:rev` 2024. It refuses what a person has to supply or what OnePDF cannot choose, with the clause of ISO 14289-2 for each finding: the unnumbered `H`, a list with labels and no `ListNumbering`, irregular tables, figures without alternate text, page destinations inside the document (PDF/UA-2 needs structure destinations), custom or unnamespaced structure types, and the part-1 findings that still apply. Combining it with PDF/A-2 or PDF/A-3 (PDF 1.7 formats) throws `PdfAException`. `PdfDocumentWriter.SetBookmarkElement` and `SetDestinationElement` write structure destinations in a PDF 2.0 writer; layout uses them so bookmarks, anchors and table-of-contents links work out of the box. Layout gains `StructureGroup` and `Slot.Aside()`, `Footnote()`, `Title()` and `Group(type)` for the 2.0 structure types. Checked with veraPDF's `ua2` flavour as a black box. See `docs/pdfua.md`.
- Colour glyphs in layout: OpenType `COLR` version 0 glyphs with `CPAL` palettes are drawn as their layers in palette colours, the foreground entry in the text colour. `ColorPalette` (`Default`, `Light`, `Dark`, `FromIndex`) chosen by `TextStyle.Palette`, `Text.Palette`, the fluent `Palette(...)` or `LayoutOptions.ColorPalette`. Each glyph's layers sit in one `/ActualText` span, so extraction returns the character once; PDF/A-2b and PDF/UA-1 output with colour glyphs passes veraPDF. Tables that are not valid, and glyphs described only in `COLR` version 1, are drawn in one colour with one diagnostic per font. See `docs/layout-text.md`.
- Inline objects in layout text: an icon, a small picture, a badge with its own text or a drawn checkbox sits in a line of text and flows with it. `TextSpan.Inline`/`TextSpan.ForInline`, `InlineObject` (`InlineBox` for a drawn or picture box, `InlineElement` for an element subtree in a layout `Text`), `InlineAlignment` (baseline with an offset, top, middle, bottom, text top, text bottom), `InnerBaseline`, `GrowsLine`, `KeepWithNeighbours`, `AltText`, `Artifact` and `Link`; the fluent `TextDescriptor.Inline`, `InlineImage` and `InlineCanvas` with `InlineDescriptor`. An object is one unbreakable character (U+FFFC: line break class CB, bidi class ON) with breaks around it by first fit and Knuth-Plass, justification spreads the spaces and leaves it alone, the line grows to hold it unless the line height is fixed, and right-to-left lines place it with its neighbours. In tagged output it is a `Figure` inside the paragraph with `/Alt` (or an artifact), the text extractor returns its description through `/ActualText` and never a U+FFFC, and links and anchors work on it; PDF/A-2a/3a and PDF/UA-1 output stays compliant. A literal U+FFFC in span text is now dropped. Documents without inline objects are byte-identical to before. See `docs/layout.md` and `docs/layout-text.md`.
- Per-page values in layout: `Accumulate` adds a `decimal` to a named account and `Marker` records a text under a key, both wrapping content (`Slot.Accumulate`, `Slot.Marker`); `DynamicText` (`Slot.DynamicText`, styled by `DynamicTextDescriptor`) is a line of text computed for each page by a callback from `PageValues`: `Sum`, `Total`, `BroughtForward`, `First`, `Last`, `Current`, `Entering`, page index, number, label and section. Values come from what actually landed on the page after page breaking, so "Carried forward" and "Brought forward" lines, page subtotals and running headers move with the rows that keep rules push to the next page. They work in headers, footers and the body, in tables, multi-column flow, sections, right-to-left documents and documents with a table of contents (no double counting), are artifacts in headers and footers and tagged text in the body, and are deterministic. See `docs/layout.md`.

## [1.9.0] - 2026-10-24

### Added

- Knuth–Plass line breaking in layout, opt in per paragraph and as a document default. `LineBreakMode` (`Greedy`, the default, and `KnuthPlass`), `KnuthPlassOptions` (tolerance, pre-tolerance, line, hyphen and explicit-hyphen penalties, consecutive-hyphen, final-hyphen and fitness demerits, looseness, emergency stretch), `TextParagraph.LineBreakMode`/`KnuthPlass`, `Text.LineBreakMode`/`KnuthPlass`, `LayoutOptions.LineBreakMode`/`KnuthPlass`, `TextDescriptor.OptimalLineBreaks`/`GreedyLineBreaks` and `DocumentComposer.LineBreaking`. After Knuth and Plass, 1981: the breaks of a whole paragraph are chosen together, with hyphenation points, mixed fonts and sizes and bidi text; a paragraph with no setting in tolerance is retried with emergency stretch and then accepts any line, and one beyond its search bounds is broken greedily with a warning. Greedy output is unchanged. See `docs/layout-text.md`.
- `RecursiveAI.OnePdf.Hyphenation`, a new package: `PatternHyphenator` (Liang's pattern algorithm with exception lists, minimum word length and left/right minimums) and `Hyphenators` (`For`, `Auto`, `Resolve`, `IsSupported`, `Languages`), with bundled patterns for 26 language variants loaded lazily from compressed resources: English (US, GB), German (1996, 1901, Swiss 1901), French, Spanish, Italian, Dutch, Portuguese, Polish, Danish, Norwegian (no, nb, nn), Bulgarian, Slovak, Slovenian, Lithuanian, Estonian, Ukrainian, Galician, Greek (monotonic), Latin, Welsh and Irish. Plug it into layout with `TextDescriptor.Hyphenator(Hyphenators.Auto())`; layout itself does not depend on it and hyphenation stays opt-in. Pattern data is generated by `tools/hyphenation/generate.py` from pinned upstream files, each under its own permissive licence (notices in the package). Swedish, Finnish, Icelandic, Catalan, Croatian, Russian, Turkish, Esperanto, Czech, Hungarian, Latvian and Romanian are not bundled because their pattern files are copyleft, LPPL-only or have unclear terms. See `docs/layout-text.md`.
- Gradient backgrounds in layout: `LayoutGradient` (`Linear` at any angle, `Horizontal`, `Vertical`, `Circle` with a centre and `RadialExtent`) and `LayoutGradientStop`, taken by `Slot.Background`, `Box.BackgroundGradient`, table rows and cells (`TableRowDescriptor.Background`, `CellBackground`, `TableRow.BackgroundGradient`, `TableCell.BackgroundGradient`) and page backgrounds. Clipped to rounded corners, faded by opacity, carried by transforms, drawn as artifacts. A box split over pages paints the whole gradient in each piece. See `docs/layout.md`.
- Multi-column flow in layout: `MultiColumn` (element) and `Slot.MultiColumn(cols => ...)` pour paragraphs, lists, tables and pictures through 1 to 32 equal columns, down each column, then across, then onto the next page. Column count, gap, an optional rule between columns, balanced (default) or sequential fill (`ColumnFill`), `Spacing` between items, and `ColumnBreak`. Content breaks between columns as it breaks between pages: widows and orphans, keep-together, keep-with-next, ensure-space, headings never ending a column, table rows splitting with repeated header rows. Balancing the last region is a bisection of at most 12 trial layouts, so it is bounded whatever the content, and it never breaks a rule to even the columns out. Right-to-left documents fill columns right to left; the structure tree and text extraction follow the flow. See `docs/layout.md`.
- Content builder: `PdfGradient` (`Axial`, `Radial`), `PdfGradientStop`, `PdfGradientColor` (DeviceGray, DeviceRGB, DeviceCMYK), `PdfContentBuilder.PaintGradient` and `FillRectangle`. Written as axial and radial shadings with exponential and stitching functions. See `docs/content-creation.md`.

- Generated table of contents in layout: `TableOfContents` (levels, leader, indent, gap, spacing, per-level `TocLevelStyle`), `TocEntry` for explicit entries, `Heading.InToc` and `Heading.TocTitle`, `LayoutOptions.TableOfContents`, and the fluent `Slot.TableOfContents`, `Slot.TocEntry`, `Slot.Heading(level, inToc, tocTitle)` and `TocDescriptor`. Entries show the page label of their target (roman and prefixed numbering included), wrap long titles with the number on the last line, link to their heading and are tagged `TOC`/`TOCI`/`Reference`/`Link`. Page numbers are settled by laying the document out without drawing until the entries stop changing, then once more for real; documents without a table are unaffected. See `docs/layout.md`.

### Changed

- Faster first document in a managed process (JIT included): about 16 ms less for the layout invoice (about 130 to 114 ms on the benchmark host). `PdfDocument.CreateEmpty` (and with it every `PdfDocumentWriter`) builds its cross-reference data directly instead of reading a generated file back, and the first document no longer compiles struct-keyed concurrent and sorted collections: the editable document's object table, the layout font-chain and shaping-option caches, the writer's information, destination and role maps, and the TrueType subsetter's table set and composite check now use plain dictionaries, arrays and snapshots. Output is byte-identical. Peak memory of the first document is about 1 MB lower.
- Much less memory when recording large pages into a `PdfDisplayList`. The recording now grows in fixed-size chunks instead of doubling and copying its arrays, stores each distinct graphics state once, and shares the outline of a glyph between all its uses instead of copying it for every occurrence (`PdfPath.ToOwned` returns a library-owned, never-modified path as it is). Replay output and the text format are unchanged. On two large real-world files (open, extract text, interpret every page): allocation 2.3 GiB to 0.9 GiB and peak working set about 1.07 GiB to 0.43 GiB for a 137-page, 135,000-item-per-page file; 3.3 GiB to 1.5 GiB and 1.0 GiB to 0.47 GiB for a 154-page file with 300,000 items on a page. A text-heavy page into a display list: 15 ms to 2.2 ms and 32 MB to 4.8 MB allocated.
- PDF/A saves embed the standard-14 substitutes (Liberation) as subsets of the characters the document actually shows, not of the whole encoding. The pages, form XObjects, patterns, soft-mask groups, Type 3 glyph procedures and annotation appearances are read to find the codes in use; a font that is never shown, is listed in the form's default resources, or sits where the scan cannot follow keeps its whole encoding. A two-font PDF/A-2b document went from 63,030 to 14,856 bytes.
- CFF subsets (`AddFont` with an OpenType-CFF or bare CFF font) no longer copy the source String INDEX; the subset's own two ROS strings are all it holds.
- A glyph that ends in the old accented-character form of `endchar` (`adx ady bchar achar`, Type 2 charstring appendix C) can now be subset. Its composed outline and width are written as a plain path charstring, because a CID-keyed subset has no glyph names to resolve the two StandardEncoding parts with, so the base and accent glyphs are not needed in the subset. A base or accent that is itself composed is still refused with a `FontProgramException`.
- Signing: a time-stamp token returned with indefinite lengths or constructed OCTET STRINGs is re-encoded to DER before it is embedded (ETSI EN 319 142-1 wants a DER SignedData in `/Contents`). Only the length framing changes; certificates, signed attributes and every value are copied byte for byte.

### Fixed

- Flattening a form in a tagged document left `OBJR` entries in the structure tree pointing at the removed widgets, and their entries in the parent tree. Both are removed now, and a structure element that held nothing else (a `Form` element, say) goes with them, so the tree stays valid and PDF/UA-1 output stays compliant. The baked-in appearance is marked as an artifact in a tagged document.
- Overlays and stamps count the page's open graphics states with the content-stream lexer instead of a scan of the raw bytes, so `q` and `Q` inside strings, comments, names, dictionaries and inline image data are no longer counted, and a damaged or unbalanced stream cannot throw the count off.
- Text stamps (and any text) set on a diagonal came out of extraction in reversed letter order, and at 135 degrees as single letters. Text rotated by more than 12 degrees from its quantized reading direction is now measured along its own baseline, so its glyphs share one line. Axis-aligned text is unchanged.

## [1.8.0] - 2026-10-24

### Added

- PDF/UA-1 (ISO 14289-1): `PdfSaveOptions.PdfUA` (`PdfUAOptions`), `PdfDocument.PreparePdfUA` and `CheckPdfUA`, `PdfUAException` and `PdfUAViolation` (Matterhorn checkpoint and ISO clause per finding). A save repairs `/MarkInfo`, `/Lang` (from `PdfUAOptions.Language`), `DisplayDocTitle`, `/Tabs /S`, link `/Contents` and the XMP identification, and refuses with every violation listed: missing title or language, figures without alternate text, untagged content, skipped heading levels, tables without header cells or with irregular rows, malformed lists, unmapped structure types, links and widgets outside their structure elements, unembedded fonts and encryption that blocks assistive technology. Only for documents that are already tagged; a clean result is not proof of conformance. See `docs/pdfua.md`.
- PDF/A level A: `PdfAConformance.A` for parts 2 and 3 (PDF/A-2a, 3a), alone or combined with PDF/UA-1 (the `pdfuaid` schema is declared as an XMP extension schema); `PdfAOptions.Language`. `XmpMetadata.PdfUAPart`.
- Layout: `LayoutDocument.Author`, `Subject` and `Keywords` (and the same on the fluent `DocumentComposer`); `LayoutDocument.Save(stream, PdfSaveOptions)` takes `PdfUA` and level A.

## [1.7.0] - 2026-10-24

### Added

- `PdfFontSource`: parses a font once and shares it between documents (`PdfDocument.AddFont` and `PdfDocumentWriter.AddFont` overloads), so producing many documents with the same face does not parse and validate it each time.
- `PdfDocumentWriter` packs dictionaries and arrays into compressed object streams (the catalog stays plain) unless the cross-reference format is a table. A tagged 250-page table went from 8.0 MB to 2.0 MB.

- `PdfDocument.AddPngImage`: a minimal PNG reader for image XObjects. Plain non-interlaced PNGs (gray, RGB, palette, 1 to 16 bits) are passed through with their IDAT data unchanged (`FlateDecode`, `/Predictor 15`); interlaced PNGs and PNGs with alpha are decoded, alpha becoming a soft mask; `tRNS` becomes a colour-key mask where it can, and a matching `iCCP` profile becomes an `ICCBased` colour space. A `png` fuzz target covers it. See `docs/content-creation.md`.
- `PdfContentBuilder.DrawGlyphRun` and `PdfShapedGlyph`: draw glyphs chosen by a shaping engine, by glyph index with advances, offsets and clusters, from an embedded font. Each cluster maps to text through the font's `/ToUnicode` map, or through `/ActualText` where one glyph cannot carry it (reordered or split clusters, a glyph reused for other text), so extraction returns the logical text.
- Marked content on the content builder: `BeginMarkedContent`, `BeginSpan`, `BeginArtifact`, `EndMarkedContent`, `MarkedContentDepth`. Marked content and `Save`/`Restore` must nest.
- `PdfDocumentWriter`: streaming creation. Each page is written when committed, so a 10,000-page document needs a few hundred bytes of memory per page. It brings `PdfStreamPageBuilder`, reserved forms for "page X of Y" (`ReserveForm`), links, bookmarks and named destinations, and a tagged structure tree (`BeginStructureElement`, `PdfStructureElement`, `PdfStructureElementOptions`, `PdfStructureAttributes`, `MapRole`) with marked-content ids, `OBJR` entries for links, a parent tree and `/Tabs /S`.
- `PdfDocumentWriter.AddPageLabelRange`: page labels (ISO 32000-2 12.4.2) for streamed documents: decimal, roman and letter styles, prefixes and start numbers.

- `PdfDocumentWriter.BeginStructureElement(parent, type, options)` and `EndStructureElement(element)`: begin a structure element under an explicit parent and end any open element once its own children have ended, so a producer whose content continues on later pages (a table row split over pages) can keep several branches open at once. The earlier overloads are unchanged.

- `RecursiveAI.OnePdf.Layout`, a new package that depends on `RecursiveAI.OnePdf`, `RecursiveAI.OnePdf.Fonts` and `RecursiveAI.OneFont` 1.1.0: a fluent layout layer. `LayoutDocument.Compose` takes nested delegates for pages, header, content and footer slots, decorators (padding, border, background, rounded corners, size limits, alignment, rotate/scale/translate), columns, rows, layers, lists, tables with spans and repeated header and footer rows, images, canvas, shaped rich text with page numbers and totals, links, bookmarks, tagging and right-to-left documents; `IComponent` for reuse; `Save` streams page by page, `Save(stream, PdfSaveOptions)` adds PDF/A and encryption, `ToPdfDocument` hands the result to the core API. The element objects stay public as the lower layer. See `docs/layout.md` and `docs/migrating-from-questpdf.md`.
- `layout` fuzz target and a set of layout reference documents with page images (`testdata/layout-references`, `tools/OnePdf.Layout.References`).

### Changed

- Layout: shaping is done once per text and released after its last line; fonts are parsed once per face; `Table.MeasuredRows` is `int?` (null: every row when a column is automatic, otherwise the first 100 body rows). Producing documents is about twice as fast and allocates about half as much.
- Layout: page totals ("page X of Y") are shaped and drawn with the embedded font, so documents with totals pass PDF/A-2b and PDF/UA-1 font checks. `Row.Compact` makes a row as wide as its items. `Rotate` and `Scale` throw `PdfLayoutException` when their child would need a page break, instead of dropping the content after it.

## [1.6.0] - 2026-10-23

### Highlights

- True redaction: text, paths, images, annotations and metadata under given areas are removed from the file, not covered.
- Reading order on multi-column pages: the reading-order error on the real-world multi-column category drops from 16.8 % to 7.6 % (PdfPig, best of its pipelines: 16.8 %); overall from 15.2 % to 14.0 %.

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

[1.9.0]: https://github.com/recursive-ai-oss/onepdf-csharp/releases/tag/v1.9.0
[1.8.0]: https://github.com/recursive-ai-oss/onepdf-csharp/releases/tag/v1.8.0
[1.7.0]: https://github.com/recursive-ai-oss/onepdf-csharp/releases/tag/v1.7.0
[1.6.0]: https://github.com/recursive-ai-oss/onepdf-csharp/releases/tag/v1.6.0
[1.5.0]: https://github.com/recursive-ai-oss/onepdf-csharp/releases/tag/v1.5.0
[1.4.0]: https://github.com/recursive-ai-oss/onepdf-csharp/releases/tag/v1.4.0
[1.3.0]: https://github.com/recursive-ai-oss/onepdf-csharp/releases/tag/v1.3.0
[1.2.0]: https://github.com/recursive-ai-oss/onepdf-csharp/releases/tag/v1.2.0
[1.1.0]: https://github.com/recursive-ai-oss/onepdf-csharp/releases/tag/v1.1.0
[1.0.1]: https://github.com/recursive-ai-oss/onepdf-csharp/releases/tag/v1.0.1
[1.0.0]: https://github.com/recursive-ai-oss/onepdf-csharp/releases/tag/v1.0.0
