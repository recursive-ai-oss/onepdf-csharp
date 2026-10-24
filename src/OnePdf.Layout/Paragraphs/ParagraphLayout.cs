using OneFont;

namespace OnePdf.Layout;

/// <summary>One laid-out line of a paragraph. Distances are in points; <see cref="Top"/> and <see cref="Baseline"/> measure down from the paragraph's top.</summary>
public sealed class ParagraphLine
{
    internal ParagraphLine(int index, Segment segment, int itemStart, int itemEnd, int hyphenAfterItem)
    {
        Index = index;
        Segment = segment;
        ItemStart = itemStart;
        ItemEnd = itemEnd;
        HyphenAfterItem = hyphenAfterItem;
    }

    /// <summary>The line's position in its layout, from 0.</summary>
    public int Index { get; internal set; }

    /// <summary>Distance from the top of the layout to the top of the line.</summary>
    public double Top { get; internal set; }

    /// <summary>The line box height: ascent plus descent of the tallest content (and the line-height rule of the styles).</summary>
    public double Height { get; internal set; }

    /// <summary>Distance from <see cref="Top"/> down to the baseline.</summary>
    public double Baseline { get; internal set; }

    /// <summary>Width of the content, including any widening by justification and a closing hyphen; trailing spaces are not counted.</summary>
    public double Width { get; internal set; }

    /// <summary>Distance from the left edge of the paragraph box to the left edge of the content.</summary>
    public double X { get; internal set; }

    /// <summary>First character of the line in <see cref="ShapedParagraph.Text"/>.</summary>
    public int TextStart { get; internal set; }

    /// <summary>End (exclusive) of the line's characters in <see cref="ShapedParagraph.Text"/>, not counting trailing spaces.</summary>
    public int TextEnd { get; internal set; }

    /// <summary>True for the last line of a hard line (the paragraph's last line, or the line before a line end). Such lines are never justified.</summary>
    public bool IsParagraphEnd { get; internal set; }

    /// <summary>True when the line ends in a hyphen the text does not have (a soft hyphen or hyphenation point was used).</summary>
    public bool EndsWithHyphen => HyphenAfterItem >= 0;

    /// <summary>True when the content is wider than the width offered, because a piece that cannot be broken is too long.</summary>
    public bool Overflows { get; internal set; }

    internal Segment Segment { get; }

    internal int ItemStart { get; }

    internal int ItemEnd { get; }

    internal int HyphenAfterItem { get; }

    /// <summary>Extra width added to each stretchable space of the line by justification.</summary>
    internal double SpaceExtra { get; set; }

    /// <summary>Extra width added after each item but the last by letter-spaced justification.</summary>
    internal double LetterExtra { get; set; }

    internal ParagraphLine CopyAt(int index, double top) => new(index, Segment, ItemStart, ItemEnd, HyphenAfterItem)
    {
        Top = top,
        Height = Height,
        Baseline = Baseline,
        Width = Width,
        X = X,
        TextStart = TextStart,
        TextEnd = TextEnd,
        IsParagraphEnd = IsParagraphEnd,
        Overflows = Overflows,
        SpaceExtra = SpaceExtra,
        LetterExtra = LetterExtra,
    };
}

/// <summary>
/// A paragraph laid out at one width: lines with heights and baselines, ready to draw with <see cref="TextRenderer"/>. The line list is also
/// where pagination looks for places to split: any boundary between lines is one (<see cref="Split"/>).
/// </summary>
public sealed class ParagraphLayout
{
    internal ParagraphLayout(ShapedParagraph paragraph, double width, List<ParagraphLine> lines)
    {
        Paragraph = paragraph;
        Width = width;
        Lines = lines;
        double h = 0;
        foreach (ParagraphLine l in lines)
        {
            h = l.Top + l.Height;
        }

        Height = h;
    }

    /// <summary>The paragraph this is a layout of.</summary>
    public ShapedParagraph Paragraph { get; }

    /// <summary>The width the lines were broken for.</summary>
    public double Width { get; }

    /// <summary>The lines from top to bottom. At least one.</summary>
    public IReadOnlyList<ParagraphLine> Lines { get; }

    /// <summary>The sum of the line heights.</summary>
    public double Height { get; }

    /// <summary>The height of <paramref name="count"/> lines from line <paramref name="first"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The range is outside the lines.</exception>
    public double HeightOf(int first, int count)
    {
        if (first < 0 || count < 0 || first + count > Lines.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "The line range is outside the layout.");
        }

