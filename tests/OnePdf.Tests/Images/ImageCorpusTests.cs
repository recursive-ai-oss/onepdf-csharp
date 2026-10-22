using System.Runtime.CompilerServices;
using Xunit;

namespace OnePdf.Tests.Images;

public class ImageCorpusTests
{
    private static string? TestData([CallerFilePath] string source = "")
    {
        for (string? d = Path.GetDirectoryName(source); d is not null; d = Path.GetDirectoryName(d))
        {
            string c = Path.Combine(d, "testdata");
            if (Directory.Exists(c))
            {
                return c;
            }
        }

        return null;
    }

    [Fact]
    public void EveryCommittedImageYieldsSamplesOrADiagnostic()
    {
        string? dir = TestData();
        if (dir is null)
        {
            return;
        }

        var sweep = new ImageSweep();
        foreach (string f in Directory.EnumerateFiles(dir, "*.pdf", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            sweep.Add(f);
        }

        Assert.True(sweep.Images > 20, sweep.ToString());
        Assert.True(sweep.Failures.Count == 0, sweep + "\n" + string.Join("\n", sweep.Failures.Take(20)));
        Console.WriteLine(sweep);
    }

    /// <summary>Set ONEPDF_IMAGE_SWEEP_DIR to a directory of PDFs (and optionally ONEPDF_IMAGE_SWEEP_LOG) for a no-crash pass.</summary>
    [Fact]
    public void LocalCorpusSweep()
    {
        string? dir = Environment.GetEnvironmentVariable("ONEPDF_IMAGE_SWEEP_DIR");
        if (dir is null || !Directory.Exists(dir))
        {
            return;
        }

        var sweep = new ImageSweep();
        string? log = Environment.GetEnvironmentVariable("ONEPDF_IMAGE_SWEEP_LOG");
        foreach (string f in Directory.EnumerateFiles(dir, "*.pdf", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            sweep.Add(f);
            if (log is not null && sweep.Files % 200 == 0)
            {
                File.WriteAllText(log, sweep + "\n" + string.Join("\n", sweep.Failures));
            }
        }

        if (log is not null)
        {
            File.WriteAllText(log, sweep + "\n" + string.Join("\n", sweep.Failures));
        }

        Assert.Equal(0, sweep.Bad);
    }

    [Fact]
    public void ImageFuzzTargetSurvivesMutations()
    {
        string? dir = TestData();
        var rnd = new Random(7);
        byte[] seed = new byte[400];
        for (int i = 0; i < 3000; i++)
        {
            rnd.NextBytes(seed);
            if (i % 3 == 0)
            {
                seed[2] &= 0x9F;
                seed[0] %= 20;
                seed[1] %= 20;
            }

            OnePdf.Fuzz.ImageTarget.Run(seed.AsSpan(0, 16 + rnd.Next(380)));
        }

        Assert.NotNull(dir);
    }
}
