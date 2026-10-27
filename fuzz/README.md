# Fuzzing

One executable (`fuzz/OnePdf.Fuzz`) holds one target per input surface. SharpFuzz instruments `OnePdf.dll`; libFuzzer drives it.

| Target | Input | Surface |
|---|---|---|
| `parser` | bytes | lexer and object parser |
| `document` | a PDF file | open, xref reading and repair, every object and stream, revisions |
| `model` | a PDF file | the typed layer: pages, annotations, outlines, labels, metadata, embedded files, form |
| `content` | a PDF file, or a bare content stream placed in a page with resources of every kind | interpreter into the display list, replay |
| `appearance` | a selector byte (annotation subtype or field type), then dictionary entries | generated widget and markup appearances, drawn through the interpreter; then fill, flatten, save and reopen |
| `filters` | 6 parameter bytes, then an encoded stream | Flate, LZW, ASCIIHex, ASCII85, RunLength, predictors |
| `ccitt` | 6 parameter bytes, then an encoded stream | CCITTFaxDecode |
| `security` | encryption-dictionary values, a document ID, a password | standard security handler R2–R6 |
| `pdffont` | a PDF file | PDF font dictionaries: every font of every page, whole code range through metrics, Unicode, outlines |
| `png` | a mode byte (bit 0: recompute chunk CRCs), then a PNG file | the minimal PNG reader: every colour type and depth, Adam7, tRNS, iCCP, bad CRCs and sizes; the image is added, drawn, saved, reopened and interpreted |
| `redact` | 20 parameter bytes (options, four areas), then a PDF file or a bare content stream | redaction: rewriting page, form and annotation content, image blanking, annotations, metadata; full save; reopen; no glyph left inside an area |
| `tag` | 2 parameter bytes (what to tag: every block, every second block, single words, a graphics area, header/footer artifacts, the happy path with roles by block number), then a PDF file or a bare content stream | tagging an untagged document: extraction with glyphs, the plan, rewriting page and form content, structure tree; full save; reopen; the extracted text must be unchanged |
| `fontsubset` | an option byte, 8 character picks, then a TrueType/OpenType font | adding a font to a new document, drawing with it, subsetting on save (TrueType `glyf` and CFF), reading the subset back |
| `fontprogram` | a mode byte, then a font program | Type 1, CFF, TrueType/OpenType outlines and cmaps; mode 5 runs the TrueType instruction interpreter on any TrueType font |
| `cmap` | a flag byte, then CMap text | CMap parser and lookups |
| `shading` | 12 parameter bytes (type, bit widths, colour space, geometry), then mesh data | shading types 1 to 7: mesh decoding, tessellation, evaluators |
| `type4` | 2 parameter bytes, then program text | PostScript calculator functions |
| `save` | a PDF (seeded from the test files) | open for editing, change objects, full and incremental save, reopen what was written |
| `signature` | a mode byte, then a CMS blob or a whole PDF | CMS and ASN.1 parsing, certificates, signature and time-stamp checks, chain building, byte ranges, change analysis |
| `pdfa` | a PDF (seeded from the test files) | PDF/A check, preparation and save; a file that is written must pass the checker |
| `extraction` | an options byte, then 10-byte glyph records | text layout, plain-text modes, extraction JSON, bidirectional reordering |
| `svg` | an SVG document (any bytes; the committed sample SVGs seed it) | an SVG picture in `OnePdf.Layout` (same project as `layout`, picked by the target name): parse, lay out tagged or untagged, save twice (same bytes), reopen and extract; limits tightened so one input stays cheap |
| `layout` | a flag byte (hostile font, right-to-left, untagged, justified, landscape, debug), a script length, a script that chooses a random element tree, its text and settings, then optionally a font file | `OnePdf.Layout` (separate project `fuzz/OnePdf.Layout.Fuzz`, built with `OnePdf.slnx`): laying out and saving; a second run must give the same bytes, the file must reopen, and each text token must be extracted once |

Only the library's typed exceptions (and `OperationCanceledException`) may come out of a target. Any other exception, a hang (`-timeout`) or a memory blow-up (`-rss_limit_mb`) is a finding.

## Running

    export ONEPDF_FUZZ_TOOLS=/path/to/tools     # holds fuzz/libfuzzer-dotnet and sharpfuzz/sharpfuzz
    export ONEPDF_FUZZ_WORK=/path/to/workdir    # builds, corpora, findings; must be outside the checkout
    fuzz/run.sh parser 600                      # fuzz the parser for 10 minutes
    fuzz/run.sh document 600 /path/to/pdfs      # same, with an extra read-only seed directory
    fuzz/run.sh replay document some-input...   # run saved inputs once, without libFuzzer

`run.sh` builds the harness in release mode into `$ONEPDF_FUZZ_WORK/bin`, writes seeds into `$ONEPDF_FUZZ_WORK/corpus/<target>/`, instruments `OnePdf.dll`, and runs libFuzzer. Findings land in `$ONEPDF_FUZZ_WORK/findings/<target>/`.
Seeds: the PDFs under `testdata/` for the file targets; hand-built inputs in each target's layout for the others; fonts and CMaps cut out of the test PDFs for `fontprogram` and `cmap`; `.ttf`/`.otf` files from `$FUZZ_FONTS` when set. The optional third argument is another directory of seeds (for example a sample of a local corpus); libFuzzer reads it but writes new inputs only to the work corpus.
Per-input timeout and memory limit: `FUZZ_TIMEOUT` (10 s) and `FUZZ_RSS_MB` (1024). Extra arguments after the corpus directory go to libFuzzer. Run one fuzz process at a time.

The tools:

* `libfuzzer-dotnet`: the C++ host from the SharpFuzz project, compiled with `clang++ -fsanitize=fuzzer`.
* `sharpfuzz`: `dotnet tool install SharpFuzz.CommandLine --tool-path <dir>/sharpfuzz`.

Reading the output: `ft` (features) is the number to watch. The host's own `cov` stays small because only the .NET side is instrumented.

Without a libFuzzer host the executable has local modes, run as `dotnet OnePdf.Fuzz.dll <mode>`: `mutate <target> <seconds> <seed>...` (random mutation in process; a crashing input is saved under `$ONEPDF_FUZZ_WORK/findings/mutate`), `replay`, `seeds`, `open-dir <dir>` and `interpret-dir <dir>` (sweeps over a directory of PDFs).

## Adding a target

1. Write `Run(ReadOnlySpan<byte> data)` that lets only the library's typed exceptions escape.
2. Add one line to `Targets` in `Program.cs`, one `case` in `run.sh` with its maximum input length, and seeds in `Seeds.cs`.

## Findings

Minimize the input (shorten it while `run.sh replay <target> file` still fails), save it as `testdata/repros/<area>-<what>.<ext>` (under 1 MB, built from our own files; never from a third-party file), add a test in `tests/OnePdf.Tests` that opens or feeds it and asserts a typed result, and describe it in the `repros/` list in `testdata/README.md`.
