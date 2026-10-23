using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using OnePdf.FontWriting;

namespace OnePdf.Tests.Creation;

/// <summary>Builds tiny TrueType fonts in memory so the subsetter's edge cases run without any font file.</summary>
internal static class SyntheticFont
{
    public sealed class Options
    {
        /// <summary>Glyph data by glyph index; a glyph is raw bytes as stored in 'glyf'.</summary>
        public List<byte[]> Glyphs { get; set; } = StandardGlyphs();

        public int? MaxpGlyphsOverride { get; set; }

        public int FsType { get; set; }

        public bool IncludeOs2 { get; set; } = true;

        /// <summary>Unicode code points mapped to glyphs 1.. in order ('A' = glyph 1, 'B' = glyph 2, ...).</summary>
        public string Characters { get; set; } = "ABCD";

        public Func<uint[], uint[]>? LocaEdit { get; set; }
    }

    public static byte[] Simple(short dx = 0)
    {
        // One contour, three on-curve points: (0+dx,0) (500+dx,0) (250+dx,700).
        var b = new List<byte>();
        Add16(b, 1);
        Add16(b, dx);
        Add16(b, 0);
        Add16(b, 500 + dx);
        Add16(b, 700);
        Add16(b, 2); // endPtsOfContours
        Add16(b, 0); // instructionLength
        b.AddRange([1, 1, 1]);
        Add16(b, dx);
        Add16(b, 500);
        Add16(b, -250);
        Add16(b, 0);
        Add16(b, 0);
        Add16(b, 700);
        while (b.Count % 4 != 0)
        {
            b.Add(0);
        }

        return [.. b];
    }

    public static byte[] Composite(int component, short dx, short dy, params (int Glyph, short Dx, short Dy)[] more)
    {
        var b = new List<byte>();
        Add16(b, -1);
        Add16(b, 0);
        Add16(b, 0);
        Add16(b, 1000);
        Add16(b, 1000);
        var all = new List<(int, short, short)> { (component, dx, dy) };
        foreach ((int g, short x, short y) in more)
        {
            all.Add((g, x, y));
        }

        for (int i = 0; i < all.Count; i++)
        {
            Add16(b, 0x0003 | (i < all.Count - 1 ? 0x0020 : 0));
            Add16(b, all[i].Item1);
            Add16(b, all[i].Item2);
            Add16(b, all[i].Item3);
        }

        while (b.Count % 4 != 0)
        {
            b.Add(0);
        }

        return [.. b];
    }

    public static List<byte[]> StandardGlyphs() =>
    [
        [],                          // 0 .notdef
        Simple(0),                   // 1 A
        Simple(100),                 // 2 B
        Composite(1, 0, 0, (2, 600, 0)), // 3 C = A + B moved
        Composite(3, 0, 50),         // 4 D = C moved (nested)
    ];

    public static byte[] Build(Options? options = null)
    {
        options ??= new Options();
        List<byte[]> glyphs = options.Glyphs;
        int n = glyphs.Count;
        var glyf = new List<byte>();
        var loca = new uint[n + 1];
        for (int i = 0; i < n; i++)
        {
            loca[i] = (uint)glyf.Count;
            glyf.AddRange(glyphs[i]);
        }

        loca[n] = (uint)glyf.Count;
        if (options.LocaEdit is not null)
        {
            loca = options.LocaEdit(loca);
        }

        byte[] locaBytes = new byte[loca.Length * 4];
        for (int i = 0; i < loca.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(locaBytes.AsSpan(4 * i), loca[i]);
        }

        byte[] head = new byte[54];
        BinaryPrimitives.WriteUInt32BigEndian(head, 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(12), 0x5F0F3CF5);
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(18), 1000);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(36), 0);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(38), -200);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(40), 1000);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(42), 800);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1);
        byte[] hhea = new byte[36];
        BinaryPrimitives.WriteUInt32BigEndian(hhea, 0x00010000);
        BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(4), 800);
        BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(6), -200);
        BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34), (ushort)n);
        byte[] maxp = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(maxp, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), (ushort)(options.MaxpGlyphsOverride ?? n));
        byte[] hmtx = new byte[4 * n];
        for (int i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(4 * i), (ushort)(500 + (i * 10)));
        }

        // cmap: platform 0 (Unicode), format 0 covers code points below 256.
        byte[] sub = new byte[6 + 256];
        BinaryPrimitives.WriteUInt16BigEndian(sub.AsSpan(2), (ushort)sub.Length);
        for (int i = 0; i < options.Characters.Length && i + 1 < n; i++)
        {
            sub[6 + options.Characters[i]] = (byte)(i + 1);
        }

        byte[] cmap = new byte[12 + sub.Length];
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(4), 0);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(6), 3);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(8), 12);
        sub.CopyTo(cmap.AsSpan(12));

        var tables = new SortedDictionary<uint, byte[]>
        {
            [Tag("cmap")] = cmap,
            [Tag("glyf")] = [.. glyf],
            [Tag("head")] = head,
            [Tag("hhea")] = hhea,
            [Tag("hmtx")] = hmtx,
            [Tag("loca")] = locaBytes,
            [Tag("maxp")] = maxp,
        };
        if (options.IncludeOs2)
        {
            byte[] os2 = new byte[78];
            BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(8), (ushort)options.FsType);
            tables[Tag("OS/2")] = os2;
        }

        return SfntWriter.Assemble(0x00010000, tables, Tag("head"));
    }

    private static uint Tag(string s) => ((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | s[3];

    private static void Add16(List<byte> b, int v)
    {
        b.Add((byte)(v >> 8));
        b.Add((byte)v);
    }
}
