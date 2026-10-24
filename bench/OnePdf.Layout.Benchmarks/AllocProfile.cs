using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;

namespace LayoutBench;

/// <summary>
/// <c>alloc &lt;row&gt;</c>: which types a row allocates, from the runtime's allocation-tick events (one sample per ~100 KB, so the figures are
/// estimates that add up). Used to find what the layout allocates, not to measure it.
/// </summary>
internal sealed class AllocListener : EventListener
{
    private readonly Dictionary<string, long> _bytes = [];

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
        {
            EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName is null || !e.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal) || e.Payload is null)
        {
            return;
        }

        string type = "?";
        long amount = 100_000;
        for (int i = 0; i < e.PayloadNames!.Count; i++)
        {
            if (e.PayloadNames[i] == "TypeName")
            {
                type = e.Payload[i]?.ToString() ?? "?";
            }
            else if (e.PayloadNames[i] == "AllocationAmount64")
            {
                amount = Convert.ToInt64(e.Payload[i], System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        lock (_bytes)
        {
            _bytes[type] = _bytes.GetValueOrDefault(type) + amount;
        }
    }

    public void Print(int top)
    {
        lock (_bytes)
        {
            long total = _bytes.Values.Sum();
            Console.WriteLine($"sampled total {total / 1048576.0:F0} MB");
            foreach (KeyValuePair<string, long> kv in _bytes.OrderByDescending(k => k.Value).Take(top))
            {
                Console.WriteLine($"{kv.Value / 1048576.0,8:F1} MB {kv.Value * 100.0 / total,5:F1}%  {kv.Key}");
            }
        }
    }
}
