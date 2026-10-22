# Generated test set

PDFs produced by `tools/corpus/generate.py`, each with exact truth. These are our own works (`MIT OR Apache-2.0`).

- `src/` — the sources: HTML (Chrome print-to-PDF), flat ODF text documents (LibreOffice), LaTeX (tectonic).
- `pdf/` — the output PDFs. Chrome, LibreOffice, TeX and qpdf stamp dates or random ids, so regenerating gives equivalent, not byte-identical, files; the committed files are the reference. The `raw-*` files are assembled by the script itself and are byte-for-byte reproducible.
- `truth/<name>.json` — one sidecar per PDF.

Regenerate: `python3 tools/corpus/generate.py` (needs `google-chrome`, `soffice`, `qpdf`, `tectonic` on `PATH`, or `CHROME`, `SOFFICE`, `QPDF`, `TECTONIC`; missing tools skip their cases; `--only PREFIX` rebuilds a subset). `qpdf` is used as a black-box binary only, to encrypt, rotate and repack. Validity check: `tools/corpus/check_generated.sh` converts every file with `pdftotext` (a sanity check, never truth).

## Truth sidecar (schema 1)

```json
{ "schema": 1, "name": "…", "generator": "tool and version", "pageCount": 2,
  "pages": [ { "text": "…" } ],
  "passwords": { "user": "…", "owner": "…" },
  "category": "…", "notes": "…" }
```

- `pages[i].text` is the source text of page *i* in intended reading order, written in logical order (RTL and vertical text included). `\n\n` separates blocks (paragraphs, headings); `\n` separates lines that are lines by construction (table rows, list items, hand-set lines). Line breaks inside a wrapped paragraph are **not** recorded: score on the normalized word sequence, not on line breaks. Ligatures are decomposed (`fi`), annotation and form-field text is not page text.
- `passwords` is present for encrypted files; the empty `user` password means the file opens without one. `securityRevision` names the standard security handler revision.
- Optional exact facts for the document model: `outline` (`[title, pageIndex, children]`), `pageLabels`, `links`, `annotations`, `fields`, `embeddedFiles`, `info`/`xmp`, `rotations`, `revisions`, `furniture` (header/footer lines that repeat on every page).
- `expect` marks repair cases: `recovers-all-text`, `one-page-with-diagnostic`, or `unrecoverable` (pages are empty; the file must not crash the parser). `blackBoxCheck: may-fail` marks files the `pdftotext` check may reject.

## Contents

| Prefix | What |
|---|---|
| `chrome-*` | single, two and three columns; table; Arabic and Hebrew; mixed bidirectional text; Chinese, Japanese, Korean; vertical Japanese; rotated text; ligatures; links and outline; font styles; emoji (embedded as Type 3) |
| `lo-*` | LibreOffice: report with header/footer, list, table, link, bookmarks (plain and tagged PDF); two columns; vertical Japanese; password-protected export (RC4-128, R3) |
| `tex-*` | TeX ligatures and a multi-column page (embedded Type 1C/CFF fonts) |
| `raw-*` | hand-assembled: base file, annotations, AcroForm fields, embedded files, outlines and page labels, Type 3 font, standard-14 fonts and encodings, page rotation/boxes/UserUnit, incremental update, Info and XMP metadata, shadings (axial, radial, function-based, mesh types 4-7) and tiling and shading patterns |
| `raw-broken-*` | repair cases made from `raw-base`: missing trailer, missing xref, bad `startxref`, junk before the header, wrong xref offsets, wrong `/Length` (too long, too short), no `endobj`, truncated, cyclic page tree |
| `enc-*` | the standard security handler: RC4-40 (R2), RC4-128 (R3), AES-128 (R4), AES-256 (R5 and R6); user and owner passwords; empty user password; cleartext metadata |
| `*-objstm`, `*-linearized`, `*-rotated-*` | repacked variants (object streams with an xref stream, linearized, `/Rotate`) |

Not covered: Type 3 from a text producer other than the hand-assembled file and Chrome's emoji (no other installed tool emits it).

## Images

`images/*.jpg` are 16×16 gradients (gray, RGB, CMYK with an Adobe APP14 marker and inverted ink) written by `tools/make_test_jpegs.py` with Pillow. The image tests decode them through a test-only JPEG decoder and compare against the gradient with a tolerance.
