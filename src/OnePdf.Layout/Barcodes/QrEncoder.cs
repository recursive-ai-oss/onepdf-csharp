using System.Text;

namespace OnePdf.Layout.Barcodes;

/// <summary>A finished QR Code: its dark modules, row by row.</summary>
internal sealed class QrSymbol(int version, QrErrorCorrection level, int mask, int size, bool[] modules)
{
    public int Version { get; } = version;

    public QrErrorCorrection Level { get; } = level;

    public int Mask { get; } = mask;

    /// <summary>Modules per side, 17 + 4 × version.</summary>
    public int Size { get; } = size;

    /// <summary>Row-major, true for a dark module.</summary>
    public bool[] Modules { get; } = modules;

    public bool this[int x, int y] => Modules[(y * Size) + x];
}

/// <summary>
/// A QR Code encoder written from ISO/IEC 18004: mode selection (numeric, alphanumeric and byte segments chosen by a cost search), the
/// smallest version that holds the data, Reed-Solomon error correction over GF(256), module placement, all eight masks scored with the
/// four penalty rules of clause 7.8.3, and format and version information.
/// </summary>
internal static class QrEncoder
{
    // ISO/IEC 18004 Table 9: error correction codewords per block and number of blocks, by level (L, M, Q, H) and version 1..40.
    private static readonly byte[][] EcPerBlock =
    [
        [7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        [10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28],
        [13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        [17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
    ];

    private static readonly byte[][] BlockCount =
    [
        [1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25],
        [1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49],
        [1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68],
        [1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81],
    ];

    // Format information bits 15..14 of the level: L 01, M 00, Q 11, H 10 (Table 12).
    private static readonly int[] LevelBits = [1, 0, 3, 2];

    private const string AlnumChars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    private enum Mode
    {
        Numeric = 0,
        Alnum = 1,
        Byte = 2,
    }

    private readonly record struct Segment(Mode Mode, int First, int Count);

    /// <summary>Modules in the data area of a version, function patterns excluded (7.1, Table 1 of the standard).</summary>
    internal static int RawModules(int version)
    {
        int raw = ((16 * version) + 128) * version + 64;
        if (version >= 2)
        {
            int align = (version / 7) + 2;
            raw -= (((25 * align) - 10) * align) - 55;
            if (version >= 7)
            {
                raw -= 36;
            }
        }

        return raw;
    }

    /// <summary>Data codewords a symbol of this version and level holds.</summary>
    internal static int DataCodewords(int version, QrErrorCorrection level)
        => (RawModules(version) / 8) - (EcPerBlock[(int)level][version - 1] * BlockCount[(int)level][version - 1]);

    /// <summary>Encodes the text in the smallest version from <paramref name="minVersion"/> to <paramref name="maxVersion"/> that holds it.</summary>
    public static QrSymbol Encode(string text, QrErrorCorrection level, int minVersion, int maxVersion, QrCharacterSet charset, int forcedMask = -1)
    {
        (int version, byte[] data) = Prepare(text, level, minVersion, maxVersion, charset);
        return Build(AddErrorCorrection(data, version, level), version, level, forcedMask);
    }

    /// <summary>The version chosen and its data codewords (before error correction), for checking against the standard's worked examples.</summary>
    internal static (int Version, byte[] Data) Prepare(string text, QrErrorCorrection level, int minVersion, int maxVersion, QrCharacterSet charset)
    {
        if (minVersion < 1 || maxVersion > 40 || minVersion > maxVersion)
        {
            throw new PdfBarcodeException(BarcodeSymbology.QrCode, "The QR version range must lie within 1 to 40 and the minimum may not exceed the maximum.");
        }

        if (string.IsNullOrEmpty(text))
        {
            throw new PdfBarcodeException(BarcodeSymbology.QrCode, "A QR Code needs a value to encode.");
        }

        // Code points and their bytes.
        var points = new List<int>(text.Length);
        var bytes = new List<byte[]>(text.Length);
        foreach (Rune r in EnumerateRunes(text))
        {
            points.Add(r.Value);
            if (charset == QrCharacterSet.Latin1)
            {
                if (r.Value > 0xFF)
                {
                    throw new PdfBarcodeException(BarcodeSymbology.QrCode, $"U+{r.Value:X4} cannot be encoded in ISO 8859-1.");
                }

                bytes.Add([(byte)r.Value]);
            }
            else
            {
                bytes.Add(Encoding.UTF8.GetBytes(r.ToString()));
            }
        }

        var segmentsByClass = new List<Segment>?[3];
        int version = -1;
        for (int v = minVersion; v <= maxVersion; v++)
        {
            int cls = v <= 9 ? 0 : v <= 26 ? 1 : 2;
            List<Segment> segs = segmentsByClass[cls] ??= Plan(points, bytes, cls);
            long bits = BitLength(segs, bytes, cls, charset == QrCharacterSet.Utf8WithEci);
            if (bits <= (long)DataCodewords(v, level) * 8)
            {
                version = v;
                break;
            }
        }

        if (version < 0)
        {
            throw new PdfBarcodeException(BarcodeSymbology.QrCode, $"The value does not fit a QR Code of version {maxVersion} at error correction level {level}.");
        }

        int vcls = version <= 9 ? 0 : version <= 26 ? 1 : 2;
        return (version, BuildData(segmentsByClass[vcls]!, points, bytes, vcls, charset == QrCharacterSet.Utf8WithEci, DataCodewords(version, level)));
    }

    private static List<Rune> EnumerateRunes(string text)
    {
        var list = new List<Rune>(text.Length);
        for (int i = 0; i < text.Length;)
        {
            if (!Rune.TryGetRuneAt(text, i, out Rune r))
            {
                throw new PdfBarcodeException(BarcodeSymbology.QrCode, "The value holds an unpaired surrogate, which has no encoding.");
            }

            list.Add(r);
            i += r.Utf16SequenceLength;
        }

        return list;
    }

    private static readonly int[][] CountBits = [[10, 12, 14], [9, 11, 13], [8, 16, 16]];

    private static bool Allowed(int cp, Mode m) => m switch
    {
        Mode.Numeric => cp is >= '0' and <= '9',
        Mode.Alnum => cp < 128 && AlnumChars.Contains((char)cp, StringComparison.Ordinal),
        _ => true,
    };

    // Chooses segments by dynamic programming over the cost in sixths of a bit (10/3 bits per digit, 5.5 per alphanumeric character, 8 per
    // byte, plus the header of every segment). The choice only steers the size: whatever it picks decodes to the same text.
    private static List<Segment> Plan(List<int> points, List<byte[]> bytes, int cls)
    {
        int n = points.Count;
        const long Inf = long.MaxValue / 4;
        var cost = new long[n + 1, 3];
        var prev = new int[n + 1, 3];
        for (int m = 0; m < 3; m++)
        {
            cost[0, m] = Inf;
        }

        for (int i = 0; i < n; i++)
        {
            for (int m = 0; m < 3; m++)
            {
                cost[i + 1, m] = Inf;
                if (!Allowed(points[i], (Mode)m))
                {
                    continue;
                }

                long unit = m == 0 ? 20 : m == 1 ? 33 : 48L * bytes[i].Length;
                long header = 6L * (4 + CountBits[m][cls]);
                if (i == 0)
                {
                    cost[1, m] = header + unit;
                    prev[1, m] = -1;
                    continue;
                }

                for (int p = 0; p < 3; p++)
                {
                    if (cost[i, p] >= Inf)
                    {
                        continue;
                    }

                    long c = cost[i, p] + (p == m ? 0 : header) + unit;
                    if (c < cost[i + 1, m])
                    {
                        cost[i + 1, m] = c;
                        prev[i + 1, m] = p;
                    }
                }
            }
        }

        int best = 2;
        for (int m = 0; m < 2; m++)
        {
            if (cost[n, m] < cost[n, best])
            {
                best = m;
            }
        }

        var reversed = new List<Segment>();
        int mode = best;
        int end = n;
        while (end > 0)
        {
            int start = end;
            int p;
            do
            {
                p = prev[start, mode];
                start--;
            }
            while (p == mode);

            reversed.Add(new Segment((Mode)mode, start, end - start));
            end = start;
            mode = p;
        }

        reversed.Reverse();
        return reversed;
    }

    private static int PayloadBits(Segment s, List<byte[]> bytes)
    {
        switch (s.Mode)
        {
            case Mode.Numeric:
                return (s.Count / 3 * 10) + (s.Count % 3 == 0 ? 0 : s.Count % 3 == 1 ? 4 : 7);
            case Mode.Alnum:
                return (s.Count / 2 * 11) + (s.Count % 2 * 6);
            default:
                int total = 0;
                for (int i = 0; i < s.Count; i++)
                {
                    total += bytes[s.First + i].Length;
                }

                return total * 8;
        }
    }

    private static long BitLength(List<Segment> segs, List<byte[]> bytes, int cls, bool eci)
    {
        long bits = eci ? 12 : 0;
        foreach (Segment s in segs)
        {
            bits += 4 + CountBits[(int)s.Mode][cls] + PayloadBits(s, bytes);
        }

        return bits;
    }

    private sealed class Bits
    {
        private readonly List<bool> _bits = [];

        public int Count => _bits.Count;

        public void Append(int value, int length)
        {
            for (int i = length - 1; i >= 0; i--)
            {
                _bits.Add(((value >> i) & 1) != 0);
            }
        }

        public byte[] ToBytes(int total)
        {
            var result = new byte[total];
            for (int i = 0; i < _bits.Count; i++)
            {
                if (_bits[i])
                {
                    result[i >> 3] |= (byte)(0x80 >> (i & 7));
                }
            }

            return result;
        }
    }

    private static byte[] BuildData(List<Segment> segs, List<int> points, List<byte[]> bytes, int cls, bool eci, int dataCodewords)
    {
        var bits = new Bits();
        if (eci)
        {
            bits.Append(0b0111, 4);
            bits.Append(26, 8);
        }

        foreach (Segment s in segs)
        {
            bits.Append(s.Mode == Mode.Numeric ? 1 : s.Mode == Mode.Alnum ? 2 : 4, 4);
            int count = s.Count;
            if (s.Mode == Mode.Byte)
            {
                count = 0;
                for (int i = 0; i < s.Count; i++)
                {
                    count += bytes[s.First + i].Length;
                }
            }

            bits.Append(count, CountBits[(int)s.Mode][cls]);
            switch (s.Mode)
            {
                case Mode.Numeric:
                    for (int i = 0; i < s.Count; i += 3)
                    {
                        int take = Math.Min(3, s.Count - i);
                        int v = 0;
                        for (int k = 0; k < take; k++)
                        {
                            v = (v * 10) + (points[s.First + i + k] - '0');
                        }

                        bits.Append(v, take == 3 ? 10 : take == 2 ? 7 : 4);
                    }

                    break;
                case Mode.Alnum:
                    for (int i = 0; i < s.Count; i += 2)
                    {
                        int a = AlnumChars.IndexOf((char)points[s.First + i], StringComparison.Ordinal);
                        if (i + 1 < s.Count)
                        {
                            int b = AlnumChars.IndexOf((char)points[s.First + i + 1], StringComparison.Ordinal);
                            bits.Append((a * 45) + b, 11);
                        }
                        else
                        {
                            bits.Append(a, 6);
                        }
                    }

                    break;
                default:
                    for (int i = 0; i < s.Count; i++)
                    {
                        foreach (byte b in bytes[s.First + i])
                        {
                            bits.Append(b, 8);
                        }
                    }

                    break;
            }
        }

        // 7.4.9: a terminator of up to four zero bits, padding to a byte, then the alternating pad codewords 0xEC and 0x11.
        int capacity = dataCodewords * 8;
        bits.Append(0, Math.Min(4, capacity - bits.Count));
        bits.Append(0, (8 - (bits.Count % 8)) % 8);
        for (int pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11)
        {
            bits.Append(pad, 8);
        }

        return bits.ToBytes(dataCodewords);
    }

    // GF(256) with the primitive polynomial x^8 + x^4 + x^3 + x^2 + 1 (Annex A).
    private static readonly byte[] Exp = new byte[512];
    private static readonly int[] Log = new int[256];

    static QrEncoder()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = i;
            x <<= 1;
            if (x >= 256)
            {
                x ^= 0x11D;
            }
        }

        for (int i = 255; i < 512; i++)
        {
            Exp[i] = Exp[i - 255];
        }
    }

    private static byte Multiply(int a, int b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

    /// <summary>The check codewords of a block: the remainder of the data polynomial divided by the generator of this degree (Annex A).</summary>
    internal static byte[] ReedSolomon(ReadOnlySpan<byte> data, int degree)
    {
        var generator = new byte[degree];
        generator[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                generator[j] = Multiply(generator[j], root);
                if (j + 1 < degree)
                {
                    generator[j] ^= generator[j + 1];
                }
            }

            root = Multiply(root, 2);
        }

        var result = new byte[degree];
        foreach (byte b in data)
        {
            int factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, degree - 1);
            result[degree - 1] = 0;
            for (int j = 0; j < degree; j++)
            {
                result[j] ^= Multiply(generator[j], factor);
            }
        }

        return result;
    }

