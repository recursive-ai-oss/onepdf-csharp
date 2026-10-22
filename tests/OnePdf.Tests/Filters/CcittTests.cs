using System.Runtime.CompilerServices;
using System.Text;
using OnePdf.Filters;

namespace OnePdf.Tests.Filters;

public class CcittTests
{
    private static string FixtureDir([CallerFilePath] string source = "") => Path.Combine(Path.GetDirectoryName(source)!, "ccitt");

    public static IEnumerable<TheoryDataRow<string>> Fixtures()
    {
        foreach (string f in Directory.EnumerateFiles(FixtureDir(), "*.bin").Order(StringComparer.Ordinal))
        {
            yield return new TheoryDataRow<string>(Path.GetFileName(f));
        }
    }

    // File name: <name>_<w>x<h>.<mode>.bin
    private static (string Truth, int W, int H, string Mode) Describe(string file)
    {
        string[] parts = file.Split('.');
        string[] size = parts[0][(parts[0].LastIndexOf('_') + 1)..].Split('x');
        return (Path.Combine(FixtureDir(), parts[0] + ".truth"), int.Parse(size[0]), int.Parse(size[1]), parts[1]);
    }

    private static FilterParameters For(string mode, int w, int h, bool blackIs1, bool useRows = true) => mode switch
    {
        "g4" => new FilterParameters(K: -1, CcittColumns: w, Rows: useRows ? h : 0, BlackIs1: blackIs1),
        "g3-2d" => new FilterParameters(K: 4, EndOfLine: true, CcittColumns: w, Rows: useRows ? h : 0, BlackIs1: blackIs1),
        "g3-1d-fill" => new FilterParameters(K: 0, EndOfLine: true, CcittColumns: w, Rows: useRows ? h : 0, BlackIs1: blackIs1),
        _ => new FilterParameters(K: 0, CcittColumns: w, Rows: useRows ? h : 0, BlackIs1: blackIs1),
    };

