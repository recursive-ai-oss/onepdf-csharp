# Layout references

A small set of documents laid out by `OnePdf.Layout`, and a picture of every page, used to notice when a change moves anything on a page.

- `documents/` holds 14 PDFs (one per family of features: boxes, columns, gradients, images, invoice, line breaking, lists, report, right-to-left, mixed scripts, sections, table over several pages, text styles, transforms) and `index.csv`. They are written by `tools/OnePdf.Layout.Documents` from the fluent API; the output is deterministic.
- `png/` holds 19 page images at 100 dpi and `manifest.csv` (image, source document, description, generator, renderer, dpi, SHA-256 of image and of document).
- The images come from the published `RecursiveAI.OneImage.Pdf` package, used as a black box by `tools/OnePdf.Layout.References`, which knows nothing about the layout package.

Each reference was checked once against a PDFium render (same pages side by side: positions of text, rules, boxes and gradient fills agree on the first 17 pages; the two text-heavy pages differ only by glyph rasterization). The line-breaking page was added later and checked by eye: both columns hold the same text, the right one broken by total fit.

Regenerate, from the repository root:

    dotnet run --project tools/OnePdf.Layout.Documents -- .
    dotnet run --project tools/OnePdf.Layout.References -- render testdata/layout-references/documents testdata/layout-references/png 100

Compare after a change (this does not write anything):

    dotnet run --project tools/OnePdf.Layout.References -- compare testdata/layout-references/documents testdata/layout-references/png 100

A page counts as different when more than 0.05 % of its pixels differ by more than 16 levels in any channel. Run it at milestones and releases, not in CI. When a difference is intended, look at the new page, then render again and commit the new images with the changed documents.

The columns page was added later: its text order was checked by eye (column 1, then 2, then 3, the table continuing into the third with its header repeated) and against a Poppler render of the same file, which agrees.