    internal static byte[] AddErrorCorrection(byte[] data, int version, QrErrorCorrection level)
    {
        int blocks = BlockCount[(int)level][version - 1];
        int ec = EcPerBlock[(int)level][version - 1];
        int totalCodewords = RawModules(version) / 8;
        int shortBlocks = blocks - (totalCodewords % blocks);
        int shortLength = (totalCodewords / blocks) - ec;

        var dataBlocks = new byte[blocks][];
        var ecBlocks = new byte[blocks][];
        int offset = 0;
        for (int b = 0; b < blocks; b++)
        {
            int len = shortLength + (b < shortBlocks ? 0 : 1);
            dataBlocks[b] = data.AsSpan(offset, len).ToArray();
            ecBlocks[b] = ReedSolomon(dataBlocks[b], ec);
            offset += len;
        }

        // 7.6: the codewords of the blocks are interleaved, data first, then check codewords.
        var result = new byte[totalCodewords];
        int k = 0;
        for (int i = 0; i <= shortLength; i++)
        {
            for (int b = 0; b < blocks; b++)
            {
                if (i < dataBlocks[b].Length)
                {
                    result[k++] = dataBlocks[b][i];
                }
            }
        }

        for (int i = 0; i < ec; i++)
        {
            for (int b = 0; b < blocks; b++)
            {
                result[k++] = ecBlocks[b][i];
            }
        }

        return result;
    }

