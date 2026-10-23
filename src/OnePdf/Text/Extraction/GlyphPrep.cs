using System.Buffers;
using System.Text;

namespace OnePdf.Text;

/// <summary>Per-glyph clean-up before layout: exclusions, <c>/ActualText</c>, ligatures, duplicates.</summary>
internal static class GlyphPrep
{
    /// <summary>Characters below this are never changed by <see cref="Decompose"/>, <see cref="FoldCompatibility"/> or <see cref="Sanitize"/>; a test holds the three to it.</summary>
    internal const char PlainBelow = '\u2E80';

    /// <summary>
    /// Prepares the glyphs of a page. With <paramref name="pooled"/> the result array is rented from the shared pool and may be longer than
    /// <paramref name="count"/>; the caller returns it. Otherwise it has exactly <paramref name="count"/> elements.
    /// </summary>
    internal static PdfTextGlyph[] Prepare(PdfPageGlyphs page, PdfTextOptions o, bool pooled, out int count, CancellationToken ct)
    {
        ReadOnlySpan<PdfTextGlyph> src;
        PdfTextGlyph[] work;
        int n;
        if (pooled && page.TakePooled(out PdfTextGlyph[]? owned, out n))
        {
            // The page's own pooled buffer is compacted in place: kept glyphs only ever move to a lower index.
            work = owned;
            src = owned.AsSpan(0, n);
        }
        else
        {
            src = page.Span;
            if (src.IsEmpty && page.Glyphs.Count > 0)
            {
                src = page.Glyphs is PdfTextGlyph[] arr ? arr : [.. page.Glyphs];
            }

            n = src.Length;
            work = pooled ? ArrayPool<PdfTextGlyph>.Shared.Rent(n) : new PdfTextGlyph[n];
        }

        // The duplicate search wants each kept glyph's x, y and size; they are taken here, while the glyph is in hand.
        bool collapse = o.CollapseDuplicates && n > 1;
        double[]? ex = collapse ? ArrayPool<double>.Shared.Rent(n) : null;
        double[]? ef = collapse ? ArrayPool<double>.Shared.Rent(n) : null;
        double[]? es = collapse ? ArrayPool<double>.Shared.Rent(n) : null;
        int kept = 0;
        try
        {
            int curSpan = 0;
            bool replacing = false;
            bool first = false;

            for (int i = 0; i < n; i++)
            {
                if ((i & 1023) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                ref readonly PdfTextGlyph g = ref src[i];
                if (!IsFinite(g.Matrix) || !double.IsFinite(g.Advance) || !double.IsFinite(g.Ascent) || !double.IsFinite(g.Descent))
                {
                    continue;
                }

                if (!o.IncludeClippedAndOffPage)
                {
                    if (g.IsClipped)
                    {
                        continue;
                    }

                    g.Corners(out double ax, out double ay, out double bx, out double by, out double cx, out double cy, out double dx, out double dy);
                    double cxm = (Math.Min(Math.Min(ax, bx), Math.Min(cx, dx)) + Math.Max(Math.Max(ax, bx), Math.Max(cx, dx))) / 2;
                    double cym = (Math.Min(Math.Min(ay, by), Math.Min(cy, dy)) + Math.Max(Math.Max(ay, by), Math.Max(cy, dy))) / 2;
                    if (cxm < -LayoutParameters.OffPageMargin || cym < -LayoutParameters.OffPageMargin ||
                        cxm > page.Width + LayoutParameters.OffPageMargin || cym > page.Height + LayoutParameters.OffPageMargin)
                    {
                        continue;
                    }
                }

                string text = g.Text ?? "";
                if (g.ActualTextSpan != 0)
                {
                    if (g.ActualTextSpan != curSpan)
                    {
                        curSpan = g.ActualTextSpan;
                        replacing = g.ActualText is not null;
                        first = true;
                    }

                    if (replacing)
                    {
                        text = first ? g.ActualText! : "";
                        first = false;
                    }
                }
                else
                {
                    curSpan = 0;
                    replacing = false;
                }

                // A single character below U+2E80 is untouched by ligature decomposition, compatibility folding and surrogate repair
                // (all three only act on U+2E80 and above); most text is that, so it skips the three scans.
                if (!(text.Length == 1 && text[0] < PlainBelow))
                {
                    if (o.DecomposeLigatures)
                    {
                        text = Decompose(text);
                    }

                    if (o.FoldCompatibilityForms)
                    {
                        text = FoldCompatibility(text);
                    }

                    text = Sanitize(text);
                }

                if (collapse)
                {
                    ex![kept] = g.Matrix.E;
                    ef![kept] = g.Matrix.F;
                    es![kept] = g.Size;
                }

                work[kept++] = ReferenceEquals(text, g.Text) ? g : g.WithText(text);
            }

            if (collapse && kept > 1)
            {
                kept = CollapseCore(work, kept, ex!, ef!, es!);
            }
        }
        finally
        {
            if (collapse)
            {
                ArrayPool<double>.Shared.Return(ex!);
                ArrayPool<double>.Shared.Return(ef!);
                ArrayPool<double>.Shared.Return(es!);
            }
        }

        // The result is exactly sized; the common case (nothing dropped) hands the working array over without a copy.
        if (kept != n && !pooled)
        {
            Array.Resize(ref work, kept);
        }

        count = kept;
        return work;
    }

    private static bool IsFinite(in PdfTextMatrix m) =>
        double.IsFinite(m.A) && double.IsFinite(m.B) && double.IsFinite(m.C) && double.IsFinite(m.D) && double.IsFinite(m.E) && double.IsFinite(m.F);

    /// <summary>The Latin ligature code points of the Alphabetic Presentation Forms block.</summary>
    internal static string Decompose(string s)
    {
        int idx = -1;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] >= '\uFB00' && s[i] <= '\uFB06')
            {
                idx = i;
                break;
            }
        }

        if (idx < 0)
        {
            return s;
        }

        StringBuilder sb = new(s.Length + 2);
        foreach (char c in s)
        {
            sb.Append(c switch
            {
                '\uFB00' => "ff",
                '\uFB01' => "fi",
                '\uFB02' => "fl",
                '\uFB03' => "ffi",
                '\uFB04' => "ffl",
                '\uFB05' => "st",
                '\uFB06' => "st",
                _ => c.ToString(),
            });
        }

        return sb.ToString();
    }

    private static bool MayFold(char c) =>
        c >= '⺀' && (c <= '⿟' || (c >= '豈' && c <= '﫿') || (c >= 'יִ' && c <= '﷿') || (c >= 'ﹰ' && c <= '﻿') || char.IsHighSurrogate(c));

    /// <summary>
    /// Replaces presentation forms and compatibility characters by the characters they stand for (Kangxi and CJK radicals, CJK
    /// compatibility ideographs, Hebrew and Arabic presentation forms): the full decomposition from the Unicode Character Database,
    /// from a generated table because <c>string.Normalize</c> does nothing when globalization is invariant. Latin ligatures
    /// U+FB00 to U+FB06 are left to <see cref="Decompose"/>.
    /// </summary>
    internal static string FoldCompatibility(string s)
    {
        int first = -1;
        for (int i = 0; i < s.Length; i++)
        {
            if (MayFold(s[i]))
            {
                first = i;
                break;
            }
        }

        if (first < 0)
        {
            return s;
        }

        ReadOnlySpan<int> keys = FoldData.Keys;
        StringBuilder sb = new(s.Length + 8);
        bool changed = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            int cp = c;
            int len = 1;
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                cp = char.ConvertToUtf32(c, s[i + 1]);
                len = 2;
            }

            int k = cp >= 0x2E80 ? keys.BinarySearch(cp) : -1;
            if (k >= 0)
            {
                ReadOnlySpan<int> offsets = FoldData.Offsets;
                sb.Append(FoldData.Text[offsets[k]..offsets[k + 1]]);
                changed = true;
            }
            else
            {
                sb.Append(s, i, len);
            }

            i += len - 1;
        }

        return changed ? sb.ToString() : s;
    }

    /// <summary>Replaces unpaired surrogates by U+FFFD so every string can be written as UTF-8 and JSON.</summary>
    internal static string Sanitize(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsSurrogate(c))
            {
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    i++;
                    continue;
                }

                return Fix(s);
            }
        }

        return s;

        static string Fix(string s)
        {
            StringBuilder sb = new(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    sb.Append(c).Append(s[++i]);
                }
                else if (char.IsSurrogate(c))
                {
                    sb.Append('\uFFFD');
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// Drops a glyph drawn again at almost the same place by a later operator (fake bold, drop shadow), compacting <paramref name="g"/> in place.
    /// Returns the new count. Candidates are visited by origin x, ties by position in the stream; that is a total order, so any correct sort gives
    /// the same visiting order.
    /// </summary>
    internal static int CollapseDuplicates(PdfTextGlyph[] g, int n)
    {
        double[] x = new double[n], f = new double[n], s = new double[n];
        for (int i = 0; i < n; i++)
        {
            x[i] = g[i].Matrix.E;
            f[i] = g[i].Matrix.F;
            s[i] = g[i].Size;
        }

        return CollapseCore(g, n, x, f, s);
    }

    /// <summary>The duplicate search over glyphs whose x, y (origin) and size are already in <paramref name="ex"/>, <paramref name="ef"/>, <paramref name="es"/>.</summary>
    private static int CollapseCore(PdfTextGlyph[] g, int n, double[] ex, double[] ef, double[] es)
    {
        ulong[] k0 = ArrayPool<ulong>.Shared.Rent(n);
        ulong[] k1 = ArrayPool<ulong>.Shared.Rent(n);
        int[] i0 = ArrayPool<int>.Shared.Rent(n);
        int[] i1 = ArrayPool<int>.Shared.Rent(n);
        bool[] drop = ArrayPool<bool>.Shared.Rent(n);
        double[] sx = ArrayPool<double>.Shared.Rent(n);
        double[] sf = ArrayPool<double>.Shared.Rent(n);
        double[] ss = ArrayPool<double>.Shared.Rent(n);
        try
        {
            for (int i = 0; i < n; i++)
            {
                k0[i] = SortableBits(ex[i]);
                i0[i] = i;
                drop[i] = false;
            }

            int[] order = SortByX(k0, i0, k1, i1, n, out _);

            // The three values the scan reads, laid out in visiting order so that it runs over contiguous memory.
            for (int p = 0; p < n; p++)
            {
                int i = order[p];
                sx[p] = ex[i];
                sf[p] = ef[i];
                ss[p] = es[i];
            }

            bool any = false;
            for (int p = 0; p < n; p++)
            {
                double size = ss[p];
                double tol = LayoutParameters.DuplicateOffsetEm * size;
                double x = sx[p], f = sf[p];
                int limit = Math.Min(n, p + 1 + 32);
                for (int q = p + 1; q < limit; q++)
                {
                    if (sx[q] - x > tol)
                    {
                        break;
                    }

                    // Cheapest test first; all of them must hold.
                    if (Math.Abs(sf[q] - f) > tol)
                    {
                        continue;
                    }

                    int i = order[p], j = order[q];
                    if (g[i].Text.Length == 0 || g[i].Text != g[j].Text || g[i].IsVertical != g[j].IsVertical)
                    {
                        continue;
                    }

                    if (Math.Abs(ss[q] - size) > 0.05 * size)
                    {
                        continue;
                    }

                    drop[Math.Max(i, j)] = true;
                    any = true;
                }
            }

            if (!any)
            {
                return n;
            }

            int kept = 0;
            for (int i = 0; i < n; i++)
            {
                if (!drop[i])
                {
                    g[kept++] = g[i];
                }
            }

            Array.Clear(g, kept, n - kept);
            return kept;
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(k0);
            ArrayPool<ulong>.Shared.Return(k1);
            ArrayPool<int>.Shared.Return(i0);
            ArrayPool<int>.Shared.Return(i1);
            ArrayPool<bool>.Shared.Return(drop);
            ArrayPool<double>.Shared.Return(sx);
            ArrayPool<double>.Shared.Return(sf);
            ArrayPool<double>.Shared.Return(ss);
        }
    }

    /// <summary>A key whose unsigned order is the numeric order of a finite double (−0 and +0 equal).</summary>
    private static ulong SortableBits(double v)
    {
        long bits = BitConverter.DoubleToInt64Bits(v + 0.0);
        return bits < 0 ? ~(ulong)bits : (ulong)bits | 0x8000_0000_0000_0000UL;
    }

    /// <summary>
    /// Orders indices by key, equal keys by index: three stable radix passes over the top 33 bits (about 1e-4 of a point at page
    /// coordinates), then an insertion pass that settles the few elements whose top bits agree. Returns the array holding the order.
    /// </summary>
    private static int[] SortByX(ulong[] k0, int[] i0, ulong[] k1, int[] i1, int n, out ulong[] keys)
    {
        Span<int> count = stackalloc int[2048];
        ulong[] ks = k0, kd = k1;
        int[] ids = i0, idd = i1;
        for (int shift = 31; shift < 64; shift += 11)
        {
            count.Clear();
            for (int i = 0; i < n; i++)
            {
                count[(int)((ks[i] >> shift) & 2047)]++;
            }

            if (count[(int)((ks[0] >> shift) & 2047)] == n)
            {
                continue; // every key shares this digit
            }

            int sum = 0;
            for (int b = 0; b < 2048; b++)
            {
                int c = count[b];
                count[b] = sum;
                sum += c;
            }

            for (int i = 0; i < n; i++)
            {
                int d = (int)((ks[i] >> shift) & 2047);
                int at = count[d]++;
                kd[at] = ks[i];
                idd[at] = ids[i];
            }

            (ks, kd) = (kd, ks);
            (ids, idd) = (idd, ids);
        }

        // Within a group that agrees on the top bits the order is not settled yet. Most groups hold one or two elements; a large group
        // (many glyphs at almost the same x) is sorted properly so hostile input cannot make this quadratic.
        for (int s = 0; s < n;)
        {
            ulong top = ks[s] >> 31;
            int e = s + 1;
            while (e < n && (ks[e] >> 31) == top)
            {
                e++;
            }

            if (e - s > 24)
            {
                Array.Sort(ks, ids, s, e - s);
                for (int a = s; a < e;)
                {
                    int z = a + 1;
                    while (z < e && ks[z] == ks[a])
                    {
                        z++;
                    }

                    if (z - a > 1)
                    {
                        Array.Sort(ids, a, z - a);
                    }

                    a = z;
                }
            }
            else
            {
                for (int i = s + 1; i < e; i++)
                {
                    ulong key = ks[i];
                    int id = ids[i];
                    int j = i - 1;
                    while (j >= s && (ks[j] > key || (ks[j] == key && ids[j] > id)))
                    {
                        ks[j + 1] = ks[j];
                        ids[j + 1] = ids[j];
                        j--;
                    }

                    ks[j + 1] = key;
                    ids[j + 1] = id;
                }
            }

            s = e;
        }

        keys = ks;
        return ids;
    }
}
