# Redaction

`PdfDocument.Redact` removes what is drawn inside given areas of a page: the glyphs, the vector paths, the image samples, the annotations and form values under them, and (on request) metadata that repeats the text. It rewrites the content; nothing is hidden under a box. After `Save`, extraction, a byte search of the decoded streams and other readers find nothing of the removed text.

```csharp
using PdfDocument doc = PdfDocument.Open("in.pdf", new PdfOpenOptions { AllowEditing = true });
PdfRedactionResult r = doc.Redact(
    [new PdfRedactionRegion(0, new PdfRectangle(72, 600, 300, 620))],
    new PdfRedactionOptions { Overlay = new PdfRedactionOverlay(), MetadataTerms = ["Jane Doe"] });
doc.Save("out.pdf");
```

`ApplyRedactAnnotations` does the same for the `/Redact` annotations in the file (quad points or `/Rect`, `/IC`, `/OverlayText`) and removes them.

## What the rewrite does

The interpreter runs the page and reports, for every operator, the device calls it caused and the bytes it occupies. The rewrite is made from that reading, so fonts, widths, matrices and clips decide what is under an area exactly as they decide what is drawn. Areas are in the page's default user space (before `/Rotate`).

- **Text.** A glyph is removed when its box meets an area. The box is the advance wide, from 0.2 em below the baseline to 0.9 em above, united with the outline's control bounds; vertical glyphs use their vertical advance. A glyph that overlaps by no more than 0.01 pt does not count. `FullyContainedOnly` removes only glyphs wholly inside. `Tj`, `TJ`, `'` and `"` become a `TJ` whose numbers move the remaining glyphs back to where they were. A Type 3 glyph is removed whole.
- **Vector paths.** `RemovePaths` (default) drops a fill, stroke or both when the path's control bounds (widened by half the line width for strokes) meet an area, including the coordinates. A clipping path stays. `ClipOut` keeps the path but draws it inside a clip that excludes the areas; the geometry then remains readable in the file.
- **Shadings** (`sh`) whose clipped extent meets an area are dropped.
- **Images.** Samples inside an area are blanked in the image's stored sample domain (zero, the unpainted value for a mask, or transparent through the soft mask) and the image is written again as Flate. DCT, JPX and JBIG2 are decoded with the decoder registry (`PdfRedactionOptions.Decoders`); with no decoder for the codec the image is removed whole. Inline images that meet an area are removed whole. Images are copied before they change, so other pages that share one keep the original.
- **Form XObjects** are copied before they are changed; other uses keep the original. Annotation appearances that are kept are redacted the same way.
- **Marked content.** `/ActualText`, `/Alt` and `/E` of the marked-content sequences and structure elements that cover removed content are removed. Structure-tree text of redacted marked content is dropped.
- **Annotations** that meet an area are removed with their pop-ups (`Remove`, default), or kept with their appearance redacted and `/Contents`, `/RC` and values cleared (`KeepRedacted`). Form fields whose widget meets an area lose their value.
- **Metadata.** `MetadataTerms` clears matching Info entries, XMP elements and attributes, and matching outline titles (case-insensitive substring). `RemoveJavaScript`, `RemoveEmbeddedFiles` and `RemoveThumbnails` strip those parts; thumbnails of redacted pages always go.
- **Overlay.** `PdfRedactionOverlay` (or a region's own `FillColor` and `OverlayText`) is drawn over each area after the content is gone.
- **Comments** in rewritten content streams are dropped, because a comment can hold text from the page.

## Fail closed

Anything the rewrite cannot check is not copied through. If a page's content cannot be read completely, the call throws `PdfRedactionException` naming the page and the document is unchanged. This covers an undecodable content stream or form, a limit being hit, a form XObject that uses itself, and a text operator whose glyphs do not match its string. The work is two-phase: every page is planned first and applied only if all of them succeeded.

Bytes after the last operator the interpreter executed (a string or array cut off by the end of the data, operands without an operator) are dropped from the rewritten stream, since they were never checked against the areas.

A page with areas is always written back from what was read.

## Limits

These are not covered; look at the output before relying on it for them.

- Tiling patterns, soft-mask groups and the procedures inside a Type 3 glyph are not edited. A Type 3 glyph under an area goes whole; a pattern or soft mask that draws text elsewhere keeps it.
- Resources the content no longer uses are pruned only where the rewrite replaced them. Unused fonts, images and forms elsewhere stay in the file if something still reaches them.
- Embedded font programs are kept whole, including outlines of glyphs that were removed. They hold shapes, not the text, but a font subset made for the page does name its glyphs.
- Text in outlines (bookmark titles) is cleared only through `MetadataTerms`; destinations and named-destination names are not touched.
- Signatures are invalid once the document is saved. `PdfRedactionResult.SignaturesInvalidated` and a warning say so.
- Only a full `Save` is supported afterwards (it writes no earlier revisions and no unreferenced objects); `SaveIncremental` is refused, because the old bytes would remain in the file.
- Text that is only visible as an image (a scan without a text layer) is removed only by the image blanking above.
- Matching is by geometry. A word that is drawn twice in different places is removed only where an area covers it.
