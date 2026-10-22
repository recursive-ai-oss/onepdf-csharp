using System.IO.Compression;
using System.Text;
using OnePdf.Filters;

namespace OnePdf.Tests.Filters;

public class FlateTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(100_000)]
    [InlineData(3_000_000)]
    public void RoundTripsRandomAndTextData(int n)
    {
        foreach (byte[] data in new[] { FilterTestUtil.Random(n, 1), FilterTestUtil.Texty(n, 2), new byte[n] })
        {
            foreach (CompressionLevel lv in new[] { CompressionLevel.NoCompression, CompressionLevel.Fastest, CompressionLevel.SmallestSize })
            {
                FilterResult r = FilterTestUtil.Flate(FilterTestUtil.Zlib(data, lv));
                Assert.True(r.IsClean, r.Message);
                Assert.True(data.AsSpan().SequenceEqual(r.Data.Span));
            }
        }
    }

    [Fact]
    public void AbbreviatedNameAndByteNameWork()
    {
        byte[] data = FilterTestUtil.Texty(5000, 3);
        byte[] z = FilterTestUtil.Zlib(data);
        Assert.True(data.AsSpan().SequenceEqual(StreamFilters.Decode("Fl", z, null, DecodeLimits.Default).Data.Span));
        Assert.True(data.AsSpan().SequenceEqual(StreamFilters.Decode("FlateDecode"u8, z, null, DecodeLimits.Default).Data.Span));
    }

    [Fact]
    public void RawDeflateWithoutHeaderDecodesWithBadHeaderFlag()
    {
        byte[] data = FilterTestUtil.Texty(20_000, 4);
        FilterResult r = FilterTestUtil.Flate(FilterTestUtil.RawDeflate(data));
        Assert.Equal(FilterIssues.BadHeader, r.Issues);
        Assert.True(data.AsSpan().SequenceEqual(r.Data.Span));
    }

    [Fact]
    public void DamagedHeaderCheckIsSkipped()
    {
        byte[] data = FilterTestUtil.Texty(20_000, 5);
        byte[] z = FilterTestUtil.Zlib(data);
        z[1] ^= 0x01;
        FilterResult r = FilterTestUtil.Flate(z);
        Assert.True((r.Issues & FilterIssues.BadHeader) != 0);
        Assert.True(data.AsSpan().SequenceEqual(r.Data.Span));
    }

    [Fact]
    public void LeadingEndOfLineBeforeHeaderIsSkipped()
    {
        byte[] data = FilterTestUtil.Texty(2000, 6);
        byte[] z = [(byte)'\r', (byte)'\n', .. FilterTestUtil.Zlib(data)];
        FilterResult r = FilterTestUtil.Flate(z);
        Assert.True(data.AsSpan().SequenceEqual(r.Data.Span));
        Assert.True((r.Issues & FilterIssues.BadHeader) != 0);
    }

    [Fact]
    public void BadChecksumKeepsAllData()
    {
        byte[] data = FilterTestUtil.Texty(20_000, 7);
        byte[] z = FilterTestUtil.Zlib(data);
        z[^1] ^= 0xFF;
        FilterResult r = FilterTestUtil.Flate(z);
        Assert.Equal(FilterIssues.BadChecksum, r.Issues);
        Assert.True(data.AsSpan().SequenceEqual(r.Data.Span));
    }

    [Fact]
    public void MissingChecksumKeepsAllData()
    {
        byte[] data = FilterTestUtil.Texty(20_000, 8);
        byte[] z = FilterTestUtil.Zlib(data);
        foreach (int cut in new[] { 4, 3, 1 })
        {
            FilterResult r = FilterTestUtil.Flate(z.AsSpan(0, z.Length - cut).ToArray());
            Assert.Equal(FilterIssues.MissingChecksum, r.Issues);
            Assert.True(data.AsSpan().SequenceEqual(r.Data.Span));
        }
    }

    [Fact]
    public void TrailingJunkIsFlaggedAndWhitespaceIsNot()
    {
        byte[] data = FilterTestUtil.Texty(3000, 9);
        byte[] z = FilterTestUtil.Zlib(data);
        FilterResult r = FilterTestUtil.Flate([.. z, .. "endstream junk"u8.ToArray()]);
        Assert.Equal(FilterIssues.TrailingData, r.Issues);
        Assert.True(data.AsSpan().SequenceEqual(r.Data.Span));
        Assert.True(FilterTestUtil.Flate([.. z, (byte)'\r', (byte)'\n']).IsClean);
    }

    [Fact]
    public void TruncatedStreamReturnsPrefix()
    {
        byte[] data = FilterTestUtil.Random(200_000, 10);
        byte[] z = FilterTestUtil.Zlib(data);
        foreach (int keep in new[] { 2, 3, 100, z.Length / 2, z.Length - 5 })
        {
            FilterResult r = FilterTestUtil.Flate(z.AsSpan(0, keep).ToArray());
            Assert.True((r.Issues & FilterIssues.Truncated) != 0, "keep=" + keep);
            Assert.True(r.Data.Length <= data.Length);
            Assert.True(data.AsSpan(0, r.Data.Length).SequenceEqual(r.Data.Span));
            if (keep == z.Length / 2)
            {
                Assert.True(r.Data.Length > 50_000);
            }
        }
    }

    [Fact]
    public void CorruptionInTheMiddleKeepsEarlierOutput()
    {
        byte[] data = FilterTestUtil.Texty(300_000, 11);
        byte[] z = FilterTestUtil.Zlib(data);
        int bad = 0;
        for (int seed = 0; seed < 30; seed++)
        {
            byte[] c = (byte[])z.Clone();
            for (int k = 0; k < 64; k++)
            {
                c[(c.Length / 2) + k] ^= (byte)(0x55 + seed + k);
            }

            FilterResult r = FilterTestUtil.Flate(c);
            Assert.False(r.IsClean);
            if ((r.Issues & FilterIssues.DataError) != 0)
            {
                bad++;
                Assert.True(r.Data.Length > 1000);
            }
        }

        Assert.True(bad > 0);
    }

    [Fact]
    public void GarbageNeverThrows()
    {
        var rnd = new Random(12);
        for (int i = 0; i < 300; i++)
        {
            byte[] g = FilterTestUtil.Random(rnd.Next(0, 300), i);
            if (i % 2 == 0 && g.Length > 2)
            {
                g[0] = 0x78;
                g[1] = 0x9C;
            }

            FilterResult r = FilterTestUtil.Flate(g);
            Assert.True(r.Data.Length >= 0);
        }
    }

    [Fact]
    public void OutputCapStopsAtTheCap()
    {
        byte[] data = new byte[5_000_000];
        byte[] z = FilterTestUtil.Zlib(data);
        var limits = new DecodeLimits(1_000_000, 0, 0);
        FilterResult r = FilterTestUtil.Flate(z, null, limits);
        Assert.True((r.Issues & FilterIssues.LimitExceeded) != 0);
        Assert.True(r.Data.Length <= 1_000_000);
    }

    [Fact]
    public void ExactlyAtTheCapPasses()
    {
        byte[] data = FilterTestUtil.Texty(100_000, 13);
        FilterResult r = FilterTestUtil.Flate(FilterTestUtil.Zlib(data), null, new DecodeLimits(data.Length, 0, 0));
        Assert.True(r.IsClean, r.Message);
        Assert.Equal(data.Length, r.Data.Length);
    }

    [Fact]
    public void DecompressionBombIsStoppedByTheRatioBeforeItGrows()
    {
        // The input is streamed through the compressor in 1 MiB pieces: no 200 MB array exists at any time.
        byte[] z;
        using (var ms = new MemoryStream())
        {
            using (var zs = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                byte[] zeros = new byte[1 << 20];
                for (int i = 0; i < 200; i++)
                {
                    zs.Write(zeros);
                }
            }

            z = ms.ToArray();
        }

        Assert.True(z.Length < 300_000);
        // Default limits: 1100x applies past 16 MiB; deflate tops out near 1032x so only the cap can stop this one,
        // which is 256 MiB and not hit. Use a tighter ratio to show the ratio check runs and bounds the allocation.
        var limits = new DecodeLimits(1L << 30, 100, 1 << 20);
        // Per-thread count: other tests running in parallel do not add to it (the decode runs on this thread).
        long before = GC.GetAllocatedBytesForCurrentThread();
        FilterResult r = FilterTestUtil.Flate(z, null, limits);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True((r.Issues & FilterIssues.LimitExceeded) != 0);
        long allowed = (long)(100.0 * z.Length) + (1 << 20);
        Assert.True(r.Data.Length <= allowed + (1 << 18), "produced " + r.Data.Length);
        Assert.True(allocated < allowed * 4 + (8 << 20), "allocated " + allocated);
    }

    [Fact]
    public void RatioIsIgnoredBelowTheFloor()
    {
        byte[] z = FilterTestUtil.Zlib(new byte[1_000_000]);
        Assert.True(FilterTestUtil.Flate(z, null, new DecodeLimits(1L << 30, 10, 2_000_000)).IsClean);
        Assert.True((FilterTestUtil.Flate(z, null, new DecodeLimits(1L << 30, 10, 0)).Issues & FilterIssues.LimitExceeded) != 0);
    }

    [Fact]
    public void CancellationIsObserved()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        byte[] z = FilterTestUtil.Zlib(new byte[100]);
        Assert.Throws<OperationCanceledException>(() => StreamFilters.Decode("FlateDecode", z, null, DecodeLimits.Default, cts.Token));
    }

    [Fact]
    public void ConcurrentDecodesAgree()
    {
        byte[] data = FilterTestUtil.Texty(400_000, 14);
        byte[] z = FilterTestUtil.Zlib(data);
        Parallel.For(0, 64, _ => Assert.True(data.AsSpan().SequenceEqual(FilterTestUtil.Flate(z).Data.Span)));
    }
}

