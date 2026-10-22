using System;
using System.Collections.Generic;
using OnePdf;
using OnePdf.Fuzz;
using SharpFuzz;

// One fuzz executable, one target per input surface. The target is chosen by the ONEPDF_FUZZ_TARGET environment
// variable because libFuzzer's .NET host starts the program without command-line arguments.
//
// To add a target: write a static method `Run(ReadOnlySpan<byte> data)` that lets only the library's typed exceptions
// escape, then add one line to `Targets` below.
internal static class Program
{
    private delegate void Target(ReadOnlySpan<byte> data);

    private static readonly Dictionary<string, Target> Targets = new(StringComparer.Ordinal)
    {
        ["noop"] = static data => _ = Library.Name.Length + data.Length,
        ["filters"] = FilterTarget.Run,
        ["security"] = SecurityTarget.Run,
    };

    private static int Main(string[] args)
    {
        string? name = Environment.GetEnvironmentVariable("ONEPDF_FUZZ_TARGET") ?? (args.Length > 0 ? args[0] : null);
        if (name is null || !Targets.TryGetValue(name, out Target? target))
        {
            Console.Error.WriteLine("usage: set ONEPDF_FUZZ_TARGET to one of: " + string.Join(", ", Targets.Keys));
            return 2;
        }

        Fuzzer.LibFuzzer.Run(data => target(data));
        return 0;
    }
}
