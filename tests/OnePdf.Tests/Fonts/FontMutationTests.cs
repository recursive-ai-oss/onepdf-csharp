using System.Diagnostics;
using System.Text;
using OnePdf.Fuzz;

namespace OnePdf.Tests.Fonts;

/// <summary>
/// Bounded, deterministic mutation runs of the font fuzz targets over valid inputs, for regressions between real fuzzing campaigns.
/// Anything other than the typed font exception fails the test.
/// </summary>
public class FontMutationTests
{
    private static byte[] Mutate(byte[] seed, Random rnd)
    {
        byte[] m = (byte[])seed.Clone();
        int edits = rnd.Next(1, 10);
        for (int e = 0; e < edits; e++)
        {
            switch (rnd.Next(5))
            {
                case 0:
                    m[rnd.Next(m.Length)] = (byte)rnd.Next(256);
                    break;
                case 1:
                    m[rnd.Next(m.Length)] ^= (byte)(1 << rnd.Next(8));
                    break;
                case 2:
                    m = m.AsSpan(0, rnd.Next(m.Length + 1)).ToArray();
                    if (m.Length == 0)
                    {
                        m = [0];
                    }

                    break;
                case 3:
                    int at = rnd.Next(m.Length);
                    m = [.. m.AsSpan(0, at), (byte)rnd.Next(256), .. m.AsSpan(at)];
                    break;
                default:
                    // Overwrite a few bytes with big-number values, which is what breaks offsets and counts.
                    int p = rnd.Next(m.Length);
                    for (int k = 0; k < 4 && p + k < m.Length; k++)
                    {
                        m[p + k] = (byte)(rnd.Next(2) == 0 ? 0xFF : 0x7F);
                    }

                    break;
            }
        }

        return m;
    }

    private static void RunFor(int milliseconds, List<byte[]> seeds, Action<byte[]> target, int seed)
    {
        // ONEPDF_MUTATION_SCALE stretches the time budget for longer local runs; ONEPDF_MUTATION_LOG appends the execution counts.
        int scale = int.TryParse(Environment.GetEnvironmentVariable("ONEPDF_MUTATION_SCALE"), out int sc) && sc > 0 ? sc : 1;
        milliseconds *= scale;
        var rnd = new Random(seed);
        var sw = Stopwatch.StartNew();
        int runs = 0;
        while (sw.ElapsedMilliseconds < milliseconds)
        {
            target(Mutate(seeds[rnd.Next(seeds.Count)], rnd));
            runs++;
        }

        if (Environment.GetEnvironmentVariable("ONEPDF_MUTATION_LOG") is { } log)
        {
            File.AppendAllText(log, $"seed {seed}: {runs} executions in {sw.ElapsedMilliseconds} ms over {seeds.Count} seeds\n");
        }

        Assert.True(runs > 20, "runs: " + runs);
    }

    private static byte[] WithMode(int mode, byte[] font) => [(byte)mode, .. font];

    [Fact]
    public void MutatedType1FontsOnlyThrowTypedErrors()
    {
        var seeds = new List<byte[]>
        {
            WithMode(1, Type1Builder.Font()),
            WithMode(1, Type1Builder.Font(hexEexec: true)),
            WithMode(1, Type1Builder.Font(pfb: true)),
            WithMode(0, Type1Builder.Font(lenIV: -1)),
        };
        foreach (string p in FontEnv.SystemFonts(".pfb").Take(1))
        {
            seeds.Add(WithMode(1, File.ReadAllBytes(p)));
        }

        RunFor(4000, seeds, d => FontProgramTarget.Run(d), 11);
    }

    [Fact]
    public void MutatedSfntAndCffFontsOnlyThrowTypedErrors()
    {
        var seeds = new List<byte[]>();
        string? dir = FontEnv.CacheDir;
        string? lib = dir is null ? null : FontEnv.CachedFonts().FirstOrDefault(p => p.EndsWith("LiberationMono-Regular.ttf", StringComparison.Ordinal));
        if (lib is not null)
        {
            seeds.Add(WithMode(3, File.ReadAllBytes(lib)));
        }

        string? otf = FontEnv.SystemFonts(".otf").FirstOrDefault(p => p.EndsWith("NimbusMonoPS-Regular.otf", StringComparison.Ordinal));
        if (otf is not null)
        {
            byte[] file = File.ReadAllBytes(otf);
            seeds.Add(WithMode(3, file));
            // The bare CFF table, as a Type1C stream would carry it.
            int n = (file[4] << 8) | file[5];
            for (int i = 0; i < n; i++)
            {
                int e = 12 + (16 * i);
                if (Encoding.ASCII.GetString(file, e, 4) == "CFF ")
                {
                    int off = (file[e + 8] << 24) | (file[e + 9] << 16) | (file[e + 10] << 8) | file[e + 11];
                    int len = (file[e + 12] << 24) | (file[e + 13] << 16) | (file[e + 14] << 8) | file[e + 15];
                    seeds.Add(WithMode(2, file.AsSpan(off, len).ToArray()));
                }
            }
        }

        if (seeds.Count == 0)
        {
            Assert.Skip("No TrueType or OpenType seed fonts available.");
        }

        RunFor(6000, seeds, d => FontProgramTarget.Run(d), 12);
    }

    [Fact]
    public void MutatedCMapsOnlyThrowNothing()
    {
        var seeds = new List<byte[]>
        {
            WithMode(0, Encoding.Latin1.GetBytes("/90ms-RKSJ-H usecmap /WMode 1 def 2 begincodespacerange <00> <80> <8140> <9FFC> endcodespacerange 2 begincidrange <20> <7e> 1 <8140> <817e> 633 endcidrange 1 begincidchar <01> 5 endcidchar")),
            WithMode(1, Encoding.Latin1.GetBytes("1 begincodespacerange <0000> <FFFF> endcodespacerange 2 beginbfchar <0003> <0020> <0005> <D835DC00> endbfchar 2 beginbfrange <0010> <0012> <0041> <0020> <0022> [<0061> <00620063> <D835DC01>] endbfrange")),
        };
        RunFor(3000, seeds, d => CMapTarget.Run(d), 13);
    }
}
