# Layout benchmarks

BenchmarkDotNet suite and peak-memory runner that compare OnePDF Layout with **MigraDoc** (PDFsharp-MigraDoc 6.x) on the same documents, with **iText** as an information-only row. Every competitor is used as a compiled NuGet package through its public API, in this project only. The library projects reference none of them.

This project is in `OnePdf.slnx` so it keeps compiling; it is never packed or run in CI.

## Licences, please read before running

This is a test harness. It is never packed, never distributed, and is not a combined work shipped with OnePDF: the competitor packages are restored on the machine that runs it and nowhere else.

| Package | Version | Licence | Where to read it |
|---|---|---|---|
| PDFsharp-MigraDoc | 6.2.4 | MIT | <https://www.nuget.org/packages/PDFsharp-MigraDoc> (licence expression on the package page), <https://docs.pdfsharp.net/> |
| iText (iText Core) | 9.8.0 | **AGPL-3.0** or a commercial licence | <https://itextpdf.com/how-buy/AGPLv3-license>, <https://www.gnu.org/licenses/agpl-3.0.html>, <https://itextpdf.com/how-buy> |

- **iText is AGPL-3.0 (or commercial).** Its row is information only. Run the harness for your own measurement and do not ship the binaries it produces.
- The generated PDFs are our own test content. They are not committed.

Of the iText family, the "iText 7" line is the one the project's plan names; the 9.x packages are the same Core library and layout API, and are the current release.

## Workloads

The same generated content (`Content.cs`: fixed-seed text, table data, a 240 × 140 RGB PNG, the Liberation faces from the Fonts package and two Noto test subsets) goes through each engine's own elements.

| Row | Content | Page |
|---|---|---|
| invoice | letterhead, bill-to block, 14-line table, totals | A4, 1 page |
| report50 | 50 sections, each: heading, justified paragraph, 10-row table, picture, paragraph, forced page break; header; footer with "Page X of Y" | A4, 50 pages |
| table10k | one table, 10,000 rows × 4 columns, header row repeated | A4, about 250 pages |
| book500 | 1,900 justified paragraphs, 11 pt serif, first-line indent | A5, about 500 pages |
| mixed20 | Latin, Arabic (right to left) and Devanagari paragraphs in rotation | A4, about 20 pages |
| throughput32 | 32 invoices produced at the same time in one process | |

Page counts of `book500`, `mixed20` and `table10k` differ a little between engines, because each engine breaks lines and pages by its own rules. The check run prints each engine's count.

### What an engine cannot express here, and what stands in

- **Complex scripts.** OnePDF shapes Arabic and Devanagari and mirrors right-to-left content. MigraDoc and iText (without its separate shaping add-on) draw the characters unshaped: for them `mixed20` measures layout of the same characters, not the same glyphs. The check run reports the engines whose output text does not contain the Arabic probe.
- **NativeAOT.** The cold first-document row runs OnePDF as a NativeAOT program (`OnePdf.Layout.Benchmarks.Aot`). The competitors use reflection or native libraries and are measured as ordinary managed processes only.
- **Picture reuse.** The report puts one picture on 50 pages. OnePDF's element is shared, so the image is stored once. Output size shows that iText stores a copy for every use.

### Fonts

Every engine gets the same Liberation Sans/Serif files (regular, bold, italic, bold italic) and the two Noto subsets, registered explicitly. All three embed subsets of the glyphs used (OnePDF and iText as CID TrueType with Identity-H and ToUnicode; MigraDoc as simple TrueType with WinAnsi encoding and no ToUnicode, so it cannot draw the Arabic and Devanagari characters at all).

## Measures

- **Time and allocation:** BenchmarkDotNet with `[MemoryDiagnoser]`. Each document is written to a stream that discards and counts. The managed allocation figure does not include native memory, which is why peak RSS is measured too.
- **Peak RSS and cold time:** `rss.sh` runs each (engine, row) cell in a fresh process under `/usr/bin/time -v`, three repetitions. The wall time is the first document in the process, JIT included.
- **Output size:** printed by every runner (`bytes=`).
- **Correctness of the comparison:** `check <dir>` writes every engine's every document and opens it with OnePDF's reader. It prints page count, size and how many probe strings (headings, numbers, the first and last words of a paragraph) the extracted text contains.

## Running

    dotnet build -c Release bench/OnePdf.Layout.Benchmarks
    dotnet bench/OnePdf.Layout.Benchmarks/.../OnePdf.Layout.Benchmarks.dll check out/        # outputs + spot check
    (cd bench/OnePdf.Layout.Benchmarks && dotnet <dll> --filter '*Report*')                     # BenchmarkDotNet; run from this folder
    dotnet <dll> rss onepdf table10k                                                           # one cell, for /usr/bin/time -v
    dotnet <dll> profile table10k                                                              # where OnePDF's time and allocation go
    bench/OnePdf.Layout.Benchmarks/rss.sh results.csv                                          # all cells

`worker-run.sh <out-dir>` is the whole measured run on a shared benchmark machine: it takes the host's benchmark lock, builds, records machine and load every 15 s, runs the check, BenchmarkDotNet and `rss.sh`. Set `ONEFONT_PROJECT` to the OneFont project file if it is not beside the repository.
