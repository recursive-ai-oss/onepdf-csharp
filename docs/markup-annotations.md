# Text markup annotations

`PdfPage.AddMarkupAnnotation` writes a Highlight, StrikeOut, Underline, Squiggly or Caret annotation onto a page of a document opened with
`PdfOpenOptions.AllowEditing`, with a generated normal appearance, and returns it as a `PdfAnnotation`. `Save` writes the whole file;
`SaveIncremental` appends an update and leaves the original bytes (and any signature over them) in place.

```csharp
using var doc = PdfDocument.Open("in.pdf", new PdfOpenOptions { AllowEditing = true });
doc.Pages[0].AddMarkupAnnotation(new PdfMarkupAnnotationOptions
{
    Kind = PdfMarkupKind.Highlight,
    Quads = [PdfMarkupQuad.FromRectangle(new PdfRectangle(72, 700, 200, 714))],
    Color = [1, 1, 0],
    Contents = "Check this figure",
    Title = "Reviewer",
    Name = "review-1",
    ModifiedDate = new DateTimeOffset(2026, 11, 4, 9, 30, 0, TimeSpan.Zero),
    Popup = new PdfMarkupPopup(new PdfRectangle(300, 650, 500, 750), Open: false),
});
using var output = File.Create("out.pdf");
doc.SaveIncremental(output);
```

## What is written

| Key | Source |
|---|---|
| `/Subtype`, `/Rect` | the kind; the quadrilaterals' bounds (or `Rectangle`, required for a Caret), grown to hold the generated appearance |
| `/QuadPoints` | `Quads`, eight numbers each, in the order viewers write (upper left, upper right, lower left, lower right; ISO 32000-2 Table 181 describes counterclockwise order, but that is not what files contain). Not written for a Caret, which has none (12.5.6.11) |
| `/RD`, `/Sy` | Caret only: `RectangleDifferences`, `CaretSymbol` (`/P` when `Paragraph`) |
| `/C`, `/CA` | `Color` (1, 3 or 4 components), `Opacity`; left out when null |
| `/Contents`, `/T`, `/NM`, `/M` | `Contents`, `Title`, `Name`, `ModifiedDate`; each left out when null. Nothing comes from the clock or a random source |
| `/F` | `Flags`, default Print |
| `/P` | the page |
| `/AP /N` | a form XObject from the same generator the interpreter uses for annotations without one (`docs/appearance-generation.md`); its `/BBox` equals `/Rect`. A Highlight draws with the `Multiply` blend mode, and `/CA` becomes the group's constant opacity |
| `/Popup` | when `Popup` is set: a Popup annotation (`/Parent` back to the markup, `/Open`, its own rectangle), linked both ways, appended to the page after the markup (12.5.6.14) |

Invalid input (no quadrilaterals, quadrilaterals on a Caret, non-finite or oversized coordinates, colour outside 0..1, an empty pop-up
rectangle, a geometry the generator cannot draw) throws `ArgumentException` before anything is added. More than
`PdfLimits.MaxAppearanceItems` quadrilaterals throws `PdfLimitException`.

## Determinism

The same input and the same calls give the same bytes, for `Save` and for `SaveIncremental`:

- New objects take the next free numbers in call order: the annotation, then its appearance, then its pop-up.
- Without a `PdfSaveOptions.DocumentId`, a file that has no trailer `/ID` gets one derived from a SHA-256 of the bytes written, and an incremental
  update replaces the second `/ID` string with one derived from the previous string and the update's bytes (ISO 32000-2 14.4: the second string says
  this is a different revision). Encrypted output still varies (random IVs).

## Signed files

An incremental save keeps every signed byte. Whether `VerifySignatures` still calls an earlier signature valid depends on its
permission level (ISO 32000-2 12.8.2.2, Table 257):

- **Approval signature (no certification):** the added markup annotations and their pop-ups are an allowed change, and the earlier
  signature stays valid, as long as the update only appends them to the page's `/Annots`. The analyzer allows exactly Highlight,
  StrikeOut, Underline, Squiggly and Caret annotations, and Popups whose `/Parent` is one of those new annotations. Any other change in
  the same update (another annotation subtype, a changed existing annotation, page content) is flagged as at level 2. Table 257 attaches
  permissions to certification, so this is the library's own conservative default.
- **Certification at P=3** (`PdfCertificationLevel.FormFillingAndAnnotations`): allowed.
- **Certification at P=2 or P=1:** reported as disallowed `Annotation` changes.

A full `Save` rewrites the file and drops signatures' coverage: use `SaveIncremental` on signed documents.
