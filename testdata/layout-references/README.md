# Layout references

A small set of documents laid out by `OnePdf.Layout`, and a picture of every page, used to notice when a change moves anything on a page.

- `documents/` holds 18 PDFs (one per family of features: a tagged document saved as PDF/UA-2, boxes, columns, gradients, images, inline objects, invoice, line breaking, lists, report, right-to-left, mixed scripts, sections, a three-page statement with carried-forward values, table over several pages, table of contents, text styles, transforms) and `index.csv`. They are written by `tools/OnePdf.Layout.Documents` from the fluent API; the output is deterministic.
- `png/` holds 32 page images at 100 dpi and `manifest.csv` (image, source document, description, generator, renderer, dpi, SHA-256 of image and of document).
- The images come from the published `RecursiveAI.OneImage.Pdf` package, used as a black box by `tools/OnePdf.Layout.References`, which knows nothing about the layout package.

Each reference was checked once against a PDFium render (same pages side by side: positions of text, rules, boxes and gradient fills agree on the first 17 pages; the two text-heavy pages differ only by glyph rasterization). The line-breaking page was added later and checked by eye: both columns hold the same text, the right one broken by total fit. The table-of-contents pages were checked by eye too: every number equals the label printed in the footer of the page its heading is on, a long title wraps with its number on the last line, and dots line up within a level.

Regenerate, from the repository root:

    dotnet run --project tools/OnePdf.Layout.Documents -- .
    dotnet run --project tools/OnePdf.Layout.References -- render testdata/layout-references/documents testdata/layout-references/png 100

Compare after a change (this does not write anything):

    dotnet run --project tools/OnePdf.Layout.References -- compare testdata/layout-references/documents testdata/layout-references/png 100

A page counts as different when more than 0.05 % of its pixels differ by more than 16 levels in any channel. Run it at milestones and releases, not in CI. When a difference is intended, look at the new page, then render again and commit the new images with the changed documents.

The columns page was added later: its text order was checked by eye (column 1, then 2, then 3, the table continuing into the third with its header repeated) and against a Poppler render of the same file, which agrees.

The statement pages were checked by eye: the amounts of each page add up to its subtotal (page 2: 1,083.75), every carried-forward figure equals the next page's brought-forward figure, the last page carries the sum of all rows, and the header month is that of the page's first row.

The inline-objects page was checked by eye and against a Poppler render of the same file, which agrees: each of the six alignments puts the 16-point picture where its rule says (baseline: on the baseline; top: at the top of the line; middle: centred on the text; bottom: at the bottom of the line; text top and text bottom: at the font's ascent and descent), the badge text shares the baseline of the line, objects keep their size in the justified paragraph, and the object between two Arabic words sits between them in display order.

The PDF/UA-2 document (`accessible-ua2.pdf`) is the one reference saved through `PdfSaveOptions.PdfUA` with `PdfUAPart.Part2`: PDF 2.0, structure namespace on every element, structure destinations behind its bookmarks, anchor link and table-of-contents links. It was checked by eye (the table of contents lists four entries whose page numbers match the pages their headings are on; the aside, footnote, picture, list and table draw as in any other tagged document) and with veraPDF's `ua2` flavour, which reports it compliant.
