namespace OnePdf.Layout;

// Total-fit line breaking after Knuth and Plass, "Breaking Paragraphs into Lines", Software: Practice and Experience 11 (1981), with the
// parameters of Knuth's "Digital Typography" chapter 3. The paper's boxes, glue and penalties are the paragraph's clusters (boxes),
// its spaces (glue that stretches by half and shrinks by a third of its width) and its break opportunities (penalties; a hyphenation
// point is a flagged penalty that adds the hyphen's width when taken).
public sealed partial class ShapedParagraph
{
    private const int MaxActive = 4096;
    private const double InfBad = 10000;
    private const double StretchFactor = 0.5;
    private const double ShrinkFactor = 1.0 / 3.0;
    private const double RaggedStretch = 0.25;
    private const double NoStretch = 100;

    private bool UseKnuthPlass => _source.LineBreakMode == LineBreakMode.KnuthPlass;

    /// <summary>The widening or narrowing of the spaces a line can take up at most (used to tell a squeezed line from an overfull one).</summary>
    private double ShrinkRoom(int from, int to)
    {
        double room = 0;
        for (int k = from; k < to; k++)
        {
            if (_items[k].Stretch)
            {
                room += _items[k].Advance * ShrinkFactor;
            }
        }

        return room;
    }

    // Chooses the breaks of one hard line. Returns the cut after each line (the item the next line starts at, and whether a hyphen is added),
    // or null when the search was given up and the caller breaks greedily.
    private List<(int Cut, bool Hyphen)>? BreakOptimal(Segment seg, double firstIndent, double width)
    {
        KnuthPlassOptions o = _source.KnuthPlass ?? KnuthPlassOptions.Default;
        int n = seg.Count;
        if (n > KnuthPlassOptions.MaxItems)
        {
            Warn("A paragraph line is too long for Knuth-Plass breaking; it was broken greedily.");
            return null;
        }

        var problem = new KpProblem(this, seg, o, width, firstIndent, _source.Alignment == TextAlignment.Justify);
        bool hasHyphens = problem.HasHyphens;
        List<(int, bool)>? result;
        if (hasHyphens && o.PreTolerance >= 0)
        {
            result = problem.Solve(false, o.PreTolerance, 0, false);
            if (result is not null)
            {
                return result;
            }
        }

        // The main pass, then the same with emergency stretch, then any line that is not too full.
        result = problem.Solve(hasHyphens, o.Tolerance, 0, false);
        if (result is null && !problem.Aborted && o.EmergencyStretch > 0)
        {
            result = problem.Solve(hasHyphens, o.Tolerance, o.EmergencyStretch, false);
        }

        if (result is null && !problem.Aborted)
        {
            result = problem.Solve(hasHyphens, double.PositiveInfinity, o.EmergencyStretch, true);
        }

        if (result is null)
        {
            return Abort();
        }

        return result;

        List<(int, bool)>? Abort()
        {
            Warn("Knuth-Plass breaking found no solution within its bounds; the paragraph was broken greedily.");
            return null;
        }
    }

    private readonly struct Cand(int pos, int trimEnd, bool hyphen, bool flagged, double penalty, double hyphenWidth)
    {
        public readonly int Pos = pos;
        public readonly int TrimEnd = trimEnd;
        public readonly bool Hyphen = hyphen;
        public readonly bool Flagged = flagged;
        public readonly double Penalty = penalty;
        public readonly double HyphenWidth = hyphenWidth;
    }

    private struct Node
    {
        public int Cand;
        public int Prev;
        public int Line;
        public byte Fit;
        public bool Flagged;
        public double Demerits;
    }

    private struct Entry
    {
        public byte Fit;
        public int LineKey;
        public int Prev;
        public double Demerits;
    }

    private sealed class KpProblem
    {
        private readonly Segment _seg;
        private readonly KnuthPlassOptions _o;
        private readonly double _width;
        private readonly double _firstIndent;
        private readonly bool _justify;
        private readonly double[] _stretch;
        private readonly double[] _shrink;
        private readonly List<Cand> _cands = [];

