# Real-world extraction-quality set

PDFs made by real producers, each with reference text for one or a few pages, scored by `tools/OnePdf.Quality` next to the generated set
(`dotnet run -c Release --project tools/OnePdf.Quality -- all --set realworld`).

- `pdf/<name>.pdf` — the files, **unmodified** copies from the sources named in `MANIFEST.csv` (source URL, licence, SHA-256, attribution per file).
  Only public-domain and CC BY material is committed: US federal works (GovDocs1 files whose original URL is a federal site, 17 U.S.C. 105; contractor-run laboratories
  and documents filed by third parties on federal sites are left out) and CC BY 4.0 papers and slide decks (arXiv, Zenodo; each record's licence is on the page the manifest links to).
- `truth/<name>.json` — one sidecar per file, the schema of `../generated/README.md` plus the fields below.
- Files whose terms do not allow committing (United Nations documents here) are local-only: `tools/corpus/realworld-local.lock.json` pins their URLs and SHA-256,
  `ONEPDF_QUALITY_CACHE=<dir> python3 tools/corpus/fetch_realworld.py` downloads them (safe to run again) and the harness scores them as well when the same variable is set.
  Their reference text stays in that directory (`truth/`), never in the repository.

## Added fields

```json
{ "pageCount": 12, "scoredPages": [5],
  "pages": [ { "text": "…" } ],
  "reference": { "method": "…", "confidence": "high|medium|low", "checkedPages": [5],
                 "extractorAgreement": { "6": 0.99 }, "notes": "…" },
  "source": { "license": "CC-BY-4.0", "source_url": "…", "upstream_ref": "…", "attribution": "…" } }
```

- `scoredPages` are the zero-based document pages the entries of `pages` belong to; the rest of the file is not scored (keeps the files small and the truth reviewable).
- `reference.method` says how the text was made. For every file: a draft from the text layer as recovered by two black-box extractors (poppler `pdftotext`; MuPDF through its Python binding),
  compared with each other and, for Latin text, with tesseract OCR of the rendered page (`extractorAgreement` is the bag-of-words overlap between the two extractors on that page);
  block order and completeness then checked against the page image. Characters are the **text layer**, not re-read from pixels: a ToUnicode defect of the producer is kept,
  because it is what an extractor can recover. For scanned files the reference is the embedded OCR layer with its errors, so the set measures extraction, not recognition.
  Tables are written row by row with `pdftotext -layout`; two-column pages come from a column-by-column rule with running heads and page numbers at their top or bottom position.
  No single extractor's output is used unchecked, and none is the truth.
- `reference.confidence`: **high** = checked against the page image and the two extractors agree (≥ 0.97); **medium** = checked against the image, layout-dependent or extractors differ;
  **low** = layout or content is inherently ambiguous (equations, sideways text, tables of marks) **or the page has not been compared with its image yet** (`checkedPages` is empty).
  There is no source-derived reference in this set yet: LaTeX sources were not used.
- `checkedPages`: the zero-based pages whose image was compared with the reference by eye (at 72 dpi: block order, missing or extra blocks, columns, headers and footers).
  Letters were not proofread against pixels.
- Conventions as in the generated set: logical order (RTL included), blocks separated by a blank line, wrapped-paragraph breaks not recorded, line-end hyphenation joined when the next line starts in lower case,
  ligatures decomposed, Unicode presentation forms normalised (NFKC) and bidirectional control characters removed. Page furniture (running heads, page numbers, footers) is part of the page text at its position.
  Form-field values and annotations are not page text.

## Categories

`form` (static text of AcroForm files), `scanned-ocr` (OCR layer over page images), `multi-column`, `table`, `rotated` (pages with `/Rotate` or text running sideways), `report` (running heads and footers),
`old` (PDF 1.1–1.3 producers), `math`, `slides`, `rtl` (Arabic, Hebrew, Persian), `cjk` (Chinese, Japanese, Korean). A file sits in one category even when it also fits another.
