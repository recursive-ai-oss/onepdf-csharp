# OnePDF

**A pure-managed .NET PDF library: open, repair, decrypt, extract text in reading order, read document structure,
interpret pages into a drawing device or a recorded display list, and edit and save documents.** Pure C# with no dependency beyond .NET itself; untrusted files
are read through safe, bounds-checked code with size limits on by default.

[![NuGet](https://img.shields.io/nuget/v/RecursiveAI.OnePdf)](https://www.nuget.org/packages/RecursiveAI.OnePdf)
[![NuGet: Fonts](https://img.shields.io/nuget/v/RecursiveAI.OnePdf.Fonts?label=fonts)](https://www.nuget.org/packages/RecursiveAI.OnePdf.Fonts)
[![CI](https://github.com/recursive-ai-oss/onepdf-csharp/actions/workflows/ci.yml/badge.svg)](https://github.com/recursive-ai-oss/onepdf-csharp/actions/workflows/ci.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](#install)
[![License](https://img.shields.io/badge/license-MIT%20OR%20Apache--2.0-blue)](#license)

## Why OnePDF

- **Opens the files that are actually out there.** Cross-reference tables and streams, object streams, hybrid and incremental
  files, and a documented repair chain for damaged ones: trust the xref, then fix offsets and `/Length` by scanning, then rebuild by
  scanning for object headers. Every repair is recorded as a diagnostic with object number and offset
  ([repair rules](https://github.com/recursive-ai-oss/onepdf-csharp/blob/main/docs/repair.md)).
- **Encrypted files.** The standard security handler, revisions 2 to 6 (RC4 40/128, AES-128, AES-256), with user or owner
  password.
- **Text with positions and reading order.** Glyphs, words, lines and blocks; the structure tree for tagged files and geometry
  otherwise; right-to-left text through our own UAX #9; three plain-text modes; a versioned JSON form
  ([layout parameters](https://github.com/recursive-ai-oss/onepdf-csharp/blob/main/docs/text-layout.md), [JSON format](https://github.com/recursive-ai-oss/onepdf-csharp/blob/main/docs/extraction-json.md)).
- **Document structure as data.** Info and XMP metadata, outlines, page labels, links, annotations, embedded files, AcroForm field
  values, permissions.
- **A content interpreter with one clean seam.** `page.Interpret(device)` resolves everything PDF-specific (fonts, color spaces,
  functions, images, patterns, optional content, annotation appearances) and hands a 2D engine paths, glyph runs, finished
  images and shadings. `PdfDisplayList` records the calls and replays them, concurrently if you like
  ([text format](https://github.com/recursive-ai-oss/onepdf-csharp/blob/main/docs/display-list-format.md)).
- **Safe on files you did not write.** No `unsafe` code, no pointers, no reinterpret casts. Limits (decoded size, expansion
  ratio, nesting, recursion, object count, operators per page, image pixels) are checked before the allocation or work they guard,
  and the parsers, filters and font readers are fuzzed. NativeAOT and trimming are supported.
- **Editing and saving.** Change objects and metadata, then write a clean new file (classic cross-reference table, or
  cross-reference and object streams) or an incremental update that leaves the original bytes untouched; keep, remove or set
  AES encryption
  ([writing](https://github.com/recursive-ai-oss/onepdf-csharp/blob/main/docs/writing.md)).
- **Page operations.** Delete, reorder, rotate, duplicate and insert pages; import pages from other documents, merge documents and
  split one into parts, with bookmarks, links, form fields and tagged structure carried along
  ([page operations](https://github.com/recursive-ai-oss/onepdf-csharp/blob/main/docs/page-operations.md)).
- **Forms.** Fill text, choice, check box and radio fields with validation, store their appearances, reset them, and flatten
  fields and annotations into the page
  ([forms](https://github.com/recursive-ai-oss/onepdf-csharp/blob/main/docs/forms.md)).
- **Content creation.** Draw text, paths, images and reusable forms onto new or existing pages (over or under what is there),
  with the 14 standard fonts or TrueType/OpenType fonts embedded as subsets with `ToUnicode` maps, and stamp or watermark pages
  ([content creation](https://github.com/recursive-ai-oss/onepdf-csharp/blob/main/docs/content-creation.md)).
- **Signatures.** Find signatures and document time-stamps, check byte ranges, digests, RSA (PKCS#1 and PSS) and ECDSA
  signatures, RFC 3161 time-stamps and certificate chains (to anchors you supply; nothing is fetched), and classify what changed after
  each signature in later revisions. Sign as an incremental update: approval or certification signatures (PAdES baseline B-B, B-T with a
  time-stamp from your callback, or `adbe.pkcs7.detached`), visible or invisible, with a local key or an external signer, and document
  time-stamps
  ([signatures](https://github.com/recursive-ai-oss/onepdf-csharp/blob/main/docs/signatures.md)). Changes the
  specification permits after a signature are listed, not judged: see
  [known attacks and what the result says](https://github.com/recursive-ai-oss/onepdf-csharp/blob/main/docs/signatures.md#known-attacks-and-what-the-result-says).
- **PDF/A.** Write PDF/A-2b, 2u, 3b and 3u from documents made or edited with OnePDF, and from existing files that already meet the
  requirements: the sRGB output intent (a profile generated by OnePDF), XMP, embedded fonts, annotation flags and appearances are
  taken care of, and a document that cannot be written as PDF/A is refused with every violation listed, by object. PDF/A-3 files can
  carry attachments. It is not a validator and does not convert arbitrary files
  ([PDF/A](https://github.com/recursive-ai-oss/onepdf-csharp/blob/main/docs/pdfa.md)).
- **PDF/UA-1 and PDF/A level A.** Write PDF/UA-1 (ISO 14289-1) and PDF/A-2a/3a, alone or combined, from documents OnePDF laid out or
  created with tags. `PdfSaveOptions.PdfUA` repairs the clerical parts (language, title display, tab order, link descriptions, XMP
  identification) and refuses what the document must provide (title, alternate text for figures, heading levels, table header
  cells, untagged content, unembedded fonts, ...) with every violation listed; `CheckPdfUA` only reports. It never tags untagged
  files or guesses alternate text, and a clean result is not proof of conformance
  ([PDF/UA](https://github.com/recursive-ai-oss/onepdf-csharp/blob/main/docs/pdfua.md)).
- **Concurrent.** Objects parse lazily into a byte-budgeted cache with single-flight parsing; any number of threads can read pages
  of one document at once.

## Install

```sh
dotnet add package RecursiveAI.OnePdf
dotnet add package RecursiveAI.OnePdf.Fonts   # optional: substitute fonts for the standard 14
dotnet add package RecursiveAI.OnePdf.Hyphenation   # optional: hyphenation patterns for layout
```

The package is `RecursiveAI.OnePdf`; the namespace is `OnePdf` (`using OnePdf;`). Target framework `net10.0`. Moving from PdfPig?
See the [migration guide](https://github.com/recursive-ai-oss/onepdf-csharp/blob/main/docs/migrating-from-pdfpig.md). Moving from PDFsharp? See [that guide](https://github.com/recursive-ai-oss/onepdf-csharp/blob/main/docs/migrating-from-pdfsharp.md).

## Quick start

Open a document, look at pages and metadata. `PdfDocument` is `IDisposable`: it holds the file open and reads it by position, so
dispose it (a finalizer covers forgetting, but don't rely on it). Page indexes are zero-based.

<!-- sample: Open -->
```csharp
using OnePdf;

using PdfDocument doc = PdfDocument.Open("in.pdf");
Console.WriteLine($"{doc.PageCount} pages, PDF {doc.Version}");

PdfPage page = doc.Pages[0];
Console.WriteLine($"{page.CropBox.Width} x {page.CropBox.Height} pt, rotated {page.Rotate}, label {page.Label}");

DocumentInfo info = doc.Info;                       // the Info dictionary
Console.WriteLine($"{info.Title} / {info.Author} / {info.CreationDate}");
Console.WriteLine(doc.Xmp?.Title);                  // XMP, when present

// No exception for a file that will not open:
if (PdfDocument.TryOpen("maybe.pdf", out PdfDocument? other, out PdfException? error))
    other.Dispose();
else
    Console.WriteLine(error?.Message);
```

Sources can be a path, a seekable `Stream` (left open with `LeaveOpen`), a non-seekable stream (copied; use `OpenAsync`), or a
`ReadOnlyMemory<byte>`. If the file changes underneath an open document, reads throw `PdfSourceChangedException`.

### Text

<!-- sample: Text -->
```csharp
using OnePdf;
using OnePdf.Text;

using PdfDocument doc = PdfDocument.Open("in.pdf");

// Every page, in parallel (min(4, cores) by default), headers and footers flagged by their repetition.
IReadOnlyList<PdfPageText> pages = doc.ExtractText(new PdfTextOptions { Mode = PdfTextMode.ReadingOrder });
Console.WriteLine(pages[0].Text);                              // plain text, in the chosen mode

// One page, by itself.
PdfPageText text = doc.Pages[0].ExtractText();

foreach (PdfTextWord w in text.Words)                          // words with positions (PDF points, y up)
    Console.WriteLine($"{w.Text} {w.Bounds}");

foreach (PdfTextBlock block in text.Blocks)                    // blocks > lines > words, in reading order
    foreach (PdfTextLine line in block.Lines)
        Console.WriteLine(line.Text);
```

Plain-text modes are `ReadingOrder` (default), `ContentOrder` and `Layout` (spatial, like `pdftotext -layout`). Ligatures are
decomposed, presentation forms (Kangxi radicals, Arabic and Hebrew forms) are folded to their base characters, `/ActualText` replaces its span, and invisible text (such as an OCR layer) is included and flagged `IsVisible = false`.
The Unicode chain is ToUnicode, then glyph names through the Adobe Glyph List, then the font's own cmap, then predefined CMaps,
then U+FFFD. OnePDF does no OCR.

Whole-document `doc.ExtractText()` does not keep each page's glyph list, so a long document stays small; words, lines, blocks and every
plain-text mode are still there, and `pageText.ReadGlyphs()` reads a page's glyphs on request. Single-page `page.ExtractText()` keeps
them. Set `PdfTextOptions.RetainGlyphs` to choose either way.

### Interpret a page

<!-- sample: Interpret -->
```csharp
using OnePdf;

using PdfDocument doc = PdfDocument.Open("in.pdf");

var list = new PdfDisplayList();            // an IPdfDevice that records
doc.Pages[0].Interpret(list);               // or pass your own IPdfDevice

Console.WriteLine($"{list.ItemCount} items");
File.WriteAllText("page1.txt", list.ToText());   // the versioned text form
list.Replay(new PdfDisplayList());          // replay into any device; safe from several threads
```

The device receives paths in user space with the full matrix, final colors, glyph runs with identity and Unicode, finished image
buffers with masks, and bracketed groups and soft masks. JPEG, JPEG 2000 and JBIG2 data are decoded through an
`IPdfImageDecoder` registry you fill; with none registered, the device gets the encoded bytes and a diagnostic.

### Passwords

<!-- sample: Password -->
```csharp
using OnePdf;

try
{
    using PdfDocument doc = PdfDocument.Open("secret.pdf", new PdfOpenOptions { Password = "hunter2" });
    Console.WriteLine($"{doc.Encryption?.Cipher}, {doc.AccessLevel}, {doc.Permissions}");
}
catch (PdfPasswordException)
{
    Console.WriteLine("wrong or missing password");
}
```

Either the user or the owner password opens the file. `doc.Permissions` exposes the permission flags; OnePDF does not enforce
them, and honouring them is the application's job.

### Limits, diagnostics and errors

<!-- sample: Limits -->
```csharp
using OnePdf;

var options = new PdfOpenOptions
{
    Strict = false,                                         // default: repair and record instead of throwing
    Limits = PdfLimits.Default with { MaxDecodedStreamBytes = 64L << 20, MaxObjects = 1_000_000 },
};

try
{
    using PdfDocument doc = PdfDocument.Open("untrusted.pdf", options);
    doc.Pages[0].Interpret(new PdfDisplayList(), cancellationToken: CancellationToken.None);
    foreach (PdfDiagnostic d in doc.Diagnostics)            // what was repaired or skipped
        Console.WriteLine(d);                               // severity, object number, offset, message
}
catch (PdfLimitException e) { Console.WriteLine(e.Message); }
catch (PdfFormatException e) { Console.WriteLine(e.Message); }
```

- **Lenient by default.** Unambiguous repairs and skipped operators become `PdfDiagnostic`s on `doc.Diagnostics`. A page that
  cannot be parsed throws only when you access it. `PdfOpenOptions.Strict` turns warnings into errors.
- **Exceptions** are `PdfFormatException`, `PdfPasswordException`, `PdfEncryptionException`, `PdfLimitException` and
  `PdfSourceChangedException` (all `PdfException`), plus argument exceptions, `OperationCanceledException` and I/O exceptions passed
  through.
- **Limits** are on by default (`PdfLimits.Default`: for example 256 MiB per decoded stream, an expansion ratio of 1100 after the
  first 16 MiB, nesting 256, 2^23 objects, 2^24 operators per page, 2^28 image pixels). `PdfLimits.Unrestricted` turns them off.
  Wall-clock limits are yours: pass a `CancellationToken`.

## Redaction

`doc.Redact(regions, options)` removes the glyphs, paths, image samples, annotations and form values under given areas by
rewriting the content, so extraction and a byte search of the saved file find nothing of them. Pages it cannot read completely
fail the call and leave the document unchanged. What is and is not covered is in
[redaction](https://github.com/recursive-ai-oss/onepdf-csharp/blob/main/docs/redaction.md).

```csharp
using PdfDocument doc = PdfDocument.Open("in.pdf", new PdfOpenOptions { AllowEditing = true });
doc.Redact([new PdfRedactionRegion(0, new PdfRectangle(72, 600, 300, 620))], new PdfRedactionOptions { Overlay = new PdfRedactionOverlay() });
doc.Save("out.pdf");
```

## Layout

`OnePdf.Layout` generates documents from code: nested delegates describe pages, and it lays out shaped text (Latin, Arabic, Indic and more), tables, lists, images, headers, footers and "page X of Y", then streams a tagged PDF page by page. It is pure managed, BCL-only and NativeAOT-clean like the rest, and it ships as its own package, `RecursiveAI.OnePdf.Layout`, which builds on `RecursiveAI.OneFont` for shaping.

```csharp
LayoutDocument.Compose(doc =>
{
    doc.Title("Hello");
    doc.Page(page =>
    {
        page.Size(PageSize.A4);
        page.Margin(36);
        page.Content().Text("Hello, world").FontSize(24);
    });
}).Save("hello.pdf");
```

See the [layout guide](https://github.com/recursive-ai-oss/onepdf-csharp/blob/main/docs/layout.md), and [Migrating from QuestPDF](https://github.com/recursive-ai-oss/onepdf-csharp/blob/main/docs/migrating-from-questpdf.md) if you know a library of that style.

## Security

**OnePDF never runs code or touches the network.** JavaScript, actions and URIs in a document are data that you can read, never
executed or fetched. External stream references are treated as empty with a diagnostic. XFA is raw XML and is never rendered.
Embedded files are read only when you ask. The only files OnePDF opens are the document you give it and, through the font provider,
local system fonts. The code is safe C# with no `unsafe`, `Unsafe.*` or `MemoryMarshal`.

## What it does not do

- No PDF/A validator or converter (existing files are checked and refused, never rewritten into conformance), and no long-term validation data (`/DSS`, PAdES B-LT/B-LTA) when signing. Field scripts are never run, so calculated fields are not recomputed. New text is laid out
  without a shaping engine, so complex scripts (Arabic, Indic and similar) are not shaped.
- **It does not render pixels.** OnePDF resolves a page into drawing calls; a 2D engine paints them. Rasterizing is planned as a
  separate package, `RecursiveAI.OneImage.Pdf`, built on [OneImage](https://github.com/recursive-ai-oss/oneimage-csharp); it is
  not available yet. It will implement OnePDF's device and register PDF as an image format. OnePDF has no imaging dependency.
- **No OCR.** Scanned pages without a text layer give no text.
- **Missing annotation appearances are generated** for form fields and simple markup only (see [appearance generation](https://github.com/recursive-ai-oss/onepdf-csharp/blob/main/docs/appearance-generation.md)); other annotation types without one draw nothing and record a diagnostic.
- **Public-key encryption** is not supported yet. Encrypted files need the standard handler and the BCL's AES, which
  browser WebAssembly lacks (those files fail with a typed error there).
- Permissions are reported, not enforced.

## Packages

| Package | Contents |
|---|---|
| `RecursiveAI.OnePdf` | The library (assembly and namespace `OnePdf`). `MIT OR Apache-2.0`. |
| `RecursiveAI.OnePdf.Fonts` | Substitute outlines for the standard 14 fonts when a document does not embed them: Liberation Sans, Serif and Mono; Noto Sans Math and Noto Sans Symbols for Symbol and ZapfDingbats. |
| `RecursiveAI.OnePdf.Hyphenation` | Hyphenation for layout: Liang patterns with exceptions for 26 language variants (English, German, French, Spanish, Italian, Dutch, Portuguese, Polish, Danish, Norwegian and more), loaded lazily. Opt-in: `Text(...).Hyphenator(Hyphenators.Auto())`. The pattern files keep their own permissive licences (MIT, BSD-3-Clause, Unlicense and two short copy permissions), so the license expression is `(MIT OR Apache-2.0) AND MIT AND BSD-3-Clause AND Unlicense`; the notices ship in the package. |

Register the substitutes once at startup; documents that do not embed Helvetica, Times, Courier, Symbol or ZapfDingbats (or their
Arial, Times New Roman and Courier New aliases) then get outlines from the package, behind any matching installed font:

```csharp
OnePdf.Fonts.FontsPackage.Register();   // before opening documents; calling it again is harmless
```

The Fonts package ships the upstream font files unmodified under the SIL Open Font License 1.1, so its license expression is
`(MIT OR Apache-2.0) AND OFL-1.1`: the code is `MIT OR Apache-2.0`, the fonts are OFL. Each font's license text and copyright
lines are in the package under `licenses/`. The fonts are never offered on their own. Core stays `MIT OR Apache-2.0`; the data
tables it embeds from Adobe and Unicode, with their notices, are listed in [THIRD-PARTY-NOTICES.md](https://github.com/recursive-ai-oss/onepdf-csharp/blob/main/THIRD-PARTY-NOTICES.md).

## Platforms

Target framework `net10.0` on any platform .NET 10 supports. CI builds, tests and publishes a NativeAOT smoke app on win-x64,
linux-x64, linux-arm64 and osx-arm64. The library has no reflection, `dynamic` or runtime code generation, so it is trim- and
AOT-compatible (`IsAotCompatible` and `IsTrimmable`), with warnings as errors. Output is deterministic: the same input gives the same
geometry and the same text on every platform.

## Benchmarks

OnePDF against [PdfPig](https://github.com/UglyToad/PdfPig) 0.1.16 on the same files, in the same process, for each task: wall-clock
time and peak memory, each at degree of parallelism 1 and at the default degree (min(4, cores), so 4 here). Parallel figures are
shown on their own rows and are never single-thread results. PdfPig has no parallel mode; at the default degree each worker opens its
own copy of the file and extracts a run of pages. Open and metadata work is single-threaded in both libraries. Speedup is PdfPig time
divided by OnePDF time.

| Task | Degree | OnePDF time | PdfPig time | Speedup | OnePDF peak | PdfPig peak |
|---|---|---|---|---|---|---|
| Open + page count, small (2 pages) | — | 17.4 µs | 27.7 µs | 1.59× | 36 MiB | 79 MiB |
| Open + page count, 1,000 pages | — | 2.55 ms | 4.15 ms | 1.63× | 38 MiB | 87 MiB |
| Open + page count, broken (needs repair) | — | 4.41 ms | 17.5 ms | 3.96× | 44 MiB | 87 MiB |
| Open + page count, AES-256 | — | 1.31 ms | 6.77 ms | 5.15× | 48 MiB | 103 MiB |
| Metadata, small file | — | 20.5 µs | 22.6 µs | 1.10× | 42 MiB | 79 MiB |
| Metadata, 300-page article | — | 0.48 ms | 4.06 ms | 8.50× | 40 MiB | 88 MiB |
| Full text, born-digital 300 pages | 1 | 211 ms | 238 ms | 1.13× | 104 MiB | 124 MiB |
| Full text, born-digital 300 pages (whole-document parallel) | default | 68.9 ms | 123 ms | 1.79× | 112 MiB | 135 MiB |
| Full text, 40-page scan with OCR layer | 1 | 18.4 ms | 34.7 ms | 1.89× | 56 MiB | 165 MiB |
| Full text, 40-page scan with OCR layer | default | 5.11 ms | 23.3 ms | 4.57× | 67 MiB | 230 MiB |
| Full text, CJK (120 pages) | 1 | 57.5 ms | 229 ms | 3.99× | 66 MiB | 120 MiB |
| Full text, CJK (120 pages) | default | 28.3 ms | 98.8 ms | 3.49× | 96 MiB | 138 MiB |
| Words with positions, 300 pages | 1 | 214 ms | 327 ms | 1.53× | 117 MiB | 124 MiB |
| Words with positions, 300 pages | default | 96.4 ms | 149 ms | 1.55× | 118 MiB | 139 MiB |
| Words with positions, one dense page | 1 | 4.64 ms | 13.8 ms | 2.97× | 67 MiB | 101 MiB |
| AES-256, full text (300 pages) | 1 | 217 ms | 249 ms | 1.15× | 112 MiB | 135 MiB |
| AES-256, full text (300 pages) | default | 70.0 ms | 132 ms | 1.89× | 115 MiB | 143 MiB |

OnePDF is faster on every row. The smallest margins are single-thread born-digital text (1.13×) and metadata of a small file
(1.10×, about 2 µs). Allocation is lower throughout: for example 62 MB against 562 MB for the 300-page text row at degree 1.

Information rows (not part of the comparison above):

| Row | Degree | Time | Peak |
|---|---|---|---|
| OnePDF full text, 300 pages, glyph lists kept (`RetainGlyphs = true`) | 1 | 282 ms | 239 MiB |
| OnePDF full text, 300 pages, glyph lists kept (`RetainGlyphs = true`) | default | 126 ms | 258 MiB |
| OnePDF interpret one text-heavy page into a display list | 1 | 2.07 ms | 55 MiB |
| OnePDF interpret one vector-heavy page (about 60,000 segments) | 1 | 5.51 ms | 45 MiB |
| PDFium (Docnet.Core 2.6.0) full text, born-digital 300 pages | 1 | 626 ms | 43 MiB |
| PDFium full text, 40-page scan with OCR layer | 1 | 31.9 ms | 52 MiB |
| PDFium full text, CJK (120 pages) | 1 | 410 ms | 42 MiB |
| mutool 1.23.10 (`mutool draw -F txt`, process start included) full text, born-digital 300 pages | 1 | 261 ms | 45 MiB |
| mutool full text, 40-page scan with OCR layer | 1 | 43.2 ms | 43 MiB |
| mutool full text, CJK (120 pages) | 1 | 188 ms | 44 MiB |

Whole-document extraction drops each page's glyph list by default to keep long documents small; keeping them costs time and
about 2.3× the peak memory on the 300-page file (single-page extraction keeps them).

**PDFium and mutool use less peak memory than OnePDF.** On born-digital text, PDFium peaks at 43 MiB and mutool at 45 MiB against
OnePDF's 104 MiB at degree 1 (112 MiB at the default degree); on the scan and CJK files the gap is smaller (PDFium 52 and 42 MiB,
mutool 43 and 44 MiB, OnePDF 56 and 66 MiB). The PDFium figures go through Docnet.Core at one thread; the mutool figures include
starting its process, and its peak includes the child process.

**Method.** Measured on a dedicated workstation (AMD Ryzen 9 7950X3D, 16 cores / 32 threads, Linux Mint 22.3 x64, .NET SDK
10.0.402, runtime .NET 10.0.13); other jobs were held off during the run. Times are BenchmarkDotNet 0.15.8 means with the default job and
`[MemoryDiagnoser]`. Peak memory is the process's maximum resident set (`/usr/bin/time -v`), one cold process per row, and includes
the .NET runtime itself, so the differences matter more than the absolute figures. Load average
(1 minute) was 1.18 at the start, 1.40 when BenchmarkDotNet finished and 1.53 at the end, and stayed between 1.06 and 2.83 in the
30-second trace during the run. Inputs and the benchmark project are in [bench/](https://github.com/recursive-ai-oss/onepdf-csharp/blob/main/bench/README.md); the files are generated by
`tools/corpus/make_bench_inputs.py`. The mutool rows come from a second run on the same machine and commit (load 1.25 at the start, at most 1.31). The two interpretation rows were re-measured for 1.9.0 on the same machine (load 2.65 at the start and 4.70 at the end of that part, the benchmark itself). Results are for these files on this machine; yours will differ.

### Layout

`OnePdf.Layout` against [MigraDoc](https://github.com/empira/MigraDoc) (PDFsharp-MigraDoc 6.2.4) producing the same documents,
one document per operation unless noted. Time and allocation are BenchmarkDotNet means; peak is the process's maximum resident set,
one fresh process per row, median of three.

| Row | OnePDF time | MigraDoc time | OnePDF allocated | MigraDoc allocated | OnePDF peak | MigraDoc peak |
|---|---|---|---|---|---|---|
| Invoice, 1 page | 1.01 ms | 1.30 ms | 0.96 MB | 1.97 MB | 49 MiB | 50 MiB |
| Report, 50 pages (tables, images, headers, page X of Y) | 37.1 ms | 175 ms | 30.8 MB | 188 MB | 87 MiB | 127 MiB |
| Table, 10,000 rows (about 250 pages, repeated header) | 362 ms | 1,191 ms | 357 MB | 574 MB | 146 MiB | 291 MiB |
| Book, 500 pages, justified | 317 ms | 496 ms | 216 MB | 1,099 MB | 109 MiB | 177 MiB |
| 32 invoices concurrently | 9.99 ms | 51.7 ms | 30.8 MB | 74.9 MB | 99 MiB | 111 MiB |

Information rows (not part of the comparison above):

| Row | OnePDF | MigraDoc |
|---|---|---|
| First invoice in a fresh process, JIT included | 103 ms, 49 MiB | 108 ms, 50 MiB |
| First invoice in a fresh NativeAOT process | 4 ms, 12 MiB | — (not NativeAOT-compatible) |
| Mixed scripts (Latin, Arabic, Devanagari), 20 pages | 129 ms, 66 MiB | 35.7 ms, 100 MiB |
| Book, 500 pages, with Knuth–Plass line breaking (`LineBreakMode.KnuthPlass`) | 333 ms, 262 MB allocated | — |
| Output size: invoice / report / table / book | 26 KB / 300 KB / 2.0 MB / 1.6 MB | 39 KB / 268 KB / 1.2 MB / 1.7 MB |

The first document in a fresh process, JIT included, takes 103 ms against MigraDoc's 108 ms; published as NativeAOT, OnePDF's
first document takes 4 ms. **MigraDoc does not shape these scripts:** in the mixed-scripts
row it draws Arabic and Devanagari characters unshaped, so it measures layout of the same characters, not the same
glyphs. OnePDF's documents are tagged by default; the table's 2.0 MB is mostly its structure tree (about 90,000 elements, one per cell
and paragraph), which MigraDoc does not write.

[iText](https://itextpdf.com/) 9.8.0 (AGPL-3.0 or commercial) ran as an information row: 5.19 ms for the invoice, 154 ms for the report,
2,771 ms for the table, 765 ms for the book. Method, inputs and the licence notes are in
[bench/OnePdf.Layout.Benchmarks](https://github.com/recursive-ai-oss/onepdf-csharp/blob/main/bench/OnePdf.Layout.Benchmarks/README.md).
Same machine as above (Ryzen 9 7950X3D, 32 threads, .NET SDK 10.0.402, runtime 10.0.13), other jobs held off: load average 0.02
at the start, at most 2.65 during the run (the benchmark itself) and at the end.

### Text extraction quality

Character error rate on content (order-free) and reading-order error, in percent, lower is better. PdfPig is shown as its default
`Page.Text` and as its documented layout pipeline (Docstrum blocks plus unsupervised reading order); on the real-world set the
PdfPig figure is the better of the two for each metric and category, best of 8 runs.

| Generated set (62 files, exact truth) | Content error | Reading-order error |
|---|---|---|
| **OnePDF** | **0.3** | **1.2** |
| PdfPig, default | 21.9 | 22.7 |
| PdfPig, layout pipeline | 5.6 | 10.7 |

| Real-world set (92 files, 11 categories) | Content error | Reading-order error |
|---|---|---|
| **OnePDF** | **5.6** | **15.9** |
| PdfPig, best of default and layout | 13.5 | 36.5 |

How it is measured, and the per-category tables: [tools/OnePdf.Quality](https://github.com/recursive-ai-oss/onepdf-csharp/blob/main/tools/OnePdf.Quality/README.md) and
[testdata/realworld](https://github.com/recursive-ai-oss/onepdf-csharp/blob/main/testdata/realworld/README.md). Reference text is the PDF's text layer in logical order, so an extractor that
emits right-to-left text in visual order scores worse by design.

### Robustness

Over the test data and three public corpora, GovDocs1 (5,767 files), a 5,000-file sample of SafeDocs CC-MAIN-2021-31 and the veraPDF
corpus (2,906 files; fetched by script, not committed), PdfPig opened no file that OnePDF failed to open, and OnePDF opened 20 files PdfPig did not. A sweep that
opens each file, extracts all text and interprets every page, with default options, found 0 crashes and 0 hangs (slowest page 0.40 s,
highest peak memory 889 MiB). The 13 fuzz targets (parser, document and repair, typed model, interpreter, filters, CCITT, decryption, PDF fonts, font programs,
CMaps, shadings, Type 4 functions and text extraction) each ran 600 s under libFuzzer with no findings.

## Building

```sh
dotnet build
dotnet test
dotnet publish tests/OnePdf.AotSmoke -c Release -r linux-x64 -warnaserror
```

Build output goes outside the source tree. Set `ONEPDF_ARTIFACTS` to choose where; without it, output goes to `artifacts/` at the
repository root, which is ignored by git.

## License

Licensed under either of [MIT](https://github.com/recursive-ai-oss/onepdf-csharp/blob/main/LICENSE-MIT) or [Apache-2.0](https://github.com/recursive-ai-oss/onepdf-csharp/blob/main/LICENSE-APACHE) at your option.

Copyright (c) Recursive AI, LLC. Third-party data and fonts are listed in [THIRD-PARTY-NOTICES.md](https://github.com/recursive-ai-oss/onepdf-csharp/blob/main/THIRD-PARTY-NOTICES.md).
