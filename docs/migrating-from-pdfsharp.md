# Migrating from PDFsharp

This maps the PDFsharp calls most programs use to their OnePDF equivalents for opening, reading, changing, saving and page
operations. It is based on PDFsharp's public documentation (docs.pdfsharp.net, version 6.x) and its published API reference; check
those for details of its behavior. OnePDF's API is smaller in some places and larger in others, so this is a map, not a drop-in
replacement.

## What OnePDF does not do (yet)

PDFsharp is first of all a library for *creating* PDF files: you draw with `XGraphics`, use `XFont`, `XImage` and MigraDoc. OnePDF
reads, extracts, interprets into a device and edits the page structure of existing files; **it has no drawing API for making new
content** (adding text, images or shapes to a page is on the roadmap, not in this version). If your program mostly draws, keep
PDFsharp for that and use OnePDF for the parts below, or wait for content creation.

## The differences that matter first

| | PDFsharp | OnePDF |
|---|---|---|
| Namespaces | `PdfSharp.Pdf`, `PdfSharp.Pdf.IO` | `OnePdf` (text types in `OnePdf.Text`) |
| Page numbers | `Pages[0]` is the first page | Zero-based too: `doc.Pages[0]`. Printed page labels are separate: `page.Label`. |
| Reading vs changing | `PdfReader.Open(path, PdfDocumentOpenMode.…)` decides: `Modify`, `Import`, `ReadOnly`, `InformationOnly` | One mode of reading. Changing is opt-in: `new PdfOpenOptions { AllowEditing = true }`. Pages can be imported from any open document, read-only or not. |
| New document | `new PdfDocument()` | `PdfDocument.CreateEmpty()` |
| Untrusted input | You set your own guards | `PdfLimits` on by default, checked before allocation |
| Damaged files | Throws for what it cannot read | Repairs what it can (record in `doc.Diagnostics`); `PdfOpenOptions.Strict` turns warnings into errors |
| Page size of a new page | `AddPage()` gives a default size (`page.Size`, `page.Width`) | You give the size: `InsertBlankPage(index, width, height)` (points) |
| Threads | Check PDFsharp's docs before sharing a document across threads | Any number of threads can read one document; changes are for one thread at a time |
| Lifetime | `PdfDocument` is not disposable | `PdfDocument` is `IDisposable`; it holds the file open and reads by position. Merge and split results live in memory. |

## Opening

| PDFsharp | OnePDF |
|---|---|
| `PdfReader.Open("a.pdf", PdfDocumentOpenMode.Import)` | `PdfDocument.Open("a.pdf")` (the document is only read; import from it with `ImportPages`) |
| `PdfReader.Open("a.pdf", PdfDocumentOpenMode.Modify)` | `PdfDocument.Open("a.pdf", new PdfOpenOptions { AllowEditing = true })` |
| `PdfReader.Open("a.pdf", PdfDocumentOpenMode.ReadOnly)` / `InformationOnly` | `PdfDocument.Open("a.pdf")`; pages and objects are read when first used, so opening for a page count or metadata is cheap |
| `PdfReader.Open(stream, mode)` | `PdfDocument.Open(stream)` / `Open(ReadOnlyMemory<byte>)`; non-seekable streams: `await PdfDocument.OpenAsync(stream)` |
| `PdfReader.Open("a.pdf", "password")` | `PdfDocument.Open("a.pdf", new PdfOpenOptions { Password = "password" })` |
| `document.PageCount` | `doc.PageCount` |
| `document.Version` | `doc.Version` (a string such as `1.7`; a save raises it when the content needs it) |
| `document.SecuritySettings` (permission flags of an opened file) | `doc.IsEncrypted`, `doc.Permissions` |

A wrong or missing password throws `PdfPasswordException`. PDFsharp's documentation notes that a file with an owner password cannot
be opened for modification with the user password (open it in `Import` mode instead and copy the pages). OnePDF has no such split:
either password opens the file, and `doc.Permissions` reports the flags without enforcing them. Honouring them is up to your
application.

```csharp
// PDFsharp: pages of an owner-protected file, opened with the user password
var import = PdfReader.Open(path, userPassword, PdfDocumentOpenMode.Import);
var output = new PdfDocument();
foreach (PdfPage page in import.Pages)
    output.AddPage(page);
output.Save("copy.pdf");

// OnePDF
using var import = PdfDocument.Open(path, new PdfOpenOptions { Password = userPassword });
using var output = PdfDocument.CreateEmpty();
output.ImportPages(import);
output.Save("copy.pdf");
```

## Pages

| PDFsharp | OnePDF |
|---|---|
| `document.Pages` / `document.PageCount` | `doc.Pages` / `doc.PageCount` |
| `document.AddPage()` | `doc.InsertBlankPage(doc.PageCount, width, height)` |
| `document.InsertPage(index)` | `doc.InsertBlankPage(index, width, height)` |
| `document.AddPage(importedPage)` | `doc.ImportPages(source, [pageIndex])` (indices in `source`) |
| `document.Pages.Insert(index, page)` | `doc.ImportPages(source, [pageIndex], insertAt: index)` |
| `document.Pages.RemoveAt(index)` | `doc.RemovePage(index)` / `doc.RemovePages(indexes)` |
| Moving a page (remove it and insert it again) | `doc.MovePage(from, to)`; a whole new order: `doc.ReorderPages(newOrder)` |
| `page.Rotate = 90` (absolute, a multiple of 90) | `doc.SetPageRotation(index, 90)`; relative: `doc.RotatePage(index, 90)` |
| `page.Width`, `page.Height`, `page.MediaBox`, `page.CropBox` | `page.CropBox.Width` / `Height` (the visible area; `MediaBox`, `TrimBox`, `BleedBox`, `ArtBox` also), `page.Rotate`, `page.UserUnit` |
| Duplicating a page (import the same page again) | `doc.DuplicatePage(index)` |
| `page.Annotations` | `page.Annotations`, `page.Links` |

