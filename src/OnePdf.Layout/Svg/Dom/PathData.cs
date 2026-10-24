using System.Globalization;

namespace OnePdf.Layout.Svg.Dom;

/// <summary>
/// Parses path data (<c>d</c>, SVG 1.1 section 8.3 and its error handling): the path is rendered up to the first error, so a malformed
/// tail leaves the segments before it. Data that does not start with a move yields an empty path.
/// </summary>
internal static class PathData
{
    /// <summary>Parses <paramref name="data"/>; never throws on malformed data. Returns whether the data was well formed to the end.</summary>
    public static bool Parse(ReadOnlySpan<char> data, out SvgPath path) => Parse(data, out path, out _);

    /// <summary>
    /// Parses <paramref name="data"/>. <paramref name="dotted"/> is the same path with every open subpath of zero length (all its points
    /// equal) closed, or <see langword="null"/> when there is none: SVG draws such a subpath as a dot under round and square caps,
    /// which the stroker does for a closed figure.
    /// </summary>
    public static bool Parse(ReadOnlySpan<char> data, out SvgPath path, out SvgPath? dotted) => Parse(data, out path, out dotted, null);

    /// <summary>As the other overload, and records the segments into <paramref name="markers"/> (when given) for marker placement.</summary>
    public static bool Parse(ReadOnlySpan<char> data, out SvgPath path, out SvgPath? dotted, MarkerPath? markers, PathBudget? budget = null)
    {
        var b = new Dual(markers, budget);
        int i = 0;
        char cmd = '\0';
        bool first = true;
        double cx = 0, cy = 0, sx = 0, sy = 0;
        double lastCx = 0, lastCy = 0;
        char prev = '\0';
        bool ok = true;
        Skip(data, ref i);
        while (i < data.Length)
        {
            char c = data[i];
            if (IsCommand(c))
            {
                cmd = c;
                i++;
            }
            else if (cmd == '\0' || cmd is 'Z' or 'z')
            {
                ok = false;
                break;
            }

            if (first && cmd is not ('M' or 'm'))
            {
                ok = false;
                break;
            }

            first = false;
            bool rel = char.IsAsciiLetterLower(cmd);
            double ox = rel ? cx : 0;
            double oy = rel ? cy : 0;
            char up = char.ToUpperInvariant(cmd);
            double x = 0, y = 0, x1 = 0, y1 = 0, x2 = 0, y2 = 0;
            bool large = false, sweep = false;
            bool good = true;
            switch (up)
            {
                case 'Z':
                    b.Close();
                    cx = sx;
                    cy = sy;
                    break;
                case 'M':
                    good = Num(data, ref i, out x) && Num(data, ref i, out y);
                    if (good)
                    {
                        cx = sx = x + ox;
                        cy = sy = y + oy;
                        b.MoveTo(Coord.D(cx), Coord.D(cy));
                        cmd = rel ? 'l' : 'L';
                    }

                    break;
                case 'L':
                    good = Num(data, ref i, out x) && Num(data, ref i, out y);
                    if (good)
                    {
                        cx = x + ox;
                        cy = y + oy;
                        b.LineTo(Coord.D(cx), Coord.D(cy));
                    }

                    break;
                case 'H':
                    good = Num(data, ref i, out x);
                    if (good)
                    {
                        cx = x + ox;
                        b.LineTo(Coord.D(cx), Coord.D(cy));
                    }

                    break;
                case 'V':
                    good = Num(data, ref i, out y);
                    if (good)
                    {
                        cy = y + oy;
                        b.LineTo(Coord.D(cx), Coord.D(cy));
                    }

                    break;
                case 'C':
                case 'S':
                {
                    x1 = y1 = 0;
                    if (up == 'C')
                    {
                        good = Num(data, ref i, out x1) && Num(data, ref i, out y1);
                        x1 += ox;
                        y1 += oy;
                    }
                    else
                    {
                        bool smooth = prev is 'C' or 'S';
                        x1 = smooth ? (2 * cx) - lastCx : cx;
                        y1 = smooth ? (2 * cy) - lastCy : cy;
                    }

                    good = good && Num(data, ref i, out x2) && Num(data, ref i, out y2) && Num(data, ref i, out x) && Num(data, ref i, out y);
                    if (good)
                    {
                        x2 += ox;
                        y2 += oy;
                        x += ox;
                        y += oy;
                        b.CubicBezierTo(Coord.D(x1), Coord.D(y1), Coord.D(x2), Coord.D(y2), Coord.D(x), Coord.D(y));
                        lastCx = x2;
                        lastCy = y2;
                        cx = x;
                        cy = y;
                    }

                    break;
                }

                case 'Q':
                case 'T':
                {
                    x1 = y1 = 0;
                    if (up == 'Q')
                    {
                        good = Num(data, ref i, out x1) && Num(data, ref i, out y1);
                        x1 += ox;
                        y1 += oy;
                    }
                    else
                    {
                        bool smooth = prev is 'Q' or 'T';
                        x1 = smooth ? (2 * cx) - lastCx : cx;
                        y1 = smooth ? (2 * cy) - lastCy : cy;
                    }

                    good = good && Num(data, ref i, out x) && Num(data, ref i, out y);
                    if (good)
                    {
                        x += ox;
                        y += oy;
                        b.QuadraticBezierTo(Coord.D(x1), Coord.D(y1), Coord.D(x), Coord.D(y));
                        lastCx = x1;
                        lastCy = y1;
                        cx = x;
                        cy = y;
                    }

                    break;
                }

                case 'A':
                {
                    good = Num(data, ref i, out x1) && Num(data, ref i, out y1) && Num(data, ref i, out x2)
                        && Flag(data, ref i, out large) && Flag(data, ref i, out sweep) && Num(data, ref i, out x) && Num(data, ref i, out y);
                    if (good)
                    {
                        x += ox;
                        y += oy;
                        b.ArcTo(Coord.D(x1), Coord.D(y1), Coord.D(x2), large, sweep, Coord.D(x), Coord.D(y));
                        cx = x;
                        cy = y;
                    }

                    break;
                }

                default:
                    good = false;
                    break;
            }

            if (!good)
            {
                ok = false;
                break;
            }

            prev = up;
            Skip(data, ref i);
        }

        path = b.Build(out dotted);
        return ok;
    }

