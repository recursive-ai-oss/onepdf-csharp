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

internal sealed class Frag
{
    internal int Line;
    internal int[] Items = [];
    internal double X0, X1, Top, Bot, Base, Size;
    internal int NonSpace;
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

    internal static Item MakeItem(in PdfTextGlyph g, int index)
    {
        int k = Quantize(g);
        double ux = Dx[k], uy = Dy[k];
        double nx = uy, ny = -ux;
        PdfQuad q = g.Quad;
        double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
        for (int i = 0; i < 4; i++)
        {
            double px = i == 0 ? q.X0 : i == 1 ? q.X1 : i == 2 ? q.X2 : q.X3;
            double py = i == 0 ? q.Y0 : i == 1 ? q.Y1 : i == 2 ? q.Y2 : q.Y3;
            double fx = ux * px + uy * py;
            double fy = nx * px + ny * py;
            x0 = Math.Min(x0, fx);
            x1 = Math.Max(x1, fx);
            y0 = Math.Min(y0, fy);
            y1 = Math.Max(y1, fy);
        }

        string t = g.Text ?? "";
        bool space = t.Length > 0;
        for (int i = 0; i < t.Length; i++)
        {
            if (!char.IsWhiteSpace(t[i]))
            {
                space = false;
                break;
            }
        }

        int first = t.Length > 0 ? char.ConvertToUtf32(t, 0) : 0;
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

    /// <summary>Lays out the glyphs of <paramref name="subset"/> (indices into <paramref name="g"/>) and returns blocks in reading order.</summary>
    internal static List<PdfTextBlock> Run(PdfTextGlyph[] g, ReadOnlySpan<int> subset, CancellationToken ct)
    {
        List<PdfTextBlock> result = [];
        if (subset.Length == 0)
        {
            return result;
        }

        List<int>[] byK = [[], [], [], []];
        for (int i = 0; i < subset.Length; i++)
        {
            byK[Quantize(g[subset[i]])].Add(subset[i]);
        }

        int dominant = 0;
        for (int k = 1; k < 4; k++)
        {
            if (byK[k].Count > byK[dominant].Count)
            {
                dominant = k;
            }
        }

        for (int pass = -1; pass < 4; pass++)
        {
            int k = pass < 0 ? dominant : pass;
            if ((pass >= 0 && k == dominant) || byK[k].Count == 0)
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();
            LayoutClass(g, byK[k], result, ct);
        }

        return result;
    }

    internal static Item[] MakeItems(PdfTextGlyph[] g, List<int> idx)
    {
        Item[] items = new Item[idx.Count];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = MakeItem(g[idx[i]], idx[i]);
        }

        return items;
    }

    private static void LayoutClass(PdfTextGlyph[] g, List<int> idx, List<PdfTextBlock> output, CancellationToken ct)
    {
        Item[] items = MakeItems(g, idx);
        int lineCount = ClusterLines(items);
        List<Frag> frags = Fragment(items, lineCount);
        foreach ((List<Frag> fr, bool table) in XyCut(frags, items, ct))
        {
            PdfTextBlock? b = MakeBlock(g, items, fr, table);
            if (b is not null)
            {
                output.Add(b);
            }
        }
    }

    // ---- lines ------------------------------------------------------------------------------------------------------------------

    /// <summary>Assigns <see cref="Item.Line"/>: glyphs on one baseline, with sub/superscripts joined to the line they touch. Returns the line count.</summary>
    internal static int ClusterLines(Item[] items)
    {
        int n = items.Length;
        int[] order = new int[n];
        for (int i = 0; i < n; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) =>
        {
            int c = items[a].Base.CompareTo(items[b].Base);
            return c != 0 ? c : items[a].X0.CompareTo(items[b].X0);
        });

