# Page geometry: boxes, rotation and user unit

Every page has a media box and optional crop, bleed, trim and art boxes, a clockwise `/Rotate` and a `/UserUnit` (ISO 32000-2 7.7.3.3, Table 31; 14.11.2). OnePDF reads and writes all of them.

## Reading

- `page.MediaBox`, `CropBox`, `BleedBox`, `TrimBox`, `ArtBox` are the *effective* rectangles. The media box and crop box and `Rotate` are inherited from the page tree. The crop box defaults to the media box; the other three default to the crop box. Every box is clipped to the media box (a box that misses it reads as its default). A malformed entry reads as its default with a diagnostic.
- `page.GetBox(PdfPageBox)` is the same by name, and `page.HasExplicitBox(PdfPageBox)` tells a stored box from a default.
- `page.GetDisplaySize(box, applyRotate, applyUserUnit)` returns a `PdfPageSize`: points after `/Rotate` (width and height swap at 90 and 270) and `/UserUnit`, with `WidthInches`, `HeightInches`, `WidthMillimeters` and `HeightMillimeters`. The interpreter's `PdfInterpretOptions.Box` uses the same box choice.
- `page.GetBoxColorInfo(box)` returns the page's `/BoxColorInfo` entry for the crop, bleed, trim or art box (colour, line width, solid or dashed with dash pattern; Table 394 defaults), or null.

## Editing existing pages (`AllowEditing`)

```csharp
using var doc = PdfDocument.Open(path, new PdfOpenOptions { AllowEditing = true });
PdfPage page = doc.Pages[0];
page.SetTrimBox(new PdfRectangle(36, 36, 264, 264));
page.ClearBleedBox();
page.SetRotate(90);
page.SetUserUnit(2);
page.SetBoxColorInfo(PdfPageBox.TrimBox, new PdfBoxColorInfo { Blue = 1 });

doc.SetPrintGeometry(bleed: 9);                        // every page: keep trim, media = bleed = trim + 9 pt
page.SetPrintGeometry(new PdfRectangle(36, 36, 264, 264), 9); // one page, explicit trim
doc.Save(output);                                      // or SaveIncremental
```

- Every setter writes on the page itself. An inherited media box, crop box or rotation is overridden; the page-tree node is not changed.
- Rectangles are normalized when written (any two opposite corners are accepted). Non-finite or empty rectangles, a `Rotate` that is not a multiple of 90 (negative values and values over 360 are normalized), and a `UserUnit` that is not finite and positive throw `ArgumentException`; nothing is changed.
- `Clear*` returns a box to its default. For the crop box and `Rotate`, which can be inherited, an inherited value is overridden on the page with the default (the media box, 0).
- **Shrinking the media box** clips the page's own crop, bleed, trim and art boxes to it and removes one that no longer overlaps it, so what is stored matches what a reader reports.
- **`SetPrintGeometry`** sets the trim box; the media and bleed boxes become the trim box grown by the bleed; the crop box is cleared. Content keeps its coordinates.
- **Slug.** `SetPrintGeometry(trim, bleed, slug)` (and `PdfDocument.SetPrintGeometry(bleed, slug)` for every page) adds a slug, the room outside the bleed for crop marks, colour bars and job information: the trim box is the trim box, the bleed box is the trim box grown by the bleed, and the media box is the bleed box grown by the slug. A zero slug is the same as the two-argument form. **The crop box is cleared, not set to the bleed box.** ISO 32000-2 14.11.2.2 makes the crop box the region a viewer displays and prints, so a crop box equal to the bleed box would hide the marks from every reader that honours it, while the media box (14.11.2.1, the full physical medium) is what the printer receives. With no crop box the whole media box shows. If you want viewers to show only the trimmed or bled page, call `SetCropBox` afterwards. OnePDF does not draw marks: draw them in the slug yourself, outside the bleed box, so they never touch the bleed or the trim.
- `SetUserUnit` raises the document version to 1.6, where `/UserUnit` was introduced.
- The page objects update in place: a page you hold reports the new values straight away, and `Save`/`SaveIncremental` write them. Reopening the saved file gives the same values.
- `PdfDocument.RotatePage` adds to the rotation; `PdfDocument.SetPageRotation` sets it by index. `page.SetRotate` is the same on a page object.

## Creating pages

`PdfDocumentWriter.BeginPage(width, height, rotate)` starts a page whose media box is `[0 0 width height]`. Before `Commit`, the builder takes `SetCropBox`, `SetBleedBox`, `SetTrimBox`, `SetArtBox` and `SetUserUnit`. `BeginPrintPage(trimWidth, trimHeight, bleed, rotate)` is the one-liner: media and bleed box are the trim size plus the bleed on every side, the trim box sits at `(bleed, bleed)`, and drawing coordinates start at the lower left of the media box. `BeginPrintPage(trimWidth, trimHeight, bleed, slug, rotate)` adds the slug (all five arguments are required, so a four-number call keeps meaning bleed and rotation): the bleed box is inset by the slug from the media box and the trim box by bleed plus slug, with no crop box.

`PdfStreamPageBuilder.SetBoxColorInfo(box, info)` sets the page's `/BoxColorInfo` entry for a box before `Commit`, with the same validation and the same bytes as `PdfPage.SetBoxColorInfo`; null removes an entry set earlier on the builder. `PdfDocument.InsertBlankPage` returns a `PdfPage`, so the editing calls above work on it.

## PDF/A

ISO 19005-2 6.1.13 limits every page box to 3 to 14400 units on each side; the PDF/A checker applies it to the boxes written here.