        public KpProblem(ShapedParagraph p, Segment seg, KnuthPlassOptions o, double width, double firstIndent, bool justify)
        {
            _seg = seg;
            _o = o;
            _width = width;
            _firstIndent = firstIndent;
            _justify = justify;
            int n = seg.Count;
            _stretch = new double[n + 1];
            _shrink = new double[n + 1];
            int first = seg.First;
            for (int k = 0; k < n; k++)
            {
                Item it = p._items[first + k];
                _stretch[k + 1] = _stretch[k] + (it.Stretch ? it.Advance * StretchFactor : 0);
                _shrink[k + 1] = _shrink[k] + (it.Stretch ? it.Advance * ShrinkFactor : 0);
            }

            int end = first + n;
            string text = p._norm.Text;
            for (int j = first + 1; j < end; j++)
            {
                Item it = p._items[j];
                if (!it.BreakOk && !it.HyphenOk)
                {
                    continue;
                }

                int te = j;
                while (te > first && p._items[te - 1].TrimSpace)
                {
                    te--;
                }

                if (it.HyphenOk)
                {
                    if (o.HyphenPenalty >= 10000)
                    {
                        continue;
                    }

                    _cands.Add(new Cand(j, te, true, true, o.HyphenPenalty, p.HyphenFor(j - 1).Advance));
                    HasHyphens = true;
                }
                else
                {
                    Item prev = p._items[j - 1];
                    char c = prev.Length == 1 ? text[prev.Start] : '\0';
                    bool explicitHyphen = c is '-' or '\u2010';
                    if (explicitHyphen && o.ExplicitHyphenPenalty >= 10000)
                    {
                        continue;
                    }

                    _cands.Add(new Cand(j, te, false, explicitHyphen, explicitHyphen ? o.ExplicitHyphenPenalty : 0, 0));
                }
            }

            int tEnd = end;
            while (tEnd > first && p._items[tEnd - 1].TrimSpace)
            {
                tEnd--;
            }

            _cands.Add(new Cand(end, tEnd, false, false, 0, 0));
        }

        public bool HasHyphens { get; }

        public bool Aborted { get; private set; }

        // One pass. Returns the cuts of the best solution, or null when there is none (or Aborted is set).
        public List<(int, bool)>? Solve(bool allowHyphens, double threshold, double emergency, bool final)
        {
            Aborted = false;
            int first = _seg.First;
            var nodes = new List<Node>(_cands.Count + 1) { new Node { Cand = -1, Prev = -1, Line = 0, Fit = 2, Demerits = 0 } };
            var active = new List<int>(64) { 0 };
            var entries = new List<Entry>(8);
            var finals = new List<int>(4);
            bool looseKeyed = _o.Looseness != 0;
            for (int c = 0; c < _cands.Count; c++)
            {
                Cand cand = _cands[c];
                if (cand.Hyphen && !allowHyphens)
                {
                    continue;
                }

                bool last = c == _cands.Count - 1;
                entries.Clear();
                int fallbackNode = -1;
                double fallbackDem = double.PositiveInfinity;
                int fallbackStart = -1;
                for (int ai = 0; ai < active.Count;)
                {
                    int id = active[ai];
                    Node node = nodes[id];
                    int a = node.Cand < 0 ? first : _cands[node.Cand].Pos;
                    if (cand.TrimEnd <= a && !last)
                    {
                        // Nothing but spaces between the line start and here: not a line.
                        ai++;
                        continue;
                    }

                    double avail = _width - (node.Line == 0 ? _firstIndent : 0);
                    double natural = _seg.Prefix[cand.TrimEnd - first] - _seg.Prefix[a - first] + (cand.Hyphen ? cand.HyphenWidth : 0);
                    double slack = avail - natural;
                    double r;
                    if (last)
                    {
                        r = slack >= -Eps ? 0 : -2;
                    }
                    else
                    {
                        double st = _stretch[cand.TrimEnd - first] - _stretch[a - first] + (emergency * avail);
                        double sh = _shrink[cand.TrimEnd - first] - _shrink[a - first];
                        if (_justify)
                        {
                            if (slack >= -Eps)
                            {
                                // A justified line with nothing to stretch stays short: as bad as can be, like glue-less lines in TeX.
                                r = slack <= Eps ? 0 : (st > Eps ? slack / st : NoStretch);
                            }
                            else
                            {
                                r = sh > Eps ? slack / sh : -2;
                            }
                        }
                        else
                        {
                            r = slack >= -Eps ? (slack <= Eps ? 0 : slack / ((RaggedStretch * avail) + (emergency * avail))) : -2;
                        }
                    }

                    if (r < -1)
                    {
                        if (cand.Hyphen)
                        {
                            // A hyphen's own width can make this break too wide while a later one is not: the node stays.
                            ai++;
                            continue;
                        }

                        if (final)
                        {
                            // The shortest way out wins (the piece alone on its line), then the fewest demerits so far.
                            double d = node.Demerits + Sq(_o.LinePenalty + InfBad);
                            if (a > fallbackStart || (a == fallbackStart && d < fallbackDem))
                            {
                                fallbackStart = a;
                                fallbackDem = d;
                                fallbackNode = id;
                            }
                        }

                        active.RemoveAt(ai);
                        continue;
                    }

                    ai++;
                    double bad = last ? 0 : Math.Min(InfBad, 100 * Math.Abs(r) * Math.Abs(r) * Math.Abs(r));
                    if (bad > threshold)
                    {
                        continue;
                    }

                    byte fit = r < 0 ? (bad > 12 ? (byte)3 : (byte)2) : (bad > 99 ? (byte)0 : bad > 12 ? (byte)1 : (byte)2);
                    double dem = Demerits(node, cand, bad, fit, last);
                    AddEntry(entries, fit, looseKeyed ? node.Line + 1 : 0, id, node.Demerits + dem);
                }

                if (entries.Count == 0 && active.Count == 0 && fallbackNode >= 0)
                {
                    // Even the least bad way out is too full: an unbreakable piece wider than the line. Take it, as greedy does.
                    Node fb = nodes[fallbackNode];
                    entries.Add(new Entry { Fit = 3, LineKey = looseKeyed ? fb.Line + 1 : 0, Prev = fallbackNode, Demerits = fallbackDem });
                }

                if (entries.Count == 0)
                {
                    if (active.Count == 0 && !last)
                    {
                        return null;
                    }

                    continue;
                }

                // The pruning of the paper, per line count when looseness is asked for: a class is kept when it is within the
                // fitness-change price of the best one with the same line count.
                foreach (Entry e in entries)
                {
                    double best = double.PositiveInfinity;
                    foreach (Entry other in entries)
                    {
                        if (other.LineKey == e.LineKey)
                        {
                            best = Math.Min(best, other.Demerits);
                        }
                    }

                    if (e.Demerits > best + _o.AdjacentFitnessDemerits)
                    {
                        continue;
                    }

                    var nn = new Node
                    {
                        Cand = c,
                        Prev = e.Prev,
                        Line = nodes[e.Prev].Line + 1,
                        Fit = e.Fit,
                        Flagged = cand.Flagged,
                        Demerits = e.Demerits,
                    };
                    nodes.Add(nn);
                    if (last)
                    {
                        finals.Add(nodes.Count - 1);
                    }
                    else
                    {
                        active.Add(nodes.Count - 1);
                    }
                }

                if (active.Count > MaxActive)
                {
                    Aborted = true;
                    return null;
                }
            }

            if (finals.Count == 0)
            {
                return null;
            }

            int chosen = Choose(nodes, finals);
            var cuts = new List<(int, bool)>();
            for (int id = chosen; id > 0; id = nodes[id].Prev)
            {
                Cand cd = _cands[nodes[id].Cand];
                cuts.Add((cd.Pos, cd.Hyphen));
            }

            cuts.Reverse();
            return cuts;
        }

