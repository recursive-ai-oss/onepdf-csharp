# OnePDF

**A pure-managed, read-only .NET PDF library: open, repair, decrypt, extract text in reading order, read document structure, and
interpret pages into a drawing device or a recorded display list.** Pure C# with no dependency beyond .NET itself; untrusted files
are read through safe, bounds-checked code with size limits on by default.

[![NuGet](https://img.shields.io/nuget/v/RecursiveAI.OnePdf)](https://www.nuget.org/packages/RecursiveAI.OnePdf)
[![CI](https://github.com/recursive-ai-oss/onepdf-csharp/actions/workflows/ci.yml/badge.svg)](https://github.com/recursive-ai-oss/onepdf-csharp/actions/workflows/ci.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](#install)
[![License](https://img.shields.io/badge/license-MIT%20OR%20Apache--2.0-blue)](#license)

## Why OnePDF

- **Opens the files that are actually out there.** Cross-reference tables and streams, object streams, hybrid and incremental
  files, and a documented repair chain for damaged ones: trust the xref, then fix offsets and `/Length` by scanning, then rebuild by
  scanning for object headers. Every repair is recorded as a diagnostic with object number and offset
  ([repair rules](docs/repair.md)).
- **Encrypted files.** The standard security handler, revisions 2 to 6 (RC4 40/128, AES-128, AES-256), with user or owner
  password.
- **Text with positions and reading order.** Glyphs, words, lines and blocks; the structure tree for tagged files and geometry
  otherwise; right-to-left text through our own UAX #9; three plain-text modes; a versioned JSON form
  ([layout parameters](docs/text-layout.md), [JSON format](docs/extraction-json.md)).
- **Document structure as data.** Info and XMP metadata, outlines, page labels, links, annotations, embedded files, AcroForm field
  values, permissions.
- **A content interpreter with one clean seam.** `page.Interpret(device)` resolves everything PDF-specific (fonts, color spaces,
  functions, images, patterns, optional content, annotation appearances) and hands a 2D engine paths, glyph runs, finished
  images and shadings. `PdfDisplayList` records the calls and replays them, concurrently if you like
  ([text format](docs/display-list-format.md)).
- **Safe on files you did not write.** No `unsafe` code, no pointers, no reinterpret casts. Limits (decoded size, expansion
  ratio, nesting, recursion, object count, operators per page, image pixels) are checked before the allocation or work they guard,
  and the parsers, filters and font readers are fuzzed. NativeAOT and trimming are supported.
- **Concurrent.** Objects parse lazily into a byte-budgeted cache with single-flight parsing; any number of threads can read pages
  of one document at once.

## Install

```sh
dotnet add package RecursiveAI.OnePdf
```

The package is `RecursiveAI.OnePdf`; the namespace is `OnePdf` (`using OnePdf;`). Target framework `net10.0`. Moving from PdfPig?
See the [migration guide](docs/migrating-from-pdfpig.md).

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

## Security

**OnePDF never runs code or touches the network.** JavaScript, actions and URIs in a document are data that you can read, never
executed or fetched. External stream references are treated as empty with a diagnostic. XFA is raw XML and is never rendered.
Embedded files are read only when you ask. The only files OnePDF opens are the document you give it and, through the font provider,
local system fonts. The code is safe C# with no `unsafe`, `Unsafe.*` or `MemoryMarshal`.

## What it does not do

- **It is read-only.** No saving, editing, merging, form filling, signing or redaction in this version.
- **It does not render pixels.** OnePDF resolves a page into drawing calls; a 2D engine paints them. Rasterizing is done by a
  separate package, `RecursiveAI.OneImage.Pdf`, built on [OneImage](https://github.com/recursive-ai-oss/oneimage-csharp), which
  implements OnePDF's device and registers PDF as an image format. OnePDF has no imaging dependency.
- **No OCR.** Scanned pages without a text layer give no text.
- **Missing annotation appearance streams draw nothing** and record a diagnostic; they are not generated.
- **Public-key encryption and signatures** are not supported yet. Encrypted files need the standard handler and the BCL's AES, which
  browser WebAssembly lacks (those files fail with a typed error there).
- Permissions are reported, not enforced.

## Packages

| Package | Contents |
|---|---|
| `RecursiveAI.OnePdf` | The library (assembly and namespace `OnePdf`). `MIT OR Apache-2.0`. |
| `RecursiveAI.OnePdf.Fonts` | Substitute outlines for the standard 14 fonts when a document does not embed them: Liberation Sans, Serif and Mono; Noto Sans Math and Noto Sans Symbols for Symbol and ZapfDingbats. |

Register the substitutes once at startup; documents that do not embed Helvetica, Times, Courier, Symbol or ZapfDingbats (or their
Arial, Times New Roman and Courier New aliases) then get outlines from the package, behind any matching installed font:

```csharp
OnePdf.Fonts.FontsPackage.Register();   // before opening documents; calling it again is harmless
```

The Fonts package ships the upstream font files unmodified under the SIL Open Font License 1.1, so its license expression is
`(MIT OR Apache-2.0) AND OFL-1.1`: the code is `MIT OR Apache-2.0`, the fonts are OFL. Each font's license text and copyright
lines are in the package under `licenses/`. The fonts are never offered on their own. Core stays `MIT OR Apache-2.0`; the data
tables it embeds from Adobe and Unicode, with their notices, are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Platforms

Target framework `net10.0` on any platform .NET 10 supports. CI builds, tests and publishes a NativeAOT smoke app on win-x64,
linux-x64, linux-arm64 and osx-arm64. The library has no reflection, `dynamic` or runtime code generation, so it is trim- and
AOT-compatible (`IsAotCompatible` and `IsTrimmable`), with warnings as errors. Output is deterministic: the same input gives the same
geometry and the same text on every platform.

## Benchmarks

<!-- TODO(release): fill from the gate run on the shared workstation (bench/README.md). No number goes in this table until it was
     measured; keep the SDK, runtime, CPU and load lines accurate. -->
Compared with PdfPig on each task, wall-clock time and peak memory, each at degree of parallelism 1 and at the default degree
(parallel figures are never shown as single-thread). **Measured on a shared workstation**, local runs with BenchmarkDotNet and peak
RSS from `/usr/bin/time -v`, one run at a time; the machine's load is recorded with every figure.

| Task | OnePDF time | PdfPig time | Speedup | OnePDF peak | PdfPig peak |
|---|---|---|---|---|---|
| Open + page count, small | TBD | TBD | TBD | TBD | TBD |
| Open + page count, 1,000 pages | TBD | TBD | TBD | TBD | TBD |
| Open + page count, broken (needs repair) | TBD | TBD | TBD | TBD | TBD |
| Metadata | TBD | TBD | TBD | TBD | TBD |
| Full text, born-digital 300 pages | TBD | TBD | TBD | TBD | TBD |
| Full text, scanned with OCR layer | TBD | TBD | TBD | TBD | TBD |
| Full text, CJK | TBD | TBD | TBD | TBD | TBD |
| Words with positions | TBD | TBD | TBD | TBD | TBD |
| AES-256 encrypted, full text | TBD | TBD | TBD | TBD | TBD |
| Whole-document parallel extraction | TBD | TBD | TBD | TBD | TBD |

Text quality (character error rate on content and reading-order error, overall and per category, against PdfPig's better of its
default and layout pipelines): TBD.

SDK TBD, runtime TBD, CPU TBD, load before/during/after TBD.

## Building

```sh
dotnet build
dotnet test
dotnet publish tests/OnePdf.AotSmoke -c Release -r linux-x64 -warnaserror
```

Build output goes outside the source tree. Set `ONEPDF_ARTIFACTS` to choose where; without it, output goes to `artifacts/` at the
repository root, which is ignored by git.

## License

Licensed under either of [MIT](LICENSE-MIT) or [Apache-2.0](LICENSE-APACHE) at your option.

Copyright (c) Recursive AI, LLC. Third-party data and fonts are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
