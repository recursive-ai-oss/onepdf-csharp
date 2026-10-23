using System.Numerics;

namespace OnePdf.Text;

/// <summary>A strict weak order on <typeparamref name="T"/>, supplied as a static method of a struct so the sort can inline it.</summary>
internal interface ISortOrder<T>
{
    /// <summary>Negative when <paramref name="a"/> sorts before <paramref name="b"/>, positive after, zero when equal.</summary>
    static abstract int Compare(in T a, in T b);
}

/// <summary>Orders <see cref="KeyIdx"/> by key.</summary>
internal readonly struct ByKeyOrder : ISortOrder<KeyIdx>
{
    public static int Compare(in KeyIdx a, in KeyIdx b) => a.Key.CompareTo(b.Key);
}

/// <summary>Orders <see cref="Key2Idx"/> by its first value, then its second.</summary>
internal readonly struct ByKey2Order : ISortOrder<Key2Idx>
{
    public static int Compare(in Key2Idx a, in Key2Idx b)
    {
        int c = a.A.CompareTo(b.A);
        return c != 0 ? c : a.B.CompareTo(b.B);
    }
}

/// <summary>
/// The introsort that <c>Span&lt;T&gt;.Sort(Comparison&lt;T&gt;)</c> uses (median-of-three quicksort, insertion sort up to 16 elements, heap sort when the
/// depth limit is reached), with the comparison inlined instead of called through a delegate. Elements that compare equal end up in the same
/// places as with the framework sort, which the layout relies on: its output must not depend on which of two equal keys comes first.
/// A test compares the two on random and tie-heavy input.
/// </summary>
internal static class IntroSort
{
    private const int SizeThreshold = 16;

    internal static void Sort<T, TOrder>(Span<T> keys)
        where TOrder : ISortOrder<T>
    {
        if (keys.Length > 1)
        {
            Sort<T, TOrder>(keys, 2 * (BitOperations.Log2((uint)keys.Length) + 1));
        }
    }

    /// <summary>
    /// Sorts like <see cref="Sort{T, TOrder}(Span{T})"/>, faster when <paramref name="keys"/> is already nearly in order, which layout input usually is
    /// (glyphs arrive line by line). A first attempt insertion-sorts a copy-protected array and gives up at the first pair of equal keys, since only
    /// then could the framework's order of equals differ from any other sort's, or after too many moves; the original order is then restored from
    /// <paramref name="scratch"/> (at least as long as <paramref name="keys"/>) and the framework-equivalent sort runs. The result is always identical.
    /// </summary>
    internal static void SortNearlyOrdered<T, TOrder>(Span<T> keys, Span<T> scratch)
        where TOrder : ISortOrder<T>
    {
        int n = keys.Length;
        if (n <= SizeThreshold)
        {
            Sort<T, TOrder>(keys);
            return;
        }

        keys.CopyTo(scratch);
        if (TryInsertionDistinct<T, TOrder>(keys, 8L * n + 64))
        {
            return;
        }

        scratch.Slice(0, n).CopyTo(keys);
        Sort<T, TOrder>(keys);
    }

    // Insertion sort that reports failure on a tie or after more than moveBudget element moves. Without ties the sorted order is unique.
    private static bool TryInsertionDistinct<T, TOrder>(Span<T> keys, long moveBudget)
        where TOrder : ISortOrder<T>
    {
        long moves = 0;
        for (int i = 1; i < keys.Length; i++)
        {
            T t = keys[i];
            int c = TOrder.Compare(t, keys[i - 1]);
            if (c > 0)
            {
                continue;
            }

            if (c == 0)
            {
                return false;
            }

            int j = i - 1;
            do
            {
                keys[j + 1] = keys[j];
                j--;
                moves++;
            }
            while (j >= 0 && (c = TOrder.Compare(t, keys[j])) < 0);

            if (moves > moveBudget || (j >= 0 && c == 0))
            {
                return false;
            }

            keys[j + 1] = t;
        }

        return true;
    }