public class LzwTests
{
    private static FilterResult Lzw(byte[] input, int early = 1, DecodeLimits? limits = null) =>
        StreamFilters.Decode("LZWDecode", input, new FilterParameters(EarlyChange: early), limits ?? DecodeLimits.Default);

    [Fact]
    public void SpecExampleDecodes()
    {
        // ISO 32000-2 7.4.4.2 example: 45 45 45 45 45 65 45 45 45 66.
        byte[] enc = [0x80, 0x0B, 0x60, 0x50, 0x22, 0x0C, 0x0C, 0x85, 0x01];
        FilterResult r = Lzw(enc);
        Assert.True(r.IsClean, r.Message);
        Assert.Equal(new byte[] { 45, 45, 45, 45, 45, 65, 45, 45, 45, 66 }, r.Data.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void RoundTripsAcrossWidthChangesAndTableResets(int early)
    {
        foreach (int n in new[] { 0, 1, 10, 600, 5000, 200_000 })
        {
            foreach (byte[] data in new[] { FilterTestUtil.Random(n, 20), FilterTestUtil.Texty(n, 21), new byte[n] })
            {
                FilterResult r = Lzw(FilterTestUtil.LzwEncode(data, early), early);
                Assert.True(r.IsClean, $"early={early} n={n}: {r.Message}");
                Assert.True(data.AsSpan().SequenceEqual(r.Data.Span), $"early={early} n={n}");
            }
        }
    }

    [Fact]
    public void WrongEarlyChangeDoesNotMatchTheData()
    {
        byte[] data = FilterTestUtil.Texty(50_000, 22);
        byte[] enc = FilterTestUtil.LzwEncode(data, 1);
        FilterResult r = Lzw(enc, 0);
        Assert.False(data.AsSpan().SequenceEqual(r.Data.Span));
    }

    [Fact]
    public void TableFullWithoutClearKeepsDecoding()
    {
        byte[] data = FilterTestUtil.Texty(300_000, 23);
        FilterResult r = Lzw(FilterTestUtil.LzwEncode(data, 1, clearWhenFull: false));
        Assert.True(r.IsClean, r.Message);
        Assert.True(data.AsSpan().SequenceEqual(r.Data.Span));
    }

    [Fact]
    public void MissingEodAndTruncationReturnPrefix()
    {
        byte[] data = FilterTestUtil.Texty(50_000, 24);
        byte[] enc = FilterTestUtil.LzwEncode(data, 1);
        FilterResult r = Lzw(enc.AsSpan(0, enc.Length / 2).ToArray());
        Assert.True((r.Issues & FilterIssues.MissingEndOfData) != 0);
        Assert.True(r.Data.Length > 10_000);
        Assert.True(data.AsSpan(0, r.Data.Length).SequenceEqual(r.Data.Span));
    }

    [Fact]
    public void BadCodeKeepsPrefixAndFlagsError()
    {
        // clear, 'A', then code 400 (not yet defined).
        byte[] enc = [0x80, 0x10, 0x72, 0x00];
        FilterResult r = Lzw(enc);
        Assert.True((r.Issues & FilterIssues.DataError) != 0);
        Assert.Equal(new byte[] { 0x41 }.Length, r.Data.Length);
    }

    [Fact]
    public void GarbageNeverThrows()
    {
        for (int i = 0; i < 300; i++)
        {
            _ = Lzw(FilterTestUtil.Random(i, i), i & 1);
        }
    }

    [Fact]
    public void BombIsStopped()
    {
        byte[] data = new byte[20_000_000];
        byte[] enc = FilterTestUtil.LzwEncode(data, 1);
        FilterResult r = Lzw(enc, 1, new DecodeLimits(1L << 30, 50, 1 << 20));
        Assert.True((r.Issues & FilterIssues.LimitExceeded) != 0);
        Assert.True(r.Data.Length < (50L * enc.Length) + (1 << 20) + 5000);
        FilterResult cap = Lzw(enc, 1, new DecodeLimits(1_000_000, 0, 0));
        Assert.True((cap.Issues & FilterIssues.LimitExceeded) != 0);
        Assert.True(cap.Data.Length <= 1_000_000);
    }
}

public class AsciiFilterTests
{
    private static FilterResult Run(string name, string text) =>
        StreamFilters.Decode(name, Encoding.Latin1.GetBytes(text), null, DecodeLimits.Default);

    [Fact]
    public void HexBasics()
    {
        FilterResult r = Run("ASCIIHexDecode", "48 65\n6C6c 6F>");
        Assert.True(r.IsClean);
        Assert.Equal("Hello", Encoding.ASCII.GetString(r.Data.Span));
    }

    [Fact]
    public void HexOddDigitPadsWithZero()
    {
        Assert.Equal(new byte[] { 0x7A, 0x30 }, Run("AHx", "7A3>").Data.ToArray());
    }

    [Fact]
    public void HexMissingEodAndBadCharsAreFlagged()
    {
        FilterResult r = Run("ASCIIHexDecode", "41g42");
        Assert.Equal(new byte[] { 0x41, 0x42 }, r.Data.ToArray());
        Assert.Equal(FilterIssues.DataError | FilterIssues.MissingEndOfData, r.Issues);
    }

    [Fact]
    public void HexDataAfterEodIsIgnored()
    {
        FilterResult r = Run("ASCIIHexDecode", "41>4242");
        Assert.Equal(new byte[] { 0x41 }, r.Data.ToArray());
        Assert.Equal(FilterIssues.TrailingData, r.Issues);
    }

    private static string Enc85(byte[] data, bool eod = true)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < data.Length; i += 4)
        {
            int n = Math.Min(4, data.Length - i);
            uint v = 0;
            for (int k = 0; k < 4; k++)
            {
                v = (v << 8) | (k < n ? data[i + k] : (byte)0);
            }

            if (n == 4 && v == 0)
            {
                sb.Append('z');
                continue;
            }

            var c = new char[5];
            for (int k = 4; k >= 0; k--)
            {
                c[k] = (char)('!' + (v % 85));
                v /= 85;
            }

            sb.Append(c, 0, n + 1);
        }

        return eod ? sb + "~>" : sb.ToString();
    }

