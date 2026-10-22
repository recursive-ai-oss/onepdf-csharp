# Real-world extraction-quality set

PDFs made by real producers, each with reference text for one or a few pages, scored by `tools/OnePdf.Quality` next to the generated set
(`dotnet run -c Release --project tools/OnePdf.Quality -- all --set realworld`).

- `pdf/<name>.pdf` — the files, **unmodified** copies from the sources named in `MANIFEST.csv` (source URL, licence, SHA-256, attribution per file).
  Only public-domain and CC BY material is committed: US federal works (GovDocs1 files whose original URL is a federal site, 17 U.S.C. 105; contractor-run laboratories
  and documents filed by third parties on federal sites are left out) and CC BY 4.0 papers and slide decks (arXiv, Zenodo; each record's licence is on the page the manifest links to).
- `truth/<name>.json` — one sidecar per file, the schema of `../generated/README.md` plus the fields below.
- Files whose terms do not allow committing (United Nations documents here) are local-only: `tools/corpus/realworld-local.lock.json` pins their URLs, SHA-256 and the SHA-256 of their
  reference text, `ONEPDF_QUALITY_CACHE=<dir> python3 tools/corpus/fetch_realworld.py` downloads them (safe to run again) and the harness scores them as well when the same variable is set.
  Their reference text stays in that directory (`truth/`), never in the repository. Files with unclear terms stay local-only.
- `python3 tools/corpus/build_manifest.py --check` verifies the manifest, the licence of every row and that every file still has the SHA-256 (and, for GovDocs1 files, SHA-1) its
  selection entry names.

## Size

| category | committed | local-only |
|---|---:|---:|
| `cjk` | 9 | 4 |
| `form` | 8 | |
| `math` | 8 | |
| `multi-column` | 9 | |
| `old` | 8 | |
| `report` | 8 | |
| `rotated` | 8 | |
| `rtl` | 9 | 3 |
| `scanned-ocr` | 8 | 1 |
| `slides` | 9 | |
| `table` | 8 | |
| total | 92 | 8 |

No committed PDF is over 1 MB.

## Added fields

```json
{ "pageCount": 12, "scoredPages": [5],
  "pages": [ { "text": "…" } ],
  "reference": { "method": "…", "confidence": "high|medium|low", "checkedPages": [5],
                 "extractorAgreement": { "6": 0.99 }, "notes": "…" },
  "source": { "license": "CC-BY-4.0", "source_url": "…", "upstream_ref": "…", "attribution": "…" } }
```

- `scoredPages` are the zero-based document pages the entries of `pages` belong to; the rest of the file is not scored (keeps the files small and the truth reviewable).
- `reference.method` says how the text was made. For every file: a draft from the text layer as recovered by two black-box extractors (poppler `pdftotext`; MuPDF through its Python
  binding), compared with each other and, for Latin text, with tesseract OCR of the rendered page (`extractorAgreement` is the bag-of-words overlap between the two extractors on that page).
  **Every scored page of every file was then compared with a rendering of the page** (poppler `pdftoppm`) and corrected: block order, columns, missing or extra text, headers and footers,
  figure and table order. No extractor's output is used unchecked, and none is the truth. Characters are the **text layer**, not re-read from pixels: a ToUnicode defect of the producer
  is kept, because it is what an extractor can recover. For scanned files the reference is the embedded OCR layer with its errors, so the set measures extraction, not recognition.
- `reference.checkedPages` equals `scoredPages` in every file.
- `reference.confidence`: **high** = text and order are unambiguous and the extractors agree; **medium** = layout-dependent (columns, forms, tables) or the characters have producer defects;
  **low** = what is left is inherently uncertain: the internal order of formulas, a table whose columns cannot be told from the text, a newspaper page whose articles and pull-quotes
  have no single reading order, or a text layer too damaged to confirm letters against the image. A file is never low only because it has not been checked.
- Conventions as in the generated set: logical order, blocks separated by a blank line, wrapped-paragraph breaks not recorded, line-end hyphenation joined when the next line starts in
  lower case, ligatures decomposed, Unicode presentation forms normalised (NFKC) and bidirectional control characters removed. Page furniture (running heads, page numbers, footers) is
  part of the page text at its position.

## Reference decisions

- **Right-to-left text is in logical order** (Unicode storage order: the first character read is the first character of the string), not visual order. Numbers and Latin runs inside
  Arabic, Persian and Hebrew sit where a reader meets them, list numbers before their text, and brackets enclose what they enclose. An extractor that emits visual order scores worse;
  that is intended.
- **What counts as page text.** Text a reader sees on the rendered page, plus the OCR layer of scanned pages. Left out: form-field values and the captions of buttons and other widgets
  (annotations); text that is in the content stream but not visible (white text, printer's slug lines, master-slide footers that the slide does not show); text inside raster images
  (charts, logos, banners). Where such invisible text exists it is named in the file's `notes`.
- **Not text.** Control characters (glyph codes that a producer's font maps to the C0 range for math symbols) are dropped from the reference. Private-use code points are kept as the layer has them.
- **Footnote markers and superscripts** follow the word they belong to (`slaves. 16`), ordinal suffixes are written in line (`6th`). Footnotes come after the body of their column or page.
- **Column order.** Left to right (right to left for Arabic, Persian and Hebrew pages), each column top to bottom; a sentence that runs from the foot of one column into the next is kept whole;
  figure and table captions where they are printed; a data table is written row by row, cells of a row on one line.
- **Sidebars and vertical running heads** come after the main text.
- **Newspaper pages** (one file) are ordered article by article, headline and standfirst first, then body columns, then pull-quotes and captions where they sit; the order of
  those parts is a judgement and the file is low confidence for that reason.

## Categories

`form` (static text of AcroForm files), `scanned-ocr` (OCR layer over page images), `multi-column`, `table`, `rotated` (pages with `/Rotate` or text running sideways), `report` (running heads and footers),
`old` (PDF 1.1–1.3 producers), `math`, `slides`, `rtl` (Arabic, Hebrew, Persian), `cjk` (Chinese, Japanese, Korean). A file sits in one category even when it also fits another.

## PdfPig baseline (the bar)

PdfPig 0.1.16, `net10.0` (SDK 10.0.400, runtime 10.0.11), measured with `dotnet <tool>.dll pdfpig --set realworld` on a shared workstation (load average about 11 on 32 cores; a local
smoke result, not a benchmark). Percent, lower is better; each cell is content / order (metrics as in `tools/OnePdf.Quality/README.md`). *default* is `Page.Text`; *layout* is its documented
layout pipeline; the **bar** is the better of the two per metric.

**PdfPig's layout pipeline is not deterministic.** Its *content* figures and the whole *default* column are identical on every run, but the layout *order* figure moves between runs
(here by 0.1 to 1.1 percentage points per category, 0.1 overall; it differed between runs with different thread-pool sizes and JIT settings, and was identical across repeated runs of one
configuration). The layout order column and the bar below are therefore the **best of 8 runs** per set (3 default, 3 with `DOTNET_PROCESSOR_COUNT=1`, 2 with `DOTNET_TieredCompilation=0`);
the last column shows the worst of the 8 runs for the layout order, to show the spread. A tie is within 0.1 percentage points.

Committed files only (92 files; reproducible without the local-only set):

| category | files | default | layout (best of 8) | bar (best of both) | layout order, worst run |
|---|---:|---:|---:|---:|---:|
| cjk | 9 | 4.3 / 3.9 | 4.3 / 5.7 | 4.3 / 3.9 | 5.7 |
| form | 8 | 17.6 / 50.7 | 0.4 / 37.4 | 0.4 / 37.4 | 37.4 |
| math | 8 | 71.4 / 86.2 | 9.6 / 29.8 | 9.6 / 29.8 | 29.8 |
| multi-column | 9 | 47.0 / 55.8 | 4.8 / 16.8 | 4.8 / 16.8 | 16.9 |
| old | 8 | 27.2 / 43.4 | 1.9 / 31.3 | 1.9 / 31.3 | 31.3 |
| report | 8 | 27.2 / 36.2 | 2.6 / 24.4 | 2.6 / 24.4 | 24.4 |
| rotated | 8 | 34.2 / 43.8 | 0.4 / 55.3 | 0.4 / 43.8 | 55.9 |
| rtl | 9 | 83.0 / 98.7 | 83.3 / 99.8 | 83.0 / 98.7 | 99.8 |
| scanned-ocr | 8 | 36.5 / 53.2 | 7.3 / 25.7 | 7.3 / 25.7 | 25.7 |
| slides | 9 | 32.0 / 45.0 | 6.8 / 33.1 | 6.8 / 33.1 | 34.3 |
| table | 8 | 54.5 / 64.5 | 7.2 / 85.9 | 7.2 / 64.5 | 85.9 |
| **all** | 92 | 41.3 / 48.4 | 13.5 / 36.5 | 13.5 / 36.5 | 36.5 |

Committed plus local-only files (100 files, with `ONEPDF_QUALITY_CACHE` set); the other categories are unchanged:

| category | files | default | layout (best of 8) | bar (best of both) | layout order, worst run |
|---|---:|---:|---:|---:|---:|
| cjk | 13 | 3.2 / 3.0 | 3.1 / 4.2 | 3.1 / 3.0 | 4.2 |
| rtl | 12 | 82.6 / 98.5 | 82.8 / 99.9 | 82.6 / 98.5 | 100.0 |
| scanned-ocr | 9 | 34.2 / 50.3 | 7.1 / 24.5 | 7.1 / 24.5 | 24.5 |
| **all** | 100 | 40.8 / 45.6 | 14.6 / 34.9 | 14.6 / 34.9 | 35.0 |

Reading the numbers: PdfPig emits right-to-left text in visual order, so the logical-order `rtl` reference puts both its variants near 100 % on order; the `table` reference is row by row,
which block-based layout analysis does not produce (layout order 85.9 %); `math` references keep the text layer's order for formula parts and are low confidence. The `cjk` and `rtl`
content rates are character-level and sensitive to spaces the producer did or did not store.

## How the references were corrected

Token-sequence edit distance between the reference as first drafted (or as last committed, for files checked before) and the final reference, over the final token count (tokens as in the
scorer: white-space runs, every ideograph, kana and hangul its own token). Splitting a run of text into blocks changes nothing here. 28 committed and 8 local-only references had been
drafted but not compared with their page image; they were all compared and corrected, and 6 new files were added.

| category | files compared now | of them changed in words or order | edit distance / final tokens |
|---|---:|---:|---:|
| cjk (committed) | 3 | 0 | 0.0 % |
| cjk (local-only) | 4 | 3 | 28.9 % |
| form | 3 | 3 | 9.6 % |
| math | 5 | 4 | 8.0 % |
| old | 2 | 2 | 23.0 % |
| rtl (committed) | 7 | 5 | 25.5 % |
| rtl (local-only) | 3 | 3 | 30.4 % |
| scanned-ocr (committed) | 2 | 1 | 5.8 % |
| scanned-ocr (local-only) | 1 | 1 | 76.7 % |
| slides | 6 | 3 | 21.5 % |

Files that had been checked before were re-compared where a defect showed up: `cjk-04` (two interleaved columns rebuilt column by column),
`form-05` (a button caption removed), `old-04` (control characters), `scanned-ocr-04` (a split line rejoined), `math-01` and `math-03` (control characters, notes). The other previously
checked references (multi-column, report, rotated, table, the rest of slides, cjk, rtl, form and old) were unchanged.
