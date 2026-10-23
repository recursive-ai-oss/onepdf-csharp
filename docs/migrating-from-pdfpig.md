# Migrating from PdfPig

This maps the PdfPig calls most programs use to their OnePDF equivalents. It is based on PdfPig's public documentation and README;
check its docs for details of its behavior. OnePDF's API is smaller in some places and larger in others, so this is a map, not a
drop-in replacement.

## The differences that matter first

| | PdfPig | OnePDF |
|---|---|---|
| Namespace | `UglyToad.PdfPig` | `OnePdf` (text types in `OnePdf.Text`) |
| Page numbers | `GetPage(1)` is the first page | **Zero-based**: `doc.Pages[0]`. Printed page labels are separate: `page.Label`. |
| Coordinates | PDF points, origin bottom-left | PDF points, origin bottom-left, in an upright page space (rotation applied) |
| Errors | Mostly lenient, some exceptions | Lenient by default, repairs recorded in `doc.Diagnostics`; `PdfOpenOptions.Strict` turns warnings into errors |
| Untrusted input | You set your own guards | `PdfLimits` on by default, checked before allocation |
| Lifetime | `PdfDocument` is `IDisposable` | `PdfDocument` is `IDisposable`; it holds the file open and reads by position |
| Threads | Check PdfPig's docs before sharing a document across threads | Any number of threads can read pages of one document |

## Opening

| PdfPig | OnePDF |
|---|---|
| `PdfDocument.Open("a.pdf")` | `PdfDocument.Open("a.pdf")` |
| `PdfDocument.Open(stream)` / `Open(bytes)` | `PdfDocument.Open(stream)` / `Open(ReadOnlyMemory<byte>)`; non-seekable streams: `await PdfDocument.OpenAsync(stream)` |
| `PdfDocument.Open("a.pdf", new ParsingOptions { Password = "pw" })` | `PdfDocument.Open("a.pdf", new PdfOpenOptions { Password = "pw" })` |
| `ParsingOptions.UseLenientParsing` | Lenient is the default; `new PdfOpenOptions { Strict = true }` for the opposite |
| `ParsingOptions.SkipMissingFonts` | Not needed: a missing font is substituted and recorded in `Diagnostics` |
| `document.NumberOfPages` | `doc.PageCount` |
| `document.Version` | `doc.Version` |
| `document.IsEncrypted` | `doc.IsEncrypted` |
| (try/catch around `Open`) | `PdfDocument.TryOpen(path, out var doc, out var error)` |

```csharp
// PdfPig
using var document = PdfDocument.Open("a.pdf", new ParsingOptions { Password = "secret" });

// OnePDF
using var doc = OnePdf.PdfDocument.Open("a.pdf", new OnePdf.PdfOpenOptions { Password = "secret" });
```

A wrong or missing password throws `PdfPasswordException`. The user or the owner password opens the file. `doc.Permissions` reports
the permission flags; they are not enforced.

## Pages

| PdfPig | OnePDF |
|---|---|
| `document.GetPages()` | `doc.Pages` (an `IReadOnlyList<PdfPage>`; `foreach` works) |
| `document.GetPage(n)` (one-based) | `doc.Pages[n - 1]` |
| `page.Number` | `page.Index` (zero-based) |
| `page.Width`, `page.Height` | `page.CropBox.Width`, `page.CropBox.Height` (other boxes: `MediaBox`, `TrimBox`, `BleedBox`, `ArtBox`); `page.Rotate` (degrees), `page.UserUnit` |
| `page.GetAnnotations()` | `page.Annotations` (links: `page.Links`) |
| `page.GetImages()` | Images arrive through the device: interpret the page and handle `DrawImage`, or read the XObjects from `page.Resources` |
| `document.GetBookmarks()` | `doc.Outline` |
| Embedded files, form fields | `doc.EmbeddedFiles`, `doc.Form` |

## Metadata

| PdfPig | OnePDF |
|---|---|
| `document.Information.Title` / `Author` / `Subject` / `Keywords` / `Creator` / `Producer` | `doc.Info.Title` / `Author` / `Subject` / `Keywords` / `Creator` / `Producer` |
| `document.Information.CreationDate` / `ModifiedDate` (strings) | `doc.Info.CreationDate` / `ModificationDate` (`DateTimeOffset?`) |
| `document.GetXmpMetadata()` | `doc.Xmp` (parsed fields plus `RawBytes`) |
| Other Info keys | `doc.Info.Entries`, or `doc.Info.Dictionary` for the raw COS dictionary |

