using System.Buffers;
using System.Text;

namespace OnePdf.Text;

/// <summary>Per-glyph clean-up before layout: exclusions, <c>/ActualText</c>, ligatures, duplicates.</summary>
internal static class GlyphPrep
{
    internal static PdfTextGlyph[] Prepare(PdfPageGlyphs page, PdfTextOptions o, CancellationToken ct)
    {
        IReadOnlyList<PdfTextGlyph> src = page.Glyphs;
        int n = src.Count;
        PdfTextGlyph[] work = new PdfTextGlyph[n];
        int kept = 0;
        int curSpan = 0;
        bool replacing = false;
        bool first = false;

        for (int i = 0; i < n; i++)
        {
            if ((i & 1023) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            PdfTextGlyph g = src[i];
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

                PdfRectangle b = g.Bounds;
                double cx = (b.Left + b.Right) / 2, cy = (b.Bottom + b.Top) / 2;
                if (cx < -LayoutParameters.OffPageMargin || cy < -LayoutParameters.OffPageMargin ||
                    cx > page.Width + LayoutParameters.OffPageMargin || cy > page.Height + LayoutParameters.OffPageMargin)
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

            if (o.DecomposeLigatures)
            {
                text = Decompose(text);
            }

            if (o.FoldCompatibilityForms)
            {
                text = FoldCompatibility(text);
            }

            text = Sanitize(text);
            work[kept++] = ReferenceEquals(text, g.Text) ? g : g.WithText(text);
        }

        if (o.CollapseDuplicates && kept > 1)
        {
            kept = CollapseDuplicates(work, kept);
        }

        // The result is exactly sized; the common case (nothing dropped) hands the working array over without a copy.
        if (kept != n)
        {
            Array.Resize(ref work, kept);
        }

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

    /// <summary>A glyph origin with the stream position it came from; the working set of the duplicate search.</summary>
    private struct Origin
    {
        internal double E, F, Size;
        internal int Idx;
    }

    /// <summary>
    /// Drops a glyph drawn again at almost the same place by a later operator (fake bold, drop shadow), compacting <paramref name="g"/> in place.
    /// Returns the new count. Candidates are visited by origin x, ties by position in the stream; that is a total order, so any correct sort gives
    /// the same visiting order.
    /// </summary>
    private static int CollapseDuplicates(PdfTextGlyph[] g, int n)
    {
        double[] keys = ArrayPool<double>.Shared.Rent(n);
        int[] order = ArrayPool<int>.Shared.Rent(n);
        Origin[] o = ArrayPool<Origin>.Shared.Rent(n);
        bool[] drop = ArrayPool<bool>.Shared.Rent(n);
        try
        {
            for (int i = 0; i < n; i++)
            {
                keys[i] = g[i].Matrix.E;
                order[i] = i;
                drop[i] = false;
            }

            // Sort by x with the primitive sort (no delegate per comparison), then put equal x in stream order.
            Array.Sort(keys, order, 0, n);
            for (int s = 0; s < n;)
            {
                int e = s + 1;
                while (e < n && keys[e] == keys[s])
                {
                    e++;
                }

                if (e - s > 1)
                {
                    Array.Sort(order, s, e - s);
                }

                s = e;
            }

            for (int p = 0; p < n; p++)
            {
                ref readonly PdfTextGlyph src = ref g[order[p]];
                o[p] = new Origin { E = keys[p], F = src.Matrix.F, Size = src.Size, Idx = order[p] };
            }

            bool any = false;
            for (int p = 0; p < n; p++)
            {
                int i = o[p].Idx;
                double size = o[p].Size;
                double tol = LayoutParameters.DuplicateOffsetEm * size;
                for (int q = p + 1, steps = 0; q < n && steps < 32; q++, steps++)
                {
                    if (o[q].E - o[p].E > tol)
                    {
                        break;
                    }

                    // Cheapest test first; all of them must hold.
                    if (Math.Abs(o[q].F - o[p].F) > tol)
                    {
                        continue;
                    }

                    int j = o[q].Idx;
                    if (g[i].Text.Length == 0 || g[i].Text != g[j].Text || g[i].IsVertical != g[j].IsVertical)
                    {
                        continue;
                    }

                    if (Math.Abs(o[q].Size - size) > 0.05 * size)
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
            ArrayPool<double>.Shared.Return(keys);
            ArrayPool<int>.Shared.Return(order);
            ArrayPool<Origin>.Shared.Return(o);
            ArrayPool<bool>.Shared.Return(drop);
        }
    }
}
