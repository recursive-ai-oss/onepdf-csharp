# OnePdf.RenderCompare

Checks that a rewrite leaves every page looking exactly the same. It renders every page of two PDFs with two independent renderers, used as black boxes, and compares them pixel by pixel:

- the published `RecursiveAI.OneImage.Pdf` renderer, called in process through its public API;
- poppler's `/usr/bin/pdftoppm`, a separate process (skipped with a note when it is not installed).

    OnePdf.RenderCompare <a.pdf> <b.pdf> [--dpi 150] [--out dir]
    OnePdf.RenderCompare --self-test [--dpi 150] [--out dir]

For each renderer it prints whether the page counts are equal, how many pages and pixels differ and the largest channel difference (alpha included). Pages that differ are listed with their pixel counts, and with `--out` a PNG per differing page is written (page a faded, every differing pixel red). Exit code 0 means every page is identical in every renderer that ran; 1 means a difference or a different page count; 2 means a usage or rendering error.

The comparison is exact on purpose: adding marked content (`BDC`/`EMC`, `/Artifact`), a structure tree or metadata must not move a pixel. Page sizes are compared within a renderer only, so the two renderers' different rounding of the page size does not matter.

`--self-test` shows that the check can see what it must. It writes small files, then expects: an identical copy and a page whose content is wrapped in `BDC`/`EMC` and `/Artifact BMC`/`EMC` to compare identical; a changed colour operand, a rectangle shifted by one unit and an extra page to be detected, in both renderers.

Run it by hand, for instance on a file and its tagged version, or from a script around the tagging API.