    private sealed class Grid(int size)
    {
        public readonly int Size = size;
        public readonly bool[] Dark = new bool[size * size];
        public readonly bool[] Function = new bool[size * size];

        public void SetFunction(int x, int y, bool dark)
        {
            Dark[(y * Size) + x] = dark;
            Function[(y * Size) + x] = true;
        }
    }

    internal static int[] AlignmentPositions(int version)
    {
        if (version == 1)
        {
            return [];
        }

        int count = (version / 7) + 2;
        int size = 17 + (4 * version);
        int step = version == 32 ? 26 : ((((size - 13) + ((count * 2) - 2) - 1) / ((count * 2) - 2)) * 2);
        var positions = new int[count];
        positions[0] = 6;
        for (int i = count - 1, pos = size - 7; i >= 1; i--, pos -= step)
        {
            positions[i] = pos;
        }

        return positions;
    }

    private static Grid BuildFunctionPatterns(int version)
    {
        int size = 17 + (4 * version);
        var g = new Grid(size);
        for (int i = 0; i < size; i++)
        {
            g.SetFunction(6, i, i % 2 == 0);
            g.SetFunction(i, 6, i % 2 == 0);
        }

        // Finder patterns with their separators.
        foreach ((int cx, int cy) in new[] { (3, 3), (size - 4, 3), (3, size - 4) })
        {
            for (int dy = -4; dy <= 4; dy++)
            {
                for (int dx = -4; dx <= 4; dx++)
                {
                    int x = cx + dx;
                    int y = cy + dy;
                    if (x < 0 || y < 0 || x >= size || y >= size)
                    {
                        continue;
                    }

                    int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    g.SetFunction(x, y, dist != 2 && dist != 4);
                }
            }
        }

        // Alignment patterns, except where they would touch a finder pattern.
        int[] pos = AlignmentPositions(version);
        for (int i = 0; i < pos.Length; i++)
        {
            for (int j = 0; j < pos.Length; j++)
            {
                bool corner = (i == 0 && j == 0) || (i == 0 && j == pos.Length - 1) || (i == pos.Length - 1 && j == 0);
                if (corner)
                {
                    continue;
                }

                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        g.SetFunction(pos[i] + dx, pos[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                    }
                }
            }
        }

        DrawFormat(g, QrErrorCorrection.Low, 0);
        if (version >= 7)
        {
            int rem = version;
            for (int i = 0; i < 12; i++)
            {
                rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            }

            int bits = (version << 12) | rem;
            for (int i = 0; i < 18; i++)
            {
                bool bit = ((bits >> i) & 1) != 0;
                int a = size - 11 + (i % 3);
                int b = i / 3;
                g.SetFunction(a, b, bit);
                g.SetFunction(b, a, bit);
            }
        }

        return g;
    }

