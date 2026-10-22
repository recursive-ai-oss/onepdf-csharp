using System.Diagnostics;
using System.Text;
using OnePdf.Filters;
using OnePdf.Fuzz;

namespace OnePdf.Tests.Filters;

/// <summary>
/// Bounded, deterministic mutation run of the filter fuzz target over valid encoded streams, for regressions between real
/// fuzzing campaigns. Any exception from a filter fails the test.
/// </summary>
public class FilterMutationTests
{
    [Fact]
    public void MutatedStreamsNeverThrow()
    {
        byte[] text = FilterTestUtil.Texty(3000, 90);
        var seeds = new List<byte[]>
        {
            FilterTestUtil.Zlib(text),
            FilterTestUtil.RawDeflate(text),
            FilterTestUtil.Zlib(FilterTestUtil.PngEncode(text, 30, 1, r => r % 5)),
            FilterTestUtil.LzwEncode(text, 1),
            FilterTestUtil.LzwEncode(text, 0),
            Encoding.ASCII.GetBytes(Convert.ToHexString(text.AsSpan(0, 200)) + ">"),
            "9jqo^BlbD-BleB1DJ+*+F(f,q/0JhKF<GL>Cj@.4Gp$d7F!,L7@<6@)/0JDEF<G%<+EV:2F!,O<DJ+*.@<*K0@<6L(Df-Ec5e;DffZ(EZee.Bl.9pF~>"u8.ToArray(),
            new byte[] { 2, 1, 2, 3, 254, 9, 0, 7, 128 },
        };
        var rnd = new Random(1234);
        var sw = Stopwatch.StartNew();
        int runs = 0;
        while (sw.ElapsedMilliseconds < 4000)
        {
            byte[] seed = seeds[rnd.Next(seeds.Count)];
            byte[] m = (byte[])seed.Clone();
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
                        m = m.AsSpan(0, rnd.Next(m.Length + 1)).ToArray();
                        if (m.Length == 0)
                        {
                            m = [0];
                        }

                        break;
                    default:
                        int at = rnd.Next(m.Length);
                        m = [.. m.AsSpan(0, at), (byte)rnd.Next(256), .. m.AsSpan(at)];
                        break;
                }
            }

            byte[] header = [(byte)rnd.Next(256), (byte)rnd.Next(17), (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256)];
            FilterTarget.Run([.. header, .. m]);
            runs++;
        }

        Assert.True(runs > 100);
    }
}
