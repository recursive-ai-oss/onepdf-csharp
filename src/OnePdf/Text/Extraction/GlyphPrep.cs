using System.Text;

namespace OnePdf.Text;

/// <summary>Per-glyph clean-up before layout: exclusions, <c>/ActualText</c>, ligatures, duplicates.</summary>
internal static class GlyphPrep
{
    internal static PdfGlyph[] Prepare(PdfPageGlyphs page, PdfTextOptions o, CancellationToken ct)
    {
        IReadOnlyList<PdfGlyph> src = page.Glyphs;
        int n = src.Count;
        List<PdfGlyph> work = new(n);
        int curSpan = 0;
        bool replacing = false;
        bool first = false;

        for (int i = 0; i < n; i++)
        {
            if ((i & 1023) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            PdfGlyph g = src[i];
            Matrix(g, out bool finite);
            if (!finite || !double.IsFinite(g.Advance) || !double.IsFinite(g.Ascent) || !double.IsFinite(g.Descent))
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

            text = Sanitize(text);
            work.Add(g.WithText(text));
        }

        PdfGlyph[] result = work.ToArray();
        if (o.CollapseDuplicates && result.Length > 1)
        {
            result = CollapseDuplicates(result);
        }

        return result;
    }

    private static PdfTextMatrix Matrix(in PdfGlyph g, out bool finite)
    {
        PdfTextMatrix m = g.Matrix;
        finite = double.IsFinite(m.A) && double.IsFinite(m.B) && double.IsFinite(m.C) && double.IsFinite(m.D) && double.IsFinite(m.E) && double.IsFinite(m.F);
        return m;
    }

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

    /// <summary>Drops a glyph drawn again at almost the same place by a later operator (fake bold, drop shadow).</summary>
    private static PdfGlyph[] CollapseDuplicates(PdfGlyph[] g)
    {
        int n = g.Length;
        int[] order = new int[n];
        for (int i = 0; i < n; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) =>
        {
            int c = g[a].Matrix.E.CompareTo(g[b].Matrix.E);
            return c != 0 ? c : a.CompareTo(b);
        });

        bool[] drop = new bool[n];
        bool any = false;
        for (int p = 0; p < n; p++)
        {
            int i = order[p];
            double size = g[i].Size;
            double tol = LayoutParameters.DuplicateOffsetEm * size;
            for (int q = p + 1, steps = 0; q < n && steps < 32; q++, steps++)
            {
                int j = order[q];
                if (g[j].Matrix.E - g[i].Matrix.E > tol)
                {
                    break;
                }

                if (g[i].Text.Length == 0 || g[i].Text != g[j].Text || g[i].IsVertical != g[j].IsVertical)
                {
                    continue;
                }

                if (Math.Abs(g[j].Matrix.F - g[i].Matrix.F) > tol)
                {
                    continue;
                }

                double sj = g[j].Size;
                if (Math.Abs(sj - size) > 0.05 * size)
                {
                    continue;
                }

                drop[Math.Max(i, j)] = true;
                any = true;
            }
        }

        if (!any)
        {
            return g;
        }

        List<PdfGlyph> keep = new(n);
        for (int i = 0; i < n; i++)
        {
            if (!drop[i])
            {
                keep.Add(g[i]);
            }
        }

        return keep.ToArray();
    }
}