    [Fact]
    public void Ascii85RoundTripsAllTailLengths()
    {
        for (int n = 0; n < 40; n++)
        {
            byte[] data = FilterTestUtil.Random(n, 30 + n);
            FilterResult r = Run("ASCII85Decode", Enc85(data));
            Assert.True(r.IsClean, r.Message);
            Assert.True(data.AsSpan().SequenceEqual(r.Data.Span), "n=" + n);
        }
    }

    [Fact]
    public void Ascii85ZeroGroupsAndWhitespace()
    {
        byte[] data = [0, 0, 0, 0, 1, 2, 3, 4, 0, 0, 0, 0, 0, 0];
        string enc = Enc85(data);
        Assert.Contains('z', enc);
        string spaced = string.Join(" \r\n\t", enc.Select(ch => ch.ToString()));
        FilterResult r = Run("A85", spaced);
        Assert.Equal(data, r.Data.ToArray());
        Assert.True(r.IsClean, r.Message);
    }

    [Fact]
    public void Ascii85KnownVector()
    {
        Assert.Equal("Man ", Encoding.ASCII.GetString(Run("ASCII85Decode", "9jqo^~>").Data.Span));
        Assert.Equal("Ma", Encoding.ASCII.GetString(Run("ASCII85Decode", "9jn~>").Data.Span));
    }

