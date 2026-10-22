using System.Text;
using OnePdf.Filters;
using OnePdf.Tests.Content;
using OnePdf.Tests.Images;

namespace OnePdf.Tests.Filters;

/// <summary>Shared /DecodeParms reading, and the expected-size hint for large Flate output.</summary>
public class FilterParametersAndSizeHintTests
{
    // ---- /DecodeParms ----

    [Fact]
    public void CcittColumnsDefaultTo1728AndOtherFiltersTo1()
    {
        var empty = new PdfDictionary(null);
        Assert.Equal(1728, FilterParametersReader.From(empty, "CCITTFaxDecode")!.CcittColumns);
        Assert.Equal(1, FilterParametersReader.From(empty, "FlateDecode")!.Columns);
        var set = new PdfDictionary(null);
        set.Set("Columns", new PdfInteger(100));
        Assert.Equal(100, FilterParametersReader.From(set, "CCITTFaxDecode")!.CcittColumns);
        Assert.Null(FilterParametersReader.From(null, "CCITTFaxDecode"));
    }

    [Fact]
    public void ParameterArrayIsParallelToTheFilters()
    {
        var first = new PdfDictionary(null);
        first.Set("Predictor", new PdfInteger(12));
        PdfDictionary? at0 = FilterParametersReader.DictionaryAt(first, 0);
        Assert.Same(first, at0);
        Assert.Null(FilterParametersReader.DictionaryAt(first, 1));
        Assert.Null(FilterParametersReader.DictionaryAt(new PdfArray(null), 0));
        Assert.Null(FilterParametersReader.DictionaryAt(null, 0));
    }

    // Three all-white Group 4 rows are the code '1' (V0) three times: 0b111 padded.
    private static readonly byte[] WhiteRows = [0xE0];

    [Fact]
    public void CcittWithoutColumnsIs1728WideOnStreamsAndInlineImages()
    {
        // Stream path: /K -1 and no /Columns still decodes three rows of 216 bytes, all white (1 bits).
        using (ImageKit.Result viaStream = ImageKit.Make("/Width 1728 /Height 3 /ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /CCITTFaxDecode /DecodeParms << /K -1 >>", WhiteRows))
        {
            Assert.Equal(1728 * 3, viaStream.Img.Samples.Length);
            Assert.All(viaStream.Img.Samples.ToArray(), v => Assert.Equal(255, v));
        }

        // Inline path: the same image through BI/ID/EI with the abbreviated keys.
        string content = "BI /W 1728 /H 3 /BPC 1 /CS /G /F /CCF /DP << /K -1 >> ID \u00e0 EI";
        string withColumns = content.Replace("/K -1", "/K -1 /Columns 1728", StringComparison.Ordinal);
        string narrower = content.Replace("/K -1", "/K -1 /Columns 8", StringComparison.Ordinal);
        string implied = Image(content);
        Assert.Contains("decoded:1728x3:gray:", implied, StringComparison.Ordinal);
        Assert.Equal(Image(withColumns), implied);
        Assert.NotEqual(Image(narrower), implied);
    }

    // The finished-image field of the image record (the dictionary printed after it differs between the variants).
    private static string Image(string content)
    {
        string line = TestPdf.Lines(TestPdf.Text(TestPdf.Build(content)), "image").Single();
        int at = line.IndexOf("data=", StringComparison.Ordinal);
        return line.Substring(at, line.IndexOf(' ', at) - at);
    }

    // ---- expected-size hint ----

    private static readonly DecodeLimits Limits = DecodeLimits.Default;

