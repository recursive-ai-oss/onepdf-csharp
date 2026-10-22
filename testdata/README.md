# Test data

| Folder | What | License | Manifest |
|---|---|---|---|
| `generated/` | Files this project generated with exact truth sidecars; sources and the generator script are checked in | our own work (`MIT OR Apache-2.0`) | `generated/README.md` |
| `pdf20examples/` | The PDF Association's PDF 2.0 example files, **unmodified** | CC BY-SA 4.0 (their `LICENSE.md` is alongside) | `MANIFEST.csv` |
| `verapdf/` | A feature-diverse selection from the veraPDF corpus, **unmodified** | CC BY 4.0 | `MANIFEST.csv` |
| `govdocs/` | A small selection from GovDocs1 whose original URL is a US federal site, **unmodified** | US government works (17 U.S.C. 105) | `MANIFEST.csv` (URL and SHA-1 per file) |

Rules:

- Files under `pdf20examples/`, `verapdf/` and `govdocs/` are read by tests and never rewritten. A modified derivative stays local-only.
- No test data goes into any NuGet package; `tools/corpus/check_nupkg.sh` checks the packed files in CI.
- `MANIFEST.csv` lists every redistributed file with source URL, upstream commit or date, license, SHA-256 and attribution. `python3 tools/corpus/build_manifest.py --check` verifies it against the tree.
- The big local-only sets (GovDocs1 and SafeDocs samples, the full veraPDF corpus) are fetched by `tools/corpus/fetch_local_corpus.py` into a cache directory and are never committed.
