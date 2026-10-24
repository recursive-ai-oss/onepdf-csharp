using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace OnePdf.Tests.Images;

/// <summary>A small PNG encoder for tests: it writes known pixels with every row filter, optionally Adam7-interlaced, so a reader can be checked sample for sample.</summary>
internal static class PngKit
{
    public sealed record Spec(int Width, int Height, int ColorType, int Depth)
    {
        public bool Interlaced { get; init; }

        public byte[]? Palette { get; init; }

        public byte[]? Trns { get; init; }

        public byte[]? Iccp { get; init; }

        public bool BadCrcOnIdat { get; init; }

        public bool SplitIdat { get; init; }

        public int Channels => ColorType switch { 0 or 3 => 1, 4 => 2, 2 => 3, _ => 4 };

        public int BitsPerPixel => Channels * Depth;

        public int Stride => ((Width * BitsPerPixel) + 7) / 8;
    }

    public static byte[] RandomRows(Spec s, int seed)
    {
        var rng = new Random(seed);
        byte[] rows = new byte[s.Stride * s.Height];
        rng.NextBytes(rows);
        int usedBits = s.Width * s.BitsPerPixel;
        int pad = (s.Stride * 8) - usedBits;
        if (pad > 0)
        {
            for (int y = 0; y < s.Height; y++)
            {
                rows[(y * s.Stride) + s.Stride - 1] &= (byte)(0xFF << pad);
            }
        }

        if (s.ColorType == 3)
        {
            int entries = (s.Palette?.Length ?? 3) / 3;
            for (int y = 0; y < s.Height; y++)
            {
                for (int x = 0; x < s.Width; x++)
                {
                    SetBits(rows, (y * s.Stride * 8) + (x * s.Depth), s.Depth, rng.Next(entries));
                }
            }
        }

        return rows;
    }

    public static int GetBits(byte[] data, int bitPos, int count)
    {
        int v = 0;
        for (int i = 0; i < count; i++)
        {
            int p = bitPos + i;
            v = (v << 1) | ((data[p >> 3] >> (7 - (p & 7))) & 1);
        }

        return v;
    }

    public static void SetBits(byte[] data, int bitPos, int count, int value)
    {
        for (int i = 0; i < count; i++)
        {
            int p = bitPos + i;
            int bit = (value >> (count - 1 - i)) & 1;
            data[p >> 3] = (byte)((data[p >> 3] & ~(0x80 >> (p & 7))) | (bit << (7 - (p & 7))));
        }
    }

    private static readonly int[] XStart = [0, 4, 0, 2, 0, 1, 0];
    private static readonly int[] YStart = [0, 0, 4, 0, 2, 0, 1];
    private static readonly int[] XStep = [8, 8, 4, 4, 2, 2, 1];
    private static readonly int[] YStep = [8, 8, 8, 4, 4, 2, 2];

    /// <summary>Encodes the packed, unfiltered rows as a PNG.</summary>
    public static byte[] Encode(Spec s, byte[] rows)
    {
        byte[] raw;
        using (var ms = new MemoryStream())
        {
            int rowNo = 0;
            if (!s.Interlaced)
            {
                byte[] prev = new byte[s.Stride];
                for (int y = 0; y < s.Height; y++)
                {
                    byte[] cur = rows.AsSpan(y * s.Stride, s.Stride).ToArray();
                    WriteFiltered(ms, cur, prev, Math.Max(1, s.BitsPerPixel / 8), rowNo++ % 5);
                    prev = cur;
                }
            }
            else
            {
                for (int pass = 0; pass < 7; pass++)
                {
                    int pw = (s.Width - XStart[pass] + XStep[pass] - 1) / XStep[pass];
                    int ph = (s.Height - YStart[pass] + YStep[pass] - 1) / YStep[pass];
                    if (pw <= 0 || ph <= 0)
                    {
                        continue;
                    }

                    int pstride = ((pw * s.BitsPerPixel) + 7) / 8;
                    byte[] prev = new byte[pstride];
                    for (int y = 0; y < ph; y++)
                    {
                        byte[] cur = new byte[pstride];
                        for (int x = 0; x < pw; x++)
                        {
                            int sx = XStart[pass] + (x * XStep[pass]);
                            int sy = YStart[pass] + (y * YStep[pass]);
                            for (int b = 0; b < s.BitsPerPixel; b++)
                            {
                                int bit = GetBits(rows, (sy * s.Stride * 8) + (sx * s.BitsPerPixel) + b, 1);
                                SetBits(cur, (x * s.BitsPerPixel) + b, 1, bit);
                            }
                        }

                        WriteFiltered(ms, cur, prev, Math.Max(1, s.BitsPerPixel / 8), rowNo++ % 5);
                        prev = cur;
                    }
                }
            }

            raw = ms.ToArray();
        }

        byte[] zlib;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                z.Write(raw);
            }