        return count == 0 ? 0 : Lines[first + count - 1].Top + Lines[first + count - 1].Height - Lines[first].Top;
    }

    /// <summary>
    /// Splits between two lines for pagination. The continuation holds the remaining lines exactly as an unsplit layout laid them out,
    /// moved up so that its first line is at the top.
    /// </summary>
    /// <param name="lineCount">How many lines stay in the first part, from 1 to <c>Lines.Count - 1</c>.</param>
    /// <returns>The first part and the continuation.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lineCount"/> leaves either part empty.</exception>
    public (ParagraphLayout First, ParagraphLayout Remainder) Split(int lineCount)
    {
        if (lineCount < 1 || lineCount >= Lines.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(lineCount), lineCount, "A split leaves at least one line on each side.");
        }

        var first = new List<ParagraphLine>(lineCount);
        for (int i = 0; i < lineCount; i++)
        {
            first.Add(Lines[i]);
        }

        var rest = new List<ParagraphLine>(Lines.Count - lineCount);
        double shift = Lines[lineCount].Top;
        for (int i = lineCount; i < Lines.Count; i++)
        {
            rest.Add(Lines[i].CopyAt(i - lineCount, Lines[i].Top - shift));
        }

        return (new ParagraphLayout(Paragraph, Width, first), new ParagraphLayout(Paragraph, Width, rest));
    }
}

