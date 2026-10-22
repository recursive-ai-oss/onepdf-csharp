using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using OnePdf.Fuzz;

namespace OnePdf.Tests.Functions;

/// <summary>Bounded, deterministic mutation runs of the Type 4 and CCITT fuzz targets, for regressions between real fuzzing campaigns.</summary>
public class MutationSmokeTests
{
    private static string FixtureDir([CallerFilePath] string source = "") => Path.Combine(Path.GetDirectoryName(source)!, "..", "Filters", "ccitt");

    private static void Mutate(List<byte[]> seeds, Action<byte[]> target, int milliseconds, int seed)
    {
        var rnd = new Random(seed);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < milliseconds)
        {
            byte[] m = (byte[])seeds[rnd.Next(seeds.Count)].Clone();
            int edits = rnd.Next(1, 8);
            for (int e = 0; e < edits; e++)
            {
                switch (rnd.Next(4))
                {
                    case 0:
                        m[rnd.Next(m.Length)] = (byte)rnd.Next(256);
                        break;
                    case 1:
                        m[rnd.Next(m.Length)] ^= (byte)(1 << rnd.Next(8));
                        break;
                    case 2:
                        m = m.AsSpan(0, Math.Max(1, rnd.Next(m.Length + 1))).ToArray();
                        break;
                    default:
                        int at = rnd.Next(m.Length);
                        m = [.. m.AsSpan(0, at), (byte)rnd.Next(256), .. m.AsSpan(at)];
                        break;
                }
            }

            target(m);
        }
    }

    [Fact]
    public void Type4TargetSurvivesMutatedPrograms()
    {
        string[] programs =
        [
            "{ dup 0.5 gt { 1 exch sub } { 2 mul } ifelse }",
            "{ 360 mul sin 2 div exch 360 mul sin 2 div add }",
            "{ 1 2 3 3 -1 roll 2 index 2 copy add mul idiv 7 3 bitshift }",
            "{ dup 0 lt { neg } if dup 100 gt { pop 100 } if sqrt ln 10 exp atan }",
        ];
        var seeds = programs.Select(p => new byte[] { 1, 1 }.Concat(Encoding.Latin1.GetBytes(p)).ToArray()).ToList();
        Mutate(seeds, b => Type4Target.Run(b), 3000, 1);
    }

    [Fact]
    public void CcittTargetSurvivesMutatedStreams()
    {
        var seeds = new List<byte[]>();
        foreach (string f in Directory.EnumerateFiles(FixtureDir(), "*.bin").Order(StringComparer.Ordinal))
        {
            byte[] d = File.ReadAllBytes(f);
            if (d.Length < 3000)
            {
                seeds.Add([.. new byte[] { (byte)(f.Contains("g4", StringComparison.Ordinal) ? 0 : 1), 0, 0, 0, 200, 0 }, .. d]);
            }
        }

        Assert.NotEmpty(seeds);
        Mutate(seeds, b => CcittTarget.Run(b), 4000, 2);
    }
}