    // Builds the path and, beside it, the variant in which zero-length open subpaths are closed.
    /// <summary>How many more path segments a document may spend; shared by every path parsed for it.</summary>
    internal sealed class PathBudget(long segments)
    {
        private long _left = segments;
        private readonly long _total = segments;

        /// <summary>Spends <paramref name="count"/> segments, failing with <see cref="PdfLimitException"/> once the budget is gone.</summary>
        public void Spend(long count)
        {
            if ((_left -= count) < 0)
            {
                throw new PdfLimitException($"The SVG has more than {_total} path segments.");
            }
        }
    }

    private sealed class Dual
    {
        private readonly PathBudget? _budget;
        private readonly SvgPath _main = new();
        private readonly MarkerPath? _rec;

        public Dual(MarkerPath? rec, PathBudget? budget)
        {
            _rec = rec;
            _budget = budget;
        }

        public void MoveTo(double x, double y)
        {
            _budget?.Spend(1);
            _rec?.MoveTo(x, y);
            _main.MoveTo(x, y);
        }

        public void LineTo(double x, double y)
        {
            _budget?.Spend(1);
            _rec?.LineTo(x, y);
            _main.LineTo(x, y);
        }

        public void QuadraticBezierTo(double cx, double cy, double x, double y)
        {
            _budget?.Spend(1);
            _rec?.QuadTo(cx, cy, x, y);
            _main.QuadTo(cx, cy, x, y);
        }

        public void CubicBezierTo(double c1x, double c1y, double c2x, double c2y, double x, double y)
        {
            _budget?.Spend(1);
            _rec?.CubicTo(c1x, c1y, c2x, c2y, x, y);
            _main.CubicTo(c1x, c1y, c2x, c2y, x, y);
        }

        public void ArcTo(double rx, double ry, double rotation, bool large, bool sweep, double x, double y)
        {
            _budget?.Spend(1);
            _rec?.ArcTo(rx, ry, rotation, large, sweep, x, y);
            _main.ArcTo(rx, ry, rotation, large, sweep, x, y);
        }

        public void Close()
        {
            _rec?.Close();
            _main.Close();
        }

        public SvgPath Build(out SvgPath? dotted)
        {
            dotted = null;
            return _main;
        }
    }

    private static bool IsCommand(char c) => c is 'M' or 'm' or 'L' or 'l' or 'H' or 'h' or 'V' or 'v' or 'C' or 'c' or 'S' or 's' or 'Q' or 'q' or 'T' or 't' or 'A' or 'a' or 'Z' or 'z';

    private static void Skip(ReadOnlySpan<char> d, ref int i)
    {
        while (i < d.Length && d[i] is ' ' or '\t' or '\n' or '\r' or '\f')
        {
            i++;
        }
    }

    // Separators between arguments: white space with at most one comma.
    private static void Sep(ReadOnlySpan<char> d, ref int i)
    {
        Skip(d, ref i);
        if (i < d.Length && d[i] == ',')
        {
            i++;
            Skip(d, ref i);
        }
    }

    private static bool Num(ReadOnlySpan<char> d, ref int i, out double v)
    {
        v = 0;
        Skip(d, ref i);
        int len = SvgSize.NumberLength(d[i..]);
        if (len == 0 || !double.TryParse(d.Slice(i, len), NumberStyles.Float, CultureInfo.InvariantCulture, out v) || !double.IsFinite(v) || Math.Abs(v) > 3e38)
        {
            return false;
        }

        i += len;
        Sep(d, ref i);
        return true;
    }

    private static bool Flag(ReadOnlySpan<char> d, ref int i, out bool v)
    {
        v = false;
        Skip(d, ref i);
        if (i >= d.Length || d[i] is not ('0' or '1'))
        {
            return false;
        }

        v = d[i] == '1';
        i++;
        Sep(d, ref i);
        return true;
    }
}