        List<List<int>> clusters = [];
        List<int>? cur = null;
        double sum = 0, minSize = 0;
        foreach (int i in order)
        {
            ref Item it = ref items[i];
            if (cur is not null)
            {
                double mean = sum / cur.Count;
                double tol = LayoutParameters.BaselineToleranceEm * Math.Min(minSize, it.Size);
                if (Math.Abs(it.Base - mean) <= tol)
                {
                    cur.Add(i);
                    sum += it.Base;
                    minSize = Math.Min(minSize, it.Size);
                    continue;
                }
            }

            cur = [i];
            clusters.Add(cur);
            sum = it.Base;
            minSize = it.Size;
        }

        // Join script runs (small clusters) to a larger neighbouring line they touch.
        int cc = clusters.Count;
        double[] medSize = new double[cc];
        double[] medBase = new double[cc];
        for (int c = 0; c < cc; c++)
        {
            medSize[c] = Median(clusters[c], items, static it => it.Size);
            medBase[c] = Median(clusters[c], items, static it => it.Base);
        }

        int[] parent = new int[cc];
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

                if (Touches(clusters[a], clusters[b], items, LayoutParameters.ScriptTouchEm * medSize[b]))
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

        int[] id = new int[cc];
        int count = 0;
        Array.Fill(id, -1);
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
            foreach (int i in clusters[c])
            {
                items[i].Line = id[r];
            }
        }

        return count;
    }

    private static bool Touches(List<int> small, List<int> big, Item[] items, double dist)
    {
        foreach (int i in small)
        {
            foreach (int j in big)
            {
                double gap = Math.Max(items[i].X0 - items[j].X1, items[j].X0 - items[i].X1);
                if (gap <= dist)
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static double Median(List<int> idx, Item[] items, Func<Item, double> f)
    {
        double[] v = new double[idx.Count];
        for (int i = 0; i < v.Length; i++)
        {
            v[i] = f(items[idx[i]]);
        }

        return Median(v);
    }

    internal static double Median(double[] v)
    {
        if (v.Length == 0)
        {
            return 0;
        }

        Array.Sort(v);
        return (v[(v.Length - 1) / 2] + v[v.Length / 2]) / 2;
    }

    // ---- fragments --------------------------------------------------------------------------------------------------------------

    private static List<Frag> Fragment(Item[] items, int lineCount)
    {
        List<int>[] lines = new List<int>[lineCount];
        for (int i = 0; i < lineCount; i++)
        {
            lines[i] = [];
        }

        for (int i = 0; i < items.Length; i++)
        {
            lines[items[i].Line].Add(i);
        }

        List<Frag> frags = [];
        for (int l = 0; l < lineCount; l++)
        {
            List<int> li = lines[l];
            li.Sort((a, b) => items[a].X0.CompareTo(items[b].X0));
            List<int> cur = [];
            double lastX1 = double.NaN;
            double lastSize = 0;
            foreach (int i in li)
            {
                ref Item it = ref items[i];
                if (!it.Space)
                {
                    if (!double.IsNaN(lastX1) && cur.Count > 0)
                    {
                        double gap = it.X0 - lastX1;
                        if (gap > LayoutParameters.FragmentGapEm * (lastSize + it.Size) / 2)
                        {
                            frags.Add(MakeFrag(items, cur, l));
                            cur = [];
                        }
                    }

                    lastX1 = double.IsNaN(lastX1) ? it.X1 : Math.Max(lastX1, it.X1);
                    lastSize = it.Size;
                }

                cur.Add(i);
            }

            if (cur.Count > 0)
            {
                frags.Add(MakeFrag(items, cur, l));
            }
        }

        return frags;
    }

    private static Frag MakeFrag(Item[] items, List<int> idx, int line)
    {
        Frag f = new() { Line = line, Items = idx.ToArray(), X0 = double.MaxValue, X1 = double.MinValue, Top = double.MaxValue, Bot = double.MinValue };
        double[] sizes = new double[idx.Count];
        double bs = 0;
        int ns = 0;
        for (int i = 0; i < idx.Count; i++)
        {
            ref Item it = ref items[idx[i]];
            sizes[i] = it.Size;
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
            ref Item it0 = ref items[idx[0]];
            f.X0 = it0.X0;
            f.X1 = it0.X1;
            f.Top = it0.Top;
            f.Bot = it0.Bot;
            bs = it0.Base;
            ns = 1;
        }

        f.Base = bs / ns;
        f.Size = Median(sizes);
        return f;
    }

    // ---- XY-cut -----------------------------------------------------------------------------------------------------------------

    private static List<(List<Frag>, bool)> XyCut(List<Frag> all, Item[] items, CancellationToken ct)
    {
        List<(List<Frag>, bool)> leaves = [];
        Stack<List<Frag>> stack = new();
        stack.Push(all);
        int guard = 0;
        while (stack.Count > 0)
        {
            if ((++guard & 255) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            List<Frag> r = stack.Pop();
            if (r.Count == 0)
            {
                continue;
            }

            if (r.Count == 1 || DistinctLines(r) <= 1)
            {
                leaves.Add((r, false));
                continue;
            }

            double[] sizes = new double[r.Count];
            for (int i = 0; i < sizes.Length; i++)
            {
                sizes[i] = r[i].Size;
            }

            double ms = Median(sizes);
            List<List<Frag>>? parts = Partition(r, static f => f.X0, static f => f.X1, LayoutParameters.ColumnGutterEm * ms);
            if (parts is not null)
            {
                if (IsTable(r, parts, ms))
                {
                    leaves.Add((r, true));
                    continue;
                }

                if (MajorityRtl(r, items))
                {
                    parts.Reverse();
                }
            }
            else
            {
                parts = Partition(r, static f => f.Top, static f => f.Bot, LayoutParameters.BlockGapEm * ms) ?? SplitByLineKind(r);
            }

            if (parts is null)
            {
                leaves.Add((r, false));
                continue;
            }

            for (int i = parts.Count - 1; i >= 0; i--)
            {
                stack.Push(parts[i]);
            }
        }

        return leaves;
    }

    private static int DistinctLines(List<Frag> r)
    {
        HashSet<int> s = [];
        foreach (Frag f in r)
        {
            s.Add(f.Line);
        }

        return s.Count;
    }

    /// <summary>Splits at every empty stripe of at least <paramref name="gap"/> along one axis; null when there is none.</summary>
    private static List<List<Frag>>? Partition(List<Frag> r, Func<Frag, double> start, Func<Frag, double> end, double gap)
    {
        Frag[] s = r.ToArray();
        Array.Sort(s, (a, b) => start(a).CompareTo(start(b)));
        List<List<Frag>> groups = [];
        List<Frag> cur = [s[0]];
        double curEnd = end(s[0]);
        for (int i = 1; i < s.Length; i++)
        {
            if (start(s[i]) - curEnd >= gap)
            {
                groups.Add(cur);
                cur = [];
            }

            cur.Add(s[i]);
            curEnd = Math.Max(curEnd, end(s[i]));
        }

        groups.Add(cur);
        return groups.Count >= 2 ? groups : null;
    }

    private static bool IsTable(List<Frag> r, List<List<Frag>> cols, double ms)
    {
        Dictionary<int, int> rowFirstCol = [];
        Dictionary<int, bool> rowMulti = [];
        for (int c = 0; c < cols.Count; c++)
        {
            foreach (Frag f in cols[c])
            {
                if (!rowFirstCol.TryGetValue(f.Line, out int first))
                {
                    rowFirstCol[f.Line] = c;
                    rowMulti[f.Line] = false;
                }
                else if (first != c)
                {
                    rowMulti[f.Line] = true;
                }
            }
        }

        int rows = rowMulti.Count;
        if (rows < LayoutParameters.TableMinRows)
        {
            return false;
        }

        int multi = 0;
        foreach (bool v in rowMulti.Values)
        {
            if (v)
            {
                multi++;
            }
        }

        if (multi < LayoutParameters.TableRowFraction * rows)
        {
            return false;
        }

        double[] widths = new double[r.Count];
        for (int i = 0; i < widths.Length; i++)
        {
            widths[i] = (r[i].X1 - r[i].X0) / Math.Max(ms, 1e-6);
        }

        return Median(widths) <= LayoutParameters.TableCellWidthEm;
    }

    private static bool MajorityRtl(List<Frag> r, Item[] items)
    {
        int rtl = 0, ltr = 0;
        foreach (Frag f in r)
        {
            foreach (int i in f.Items)
            {
                if (items[i].FirstCp == 0)
                {
                    continue;
                }

                BidiClass c = Bidi.GetClass(items[i].FirstCp);
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

        return rtl > ltr;
    }

    /// <summary>Splits a region where lines with a gutter (several fragments) alternate with single-fragment lines.</summary>
    private static List<List<Frag>>? SplitByLineKind(List<Frag> r)
    {
        Dictionary<int, int> count = [];
        Dictionary<int, double> top = [];
        foreach (Frag f in r)
        {
            count[f.Line] = count.GetValueOrDefault(f.Line) + 1;
            top[f.Line] = top.TryGetValue(f.Line, out double t) ? Math.Min(t, f.Base) : f.Base;
        }

        int[] lines = [.. count.Keys];
        Array.Sort(lines, (a, b) => top[a].CompareTo(top[b]));
        Dictionary<int, int> runOf = [];
        int run = 0;
        bool prevMulti = count[lines[0]] > 1;
        foreach (int l in lines)
        {
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
            return null;
        }

        List<List<Frag>> runs = [];
        for (int i = 0; i <= run; i++)
        {
            runs.Add([]);
        }

        foreach (Frag f in r)
        {
            runs[runOf[f.Line]].Add(f);
        }

        return runs;
    }

    // ---- blocks, lines, words ---------------------------------------------------------------------------------------------------

    private sealed class WordBuild
    {
        internal List<int> Items = [];
        internal bool CellStart;
    }

    private static PdfTextBlock? MakeBlock(PdfTextGlyph[] g, Item[] items, List<Frag> frags, bool table)
    {
        Dictionary<int, List<Frag>> byLine = [];
        foreach (Frag f in frags)
        {
            if (!byLine.TryGetValue(f.Line, out List<Frag>? l))
            {
                byLine[f.Line] = l = [];
            }

            l.Add(f);
        }

        List<List<Frag>> lines = [.. byLine.Values];
        lines.Sort((a, b) => a[0].Base.CompareTo(b[0].Base));

        int rtl = 0, ltr = 0;
        foreach (Frag f in frags)
        {
            foreach (int i in f.Items)
            {
                if (items[i].FirstCp == 0)
                {
                    continue;
                }

                BidiClass c = Bidi.GetClass(items[i].FirstCp);
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

        int para = rtl > ltr ? 1 : 0;

        List<PdfTextLine> outLines = [];
        double bl = double.MaxValue, bb = double.MaxValue, br = double.MinValue, bt = double.MinValue;
        foreach (List<Frag> lf in lines)
        {
            lf.Sort((a, b) => a.X0.CompareTo(b.X0));
            PdfTextLine? line = MakeLine(g, items, lf, para);
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

    /// <summary>Splits a run of items (in x order) into words: at space glyphs and at gaps wider than the word-gap thresholds.</summary>
    internal static List<List<int>> SplitWords(Item[] items, IEnumerable<int> run)
    {
        List<List<int>> words = [];
        List<int>? cur = null;
        Item prev = default;
        bool havePrev = false;
        foreach (int i in run)
        {
            ref Item it = ref items[i];
            if (it.Space)
            {
                cur = null;
                continue;
            }

            if (cur is not null && havePrev)
            {
                double gap = it.X0 - prev.X1;
                double size = (prev.Size + it.Size) / 2;
                double thr = (prev.Cjk || it.Cjk ? LayoutParameters.CjkWordGapEm : LayoutParameters.WordGapEm) * size;
                if (gap > thr)
                {
                    cur = null;
                }
            }

            if (cur is null)
            {
                cur = [];
                words.Add(cur);
            }

            cur.Add(i);
            prev = it;
            havePrev = true;
        }

        return words;
    }

    private static PdfTextLine? MakeLine(PdfTextGlyph[] g, Item[] items, List<Frag> frags, int para)
    {
        List<WordBuild> words = [];
        for (int f = 0; f < frags.Count; f++)
        {
            List<List<int>> fw = SplitWords(items, frags[f].Items);
            for (int w = 0; w < fw.Count; w++)
            {
                words.Add(new WordBuild { Items = fw[w], CellStart = w == 0 && f > 0 });
            }
        }

        if (words.Count == 0)
        {
            return null;
        }

        words[0].CellStart = true;

        int wc = words.Count;
        string[] visual = new string[wc];
        bool needBidi = para == 1;
        for (int w = 0; w < wc; w++)
        {
            StringBuilder sb = new();
            foreach (int i in words[w].Items)
            {
                string t = g[items[i].G].Text;
                sb.Append(t);
                if (!needBidi && t.Length > 0)
                {
                    needBidi = MayBeRtl(t);
                }
            }

            visual[w] = sb.ToString();
        }

        int[] order = new int[wc];
        string[] logical = visual;
        bool[] cellStart = new bool[wc];
        for (int w = 0; w < wc; w++)
        {
            order[w] = w;
            cellStart[w] = words[w].CellStart;
        }

        if (needBidi)
        {
            Reorder(visual, cellStart, para, out logical, out order, out cellStart);
        }

        List<PdfTextWord> outWords = new(wc);
        List<int> cells = [];
        double ll = double.MaxValue, lb = double.MaxValue, lr = double.MinValue, lt = double.MinValue;
        for (int w = 0; w < wc; w++)
        {
            string text = logical[w];
            if (text.Length == 0)
            {
                continue;
            }

            WordBuild wb = words[order[w]];
            int[] gi = new int[wb.Items.Count];
            double wl = double.MaxValue, wbm = double.MaxValue, wr = double.MinValue, wt = double.MinValue;
            bool vis = false;
            for (int k = 0; k < gi.Length; k++)
            {
                int gx = items[wb.Items[k]].G;
                gi[k] = gx;
                PdfRectangle b = g[gx].Bounds;
                wl = Math.Min(wl, b.Left);
                wbm = Math.Min(wbm, b.Bottom);
                wr = Math.Max(wr, b.Right);
                wt = Math.Max(wt, b.Top);
                vis |= g[gx].IsVisible;
            }

            if (cellStart[w])
            {
                cells.Add(outWords.Count);
            }

            outWords.Add(new PdfTextWord(text, new PdfRectangle(wl, wbm, wr, wt), gi, vis));
            ll = Math.Min(ll, wl);
            lb = Math.Min(lb, wbm);
            lr = Math.Max(lr, wr);
            lt = Math.Max(lt, wt);
        }

        if (outWords.Count == 0)
        {
            return null;
        }

        if (cells.Count == 0 || cells[0] != 0)
        {
            cells.Insert(0, 0);
        }

        HashSet<int> cellSet = [.. cells];
        StringBuilder line = new();
        for (int w = 0; w < outWords.Count; w++)
        {
            if (w > 0)
            {
                line.Append(cellSet.Contains(w) ? '\t' : ' ');
            }

            line.Append(outWords[w].Text);
        }

        return new PdfTextLine(outWords.ToArray(), new PdfRectangle(ll, lb, lr, lt),
            para == 1 ? PdfTextDirection.RightToLeft : PdfTextDirection.LeftToRight, g[items[words[0].Items[0]].G].IsVertical, cells.ToArray(), line.ToString());
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