public sealed partial class ShapedParagraph
{
    /// <summary>
    /// Breaks the paragraph into lines of at most <paramref name="width"/> points: greedy first fit over the line break opportunities of
    /// UAX #14, soft hyphens and hyphenation points (where a hyphen is added at the line end). A line is as full as it can be; trailing
    /// spaces hang past the edge and do not count. A piece with no opportunity that is wider than the width stays whole on a line of its own
    /// (<see cref="ParagraphLine.Overflows"/>).
    /// </summary>
    /// <param name="width">The line width in points, finite and positive.</param>
    /// <returns>The layout.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> is not a positive finite number.</exception>
    public ParagraphLayout Layout(double width)
    {
        if (!(width > 0) || !double.IsFinite(width))
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "The width must be positive and finite.");
        }

        var lines = new List<ParagraphLine>();
        double top = 0;
        bool firstLine = true;
        bool overflowNoted = false;
        foreach (Segment seg in _segments)
        {
            if (seg.Count == 0)
            {
                Fmt strut = _fmts[seg.StrutFmt];
                var empty = new ParagraphLine(lines.Count, seg, seg.First, seg.First, -1)
                {
                    Top = top,
                    Height = strut.Ascent + strut.Descent,
                    Baseline = strut.Ascent,
                    TextStart = seg.TextStart,
                    TextEnd = seg.TextStart,
                    IsParagraphEnd = true,
                };
                lines.Add(empty);
                top += empty.Height;
                firstLine = false;
                continue;
            }

            int end = seg.First + seg.Count;
            int i = seg.First;
            bool rtl = (seg.ParaLevel & 1) == 1;
            List<(int Cut, bool Hyphen)>? optimal = UseKnuthPlass ? BreakOptimal(seg, firstLine ? _source.FirstLineIndent : 0, width) : null;
            int optimalNext = 0;
            while (i < end)
            {
                double indent = firstLine ? _source.FirstLineIndent : 0;
                double avail = width - indent;
                (int cut, bool hyphen, bool overflow) = optimal is null ? FindBreak(seg, i, end, avail) : (optimal[optimalNext].Cut, optimal[optimalNext++].Hyphen, false);

                int lineEnd = cut;
                while (lineEnd > i && _items[lineEnd - 1].TrimSpace)
                {
                    lineEnd--;
                }

                int hyphenAfter = hyphen && lineEnd > i ? lineEnd - 1 : -1;
                double content = seg.Prefix[lineEnd - seg.First] - seg.Prefix[i - seg.First];
                if (hyphenAfter >= 0)
                {
                    content += HyphenFor(hyphenAfter).Advance;
                }

                bool paragraphEnd = cut == end;
                var line = new ParagraphLine(lines.Count, seg, i, lineEnd, hyphenAfter)
                {
                    TextStart = _items[i].Start,
                    TextEnd = lineEnd > i ? _items[lineEnd - 1].Start + _items[lineEnd - 1].Length : _items[i].Start,
                    IsParagraphEnd = paragraphEnd,
                    Overflows = overflow || content > avail + Eps,
                };
                bool squeezed = false;
                if (optimal is not null && !overflow && !paragraphEnd && content > avail + Eps && _source.Alignment == TextAlignment.Justify && content - avail <= ShrinkRoom(i, lineEnd) + Eps)
                {
                    // A line the total-fit breaker chose to squeeze: its spaces narrow instead of widen.
                    line.Overflows = false;
                    squeezed = true;
                }
                if (line.Overflows && !overflowNoted)
                {
                    overflowNoted = true;
                    Warn("A word or unbreakable piece is wider than the line.");
                }

                double lineWidth = content;
                TextAlignment align = _source.Alignment;
                if (align == TextAlignment.Justify)
                {
                    if (!paragraphEnd && !line.Overflows && (avail - content > Eps || squeezed))
                    {
                        double extra = avail - content;
                        int stretch = 0;
                        for (int k = i; k < lineEnd; k++)
                        {
                            if (_items[k].Stretch)
                            {
                                stretch++;
                            }
                        }

                        if (stretch > 0)
                        {
                            line.SpaceExtra = extra / stretch;
                            lineWidth = avail;
                        }
                        else if (_source.JustifyWithLetterSpacing && lineEnd - i > 1)
                        {
                            line.LetterExtra = extra / (lineEnd - i - 1 + (hyphenAfter >= 0 ? 1 : 0));
                            lineWidth = avail;
                        }
                    }

                    align = TextAlignment.Start;
                }

                line.Width = lineWidth;
                line.X = PlaceX(align, rtl, width, indent, lineWidth);

                double asc = 0;
                double desc = 0;
                for (int k = i; k < lineEnd; k++)
                {
                    Fmt f = _fmts[_items[k].Fmt];
                    asc = Math.Max(asc, f.Ascent);
                    desc = Math.Max(desc, f.Descent);
                }

                if (lineEnd == i)
                {
                    Fmt strut = _fmts[seg.StrutFmt];
                    asc = strut.Ascent;
                    desc = strut.Descent;
                }
                else if (HasInline)
                {
                    ApplyInlineExtents(i, lineEnd, ref asc, ref desc);
                }

                line.Baseline = asc;
                line.Height = asc + desc;
                line.Top = top;
                top += line.Height;
                lines.Add(line);
                firstLine = false;
                i = cut;
            }
        }

        return new ParagraphLayout(this, width, lines);
    }

    private static double PlaceX(TextAlignment align, bool rtl, double width, double indent, double lineWidth)
    {
        double leftInset = rtl ? 0 : indent;
        double rightInset = rtl ? indent : 0;
        TextAlignment a = align switch
        {
            TextAlignment.Start => rtl ? TextAlignment.Right : TextAlignment.Left,
            TextAlignment.End => rtl ? TextAlignment.Left : TextAlignment.Right,
            _ => align,
        };
        switch (a)
        {
            case TextAlignment.Right:
                return width - rightInset - lineWidth;
            case TextAlignment.Center:
                return leftInset + ((width - leftInset - rightInset - lineWidth) / 2);
            default:
                return a == TextAlignment.Left && rtl ? 0 : leftInset;
        }
    }

    // Greedy first fit: the last break opportunity at which the line, without its trailing spaces (and with a hyphen where the break
    // is a hyphenation point), still fits. Returns the item the next line starts at.
    private (int Cut, bool Hyphen, bool Overflow) FindBreak(Segment seg, int start, int end, double avail)
    {
        int best = -1;
        bool bestHyphen = false;
        double raw = 0;
        double trail = 0;
        for (int j = start; j < end; j++)
        {
            Item it = _items[j];
            if (j > start)
            {
                double trimmed = raw - trail;
                if (it.BreakOk || it.HyphenOk)
                {
                    if (trimmed > avail + Eps)
                    {
                        // Past the edge. Take the best earlier break; with none, the piece cannot be broken, so end it here.
                        return best >= 0 ? (best, bestHyphen, false) : (j, it.HyphenOk, true);
                    }

                    if (it.BreakOk)
                    {
                        best = j;
                        bestHyphen = false;
                    }
                    else if (trimmed + HyphenFor(j - 1).Advance <= avail + Eps)
                    {
                        best = j;
                        bestHyphen = true;
                    }
                }
                else if (best >= 0 && trimmed > avail + Eps)
                {
                    return (best, bestHyphen, false);
                }
            }

            raw += it.Advance;
            trail = it.TrimSpace ? trail + it.Advance : 0;
        }

        if (raw - trail <= avail + Eps)
        {
            return (end, false, false);
        }

        return best >= 0 ? (best, bestHyphen, false) : (end, false, true);
    }

    internal Hyph HyphenFor(int itemIndex)
    {
        int fmtIndex = _items[itemIndex].Fmt;
        _hyphens ??= [];
        if (_hyphens.TryGetValue(fmtIndex, out Hyph h))
        {
            return h;
        }

        Fmt fmt = _fmts[fmtIndex];
        FontFace face = fmt.Face;
        int cp = fmt.IsStandard ? '-' : (face.HasGlyph(0x2010) ? 0x2010 : '-');
        int gid = face.GetGlyphIndex(cp);
        double adv = face.GetAdvance(gid) * fmt.Size / face.UnitsPerEm;
        h = new Hyph(fmtIndex, gid, adv, fmt.IsStandard ? "-" : "\u00AD");
        _hyphens[fmtIndex] = h;
        return h;
    }
}
