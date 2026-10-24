using OneFont;

namespace OnePdf.Layout;

// Inline objects in a paragraph: how they enter the item list, how they affect line breaking, and where they sit against a line.
public sealed partial class ShapedParagraph
{
    private void AddInline(int pos, int inlineIndex, int[] groups, List<ResolvedChain> chains, int span, byte level)
    {
        InlineObject obj = _norm.Inlines![inlineIndex];
        // The object takes the font of the span it stands in: that font gives the text metrics it is aligned against, and the line's
        // minimum height, exactly as a space in the same place would.
        int fmtIndex = GetFmt(chains[groups[span]].Entries[0], span, false);
        _items.Add(new Item
        {
            Start = pos,
            Length = 1,
            Fmt = fmtIndex,
            GlyphStart = _glyphs.Count,
            GlyphCount = 0,
            Advance = obj.ResolvedWidth,
            Level = level,
            Inline = inlineIndex + 1,
        });
    }

    // U+FFFC is class CB, which allows a break on both sides (UAX #14 LB20). An object marked to stay with its neighbours gets neither.
    private void GlueInlines(int segStart, int segLength, LineBreakOpportunity[] opp)
    {
        foreach ((int pos, int index) in _norm.InlineAt!)
        {
            int rel = pos - segStart;
            if (rel < 0 || rel >= segLength || !_norm.Inlines![index].KeepWithNeighbours)
            {
                continue;
            }

            opp[rel] = LineBreakOpportunity.None;
            if (rel + 1 <= segLength)
            {
                opp[rel + 1] = LineBreakOpportunity.None;
            }
        }
    }

    /// <summary>
    /// The distance from the baseline up to the bottom edge of an inline object (negative when it hangs below), for a line whose box extends
    /// <paramref name="lineAscent"/> above the baseline and <paramref name="lineDescent"/> below it.
    /// </summary>
    internal static double InlineBottom(InlineObject o, Fmt f, double lineAscent, double lineDescent)
    {
        double h = o.ResolvedHeight;
        return o.Alignment switch
        {
            InlineAlignment.Top => lineAscent - h,
            InlineAlignment.Bottom => -lineDescent,
            InlineAlignment.Middle => ((f.FontAscent - f.FontDescent) / 2) - (h / 2),
            InlineAlignment.TextTop => f.FontAscent - h,
            InlineAlignment.TextBottom => -f.FontDescent,
            _ => o.BaselineOffset - o.ResolvedInnerBaseline,
        };
    }

    // Grows the line box to hold its inline objects. Objects placed against the baseline or the text come first; then objects aligned to the
    // top of the line extend it downward and objects aligned to the bottom extend it upward, as far as they are taller than what is there.
    private void ApplyInlineExtents(int from, int to, ref double asc, ref double desc)
    {
        for (int k = from; k < to; k++)
        {
            Item it = _items[k];
            if (it.Inline == 0)
            {
                continue;
            }

            InlineObject o = InlineOf(it);
            if (!o.GrowsLine || o.Alignment is InlineAlignment.Top or InlineAlignment.Bottom)
            {
                continue;
            }

            double bottom = InlineBottom(o, _fmts[it.Fmt], asc, desc);
            asc = Math.Max(asc, bottom + o.ResolvedHeight);
            desc = Math.Max(desc, -bottom);
        }

        for (int pass = 0; pass < 2; pass++)
        {
            InlineAlignment which = pass == 0 ? InlineAlignment.Top : InlineAlignment.Bottom;
            for (int k = from; k < to; k++)
            {
                Item it = _items[k];
                if (it.Inline == 0)
                {
                    continue;
                }

                InlineObject o = InlineOf(it);
                if (!o.GrowsLine || o.Alignment != which)
                {
                    continue;
                }

                double h = o.ResolvedHeight;
                if (h > asc + desc)
                {
                    if (which == InlineAlignment.Top)
                    {
                        desc = h - asc;
                    }
                    else
                    {
                        asc = h - desc;
                    }
                }
            }
        }
    }
}
