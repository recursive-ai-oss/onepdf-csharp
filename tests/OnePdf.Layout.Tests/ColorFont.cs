using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace OnePdf.Layout.Tests;

/// <summary>
/// Builds a tiny TrueType colour font from scratch (our own data, no third-party font): flat shapes as glyphs, a COLR version 0 table that
/// stacks them into colour glyphs and a CPAL table with two palettes. Used by the colour glyph tests and by the layout reference documents.
/// </summary>
/// <remarks>
/// Glyphs: 1 = <c>A</c> (a plain triangle), 2 = square, 3 = triangle, 4 = diamond (the shapes the layers use), 5 = U+1F600 (layers 2, 3 and 4:
/// palette entry 0, text colour, palette entry 1), 6 = U+1F601 (layers 4 then 2, palette entries 2 and 0), 7 = U+1F602 (only described in
/// COLR version 1), 8 = space. Palette 0 (tagged for light backgrounds): red, green, blue. Palette 1 (tagged for dark backgrounds): orange, cyan, yellow.
/// </remarks>
internal static class ColorFont
{
    public const string Grin = "\U0001F600";
    public const string Beam = "\U0001F601";
    public const string Joy = "\U0001F602";

    public const int GlyphCount = 9;

    /// <summary>The raw tables, so a test can break them before the file is assembled.</summary>
    public sealed class Parts
    {
        public byte[] Colr { get; set; } = [];

        public byte[] Cpal { get; set; } = [];

        public bool OmitCpal { get; set; }

        public bool OmitColr { get; set; }

        /// <summary>More cmap entries (code point, glyph), for fonts that map characters to glyphs the font does not have.</summary>
        public List<(uint Code, int Glyph)> ExtraMap { get; } = [];
    }

