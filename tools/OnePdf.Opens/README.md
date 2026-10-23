# OnePdf.Opens

Corpus tool with two modes. It references PdfPig and PDFsharp as compiled packages (comparison only; nothing in `src/` depends on them).

```
dotnet OnePdf.Opens.dll opens <out-dir> [--jobs N] [--timeout S] <label>=<dir>...
dotnet OnePdf.Opens.dll sweep <out-dir> [--jobs N] <label>=<dir>...
```

Every file runs in its own child process, so a crash, stack overflow or hang in any engine is a result and not the end of the run. Linux peak RSS comes from `/usr/bin/time -f %M`.

**opens** runs OnePDF, PdfPig and PDFsharp on each file and writes `opens.csv` (one row per file) and `opens-summary.md`.
"Opens" means the document opens, the page count is read and the text of every page is read without an exception. PDFsharp has no text
layer, so for it the page objects are read instead; it is information only. No engine is given a password: each tries the empty password
itself, and a file that still asks for one is classed `password`. When both OnePDF and PdfPig ask, the file is left out of the 2×2
table and counted separately. Child limit: 8 GiB managed heap, `--timeout` seconds (default 120) without a result.

**sweep** is the crash/hang check: open, `ExtractText` for the whole document, then every page interpreted into a `PdfDisplayList`
and replayed. It writes `sweep.csv` and `sweep-summary.md`. A finding is an unexpected exception, a crash, a hang (no progress for
60 s), a page or the text pass over 10 s, or a peak RSS over 1 GiB. Typed `PdfException`s are expected on broken files and only counted.
Images are interpreted without a registered decoder. `--drop-glyphs` runs the text pass with `RetainGlyphs = false`; `ONEPDF_SWEEP_PHASES=text|interpret` limits a child run to one phase (diagnosis).

Both modes pause while a BenchmarkDotNet process is running. Local corpora come from `tools/corpus/fetch_local_corpus.py` and
`tools/corpus/fetch_realworld.py`.