    [Fact]
    public void Ascii85MissingEodAndPrefix()
    {
        FilterResult r = Run("ASCII85Decode", "<~9jqo^");
        Assert.Equal("Man ", Encoding.ASCII.GetString(r.Data.Span));
        Assert.Equal(FilterIssues.MissingEndOfData, r.Issues);
        Assert.Equal("Man ", Encoding.ASCII.GetString(Run("ASCII85Decode", "9jqo^~").Data.Span));
    }

    [Fact]
    public void Ascii85BadInputIsFlaggedNotFatal()
    {
        FilterResult single = Run("ASCII85Decode", "9jqo^9~>");
        Assert.Equal("Man ", Encoding.ASCII.GetString(single.Data.Span));
        Assert.True((single.Issues & FilterIssues.DataError) != 0);
        FilterResult overflow = Run("ASCII85Decode", "uuuuu~>");
        Assert.True((overflow.Issues & FilterIssues.DataError) != 0);
        Assert.True((Run("ASCII85Decode", "9jqo^\u00e9~>").Issues & FilterIssues.DataError) != 0);
        Assert.True((Run("ASCII85Decode", "9jqz^~>").Issues & FilterIssues.DataError) != 0);
    }

    [Fact]
    public void RunLengthBasics()
    {
        byte[] enc = [2, 1, 2, 3, 254, 9, 0, 7, 128, 99];
        FilterResult r = StreamFilters.Decode("RunLengthDecode", enc, null, DecodeLimits.Default);
        Assert.Equal(new byte[] { 1, 2, 3, 9, 9, 9, 7 }, r.Data.ToArray());
        Assert.Equal(FilterIssues.TrailingData, r.Issues);
        Assert.True(StreamFilters.Decode("RL", [0, 5, 128], null, DecodeLimits.Default).IsClean);
    }

