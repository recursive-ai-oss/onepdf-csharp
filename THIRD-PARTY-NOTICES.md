# Third-party notices

OnePDF's own code is licensed `MIT OR Apache-2.0`. Material from other projects that ships with the packages is listed here.

## Core library (`RecursiveAI.OnePdf`)

None yet.

## Fonts package (`RecursiveAI.OnePdf.Fonts`)

None yet.

## Test data (not shipped)

None of this is in any package (`tools/corpus/check_nupkg.sh` checks the packed files). Every redistributed file is listed with source URL, upstream commit, license and SHA-256 in `testdata/MANIFEST.csv`.

- `testdata/pdf20examples/` — PDF Association, *pdf20examples* (https://github.com/pdf-association/pdf20examples). "The PDF Association provides these example PDF 2.0 files under the Creative Commons Attribution-ShareAlike 4.0 International (CC BY-SA 4.0) license." Files are unmodified and kept apart from the library code; the library does not incorporate or derive from them. Their `LICENSE.md` sits in that folder.
- `testdata/verapdf/` — veraPDF Consortium, *veraPDF corpus* (https://github.com/veraPDF/veraPDF-corpus), Creative Commons Attribution 4.0 International (CC BY 4.0). Selected files, unmodified.
- `testdata/govdocs/` — selected files from the GovDocs1 corpus whose original URL is a United States federal site; works of the US government (17 U.S.C. 105).
- `testdata/generated/` — produced by this project's `tools/corpus/generate.py`; our own work. Chrome, LibreOffice, TeX and qpdf were used as tools; fonts embedded in the output PDFs come from fonts installed on the generating machine (Liberation, DejaVu, Noto, all under the SIL Open Font License or the Bitstream Vera license, which permit embedding in documents).