    // Compares pixel bits only; the padding bits of each row are not part of the picture.
    private static void AssertRows(byte[] truthBlack1, ReadOnlySpan<byte> actual, int w, int h, bool blackIs1, string what)
    {
        int rb = (w + 7) / 8;
        Assert.True(actual.Length == rb * h, $"{what}: {actual.Length} bytes, expected {rb * h}");
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int t = (truthBlack1[(y * rb) + (x >> 3)] >> (7 - (x & 7))) & 1;
                int a = (actual[(y * rb) + (x >> 3)] >> (7 - (x & 7))) & 1;
                if (!blackIs1)
                {
                    a ^= 1;
                }

                Assert.True(t == a, $"{what}: pixel ({x},{y}) is {a} but should be {t}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void LibtiffFixturesDecodeExactly(string file)
    {
        (string truthPath, int w, int h, string mode) = Describe(file);
        byte[] data = File.ReadAllBytes(Path.Combine(FixtureDir(), file));
        byte[] truth = File.ReadAllBytes(truthPath);
        foreach (bool blackIs1 in new[] { true, false })
        {
            foreach (bool useRows in new[] { true, false })
            {
                FilterResult r = StreamFilters.Decode("CCITTFaxDecode", data, For(mode, w, h, blackIs1, useRows), DecodeLimits.Default);
                Assert.True((r.Issues & ~FilterIssues.Truncated) == 0, file + ": " + r.Message);
                AssertRows(truth, r.Data.Span, w, h, blackIs1, file);
            }
        }
    }

    [Fact]
    public void LibtiffFixturesAreComplete() => Assert.True(Fixtures().Count() >= 32);

    [Fact]
    public void SpecWorkedGroup4Rows()
    {
        // 8 white pixels: V0 ("1"), then EOFB (two EOLs).
        FilterResult white = StreamFilters.Decode("CCF", [0x80, 0x08, 0x00, 0x80], new FilterParameters(K: -1, CcittColumns: 8), DecodeLimits.Default);
        Assert.True(white.IsClean, white.Message);
        Assert.Equal(new byte[] { 0xFF }, white.Data.ToArray());

        // 3 black then 5 white: horizontal mode (001), white run 0 (00110101), black run 3 (10), then V0 to the edge (1).
        FilterResult mixed = StreamFilters.Decode("CCITTFaxDecode", [0x26, 0xB4], new FilterParameters(K: -1, CcittColumns: 8), DecodeLimits.Default);
        Assert.True(mixed.IsClean, mixed.Message);
        Assert.Equal(new byte[] { 0x1F }, mixed.Data.ToArray());
        FilterResult inverted = StreamFilters.Decode("CCITTFaxDecode", [0x26, 0xB4], new FilterParameters(K: -1, CcittColumns: 8, BlackIs1: true), DecodeLimits.Default);
        Assert.Equal(new byte[] { 0xE0 }, inverted.Data.ToArray());
    }

    // A small Group 3 one-dimensional encoder for the layout options libtiff cannot produce.
    private static byte[] Encode1D(bool[][] rows, bool eol, bool byteAlign, bool rtc)
    {
        var bits = new StringBuilder();
        void Align()
        {
            while (bits.Length % 8 != 0)
            {
                bits.Append('0');
            }
        }

        foreach (bool[] row in rows)
        {
            if (eol)
            {
                if (byteAlign)
                {
                    // Fill so the EOL ends on a byte boundary.
                    while ((bits.Length + 12) % 8 != 0)
                    {
                        bits.Append('0');
                    }
                }

                bits.Append("000000000001");
            }
            else if (byteAlign)
            {
                Align();
            }

            bool black = false;
            int x = 0;
            while (x < row.Length)
            {
                int run = 0;
                while (x + run < row.Length && row[x + run] == black)
                {
                    run++;
                }

                int left = run;
                while (left > 2560)
                {
                    bits.Append(CcittFax.CodeFor(black, 2560));
                    left -= 2560;
                }

                bits.Append(CcittFax.CodeFor(black, left));
                x += run;
                black = !black;
            }
        }

        if (rtc)
        {
            for (int i = 0; i < 6; i++)
            {
                bits.Append("000000000001");
            }
        }

        Align();
        var bytes = new byte[bits.Length / 8];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(bits.ToString(i * 8, 8), 2);
        }

        return bytes;
    }

    private static bool[][] Picture(int w, int h, int seed)
    {
        var r = new Random(seed);
        var rows = new bool[h][];
        for (int y = 0; y < h; y++)
        {
            rows[y] = new bool[w];
            int x = 0;
            bool black = false;
            while (x < w)
            {
                int run = r.Next(1, y % 3 == 0 ? 400 : 20);
                for (int i = 0; i < run && x < w; i++)
                {
                    rows[y][x++] = black;
                }

                black = !black;
            }
        }

        return rows;
    }

    private static byte[] Pack(bool[][] rows, int w)
    {
        int rb = (w + 7) / 8;
        var o = new byte[rb * rows.Length];
        for (int y = 0; y < rows.Length; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (rows[y][x])
                {
                    o[(y * rb) + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }
            }
        }

        return o;
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void GroupThreeLayoutOptions(bool eol, bool byteAlign, bool rtc)
    {
        const int W = 1000;
        const int H = 25;
        bool[][] pic = Picture(W, H, 11);
        byte[] enc = Encode1D(pic, eol, byteAlign, rtc);
        var p = new FilterParameters(K: 0, EndOfLine: eol, EncodedByteAlign: byteAlign, CcittColumns: W, Rows: 0, BlackIs1: true);
        FilterResult r = StreamFilters.Decode("CCITTFaxDecode", enc, p, DecodeLimits.Default);
        Assert.True(r.IsClean, r.Message);
        AssertRows(Pack(pic, W), r.Data.Span, W, H, true, "layout");
    }

    [Fact]
    public void RunsLongerThanTheRowAreDamageNotACrash()
    {
        // A white run of 64 in an 8 pixel row.
        byte[] enc = Encode1D([new bool[64]], eol: true, byteAlign: false, rtc: false);
        FilterResult r = StreamFilters.Decode("CCITTFaxDecode", enc, new FilterParameters(K: 0, CcittColumns: 8, EndOfLine: true), DecodeLimits.Default);
        Assert.True((r.Issues & FilterIssues.DataError) != 0);
        Assert.Equal(1, r.Data.Length);
    }

    [Fact]
    public void DamagedGroup3RowsAreRepairedAndLaterRowsSurvive()
    {
        string file = "rects_200x60.g3-1d.bin";
        byte[] data = File.ReadAllBytes(Path.Combine(FixtureDir(), file));
        byte[] truth = File.ReadAllBytes(Path.Combine(FixtureDir(), "rects_200x60.truth"));
        int rb = 25;
        var rnd = new Random(5);
        for (int trial = 0; trial < 20; trial++)
        {
            byte[] bad = (byte[])data.Clone();
            int at = rnd.Next(10, bad.Length / 3);
            bad[at] ^= (byte)(1 << rnd.Next(8));
            bad[at + 1] = 0xFF;
            FilterResult r = StreamFilters.Decode("CCITTFaxDecode", bad, For("g3-1d", 200, 60, true), DecodeLimits.Default);
            // Damage that swallows an EOL merges two rows into one, so a row or two may be lost, but decoding goes on to the end.
            Assert.Equal(0, r.Data.Length % rb);
            int rows = r.Data.Length / rb;
            Assert.InRange(rows, 55, 60);
            Assert.True(truth.AsSpan(rb * 50, rb * 10).SequenceEqual(r.Data.Span.Slice((rows - 10) * rb, rb * 10)), "tail rows differ in trial " + trial);
        }
    }

    [Fact]
    public void DamagedGroup3FlagsAndKeepsGoing()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(FixtureDir(), "noise_37x30.g3-1d.bin"));
        byte[] bad = (byte[])data.Clone();
        for (int i = 40; i < 46; i++)
        {
            bad[i] = 0x00; // a long run of zeros is no valid code
        }

        FilterResult r = StreamFilters.Decode("CCITTFaxDecode", bad, For("g3-1d", 37, 30, true), DecodeLimits.Default);
        Assert.True((r.Issues & FilterIssues.DataError) != 0, r.Message);
        Assert.True(r.Data.Length >= 5 * 20, "decoding should continue past the damage");
    }

