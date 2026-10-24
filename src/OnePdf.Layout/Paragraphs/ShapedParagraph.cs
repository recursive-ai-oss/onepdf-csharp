using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using OneFont;

namespace OnePdf.Layout;

/// <summary>A font, a style and an effective size: what an item of text is drawn with.</summary>
internal sealed class Fmt(FontEntry entry, TextStyle style, double size, double rise, int span = 0, bool synthesized = false)
{
    public int Span { get; } = span;

    public bool Synthesized { get; } = synthesized;

    public FontEntry Entry { get; } = entry;

    public FontFace Face { get; } = entry.Face;

    public TextStyle Style { get; } = style;

    /// <summary>Size in points after superscript/subscript scaling.</summary>
    public double Size { get; } = size;

    /// <summary>Shift of the baseline in points (up is positive); 0 when the font's own glyphs are used.</summary>
    public double Rise { get; } = rise;

    public bool IsStandard => Entry.StandardName is not null;

    public double Ascent { get; set; }

    public double Descent { get; set; }
}

/// <summary>One shaped cluster placed in logical order.</summary>
internal struct Item
{
    /// <summary>Start in the normalised text, and length.</summary>
    public int Start;
    public int Length;
    public int Fmt;
    public int GlyphStart;
    public int GlyphCount;

    /// <summary>Advance in points including letter spacing.</summary>
    public double Advance;
    public double Spacing;
    public byte Level;
    public bool TrimSpace;
    public bool Stretch;
    public bool BreakOk;
    public bool HyphenOk;
}

internal struct GlyphRec
{
    public int Gid;
    public double Advance;
    public double Dx;
    public double Dy;
}

/// <summary>A hard line of the paragraph: its items, bidi level and prefix widths.</summary>
internal sealed class Segment
{
    public int TextStart;
    public int TextEnd;
    public int First;
    public int Count;
    public byte ParaLevel;
    public int StrutFmt;

    /// <summary>Cumulative advances: Prefix[k] is the width of the segment's first k items.</summary>
    public double[] Prefix = [];
}

/// <summary>
/// A paragraph after shaping: faces chosen, clusters shaped, break opportunities known. It lays out at any width with <see cref="Layout"/>
/// and reports its content widths. Shaped paragraphs are immutable, so one may be laid out several times and at several widths.
/// </summary>
public sealed partial class ShapedParagraph
{
    private const double Eps = 1e-9;
    private const double SuperscriptScale = 0.65;
    private const double SuperscriptDefaultRise = 0.35;
    private const double SubscriptDefaultDrop = 0.14;

    private readonly TextParagraph _source;
    private readonly NormalizedText _norm;
    private readonly List<Fmt> _fmts = [];
    private readonly List<Item> _items;
    private readonly List<GlyphRec> _glyphs;
    private readonly List<Segment> _segments = [];

    // Built when the paragraph has more than a few formats, which a linear search no longer serves; null before.
    private Dictionary<(FontEntry, int, bool), int>? _fmtIndex;
    private Dictionary<long, Hyph>? _hyphens;
    private List<string>? _warnings;

    // Only paragraphs with soft hyphens or a hyphenator have break-with-hyphen places; null for the rest.
    private bool[]? _discretionary;

    internal ShapedParagraph(TextParagraph source, TextFontRegistry fonts)
    {
        _source = source;
        _norm = NormalizedText.Create(source);
        // About one item and one glyph per character; sizing the lists once avoids the garbage of doubling them.
        int guess = _norm.Text.Length + 1;
        _items = new List<Item>(guess);
        _glyphs = new List<GlyphRec>(guess);
        if (_norm.SoftHyphens.Count > 0 || source.Hyphenator is not null)
        {
            _discretionary = new bool[_norm.Text.Length + 1];
            foreach (int p in _norm.SoftHyphens)
            {
                _discretionary[p] = true;
            }
        }

        // Spans that shape the same way (same faces, language and script position) are shaped together, so Arabic joining and kerning
        // continue across a change of colour or size.
        int spanCount = _norm.SpanStarts.Count;
        var groups = new int[spanCount];
        var chains = new List<ResolvedChain>(1);
        var groupStyles = new List<TextStyle>(1);
        if (spanCount == 1)
        {
            chains.Add(fonts.Resolve(_norm.Styles[0]));
            groupStyles.Add(_norm.Styles[0]);
        }
        else
        {
            var groupKeys = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int s = 0; s < spanCount; s++)
            {
                TextStyle st = _norm.Styles[s];
                string key = string.Create(CultureInfo.InvariantCulture, $"{TextFontRegistry.ChainKey(st)}#{st.Language}#{(int)st.Position}");
                if (!groupKeys.TryGetValue(key, out int g))
                {
                    g = chains.Count;
                    groupKeys[key] = g;
                    chains.Add(fonts.Resolve(st));
                    groupStyles.Add(st);
                }

                groups[s] = g;
            }
        }

