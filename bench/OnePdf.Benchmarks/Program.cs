using System;
using System.Linq;
using System.Reflection;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Running;

namespace OnePdf.Benchmarks;

/// <summary>Leaves out benchmarks marked <see cref="PendingAttribute"/> whose prerequisite is missing.</summary>
internal sealed class SkipPendingFilter : IFilter
{
    public bool Predicate(BenchmarkCase benchmarkCase) =>
        benchmarkCase.Descriptor.WorkloadMethod.GetCustomAttribute<PendingAttribute>() is not { IsMet: false };
}

internal static class Program
{
    private static int Main(string[] args)
    {
        // rss <engine> <row> <file name> [degree] runs one operation once for the peak-memory script (bench/rss.sh).
        if (args.Length > 0 && args[0] == "rss")
        {
            return PeakMemoryRunner.Run(args);
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
            .Run(args, DefaultConfig.Instance.AddFilter(new SkipPendingFilter()));
        return 0;
    }
}
