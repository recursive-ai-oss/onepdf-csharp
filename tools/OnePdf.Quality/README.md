# OnePdf.Quality

Scores text extraction against the truth of two sets, per category, for OnePDF and for PdfPig (a compiled package, referenced by this project only): the generated set
(`testdata/generated`, exact truth) and the real-world set (`testdata/realworld`, reference text checked by hand against the pages; see its README).

```
dotnet run -c Release --project tools/OnePdf.Quality -- all [--set generated|realworld|all | --dir DIR] [--md report.md] [--json report.json] [--files]
dotnet run -c Release --project tools/OnePdf.Quality -- pdfpig     # only the comparison library
dotnet run -c Release --project tools/OnePdf.Quality -- selftest   # checks the scorer on known answers
dotnet run -c Release --project tools/OnePdf.Quality -- smoke      # times layout of synthetic pages (not a benchmark)
```

The OnePDF column runs as soon as the build has a glyph producer (`PdfTextExtractor.TryCreate` stops returning null); until then the
command prints PdfPig's numbers and says the OnePDF column is not available.

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

## PdfPig baseline (the bar)

PdfPig 0.1.16 on `net10.0`, 59 files with truth, generated set as committed. Percent, lower is better; each cell is content / order. This is the
bar the OnePDF column must meet or tie in every category.

| category | files | default | layout | bar (best of both) |
|---|---:|---:|---:|---:|
| annotations | 1 | 20.3 / 22.2 | 0.0 / 0.0 | 0.0 / 0.0 |
| cjk | 3 | 14.1 / 8.0 | 14.1 / 8.0 | 14.1 / 8.0 |
| cjk-vertical | 2 | 10.5 / 9.0 | 10.5 / 72.1 | 10.5 / 9.0 |
| embedded-files | 1 | 0.0 / 0.0 | 0.0 / 0.0 | 0.0 / 0.0 |
| encrypted | 13 | 12.6 / 13.9 | 0.0 / 0.0 | 0.0 / 0.0 |
| fonts-std14 | 1 | 26.0 / 53.3 | 20.8 / 20.0 | 20.8 / 20.0 |
| fonts-styles | 1 | 7.4 / 14.8 | 3.7 / 18.5 | 3.7 / 14.8 |
| forms | 1 | 23.1 / 33.3 | 0.0 / 0.0 | 0.0 / 0.0 |
| ligatures | 2 | 70.0 / 91.7 | 26.2 / 44.0 | 26.2 / 44.0 |
| links-outline | 1 | 25.1 / 33.3 | 0.0 / 0.0 | 0.0 / 0.0 |
| metadata | 1 | 0.0 / 0.0 | 0.0 / 0.0 | 0.0 / 0.0 |
| multi-column | 3 | 22.1 / 25.3 | 0.0 / 0.0 | 0.0 / 0.0 |
| outlines-labels | 1 | 0.0 / 0.0 | 0.0 / 0.0 | 0.0 / 0.0 |
| repair | 9 | 20.9 / 21.5 | 12.3 / 12.3 | 12.3 / 12.3 |
| report | 2 | 32.3 / 31.7 | 0.9 / 2.9 | 0.9 / 2.9 |
| rotated-text | 1 | 11.5 / 15.0 | 0.0 / 0.0 | 0.0 / 0.0 |
| rotation | 3 | 22.6 / 23.8 | 0.0 / 0.0 | 0.0 / 0.0 |
| rtl | 3 | 71.4 / 84.4 | 66.9 / 77.9 | 66.9 / 77.9 |
| single-column | 1 | 14.6 / 16.2 | 0.0 / 0.0 | 0.0 / 0.0 |
| structure | 6 | 14.1 / 16.4 | 0.0 / 0.0 | 0.0 / 0.0 |
| symbols | 1 | 29.0 / 15.4 | 0.0 / 0.0 | 0.0 / 0.0 |
| table | 1 | 77.0 / 88.5 | 0.0 / 46.2 | 0.0 / 46.2 |
| type3 | 1 | 61.5 / 100.0 | 0.0 / 0.0 | 0.0 / 0.0 |
| **all** | 59 | 22.1 / 22.8 | 5.6 / 10.8 | 5.6 / 10.8 |

Note the "bar" is per-metric best of the two variants, so in some rows the two numbers come from different variants. The PdfPig layout variant gets 0.0 in most clean categories,
so on those OnePDF can only tie. The `repair` category includes one file PdfPig cannot open (truncated tail).

## PdfPig baseline on the real-world set

PdfPig 0.1.16 on `net10.0`, 94 files (86 committed + 8 local-only United Nations documents), one to a few scored pages each. Percent, lower is better; each cell is content / order.
The reference text is the text layer checked against the page image (confidence is per file in the sidecars; 32 of the 94 are marked low), so read the order column as
"agreement with a reading order a person confirmed", not as exact truth. The bar is the better of the two variants per metric, as above.

| category | files | default | layout | bar (best of both) |
|---|---:|---:|---:|---:|
| cjk | 13 | 0.6 / 16.9 | 0.5 / 18.2 | 0.5 / 16.9 |
| form | 8 | 17.7 / 51.9 | 0.6 / 39.8 | 0.6 / 39.8 |
| math | 7 | 69.1 / 88.3 | 10.1 / 35.7 | 10.1 / 35.7 |
| multi-column | 9 | 47.0 / 55.8 | 4.8 / 16.8 | 4.8 / 16.8 |
| old | 7 | 22.3 / 54.2 | 1.7 / 40.8 | 1.7 / 40.8 |
| report | 6 | 16.4 / 23.2 | 2.0 / 29.8 | 2.0 / 23.2 |
| rotated | 8 | 34.2 / 43.8 | 0.4 / 55.9 | 0.4 / 43.8 |
| rtl | 12 | 82.5 / 98.9 | 82.6 / 99.7 | 82.5 / 98.9 |
| scanned-ocr | 8 | 41.3 / 60.3 | 6.1 / 26.2 | 6.1 / 26.2 |
| slides | 9 | 29.7 / 49.8 | 2.0 / 37.6 | 2.0 / 37.6 |
| table | 7 | 48.3 / 58.8 | 0.0 / 84.9 | 0.0 / 58.8 |
| **all** | 94 | 38.7 / 49.6 | 14.5 / 40.8 | 14.5 / 40.8 |

Before reading a verdict: PdfPig emits right-to-left text in visual order, so the logical-order reference puts its `rtl` rows near 100 %; the `table` reference is row by row,
which block-based layout analysis does not produce (layout order 84.9 %); `math` references keep the extractor's order for formula parts and are low confidence.
Layout-variant order figures move by a few tenths between runs.
