using System.IO.Compression;
using OnePdf.Filters;

namespace OnePdf.Tests.Filters;

internal static class FilterTestUtil
{
    public static byte[] Zlib(byte[] data, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, level, leaveOpen: true))
        {
            z.Write(data);
        }

        return ms.ToArray();
    }

    public static byte[] RawDeflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(data);
        }

        return ms.ToArray();
    }

    public static byte[] Random(int n, int seed)
    {
        var b = new byte[n];
        new System.Random(seed).NextBytes(b);
        return b;
    }

    /// <summary>Compressible but not trivial: random walk over a small alphabet with repeats.</summary>
    public static byte[] Texty(int n, int seed)
    {
        var r = new System.Random(seed);
        var b = new byte[n];
        for (int i = 0; i < n; i++)
        {
            b[i] = i > 20 && r.Next(3) == 0 ? b[i - r.Next(1, 20)] : (byte)('a' + r.Next(12));
        }

        return b;
    }

    public static FilterResult Flate(byte[] input, FilterParameters? p = null, DecodeLimits? limits = null) =>
        StreamFilters.Decode("FlateDecode", input, p, limits ?? DecodeLimits.Default);

    /// <summary>Test LZW encoder, PDF flavour: MSB-first, clear 256, EOD 257, early change 0 or 1.</summary>
    public static byte[] LzwEncode(byte[] data, int early, bool clearWhenFull = true)
    {
        var outBytes = new List<byte>();
        ulong acc = 0;
        int nbits = 0;
        void Put(int code, int width)
        {
            acc = (acc << width) | (uint)code;
            nbits += width;
            while (nbits >= 8)
            {
                outBytes.Add((byte)(acc >> (nbits - 8)));
                nbits -= 8;
            }
        }

        var dict = new Dictionary<(int, byte), int>();
        int next = 258;
        int width = 9;
        int decNext = 258;
        int emitted = 0;
        Put(256, width);

        void Emit(int code)
        {
            Put(code, width);
            // The decoder is one table entry behind the encoder, so it widens the code width later than the encoder
            // added entries; mirror what it will do.
            if (emitted > 0 && decNext < 4096)
            {
                decNext++;
                if (decNext + early == (1 << width) && width < 12)
                {
                    width++;
                }
            }

            emitted++;
        }

        int cur = -1;
        foreach (byte c in data)
        {
            if (cur < 0)
            {
                cur = c;
                continue;
            }

            if (dict.TryGetValue((cur, c), out int found))
            {
                cur = found;
                continue;
            }

            Emit(cur);
            if (next < 4096)
            {
                dict[(cur, c)] = next++;
            }

            if (clearWhenFull && next >= 4094)
            {
                Put(256, width);
                dict.Clear();
                next = 258;
                width = 9;
                decNext = 258;
                emitted = 0;
            }

            cur = c;
        }

        if (cur >= 0)
        {
            Emit(cur);
        }

        Put(257, width);
        if (nbits > 0)
        {
            outBytes.Add((byte)(acc << (8 - nbits)));
        }

        return outBytes.ToArray();
    }

    public static byte[] PngEncode(byte[] raw, int rowBytes, int bpp, Func<int, int> tagForRow)
    {
        int rows = (raw.Length + rowBytes - 1) / rowBytes;
        var o = new List<byte>();
        for (int r = 0; r < rows; r++)
        {
            int tag = tagForRow(r);
            o.Add((byte)tag);
            for (int i = 0; i < rowBytes && (r * rowBytes) + i < raw.Length; i++)
            {
                int x = raw[(r * rowBytes) + i];
                int a = i >= bpp ? raw[(r * rowBytes) + i - bpp] : 0;
                int b = r > 0 ? raw[((r - 1) * rowBytes) + i] : 0;
                int c = r > 0 && i >= bpp ? raw[((r - 1) * rowBytes) + i - bpp] : 0;
                int pred = tag switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) >> 1,
                    _ => Paeth(a, b, c),
                };
                o.Add((byte)(x - pred));
            }
        }

        return o.ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>Forward TIFF predictor 2 over rows of packed samples.</summary>
    public static byte[] TiffEncode(byte[] raw, int colors, int bpc, int columns)
    {
        int rowBytes = ((colors * bpc * columns) + 7) / 8;
        var o = (byte[])raw.Clone();
        int mask = (1 << Math.Min(bpc, 8)) - 1;
        for (int row = 0; row + rowBytes <= raw.Length; row += rowBytes)
        {
            if (bpc == 16)
            {
                for (int k = (columns * colors) - 1; k >= colors; k--)
                {
                    int i = row + (k * 2);
                    int j = i - (colors * 2);
                    int v = ((raw[i] << 8) | raw[i + 1]) - ((raw[j] << 8) | raw[j + 1]);
                    o[i] = (byte)(v >> 8);
                    o[i + 1] = (byte)v;
                }
            }
            else if (bpc == 8)
            {
                for (int k = (columns * colors) - 1; k >= colors; k--)
                {
                    o[row + k] = (byte)(raw[row + k] - raw[row + k - colors]);
                }
            }
            else
            {
                int Get(int k) => (raw[row + ((k * bpc) >> 3)] >> (8 - bpc - ((k * bpc) & 7))) & mask;
                for (int k = (columns * colors) - 1; k >= colors; k--)
                {
                    int v = (Get(k) - Get(k - colors)) & mask;
                    int bit = k * bpc;
                    int shift = 8 - bpc - (bit & 7);
                    o[row + (bit >> 3)] = (byte)((o[row + (bit >> 3)] & ~(mask << shift)) | (v << shift));
                }
            }
        }

        return o;
    }
}
