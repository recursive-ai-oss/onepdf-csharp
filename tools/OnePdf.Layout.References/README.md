# OnePdf.Layout.References

Renders PDFs to PNG through the published `RecursiveAI.OneImage.Pdf` package and compares fresh renders with committed ones. It has no reference to the layout package: it reads PDF files only, so it checks what a viewer would show.

    OnePdf.Layout.References render  <documents dir> <png dir> [dpi]
    OnePdf.Layout.References compare <documents dir> <png dir> [dpi]

`render` writes `<document>-p<page>.png` for every page and `manifest.csv`. `compare` renders again and prints, for each page, the largest channel difference, the mean difference and the share of pixels that differ by more than 16 levels; a page whose share is above 0.05 % is reported as `DIFFERS` and the exit code is 1. The default resolution is 72 dpi; the committed set uses 100.

The documents and the committed images are described in `testdata/layout-references/README.md`. Run it by hand at milestones and releases.