    [Fact]
    public void RunLengthTruncationAndMissingEod()
    {
        FilterResult lit = StreamFilters.Decode("RL", [5, 1, 2], null, DecodeLimits.Default);
        Assert.Equal(new byte[] { 1, 2 }, lit.Data.ToArray());
        Assert.True((lit.Issues & FilterIssues.Truncated) != 0);
        FilterResult rep = StreamFilters.Decode("RL", [0, 4, 200], null, DecodeLimits.Default);
        Assert.Equal(new byte[] { 4 }, rep.Data.ToArray());
        Assert.True((rep.Issues & FilterIssues.Truncated) != 0);
        Assert.Equal(FilterIssues.MissingEndOfData, StreamFilters.Decode("RL", [0, 4], null, DecodeLimits.Default).Issues);
    }

    [Fact]
    public void RunLengthCapStopsExpansion()
    {
        byte[] enc = new byte[20_000];
        for (int i = 0; i < enc.Length; i += 2)
        {
            enc[i] = 129;
        }

        FilterResult r = StreamFilters.Decode("RunLengthDecode", enc, null, new DecodeLimits(100_000, 0, 0));
        Assert.True((r.Issues & FilterIssues.LimitExceeded) != 0);
        Assert.True(r.Data.Length <= 100_000);
    }