    [Fact]
    public void TruncatedGroup4KeepsDecodedRows()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(FixtureDir(), "rects_200x60.g4.bin"));
        byte[] truth = File.ReadAllBytes(Path.Combine(FixtureDir(), "rects_200x60.truth"));
        byte[] cut = data.AsSpan(0, data.Length / 2).ToArray();
        FilterResult r = StreamFilters.Decode("CCITTFaxDecode", cut, For("g4", 200, 60, true), DecodeLimits.Default);
        Assert.True((r.Issues & FilterIssues.Truncated) != 0);
        int rows = r.Data.Length / 25;
        Assert.InRange(rows, 5, 59);
        // Every row but the one cut off is exact.
        Assert.True(truth.AsSpan(0, (rows - 1) * 25).SequenceEqual(r.Data.Span.Slice(0, (rows - 1) * 25)));
    }

    [Fact]
    public void GarbageNeverThrowsAndStaysWithinTheCap()
    {
        var rnd = new Random(9);
        var limits = new DecodeLimits(1 << 16, 0, 0);
        for (int i = 0; i < 400; i++)
        {
            byte[] junk = new byte[rnd.Next(0, 300)];
            rnd.NextBytes(junk);
            var p = new FilterParameters(K: rnd.Next(-1, 3), EndOfLine: rnd.Next(2) == 0, EncodedByteAlign: rnd.Next(2) == 0, CcittColumns: rnd.Next(1, 5000), Rows: rnd.Next(0, 100), BlackIs1: rnd.Next(2) == 0);
            FilterResult r = StreamFilters.Decode("CCITTFaxDecode", junk, p, limits);
            Assert.True(r.Data.Length <= limits.MaxDecodedBytes);
        }
    }

    [Fact]
    public void RowsTimesColumnsAreCheckedAgainstTheCap()
    {
        // All-white Group 4 rows cost one bit each, so a small input asks for far more output than the cap allows.
        byte[] ones = new byte[2000];
        Array.Fill(ones, (byte)0xFF);
        var limits = new DecodeLimits(100_000, 0, 0);
        FilterResult r = StreamFilters.Decode("CCITTFaxDecode", ones, new FilterParameters(K: -1, CcittColumns: 4096, Rows: 1_000_000), limits);
        Assert.True((r.Issues & FilterIssues.LimitExceeded) != 0);
        Assert.Equal(100_000 / 512 * 512, r.Data.Length);

        // A row wider than the cap allows: nothing is allocated, nothing is output.
        FilterResult wide = StreamFilters.Decode("CCITTFaxDecode", ones, new FilterParameters(K: -1, CcittColumns: 1 << 24), new DecodeLimits(1000, 0, 0));
        Assert.True((wide.Issues & FilterIssues.LimitExceeded) != 0);
        Assert.Equal(0, wide.Data.Length);

        FilterResult huge = StreamFilters.Decode("CCITTFaxDecode", ones, new FilterParameters(K: -1, CcittColumns: int.MaxValue), DecodeLimits.Default);
        Assert.True((huge.Issues & FilterIssues.BadParameters) != 0);
    }

    [Fact]
    public void ChainDecodesFlateThenCcitt()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(FixtureDir(), "stripes_129x40.g4.bin"));
        byte[] truth = File.ReadAllBytes(Path.Combine(FixtureDir(), "stripes_129x40.truth"));
        byte[] z = FilterTestUtil.Zlib(data);
        FilterResult r = StreamFilters.DecodeChain(["FlateDecode", "CCF"], [null, For("g4", 129, 40, true)], z, DecodeLimits.Default);
        Assert.Equal(-1, r.StoppedAtFilter);
        Assert.True(StreamFilters.IsSupported("CCITTFaxDecode"));
        AssertRows(truth, r.Data.Span, 129, 40, true, "chain");
    }

    [Fact]
    public void StreamObjectDecodesWithDecodeParms()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(FixtureDir(), "rects_200x60.g4.bin"));
        byte[] truth = File.ReadAllBytes(Path.Combine(FixtureDir(), "rects_200x60.truth"));
        var b = new PdfBuilder();
        b.BasicPages();
        b.Stream(4, "/Filter /CCITTFaxDecode /DecodeParms << /K -1 /Columns 200 /Rows 60 /BlackIs1 true >>", data);
        b.Stream(5, "/Filter [/CCF] /DecodeParms [<< /K -1 /Columns 200 >>]", data);
        long x = b.XrefTable(6);
        b.Trailer("/Size 6 /Root 1 0 R", x);
        using PdfDocument doc = PdfDocument.Open(b.ToArray());
        var s4 = Assert.IsType<PdfStream>(doc.GetObject(4));
        Assert.Null(s4.UndecodedFilter);
        AssertRows(truth, s4.GetDecodedBytes().Span, 200, 60, true, "stream with BlackIs1");
        var s5 = Assert.IsType<PdfStream>(doc.GetObject(5));
        AssertRows(truth, s5.GetDecodedBytes().Span, 200, 60, false, "stream with default polarity");
    }
}
