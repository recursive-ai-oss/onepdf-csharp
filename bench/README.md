# Benchmarks

BenchmarkDotNet suite for the performance gate: OnePDF against PdfPig (the NuGet package, used as a black box) on the same files in the same process, with `[MemoryDiagnoser]`. PDFium (through Docnet.Core) and `mutool` (when installed) appear in information-only rows. The competitor packages are referenced by this project only.

## Inputs

Small inputs come from `testdata/`; the large ones are generated into a scratch directory (about 30 MB) by

    python3 tools/corpus/make_bench_inputs.py $ONEPDF_BENCH_DATA     # needs tectonic, qpdf and Chrome on PATH (or TECTONIC, QPDF, CHROME)

| Name | File | What |
|---|---|---|
| `small` | `testdata/generated/pdf/lo-report.pdf` | 2 pages, LibreOffice |
| `small-metadata` | `testdata/generated/pdf/raw-metadata.pdf` | Info and XMP |
| `pages-1000` | `pages-1000.pdf` | 1,000 pages of Helvetica text, hand-assembled |
| `broken` | `born-300-broken.pdf` | the 300-page file with its cross-reference table and `startxref` damaged: objects found by scanning |
| `born-300` | `born-300.pdf` | 300-page TeX article, embedded fonts, outline, Info |
| `aes256` | `born-300-aes256.pdf` | `born-300` encrypted by qpdf, AES-256, user password `bench` |
| `scan-ocr` | `scan-ocr-40.pdf` | 40 pages: a full-page grey image plus an invisible (render mode 3) text layer |
| `cjk` | `cjk-120.pdf` | 120 pages of Japanese, Chinese and Korean text, Chrome with system CJK fonts |
| `vector-page` | `vector-page.pdf` | one page, about 60,000 path segments |
| `text-page` | `text-page.pdf` | one page, about 6,000 words |

`rss check` (below) opens every input with both libraries and prints page counts, to catch a missing or damaged file before a run.

## Rows

Gate rows (`--anyCategories gate`): open + page count (`OpenBenchmarks`: small, pages-1000, broken); metadata (`MetadataBenchmarks`); full text (`TextBenchmarks`: born-300, scan-ocr, cjk); words with positions (`WordsBenchmarks`: born-300, text-page); AES-256 open and text (`EncryptedOpenBenchmarks`, `EncryptedTextBenchmarks`). Text, words and encrypted text run at degree `1` and `default` (min(4, cores)), shown as separate rows; `TextBenchmarks` on `born-300` at `default` is the whole-document parallel row. Open and metadata work is single-threaded in both libraries and has no degree. PdfPig has no parallel mode: at `default` each worker opens its own copy of the file and extracts a contiguous run of pages.

Information rows (`--anyCategories info`): `InterpretBenchmarks` (one page into a display list: vector-heavy and text-heavy), `OtherEnginesTextBenchmarks` (PDFium through Docnet.Core; `mutool draw -F txt` including process start, skipped when `mutool` is not on PATH).

OnePDF text and words rows are marked `[Pending("text")]` and left out of runs until the extraction API is wired into `TextSeam.cs` (two methods and one flag). Nothing else changes when it is.

## Running

    export ONEPDF_BENCH_DATA=/scratch/benchdata ONEPDF_TESTDATA=$PWD/testdata
    cd bench/OnePdf.Benchmarks
    dotnet run -c Release -- --anyCategories gate                 # real run: needs a quiet machine
    dotnet run -c Release -- --anyCategories gate info --job dry  # proves everything executes; its numbers mean nothing
    dotnet run -c Release -- --filter '*TextBenchmarks*'          # one class

Peak memory, one fresh process per row under `/usr/bin/time -v`:

    bench/rss.sh $SCRATCH/rss.csv         # also: dotnet OnePdf.Benchmarks.dll rss <engine> <row> <input> [1|default]

The `rss` figure includes the .NET runtime baseline (the same for every engine) and is one cold run per row. Record `uptime` and the SDK and runtime versions beside any numbers; they are local measurements on a shared machine.
