using System.Text;

namespace OnePdf.Tests.PdfFonts;

/// <summary>Builds a minimal valid TrueType font: square glyphs, chosen cmap subtables, optional post names.</summary>
internal static class TinyTrueType
{
    /// <summary>One cmap subtable: platform, encoding, and code to glyph pairs.</summary>
    public sealed record Cmap(int Platform, int Encoding, Dictionary<int, int> Map);

    /// <summary>
    /// A font with <paramref name="glyphCount"/> glyphs (0 is .notdef and empty; the rest are 500-unit squares), all advancing
    /// <paramref name="advance"/>, with the given cmap subtables and, when <paramref name="names"/> is not null, a format 2 post table.
    /// </summary>
    public static byte[] Build(int glyphCount, IReadOnlyList<Cmap> cmaps, string[]? names = null, int advance = 600)
    {
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);

        byte[] head = new byte[54];
        W16(head, 18, 1000);
        W16(head, 50, 0);
        tables["head"] = head;

        byte[] maxp = new byte[6];
        W32(maxp, 0, 0x00010000);
        W16(maxp, 4, glyphCount);
        tables["maxp"] = maxp;

        byte[] hhea = new byte[36];
        W32(hhea, 0, 0x00010000);
        W16(hhea, 34, glyphCount);
        tables["hhea"] = hhea;

        byte[] hmtx = new byte[4 * glyphCount];
        for (int g = 0; g < glyphCount; g++)
        {
            W16(hmtx, 4 * g, advance);
        }

        tables["hmtx"] = hmtx;

        // glyf: glyph 0 empty, others a square. loca uses short offsets (value / 2).
        var glyf = new List<byte>();
        byte[] loca = new byte[2 * (glyphCount + 1)];
        byte[] square = Square();
        for (int g = 0; g < glyphCount; g++)
        {
            W16(loca, 2 * g, glyf.Count / 2);
            if (g > 0)
            {
                glyf.AddRange(square);
            }
        }

        W16(loca, 2 * glyphCount, glyf.Count / 2);
        tables["glyf"] = [.. glyf];
        tables["loca"] = loca;

        tables["cmap"] = CmapTable(cmaps);
        if (names is not null)
        {
            tables["post"] = PostTable(glyphCount, names);
        }

        return Assemble(tables);
    }

    private static byte[] Square()
    {
        // One contour of four on-curve points: (0,0) (500,0) (500,500) (0,500), 16-bit coordinates.
        var b = new List<byte>();
        Add16(b, 1);
        Add16(b, 0);
        Add16(b, 0);
        Add16(b, 500);
        Add16(b, 500);
        Add16(b, 3);
        Add16(b, 0);
        b.AddRange([1, 1, 1, 1]);
        foreach (int dx in new[] { 0, 500, 0, -500 })
        {
            Add16(b, dx);
        }

        foreach (int dy in new[] { 0, 0, 500, 0 })
        {
            Add16(b, dy);
        }

        while (b.Count % 4 != 0)
        {
            b.Add(0);
        }

        return [.. b];
    }

    private static byte[] CmapTable(IReadOnlyList<Cmap> cmaps)
    {
        var subtables = new List<byte[]>();
        foreach (Cmap c in cmaps)
        {
            subtables.Add(c.Platform == 1 ? Format0(c.Map) : Format4(c.Map));
        }

        var b = new List<byte>();
        Add16(b, 0);
        Add16(b, cmaps.Count);
        int offset = 4 + (8 * cmaps.Count);
        for (int i = 0; i < cmaps.Count; i++)
        {
            Add16(b, cmaps[i].Platform);
            Add16(b, cmaps[i].Encoding);
            Add32(b, offset);
            offset += subtables[i].Length;
        }

        foreach (byte[] s in subtables)
        {
            b.AddRange(s);
        }

        return [.. b];
    }

    private static byte[] Format0(Dictionary<int, int> map)
    {
        byte[] t = new byte[262];
        W16(t, 0, 0);
        W16(t, 2, 262);
        foreach ((int code, int gid) in map)
        {
            if (code is >= 0 and < 256)
            {
                t[6 + code] = (byte)gid;
            }
        }

        return t;
    }

    private static byte[] Format4(Dictionary<int, int> map)
    {
        int[] codes = [.. map.Keys.Order()];
        int segs = codes.Length + 1;
        var b = new List<byte>();
        Add16(b, 4);
        Add16(b, 16 + (8 * segs));
        Add16(b, 0);
        Add16(b, segs * 2);
        int pow = 1, log = 0;
        while (pow * 2 <= segs)
        {
            pow *= 2;
            log++;
        }

        Add16(b, pow * 2);
        Add16(b, log);
        Add16(b, (segs * 2) - (pow * 2));
        foreach (int c in codes)
        {
            Add16(b, c);
        }

        Add16(b, 0xFFFF);
        Add16(b, 0);
        foreach (int c in codes)
        {
            Add16(b, c);
        }

        Add16(b, 0xFFFF);
        foreach (int c in codes)
        {
            Add16(b, (map[c] - c) & 0xFFFF);
        }

        Add16(b, 1);
        for (int i = 0; i < segs; i++)
        {
            Add16(b, 0);
        }

        return [.. b];
    }

    private static byte[] PostTable(int glyphCount, string[] names)
    {
        var b = new List<byte>();
        Add32(b, 0x00020000);
        for (int i = 0; i < 28; i++)
        {
            b.Add(0);
        }

        Add16(b, glyphCount);
        var strings = new List<byte>();
        for (int g = 0; g < glyphCount; g++)
        {
            // Glyph 0 uses standard name 0 (.notdef); the others use custom names from index 258.
            if (g == 0)
            {
                Add16(b, 0);
                continue;
            }

            Add16(b, 258 + g - 1);
            byte[] n = Encoding.ASCII.GetBytes(names[g - 1]);
            strings.Add((byte)n.Length);
            strings.AddRange(n);
        }

        b.AddRange(strings);
        return [.. b];
    }

    private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        var b = new List<byte>();
        Add32(b, 0x00010000);
        Add16(b, tables.Count);
        Add16(b, 0);
        Add16(b, 0);
        Add16(b, 0);
        int offset = 12 + (16 * tables.Count);
        foreach ((string tag, byte[] data) in tables)
        {
            b.AddRange(Encoding.ASCII.GetBytes(tag));
            Add32(b, 0);
            Add32(b, offset);
            Add32(b, data.Length);
            offset += (data.Length + 3) & ~3;
        }

        foreach (byte[] data in tables.Values)
        {
            b.AddRange(data);
            while (b.Count % 4 != 0)
            {
                b.Add(0);
            }
        }

        return [.. b];
    }

    private static void Add16(List<byte> b, int v)
    {
        b.Add((byte)(v >> 8));
        b.Add((byte)v);
    }

    private static void Add32(List<byte> b, int v)
    {
        Add16(b, v >> 16);
        Add16(b, v & 0xFFFF);
    }

    private static void W16(byte[] a, int at, int v)
    {
        a[at] = (byte)(v >> 8);
        a[at + 1] = (byte)v;
    }

    private static void W32(byte[] a, int at, uint v)
    {
        W16(a, at, (int)(v >> 16));
        W16(a, at + 2, (int)(v & 0xFFFF));
    }
}