OnePDF's page objects are read views; operations are methods of the document, and `doc.Pages` is rebuilt after each one (take pages
again after calling an operation). Details of what each operation does to bookmarks, links, forms and the rest are in
[page-operations.md](page-operations.md).

### Merge

```csharp
// PDFsharp
var output = new PdfDocument();
foreach (string file in files)
{
    var input = PdfReader.Open(file, PdfDocumentOpenMode.Import);
    foreach (PdfPage page in input.Pages)
        output.AddPage(page);
}
output.Save("merged.pdf");

// OnePDF
var inputs = files.Select(f => PdfDocument.Open(f)).ToList();
using (PdfDocument merged = PdfDocument.Merge(inputs))
{
    merged.SetInfo("Title", "Merged");
    merged.Save("merged.pdf");
}
foreach (var d in inputs) d.Dispose();
```

`Merge` carries more than the pages: bookmarks, named destinations the links use, form fields (with clashing names renamed),
optional content, page labels and the structure tree of tagged files. PDFsharp's documentation says it does not write object
streams, so files that contain them can grow when saved; OnePDF reads and writes them (`PdfSaveOptions.UseObjectStreams`).

### Split

```csharp
// PDFsharp: one file per page
for (int i = 0; i < input.PageCount; i++)
{
    var output = new PdfDocument();
    output.AddPage(input.Pages[i]);
    output.Save($"page-{i + 1}.pdf");
}

// OnePDF
IReadOnlyList<PdfDocument> parts = PdfDocument.Split(input, pagesPerPart: 1);
for (int i = 0; i < parts.Count; i++)
{
    parts[i].Save($"page-{i + 1}.pdf");
    parts[i].Dispose();
}
// or by ranges: PdfDocument.Split(input, [new PdfPageRange(0, 3), new PdfPageRange(3, 5)])
```

Each part holds the objects its pages reach. A resource dictionary shared by every page of the source (typical of slide decks) is
copied whole into each part.

## Metadata

| PDFsharp | OnePDF |
|---|---|
| `document.Info.Title` (read) | `doc.Info.Title`, `Author`, `Subject`, `Keywords`, `Creator`, `Producer`, `CreationDate`, `ModificationDate` (dates are `DateTimeOffset?`) |
| `document.Info.Title = "x"` | `doc.SetInfo("Title", "x")` (editable document; null removes the entry) |
| XMP metadata | `doc.Xmp` (parsed fields plus raw bytes); `doc.SetXmp(bytes)` |
| Outlines (`document.Outlines`) | `doc.Outline` (read; kept in step by page operations) |

## Saving

| PDFsharp | OnePDF |
|---|---|
| `document.Save("out.pdf")` / `Save(stream)` | `doc.Save("out.pdf")` / `doc.Save(stream)`: a complete new file |
| (no incremental update) | `doc.SaveIncremental(stream)`: the original bytes plus an update |
| `document.Options.FlateEncodeMode`, compression options | `PdfSaveOptions.CompressStreams`, `XrefFormat`, `UseObjectStreams` |
| `document.SecuritySettings.UserPassword` / `OwnerPassword` | `PdfSaveOptions { Encryption = PdfEncryptionAction.Set, NewEncryption = new PdfEncryptionOptions { UserPassword = …, OwnerPassword = … } }` |
| `SecurityHandler.SetEncryptionToV4UsingAES()` (the default) | `Cipher = PdfCipher.Aes128` |
| `SecurityHandler.SetEncryptionToV5()` | `Cipher = PdfCipher.Aes256` (the default of `PdfEncryptionOptions`) |
| `SecurityHandler.SetEncryptionToNoneAndResetPasswords()` | `Encryption = PdfEncryptionAction.Remove` |
| `SecuritySettings.PermitPrint`, `PermitExtractContent`, … | `PdfEncryptionOptions.Permissions` (`PdfPermissions.Print`, `CopyContent`, `ModifyContents`, `ModifyAnnotations`, `FillForms`, `AssembleDocument`, `PrintHighQuality`, …) |
| RC4 for new files | Not offered for new files; an existing RC4 file saved with `Keep` stays RC4 |

An encrypted source never needs special handling for page operations: it is read with its password, and what you write is plain
unless you ask for encryption.

## Forms

PDFsharp's documentation describes limited AcroForm support. OnePDF reads field values and kinds (`doc.Form`, `FindField`), merges
and renames fields when pages are imported, and draws fields with missing appearances (`PdfAnnotation.GenerateAppearance`). It does
not yet fill fields.

## Beyond what PDFsharp does

- **Text extraction** (PDFsharp's FAQ says it has no high-level text extraction): `doc.ExtractText()`; see the
  [PdfPig guide](migrating-from-pdfpig.md) for the API.
- **Interpreting pages** into a drawing device or a recorded display list (`page.Interpret`), which a renderer can replay.
- **Repair** of broken files and **limits** for hostile ones.

## Things that behave differently and can change your output

- Imported pages are *copied*, not shared: a page imported twice becomes two pages with their own annotations, and nothing refers
  back to the source document.
- Inherited page attributes (`/Resources`, `/MediaBox`, `/CropBox`, `/Rotate`) are written into the imported page.
- `ImportPages` copies pages from a document you have open; there is no separate "import mode" document type, and the source is
  never changed.
- Deleting a page repairs bookmarks, links and destinations instead of leaving them dangling, and drops the structure tree of a
  tagged file (it would keep the deleted content reachable).
