using System.Globalization;

namespace OnePdf.Appearance;

/// <summary>The parts of a default appearance string (<c>/DA</c>, ISO 32000-2 12.7.4.3) that a generated appearance uses.</summary>
internal readonly record struct DefaultAppearance(string? FontName, double Size, double[]? Color)
{
    private const int MaxTokens = 512;

    /// <summary>Reads <c>/Name size Tf</c> and the last <c>g</c>, <c>rg</c> or <c>k</c> colour; anything else is ignored. Never throws.</summary>
    public static DefaultAppearance Parse(string? da)
    {
        string? font = null;
        double size = 0;
        double[]? color = null;
        if (string.IsNullOrWhiteSpace(da))
        {
            return new DefaultAppearance(null, 0, null);
        }

        string[] tok = da.Split([' ', '\t', '\r', '\n', '\f', '\0'], MaxTokens, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tok.Length; i++)
        {
            switch (tok[i])
            {
                case "Tf" when i >= 2 && tok[i - 2].StartsWith('/'):
                    font = tok[i - 2][1..];
                    size = Number(tok[i - 1]);
                    break;
                case "g" when i >= 1:
                    color = [Number(tok[i - 1])];
                    break;
                case "rg" when i >= 3:
                    color = [Number(tok[i - 3]), Number(tok[i - 2]), Number(tok[i - 1])];
                    break;
                case "k" when i >= 4:
                    color = [Number(tok[i - 4]), Number(tok[i - 3]), Number(tok[i - 2]), Number(tok[i - 1])];
                    break;
            }
        }

        // A size beyond any real page is a hostile value, not a wish.
        if (!double.IsFinite(size) || size < 0)
        {
            size = 0;
        }
        else if (size > 1000)
        {
            size = 1000;
        }

        return new DefaultAppearance(font, size, color);
    }

    private static double Number(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : 0;
}

/// <summary>One laid-out line: a half-open range of the glyph list and its width at font size 1.</summary>
internal readonly record struct TextLine(int Start, int End, double Width);

internal static class TextLayout
{
    /// <summary>Line height as a multiple of the font size (ascent plus descent plus gap of the usual sans-serif faces).</summary>
    public const double LineHeight = 1.15;

    /// <summary>The height of a capital letter as a multiple of the font size, used to centre single lines vertically.</summary>
    public const double CapHeight = 0.72;

    /// <summary>The distance from the top of a line box to its baseline as a multiple of the font size.</summary>
    public const double Ascent = 0.9;

    public static double Width(List<ShapedGlyph> g, int start, int end)
    {
        double w = 0;
        for (int i = start; i < end; i++)
        {
            w += g[i].Advance;
        }

        return w;
    }

    /// <summary>
    /// Breaks glyphs into lines no wider than <paramref name="avail"/> at <paramref name="size"/>: at explicit breaks, at the last
    /// space that fits, and inside a word that is wider than the line. Stops after <paramref name="maxLines"/> lines.
    /// </summary>
    public static List<TextLine> Wrap(List<ShapedGlyph> g, double avail, double size, int maxLines)
    {
        var lines = new List<TextLine>();
        int start = 0, lastSpace = -1;
        double acc = 0;
        for (int i = 0; i < g.Count && lines.Count < maxLines; i++)
        {
            ShapedGlyph x = g[i];
            if (x.IsBreak)
            {
                lines.Add(new TextLine(start, i, Width(g, start, i)));
                start = i + 1;
                lastSpace = -1;
                acc = 0;
                continue;
            }

            acc += x.Advance * size;
            if (x.IsSpace)
            {
                lastSpace = i;
                continue;
            }

            if (acc > avail && i > start)
            {
                if (lastSpace >= start)
                {
                    lines.Add(new TextLine(start, lastSpace, Width(g, start, lastSpace)));
                    start = lastSpace + 1;
                }
                else
                {
                    lines.Add(new TextLine(start, i, Width(g, start, i)));
                    start = i;
                }

                lastSpace = -1;
                acc = Width(g, start, i + 1) * size;
            }
        }

        if (lines.Count < maxLines && start <= g.Count && (start < g.Count || lines.Count == 0))
        {
            lines.Add(new TextLine(start, g.Count, Width(g, start, g.Count)));
        }

        return lines;
    }

    /// <summary>Writes the glyphs of one line at (x, y), switching fonts where the chain does; <paramref name="current"/> tracks the font set by the last <c>Tf</c>.</summary>
    public static void EmitLine(ContentBuilder cb, FontChain chain, List<ShapedGlyph> g, int start, int end, double x, double y, double size, ref int current, HashSet<int> used)
    {
        cb.Op("Tm", 1, 0, 0, 1, x, y);
        Span<byte> buf = stackalloc byte[256];
        int i = start;
        while (i < end)
        {
            int font = g[i].Font;
            int n = 0;
            while (i < end && g[i].Font == font && n < buf.Length)
            {
                buf[n++] = g[i].Code;
                i++;
            }

            if (font != current)
            {
                cb.Raw("/" + chain.Alias(font) + " " + ContentBuilder.Num(size) + " Tf\n");
                current = font;
                used.Add(font);
            }

            cb.Literal(buf[..n]).Raw(" Tj\n");
        }
    }
}