    [Fact]
    public void ChainAsciiThenFlateThenStopsAtImageFilter()
    {
        byte[] data = FilterTestUtil.Texty(10_000, 40);
        byte[] z = FilterTestUtil.Zlib(data);
        byte[] hex = Encoding.ASCII.GetBytes(Convert.ToHexString(z) + ">");
        FilterResult r = StreamFilters.DecodeChain(["AHx", "Fl"], null, hex, DecodeLimits.Default);
        Assert.True(r.IsClean, r.Message);
        Assert.Equal(-1, r.StoppedAtFilter);
        Assert.True(data.AsSpan().SequenceEqual(r.Data.Span));

        FilterResult img = StreamFilters.DecodeChain(["ASCIIHexDecode", "FlateDecode", "DCTDecode"], null, hex, DecodeLimits.Default);
        Assert.Equal(2, img.StoppedAtFilter);
        Assert.True((img.Issues & FilterIssues.UnsupportedFilter) != 0);
        Assert.True(data.AsSpan().SequenceEqual(img.Data.Span));
    }

    [Fact]
    public void ChainWithPerFilterParameters()
    {
        byte[] raw = FilterTestUtil.Texty(64 * 20, 41);
        byte[] png = FilterTestUtil.PngEncode(raw, 64, 1, r => 1 + (r % 4));
        byte[] chained = FilterTestUtil.Zlib(png);
        byte[] a85 = Encoding.ASCII.GetBytes(Convert.ToBase64String(chained)); // not a85: use hex instead
        _ = a85;
        byte[] hex = Encoding.ASCII.GetBytes(Convert.ToHexString(chained) + ">");
        FilterResult r = StreamFilters.DecodeChain(
            ["ASCIIHexDecode", "FlateDecode"],
            [null, new FilterParameters(Predictor: 15, Columns: 64)],
            hex,
            DecodeLimits.Default);
        Assert.True(r.IsClean, r.Message);
        Assert.True(raw.AsSpan().SequenceEqual(r.Data.Span));
    }
}

public class PredictorTests
{
    [Theory]
    [InlineData(1, 8, 17)]
    [InlineData(3, 8, 40)]
    [InlineData(4, 8, 13)]
    [InlineData(1, 16, 9)]
    [InlineData(3, 1, 100)]
    [InlineData(4, 4, 23)]
    [InlineData(1, 2, 31)]
    public void PngPredictorsRoundTripThroughFlateAndLzw(int colors, int bpc, int columns)
    {
        int rowBytes = ((colors * bpc * columns) + 7) / 8;
        int bpp = Math.Max(1, ((colors * bpc) + 7) / 8);
        byte[] raw = FilterTestUtil.Texty(rowBytes * 37, colors + bpc);
        foreach (int fixedTag in new[] { -1, 0, 1, 2, 3, 4 })
        {
            byte[] enc = FilterTestUtil.PngEncode(raw, rowBytes, bpp, r => fixedTag < 0 ? r % 5 : fixedTag);
            var p = new FilterParameters(Predictor: 12, Colors: colors, BitsPerComponent: bpc, Columns: columns);
            FilterResult f = StreamFilters.Decode("FlateDecode", FilterTestUtil.Zlib(enc), p, DecodeLimits.Default);
            Assert.True(f.IsClean, f.Message);
            Assert.True(raw.AsSpan().SequenceEqual(f.Data.Span), $"flate tag={fixedTag}");
            FilterResult l = StreamFilters.Decode("LZWDecode", FilterTestUtil.LzwEncode(enc, 1), p, DecodeLimits.Default);
            Assert.True(raw.AsSpan().SequenceEqual(l.Data.Span), $"lzw tag={fixedTag}");
        }
    }

