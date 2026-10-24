using System.Buffers;
using System.Text;

namespace OnePdf.Text;

/// <summary>A glyph in the frame of its reading direction: x runs along the text, y towards the next line.</summary>
internal struct Item
{
    internal int G;
    internal int K;
    internal double X0, X1, Top, Bot, Base, Size;
    internal bool Space;
    internal bool Cjk;
    internal int Line;
    internal int FirstCp;
}

/// <summary>A run of items on one line with no wide gap inside; its items are <c>ids[Start .. Start + Count)</c> of the class's item-id array.</summary>
internal struct Frag
{
    internal int Line;
    internal int Start;
    internal int Count;
    internal double X0, X1, Top, Bot, Base, Size;
    internal int NonSpace;
}

/// <summary>A sort key with the index it belongs to.</summary>
internal struct KeyIdx
{
    internal double Key;
    internal int Idx;
}

/// <summary>A sort key of two values (compared in order) with its index.</summary>
internal struct Key2Idx
{
    internal double A, B;
    internal int Idx;
}

/// <summary>A word as a run of the item-id array.</summary>
internal struct WordRec
{
    internal int Start;
    internal int Len;
    internal bool Cell;
}

/// <summary>Arrays rented from the shared pool for the duration of one analysis, all returned together.</summary>
internal struct Rental<T>
{
    private List<T[]>? _rented;

    internal T[] Rent(int length)
    {
        T[] a = ArrayPool<T>.Shared.Rent(Math.Max(length, 1));
        (_rented ??= []).Add(a);
        return a;
    }

    internal void ReturnAll()
    {
        if (_rented is null)
        {
            return;
        }

        foreach (T[] a in _rented)
        {
            ArrayPool<T>.Shared.Return(a);
        }

        _rented.Clear();
    }
}

/// <summary>Working storage of the layout of one reading-direction class. Rented arrays hold stale data; every user writes before it reads.</summary>
internal sealed class LayoutWork : IDisposable
{
    internal Rental<int> Ints;
    internal Rental<double> Doubles;
    internal Rental<KeyIdx> Keys;
    internal Rental<Key2Idx> Keys2;
    internal Rental<Item> Items;
    internal Rental<Frag> Frags;
    internal Rental<WordRec> Words;

    /// <summary>Per-line stamps: an entry is valid only when it equals the current generation.</summary>
    internal int[] Stamp = [];

    internal int Generation;

    /// <summary>False when the words of the result will not keep their glyph indices, so they need not be collected.</summary>
    internal bool KeepWordGlyphs = true;

    internal int[] Aux1 = [];
    internal int[] Aux2 = [];
    internal int[] Aux3 = [];
    internal double[] DAux = [];

    internal void InitLines(int lineCount)
    {
        Stamp = Ints.Rent(lineCount);
        Array.Clear(Stamp, 0, lineCount);
        Aux1 = Ints.Rent(lineCount);
        Aux2 = Ints.Rent(lineCount);
        Aux3 = Ints.Rent(lineCount);
        DAux = Doubles.Rent(lineCount);
        Generation = 0;
    }

    public void Dispose()
    {
        Ints.ReturnAll();
        Doubles.ReturnAll();
        Keys.ReturnAll();
        Keys2.ReturnAll();
        Items.ReturnAll();
        Frags.ReturnAll();
        Words.ReturnAll();
        Stamp = Aux1 = Aux2 = Aux3 = [];
        DAux = [];
    }
}

/// <summary>Builds items from glyphs and turns them into blocks of lines and words. See <c>docs/text-layout.md</c>.</summary>
internal static class TextLayout
{
    // Unit vectors of the four quantized reading directions in page space (y up): right, up, left, down.
    private static readonly double[] Dx = [1, 0, -1, 0];
    private static readonly double[] Dy = [0, 1, 0, -1];

    internal static int Quantize(in PdfTextGlyph g)
    {
        double dx, dy;
        if (g.IsVertical)
        {
            dx = -g.Matrix.C;
            dy = -g.Matrix.D;
        }
        else
        {
            dx = g.Matrix.A;
            dy = g.Matrix.B;
        }

        if (dx == 0 && dy == 0)
        {
            return 0;
        }

        double a = Math.Atan2(dy, dx);
        int k = (int)Math.Round(a / (Math.PI / 2));
        return ((k % 4) + 4) % 4;
    }

    internal static Item MakeItem(in PdfTextGlyph g, int index) => MakeItem(g, index, Quantize(g));

    internal static Item MakeItem(in PdfTextGlyph g, int index, int k)
    {
        double ux = Dx[k], uy = Dy[k];
        double nx = uy, ny = -ux;
        g.Corners(out double ax, out double ay, out double bx, out double by, out double cx, out double cy, out double dx, out double dy);
        double f0 = ux * ax + uy * ay, g0 = nx * ax + ny * ay;
        double f1 = ux * bx + uy * by, g1 = nx * bx + ny * by;
        double f2 = ux * cx + uy * cy, g2 = nx * cx + ny * cy;
        double f3 = ux * dx + uy * dy, g3 = nx * dx + ny * dy;
        double x0 = Math.Min(Math.Min(f0, f1), Math.Min(f2, f3));
        double x1 = Math.Max(Math.Max(f0, f1), Math.Max(f2, f3));
        double y0 = Math.Min(Math.Min(g0, g1), Math.Min(g2, g3));
        double y1 = Math.Max(Math.Max(g0, g1), Math.Max(g2, g3));

        string t = g.Text ?? "";
        bool space;
        int first;
        if (t.Length == 1 && !char.IsSurrogate(t[0]))
        {
            // The common case: one BMP character.
            first = t[0];
            space = char.IsWhiteSpace(t[0]);
        }
        else
        {
            space = t.Length > 0;
            for (int i = 0; i < t.Length; i++)
            {
                if (!char.IsWhiteSpace(t[i]))
                {
                    space = false;
                    break;
                }
            }

            first = t.Length > 0 ? char.ConvertToUtf32(t, 0) : 0;
        }

        return new Item
        {
            G = index,
            K = k,
            X0 = x0,
            X1 = x1,
            Top = y0,
            Bot = y1,
            Base = nx * g.Matrix.E + ny * g.Matrix.F,
            Size = Math.Max(g.Size, 1e-6),
            Space = space,
            Cjk = IsCjk(first),
            FirstCp = first,
            Line = -1,
        };
    }

    internal static bool IsCjk(int cp) =>
        (cp >= 0x2E80 && cp <= 0x9FFF) || (cp >= 0xAC00 && cp <= 0xD7AF) || (cp >= 0xF900 && cp <= 0xFAFF) ||
        (cp >= 0xFF00 && cp <= 0xFFEF) || (cp >= 0x20000 && cp <= 0x3FFFF);

