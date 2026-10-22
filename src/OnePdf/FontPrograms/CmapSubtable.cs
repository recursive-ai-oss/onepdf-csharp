namespace OnePdf.FontPrograms;

/// <summary>
/// One <c>cmap</c> subtable of an sfnt font, read in place: formats 0, 4, 6, 10, 12 and 13. PDF picks subtables by platform and
/// encoding (ISO 32000-1 9.6.6.4), so every subtable is kept and addressable instead of choosing a "best" one.
/// </summary>
internal sealed class CmapSubtable
{
    private const int MaxEnumerated = 1 << 21;

    private readonly ReadOnlyMemory<byte> _data;

    public CmapSubtable(int platform, int encoding, int format, ReadOnlyMemory<byte> data)
    {
        Platform = platform;
        Encoding = encoding;
        Format = format;
        _data = data;
    }

    public int Platform { get; }

    public int Encoding { get; }

    public int Format { get; }

    /// <summary>Reads the subtable directory. Subtables with unsupported formats or offsets outside the table are left out.</summary>
    public static List<CmapSubtable> ReadAll(ReadOnlyMemory<byte> table)
    {
        var list = new List<CmapSubtable>();
        ReadOnlySpan<byte> d = table.Span;
        if (d.Length < 4)
        {
            return list;
        }

        int n = Be.U16(d, 2);
        n = Math.Min(n, (d.Length - 4) / 8);
        for (int i = 0; i < n && list.Count < 64; i++)
        {
            int platform = Be.U16(d, 4 + (8 * i));
            int encoding = Be.U16(d, 6 + (8 * i));
            uint off = Be.U32(d, 8 + (8 * i));
            if (off > d.Length - 4U)
            {
                continue;
            }

            int format = Be.U16(d, (int)off);
            if (format is 0 or 4 or 6 or 10 or 12 or 13)
            {
                list.Add(new CmapSubtable(platform, encoding, format, table[(int)off..]));
            }
        }

        return list;
    }

    /// <summary>The glyph for a character code, or 0 when the subtable has none.</summary>
    public int Lookup(int code)
    {
        if (code < 0)
        {
            return 0;
        }

        ReadOnlySpan<byte> d = _data.Span;
        try
        {
            return Format switch
            {
                0 => code < 256 ? Be.U8(d, 6 + code) : 0,
                4 => Format4(d, code),
                6 => Format6(d, code),
                10 => Format10(d, code),
                12 or 13 => Format12(d, code, Format == 13),
                _ => 0,
            };
        }
        catch (FontProgramException)
        {
            // A subtable cut short by subsetting: codes beyond the cut are unmapped.
            return 0;
        }
    }

    /// <summary>
    /// Lookup for symbolic fonts with a (3,0) subtable: the code itself, then the same code in the private-use blocks starting at
    /// U+F000, U+F100 and U+F200 where Microsoft symbol fonts keep single-byte characters (ISO 32000-1 9.6.6.4).
    /// </summary>
    public int LookupSymbolic(int code)
    {
        int g = Lookup(code);
        if (g == 0 && code < 0x100)
        {
            g = Lookup(0xF000 + code);
            if (g == 0)
            {
                g = Lookup(0xF100 + code);
            }

            if (g == 0)
            {
                g = Lookup(0xF200 + code);
            }
        }

        return g;
    }

    /// <summary>Lists (code, glyph) pairs with glyph above 0, up to a bound of about two million entries.</summary>
    public List<(int Code, int Glyph)> Enumerate()
    {
        var result = new List<(int, int)>();
        ReadOnlySpan<byte> d = _data.Span;
        try
        {
            switch (Format)
            {
                case 0:
                    for (int c = 0; c < 256; c++)
                    {
                        int g = Be.U8(d, 6 + c);
                        if (g != 0)
                        {
                            result.Add((c, g));
                        }
                    }

                    break;
                case 4:
                    EnumerateFormat4(d, result);
                    break;
                case 6:
                {
                    int first = Be.U16(d, 6);
                    int count = Math.Min(Be.U16(d, 8), (d.Length - 10) / 2);
                    for (int i = 0; i < count; i++)
                    {
                        int g = Be.U16(d, 10 + (2 * i));
                        if (g != 0)
                        {
                            result.Add((first + i, g));
                        }
                    }

                    break;
                }

                case 10:
                {
                    uint first = Be.U32(d, 12);
                    long count = Math.Min(Be.U32(d, 16), (d.Length - 20L) / 2);
                    for (int i = 0; i < count; i++)
                    {
                        int g = Be.U16(d, 20 + (2 * i));
                        if (g != 0)
                        {
                            result.Add(((int)(first + i), g));
                        }
                    }

                    break;
                }

                case 12:
                case 13:
                {
                    long n = Math.Min(Be.U32(d, 12), (d.Length - 16L) / 12);
                    long budget = 1 << 23;
                    for (int i = 0; i < n; i++)
                    {
                        uint s = Be.U32(d, 16 + (12 * i));
                        uint e = Be.U32(d, 20 + (12 * i));
                        uint g = Be.U32(d, 24 + (12 * i));
                        for (long c = s; c <= e && c <= 0x10FFFF; c++)
                        {
                            if (--budget < 0)
                            {
                                return result;
                            }

                            long gg = Format == 13 ? g : g + (c - s);
                            if (gg > 0 && gg <= 0xFFFF)
                            {
                                result.Add(((int)c, (int)gg));
                            }

                            if (result.Count >= MaxEnumerated)
                            {
                                return result;
                            }
                        }
                    }

                    break;
                }
            }
        }
        catch (FontProgramException)
        {
            // Return what was read before the damage.
        }

        return result;
    }