## Text

PdfPig's `Page.Text` is the content-stream order of letters. OnePDF's default is reading order (structure tree for tagged files,
geometry otherwise). Use `PdfTextMode.ContentOrder` for the nearest equivalent of `Page.Text`.

| PdfPig | OnePDF |
|---|---|
| `page.Text` | `pageText.GetText(PdfTextMode.ContentOrder)` |
| `ContentOrderTextExtractor.GetText(page)` | `pageText.GetText(PdfTextMode.ContentOrder)` |
| `page.GetWords()` | `pageText.Words` (each `PdfTextWord` has `Text` and `Bounds`) |
| `word.Text`, `word.BoundingBox` | `word.Text`, `word.Bounds` (`PdfRectangle`: `Left`, `Bottom`, `Right`, `Top`) |
| `page.Letters` | `pageText.Glyphs` (`PdfTextGlyph`) from `page.ExtractText()`; whole-document `doc.ExtractText()` drops glyphs to bound memory, so pass `RetainGlyphs = true` or call `pageText.ReadGlyphs()` |
| `letter.Value` | `glyph.Text` |
| `letter.Location` / `StartBaseLine` | `glyph.Origin` / `glyph.Matrix` |
| `letter.GlyphRectangle` | `glyph.Bounds` (axis-aligned) or `glyph.Quad` (rotated) |
| `letter.PointSize` | `glyph.Size` |
| `letter.FontName` | `glyph.FontName` |
| `letter.Color` | `glyph.FillColor` |
| Docstrum / recursive X-Y cut / reading-order analyzers (`DocstrumBoundingBoxes`, `UnsupervisedReadingOrderDetector`) | Built in: `pageText.Blocks` > `Lines` > `Words`, already in reading order; `PdfTextMode.Layout` for a `pdftotext -layout` style string |
| Hidden / OCR text | Included and flagged: `glyph.IsVisible`, `word.IsVisible` |

```csharp
// PdfPig
using var document = PdfDocument.Open("a.pdf");
foreach (Page page in document.GetPages())
{
    Console.WriteLine(page.Text);
    foreach (Word word in page.GetWords())
        Console.WriteLine($"{word.Text} {word.BoundingBox}");
}

// OnePDF
using var doc = OnePdf.PdfDocument.Open("a.pdf");
foreach (OnePdf.Text.PdfPageText page in doc.ExtractText())    // pages in parallel
{
    Console.WriteLine(page.Text);
    foreach (OnePdf.Text.PdfTextWord word in page.Words)
        Console.WriteLine($"{word.Text} {word.Bounds}");
}
```

`doc.ExtractText()` does every page in parallel and also flags headers and footers by their repetition across pages; the
degree is `PdfTextOptions.MaxDegreeOfParallelism` (default min(4, cores)). To keep a long document within memory it does not keep each page's glyph list (`PdfTextOptions.RetainGlyphs`); every plain-text mode, words, lines and blocks are still there, and `pageText.ReadGlyphs()` reads the glyphs of a page on request.

Things that behave differently and can change your output:

- Ligatures are decomposed by default (`DecomposeLigatures`); `/ActualText` replaces the text it covers.
- Line-end hyphens are kept unless you set `JoinHyphenatedWords`.
- Text that is clipped out or off the page is excluded unless you set `IncludeClippedAndOffPage`.
- Duplicate fake-bold text is collapsed (`CollapseDuplicates`).
- Right-to-left text comes out in logical order (UAX #9), not in the visual order of the content stream.
- Words with no Unicode mapping come out as U+FFFD; OnePDF never guesses by OCR.

## Beyond text

PdfPig has no real page rendering. In OnePDF, `page.Interpret(device)` drives an `IPdfDevice` with paths, glyph runs, images,
shadings and groups; `PdfDisplayList` is the recording device. Producing pixels is planned as a separate
package, `RecursiveAI.OneImage.Pdf`, built on OneImage; it is not available yet.

## What OnePDF does not have

PdfPig can build documents from nothing (`PdfDocumentBuilder`) and OnePDF cannot yet. OnePDF can change and save existing
documents, including incremental updates and encryption ([writing](https://github.com/recursive-ai-oss/onepdf-csharp/blob/main/docs/writing.md)). If you create new PDFs, keep PdfPig (or another writer) for that part.
