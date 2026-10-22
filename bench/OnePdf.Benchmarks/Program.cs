using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace OnePdf.Benchmarks;

/// <summary>Placeholder benchmark that proves the harness runs; real benchmarks replace it.</summary>
[MemoryDiagnoser]
public class LibraryNameBenchmarks
{
    private readonly string _suffix = "-bench";

    [Benchmark]
    public int NameLength() => (Library.Name + _suffix).Length;
}

internal static class Program
{
    private static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
