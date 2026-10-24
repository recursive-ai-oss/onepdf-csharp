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
- **Where a cold document goes:** `BENCH_RSS_TRACE=1` adds the JIT totals (methods, milliseconds, IL bytes) to an `rss` run, and `BENCH_JIT_LIST=file` writes every method the JIT compiled with its time (tab separated), to see what a first document pays for. The picture the report draws is made on first use, so the other rows' first document does not include building it.
- **Output size:** printed by every runner (`bytes=`).

### Output size

The `table10k` file is the one that stands out. OnePDF writes it tagged (the default): every cell is a `TD` with a `P` inside, so 10,000 rows of 5 cells make about 90,000 structure elements, each a small dictionary. Written as plain objects they were 8.0 MB; the streaming writer now packs dictionaries and arrays into compressed object streams, and the file is 2.0 MB: 1.17 MB of that is the structure tree (about 13 bytes per element), 0.76 MB the 250 page content streams, the rest fonts and page dictionaries. MigraDoc's 1.17 MB file carries no structure tree at all. Removing the `P` inside single-paragraph cells would shave the tree further but would change the tags a screen reader sees, so it stays. The untagged form (`Tagged(false)`) is smaller still. Output size is an information row, not part of the gate.
- **Correctness of the comparison:** `check <dir>` writes every engine's every document and opens it with OnePDF's reader. It prints page count, size and how many probe strings (headings, numbers, the first and last words of a paragraph) the extracted text contains.

### Knuth–Plass line breaking (information row)

`BookBenchmarks.OnePdfKnuthPlass` and `rss onepdf-kp book500` set the same 500-page justified book with `LineBreakMode.KnuthPlass` for every paragraph (no hyphenation, default options). It is not part of the gate, and no competitor does this. Greedy rows are the same code as before: the greedy output is byte-identical (1,567,035 bytes) and the greedy row did not slow down.

Measured locally, not on the benchmark host: AMD Ryzen 9 7950X3D (32 threads), .NET SDK 10.0.400, runtime 10.0.11, BenchmarkDotNet 0.15.8 (6 iterations, 1 warm-up). The machine was shared and busy (load average 15 to 25 on 32 threads, one other engineer building), so the times are noisy: the two BenchmarkDotNet runs of the pair differ by up to 20 %. Read the allocation and the ratios, not the milliseconds.

| Row | Mean, run 1 | Mean, run 2 | Allocated | Peak RSS (3 cold runs) | Output |
|---|---:|---:|---:|---:|---:|
| greedy, before this change | 409.7 ms | 381.3 ms | 215.46 MB | 107–110 MB | 1,567,035 B |
| greedy, after | 506.1 ms | 417.6 ms | 215.51 MB | 106–110 MB | 1,567,035 B |
| Knuth–Plass | 584.0 ms | 419.9 ms | 261.43 MB | 110–112 MB | 1,553,276 B |

Knuth–Plass costs about 21 % more allocation (arrays of the paragraph's stretch and shrink, and the search nodes) and between no measurable and about 15 % more time on this host. It is a paragraph-local search, so a longer book costs proportionally more and the memory stays flat per paragraph. Re-measure on the benchmark host before quoting the milliseconds.

## Running

    dotnet build -c Release bench/OnePdf.Layout.Benchmarks
    dotnet bench/OnePdf.Layout.Benchmarks/.../OnePdf.Layout.Benchmarks.dll check out/        # outputs + spot check
    (cd bench/OnePdf.Layout.Benchmarks && dotnet <dll> --filter '*Report*')                     # BenchmarkDotNet; run from this folder
    dotnet <dll> rss onepdf table10k                                                           # one cell, for /usr/bin/time -v
    dotnet <dll> profile table10k                                                              # where OnePDF's time and allocation go
    bench/OnePdf.Layout.Benchmarks/rss.sh results.csv                                          # all cells

`worker-run.sh <out-dir>` is the whole measured run on a shared benchmark machine: it takes the host's benchmark lock, builds, records machine and load every 15 s, runs the check, BenchmarkDotNet and `rss.sh`. Set `ONEFONT_PROJECT` to the OneFont project file if it is not beside the repository.
