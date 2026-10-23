# OnePdf.Quality

Scores text extraction against the truth of two sets, per category, for OnePDF and for PdfPig (a compiled package, referenced by this project only): the generated set
(`testdata/generated`, exact truth) and the real-world set (`testdata/realworld`, reference text checked by hand against the pages; see its README).

```
dotnet run -c Release --project tools/OnePdf.Quality -- all [--set generated|realworld|all | --dir DIR] [--only NAME-PART] [--md report.md] [--json report.json] [--files]
dotnet run -c Release --project tools/OnePdf.Quality -- dump --set realworld --only form-08 [--lines] [--glyphs]   # OnePDF text next to the truth; --lines lists lines with bounds, --glyphs the glyphs
dotnet run -c Release --project tools/OnePdf.Quality -- pdfpig     # only the comparison library
dotnet run -c Release --project tools/OnePdf.Quality -- selftest   # checks the scorer on known answers
dotnet run -c Release --project tools/OnePdf.Quality -- smoke      # times layout of synthetic pages (not a benchmark)
```

`--set` defaults to `generated`. `--set realworld` scores the committed real-world files and, when `ONEPDF_QUALITY_CACHE` names a directory with `pdf/` and `truth/` (as filled by
`tools/corpus/fetch_realworld.py`), the local-only ones too; `--set all` prints both sets as separate tables. Real-world truth lists the document pages it covers (`scoredPages`), and only those pages are extracted and scored.

## Metrics

Both sides are split into tokens (runs of non-white-space; every ideograph, kana and hangul character its own token).

- **Content error rate** (order-free): tokens sorted ordinally and joined by spaces; Levenshtein distance between the two strings divided by the truth string's length.
- **Reading-order error**: Levenshtein distance between the token sequences divided by the number of truth tokens.

Rates are summed per category (distance and size separately, then divided), so longer pages weigh more. A difference under 0.1 percentage points is a tie. The verdict column
compares OnePDF with the better of the two PdfPig variants, per metric.

## PdfPig columns

- *default*: `Page.Text`, PdfPig's default text extraction.
- *layout*: its documented layout pipeline, `NearestNeighbourWordExtractor` → `DocstrumBoundingBoxes` → `UnsupervisedReadingOrderDetector`, blocks joined by blank lines.

Both open files with the truth's passwords and lenient parsing. A file PdfPig cannot open scores as empty output.

## Results

PdfPig 0.1.16 on `net10.0`. Percent, lower is better; each cell is content / order. The bar OnePDF has to meet or tie in every
category is the better of PdfPig's two variants per metric (in some rows the two numbers come from different variants). Where PdfPig's
layout variant gets 0.0, OnePDF can only tie. One generated file (a truncated tail) cannot be opened by PdfPig and scores as empty.

### Generated set (62 files, exact truth)

| category | files | PdfPig default | PdfPig layout | OnePDF |
|---|---:|---:|---:|---:|
| annotations | 1 | 20.3 / 22.2 | 0.0 / 0.0 | 0.0 / 0.0 |
| cjk | 3 | 14.1 / 8.0 | 14.1 / 8.0 | 0.0 / 0.0 |
| cjk-vertical | 2 | 10.5 / 9.0 | 10.5 / 72.1 | 0.0 / 0.0 |
| embedded-files | 1 | 0.0 / 0.0 | 0.0 / 0.0 | 0.0 / 0.0 |
| encrypted | 13 | 12.6 / 13.9 | 0.0 / 0.0 | 0.0 / 0.0 |
| fonts-std14 | 1 | 26.0 / 53.3 | 20.8 / 20.0 | 0.0 / 0.0 |
| fonts-styles | 1 | 7.4 / 14.8 | 3.7 / 18.5 | 0.0 / 0.0 |
| forms | 1 | 23.1 / 33.3 | 0.0 / 0.0 | 0.0 / 0.0 |
| graphics | 3 | 0.0 / 0.0 | 0.0 / 0.0 | 0.0 / 0.0 |
| ligatures | 2 | 70.0 / 91.7 | 26.2 / 44.0 | 0.8 / 20.2 |
| links-outline | 1 | 25.1 / 33.3 | 0.0 / 0.0 | 0.0 / 0.0 |
| metadata | 1 | 0.0 / 0.0 | 0.0 / 0.0 | 0.0 / 0.0 |
| multi-column | 3 | 22.1 / 25.3 | 0.0 / 0.0 | 0.0 / 0.0 |
| outlines-labels | 1 | 0.0 / 0.0 | 0.0 / 0.0 | 0.0 / 0.0 |
| repair | 9 | 20.9 / 21.5 | 12.3 / 12.3 | 0.0 / 0.0 |
| report | 2 | 32.3 / 31.7 | 0.9 / 2.9 | 0.9 / 2.9 |
| rotated-text | 1 | 11.5 / 15.0 | 0.0 / 0.0 | 0.0 / 0.0 |
| rotation | 3 | 22.6 / 23.8 | 0.0 / 0.0 | 0.0 / 0.0 |
| rtl | 3 | 71.4 / 84.4 | 66.9 / 77.9 | 6.1 / 13.9 |
| single-column | 1 | 14.6 / 16.2 | 0.0 / 0.0 | 0.0 / 0.0 |
| structure | 6 | 14.1 / 16.4 | 0.0 / 0.0 | 0.0 / 0.0 |
| symbols | 1 | 29.0 / 15.4 | 0.0 / 0.0 | 0.0 / 0.0 |
| table | 1 | 77.0 / 88.5 | 0.0 / 46.2 | 0.0 / 0.0 |
| type3 | 1 | 61.5 / 100.0 | 0.0 / 0.0 | 0.0 / 0.0 |
| **all** | 62 | 21.9 / 22.7 | 5.6 / 10.7 | 0.3 / 1.2 |

### Real-world set (92 committed files)

PdfPig's layout reading order varies slightly from run to run, so the bar in
[`testdata/realworld/README.md`](../../testdata/realworld/README.md) is the best of 8 runs; this table is a single run, and the
overall figures are the same.

| category | files | PdfPig default | PdfPig layout | OnePDF |
|---|---:|---:|---:|---:|
| cjk | 9 | 4.3 / 3.9 | 4.3 / 5.7 | 2.2 / 2.1 |
| form | 8 | 17.6 / 50.7 | 0.4 / 37.4 | 0.3 / 6.5 |
| math | 8 | 71.4 / 86.2 | 9.6 / 29.8 | 6.6 / 11.9 |
| multi-column | 9 | 47.0 / 55.8 | 4.8 / 16.8 | 4.1 / 16.8 |
| old | 8 | 27.2 / 43.4 | 1.9 / 31.3 | 0.9 / 25.0 |
| report | 8 | 27.2 / 36.2 | 2.6 / 24.4 | 1.5 / 7.0 |
| rotated | 8 | 34.2 / 43.8 | 0.4 / 55.9 | 0.3 / 12.2 |
| rtl | 9 | 83.0 / 98.7 | 83.3 / 99.8 | 27.6 / 57.6 |
| scanned-ocr | 8 | 36.5 / 53.2 | 7.3 / 25.7 | 5.3 / 22.0 |
| slides | 9 | 32.0 / 45.0 | 6.8 / 33.1 | 6.5 / 21.0 |
| table | 8 | 54.5 / 64.5 | 7.2 / 85.9 | 2.4 / 3.0 |
| **all** | 92 | 41.3 / 48.4 | 13.5 / 36.5 | 5.6 / 15.9 |
