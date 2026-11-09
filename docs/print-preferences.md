# Print preferences

The catalog's `/ViewerPreferences` dictionary (ISO 32000-2 12.2, Table 147) tells a viewer how to open the file and what its print dialog
starts with. OnePDF reads and writes it; it never enforces it. Viewers treat every entry as a hint.

## Happy path

```csharp
using var doc = PdfDocument.Open("in.pdf", new PdfOpenOptions { AllowEditing = true });
doc.SetPrintPreferences(PdfPrintScaling.None, PdfDuplex.DuplexFlipLongEdge, copies: 2);
doc.Save("out.pdf");                 // or doc.SaveIncremental(stream)
```

For a new document, pass the same values through the writer:

```csharp
var options = new PdfDocumentWriterOptions
{
    ViewerPreferences = PdfViewerPreferences.ForPrint(PdfPrintScaling.None, PdfDuplex.Simplex, copies: 1),
};
```

## The typed model

`PdfViewerPreferences` has one nullable property per entry. Null means absent: it is not written, and when applied to an existing
document it leaves the existing entry alone.

| Property | Entry | Values |
|---|---|---|
| `PrintScaling` | `/PrintScaling` | `None`, `AppDefault` |
| `Duplex` | `/Duplex` | `Simplex`, `DuplexFlipShortEdge`, `DuplexFlipLongEdge` |
| `PrintPageRange` | `/PrintPageRange` | list of `PdfPrintPageRange(FirstPage, LastPage)` |
| `NumCopies` | `/NumCopies` | integer ≥ 1 |
| `PickTrayByPdfSize` | `/PickTrayByPDFSize` | bool |
| `PrintArea`, `PrintClip`, `ViewArea`, `ViewClip` | same names | `PdfViewerBox`: `MediaBox`, `CropBox`, `BleedBox`, `TrimBox`, `ArtBox` |
| `DisplayDocTitle`, `HideToolbar`, `HideMenubar`, `HideWindowUI`, `FitWindow`, `CenterWindow` | same names | bool |

- **Page ranges are one-based and inclusive**, exactly as stored: Table 147 numbers the first page 1 and stores the ranges as a flat
  array of integer pairs (`[1 1 3 3]` is page 1 and page 3). This differs from the zero-based `doc.Pages` index on purpose, so a file dump
  matches what you wrote. Ranges must be ascending and must not overlap.
- `PrintArea`, `PrintClip`, `ViewArea` and `ViewClip` are **deprecated in PDF 2.0**. They are still read and written, and most viewers
  ignore them.
- `doc.ViewerPreferences` reads the dictionary (null when the catalog has none); `.Dictionary` on the result is the raw dictionary with
  every entry, including ones not modelled here. Editing never drops entries it doesn't know (`/Direction`, `/Enforce`, extensions).
- `PdfDocumentWriterOptions.DisplayDocumentTitle` still works; an explicit `ViewerPreferences.DisplayDocTitle` wins over it. The PDF/UA
  preparer sets `DisplayDocTitle` in the same dictionary and leaves the print entries alone.

## Errors and leniency

- Writing (`SetViewerPreferences`, `SetPrintPreferences`, the writer option): `NumCopies` below 1, an empty, reversed, overlapping,
  descending or out-of-range page range (beyond the page count) or an undefined enum value throws `ArgumentException`, and nothing is
  changed. The writer reports it from `Finish`, where the page count is known.
- Reading: a value of the wrong type, an unknown name or a malformed range is left null and recorded as a warning in `doc.Diagnostics`;
  nothing is thrown (under `PdfOpenOptions.Strict` a warning is an error, as everywhere).
- Editing needs `PdfOpenOptions.AllowEditing`; otherwise the setters throw `InvalidOperationException`.