    private static void EnumerateFormat4(ReadOnlySpan<byte> d, List<(int, int)> result)
    {
        int segX2 = Be.U16(d, 6);
        int seg = segX2 / 2;
        int endPos = 14;
        int startPos = endPos + segX2 + 2;
        int budget = 1 << 22; // overlapping segments in a hostile table must not multiply the work
        for (int s = 0; s < seg; s++)
        {
            int end = Be.U16(d, endPos + (2 * s));
            int start = Be.U16(d, startPos + (2 * s));
            for (int c = start; c <= end && c < 0xFFFF; c++)
            {
                if (--budget < 0)
                {
                    return;
                }

                int g = Format4Segment(d, segX2, s, c);
                if (g != 0)
                {
                    result.Add((c, g));
                }
            }
        }
    }

    private static int Format4(ReadOnlySpan<byte> d, int cp)
    {
        if (cp > 0xFFFF)
        {
            return 0;
        }

        int segX2 = Be.U16(d, 6);
        int seg = segX2 / 2;
        int endPos = 14;
        if (seg == 0)
        {
            return 0;
        }

        int lo = 0, hi = seg - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (Be.U16(d, endPos + (2 * mid)) < cp)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        if (Be.U16(d, endPos + (2 * lo)) < cp)
        {
            return 0;
        }

        int startPos = endPos + segX2 + 2;
        if (Be.U16(d, startPos + (2 * lo)) > cp)
        {
            return 0;
        }

        return Format4Segment(d, segX2, lo, cp);
    }

    private static int Format4Segment(ReadOnlySpan<byte> d, int segX2, int s, int cp)
    {
        int startPos = 14 + segX2 + 2;
        int deltaPos = startPos + segX2;
        int rangePos = deltaPos + segX2;
        int start = Be.U16(d, startPos + (2 * s));
        int delta = Be.U16(d, deltaPos + (2 * s));
        int ro = Be.U16(d, rangePos + (2 * s));
        if (ro == 0)
        {
            return (cp + delta) & 0xFFFF;
        }

        // The range offset is relative to its own position in the idRangeOffset array.
        int g = Be.U16(d, rangePos + (2 * s) + ro + (2 * (cp - start)));
        return g == 0 ? 0 : (g + delta) & 0xFFFF;
    }

    private static int Format6(ReadOnlySpan<byte> d, int cp)
    {
        int first = Be.U16(d, 6);
        int count = Be.U16(d, 8);
        int i = cp - first;
        return i >= 0 && i < count ? Be.U16(d, 10 + (2 * i)) : 0;
    }

    private static int Format10(ReadOnlySpan<byte> d, int cp)
    {
        uint first = Be.U32(d, 12);
        uint count = Be.U32(d, 16);
        long i = cp - (long)first;
        return i >= 0 && i < count ? Be.U16(d, 20 + (int)(2 * i)) : 0;
    }

    private static int Format12(ReadOnlySpan<byte> d, int cp, bool constant)
    {
        uint n = Be.U32(d, 12);
        n = (uint)Math.Min(n, (d.Length - 16L) / 12);
        int lo = 0, hi = (int)n - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int p = 16 + (12 * mid);
            uint s = Be.U32(d, p);
            uint e = Be.U32(d, p + 4);
            if (cp < s)
            {
                hi = mid - 1;
            }
            else if (cp > e)
            {
                lo = mid + 1;
            }
            else
            {
                uint g = Be.U32(d, p + 8);
                long r = constant ? g : g + (cp - (long)s);
                return r is > 0 and <= 0xFFFF ? (int)r : 0;
            }
        }

        return 0;
    }
}
