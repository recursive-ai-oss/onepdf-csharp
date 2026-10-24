using System;
using System.IO;
using BenchmarkDotNet.Attributes;

namespace LayoutBench;

// One class per row. Every benchmark writes the whole document to a stream that counts and discards, and returns the byte count so the
// output size is part of the result. Parameterised by engine would hide the baseline; the engines are methods and OnePDF is the baseline.
// Slow rows (hundreds of pages) run one operation per iteration; BenchmarkDotNet picks that by itself.

public abstract class RowBenchmarks(Workload row)
{
    private readonly OnePdfEngine _onepdf = new();
    private readonly MigraDocEngine _migradoc = new();
    private readonly ITextEngine _itext = new();

    private long Run(IEngine e)
    {
        var s = new CountingStream();
        e.Render(row, s);
        return s.Count;
    }

    [Benchmark(Baseline = true)]
    public long OnePdf() => Run(_onepdf);

    [Benchmark]
    public long MigraDoc() => Run(_migradoc);

    [Benchmark]
    public long IText() => Run(_itext);
}

[MemoryDiagnoser]
[BenchmarkCategory("layout", "invoice")]
public class InvoiceBenchmarks() : RowBenchmarks(Workload.Invoice);

[MemoryDiagnoser]
[BenchmarkCategory("layout", "report")]
[SimpleJob(warmupCount: 2, iterationCount: 10)]
public class ReportBenchmarks() : RowBenchmarks(Workload.Report50);

[MemoryDiagnoser]
[BenchmarkCategory("layout", "table")]
[SimpleJob(warmupCount: 1, iterationCount: 6)]
public class TableBenchmarks() : RowBenchmarks(Workload.Table10k);

[MemoryDiagnoser]
[BenchmarkCategory("layout", "book")]
[SimpleJob(warmupCount: 1, iterationCount: 6)]
public class BookBenchmarks() : RowBenchmarks(Workload.Book500)
{
    private readonly OnePdfEngine _knuthPlass = new(global::OnePdf.Layout.LineBreakMode.KnuthPlass);

    /// <summary>Information row: the same book with every paragraph broken by total fit.</summary>
    [Benchmark]
    public long OnePdfKnuthPlass()
    {
        var s = new CountingStream();
        _knuthPlass.Render(Workload.Book500, s);
        return s.Count;
    }
}

[MemoryDiagnoser]
[BenchmarkCategory("layout", "mixed")]
[SimpleJob(warmupCount: 2, iterationCount: 10)]
public class MixedBenchmarks() : RowBenchmarks(Workload.Mixed20);

/// <summary>32 invoices produced at the same time by one engine in one process.</summary>
[MemoryDiagnoser]
[BenchmarkCategory("layout", "throughput")]
[SimpleJob(warmupCount: 2, iterationCount: 10)]
public class ThroughputBenchmarks
{
    private readonly OnePdfEngine _onepdf = new();
    private readonly MigraDocEngine _migradoc = new();
    private readonly ITextEngine _itext = new();

    private static long Run(IEngine e)
    {
        long total = 0;
        e.Throughput(32, () => new SizeSink(n => System.Threading.Interlocked.Add(ref total, n)));
        return total;
    }

    [Benchmark(Baseline = true)]
    public long OnePdf32() => Run(_onepdf);

    [Benchmark]
    public long MigraDoc32() => Run(_migradoc);

    [Benchmark]
    public long IText32() => Run(_itext);
}