    [Theory]
    [InlineData(1, 8, 50)]
    [InlineData(3, 8, 21)]
    [InlineData(4, 8, 7)]
    [InlineData(1, 16, 33)]
    [InlineData(3, 16, 11)]
    [InlineData(1, 1, 45)]
    [InlineData(3, 2, 13)]
    [InlineData(1, 4, 19)]
    [InlineData(2, 4, 9)]
    public void TiffPredictorRoundTrips(int colors, int bpc, int columns)
    {
        int rowBytes = ((colors * bpc * columns) + 7) / 8;
        byte[] raw = FilterTestUtil.Random(rowBytes * 25, colors * bpc);
        // Padding bits at the end of each row are not samples; clear them so the comparison is exact.
        int usedBits = colors * bpc * columns;
        if ((usedBits & 7) != 0)
        {
            for (int r = 0; r < 25; r++)
            {
                raw[(r * rowBytes) + rowBytes - 1] &= (byte)(0xFF << (8 - (usedBits & 7)));
            }
        }

        byte[] enc = FilterTestUtil.TiffEncode(raw, colors, bpc, columns);
        var p = new FilterParameters(Predictor: 2, Colors: colors, BitsPerComponent: bpc, Columns: columns);
        FilterResult f = StreamFilters.Decode("Fl", FilterTestUtil.Zlib(enc), p, DecodeLimits.Default);
        Assert.True(f.IsClean, f.Message);
        Assert.True(raw.AsSpan().SequenceEqual(f.Data.Span));
    }

    [Fact]
    public void PartialLastRowIsDecodedAndFlagged()
    {
        byte[] raw = FilterTestUtil.Texty(10 * 5 + 4, 50);
        byte[] enc = FilterTestUtil.PngEncode(raw, 10, 1, r => 2);
        FilterResult f = StreamFilters.Decode("Fl", FilterTestUtil.Zlib(enc), new FilterParameters(Predictor: 12, Columns: 10), DecodeLimits.Default);
        Assert.True(raw.AsSpan().SequenceEqual(f.Data.Span));
        Assert.True((f.Issues & FilterIssues.Truncated) != 0);
    }

    [Fact]
    public void UnknownRowTagKeepsRowAndFlags()
    {
        byte[] enc = [9, 1, 2, 3];
        FilterResult f = StreamFilters.Decode("Fl", FilterTestUtil.Zlib(enc), new FilterParameters(Predictor: 10, Columns: 3), DecodeLimits.Default);
        Assert.Equal(new byte[] { 1, 2, 3 }, f.Data.ToArray());
        Assert.True((f.Issues & FilterIssues.DataError) != 0);
    }

    [Theory]
    [InlineData(5, 1, 8, 10)]
    [InlineData(12, 0, 8, 10)]
    [InlineData(12, 1, 3, 10)]
    [InlineData(12, 1, 8, 0)]
    [InlineData(12, 255, 16, int.MaxValue)]
    public void BadParametersLeaveDataUntouched(int predictor, int colors, int bpc, int columns)
    {
        byte[] data = FilterTestUtil.Texty(100, 51);
        FilterResult f = StreamFilters.Decode("Fl", FilterTestUtil.Zlib(data), new FilterParameters(predictor, colors, bpc, columns), DecodeLimits.Default);
        Assert.True((f.Issues & FilterIssues.BadParameters) != 0);
        Assert.True(data.AsSpan().SequenceEqual(f.Data.Span));
    }

    [Fact]
    public void Predictor10To15AllBehaveTheSame()
    {
        byte[] raw = FilterTestUtil.Texty(8 * 9, 52);
        byte[] enc = FilterTestUtil.PngEncode(raw, 8, 1, r => r % 5);
        for (int pr = 10; pr <= 15; pr++)
        {
            FilterResult f = StreamFilters.Decode("Fl", FilterTestUtil.Zlib(enc), new FilterParameters(Predictor: pr, Columns: 8), DecodeLimits.Default);
            Assert.True(raw.AsSpan().SequenceEqual(f.Data.Span), "predictor " + pr);
        }
    }
}

public class UnsupportedFilterTests
{
    [Theory]
    [InlineData("DCTDecode")]
    [InlineData("Crypt")]
    [InlineData("Bogus")]
    public void ReportedNotThrown(string name)
    {
        FilterResult r = StreamFilters.Decode(name, [1, 2, 3], null, DecodeLimits.Default);
        Assert.True((r.Issues & FilterIssues.UnsupportedFilter) != 0);
        Assert.False(StreamFilters.IsSupported(name));
    }
}
