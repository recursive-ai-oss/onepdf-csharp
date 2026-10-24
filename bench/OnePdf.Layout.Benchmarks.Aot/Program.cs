using System;
using System.Diagnostics;
using LayoutBench;

// Same output line as the managed program's "rss" mode: ok <engine> <row> bytes=<n> wall_ms=<n>.
// usage: <invoice|report50|table10k|book500|mixed20|throughput32>
string row = args.Length > 0 ? args[0] : "invoice";
var engine = new OnePdfEngine();
var clock = Stopwatch.StartNew();
long bytes = 0;
if (row == "throughput32")
{
    engine.Throughput(32, () => new SizeSink(n => System.Threading.Interlocked.Add(ref bytes, n)));
}
else
{
    var sink = new CountingStream();
    engine.Render(row switch
    {
        "invoice" => Workload.Invoice,
        "report50" => Workload.Report50,
        "table10k" => Workload.Table10k,
        "book500" => Workload.Book500,
        "mixed20" => Workload.Mixed20,
        _ => throw new ArgumentException("unknown row " + row),
    }, sink);
    bytes = sink.Count;
}

Console.WriteLine($"ok onepdf-aot {row} bytes={bytes} wall_ms={clock.Elapsed.TotalMilliseconds:F0}");