        string text = _norm.Text;
        int segStart = 0;
        int spanCursor = 0;
        while (true)
        {
            int nl = text.IndexOf('\n', segStart);
            int segEnd = nl < 0 ? text.Length : nl;
            _segments.Add(ShapeSegment(segStart, segEnd, groups, chains, groupStyles, ref spanCursor));
            if (nl < 0)
            {
                break;
            }

            segStart = nl + 1;
        }

        ComputeMetrics();
        ComputeContentWidths();
    }

    /// <summary>Messages about things the layout could not do exactly (a character no face has, a word wider than the line).</summary>
    public IReadOnlyList<string> Warnings => _warnings ?? (IReadOnlyList<string>)Array.Empty<string>();

    /// <summary>
    /// The width of the widest piece that cannot be broken: the longest run between line break opportunities (UAX #14, soft hyphens and
    /// hyphenation points, counting the hyphen). The paragraph never needs to be narrower than this unless a word overflows.
    /// </summary>
    public double MinContentWidth { get; private set; }

    /// <summary>The width of the longest line when nothing wraps, including the first-line indent.</summary>
    public double MaxContentWidth { get; private set; }

    /// <summary>The normalised text the layout works on (see <see cref="TextParagraph"/>): line ends are <c>\n</c> and soft hyphens are gone.</summary>
    public string Text => _norm.Text;

    internal int ItemCount => _items.Count;

    internal Item ItemAt(int index) => _items[index];

    internal Fmt FmtAt(int index) => _fmts[index];

    internal GlyphRec GlyphAt(int index) => _glyphs[index];

    private Segment ShapeSegment(int s, int e, int[] groups, List<ResolvedChain> chains, List<TextStyle> groupStyles, ref int spanCursor)
    {
        var seg = new Segment { TextStart = s, TextEnd = e, First = _items.Count };
        while (spanCursor + 1 < _norm.SpanStarts.Count && _norm.SpanStarts[spanCursor + 1] <= s)
        {
            spanCursor++;
        }

        int strutSpan = spanCursor;
        seg.StrutFmt = GetFmt(chains[groups[strutSpan]].Entries[0], strutSpan, false);
        if (e > s)
        {
            ReadOnlySpan<char> segText = _norm.Text.AsSpan(s, e - s);
            TextDirection dir = _source.Direction switch
            {
                ParagraphDirection.LeftToRight => TextDirection.LeftToRight,
                ParagraphDirection.RightToLeft => TextDirection.RightToLeft,
                _ => TextDirection.Auto,
            };
            byte[] levels = ArrayPool<byte>.Shared.Rent(segText.Length);
            LineBreakOpportunity[]? opp = null;
            try
            {
                int paraLevel;
                if (dir != TextDirection.RightToLeft && IsBelowHebrew(segText))
                {
                    // No character before U+0590 is right-to-left, an Arabic-number or a bidi control (those start at U+0590, U+0600, U+2000):
                    // every level is 0 and so is the paragraph level (UAX #9 P2/P3 with no strong right-to-left character).
                    Array.Clear(levels, 0, segText.Length);
                    paraLevel = 0;
                }
                else
                {
                    TextShaping.GetBidiLevels(segText, dir, levels, out paraLevel);
                }

                seg.ParaLevel = (byte)paraLevel;
                MarkHyphenation(s, e);

                int i = 0;
                int span = strutSpan;
                while (i < segText.Length)
                {
                    int pos = s + i;
                    span = SpanOf(pos, span);
                    int grp = groups[span];
                    byte level = levels[i];
                    int j = i + 1;
                    int spanJ = span;
                    while (j < segText.Length && levels[j] == level)
                    {
                        spanJ = SpanOf(s + j, spanJ);
                        if (groups[spanJ] != grp)
                        {
                            break;
                        }

                        j++;
                    }

                    ShapeRun(s, i, j, level, chains[grp], groupStyles[grp], span);
                    i = j;
                }

                opp = ArrayPool<LineBreakOpportunity>.Shared.Rent(segText.Length + 1);
                TextSegmentation.GetLineBreakOpportunities(segText, opp);
                for (int k = seg.First; k < _items.Count; k++)
                {
                    Item it = _items[k];
                    int rel = it.Start - s;
                    bool breakOk = k > seg.First && opp[rel] != LineBreakOpportunity.None;
                    it.BreakOk = breakOk;
                    it.HyphenOk = k > seg.First && !breakOk && _discretionary is not null && _discretionary[it.Start];
                    _items[k] = it;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(levels);
                if (opp is not null)
                {
                    ArrayPool<LineBreakOpportunity>.Shared.Return(opp);
                }
            }
        }

        seg.Count = _items.Count - seg.First;
        seg.Prefix = new double[seg.Count + 1];
        double sum = 0;
        for (int k = 0; k < seg.Count; k++)
        {
            sum += _items[seg.First + k].Advance;
            seg.Prefix[k + 1] = sum;
        }

        return seg;
    }

    private static bool IsBelowHebrew(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (c >= '\u0590')
            {
                return false;
            }
        }

        return true;
    }

    private int SpanOf(int pos, int from)
    {
        int k = from;
        while (k + 1 < _norm.SpanStarts.Count && _norm.SpanStarts[k + 1] <= pos)
        {
            k++;
        }

        return k;
    }

    private int GetFmt(FontEntry entry, int span, bool synthesized)
    {
        if (_fmtIndex is null)
        {
            for (int i = 0; i < _fmts.Count; i++)
            {
                Fmt f = _fmts[i];
                if (ReferenceEquals(f.Entry, entry) && f.Span == span && f.Synthesized == synthesized)
                {
                    return i;
                }
            }
        }
        else if (_fmtIndex.TryGetValue((entry, span, synthesized), out int idx))
        {
            return idx;
        }

        TextStyle st = _norm.Styles[span];
        double size = st.Size;
        double rise = 0;
        if (synthesized)
        {
            FontFace face = entry.Face;
            size = st.Size * SuperscriptScale;
            if (st.Position == ScriptPosition.Superscript)
            {
                rise = face.SuperscriptYOffset > 0 ? face.SuperscriptYOffset * st.Size / face.UnitsPerEm : SuperscriptDefaultRise * st.Size;
            }
            else
            {
                rise = -(face.SubscriptYOffset > 0 ? face.SubscriptYOffset * st.Size / face.UnitsPerEm : SubscriptDefaultDrop * st.Size);
            }
        }

        _fmts.Add(new Fmt(entry, st, size, rise, span, synthesized));
        if (_fmtIndex is null && _fmts.Count > 8)
        {
            _fmtIndex = [];
            for (int i = 0; i < _fmts.Count; i++)
            {
                _fmtIndex[(_fmts[i].Entry, _fmts[i].Span, _fmts[i].Synthesized)] = i;
            }
        }
        else if (_fmtIndex is not null)
        {
            _fmtIndex[(entry, span, synthesized)] = _fmts.Count - 1;
        }

        return _fmts.Count - 1;
    }

    private void MarkHyphenation(int s, int e)
    {
        IHyphenator? h = _source.Hyphenator;
        if (h is null || _discretionary is null)
        {
            return;
        }

        string text = _norm.Text;
        int span = 0;
        int i = s;
        while (i < e)
        {
            if (!IsWordChar(text[i]))
            {
                i++;
                continue;
            }

            int j = i + 1;
            while (j < e && IsWordChar(text[j]))
            {
                j++;
            }

            if (j - i >= 2)
            {
                span = SpanOf(i, span);
                IReadOnlyList<int> points = h.GetBreaks(text.Substring(i, j - i), _norm.Styles[span].Language);
                if (points is not null)
                {
                    foreach (int p in points)
                    {
                        if (p >= 1 && p < j - i)
                        {
                            _discretionary[i + p] = true;
                        }
                    }
                }
            }

            i = j;
        }
    }

    private static bool IsWordChar(char c) =>
        char.IsLetter(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;

    private void ShapeRun(int segStart, int from, int to, byte level, ResolvedChain chain, TextStyle groupStyle, int firstSpan)
    {
        ReadOnlySpan<char> run = _norm.Text.AsSpan(segStart + from, to - from);
        int runStart = segStart + from;
        bool rtl = (level & 1) == 1;
        int span = firstSpan;
        if (chain.IsStandard)
        {
            ShapeStandard(runStart, run, level, chain.Entries[0], ref span);
            return;
        }

        bool sup = groupStyle.Position != ScriptPosition.Normal;
        ShapingOptions options = OptionsFor(groupStyle.Language, groupStyle.Position);
        ShapedRun clusters = _runs ??= new ShapedRun();
        chain.Chain!.ShapeRun(run, clusters, rtl, options);
        bool synthesize = false;
        if (sup)
        {
            // The font has the feature when turning it on changes the glyphs; otherwise scale and shift.
            ShapedRun plain = _plainRun ??= new ShapedRun();
            chain.Chain.ShapeRun(run, plain, rtl, OptionsFor(groupStyle.Language, ScriptPosition.Normal));
            synthesize = SameGlyphs(clusters, plain);
        }

        foreach (ShapedClusterRange c in clusters.Clusters)
        {
            int start = runStart + c.Start;
            span = SpanOf(start, span);
            int fmtIndex = GetFmt(chain.Entries[c.FaceIndex], span, synthesize);
            Fmt fmt = _fmts[fmtIndex];
            double scale = fmt.Size / fmt.Face.UnitsPerEm;
            int glyphStart = _glyphs.Count;
            double adv = 0;
            foreach (ShapedGlyph g in clusters.GlyphsOf(c))
            {
                double a = g.XAdvance * scale;
                _glyphs.Add(new GlyphRec { Gid = g.GlyphId, Advance = a, Dx = g.XOffset * scale, Dy = (g.YOffset * scale) + fmt.Rise });
                adv += a;
            }

            if (c.HasMissingGlyph)
            {
                Warn("A character has no glyph in any font of the chain (U+" + CodePointAt(_norm.Text, start, out _).ToString("X4", CultureInfo.InvariantCulture) + ").");
            }

            AddItem(start, c.Length, fmtIndex, glyphStart, c.GlyphCount, adv, level);
        }
    }

    // Shaping only reads its options, so one instance per language and script position is shared by every call and thread.
    private static readonly ConcurrentDictionary<(string Language, ScriptPosition Position), ShapingOptions> OptionCache = new();

    private static ShapingOptions OptionsFor(string? language, ScriptPosition position) =>
        OptionCache.GetOrAdd((language ?? string.Empty, position), static (key, lang) =>
        {
            var o = new ShapingOptions { Language = lang };
            if (key.Position != ScriptPosition.Normal)
            {
                o.Features.Add(FontFeature.On(key.Position == ScriptPosition.Superscript ? "sups" : "subs"));
            }

            return o;
        }, language);

    // The buffers the shaper fills, one pair per thread: shaping output is copied into the paragraph's own arrays at once.
    [ThreadStatic]
    private static ShapedRun? _runs;

    [ThreadStatic]
    private static ShapedRun? _plainRun;

    private void ShapeStandard(int runStart, ReadOnlySpan<char> run, byte level, FontEntry entry, ref int span)
    {
        int[] bounds = TextSegmentation.GetGraphemeBoundaries(run);
        for (int b = 0; b + 1 < bounds.Length; b++)
        {
            int start = runStart + bounds[b];
            int len = bounds[b + 1] - bounds[b];
            span = SpanOf(start, span);
            int fmtIndex = GetFmt(entry, span, false);
            Fmt fmt = _fmts[fmtIndex];
            double scale = fmt.Size / fmt.Face.UnitsPerEm;
            double adv = 0;
            for (int k = 0; k < len;)
            {
                int cp = CodePointAt(_norm.Text, start + k, out int used);
                adv += fmt.Face.GetAdvance(fmt.Face.GetGlyphIndex(cp)) * scale;
                k += used;
            }

            int glyphStart = _glyphs.Count;
            _glyphs.Add(new GlyphRec { Gid = 0, Advance = adv });
            AddItem(start, len, fmtIndex, glyphStart, 1, adv, level);
        }
    }

    private void AddItem(int start, int length, int fmtIndex, int glyphStart, int glyphCount, double glyphAdvance, byte level)
    {
        string text = _norm.Text;
        Fmt fmt = _fmts[fmtIndex];
        char first = text[start];
        bool single = length == 1;
        bool space = single && (first == ' ' || first == '\u3000' || first == '\u00A0');
        bool trim = single && char.IsWhiteSpace(first) && first != '\u00A0' && first != '\u2007' && first != '\u202F';
        double spacing = fmt.Style.LetterSpacing;
        _items.Add(new Item
        {
            Start = start,
            Length = length,
            Fmt = fmtIndex,
            GlyphStart = glyphStart,
            GlyphCount = glyphCount,
            Advance = glyphAdvance + spacing,
            Spacing = spacing,
            Level = level,
            TrimSpace = trim,
            Stretch = space,
        });
    }

    internal static int CodePointAt(string text, int index, out int used)
    {
        char c = text[index];
        if (char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            used = 2;
            return char.ConvertToUtf32(c, text[index + 1]);
        }

        used = 1;
        return char.IsSurrogate(c) ? 0xFFFD : c;
    }

    private static bool SameGlyphs(ShapedRun a, ShapedRun b)
    {
        if (a.ClusterCount != b.ClusterCount)
        {
            return false;
        }

        for (int i = 0; i < a.ClusterCount; i++)
        {
            ReadOnlySpan<ShapedGlyph> x = a.GlyphsOf(a.Clusters[i]);
            ReadOnlySpan<ShapedGlyph> y = b.GlyphsOf(b.Clusters[i]);
            if (x.Length != y.Length || a.Clusters[i].FaceIndex != b.Clusters[i].FaceIndex)
            {
                return false;
            }

            for (int k = 0; k < x.Length; k++)
            {
                if (x[k].GlyphId != y[k].GlyphId)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private void Warn(string message)
    {
        _warnings ??= [];
        if (_warnings.Count < 32 && !_warnings.Contains(message))
        {
            _warnings.Add(message);
        }
    }

    // Ascent and descent of a line box for text in each format: the font's metrics, with the line gap (or the extra of a fixed line
    // height) split over the two sides, and superscripts and subscripts extending the line when it has no fixed height.
    private void ComputeMetrics()
    {
        foreach (Fmt f in _fmts)
        {
            FontMetrics m = f.Face.Metrics;
            double sc = f.Size / f.Face.UnitsPerEm;
            double a = m.Ascender * sc;
            double d = Math.Abs(m.Descender) * sc;
            double gap = Math.Max(0, m.LineGap) * sc;
            double total = f.Style.LineHeight is { } lh ? lh * f.Style.Size : a + d + gap;
            double half = (total - (a + d)) / 2;
            double asc = a + half;
            double desc = d + half;
            if (f.Style.LineHeight is null)
            {
                asc += Math.Max(f.Rise, 0);
                desc += Math.Max(-f.Rise, 0);
            }

            f.Ascent = Math.Max(asc, 0);
            f.Descent = Math.Max(desc, 0);
        }
    }

    private void ComputeContentWidths()
    {
        double min = 0;
        double max = 0;
        bool firstSeg = true;
        foreach (Segment seg in _segments)
        {
            if (seg.Count == 0)
            {
                firstSeg = false;
                continue;
            }

            // Max: the whole segment on one line without trailing spaces.
            int end = seg.First + seg.Count;
            int last = end;
            while (last > seg.First && _items[last - 1].TrimSpace)
            {
                last--;
            }

            double whole = seg.Prefix[last - seg.First] + (firstSeg ? _source.FirstLineIndent : 0);
            max = Math.Max(max, whole);
            firstSeg = false;

            // Min: the widest piece between break opportunities.
            int pieceStart = seg.First;
            for (int k = seg.First + 1; k <= end; k++)
            {
                bool cut = k == end || _items[k].BreakOk || _items[k].HyphenOk;
                if (!cut)
                {
                    continue;
                }

                int pe = k;
                while (pe > pieceStart && _items[pe - 1].TrimSpace)
                {
                    pe--;
                }

                double w = seg.Prefix[pe - seg.First] - seg.Prefix[pieceStart - seg.First];
                if (k < end && _items[k].HyphenOk)
                {
                    w += HyphenFor(k - 1).Advance;
                }

                min = Math.Max(min, w);
                pieceStart = k;
            }
        }

        MinContentWidth = min;
        MaxContentWidth = max;
    }
}

/// <summary>The hyphen put at the end of a line: its glyph in the face of the text before it.</summary>
internal readonly record struct Hyph(int Fmt, int Gid, double Advance, string Text);
