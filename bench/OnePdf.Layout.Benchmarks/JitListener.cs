using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;

namespace LayoutBench;

/// <summary>
/// Lists the methods the JIT compiled in this process with the time each took (BENCH_JIT_LIST=file, tab-separated: milliseconds,
/// method), to see what a cold first document spends its start-up on. The listener's own work is in the figures.
/// </summary>
internal sealed class JitListener : EventListener
{
    private readonly List<string> _methods = [];
    private readonly Dictionary<ulong, DateTime> _started = [];

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
        {
            EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x10);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName is null || e.Payload is null || e.PayloadNames is null)
        {
            return;
        }

        if (e.EventName.StartsWith("MethodJittingStarted", StringComparison.Ordinal))
        {
            lock (_methods)
            {
                _started[Convert.ToUInt64(e.Payload[0], CultureInfo.InvariantCulture)] = e.TimeStamp;
            }
        }
        else if (e.EventName.StartsWith("MethodLoadVerbose", StringComparison.Ordinal))
        {
            string ns = "", name = "";
            for (int i = 0; i < e.PayloadNames.Count; i++)
            {
                if (e.PayloadNames[i] == "MethodNamespace") { ns = e.Payload[i]?.ToString() ?? ""; }
                else if (e.PayloadNames[i] == "MethodName") { name = e.Payload[i]?.ToString() ?? ""; }
            }

            lock (_methods)
            {
                double ms = _started.TryGetValue(Convert.ToUInt64(e.Payload[0], CultureInfo.InvariantCulture), out DateTime t0) ? (e.TimeStamp - t0).TotalMilliseconds : -1;
                _methods.Add(ms.ToString("F3", CultureInfo.InvariantCulture) + "\t" + e.TimeStamp.TimeOfDay.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture) + "\t" + ns + "::" + name);
            }
        }
    }

    public void Write(string path)
    {
        lock (_methods) { File.WriteAllLines(path, _methods); }
    }
}
