using System;
using System.IO;

namespace LayoutBench;

/// <summary>One engine's way of producing every workload. Output goes to the stream given; engines write the whole document before returning.</summary>
internal interface IEngine
{
    string Name { get; }

    long Render(Workload row, Stream output);

    /// <summary>Produces <paramref name="documents"/> invoices at the same time, each to its own sink stream.</summary>
    void Throughput(int documents, Func<Stream> sink);
}
