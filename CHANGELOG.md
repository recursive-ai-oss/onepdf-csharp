# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Form filling. `PdfFormField.SetValue`, `SetValues`, `SetChecked` and `Reset`, and `PdfAcroForm.SetValue`, `SetValues`, `SetChecked`, `ResetAll` and `GenerateMissingAppearances`, for documents opened with `AllowEditing`: text (with `/MaxLen`), combo and list boxes (export or display values, multi-select), check boxes and radio groups. `/V` and `/AS` are updated, generated normal appearances are stored in `/AP`, and `/NeedAppearances` is cleared once every widget has one. Refused values throw `PdfFormValueException` (an `ArgumentException`) with a `PdfFormValueError` reason; `PdfFillOptions` controls read-only fields, truncation and appearance generation. Scripts never run. See `docs/forms.md`.
- Flattening. `PdfDocument.Flatten(PdfFlattenOptions?)` bakes widget appearances (and optionally other annotations) into page content as Form XObjects, removes the annotations and fields, and drops an empty `/AcroForm`; it returns a `PdfFlattenResult`.
- The `appearance` fuzz target also fills, flattens, saves and reopens.
- Page operations on editable documents: `RemovePage`/`RemovePages`, `MovePage`, `ReorderPages`, `RotatePage`/`SetPageRotation`, `InsertBlankPage`, `DuplicatePage`, and `PdfDocument.CreateEmpty`. Deleting a page repairs the bookmarks, links, named destinations, open action, form fields and page labels that referred to it.
- Pages across documents: `PdfDocument.ImportPages` copies pages with their whole dependency graph (resources, fonts, images, annotations with appearances; inherited `/Resources`, `/MediaBox`, `/CropBox` and `/Rotate` written into the page), each shared object once. Bookmarks, named destinations used by links, form fields (clashing names renamed), optional content, page labels and the structure tree of tagged files come along (`PdfImportOptions`). `PdfDocument.Merge` joins documents; `PdfDocument.Split` cuts one into several (`PdfPageRange`, or a number of pages per part). See `docs/page-operations.md`.
- `docs/migrating-from-pdfsharp.md`, a guide for moving from PDFsharp.
- The `save` fuzz target also runs page operations, merge and split.

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

[1.2.0]: https://github.com/recursive-ai-oss/onepdf-csharp/releases/tag/v1.2.0
[1.1.0]: https://github.com/recursive-ai-oss/onepdf-csharp/releases/tag/v1.1.0
[1.0.1]: https://github.com/recursive-ai-oss/onepdf-csharp/releases/tag/v1.0.1
[1.0.0]: https://github.com/recursive-ai-oss/onepdf-csharp/releases/tag/v1.0.0
