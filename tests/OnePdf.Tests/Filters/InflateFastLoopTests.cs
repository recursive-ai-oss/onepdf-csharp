using System.Buffers.Binary;
using System.IO.Compression;
using OnePdf.Filters;

namespace OnePdf.Tests.Filters;

/// <summary>
/// The inflater's fast loop decodes up to three symbols from one 56-bit refill. A literal followed by a length/distance
/// pair can need 15 + 15 + 5 + 15 + 13 = 63 bits (RFC 1951 3.2.5), more than was counted, so the loop must refill first.
/// These streams are built with long Huffman codes so that the sequence is common, and are compared with the platform's
/// one-shot inflate.
/// </summary>
public class InflateFastLoopTests
{
    // ---- a small DEFLATE writer: one block, caller-chosen code lengths, random but valid tokens ----

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private ulong _acc;
        private int _n;

        public void Bits(uint value, int count)
        {
            _acc |= (ulong)value << _n;
            _n += count;
            while (_n >= 8)
            {
                _bytes.Add((byte)_acc);
                _acc >>= 8;
                _n -= 8;
            }
        }

        /// <summary>Huffman codes are packed most significant bit first (RFC 1951 3.1.1).</summary>
        public void Code(uint code, int len)
        {
            uint rev = 0;
            for (int i = 0; i < len; i++)
            {
                rev = (rev << 1) | ((code >> i) & 1);
            }

            Bits(rev, len);
        }

