using System.Diagnostics;

namespace OnePdf.Layout.Tests;

public class TableScaleTests
{
    private static LayoutDocument Big(int rows)
    {
        Table t = TableTests.Make(rows, 4, (r, c) => c == 0 ? r.ToString(System.Globalization.CultureInfo.InvariantCulture) : "cell " + r + " " + c + " some words to wrap", header: 1, footer: 1);
        t.Columns[1] = TableColumn.Auto();
        t.RowBackground = i => i % 2 == 1 ? new LayoutColor(0.95, 0.95, 0.95) : null;
        var doc = new LayoutDocument();
        doc.Pages.Add(new LayoutPage { Content = new Column { Items = { t } } });
        return doc;
    }

    private static (double Ms, long Bytes, int Pages) Time(int rows)
    {
        LayoutDocument doc = Big(rows);
        using var ms = new MemoryStream();
        GC.Collect();
        long before = GC.GetTotalAllocatedBytes(true);
        var sw = Stopwatch.StartNew();
        doc.Save(ms, Support.Fixed);
        sw.Stop();
        using PdfDocument back = PdfDocument.Open(ms.ToArray());
        return (sw.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(true) - before, back.Pages.Count);
    }

    [Fact]
    public void LayoutTimeIsLinearInRows()
    {
        Time(300); // warm up the JIT and the font caches

        // Best of three removes noise from the shared host.
        (double ms1, long mem1, int pages1) = Enumerable.Range(0, 3).Select(_ => Time(1000)).MinBy(r => r.Ms);
        (double ms10, long mem10, int pages10) = Enumerable.Range(0, 3).Select(_ => Time(10_000)).MinBy(r => r.Ms);
        double ratio = ms10 / ms1;
        TestContext.Current.SendDiagnosticMessage($"1k rows: {ms1:0} ms, {mem1 >> 20} MiB allocated, {pages1} pages; 10k rows: {ms10:0} ms, {mem10 >> 20} MiB allocated, {pages10} pages; time ratio {ratio:0.0}, pages/ms {pages10 / ms10:0.000} vs {pages1 / ms1:0.000}");
        Assert.True(pages10 > 200, "pages " + pages10);

        // Linear is 10; a quadratic layout would be near 100.
        Assert.True(ratio < 18, $"time ratio {ratio:0.0}");
        Assert.True(mem10 / (double)mem1 < 18, $"allocation ratio {mem10 / (double)mem1:0.0}");
    }
}