    /// <summary>
    /// Groups glyph indices by reading direction, keeping their order within a direction. <paramref name="subset"/> is the list of glyph indices, or empty
    /// with <paramref name="count"/> glyphs taken in order when <paramref name="all"/> is set. Returns the dominant direction.
    /// </summary>
    internal static int Classify(LayoutWork w, PdfTextGlyph[] g, ReadOnlySpan<int> subset, bool all, int count, out int[] grouped, Span<int> start)
    {
        // start has 5 entries: the offsets of the four direction classes in grouped, then the end.
        Span<int> n = stackalloc int[4];
        int[] ks = w.Ints.Rent(count);
        for (int i = 0; i < count; i++)
        {
            int k = Quantize(g[all ? i : subset[i]]);
            ks[i] = k;
            n[k]++;
        }

        MarkStackedCjk(w, g, subset, all, count, ks, n);
        int dominant = 0;
        for (int k = 1; k < 4; k++)
        {
            if (n[k] > n[dominant])
            {
                dominant = k;
            }
        }

        start[0] = 0;
        for (int k = 0; k < 4; k++)
        {
            start[k + 1] = start[k] + n[k];
        }

        Span<int> cur = stackalloc int[4];
        for (int k = 0; k < 4; k++)
        {
            cur[k] = start[k];
        }

        grouped = w.Ints.Rent(count);
        for (int i = 0; i < count; i++)
        {
            grouped[cur[ks[i]]++] = all ? i : subset[i];
        }

        return dominant;
    }

