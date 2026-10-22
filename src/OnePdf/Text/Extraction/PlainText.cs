using System.Text;

namespace OnePdf.Text;

/// <summary>Plain-text rendering of a page in the three modes.</summary>
internal static class PlainText
{
    internal static string Build(PdfPageText page, PdfTextMode mode, PdfTextOptions o) => mode switch
    {
        PdfTextMode.ContentOrder => ContentOrder(page),
        PdfTextMode.Layout => Layout(page),
        _ => ReadingOrder(page, o),
    };

    private static bool IsHyphen(char c) => c is '-' or '\u00AD' or '\u2010';

    internal static string ReadingOrder(PdfPageText page, PdfTextOptions o)
    {
        StringBuilder sb = new();
        bool firstBlock = true;
        foreach (PdfTextBlock b in page.Blocks)
        {
            if (o.ExcludeHeadersAndFooters && b.Role != PdfTextRole.Body)
            {
                continue;
            }

            if (!firstBlock)
            {
                sb.Append("\n\n");
            }

            firstBlock = false;
            IReadOnlyList<PdfTextLine> lines = b.Lines;
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].Text;
                bool join = false;
                if (o.JoinHyphenatedWords && i + 1 < lines.Count && t.Length >= 2 && IsHyphen(t[^1]) && char.IsLetter(t[^2]))
                {
                    string next = lines[i + 1].Text;
                    join = next.Length > 0 && char.IsLower(next[0]);
                }

                if (join)
                {
                    sb.Append(t, 0, t.Length - 1);
                }
                else
                {
                    sb.Append(t);
                    if (i + 1 < lines.Count)
                    {
                        sb.Append('\n');
                    }
                }
            }
        }

        return sb.ToString();
    }

    internal static string ContentOrder(PdfPageText page)
    {
        StringBuilder sb = new();
        PdfTextGlyph[] g = page.GlyphArray;
        Item prev = default;
        bool havePrev = false;
        for (int i = 0; i < g.Length; i++)
        {
            string t = g[i].Text;
            if (t.Length == 0)
            {
                continue;
            }

            Item it = TextLayout.MakeItem(g[i], i);
            if (it.Space)
            {
                if (sb.Length > 0 && sb[^1] != ' ' && sb[^1] != '\n')
                {
                    sb.Append(' ');
                }

                prev = it;
                havePrev = true;
                continue;
            }

            if (havePrev)
            {
                double size = Math.Min(prev.Size, it.Size);
                double gap = it.X0 - prev.X1;
                bool sameLine = prev.K == it.K && Math.Abs(it.Base - prev.Base) <= LayoutParameters.ContentLineEm * size && gap > -3 * size;
                if (!sameLine)
                {
                    TrimEnd(sb);
                    sb.Append('\n');
                }
                else if (!prev.Space && gap > (prev.Cjk || it.Cjk ? LayoutParameters.CjkWordGapEm : LayoutParameters.WordGapEm) * size)
                {
                    sb.Append(' ');
                }
            }

            sb.Append(t);
            prev = it;
            havePrev = true;
        }

        TrimEnd(sb);
        return sb.ToString();
    }

    private static void TrimEnd(StringBuilder sb)
    {
        while (sb.Length > 0 && sb[^1] == ' ')
        {
            sb.Length--;
        }
    }

    private static string LogicalWord(string visual)
    {
        bool rtl = false;
        foreach (char c in visual)
        {
            if (c >= '\u0590' && c <= '\u08FF' || c >= '\uFB1D' && c <= '\uFEFF')
            {
                rtl = true;
                break;
            }
        }

        if (!rtl)
        {
            return visual;
        }

        List<int> cps = [];
        for (int i = 0; i < visual.Length; i++)
        {
            int cp = char.ConvertToUtf32(visual, i);
            if (cp > 0xFFFF)
            {
                i++;
            }

            cps.Add(cp);
        }

        int[] a = cps.ToArray();
        byte[] lev = new byte[a.Length];
        Bidi.ResolveLevels(a, -1, lev);
        int[] ord = new int[a.Length];
        int m = Bidi.VisualOrder(lev, ord);
        StringBuilder sb = new();
        for (int i = 0; i < m; i++)
        {
            sb.Append(char.ConvertFromUtf32(a[ord[i]]));
        }

        return sb.ToString();
    }

    internal static string Layout(PdfPageText page)
    {
        PdfTextGlyph[] g = page.GlyphArray;
        if (g.Length == 0)
        {
            return "";
        }

        using LayoutWork w = new();
        Span<int> start = stackalloc int[5];
        int dominant = TextLayout.Classify(w, g, default, true, g.Length, out int[] grouped, start);
        StringBuilder sb = new();
        for (int pass = -1; pass < 4; pass++)
        {
            int k = pass < 0 ? dominant : pass;
            int cnt = start[k + 1] - start[k];
            if ((pass >= 0 && k == dominant) || cnt == 0)
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append("\n\n");
            }

            LayoutClass(g, grouped, start[k], cnt, k, sb);
        }

        return sb.ToString();
    }

    private static readonly Comparison<KeyIdx> ByKey = static (a, b) => a.Key.CompareTo(b.Key);

    private static void LayoutClass(PdfTextGlyph[] g, int[] idx, int offset, int n, int k, StringBuilder sb)
    {
        using LayoutWork w = new();
        Item[] items = TextLayout.MakeItems(w, g, idx, offset, n, k);
        int lineCount = TextLayout.ClusterLines(w, items, n);
        int[] ids = TextLayout.BucketLines(w, items, n, lineCount, out int[] lineStart);

        // The mean baseline of a line sums its items in stream order.
        double[] baseOf = w.Doubles.Rent(lineCount);
        Array.Clear(baseOf, 0, lineCount);
        double left = double.MaxValue;
        double[] sizes = w.Doubles.Rent(n);
        for (int i = 0; i < n; i++)
        {
            baseOf[items[i].Line] += items[i].Base;
            sizes[i] = items[i].Size;
            if (!items[i].Space)
            {
                left = Math.Min(left, items[i].X0);
            }
        }

        for (int l = 0; l < lineCount; l++)
        {
            baseOf[l] /= lineStart[l + 1] - lineStart[l];
        }

        double cell = Math.Max(LayoutParameters.LayoutCellEm * TextLayout.Median(sizes.AsSpan(0, n)), 1e-3);

        KeyIdx[] order = w.Keys.Rent(lineCount);
        for (int i = 0; i < lineCount; i++)
        {
            order[i] = new KeyIdx { Key = baseOf[i], Idx = i };
        }

        order.AsSpan(0, lineCount).Sort(ByKey);
        int nd = 0;
        for (int i = 1; i < lineCount; i++)
        {
            double d = baseOf[order[i].Idx] - baseOf[order[i - 1].Idx];
            if (d > 1e-6)
            {
                sizes[nd++] = d;
            }
        }

        double pitch = nd > 0 ? TextLayout.Median(sizes.AsSpan(0, nd)) : 0;
        StringBuilder row = new();
        StringBuilder ws = new();
        bool first = true;
        for (int oi = 0; oi < lineCount; oi++)
        {
            int l = order[oi].Idx;
            row.Clear();
            int p = lineStart[l], end = lineStart[l + 1];
            while (TextLayout.NextWord(items, ids, ref p, end, out int wStart, out int wLen))
            {
                ws.Clear();
                double x0 = double.MaxValue;
                for (int q = wStart; q < wStart + wLen; q++)
                {
                    ws.Append(g[items[ids[q]].G].Text);
                    x0 = Math.Min(x0, items[ids[q]].X0);
                }

                string text = LogicalWord(ws.ToString());
                if (text.Length == 0)
                {
                    continue;
                }

                int col = (int)Math.Min(Math.Round((x0 - left) / cell), 400);
                if (row.Length > 0 && col <= row.Length)
                {
                    col = row.Length + 1;
                }

                row.Append(' ', Math.Max(0, col - row.Length));
                row.Append(text);
            }

            if (row.Length == 0)
            {
                continue;
            }

            if (!first)
            {
                sb.Append('\n');
                if (pitch > 0)
                {
                    double gap = baseOf[l] - baseOf[order[oi - 1].Idx];
                    if (gap > 1.6 * pitch)
                    {
                        int blanks = Math.Min(3, (int)Math.Round(gap / pitch) - 1);
                        sb.Append('\n', Math.Max(0, blanks));
                    }
                }
            }

            first = false;
            sb.Append(row);
        }
    }
}
