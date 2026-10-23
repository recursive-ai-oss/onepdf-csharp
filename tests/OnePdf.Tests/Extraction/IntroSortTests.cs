using OnePdf.Text;

namespace OnePdf.Tests.Extraction;

public class IntroSortTests
{
    // The layout's output depends on where equal keys end up, so the inlined sort must reproduce the framework sort element for element.

    [Fact]
    public void KeyIdxMatchesFrameworkSortIncludingTies()
    {
        Random rng = new(12345);
        Comparison<KeyIdx> cmp = (a, b) => a.Key.CompareTo(b.Key);
        for (int round = 0; round < 3000; round++)
        {
            int n = round % 7 == 0 ? rng.Next(0, 5000) : rng.Next(0, 80);
            int distinct = rng.Next(1, 12);
            bool sortedRuns = round % 5 == 0;
            KeyIdx[] a = new KeyIdx[n];
            for (int i = 0; i < n; i++)
            {
                double key = sortedRuns ? (i / 7) % distinct : rng.Next(distinct);
                if (round % 11 == 0 && rng.Next(40) == 0)
                {
                    key = double.NaN;
                }

                a[i] = new KeyIdx { Key = key + (round % 3 == 0 ? 0.0 : rng.NextDouble() * 1e-9 * rng.Next(2)), Idx = i };
            }

            KeyIdx[] b = (KeyIdx[])a.Clone();
            a.AsSpan().Sort(cmp);
            IntroSort.Sort<KeyIdx, ByKeyOrder>(b);
            Assert.Equal(a.Select(x => x.Idx), b.Select(x => x.Idx));
        }
    }

    [Fact]
    public void Key2IdxMatchesFrameworkSortIncludingTies()
    {
        Random rng = new(777);
        Comparison<Key2Idx> cmp = (x, y) =>
        {
            int c = x.A.CompareTo(y.A);
            return c != 0 ? c : x.B.CompareTo(y.B);
        };
        for (int round = 0; round < 3000; round++)
        {
            int n = round % 7 == 0 ? rng.Next(0, 5000) : rng.Next(0, 80);
            int da = rng.Next(1, 6), db = rng.Next(1, 6);
            Key2Idx[] a = new Key2Idx[n];
            for (int i = 0; i < n; i++)
            {
                a[i] = new Key2Idx { A = rng.Next(da), B = round % 4 == 0 ? i % db : rng.Next(db), Idx = i };
            }

            Key2Idx[] b = (Key2Idx[])a.Clone();
            a.AsSpan().Sort(cmp);
            IntroSort.Sort<Key2Idx, ByKey2Order>(b);
            Assert.Equal(a.Select(x => x.Idx), b.Select(x => x.Idx));
        }
    }

    [Fact]
    public void AdversarialInputReachesTheHeapSortFallbackAndStillMatches()
    {
        // Organ-pipe and median-of-three killer shapes push the quicksort past its depth limit.
        Comparison<KeyIdx> cmp = (a, b) => a.Key.CompareTo(b.Key);
        foreach (int n in new[] { 17, 100, 1000, 20000 })
        {
            foreach (int shape in new[] { 0, 1, 2 })
            {
                KeyIdx[] a = new KeyIdx[n];
                for (int i = 0; i < n; i++)
                {
                    double k = shape switch
                    {
                        0 => i < n / 2 ? i : n - i,
                        1 => (i % 2 == 0) ? i / 2 : n - i / 2,
                        _ => (i * 7919) % 31,
                    };
                    a[i] = new KeyIdx { Key = k, Idx = i };
                }

                KeyIdx[] b = (KeyIdx[])a.Clone();
                a.AsSpan().Sort(cmp);
                IntroSort.Sort<KeyIdx, ByKeyOrder>(b);
                Assert.Equal(a.Select(x => x.Idx), b.Select(x => x.Idx));
            }
        }
    }
}