    private static void Sort<T, TOrder>(Span<T> keys, int depthLimit)
        where TOrder : ISortOrder<T>
    {
        int size = keys.Length;
        while (size > 1)
        {
            if (size <= SizeThreshold)
            {
                if (size == 2)
                {
                    SwapIfGreater<T, TOrder>(keys, 0, 1);
                    return;
                }

                if (size == 3)
                {
                    SwapIfGreater<T, TOrder>(keys, 0, 1);
                    SwapIfGreater<T, TOrder>(keys, 0, 2);
                    SwapIfGreater<T, TOrder>(keys, 1, 2);
                    return;
                }

                Insertion<T, TOrder>(keys.Slice(0, size));
                return;
            }

            if (depthLimit == 0)
            {
                Heap<T, TOrder>(keys.Slice(0, size));
                return;
            }

            depthLimit--;
            int p = Partition<T, TOrder>(keys.Slice(0, size));
            Sort<T, TOrder>(keys[(p + 1)..size], depthLimit);
            size = p;
        }
    }

    private static void SwapIfGreater<T, TOrder>(Span<T> keys, int i, int j)
        where TOrder : ISortOrder<T>
    {
        if (TOrder.Compare(keys[i], keys[j]) > 0)
        {
            (keys[i], keys[j]) = (keys[j], keys[i]);
        }
    }

    private static int Partition<T, TOrder>(Span<T> keys)
        where TOrder : ISortOrder<T>
    {
        int hi = keys.Length - 1;
        int middle = hi >> 1;
        SwapIfGreater<T, TOrder>(keys, 0, middle);
        SwapIfGreater<T, TOrder>(keys, 0, hi);
        SwapIfGreater<T, TOrder>(keys, middle, hi);

        T pivot = keys[middle];
        (keys[middle], keys[hi - 1]) = (keys[hi - 1], keys[middle]);
        int left = 0, right = hi - 1;
        while (left < right)
        {
            while (TOrder.Compare(keys[++left], pivot) < 0)
            {
            }

            while (TOrder.Compare(pivot, keys[--right]) < 0)
            {
            }

            if (left >= right)
            {
                break;
            }

            (keys[left], keys[right]) = (keys[right], keys[left]);
        }

        if (left != hi - 1)
        {
            (keys[left], keys[hi - 1]) = (keys[hi - 1], keys[left]);
        }

        return left;
    }

    private static void Insertion<T, TOrder>(Span<T> keys)
        where TOrder : ISortOrder<T>
    {
        for (int i = 0; i < keys.Length - 1; i++)
        {
            T t = keys[i + 1];
            int j = i;
            while (j >= 0 && TOrder.Compare(t, keys[j]) < 0)
            {
                keys[j + 1] = keys[j];
                j--;
            }

            keys[j + 1] = t;
        }
    }

    private static void Heap<T, TOrder>(Span<T> keys)
        where TOrder : ISortOrder<T>
    {
        int n = keys.Length;
        for (int i = n >> 1; i >= 1; i--)
        {
            DownHeap<T, TOrder>(keys, i, n);
        }

        for (int i = n; i > 1; i--)
        {
            (keys[0], keys[i - 1]) = (keys[i - 1], keys[0]);
            DownHeap<T, TOrder>(keys, 1, i - 1);
        }
    }

    private static void DownHeap<T, TOrder>(Span<T> keys, int i, int n)
        where TOrder : ISortOrder<T>
    {
        T d = keys[i - 1];
        while (i <= n >> 1)
        {
            int child = 2 * i;
            if (child < n && TOrder.Compare(keys[child - 1], keys[child]) < 0)
            {
                child++;
            }

            if (!(TOrder.Compare(d, keys[child - 1]) < 0))
            {
                break;
            }

            keys[i - 1] = keys[child - 1];
            i = child;
        }

        keys[i - 1] = d;
    }
}
