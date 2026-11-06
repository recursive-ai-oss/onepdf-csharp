# Test data

| Folder | What | License | Manifest |
|---|---|---|---|
| `generated/` | Files this project generated with exact truth sidecars; sources and the generator script are checked in | our own work (`MIT OR Apache-2.0`) | `generated/README.md` |
| `pdf20examples/` | The PDF Association's PDF 2.0 example files, **unmodified** | CC BY-SA 4.0 (their `LICENSE.md` is alongside) | `MANIFEST.csv` |
| `verapdf/` | A feature-diverse selection from the veraPDF corpus, **unmodified** | CC BY 4.0 | `MANIFEST.csv` |
| `fonts/` | Subsets of two Noto Sans fonts for the glyph-run tests | SIL OFL 1.1 (`OFL.txt` alongside) | `fonts/README.md` |
| `repros/` | Minimized files that reproduced a bug, rebuilt by hand or with a black-box tool from our own generated files | our own work (`MIT OR Apache-2.0`) | below |
| `realworld/` | Real-world PDFs for the extraction-quality set, **unmodified**, with reference text (public-domain and CC BY sources only) | US government works and CC BY 4.0, per file | `MANIFEST.csv`, `realworld/README.md` |
| `govdocs/` | A small selection from GovDocs1 whose original URL is a US federal site, **unmodified** | US government works (17 U.S.C. 105) | `MANIFEST.csv` (URL and SHA-1 per file) |
| `layout-references/` | Documents written by the layout package and PNG pictures of their pages, for noticing layout changes | our own work (`MIT OR Apache-2.0`) | `layout-references/png/manifest.csv`, `layout-references/README.md` |

Rules:

- Files under `pdf20examples/`, `verapdf/`, `govdocs/` and `realworld/pdf/` are read by tests and never rewritten. A modified derivative stays local-only.
- No test data goes into any NuGet package; `tools/corpus/check_nupkg.sh` checks the packed files in CI.
- `MANIFEST.csv` lists every redistributed file with source URL, upstream commit or date, license, SHA-256 and attribution. `python3 tools/corpus/build_manifest.py --check` verifies it against the tree.
- The big local-only sets (GovDocs1 and SafeDocs samples, the full veraPDF corpus) are fetched by `tools/corpus/fetch_local_corpus.py` into a cache directory and are never committed. The local-only part of the real-world quality set is fetched by `tools/corpus/fetch_realworld.py`.

`repros/`:

- `encrypted-objstm-aes128.pdf` — `raw-base-objstm.pdf` encrypted by qpdf (AES-128, user password `up`, owner password `op`) keeping its object stream. The object stream is encrypted, so reading its header before authentication fails with an invalid `/N` or `/First`; the reader now authenticates first.
- `structure-parent-tree-null-entry.pdf` — a tagged file whose parent tree lists a key with a null value; moving content out of an element into another one tried to reuse that entry and failed with a null reference (found by fuzzing the structure tree; a mutated copy of a committed file).
- `appearance-short-line-ending-array.pdf` — a Line and a PolyLine whose `/LE` array has fewer than two names; the appearance generator indexed past the end of the array (found by fuzzing). Assembled by hand.
- `save-incremental-junk-prefix-offset.pdf` — `raw-broken-junk-before-header.pdf` after a byte-level mutation. Saved incrementally, the new cross-reference section landed one byte past a place where an object header started, and the reader took the unshifted reading of `startxref` before the header-relative one. Found by the `save` fuzz target.
- `markup-dangling-annots-beyond-object-limit.pdf` — a page whose `/Annots` points at objects 4, 5 and 6, which the file never defines, and a catalog entry that points past the object limit. The far reference switched off the guard that keeps new objects from taking a pointed-at number, so a markup annotation added to the page was given number 4 and listed twice. Found by the `markup` fuzz target and reduced by hand.
- `page-tree-w9-damaged.pdf` — the IRS Form W-9 (public domain, source URL and the original SHA-256 in `MANIFEST.csv`) with 4,096 bytes from offset 40600 overwritten with `X`. That destroys page object 5, so open recovers 5 of 6 pages while the page tree still lists six; the test checks the lost-page report and that a full save writes a tree with `/Count 5` and no null kids. The only change to the original is the overwrite.
- `rtl-bidi-logical-stream.pdf`, `rtl-bidi-visual-stream.pdf` — the same page of Arabic and Hebrew lines (left-to-right lines with quoted runs, right-to-left lines with an English word, numbers and brackets), with the glyphs shown in logical order and in visual order. `rtl-tatweel.pdf` — tatweel glyphs stretched past their width and a position-only kashida. `rtl-zwnj.pdf` — Persian ZWNJ as a zero-width glyph, in a letter's ToUnicode text and in `/ActualText` only. `emoji-zwj.pdf` — an emoji ZWJ sequence as three glyphs, as `/ActualText` and as one glyph. Hand-written by `BidiFixtures` in `tests/OnePdf.Tests/Extraction` (a Type 3 font of plain boxes with a `/ToUnicode` map, so no font program is involved; `ONEPDF_REGEN_FIXTURES=1` rewrites them); read by `BidiExtractionTests`.
