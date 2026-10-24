using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace OnePdf.Layout.Fuzz;

/// <summary>Starting inputs and a local random-mutation mode for the layout target.</summary>
internal static class Seeds
{
    public static int Run(string outDir, string? fontDir)
    {
        Directory.CreateDirectory(outDir);
        var rnd = new Random(7);
        void Put(string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(outDir, name), bytes);
        for (int i = 0; i < 40; i++)
        {
            var script = new byte[40 + rnd.Next(300)];
            rnd.NextBytes(script);
            byte flags = (byte)(rnd.Next(64) & ~1);
            Put($"plain-{i:D2}.bin", [flags, (byte)script.Length, (byte)(script.Length >> 8), .. script]);
        }

        // Colour fonts: the valid COLR (version 0 and 1) and CPAL tables as the starting point for mutation, with flag 64 (and 128 for version 1).
        foreach (bool v1 in new[] { false, true })
        {
            byte[] colr = OnePdf.Layout.Tests.ColorFont.BuildColr(v1);
            byte[] cpal = OnePdf.Layout.Tests.ColorFont.BuildCpal();
            int total = colr.Length + cpal.Length;
            int split = Enumerable.Range(0, 256).First(b => b * total / 256 >= colr.Length);
            for (int i = 0; i < 10; i++)
            {
                var script = new byte[60 + rnd.Next(200)];
                rnd.NextBytes(script);
                byte flags = (byte)((rnd.Next(64) & ~1) | 64 | (v1 ? 128 : 0));
                Put($"colour-{(v1 ? 1 : 0)}-{i:D2}.bin", [flags, (byte)script.Length, (byte)(script.Length >> 8), .. script, (byte)split, .. colr, .. cpal]);
            }
        }

        if (fontDir is { Length: > 0 } && Directory.Exists(fontDir))
        {
            int k = 0;
            foreach (string f in Directory.EnumerateFiles(fontDir).Where(p => p.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)))
            {
                var script = new byte[120];
                rnd.NextBytes(script);
                Put($"font-{k++:D2}.bin", [1, (byte)script.Length, 0, .. script, .. File.ReadAllBytes(f)]);
            }
        }

        return 0;
    }

    public static int Mutate(double seconds, IEnumerable<string> seeds)
    {
        var list = new List<byte[]>();
        foreach (string p in seeds)
        {
            foreach (string f in Directory.Exists(p) ? Directory.EnumerateFiles(p) : [p])
            {
                list.Add(File.ReadAllBytes(f));
            }
        }

        var rnd = new Random(99);
        var clock = Stopwatch.StartNew();
        long runs = 0;
        double worst = 0;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            byte[] input = (byte[])list[rnd.Next(list.Count)].Clone();
            int edits = 1 + rnd.Next(6);
            for (int e = 0; e < edits && input.Length > 4; e++)
            {
                input[3 + rnd.Next(input.Length - 3)] = (byte)rnd.Next(256);
            }

            double t0 = clock.Elapsed.TotalSeconds;
            try
            {
                LayoutTarget.Run(input);
            }
#pragma warning disable CA1031 // A fuzz driver records any escaping exception.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                string path = Path.Combine(Environment.GetEnvironmentVariable("ONEPDF_FUZZ_WORK") ?? Path.GetTempPath(), $"layout-crash-{runs}.bin");
                File.WriteAllBytes(path, input);
                Console.WriteLine($"CRASH {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\nsaved {path}");
                return 1;
            }

            worst = Math.Max(worst, clock.Elapsed.TotalSeconds - t0);
            runs++;
        }

        Console.WriteLine($"runs={runs} slowest={worst:F3}s crashes=0");
        return 0;
    }
}
