# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

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

[1.0.1]: https://github.com/recursive-ai-oss/onepdf-csharp/releases/tag/v1.0.1
[1.0.0]: https://github.com/recursive-ai-oss/onepdf-csharp/releases/tag/v1.0.0