    /// <summary>The 15 format information bits: level and mask, a BCH (15,5) code, XOR the mask pattern 101010000010010 (7.9).</summary>
    internal static int FormatBits(QrErrorCorrection level, int mask)
    {
        int data = (LevelBits[(int)level] << 3) | mask;
        int rem = data;
        for (int i = 0; i < 10; i++)
        {
            rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        }

        return ((data << 10) | rem) ^ 0x5412;
    }

    private static void DrawFormat(Grid g, QrErrorCorrection level, int mask)
    {
        int bits = FormatBits(level, mask);
        int size = g.Size;
        bool Bit(int i) => ((bits >> i) & 1) != 0;

        // First copy around the top-left finder, second copy split between the other two (Figure 25).
        for (int i = 0; i <= 5; i++)
        {
            g.SetFunction(8, i, Bit(i));
        }

        g.SetFunction(8, 7, Bit(6));
        g.SetFunction(8, 8, Bit(7));
        g.SetFunction(7, 8, Bit(8));
        for (int i = 9; i < 15; i++)
        {
            g.SetFunction(14 - i, 8, Bit(i));
        }

        for (int i = 0; i < 8; i++)
        {
            g.SetFunction(size - 1 - i, 8, Bit(i));
        }

        for (int i = 8; i < 15; i++)
        {
            g.SetFunction(8, size - 15 + i, Bit(i));
        }

        g.SetFunction(8, size - 8, true);
    }

