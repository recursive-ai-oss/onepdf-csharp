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
        IReadOnlyList<PdfGlyph> g = page.Glyphs;
        Item prev = default;
        bool havePrev = false;
        for (int i = 0; i < g.Count; i++)
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
        PdfGlyph[] g = [.. page.Glyphs];
        if (g.Length == 0)
        {
            return "";
        }

        List<int>[] byK = [[], [], [], []];
        for (int i = 0; i < g.Length; i++)
        {
            byK[TextLayout.Quantize(g[i])].Add(i);
        }

        int dominant = 0;
        for (int k = 1; k < 4; k++)
        {
            if (byK[k].Count > byK[dominant].Count)
            {
                dominant = k;
            }
        }

        StringBuilder sb = new();
        for (int pass = -1; pass < 4; pass++)
        {
            int k = pass < 0 ? dominant : pass;
            if ((pass >= 0 && k == dominant) || byK[k].Count == 0)
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append("\n\n");
            }

            LayoutClass(g, byK[k], sb);
        }

        return sb.ToString();
    }

    private static void LayoutClass(PdfGlyph[] g, List<int> idx, StringBuilder sb)
    {
        Item[] items = TextLayout.MakeItems(g, idx);
        int lineCount = TextLayout.ClusterLines(items);
        List<int>[] lines = new List<int>[lineCount];
        for (int i = 0; i < lineCount; i++)
        {
            lines[i] = [];
        }

        double left = double.MaxValue;
        double[] sizes = new double[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            lines[items[i].Line].Add(i);
            sizes[i] = items[i].Size;
            if (!items[i].Space)
            {
                left = Math.Min(left, items[i].X0);
            }
        }

        double cell = Math.Max(LayoutParameters.LayoutCellEm * TextLayout.Median(sizes), 1e-3);
        double[] baseOf = new double[lineCount];
        for (int l = 0; l < lineCount; l++)
        {
            double s = 0;
            foreach (int i in lines[l])
            {
                s += items[i].Base;
            }

            baseOf[l] = s / lines[l].Count;
            lines[l].Sort((a, b) => items[a].X0.CompareTo(items[b].X0));
        }

        int[] order = new int[lineCount];
        for (int i = 0; i < lineCount; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) => baseOf[a].CompareTo(baseOf[b]));
        List<double> diffs = [];
        for (int i = 1; i < lineCount; i++)
        {
            double d = baseOf[order[i]] - baseOf[order[i - 1]];
            if (d > 1e-6)
            {
                diffs.Add(d);
            }
        }

        double pitch = diffs.Count > 0 ? TextLayout.Median(diffs.ToArray()) : 0;
        StringBuilder row = new();
        bool first = true;
        for (int oi = 0; oi < lineCount; oi++)
        {
            int l = order[oi];
            row.Clear();
            foreach (List<int> w in TextLayout.SplitWords(items, lines[l]))
            {
                StringBuilder ws = new();
                foreach (int i in w)
                {
                    ws.Append(g[items[i].G].Text);
                }

                string text = LogicalWord(ws.ToString());
                if (text.Length == 0)
                {
                    continue;
                }

                double x0 = double.MaxValue;
                foreach (int i in w)
                {
                    x0 = Math.Min(x0, items[i].X0);
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
                    double gap = baseOf[l] - baseOf[order[oi - 1]];
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