    private static long Allocated(Func<FilterResult> run, out FilterResult result)
    {
        run(); // warm up pools and tables
        long before = GC.GetAllocatedBytesForCurrentThread();
        result = run();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void ExactHintAllocatesOnceAndGivesTheSameBytes()
    {
        byte[] raw = FilterTestUtil.Texty(6 << 20, 1);
        byte[] z = FilterTestUtil.Zlib(raw);
        long plain = Allocated(() => StreamFilters.Decode("FlateDecode", z, null, Limits), out FilterResult a);
        long hinted = Allocated(() => StreamFilters.Decode("FlateDecode", z, null, Limits, raw.Length), out FilterResult b);
        Assert.True(raw.AsSpan().SequenceEqual(a.Data.Span));
        Assert.True(raw.AsSpan().SequenceEqual(b.Data.Span));
        Assert.True(hinted < raw.Length * 1.05, $"hinted allocated {hinted} for {raw.Length}");
        Assert.True(plain > hinted, $"plain {plain}, hinted {hinted}");
    }

    [Fact]
    public void ExactHintWithPngPredictorAllocatesOnce()
    {
        const int columns = 1024, rows = 2048;
        byte[] image = FilterTestUtil.Texty(columns * rows, 2);
        var tagged = new byte[(columns + 1) * rows];
        for (int row = 0; row < rows; row++)
        {
            tagged[row * (columns + 1)] = 0; // PNG "None" rows keep the test about sizes, not about the predictor
            image.AsSpan(row * columns, columns).CopyTo(tagged.AsSpan((row * (columns + 1)) + 1));
        }

        byte[] z = FilterTestUtil.Zlib(tagged);
        var p = new FilterParameters(Predictor: 15, Colors: 1, BitsPerComponent: 8, Columns: columns);
        long hinted = Allocated(() => StreamFilters.Decode("FlateDecode", z, p, Limits, image.Length), out FilterResult r);
        Assert.True(image.AsSpan().SequenceEqual(r.Data.Span));
        Assert.True(hinted < tagged.Length * 1.05, $"hinted allocated {hinted} for {tagged.Length}");
    }

    [Theory]
    [InlineData(1_500_000)] // the data is longer than the hint
    [InlineData(9_000_000)] // the data is shorter
    [InlineData(1_048_577)] // just over the pooled-buffer size
    public void WrongHintStillDecodesCorrectly(int hint)
    {
        byte[] raw = FilterTestUtil.Texty(3 << 20, 3);
        byte[] z = FilterTestUtil.Zlib(raw);
        FilterResult r = StreamFilters.Decode("FlateDecode", z, null, Limits, hint);
        Assert.Equal(FilterIssues.None, r.Issues);
        Assert.True(raw.AsSpan().SequenceEqual(r.Data.Span));
    }

    [Fact]
    public void WrongHintOnDamagedDataKeepsWhatDecoded()
    {
        byte[] raw = FilterTestUtil.Texty(3 << 20, 4);
        byte[] z = FilterTestUtil.Zlib(raw);
        byte[] cut = z.AsSpan(0, z.Length / 2).ToArray();
        FilterResult plain = StreamFilters.Decode("FlateDecode", cut, null, Limits);
        FilterResult hinted = StreamFilters.Decode("FlateDecode", cut, null, Limits, raw.Length);
        Assert.NotEqual(FilterIssues.None, hinted.Issues & FilterIssues.Truncated);
        Assert.Equal(plain.Issues, hinted.Issues);
        Assert.True(plain.Data.Span.SequenceEqual(hinted.Data.Span));
        Assert.True(hinted.Data.Length > 0 && hinted.Data.Length < raw.Length);
    }

    [Fact]
    public void ImplausibleHintAllocatesNothingExtra()
    {
        // A tiny stream whose dictionary claims a huge image must not get a huge buffer.
        byte[] z = FilterTestUtil.Zlib(new byte[100]);
        long bytes = Allocated(() => StreamFilters.Decode("FlateDecode", z, null, Limits, 200L << 20), out FilterResult r);
        Assert.Equal(100, r.Data.Length);
        Assert.True(bytes < 1 << 20, $"allocated {bytes}");
    }

    [Fact]
    public void LimitsStillApplyWithAHint()
    {
        var small = new DecodeLimits(2 << 20, 1100, 16L << 20);
        byte[] z = FilterTestUtil.Zlib(new byte[3 << 20]);

        // A hint above the cap is ignored (no allocation of that size) and the output limit still stops the decode.
        long bytes = Allocated(() => StreamFilters.Decode("FlateDecode", z, null, small, 3 << 20), out FilterResult r);
        Assert.NotEqual(FilterIssues.None, r.Issues & FilterIssues.LimitExceeded);
        Assert.True(r.Data.Length <= 2 << 20);
        Assert.True(bytes < 5 << 20, $"allocated {bytes}");

        // A hint within the cap on a stream that exceeds it: the cap holds as well.
        FilterResult within = StreamFilters.Decode("FlateDecode", z, null, small, 2 << 20);
        Assert.NotEqual(FilterIssues.None, within.Issues & FilterIssues.LimitExceeded);
        Assert.True(within.Data.Length <= 2 << 20);
    }

    [Fact]
    public void LargeImageXObjectDecodesToTheSamePixelsWithTheHint()
    {
        const int w = 2048, h = 1024;
        byte[] raw = FilterTestUtil.Texty(w * h, 5);
        using ImageKit.Result r = ImageKit.Make($"/Width {w} /Height {h} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode", FilterTestUtil.Zlib(raw));
        Assert.True(raw.AsSpan().SequenceEqual(r.Img.Samples.Span));
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void ImageWhoseDataIsShorterThanItsDictionaryStillLoads()
    {
        const int w = 2048, h = 1024;
        byte[] raw = FilterTestUtil.Texty(w * h / 2, 6);
        using ImageKit.Result r = ImageKit.Make($"/Width {w} /Height {h} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode", FilterTestUtil.Zlib(raw));
        Assert.True(raw.AsSpan().SequenceEqual(r.Img.Samples.Span[..raw.Length]));
        Assert.Contains(r.Warnings, m => m.Contains("missing samples", StringComparison.Ordinal));
    }
}