    private static bool MaskBit(int mask, int row, int col) => mask switch
    {
        0 => (row + col) % 2 == 0,
        1 => row % 2 == 0,
        2 => col % 3 == 0,
        3 => (row + col) % 3 == 0,
        4 => ((row / 2) + (col / 3)) % 2 == 0,
        5 => ((row * col) % 2) + ((row * col) % 3) == 0,
        6 => (((row * col) % 2) + ((row * col) % 3)) % 2 == 0,
        _ => ((((row + col) % 2) + ((row * col) % 3)) % 2) == 0,
    };

    private static QrSymbol Build(byte[] codewords, int version, QrErrorCorrection level, int forcedMask)
    {
        Grid g = BuildFunctionPatterns(version);
        int size = g.Size;

        // 7.7.3: codewords go in two-module-wide columns from the lower right, upward then downward, skipping the timing column.
        int bit = 0;
        int total = codewords.Length * 8;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
            {
                right = 5;
            }

            for (int vert = 0; vert < size; vert++)
            {
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? size - 1 - vert : vert;
                    int index = (y * size) + x;
                    if (!g.Function[index] && bit < total)
                    {
                        g.Dark[index] = ((codewords[bit >> 3] >> (7 - (bit & 7))) & 1) != 0;
                        bit++;
                    }
                }
            }
        }

        int bestMask = 0;
        int bestPenalty = int.MaxValue;
        bool[]? best = null;
        for (int mask = forcedMask >= 0 ? forcedMask : 0; mask < (forcedMask >= 0 ? forcedMask + 1 : 8); mask++)
        {
            var trial = new Grid(size);
            Array.Copy(g.Dark, trial.Dark, g.Dark.Length);
            Array.Copy(g.Function, trial.Function, g.Function.Length);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int index = (y * size) + x;
                    if (!trial.Function[index] && MaskBit(mask, y, x))
                    {
                        trial.Dark[index] = !trial.Dark[index];
                    }
                }
            }

            DrawFormat(trial, level, mask);
            int penalty = Penalty(trial.Dark, size);
            if (penalty < bestPenalty)
            {
                bestPenalty = penalty;
                bestMask = mask;
                best = trial.Dark;
            }
        }

        return new QrSymbol(version, level, bestMask, size, best!);
    }

    /// <summary>The mask score of 7.8.3: runs, 2×2 blocks, finder-like patterns and the dark/light balance. Lower is better.</summary>
    internal static int Penalty(bool[] m, int size)
    {
        int penalty = 0;
        var line = new bool[size];
        for (int pass = 0; pass < 2; pass++)
        {
            for (int a = 0; a < size; a++)
            {
                for (int b = 0; b < size; b++)
                {
                    line[b] = pass == 0 ? m[(a * size) + b] : m[(b * size) + a];
                }

                // Rule 1: five or more equal modules in a row score 3 plus one per extra module.
                int run = 1;
                for (int b = 1; b <= size; b++)
                {
                    if (b < size && line[b] == line[b - 1])
                    {
                        run++;
                        continue;
                    }

                    if (run >= 5)
                    {
                        penalty += 3 + (run - 5);
                    }

                    run = 1;
                }

                // Rule 3: dark-light-dark-dark-dark-light-dark with four light modules on either side scores 40.
                for (int b = 0; b + 7 <= size; b++)
                {
                    if (line[b] && !line[b + 1] && line[b + 2] && line[b + 3] && line[b + 4] && !line[b + 5] && line[b + 6])
                    {
                        if (LightBefore(line, b) || LightAfter(line, b + 7))
                        {
                            penalty += 40;
                        }

                    }
                }
            }
        }

        // Rule 2: every 2×2 block of one colour scores 3.
        for (int y = 0; y + 1 < size; y++)
        {
            for (int x = 0; x + 1 < size; x++)
            {
                bool c = m[(y * size) + x];
                if (c == m[(y * size) + x + 1] && c == m[((y + 1) * size) + x] && c == m[((y + 1) * size) + x + 1])
                {
                    penalty += 3;
                }
            }
        }

        // Rule 4: ten per five percent the dark share is away from half.
        int dark = 0;
        foreach (bool d in m)
        {
            if (d)
            {
                dark++;
            }
        }

        int cells = size * size;
        int k = ((Math.Abs((dark * 20) - (cells * 10)) + cells - 1) / cells) - 1;
        if (k > 0)
        {
            penalty += k * 10;
        }

        return penalty;
    }

    private static bool LightBefore(bool[] line, int start)
    {
        if (start < 4)
        {
            return false;
        }

        return !line[start - 1] && !line[start - 2] && !line[start - 3] && !line[start - 4];
    }

    private static bool LightAfter(bool[] line, int end)
    {
        if (end + 4 > line.Length)
        {
            return false;
        }

        return !line[end] && !line[end + 1] && !line[end + 2] && !line[end + 3];
    }
}
