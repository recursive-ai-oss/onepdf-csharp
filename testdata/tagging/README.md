# Tagging samples

Untagged PDFs, each with a `<name>.roles.json` that says how a careful person would tag it. They are the acceptance set for adding a structure tree to an existing untagged file: a tagging run is given these roles, and its output must render like the input, extract the same text and pass a PDF/UA-1 check.

The PDFs are in `pdf/` (made by `make_samples.py` from `src/`, our own files) or are existing files elsewhere in `testdata/`, named by `source`.

| roles file | source | licence | covers |
|---|---|---|---|
| `chrome-article` | `pdf/` | ours (MIT OR Apache-2.0) | headings, paragraphs, bulleted list (drawn bullets), numbered list, two links, running header and footer with page numbers |
| `chrome-table` | `pdf/` | ours | ruled table with a header row over two pages; repeated header row |
| `chrome-figures` | `pdf/` | ours | raster figure, vector chart with text, captions |
| `chrome-columns` | `pdf/` | ours | two columns, a paragraph continuing across columns |
| `chrome-arabic` | `pdf/` | ours | right-to-left page with a list |
| `chrome-cjk-ja` | `pdf/` | ours | Japanese page with a list |
| `synthetic-forms` | `pdf/` | ours | Form XObjects (shared and nested), inline image, vector drawing, link, watermark |
| `lo-report` | `../generated/pdf/` | ours | LibreOffice report: running header and footer, text bullets, small ruled table, link |
| `govdocs-882313` | `../govdocs/` | US federal work, see `../MANIFEST.csv` | legal order: caption laid out with rules, a paragraph over two pages, a footnote, signature block |
| `govdocs-210477` | `../govdocs/` | US federal work, see `../MANIFEST.csv` | vector legend chart whose labels are text |

Regenerate the files in `pdf/` with `python3 testdata/tagging/make_samples.py [name-prefix]`. Chrome is driven through its DevTools protocol because only that interface can switch tagging off; Chrome stamps dates, so a regenerated Chrome file is equivalent, not byte-identical, and the committed files are the reference. `synthetic-forms.pdf` is byte-for-byte reproducible.

## Roles file, version 1

```json
{ "version": 1, "source": "pdf/chrome-table.pdf", "title": "…", "language": "en",
  "blockCounts": [26, 1], "notes": "…", "elements": [ element, … ] }
```

`elements` is the structure in reading order, depth first. `blockCounts` is the number of blocks per page in the extraction the references index into: `doc.ExtractText(new PdfTextOptions { RetainGlyphs = true })` with default options.

An **element** is `{ "role": …, "children": [ … ] }` with optional `alt` (Figure, required), `scope` (TH: `Column`, `Row`, `Both`), `rowSpan`/`colSpan`, `lang` and `artifact`. Children are elements or references, in order.

| role | children | notes |
|---|---|---|
| `H1`–`H6`, `P` | references, `Link` | |
| `L` | `LI` | |
| `LI` | `Lbl`, `LBody` | |
| `Lbl` | references | a bullet drawn as a shape is a `graphics` reference |
| `LBody` | references, `P`, `L`, `Link`, `Figure` | |
| `Table` | `TR` | header rows are `TH` with `scope`; a table may continue over pages |
| `TR` | `TH`, `TD` | |
| `TH`, `TD` | references, `P`, `Link`, `Figure` | |
| `Figure` | references | `alt` required; may hold the figure's own text |
| `Link` | references | needs one `annotation` reference |
| `Div` | any block element | grouping |
| `Artifact` | references | `artifact`: `Header`, `Footer`, `Watermark` or `Layout` |

Anything painted that no element references (rules, backgrounds, shapes) is an artifact implicitly and is not listed. A repeated table header row on a continuation page is listed as a `Layout` artifact, because it repeats information the first page's header row already gives.

A **reference** says which extracted text or mark belongs to an element. Every key has `page` (zero-based). Pages, blocks, lines, words and cells are zero-based indices into the extraction:

- `{ "page": 0, "block": 3 }` the whole block;
- `{ "page": 0, "block": 3, "lines": [1, 2] }` lines 1 to 2 (inclusive);
- `{ "page": 0, "block": 3, "line": 0, "words": [4, 6] }` words 4 to 6 of one line;
- `{ "page": 0, "block": 3, "line": 0, "cell": 2 }` a table cell: the words from `CellStarts[2]` up to the next cell start;
- `{ "page": 0, "graphics": [left, bottom, right, top] }` the non-text marks (images, paths) painted inside the rectangle, in page space;
- `{ "page": 0, "annotation": 1 }` an entry of `page.Annotations`.

Every word of extracted text is referenced exactly once, every `Link` annotation is in a `Link` element, and the nesting follows the table above. `tests/OnePdf.Tests/TaggingSamples` checks all of this against the extraction.

The roles were assigned from the page images and the extracted text, not from any tool's output.
