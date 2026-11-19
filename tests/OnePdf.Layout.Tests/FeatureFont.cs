using System.Buffers.Binary;
using System.Text;

namespace OnePdf.Layout.Tests;

/// <summary>
/// Builds a tiny TrueType font from scratch (our own data) that has OpenType features to switch: a <c>liga</c> ligature for "fi", a
/// <c>dlig</c> ligature for "ct", <c>smcp</c> for "a", <c>tnum</c> for "1", and a legacy <c>kern</c> pair for "AV".
/// </summary>
internal static class FeatureFont
{
    public const string Family = "FeatureTest";

    // Glyph ids.
    public const int F = 1, I = 2, FI = 3, C = 4, T = 5, CT = 6, A = 7, V = 8, LowerA = 9, SmallA = 10, One = 11, TabularOne = 12, Space = 13;

    public const int GlyphCount = 14;

    /// <summary>Advance of every glyph, in 1000 units per em.</summary>
    private static readonly int[] Advances = [500, 300, 250, 520, 480, 330, 800, 700, 700, 560, 480, 500, 640, 300];

    public static int Advance(int glyph) => Advances[glyph];

    public static byte[] Build()
    {
        byte[][] glyphs =
        [
            [],
            Box(300), Box(250), Box(520), Box(480), Box(330), Box(800), Box(700), Box(700), Box(560), Box(480), Box(500), Box(640),
            [],
        ];
        var glyf = new List<byte>();
        var loca = new byte[(glyphs.Length + 1) * 4];
        for (int i = 0; i < glyphs.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(4 * i), (uint)glyf.Count);
            glyf.AddRange(glyphs[i]);
        }

        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(4 * glyphs.Length), (uint)glyf.Count);

        byte[] head = new byte[54];
        BinaryPrimitives.WriteUInt32BigEndian(head, 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(12), 0x5F0F3CF5);
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(18), 1000);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(38), -200);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(40), 1000);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(42), 800);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1);
        byte[] hhea = new byte[36];
        BinaryPrimitives.WriteUInt32BigEndian(hhea, 0x00010000);
        BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(4), 800);
        BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(6), -200);
        BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34), GlyphCount);
        byte[] maxp = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(maxp, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), GlyphCount);
        byte[] hmtx = new byte[4 * GlyphCount];
        for (int i = 0; i < GlyphCount; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(4 * i), (ushort)Advances[i]);
        }

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["GSUB"] = BuildGsub(),
            ["cmap"] = BuildCmap(),
            ["glyf"] = [.. glyf],
            ["head"] = head,
            ["hhea"] = hhea,
            ["hmtx"] = hmtx,
            ["kern"] = BuildKern(),
            ["loca"] = loca,
            ["maxp"] = maxp,
            ["name"] = BuildName(),
            ["OS/2"] = new byte[78],
            ["post"] = new byte[32],
        };
        tables["post"][1] = 3;
        return Assemble(tables);
    }

    private static byte[] BuildCmap()
    {
        (int Code, int Glyph)[] map = [('A', A), ('V', V), ('a', LowerA), ('c', C), ('f', F), ('i', I), ('t', T), ('1', One), (' ', Space)];
        Array.Sort(map, (x, y) => x.Code.CompareTo(y.Code));
        int seg = map.Length + 1;
        var t = new List<byte>();
        void W(int v)
        {
            t.Add((byte)(v >> 8));
            t.Add((byte)v);
        }

        W(4);
        W(16 + (8 * seg));
        W(0);
        W(seg * 2);
        int pow = 1;
        int log = 0;
        while (pow * 2 <= seg)
        {
            pow *= 2;
            log++;
        }

        W(pow * 2);
        W(log);
        W((seg * 2) - (pow * 2));
        foreach ((int code, _) in map)
        {
            W(code);
        }

        W(0xFFFF);
        W(0);
        foreach ((int code, _) in map)
        {
            W(code);
        }

        W(0xFFFF);
        foreach ((int code, int glyph) in map)
        {
            W((glyph - code) & 0xFFFF);
        }

        W(1);
        for (int i = 0; i < seg; i++)
        {
            W(0);
        }

        var cmap = new List<byte>();
        cmap.AddRange(new byte[] { 0, 0, 0, 1, 0, 3, 0, 1, 0, 0, 0, 12 });
        cmap.AddRange(t);
        return [.. cmap];
    }

    // Format 0 pairs: A followed by V is pulled in by 150 units.
    private static byte[] BuildKern()
    {
        var t = new List<byte>();
        void W(int v)
        {
            t.Add((byte)(v >> 8));
            t.Add((byte)v);
        }

        W(0);
        W(1);
        W(0);
        W(14 + 6);
        W(0x0001);
        W(1);
        W(6);
        W(0);
        W(0);
        W(A);
        W(V);
        W(-150);
        return [.. t];
    }

    private static byte[] BuildGsub()
    {
        // Features, sorted by tag: dlig, liga, smcp, tnum; lookup n belongs to feature n in that order.
        string[] tags = ["dlig", "liga", "smcp", "tnum"];
        var b = new List<byte>();

        void W(List<byte> l, int v)
        {
            l.Add((byte)(v >> 8));
            l.Add((byte)v);
        }

        // Script list: DFLT with a default language system using all four features.
        var script = new List<byte>();
        W(script, 4);
        W(script, 0);
        W(script, 0);
        W(script, 0xFFFF);
        W(script, 4);
        for (int i = 0; i < 4; i++)
        {
            W(script, i);
        }

        var scriptList = new List<byte>();
        W(scriptList, 1);
        scriptList.AddRange(Encoding.ASCII.GetBytes("DFLT"));
        W(scriptList, 8);
        scriptList.AddRange(script);

        var featureList = new List<byte>();
        W(featureList, 4);
        int featureBase = 2 + (6 * 4);
        for (int i = 0; i < 4; i++)
        {
            featureList.AddRange(Encoding.ASCII.GetBytes(tags[i]));
            W(featureList, featureBase + (6 * i));
        }

        for (int i = 0; i < 4; i++)
        {
            W(featureList, 0);
            W(featureList, 1);
            W(featureList, i);
        }

        // Lookups: 0 dlig c+t -> ct (ligature), 1 liga f+i -> fi (ligature), 2 smcp a -> small a, 3 tnum 1 -> tabular 1 (single).
        byte[] ligCt = LigatureLookup(C, T, CT);
        byte[] ligFi = LigatureLookup(F, I, FI);
        byte[] single2 = SingleLookup(LowerA, SmallA);
        byte[] single3 = SingleLookup(One, TabularOne);
        byte[][] subtables = [ligCt, ligFi, single2, single3];
        int[] types = [4, 4, 1, 1];
        var lookupList = new List<byte>();
        W(lookupList, 4);
        int lookupBase = 2 + (2 * 4);
        int pos = lookupBase;
        var lookups = new List<byte[]>();
        for (int i = 0; i < 4; i++)
        {
            var l = new List<byte>();
            W(l, types[i]);
            W(l, 0);
            W(l, 1);
            W(l, 8);
            l.AddRange(subtables[i]);
            lookups.Add([.. l]);
            W(lookupList, pos);
            pos += l.Count;
        }

        foreach (byte[] l in lookups)
        {
            lookupList.AddRange(l);
        }

        W(b, 1);
        W(b, 0);
        int scriptOffset = 10;
        W(b, scriptOffset);
        W(b, scriptOffset + scriptList.Count);
        W(b, scriptOffset + scriptList.Count + featureList.Count);
        b.AddRange(scriptList);
        b.AddRange(featureList);
        b.AddRange(lookupList);
        return [.. b];
    }

    private static byte[] LigatureLookup(int first, int second, int ligature)
    {
        var l = new List<byte>();
        void W(int v)
        {
            l.Add((byte)(v >> 8));
            l.Add((byte)v);
        }

        // LigatureSubstFormat1: format, coverage offset, set count, set offset; then the set, the ligature and the coverage.
        W(1);
        W(8 + 2 + 2 + 6);
        W(1);
        W(8);
        // Offset 8: LigatureSet with one ligature at offset 4 from the set.
        W(1);
        W(4);
        // Ligature: glyph, component count, components after the first.
        W(ligature);
        W(2);
        W(second);
        // Coverage format 1 with the first glyph.
        W(1);
        W(1);
        W(first);
        return [.. l];
    }

    private static byte[] SingleLookup(int from, int to)
    {
        var l = new List<byte>();
        void W(int v)
        {
            l.Add((byte)(v >> 8));
            l.Add((byte)v);
        }

        // SingleSubstFormat2: format, coverage offset, glyph count, substitutes; coverage format 1 follows.
        W(2);
        W(8);
        W(1);
        W(to);
        W(1);
        W(1);
        W(from);
        return [.. l];
    }

    private static byte[] BuildName()
    {
        int[] ids = [1, 2, 4, 6];
        string[] values = ["OnePdf Feature Test", "Regular", "OnePdf Feature Test", "OnePdfFeatureTest"];
        var strings = new List<byte>();
        byte[] t = new byte[6 + (12 * ids.Length)];
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(2), (ushort)ids.Length);
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(4), (ushort)t.Length);
        for (int i = 0; i < ids.Length; i++)
        {
            byte[] s = Encoding.BigEndianUnicode.GetBytes(values[i]);
            int r = 6 + (12 * i);
            BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(r), 3);
            BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(r + 2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(r + 4), 0x409);
            BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(r + 6), (ushort)ids[i]);
            BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(r + 8), (ushort)s.Length);
            BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(r + 10), (ushort)strings.Count);
            strings.AddRange(s);
        }

        return [.. t, .. strings];
    }

    // A rectangle as a simple glyph, as wide as the advance with a small side bearing.
    private static byte[] Box(int advance)
    {
        int x0 = 40;
        int x1 = advance - 40;
        (int X, int Y)[] points = [(x0, 0), (x1, 0), (x1, 700), (x0, 700)];
        var b = new List<byte>();
        void W(int v)
        {
            b.Add((byte)(v >> 8));
            b.Add((byte)v);
        }

        W(1);
        W(x0);
        W(0);
        W(x1);
        W(700);
        W(points.Length - 1);
        W(0);
        b.AddRange(Enumerable.Repeat((byte)1, points.Length));
        int prev = 0;
        foreach ((int x, _) in points)
        {
            W(x - prev);
            prev = x;
        }

        prev = 0;
        foreach ((_, int y) in points)
        {
            W(y - prev);
            prev = y;
        }

        while (b.Count % 4 != 0)
        {
            b.Add(0);
        }

        return [.. b];
    }

    private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        int n = tables.Count;
        int offset = 12 + (16 * n);
        byte[] header = new byte[offset];
        BinaryPrimitives.WriteUInt32BigEndian(header, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)n);
        int i = 0;
        var body = new List<byte>();
        foreach ((string tag, byte[] data) in tables)
        {
            int r = 12 + (16 * i++);
            Encoding.ASCII.GetBytes(tag).CopyTo(header, r);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(r + 8), (uint)(offset + body.Count));
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(r + 12), (uint)data.Length);
            body.AddRange(data);
            while (body.Count % 4 != 0)
            {
                body.Add(0);
            }
        }

        return [.. header, .. body];
    }
}