            zlib = ms.ToArray();
        }

        using var o = new MemoryStream();
        o.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)s.Width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)s.Height);
        ihdr[8] = (byte)s.Depth;
        ihdr[9] = (byte)s.ColorType;
        ihdr[12] = (byte)(s.Interlaced ? 1 : 0);
        Chunk(o, "IHDR", ihdr);
        if (s.Iccp is not null)
        {
            Chunk(o, "iCCP", s.Iccp);
        }

        if (s.Palette is not null)
        {
            Chunk(o, "PLTE", s.Palette);
        }

        if (s.Trns is not null)
        {
            Chunk(o, "tRNS", s.Trns);
        }

        if (s.SplitIdat && zlib.Length > 4)
        {
            int half = zlib.Length / 2;
            Chunk(o, "IDAT", zlib[..half]);
            Chunk(o, "IDAT", zlib[half..]);
        }
        else
        {
            Chunk(o, "IDAT", zlib, s.BadCrcOnIdat);
        }

        Chunk(o, "IEND", []);
        return o.ToArray();
    }

    public static byte[] IdatPayload(byte[] png)
    {
        using var o = new MemoryStream();
        int pos = 8;
        while (pos + 12 <= png.Length)
        {
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos));
            if (png[pos + 4] == (byte)'I' && png[pos + 5] == (byte)'D')
            {
                o.Write(png, pos + 8, len);
            }

            pos += 12 + len;
        }

        return o.ToArray();
    }

    public static byte[] IccProfileChunk(string colorSpace, bool valid = true)
    {
        byte[] profile = new byte[200];
        System.Text.Encoding.ASCII.GetBytes(colorSpace).CopyTo(profile, 16);
        if (valid)
        {
            System.Text.Encoding.ASCII.GetBytes("acsp").CopyTo(profile, 36);
        }

        using var ms = new MemoryStream();
        ms.Write(System.Text.Encoding.ASCII.GetBytes("test\0"));
        ms.WriteByte(0);
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(profile);
        }

        return ms.ToArray();
    }

    private static void WriteFiltered(Stream o, byte[] cur, byte[] prev, int bpp, int filter)
    {
        o.WriteByte((byte)filter);
        byte[] outRow = new byte[cur.Length];
        for (int i = 0; i < cur.Length; i++)
        {
            int a = i >= bpp ? cur[i - bpp] : 0;
            int b = prev[i];
            int c = i >= bpp ? prev[i - bpp] : 0;
            int pred = filter switch
            {
                0 => 0,
                1 => a,
                2 => b,
                3 => (a + b) >> 1,
                _ => Paeth(a, b, c),
            };
            outRow[i] = (byte)(cur[i] - pred);
        }

        o.Write(outRow);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : (pb <= pc ? b : c);
    }

    private static void Chunk(Stream o, string type, byte[] data, bool badCrc = false)
    {
        byte[] head = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(head, (uint)data.Length);
        o.Write(head);
        byte[] t = System.Text.Encoding.ASCII.GetBytes(type);
        o.Write(t);
        o.Write(data);
        uint crc = Crc32(t, data);
        if (badCrc)
        {
            crc ^= 0x1;
        }

        BinaryPrimitives.WriteUInt32BigEndian(head, crc);
        o.Write(head);
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in type.Concat(data))
        {
            c ^= b;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
        }

        return c ^ 0xFFFFFFFFu;
    }
}