        public byte[] Finish()
        {
            if (_n > 0)
            {
                _bytes.Add((byte)_acc);
                _acc = 0;
                _n = 0;
            }

            return [.. _bytes];
        }
    }

    private static uint[] CanonicalCodes(int[] lengths)
    {
        int max = lengths.Max();
        int[] count = new int[max + 2];
        foreach (int l in lengths)
        {
            if (l > 0)
            {
                count[l]++;
            }
        }

        uint[] next = new uint[max + 2];
        uint code = 0;
        for (int b = 1; b <= max; b++)
        {
            code = (code + (uint)count[b - 1]) << 1;
            next[b] = code;
        }

        uint[] codes = new uint[lengths.Length];
        for (int i = 0; i < lengths.Length; i++)
        {
            if (lengths[i] > 0)
            {
                codes[i] = next[lengths[i]]++;
            }
        }

        return codes;
    }

    private static readonly int[] LenBase = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
    private static readonly int[] LenExtra = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
    private static readonly int[] DistBase = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
    private static readonly int[] DistExtra = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];

    /// <summary>16 symbols whose code lengths are 1, 2, ... 14, 15, 15 (a complete code), handed out in a seeded order.</summary>
    private static int[] SkewedLengths(int size, int[] symbols, Random rng)
    {
        int[] lens = new int[size];
        int[] order = [.. symbols.OrderBy(_ => rng.Next())];
        for (int i = 0; i < order.Length; i++)
        {
            lens[order[i]] = Math.Min(i + 1, order.Length - 1);
        }

        return lens;
    }

    private static int Adler(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;
        foreach (byte x in data)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }

        return (int)((b << 16) | a);
    }

    /// <summary>
    /// A zlib stream with one block of random literals and length/distance pairs. With <paramref name="dynamic"/> the
    /// literal/length and distance codes are skewed to 15-bit codes, so pairs cost close to the 63-bit worst case.
    /// </summary>
    private static byte[] LongCodeStream(int seed, bool dynamic, int tokens, out byte[] expected)
    {
        var rng = new Random(seed);
        var w = new BitWriter();
        w.Bits(1, 1);
        int[] litLens;
        int[] distLens;
        if (dynamic)
        {
            w.Bits(2, 2);
            int[] litSyms = [256, 65, 66, 67, 68, 69, 70, 71, 278, 279, 280, 281, 282, 283, 284, 285];
            int[] distSyms = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 28, 29];
            litLens = SkewedLengths(286, litSyms, rng);
            distLens = SkewedLengths(30, distSyms, rng);
            w.Bits(286 - 257, 5);
            w.Bits(29, 5);
            w.Bits(19 - 4, 4);
            // Code-length alphabet: symbols 0..15 get 4-bit codes (a complete code), 16..18 are unused.
            int[] clOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];
            foreach (int s in clOrder)
            {
                w.Bits(s >= 16 ? 0u : 4u, 3);
            }

            foreach (int l in litLens.Take(286))
            {
                w.Code((uint)l, 4);
            }

            foreach (int l in distLens)
            {
                w.Code((uint)l, 4);
            }
        }
        else
        {
            w.Bits(1, 2);
            litLens = [.. Enumerable.Range(0, 288).Select(i => i < 144 ? 8 : i < 256 ? 9 : i < 280 ? 7 : 8)];
            distLens = [.. Enumerable.Repeat(5, 30)];
        }

        uint[] litCodes = CanonicalCodes(litLens);
        uint[] distCodes = CanonicalCodes(distLens);
        int[] usedLit = [.. Enumerable.Range(0, litLens.Length).Where(i => litLens[i] > 0 && i != 256 && i < 286)];
        int[] usedDist = [.. Enumerable.Range(0, distLens.Length).Where(i => distLens[i] > 0)];
        var output = new List<byte>();
        for (int t = 0; t < tokens; t++)
        {
            int sym = usedLit[rng.Next(usedLit.Length)];
            if (sym < 256)
            {
                w.Code(litCodes[sym], litLens[sym]);
                output.Add((byte)sym);
                continue;
            }

            int[] feasible = [.. usedDist.Where(d => DistBase[d] <= output.Count)];
            if (feasible.Length == 0)
            {
                sym = 65;
                if (litLens[sym] == 0)
                {
                    sym = usedLit.First(s => s < 256);
                }

                w.Code(litCodes[sym], litLens[sym]);
                output.Add((byte)sym);
                continue;
            }

            int li = sym - 257;
            w.Code(litCodes[sym], litLens[sym]);
            int extra = rng.Next(1 << LenExtra[li]);
            w.Bits((uint)extra, LenExtra[li]);
            int length = LenBase[li] + extra;
            int ds = feasible[rng.Next(feasible.Length)];
            w.Code(distCodes[ds], distLens[ds]);
            int dmax = Math.Min(1 << DistExtra[ds], output.Count - DistBase[ds] + 1);
            int dextra = rng.Next(dmax);
            w.Bits((uint)dextra, DistExtra[ds]);
            int dist = DistBase[ds] + dextra;
            for (int i = 0; i < length; i++)
            {
                output.Add(output[output.Count - dist]);
            }
        }

        w.Code(litCodes[256], litLens[256]);
        expected = [.. output];
        byte[] body = w.Finish();
        byte[] z = new byte[2 + body.Length + 4];
        z[0] = 0x78;
        z[1] = 0x9C;
        body.CopyTo(z, 2);
        BinaryPrimitives.WriteInt32BigEndian(z.AsSpan(2 + body.Length), Adler(expected));
        return z;
    }

    private static byte[] OneShot(byte[] z)
    {
        using var ms = new MemoryStream();
        using (var zs = new ZLibStream(new MemoryStream(z), CompressionMode.Decompress))
        {
            zs.CopyTo(ms);
        }

        return ms.ToArray();
    }

    /// <summary>Feeds <paramref name="z"/> to the inflater in chunks cut at <paramref name="cuts"/>, with a small output buffer.</summary>
    private static byte[] Chunked(byte[] z, int[] cuts, int outSize = 4096)
    {
        using var inf = new Inflater(true);
        var result = new List<byte>();
        var pending = new List<byte>();
        byte[] buf = new byte[outSize];
        int from = 0;
        foreach (int cut in cuts.Append(z.Length))
        {
            pending.AddRange(z.AsSpan(from, cut - from).ToArray());
            from = cut;
            while (true)
            {
                byte[] inBytes = [.. pending];
                InflateStatus st = inf.Inflate(inBytes, buf, out int consumed, out int written);
                Assert.InRange(consumed, 0, inBytes.Length);
                result.AddRange(buf.AsSpan(0, written).ToArray());
                pending.RemoveRange(0, consumed);
                if (st == InflateStatus.Done)
                {
                    Assert.False(inf.ChecksumMismatch);
                    return [.. result];
                }

                if (st == InflateStatus.NeedInput)
                {
                    break;
                }
            }
        }

        throw new InvalidOperationException("The stream did not finish.");
    }

    private static void AssertSame(byte[] expected, byte[] actual, string what)
    {
        Assert.True(expected.AsSpan().SequenceEqual(actual), what + ": output differs from the platform inflate");
    }

    public static TheoryData<int, bool> Seeds()
    {
        var d = new TheoryData<int, bool>();
        for (int s = 1; s <= 24; s++)
        {
            d.Add(s, true);
        }

        d.Add(101, false);
        d.Add(102, false);
        return d;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void LongCodeStreamsDecodeWhole(int seed, bool dynamic)
    {
        byte[] z = LongCodeStream(seed, dynamic, 6000, out byte[] mine);
        byte[] reference = OneShot(z);
        Assert.True(mine.AsSpan().SequenceEqual(reference), "test encoder disagrees with the platform inflate");
        AssertSame(reference, Chunked(z, []), "whole");
        FilterResult r = FilterTestUtil.Flate(z);
        Assert.Equal(FilterIssues.None, r.Issues);
        AssertSame(reference, r.Data.ToArray(), "Flate filter");
    }

    [Fact]
    public void EverySplitPointGivesTheSameOutput()
    {
        byte[] z = LongCodeStream(7, true, 700, out _);
        byte[] reference = OneShot(z);
        for (int cut = 1; cut < z.Length; cut++)
        {
            AssertSame(reference, Chunked(z, [cut], 512), "split at " + cut);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RandomSplitsGiveTheSameOutput(int seed)
    {
        var rng = new Random(seed);
        byte[] z = LongCodeStream(seed + 50, true, 20000, out _);
        byte[] reference = OneShot(z);
        for (int round = 0; round < 40; round++)
        {
            int[] cuts = [.. Enumerable.Range(0, rng.Next(2, 60)).Select(_ => rng.Next(1, z.Length)).Distinct().Order()];
            AssertSame(reference, Chunked(z, cuts, 1 << rng.Next(6, 14)), "round " + round);
        }
    }

    [Theory]
    [InlineData(CompressionLevel.Fastest)]
    [InlineData(CompressionLevel.Optimal)]
    [InlineData(CompressionLevel.SmallestSize)]
    public void OrdinaryStreamsSplitAnywhere(CompressionLevel level)
    {
        byte[] data = [.. FilterTestUtil.Texty(30_000, 11), .. FilterTestUtil.Random(3000, 12), .. FilterTestUtil.Texty(30_000, 13)];
        byte[] z = FilterTestUtil.Zlib(data, level);
        var rng = new Random(5);
        for (int round = 0; round < 40; round++)
        {
            int[] cuts = [.. Enumerable.Range(0, rng.Next(1, 20)).Select(_ => rng.Next(1, z.Length)).Distinct().Order()];
            AssertSame(data, Chunked(z, cuts, 1 << rng.Next(6, 14)), "round " + round);
        }
    }

    [Fact]
    public void WrongChecksumOnACompleteStreamKeepsTheDataAndRecordsADiagnostic()
    {
        // PDF Flate is lenient (ISO 32000-2 7.4.4): the data is kept, but a mismatch on a complete stream is still reported.
        byte[] plain = LongCodeStream(3, true, 3000, out _);
        byte[] data = OneShot(plain);
        byte[] bad = (byte[])plain.Clone();
        bad[^1] ^= 0x55;
        var b = new PdfBuilder();
        b.BasicPages();
        b.Stream(4, "/Filter /FlateDecode", bad);
        long x = b.XrefTable(5);
        b.Trailer("/Size 5 /Root 1 0 R", x);
        using PdfDocument doc = PdfDocument.Open(b.ToArray());
        var s = (PdfStream)doc.GetObject(4)!;
        Assert.True(data.AsSpan().SequenceEqual(s.GetDecodedBytes().Span));
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("checksum", StringComparison.OrdinalIgnoreCase));
    }

    // ---- PNG files supplied through the environment (never committed) ----

    private static byte[] ConcatIdat(byte[] png)
    {
        using var ms = new MemoryStream();
        int p = 8;
        while (p + 12 <= png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(p));
            if (png[p + 4] == 'I' && png[p + 5] == 'D' && png[p + 6] == 'A' && png[p + 7] == 'T')
            {
                ms.Write(png, p + 8, len);
            }

            p += 12 + len;
        }

        return ms.ToArray();
    }

    [Fact]
    public void ExternalPngStreams()
    {
        string? dir = Environment.GetEnvironmentVariable("ONEPDF_INFLATE_REPRO");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return;
        }

        var rng = new Random(9);
        foreach (string file in Directory.GetFiles(dir, "*.png").Order())
        {
            byte[] z = ConcatIdat(File.ReadAllBytes(file));
            byte[] reference = OneShot(z);
            string name = Path.GetFileName(file);
            AssertSame(reference, Chunked(z, [], 1 << 16), name + " whole");
            FilterResult r = FilterTestUtil.Flate(z);
            Assert.Equal(FilterIssues.None, r.Issues);
            AssertSame(reference, r.Data.ToArray(), name + " Flate filter");
            for (int round = 0; round < 8; round++)
            {
                int[] cuts = [.. Enumerable.Range(0, rng.Next(1, 30)).Select(_ => rng.Next(1, z.Length)).Distinct().Order()];
                AssertSame(reference, Chunked(z, cuts, 1 << rng.Next(10, 16)), name + " split " + round);
            }
        }
    }
}