    public static byte[] Build(bool version1 = false, Action<Parts>? edit = null)
    {
        var parts = new Parts { Colr = BuildColr(version1), Cpal = BuildCpal() };
        edit?.Invoke(parts);

        List<byte[]> glyphs =
        [
            [],
            Polygon((50, 0), (950, 0), (500, 800)),
            Polygon((100, 0), (900, 0), (900, 700), (100, 700)),
            Polygon((300, 0), (900, 0), (600, 800)),
            Polygon((500, 0), (900, 400), (500, 800), (100, 400)),
            Polygon((50, 0), (950, 0), (950, 800), (50, 800)),
            Polygon((50, 0), (950, 0), (950, 800), (50, 800)),
            Polygon((50, 0), (950, 0), (950, 800), (50, 800)),
            [],
        ];

        var glyf = new List<byte>();
        var loca = new byte[(glyphs.Count + 1) * 4];
        for (int i = 0; i < glyphs.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(4 * i), (uint)glyf.Count);
            glyf.AddRange(glyphs[i]);
        }

        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(4 * glyphs.Count), (uint)glyf.Count);

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
        BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34), (ushort)glyphs.Count);
        byte[] maxp = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(maxp, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), (ushort)glyphs.Count);
        byte[] hmtx = new byte[4 * glyphs.Count];
        for (int i = 0; i < glyphs.Count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(4 * i), (ushort)(i == 8 ? 300 : 1000));
        }

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["cmap"] = BuildCmap(parts.ExtraMap),
            ["glyf"] = [.. glyf],
            ["head"] = head,
            ["hhea"] = hhea,
            ["hmtx"] = hmtx,
            ["loca"] = loca,
            ["maxp"] = maxp,
            ["name"] = BuildName(),
            ["OS/2"] = new byte[78],
            ["post"] = [0, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        };
        if (!parts.OmitColr)
        {
            tables["COLR"] = parts.Colr;
        }

        if (!parts.OmitCpal)
        {
            tables["CPAL"] = parts.Cpal;
        }

        return Assemble(tables);
    }

    public static byte[] BuildColr(bool version1)
    {
        // Base glyphs in increasing order: 5 -> layers 0..2, 6 -> layers 3..4.
        int headerLength = version1 ? 34 : 14;
        int baseOffset = headerLength;
        int layerOffset = baseOffset + 12;
        int listOffset = layerOffset + 20;
        byte[] t = new byte[listOffset + (version1 ? 4 + 6 + 8 : 0)];
        BinaryPrimitives.WriteUInt16BigEndian(t, (ushort)(version1 ? 1 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(4), (uint)baseOffset);
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(8), (uint)layerOffset);
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(12), 5);
        Put(t, baseOffset, 5, 0, 3);
        Put(t, baseOffset + 6, 6, 3, 2);
        Put(t, layerOffset, 2, 0);
        Put(t, layerOffset + 4, 3, 0xFFFF);
        Put(t, layerOffset + 8, 4, 1);
        Put(t, layerOffset + 12, 4, 2);
        Put(t, layerOffset + 16, 2, 0);
        if (version1)
        {
            BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(14), (uint)listOffset);
            BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(listOffset), 1);
            BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(listOffset + 4), 7);
            BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(listOffset + 6), 10);
        }

        return t;
    }

    private static void Put(byte[] t, int at, params int[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(at + (2 * i)), (ushort)values[i]);
        }
    }

    public static byte[] BuildCpal()
    {
        // Version 1: two palettes of three entries; types say palette 0 suits light and palette 1 dark backgrounds.
        int header = 12 + 4 + 12;
        int colors = header;
        int types = colors + (6 * 4);
        byte[] t = new byte[types + 8];
        Put(t, 0, 1, 3, 2, 6);
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(8), (uint)colors);
        Put(t, 12, 0, 3);
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(16), (uint)types);
        (byte R, byte G, byte B)[] rgb = [(230, 30, 30), (30, 160, 60), (30, 60, 220), (255, 140, 0), (0, 200, 220), (250, 220, 0)];
        for (int i = 0; i < rgb.Length; i++)
        {
            t[colors + (4 * i)] = rgb[i].B;
            t[colors + (4 * i) + 1] = rgb[i].G;
            t[colors + (4 * i) + 2] = rgb[i].R;
            t[colors + (4 * i) + 3] = 255;
        }

        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(types), 1);
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(types + 4), 2);
        return t;
    }

    private static byte[] BuildCmap(List<(uint Code, int Glyph)> extra)
    {
        (uint Code, int Glyph)[] map = [(0x20, 8), (0x41, 1), .. extra.OrderBy(e => e.Code).Where(e => e.Code is > 0x41 and < 0x1F600), (0x1F600, 5), (0x1F601, 6), (0x1F602, 7)];
        byte[] sub = new byte[16 + (12 * map.Length)];
        BinaryPrimitives.WriteUInt16BigEndian(sub, 12);
        BinaryPrimitives.WriteUInt32BigEndian(sub.AsSpan(4), (uint)sub.Length);
        BinaryPrimitives.WriteUInt32BigEndian(sub.AsSpan(12), (uint)map.Length);
        for (int i = 0; i < map.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(sub.AsSpan(16 + (12 * i)), map[i].Code);
            BinaryPrimitives.WriteUInt32BigEndian(sub.AsSpan(20 + (12 * i)), map[i].Code);
            BinaryPrimitives.WriteUInt32BigEndian(sub.AsSpan(24 + (12 * i)), (uint)map[i].Glyph);
        }

        byte[] cmap = new byte[4 + 16 + sub.Length];
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(4), 0);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(6), 4);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(8), 20);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(14), 10);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(16), 20);
        sub.CopyTo(cmap.AsSpan(20));
        return cmap;
    }

    private static byte[] BuildName()
    {
        int[] ids = [1, 2, 4, 6];
        string[] values = ["OnePdf Colour Test", "Regular", "OnePdf Colour Test", "OnePdfColourTest"];
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

    // One closed contour of straight lines, as a simple TrueType glyph with 16-bit coordinates.
    private static byte[] Polygon(params (int X, int Y)[] points)
    {
        var b = new List<byte>();
        void W(int v)
        {
            b.Add((byte)(v >> 8));
            b.Add((byte)v);
        }

        W(1);
        W(points.Min(p => p.X));
        W(points.Min(p => p.Y));
        W(points.Max(p => p.X));
        W(points.Max(p => p.Y));
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
