using System.Globalization;
using OnePdf.Text;

namespace OnePdf.Tests.Extraction;

public class BidiTests
{
    private static int[] Cps(string s)
    {
        List<int> r = [];
        for (int i = 0; i < s.Length; i++)
        {
            int cp = char.ConvertToUtf32(s, i);
            if (cp > 0xFFFF)
            {
                i++;
            }

            r.Add(cp);
        }

        return [.. r];
    }

    private static string Reorder(string logical, int para)
    {
        int[] c = Cps(logical);
        byte[] lev = new byte[c.Length];
        Bidi.ResolveLevels(c, para, lev);
        int[] ord = new int[c.Length];
        int m = Bidi.VisualOrder(lev, ord);
        return string.Concat(Enumerable.Range(0, m).Select(i => char.ConvertFromUtf32(c[ord[i]])));
    }

    [Fact]
    public void BasicReordering()
    {
        Assert.Equal("abc", Reorder("abc", 0));
        Assert.Equal("םולש", Reorder("שלום", 0));
        Assert.Equal("a םולש b", Reorder("a שלום b", 0));
        Assert.Equal("123 םולש", Reorder("שלום 123", -1));
    }

    [Fact]
    public void ConformanceBidiCharacterTest()
    {
        string? dir = Environment.GetEnvironmentVariable("ONEPDF_UNICODE");
        string path = dir is null ? "" : Path.Combine(dir, "BidiCharacterTest.txt");
        if (!File.Exists(path))
        {
            Assert.Skip("Set ONEPDF_UNICODE to a directory holding BidiCharacterTest.txt (Unicode 16.0.0) to run the conformance cases.");
        }

        int run = 0, failed = 0;
        List<string> samples = [];
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] f = line.Split(';');
            int[] cps = [.. f[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture))];
            int dir0 = int.Parse(f[1], CultureInfo.InvariantCulture);
            int paraExpected = int.Parse(f[2], CultureInfo.InvariantCulture);
            string[] levelTokens = f[3].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int[] orderExpected = [.. f[4].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => int.Parse(x, CultureInfo.InvariantCulture))];

            byte[] lev = new byte[cps.Length];
            int para = Bidi.ResolveLevels(cps, dir0 == 2 ? -1 : dir0, lev);
            bool ok = para == paraExpected;
            for (int i = 0; ok && i < cps.Length; i++)
            {
                ok = levelTokens[i] == "x" ? lev[i] == Bidi.Removed : lev[i] != Bidi.Removed && lev[i].ToString(CultureInfo.InvariantCulture) == levelTokens[i];
            }

            if (ok)
            {
                int[] ord = new int[cps.Length];
                int m = Bidi.VisualOrder(lev, ord);
                ok = ord.AsSpan(0, m).SequenceEqual(orderExpected);
            }

            run++;
            if (!ok)
            {
                failed++;
                if (samples.Count < 5)
                {
                    samples.Add(line);
                }
            }
        }

        Assert.True(run > 90000, $"only {run} cases ran");
        Assert.True(failed == 0, $"{failed} of {run} failed, e.g.\n{string.Join('\n', samples)}");
    }
}