        private int Choose(List<Node> nodes, List<int> finals)
        {
            int best = finals[0];
            foreach (int id in finals)
            {
                if (nodes[id].Demerits < nodes[best].Demerits)
                {
                    best = id;
                }
            }

            int loose = _o.Looseness;
            if (loose == 0)
            {
                return best;
            }

            // The line count nearest to the optimum's plus the looseness, on the requested side of it; the least demerits among equals.
            int target = nodes[best].Line + loose;
            int pick = best;
            int pickGap = Math.Abs(target - nodes[best].Line);
            foreach (int id in finals)
            {
                int lines = nodes[id].Line;
                if (loose > 0 ? lines < nodes[best].Line : lines > nodes[best].Line)
                {
                    continue;
                }

                int gap = Math.Abs(target - lines);
                if (gap < pickGap || (gap == pickGap && nodes[id].Demerits < nodes[pick].Demerits))
                {
                    pick = id;
                    pickGap = gap;
                }
            }

            return pick;
        }

        private double Demerits(Node from, Cand cand, double bad, byte fit, bool last)
        {
            double d = Sq(_o.LinePenalty + bad);
            double pen = cand.Penalty;
            if (pen >= 0)
            {
                d += pen * pen;
            }
            else
            {
                d -= pen * pen;
            }

            if (cand.Flagged && from.Flagged)
            {
                d += _o.ConsecutiveHyphenDemerits;
            }

            if (last && from.Flagged)
            {
                d += _o.FinalHyphenDemerits;
            }

            if (Math.Abs(fit - from.Fit) > 1)
            {
                d += _o.AdjacentFitnessDemerits;
            }

            return d;
        }

        private static double Sq(double x) => x * x;

        private static void AddEntry(List<Entry> entries, byte fit, int lineKey, int prev, double dem)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                if (e.Fit == fit && e.LineKey == lineKey)
                {
                    if (dem < e.Demerits)
                    {
                        entries[i] = new Entry { Fit = fit, LineKey = lineKey, Prev = prev, Demerits = dem };
                    }

                    return;
                }
            }

            entries.Add(new Entry { Fit = fit, LineKey = lineKey, Prev = prev, Demerits = dem });
        }
    }
}