    /// <summary>
    /// Some producers (browsers printing vertical-rl text) set each upright ideograph with its own horizontal text matrix instead of using the
    /// vertical writing mode. Such glyphs are recognised by the producer's own order: consecutive ideographs whose origins step straight down
    /// by about one em. Those glyphs are moved to the downward reading class (columns, right to left) so a column reads as a line.
    /// </summary>
    private static void MarkStackedCjk(LayoutWork w, PdfTextGlyph[] g, ReadOnlySpan<int> subset, bool all, int count, int[] ks, Span<int> n)
    {
        if (count < 4)
        {
            return;
        }

        int[] flag = w.Ints.Rent(count);
        Array.Clear(flag, 0, count);
        int down = 0;
        for (int i = 1; i < count; i++)
        {
            ref readonly PdfTextGlyph a = ref g[all ? i - 1 : subset[i - 1]];
            ref readonly PdfTextGlyph b = ref g[all ? i : subset[i]];
            if (ks[i - 1] != 0 || ks[i] != 0 || a.IsVertical || b.IsVertical || string.IsNullOrEmpty(a.Text) || string.IsNullOrEmpty(b.Text) ||
                !IsCjk(char.ConvertToUtf32(a.Text, 0)) || !IsCjk(char.ConvertToUtf32(b.Text, 0)))
            {
                continue;
            }

            double s = (a.Size + b.Size) / 2;
            double dx = b.Matrix.E - a.Matrix.E, dy = b.Matrix.F - a.Matrix.F;
            if (Math.Abs(dx) <= LayoutParameters.StackColumnDriftEm * s && dy <= -LayoutParameters.StackStepMinEm * s && dy >= -LayoutParameters.StackStepMaxEm * s)
            {
                flag[i - 1] |= 1;
                flag[i] |= 1;
                down++;
            }
            else if (Math.Abs(dy) <= LayoutParameters.StackColumnDriftEm * s && dx >= LayoutParameters.StackStepMinEm * s && dx <= LayoutParameters.StackStepMaxEm * s)
            {
                flag[i - 1] |= 2;
                flag[i] |= 2;
            }
        }

        if (down < LayoutParameters.StackMinSteps)
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            if (flag[i] == 1)
            {
                ks[i] = 3;
                n[0]--;
                n[3]++;
            }
        }
    }

    /// <summary>Lays out the glyphs of <paramref name="subset"/> (indices into <paramref name="g"/>) and returns blocks in reading order.</summary>
    internal static List<PdfTextBlock> Run(PdfTextGlyph[] g, ReadOnlySpan<int> subset, CancellationToken ct) => RunCore(g, subset, false, subset.Length, true, ct);

    /// <summary>Lays out every glyph of <paramref name="g"/>.</summary>
    internal static List<PdfTextBlock> RunAll(PdfTextGlyph[] g, CancellationToken ct) => RunCore(g, default, true, g.Length, true, ct);

    /// <summary>Lays out the first <paramref name="count"/> glyphs of <paramref name="g"/>, which may be longer.</summary>
    internal static List<PdfTextBlock> RunAll(PdfTextGlyph[] g, int count, bool keepWordGlyphs, CancellationToken ct) => RunCore(g, default, true, count, keepWordGlyphs, ct);

    private static List<PdfTextBlock> RunCore(PdfTextGlyph[] g, ReadOnlySpan<int> subset, bool all, int count, bool keepWordGlyphs, CancellationToken ct)
    {
        List<PdfTextBlock> result = [];
        if (count == 0)
        {
            return result;
        }

        using LayoutWork w = new();
        Span<int> start = stackalloc int[5];
        int dominant = Classify(w, g, subset, all, count, out int[] grouped, start);
        for (int pass = -1; pass < 4; pass++)
        {
            int k = pass < 0 ? dominant : pass;
            int cnt = start[k + 1] - start[k];
            if ((pass >= 0 && k == dominant) || cnt == 0)
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();
            LayoutClass(g, grouped, start[k], cnt, k, result, keepWordGlyphs, ct);
        }

        return result;
    }

    internal static Item[] MakeItems(LayoutWork w, PdfTextGlyph[] g, int[] idx, int offset, int count, int k)
    {
        Item[] items = w.Items.Rent(count);
        for (int i = 0; i < count; i++)
        {
            items[i] = MakeItem(g[idx[offset + i]], idx[offset + i], k);
        }

        return items;
    }

    private static void LayoutClass(PdfTextGlyph[] g, int[] idx, int offset, int n, int k, List<PdfTextBlock> output, bool keepWordGlyphs, CancellationToken ct)
    {
        using LayoutWork w = new() { KeepWordGlyphs = keepWordGlyphs };
        Item[] items = MakeItems(w, g, idx, offset, n, k);
        int lineCount = ClusterLines(w, items, n);
        w.InitLines(lineCount);
        int[] ids = BucketLines(w, items, n, lineCount, out int[] lineStart);
        Frag[] frags = Fragment(w, items, ids, lineCount, lineStart, out int nf);

        // XY-cut: regions are runs of the fragment-id array; a split reorders a region in place and pushes its parts.
        int[] reg = w.Ints.Rent(nf);
        for (int i = 0; i < nf; i++)
        {
            reg[i] = i;
        }

        int[] leafStart = w.Ints.Rent(nf);
        int[] leafLen = w.Ints.Rent(nf);
        int[] leafTable = w.Ints.Rent(nf);
        int leaves = XyCut(w, frags, items, ids, nf, reg, leafStart, leafLen, leafTable, ct);
        for (int i = 0; i < leaves; i++)
        {
            PdfTextBlock? b = MakeBlock(w, g, items, ids, frags, reg, leafStart[i], leafLen[i], leafTable[i] != 0);
            if (b is not null)
            {
                output.Add(b);
            }
        }
    }

    // ---- lines ------------------------------------------------------------------------------------------------------------------

    /// <summary>Assigns <see cref="Item.Line"/>: glyphs on one baseline, with sub/superscripts joined to the line they touch. Returns the line count.</summary>
    internal static int ClusterLines(LayoutWork w, Item[] items, int n)
    {
        Key2Idx[] ord = w.Keys2.Rent(n);
        for (int i = 0; i < n; i++)
        {
            ord[i] = new Key2Idx { A = items[i].Base, B = items[i].X0, Idx = i };
        }

        IntroSort.SortNearlyOrdered<Key2Idx, ByKey2Order>(ord.AsSpan(0, n), w.Keys2.Rent(n));

        // Clusters are consecutive runs of the sorted order: cluster c is ord[cs[c] .. cs[c + 1]).
        int[] cs = w.Ints.Rent(n + 1);
        int cc = 0;
        double sum = 0, minSize = 0;
        for (int p = 0; p < n; p++)
        {
            ref Item it = ref items[ord[p].Idx];
            if (cc > 0)
            {
                double mean = sum / (p - cs[cc - 1]);
                double tol = LayoutParameters.BaselineToleranceEm * Math.Min(minSize, it.Size);
                if (Math.Abs(it.Base - mean) <= tol)
                {
                    sum += it.Base;
                    minSize = Math.Min(minSize, it.Size);
                    continue;
                }
            }

            cs[cc++] = p;
            sum = it.Base;
            minSize = it.Size;
        }

        cs[cc] = n;

        // Join script runs (small clusters) to a larger neighbouring line they touch.
        double[] scratch = w.Doubles.Rent(n);
        double[] medSize = w.Doubles.Rent(cc);
        double[] medBase = w.Doubles.Rent(cc);
        for (int c = 0; c < cc; c++)
        {
            int len = cs[c + 1] - cs[c];
            for (int q = 0; q < len; q++)
            {
                scratch[q] = items[ord[cs[c] + q].Idx].Size;
            }

            medSize[c] = Median(scratch.AsSpan(0, len));
            for (int q = 0; q < len; q++)
            {
                scratch[q] = items[ord[cs[c] + q].Idx].Base;
            }

            medBase[c] = Median(scratch.AsSpan(0, len));
        }

        int[] parent = w.Ints.Rent(cc);
        for (int c = 0; c < cc; c++)
        {
            parent[c] = c;
        }

        for (int a = 0; a < cc; a++)
        {
            int best = -1;
            double bestD = double.MaxValue;
            for (int b = Math.Max(0, a - 4); b < Math.Min(cc, a + 5); b++)
            {
                if (b == a || medSize[a] > LayoutParameters.ScriptSizeRatio * medSize[b])
                {
                    continue;
                }

                double d = Math.Abs(medBase[a] - medBase[b]);
                if (d > LayoutParameters.ScriptOffsetEm * medSize[b] || d >= bestD)
                {
                    continue;
                }

                if (Touches(items, ord, cs[a], cs[a + 1], cs[b], cs[b + 1]))
                {
                    best = b;
                    bestD = d;
                }
            }

            if (best >= 0)
            {
                parent[a] = best;
            }
        }

        int[] id = w.Ints.Rent(cc);
        Array.Fill(id, -1, 0, cc);
        int count = 0;
        for (int c = 0; c < cc; c++)
        {
            int r = c;
            int guard = 0;
            while (parent[r] != r && guard++ < cc)
            {
                r = parent[r];
            }

            if (id[r] < 0)
            {
                id[r] = count++;
            }

            id[c] = id[r];
            for (int p = cs[c]; p < cs[c + 1]; p++)
            {
                items[ord[p].Idx].Line = id[r];
            }
        }

        return count;
    }

    /// <summary>
    /// True when some glyph of the small cluster is a script of a glyph of the larger one: smaller, with its baseline near the other's, and
    /// within a fraction of its own size horizontally. Checking glyph pairs keeps two real lines that merely sit near each other apart.
    /// </summary>
    private static bool Touches(Item[] items, Key2Idx[] ord, int s0, int s1, int b0, int b1)
    {
        for (int p = s0; p < s1; p++)
        {
            ref Item a = ref items[ord[p].Idx];
            if (a.Space)
            {
                continue;
            }

            for (int q = b0; q < b1; q++)
            {
                ref Item b = ref items[ord[q].Idx];
                if (b.Space)
                {
                    continue; // spaces are parked anywhere (often at the start of the next line) and say nothing about scripts
                }

                double gap = Math.Max(a.X0 - b.X1, b.X0 - a.X1);
                if (gap <= LayoutParameters.ScriptTouchEm * a.Size && a.Size <= LayoutParameters.ScriptSizeRatio * b.Size && Math.Abs(a.Base - b.Base) <= LayoutParameters.ScriptOffsetEm * b.Size)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The median of the values; sorts them in place.</summary>
    internal static double Median(Span<double> v)
    {
        if (v.Length == 0)
        {
            return 0;
        }

        // A run of bit-identical values (one font size, one baseline) is its own sorted order; skip the sort.
        long first = BitConverter.DoubleToInt64Bits(v[0]);
        int i = 1;
        while (i < v.Length && BitConverter.DoubleToInt64Bits(v[i]) == first)
        {
            i++;
        }

        if (i < v.Length)
        {
            v.Sort();
        }

        return (v[(v.Length - 1) / 2] + v[v.Length / 2]) / 2;
    }

    /// <summary>
    /// Buckets the items by line (in item order within a line) and sorts each line by x. Returns the item indices line after line;
    /// <paramref name="lineStart"/> has <c>lineCount + 1</c> offsets into it.
    /// </summary>
    internal static int[] BucketLines(LayoutWork w, Item[] items, int n, int lineCount, out int[] lineStart)
    {
        lineStart = w.Ints.Rent(lineCount + 1);
        Array.Clear(lineStart, 0, lineCount + 1);
        for (int i = 0; i < n; i++)
        {
            lineStart[items[i].Line + 1]++;
        }

        int maxLen = 0;
        for (int l = 0; l < lineCount; l++)
        {
            maxLen = Math.Max(maxLen, lineStart[l + 1]);
            lineStart[l + 1] += lineStart[l];
        }

        int[] fill = w.Ints.Rent(lineCount);
        Array.Copy(lineStart, fill, lineCount);
        int[] sorted = w.Ints.Rent(n);
        for (int i = 0; i < n; i++)
        {
            sorted[fill[items[i].Line]++] = i;
        }

        KeyIdx[] ks = w.Keys.Rent(maxLen);
        KeyIdx[] scratch = w.Keys.Rent(maxLen);
        for (int l = 0; l < lineCount; l++)
        {
            int s = lineStart[l], len = lineStart[l + 1] - s;
            if (len < 2)
            {
                continue;
            }

            for (int q = 0; q < len; q++)
            {
                int i = sorted[s + q];
                ks[q] = new KeyIdx { Key = items[i].X0, Idx = i };
            }

            IntroSort.SortNearlyOrdered<KeyIdx, ByKeyOrder>(ks.AsSpan(0, len), scratch);
            for (int q = 0; q < len; q++)
            {
                sorted[s + q] = ks[q].Idx;
            }
        }

        return sorted;
    }

    // ---- fragments --------------------------------------------------------------------------------------------------------------

    private static Frag[] Fragment(LayoutWork w, Item[] items, int[] ids, int lineCount, int[] lineStart, out int count)
    {
        Frag[] frags = w.Frags.Rent(lineStart[lineCount]);
        double[] scratch = w.Doubles.Rent(lineStart[lineCount]);
        int nf = 0;
        for (int l = 0; l < lineCount; l++)
        {
            int s = lineStart[l], e = lineStart[l + 1];
            int curStart = s;
            double lastX1 = double.NaN;
            double lastSize = 0;
            double coverEnd = double.NaN;
            for (int p = s; p < e; p++)
            {
                ref Item it = ref items[ids[p]];
                if (it.Space)
                {
                    // Space glyphs that follow the last word without a hole extend how far the gap is covered by explicit spaces.
                    if (!double.IsNaN(lastX1) && it.X0 <= (double.IsNaN(coverEnd) ? lastX1 : coverEnd) + LayoutParameters.SpaceCoverSlackEm * it.Size)
                    {
                        coverEnd = double.IsNaN(coverEnd) ? Math.Max(lastX1, it.X1) : Math.Max(coverEnd, it.X1);
                    }
                }
                else
                {
                    if (!double.IsNaN(lastX1) && p > curStart)
                    {
                        double gap = it.X0 - lastX1;
                        double avg = (lastSize + it.Size) / 2;

                        // A gap that space glyphs span end to end is a word space however wide the font made it (a fallback font's space, a justified
                        // line); only a gap with a hole in it separates cells.
                        bool spanned = !double.IsNaN(coverEnd) && it.X0 - coverEnd <= LayoutParameters.SpaceCoverSlackEm * avg;
                        if (gap > LayoutParameters.FragmentGapEm * avg && !spanned)
                        {
                            frags[nf++] = MakeFrag(items, ids, curStart, p, l, scratch);
                            curStart = p;
                        }
                    }

                    lastX1 = double.IsNaN(lastX1) ? it.X1 : Math.Max(lastX1, it.X1);
                    lastSize = it.Size;
                    coverEnd = double.NaN;
                }
            }

            if (e > curStart)
            {
                frags[nf++] = MakeFrag(items, ids, curStart, e, l, scratch);
            }
        }

        count = nf;
        return frags;
    }

    private static Frag MakeFrag(Item[] items, int[] ids, int start, int end, int line, double[] scratch)
    {
        Frag f = new() { Line = line, Start = start, Count = end - start, X0 = double.MaxValue, X1 = double.MinValue, Top = double.MaxValue, Bot = double.MinValue };
        double bs = 0;
        int ns = 0;
        for (int p = start; p < end; p++)
        {
            ref Item it = ref items[ids[p]];
            scratch[p - start] = it.Size;
            if (it.Space)
            {
                continue;
            }

            ns++;
            bs += it.Base;
            f.X0 = Math.Min(f.X0, it.X0);
            f.X1 = Math.Max(f.X1, it.X1);
            f.Top = Math.Min(f.Top, it.Top);
            f.Bot = Math.Max(f.Bot, it.Bot);
        }

        f.NonSpace = ns;
        if (ns == 0)
        {
            ref Item it0 = ref items[ids[start]];
            f.X0 = it0.X0;
            f.X1 = it0.X1;
            f.Top = it0.Top;
            f.Bot = it0.Bot;
            bs = it0.Base;
            ns = 1;
        }

        f.Base = bs / ns;
        f.Size = Median(scratch.AsSpan(0, end - start));
        return f;
    }

    // ---- XY-cut -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Cuts the fragments into leaf regions in reading order. A region is a run of <paramref name="reg"/>; a split rewrites it in place as its parts
    /// (sorted along the cut axis, or in their original order for a line-kind split) and pushes them on an explicit stack. A table keeps its region as it was.
    /// </summary>
    private static int XyCut(LayoutWork w, Frag[] f, Item[] items, int[] ids, int nf, int[] reg, int[] leafStart, int[] leafLen, int[] leafTable, CancellationToken ct)
    {
        int leaves = 0;
        int[] stS = w.Ints.Rent(nf + 1);
        int[] stL = w.Ints.Rent(nf + 1);
        int sp = 0;
        stS[sp] = 0;
        stL[sp++] = nf;
        KeyIdx[] ks = w.Keys.Rent(nf);
        int[] gb = w.Ints.Rent(nf + 2);
        int[] tmp = w.Ints.Rent(nf);
        double[] dscr = w.Doubles.Rent(nf);
        double[] stripesA = w.Doubles.Rent(2 * nf);
        double[] stripesB = w.Doubles.Rent(2 * nf);
        int guard = 0;
        while (sp > 0)
        {
            if ((++guard & 255) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            sp--;
            int s = stS[sp], len = stL[sp];
            if (len == 0)
            {
                continue;
            }

            if (len == 1 || DistinctLines(w, f, reg, s, len) <= 1)
            {
                AddLeaf(s, len, false);
                continue;
            }

            for (int i = 0; i < len; i++)
            {
                dscr[i] = f[reg[s + i]].Size;
            }

            double ms = Median(dscr.AsSpan(0, len));
            int groups = Partition(f, reg, s, len, true, LayoutParameters.ColumnGutterEm * ms, ks, gb);
            bool reverse = false;
            bool horizontal = false;
            if (groups > 0)
            {
                if (IsTable(w, f, reg, s, len, ks, gb, groups, ms, dscr))
                {
                    AddLeaf(s, len, true);
                    continue;
                }

                reverse = MajorityRtl(f, items, ids, reg, s, len);
            }
            else
            {
                groups = Partition(f, reg, s, len, false, LayoutParameters.BlockGapEm * ms, ks, gb);
                horizontal = true;
            }

            if (groups > 0)
            {
                for (int i = 0; i < len; i++)
                {
                    reg[s + i] = ks[i].Idx;
                }

                if (horizontal)
                {
                    groups = MergeColumnBands(f, reg, s, ks, gb, groups, LayoutParameters.ColumnGutterEm * ms, stripesA, stripesB);
                }
            }

            if (groups <= 1)
            {
                groups = SplitByLineKind(w, f, reg, s, len, ks, gb, tmp);
                if (groups == 0)
                {
                    AddLeaf(s, len, false);
                    continue;
                }
            }

            // Popped in group order; right-to-left text reverses the groups of a column split.
            if (reverse)
            {
                for (int j = 0; j < groups; j++)
                {
                    stS[sp] = s + gb[j];
                    stL[sp++] = gb[j + 1] - gb[j];
                }
            }
            else
            {
                for (int j = groups - 1; j >= 0; j--)
                {
                    stS[sp] = s + gb[j];
                    stL[sp++] = gb[j + 1] - gb[j];
                }
            }
        }

        return leaves;

        void AddLeaf(int s, int len, bool table)
        {
            leafStart[leaves] = s;
            leafLen[leaves] = len;
            leafTable[leaves++] = table ? 1 : 0;
        }
    }

    /// <summary>
    /// Joins neighbouring horizontal bands that are the same columns cut at paragraph gaps. Some other part of the region (a full-width note, a figure
    /// caption) crosses the gutter, so the vertical cut failed and the region is being cut into bands; if two successive bands each split into columns
    /// and share a gutter, the gap between them is only the paragraph gap that both columns have at the same height. Cutting there would read the
    /// columns band by band, so the bands are kept together and the next round cuts them into columns. <paramref name="gb"/> is compacted in place;
    /// returns the new group count.
    /// </summary>
    private static int MergeColumnBands(Frag[] f, int[] reg, int s, KeyIdx[] ks, int[] gb, int groups, double gap, double[] prev, double[] cur)
    {
        int pn = GutterStripes(f, reg, s + gb[0], gb[1] - gb[0], gap, ks, prev, out bool pm);
        int outGroups = 1;
        for (int j = 1; j < groups; j++)
        {
            int cn = GutterStripes(f, reg, s + gb[j], gb[j + 1] - gb[j], gap, ks, cur, out bool cm);
            if (pm && cm && ShareStripe(prev, pn, cur, cn, gap))
            {
                // Merged into the previous band: its start stays, this band's start is dropped.
            }
            else
            {
                gb[outGroups++] = gb[j];
            }

            (prev, cur) = (cur, prev);
            pn = cn;
            pm = cm;
        }

        gb[outGroups] = gb[groups];

        // 1 when everything merged: the region is whole again and the caller moves on to the next kind of cut.
        return outGroups;
    }

    /// <summary>Writes the empty vertical stripes (start, end pairs, at least <paramref name="gap"/> wide) of a run of fragments into <paramref name="stripes"/>; returns how many.</summary>
    private static int GutterStripes(Frag[] f, int[] reg, int s, int len, double gap, KeyIdx[] ks, double[] stripes, out bool paragraphs)
    {
        for (int i = 0; i < len; i++)
        {
            int id = reg[s + i];
            ks[i] = new KeyIdx { Key = f[id].X0, Idx = id };
        }

        IntroSort.Sort<KeyIdx, ByKeyOrder>(ks.AsSpan(0, len));
        int n = 0;
        bool multi = false;
        int firstLine = f[ks[0].Idx].Line;
        double curEnd = f[ks[0].Idx].X1;
        for (int i = 1; i < len; i++)
        {
            if (ks[i].Key - curEnd >= gap)
            {
                stripes[2 * n] = curEnd;
                stripes[2 * n + 1] = ks[i].Key;
                n++;
                firstLine = f[ks[i].Idx].Line;
            }
            else if (f[ks[i].Idx].Line != firstLine)
            {
                multi = true;
            }

            curEnd = Math.Max(curEnd, f[ks[i].Idx].X1);
        }

        paragraphs = n > 0 && multi;
        return n;
    }

    private static bool ShareStripe(double[] a, int an, double[] b, int bn, double gap)
    {
        int i = 0, j = 0;
        while (i < an && j < bn)
        {
            double lo = Math.Max(a[2 * i], b[2 * j]);
            double hi = Math.Min(a[2 * i + 1], b[2 * j + 1]);
            if (hi - lo >= gap)
            {
                return true;
            }

            if (a[2 * i + 1] < b[2 * j + 1])
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return false;
    }

    private static int DistinctLines(LayoutWork w, Frag[] f, int[] reg, int s, int len)
    {
        int gen = ++w.Generation;
        int[] stamp = w.Stamp;
        int c = 0;
        for (int i = 0; i < len; i++)
        {
            int line = f[reg[s + i]].Line;
            if (stamp[line] != gen)
            {
                stamp[line] = gen;
                if (++c > 1)
                {
                    break;
                }
            }
        }

        return c;
    }

    /// <summary>
    /// Sorts the region's fragments along one axis into <paramref name="ks"/> and finds every empty stripe of at least <paramref name="gap"/>. Returns the number of
    /// groups with their offsets in <paramref name="gb"/> (<c>groups + 1</c> entries), or 0 when there is no such stripe. The region itself is not changed.
    /// </summary>
    private static int Partition(Frag[] f, int[] reg, int s, int len, bool xAxis, double gap, KeyIdx[] ks, int[] gb)
    {
        for (int i = 0; i < len; i++)
        {
            int id = reg[s + i];
            ks[i] = new KeyIdx { Key = xAxis ? f[id].X0 : f[id].Top, Idx = id };
        }

        IntroSort.Sort<KeyIdx, ByKeyOrder>(ks.AsSpan(0, len));
        int groups = 1;
        gb[0] = 0;
        double curEnd = xAxis ? f[ks[0].Idx].X1 : f[ks[0].Idx].Bot;
        for (int i = 1; i < len; i++)
        {
            if (ks[i].Key - curEnd >= gap)
            {
                gb[groups++] = i;
            }

            curEnd = Math.Max(curEnd, xAxis ? f[ks[i].Idx].X1 : f[ks[i].Idx].Bot);
        }

        gb[groups] = len;
        return groups >= 2 ? groups : 0;
    }

    private static bool IsTable(LayoutWork w, Frag[] f, int[] reg, int s, int len, KeyIdx[] cols, int[] gb, int groups, double ms, double[] dscr)
    {
        int gen = ++w.Generation;
        int[] stamp = w.Stamp, firstCol = w.Aux1, multiRow = w.Aux2;
        int rows = 0, multi = 0;
        for (int c = 0; c < groups; c++)
        {
            for (int q = gb[c]; q < gb[c + 1]; q++)
            {
                int line = f[cols[q].Idx].Line;
                if (stamp[line] != gen)
                {
                    stamp[line] = gen;
                    firstCol[line] = c;
                    multiRow[line] = 0;
                    rows++;
                }
                else if (firstCol[line] != c && multiRow[line] == 0)
                {
                    multiRow[line] = 1;
                    multi++;
                }
            }
        }

        if (rows < LayoutParameters.TableMinRows)
        {
            return false;
        }

        if (multi < LayoutParameters.TableRowFraction * rows)
        {
            return false;
        }

        for (int i = 0; i < len; i++)
        {
            ref Frag fr = ref f[reg[s + i]];
            dscr[i] = (fr.X1 - fr.X0) / Math.Max(ms, 1e-6);
        }

        return Median(dscr.AsSpan(0, len)) <= LayoutParameters.TableCellWidthEm;
    }

    private static bool MajorityRtl(Frag[] f, Item[] items, int[] ids, int[] reg, int s, int len)
    {
        int rtl = 0, ltr = 0;
        for (int q = 0; q < len; q++)
        {
            ref Frag fr = ref f[reg[s + q]];
            CountDirections(items, ids, fr.Start, fr.Start + fr.Count, ref rtl, ref ltr);
        }

        return rtl > ltr;
    }

    private static void CountDirections(Item[] items, int[] ids, int start, int end, ref int rtl, ref int ltr)
    {
        for (int p = start; p < end; p++)
        {
            int cp = items[ids[p]].FirstCp;
            if (cp == 0)
            {
                continue;
            }

            BidiClass c = Bidi.GetClass(cp);
            if (Bidi.IsRtl(c))
            {
                rtl++;
            }
            else if (c == BidiClass.L)
            {
                ltr++;
            }
        }
    }

    /// <summary>
    /// Splits a region where lines with a gutter (several fragments) alternate with single-fragment lines. Rewrites the region as its runs
    /// (each in the region's order) and returns the run count with offsets in <paramref name="gb"/>; 0 when the lines are all of one kind.
    /// </summary>
    private static int SplitByLineKind(LayoutWork w, Frag[] f, int[] reg, int s, int len, KeyIdx[] ks, int[] gb, int[] tmp)
    {
        int gen = ++w.Generation;
        int[] stamp = w.Stamp, count = w.Aux1, runOf = w.Aux3;
        double[] top = w.DAux;
        int nl = 0;
        int[] lines = w.Ints.Rent(len);
        for (int i = 0; i < len; i++)
        {
            ref Frag fr = ref f[reg[s + i]];
            int line = fr.Line;
            if (stamp[line] != gen)
            {
                stamp[line] = gen;
                count[line] = 0;
                top[line] = fr.Base;
                lines[nl++] = line;
            }
            else
            {
                top[line] = Math.Min(top[line], fr.Base);
            }

            count[line]++;
        }

        for (int i = 0; i < nl; i++)
        {
            ks[i] = new KeyIdx { Key = top[lines[i]], Idx = lines[i] };
        }

        IntroSort.Sort<KeyIdx, ByKeyOrder>(ks.AsSpan(0, nl));
        int run = 0;
        bool prevMulti = count[ks[0].Idx] > 1;
        for (int i = 0; i < nl; i++)
        {
            int l = ks[i].Idx;
            bool multi = count[l] > 1;
            if (multi != prevMulti)
            {
                run++;
                prevMulti = multi;
            }

            runOf[l] = run;
        }

        if (run == 0)
        {
            return 0;
        }

        // Stable arrangement by run.
        int runs = run + 1;
        for (int j = 0; j <= runs; j++)
        {
            gb[j] = 0;
        }

        for (int i = 0; i < len; i++)
        {
            gb[runOf[f[reg[s + i]].Line] + 1]++;
        }

        for (int j = 0; j < runs; j++)
        {
            gb[j + 1] += gb[j];
        }

        Span<int> cur = runs <= 64 ? stackalloc int[runs] : new int[runs];
        for (int j = 0; j < runs; j++)
        {
            cur[j] = gb[j];
        }

        for (int i = 0; i < len; i++)
        {
            int id = reg[s + i];
            tmp[cur[runOf[f[id].Line]]++] = id;
        }

        Array.Copy(tmp, 0, reg, s, len);
        return runs;
    }

    // ---- blocks, lines, words ---------------------------------------------------------------------------------------------------

    private static PdfTextBlock? MakeBlock(LayoutWork w, PdfTextGlyph[] g, Item[] items, int[] ids, Frag[] f, int[] reg, int s, int len, bool table)
    {
        // Lines in order of first appearance, then sorted by the baseline of their first fragment.
        int gen = ++w.Generation;
        int[] stamp = w.Stamp, rank = w.Aux1;
        int[] firstFrag = w.Ints.Rent(len);
        int[] cnt = w.Ints.Rent(len);
        int nl = 0;
        int rtl = 0, ltr = 0;
        for (int i = 0; i < len; i++)
        {
            int fr = reg[s + i];
            int line = f[fr].Line;
            if (stamp[line] != gen)
            {
                stamp[line] = gen;
                rank[line] = nl;
                firstFrag[nl] = fr;
                cnt[nl] = 0;
                nl++;
            }

            cnt[rank[line]]++;
            CountDirections(items, ids, f[fr].Start, f[fr].Start + f[fr].Count, ref rtl, ref ltr);
        }

        KeyIdx[] ks = w.Keys.Rent(nl);
        for (int r = 0; r < nl; r++)
        {
            ks[r] = new KeyIdx { Key = f[firstFrag[r]].Base, Idx = r };
        }

        IntroSort.Sort<KeyIdx, ByKeyOrder>(ks.AsSpan(0, nl));

        int[] off = w.Ints.Rent(nl + 1);
        int[] cur = w.Ints.Rent(nl);
        off[0] = 0;
        for (int r = 0; r < nl; r++)
        {
            off[r + 1] = off[r] + cnt[r];
            cur[r] = off[r];
        }

        int[] bk = w.Ints.Rent(len);
        for (int i = 0; i < len; i++)
        {
            int fr = reg[s + i];
            bk[cur[rank[f[fr].Line]]++] = fr;
        }

        int para = rtl > ltr ? 1 : 0;
        KeyIdx[] ks2 = w.Keys.Rent(len);
        List<PdfTextLine> outLines = new(nl);
        double bl = double.MaxValue, bb = double.MaxValue, br = double.MinValue, bt = double.MinValue;
        for (int e = 0; e < nl; e++)
        {
            int r = ks[e].Idx;
            int os = off[r], oc = off[r + 1] - os;
            if (oc > 1)
            {
                for (int q = 0; q < oc; q++)
                {
                    ks2[q] = new KeyIdx { Key = f[bk[os + q]].X0, Idx = bk[os + q] };
                }

                IntroSort.Sort<KeyIdx, ByKeyOrder>(ks2.AsSpan(0, oc));
                for (int q = 0; q < oc; q++)
                {
                    bk[os + q] = ks2[q].Idx;
                }
            }

            PdfTextLine? line = MakeLine(w, g, items, ids, f, bk, os, oc, para);
            if (line is null)
            {
                continue;
            }

            outLines.Add(line);
            bl = Math.Min(bl, line.Bounds.Left);
            bb = Math.Min(bb, line.Bounds.Bottom);
            br = Math.Max(br, line.Bounds.Right);
            bt = Math.Max(bt, line.Bounds.Top);
        }

        return outLines.Count == 0 ? null : new PdfTextBlock(outLines.ToArray(), new PdfRectangle(bl, bb, br, bt), table);
    }

    /// <summary>
    /// Finds the next word in <c>ids[p .. end)</c>, starting at <paramref name="p"/>: runs of non-space items, split at gaps wider than the word-gap thresholds.
    /// Space items are skipped. Returns false at the end.
    /// </summary>
    internal static bool NextWord(Item[] items, int[] ids, ref int p, int end, out int start, out int length)
    {
        while (p < end && items[ids[p]].Space)
        {
            p++;
        }

        if (p >= end)
        {
            start = length = 0;
            return false;
        }

        start = p;
        ref Item prev = ref items[ids[p]];
        p++;
        while (p < end)
        {
            ref Item it = ref items[ids[p]];
            if (it.Space)
            {
                // A space drawn on top of the previous glyph (a producer that parks the trailing space of a line at its start) is no separator.
                if (it.X0 < prev.X1 - LayoutParameters.SpaceOverlapRatio * (it.X1 - it.X0))
                {
                    p++;
                    continue;
                }

                break;
            }

            double gap = it.X0 - prev.X1;
            double size = (prev.Size + it.Size) / 2;
            double thr = (prev.Cjk || it.Cjk ? LayoutParameters.CjkWordGapEm : LayoutParameters.WordGapEm) * size;
            if (gap > thr)
            {
                break;
            }

            prev = ref it;
            p++;
        }

        length = p - start;
        return true;
    }

    private static string WordText(PdfTextGlyph[] g, Item[] items, int[] ids, int start, int len)
    {
        // A word holds a space item only when the space was drawn over a glyph (see NextWord); it adds nothing to the text.
        if (len == 1)
        {
            return g[items[ids[start]].G].Text;
        }

        int total = 0;
        for (int k = 0; k < len; k++)
        {
            ref Item it = ref items[ids[start + k]];
            if (!it.Space)
            {
                total += g[it.G].Text.Length;
            }
        }

        if (total == 0)
        {
            return "";
        }

        return string.Create(total, (g, items, ids, start, len), static (span, st) =>
        {
            int pos = 0;
            for (int k = 0; k < st.len; k++)
            {
                ref Item it = ref st.items[st.ids[st.start + k]];
                if (it.Space)
                {
                    continue;
                }

                string t = st.g[it.G].Text;
                t.CopyTo(span.Slice(pos));
                pos += t.Length;
            }
        });
    }

    /// <summary>
    /// The word as a layout engine would have put it on the page, for <see cref="Reorder"/>. A single glyph that stands for several right-to-left
    /// characters (an Arabic lam-alef or lam-yeh ligature) holds them in logical order in its text; the page shows them reversed with the
    /// rest of the run, so their order inside the glyph is reversed here and the reordering restores it.
    /// </summary>
    private static string VisualWordText(PdfTextGlyph[] g, Item[] items, int[] ids, int start, int len)
    {
        var sb = new StringBuilder();
        for (int k = 0; k < len; k++)
        {
            ref Item it = ref items[ids[start + k]];
            if (it.Space)
            {
                continue;
            }

            string t = g[it.G].Text;
            if (t.Length > 1 && IsAllRtl(t))
            {
                for (int i = t.Length - 1; i >= 0; i--)
                {
                    if (char.IsLowSurrogate(t[i]) && i > 0 && char.IsHighSurrogate(t[i - 1]))
                    {
                        sb.Append(t[i - 1]).Append(t[i]);
                        i--;
                    }
                    else
                    {
                        sb.Append(t[i]);
                    }
                }
            }
            else
            {
                sb.Append(t);
            }
        }

        return sb.ToString();
    }

    // True when every character with a direction of its own is right to left (marks and neutrals do not count) and there is at least one.
    private static bool IsAllRtl(string t)
    {
        bool any = false;
        for (int i = 0; i < t.Length; i++)
        {
            int cp = char.IsHighSurrogate(t[i]) && i + 1 < t.Length && char.IsLowSurrogate(t[i + 1]) ? char.ConvertToUtf32(t[i], t[i + 1]) : t[i];
            if (cp > 0xFFFF)
            {
                i++;
            }

            BidiClass c = Bidi.GetClass(cp);
            if (Bidi.IsRtl(c))
            {
                any = true;
            }
            else if (c is BidiClass.L or BidiClass.EN or BidiClass.AN)
            {
                return false;
            }
        }

        return any;
    }

    /// <summary>The length <see cref="WordText"/> would have, without building it.</summary>
    private static int WordTextLength(PdfTextGlyph[] g, Item[] items, int[] ids, int start, int len)
    {
        if (len == 1)
        {
            return g[items[ids[start]].G].Text.Length;
        }

        int total = 0;
        for (int k = 0; k < len; k++)
        {
            ref Item it = ref items[ids[start + k]];
            if (!it.Space)
            {
                total += g[it.G].Text.Length;
            }
        }

        return total;
    }

    /// <summary>Writes what <see cref="WordText"/> would return into <paramref name="span"/> and returns its length.</summary>
    private static int WriteWordText(Span<char> span, PdfTextGlyph[] g, Item[] items, int[] ids, int start, int len)
    {
        if (len == 1)
        {
            string one = g[items[ids[start]].G].Text;
            one.CopyTo(span);
            return one.Length;
        }

        int pos = 0;
        for (int k = 0; k < len; k++)
        {
            ref Item it = ref items[ids[start + k]];
            if (it.Space)
            {
                continue;
            }

            string t = g[it.G].Text;
            t.CopyTo(span.Slice(pos));
            pos += t.Length;
        }

        return pos;
    }

    private static PdfTextLine? MakeLine(LayoutWork w, PdfTextGlyph[] g, Item[] items, int[] ids, Frag[] f, int[] bk, int off, int nfr, int para)
    {
        int total = 0;
        for (int fi = 0; fi < nfr; fi++)
        {
            total += f[bk[off + fi]].Count;
        }

        WordRec[] wr = w.Words.Rent(total);
        int wc = 0;
        for (int fi = 0; fi < nfr; fi++)
        {
            ref Frag fr = ref f[bk[off + fi]];
            int p = fr.Start, end = fr.Start + fr.Count;
            bool first = true;
            while (NextWord(items, ids, ref p, end, out int ws, out int wl))
            {
                wr[wc++] = new WordRec { Start = ws, Len = wl, Cell = first && fi > 0 };
                first = false;
            }
        }

        if (wc == 0)
        {
            return null;
        }

        wr[0].Cell = true;

        bool needBidi = para == 1;
        for (int i = 0; i < wc && !needBidi; i++)
        {
            for (int k = 0; k < wr[i].Len && !needBidi; k++)
            {
                string t = g[items[ids[wr[i].Start + k]].G].Text;
                needBidi = t.Length > 0 && MayBeRtl(t);
            }
        }

        string[]? logical = null;
        int[]? order = null;
        bool[]? cellStart = null;
        if (needBidi)
        {
            string[] visual = new string[wc];
            cellStart = new bool[wc];
            for (int i = 0; i < wc; i++)
            {
                visual[i] = VisualWordText(g, items, ids, wr[i].Start, wr[i].Len);
                cellStart[i] = wr[i].Cell;
            }

            Reorder(visual, cellStart, para, out logical, out order, out cellStart);
        }

        // A result that drops its glyphs keeps each word as a small record; PdfTextLine makes the word objects only if its Words are read.
        bool lazy = !w.KeepWordGlyphs && !needBidi;
        PdfTextWord[] outWords = lazy ? [] : new PdfTextWord[wc];
        PdfTextWordRecord[] records = lazy ? new PdfTextWordRecord[wc] : [];
        int ow = 0;
        int[] cells = w.Ints.Rent(wc + 1);
        int[] kept = w.Ints.Rent(wc);
        int cellCount = 0;
        int textPos = 0; // where the next word starts in the line's text (words are separated by one character)
        double ll = double.MaxValue, lb = double.MaxValue, lr = double.MinValue, lt = double.MinValue;
        for (int i = 0; i < wc; i++)
        {
            WordRec rec = needBidi ? wr[order![i]] : wr[i];
            string? text = needBidi ? logical![i] : null;
            int textLen = needBidi ? text!.Length : WordTextLength(g, items, ids, rec.Start, rec.Len);
            if (textLen == 0)
            {
                continue;
            }

            int ng = 0;
            for (int k = 0; k < rec.Len; k++)
            {
                if (!items[ids[rec.Start + k]].Space)
                {
                    ng++;
                }
            }

            // A result that drops its glyphs anyway never needs the indices.
            int[] gi = w.KeepWordGlyphs ? new int[ng] : [];
            ng = 0;
            double wl = double.MaxValue, wbm = double.MaxValue, wr2 = double.MinValue, wt = double.MinValue;
            bool vis = false;
            for (int k = 0; k < rec.Len; k++)
            {
                if (items[ids[rec.Start + k]].Space)
                {
                    continue;
                }

                int gx = items[ids[rec.Start + k]].G;
                if (w.KeepWordGlyphs)
                {
                    gi[ng++] = gx;
                }

                PdfRectangle b = g[gx].Bounds;
                wl = Math.Min(wl, b.Left);
                wbm = Math.Min(wbm, b.Bottom);
                wr2 = Math.Max(wr2, b.Right);
                wt = Math.Max(wt, b.Top);
                vis |= g[gx].IsVisible;
            }

            if (needBidi ? cellStart![i] : rec.Cell)
            {
                cells[cellCount++] = ow;
            }

            kept[ow] = needBidi ? 0 : i;
            if (lazy)
            {
                records[ow++] = new PdfTextWordRecord(textPos, textLen, new PdfRectangle(wl, wbm, wr2, wt), vis);
            }
            else
            {
                outWords[ow++] = needBidi
                    ? new PdfTextWord(text!, new PdfRectangle(wl, wbm, wr2, wt), gi, vis)
                    : new PdfTextWord(textPos, textLen, new PdfRectangle(wl, wbm, wr2, wt), gi, vis);
            }

            textPos += textLen + 1;
            ll = Math.Min(ll, wl);
            lb = Math.Min(lb, wbm);
            lr = Math.Max(lr, wr2);
            lt = Math.Max(lt, wt);
        }

        if (ow == 0)
        {
            return null;
        }

        if (ow != wc)
        {
            if (lazy)
            {
                Array.Resize(ref records, ow);
            }
            else
            {
                Array.Resize(ref outWords, ow);
            }
        }

        bool lead = cellCount == 0 || cells[0] != 0;
        int[] cellStarts = new int[cellCount + (lead ? 1 : 0)];
        Array.Copy(cells, 0, cellStarts, lead ? 1 : 0, cellCount);

        // Words are separated by a space, and a word that starts a cell by a tab.
        int lineLen = textPos - 1;
        string lineText;
        if (needBidi)
        {
            lineText = string.Create(lineLen, (outWords, cellStarts), static (span, st) =>
            {
                int pos = 0, ci = 0;
                for (int i = 0; i < st.outWords.Length; i++)
                {
                    if (i > 0)
                    {
                        while (ci < st.cellStarts.Length && st.cellStarts[ci] < i)
                        {
                            ci++;
                        }

                        span[pos++] = ci < st.cellStarts.Length && st.cellStarts[ci] == i ? '\t' : ' ';
                    }

                    string t = st.outWords[i].Text;
                    t.CopyTo(span.Slice(pos));
                    pos += t.Length;
                }
            });
        }
        else
        {
            // The words' texts are slices of this string; they are cut out only when someone asks.
            lineText = string.Create(lineLen, (g, items, ids, wr, kept, count: ow, cellStarts), static (span, st) =>
            {
                int pos = 0, ci = 0;
                for (int i = 0; i < st.count; i++)
                {
                    if (i > 0)
                    {
                        while (ci < st.cellStarts.Length && st.cellStarts[ci] < i)
                        {
                            ci++;
                        }

                        span[pos++] = ci < st.cellStarts.Length && st.cellStarts[ci] == i ? '\t' : ' ';
                    }

                    WordRec rec = st.wr[st.kept[i]];
                    pos += WriteWordText(span.Slice(pos), st.g, st.items, st.ids, rec.Start, rec.Len);
                }
            });
            if (!lazy)
            {
                for (int i = 0; i < ow; i++)
                {
                    outWords[i].SetLine(lineText);
                }
            }
        }

        PdfTextDirection direction = para == 1 ? PdfTextDirection.RightToLeft : PdfTextDirection.LeftToRight;
        bool vertical = g[items[ids[wr[0].Start]].G].IsVertical;
        return lazy
            ? new PdfTextLine(records, new PdfRectangle(ll, lb, lr, lt), direction, vertical, cellStarts, lineText)
            : new PdfTextLine(outWords, new PdfRectangle(ll, lb, lr, lt), direction, vertical, cellStarts, lineText);
    }

    private static bool MayBeRtl(string t)
    {
        for (int k = 0; k < t.Length; k++)
        {
            char c = t[k];
            if ((c >= '\u0590' && c <= '\u08FF') || (c >= '\uFB1D' && c <= '\uFEFF') || char.IsHighSurrogate(c) || (c >= '\u200E' && c <= '\u200F') || (c >= '\u202A' && c <= '\u202E') || (c >= '\u2066' && c <= '\u2069'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Puts words that were read in visual order (left to right) into logical order. The visual string is treated as the input of the
    /// bidirectional algorithm and reordered: that inverts the reordering a layout engine applied to produce it.
    /// </summary>
    private static void Reorder(string[] visual, bool[] cellStart, int para, out string[] logical, out int[] order, out bool[] cells)
    {
        int wc = visual.Length;
        List<int> cps = [];
        List<int> owner = [];
        for (int w = 0; w < wc; w++)
        {
            if (w > 0)
            {
                cps.Add(cellStart[w] ? '\t' : ' ');
                owner.Add(-1);
            }

            string s = visual[w];
            for (int i = 0; i < s.Length; i++)
            {
                int cp = char.ConvertToUtf32(s, i);
                if (cp > 0xFFFF)
                {
                    i++;
                }

                cps.Add(cp);
                owner.Add(w);
            }
        }

        int[] arr = cps.ToArray();
        byte[] levels = new byte[arr.Length];
        Bidi.ResolveLevels(arr, para, levels);
        int[] vis = new int[arr.Length];
        int m = Bidi.VisualOrder(levels, vis);

        StringBuilder?[] sbs = new StringBuilder?[wc];
        List<int> wordOrder = [];
        bool[] cellNext = new bool[wc];
        bool pendingTab = false;
        for (int p = 0; p < m; p++)
        {
            int ci = vis[p];
            int w = owner[ci];
            if (w < 0)
            {
                pendingTab |= arr[ci] == '\t';
                continue;
            }

            if (sbs[w] is null)
            {
                sbs[w] = new StringBuilder();
                wordOrder.Add(w);
                cellNext[w] = pendingTab;
            }

            pendingTab = false;
            sbs[w]!.Append(char.ConvertFromUtf32(arr[ci]));
        }

        logical = new string[wc];
        order = new int[wc];
        cells = new bool[wc];
        int o = 0;
        foreach (int w in wordOrder)
        {
            logical[o] = sbs[w]!.ToString();
            order[o] = w;
            cells[o] = cellNext[w];
            o++;
        }

        for (; o < wc; o++)
        {
            logical[o] = "";
        }

        if (wc > 0)
        {
            cells[0] = true;
        }
    }
}
