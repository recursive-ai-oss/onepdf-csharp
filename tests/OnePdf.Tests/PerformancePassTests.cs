using OnePdf.Filters;
using OnePdf.Tests.Filters;

namespace OnePdf.Tests;

/// <summary>Behaviour that the reader and filter speed-ups must keep: same results, no exceptions as control flow.</summary>
public class PerformancePassTests
{
    [Theory]
    [InlineData(1, 8, 700)]
    [InlineData(2, 8, 301)]
    [InlineData(3, 8, 257)]
    [InlineData(4, 8, 129)]
    [InlineData(3, 16, 90)]
    public void WidePngRowsMatchTheStraightforwardReference(int colors, int bpc, int columns)
    {
        int rowBytes = ((colors * bpc * columns) + 7) / 8;
        int bpp = Math.Max(1, ((colors * bpc) + 7) / 8);
        int rows = 23;
        var rnd = new Random(colors * 1000 + columns);
        byte[] enc = new byte[rows * (rowBytes + 1)];
        rnd.NextBytes(enc);
        for (int r = 0; r < rows; r++)
        {
            enc[r * (rowBytes + 1)] = (byte)(r % 5);
        }

        byte[] expected = ReferencePng(enc, rowBytes, bpp, rows);
        byte[] work = (byte[])enc.Clone();
        FilterIssues issues = FilterIssues.None;
        string? message = null;
        int n = Predictors.Undo(work, work.Length, new FilterParameters(Predictor: 15, Colors: colors, BitsPerComponent: bpc, Columns: columns), ref issues, ref message);
        Assert.Equal(expected.Length, n);
        Assert.True(expected.AsSpan().SequenceEqual(work.AsSpan(0, n)));
        Assert.Equal(FilterIssues.None, issues);
    }

    private static byte[] ReferencePng(byte[] enc, int rowBytes, int bpp, int rows)
    {
        var o = new byte[rows * rowBytes];
        for (int r = 0; r < rows; r++)
        {
            int tag = enc[r * (rowBytes + 1)];
            for (int i = 0; i < rowBytes; i++)
            {
                int x = enc[(r * (rowBytes + 1)) + 1 + i];
                int a = i >= bpp ? o[(r * rowBytes) + i - bpp] : 0;
                int b = r > 0 ? o[((r - 1) * rowBytes) + i] : 0;
                int c = r > 0 && i >= bpp ? o[((r - 1) * rowBytes) + i - bpp] : 0;
                int pred = tag switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    _ => Paeth(a, b, c),
                };
                o[(r * rowBytes) + i] = (byte)(x + pred);
            }
        }

        return o;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    [Fact]
    public void FlateResultIsAnExactSizeArray()
    {
        byte[] data = FilterTestUtil.Texty(1_234_567, 5);
        FilterResult r = StreamFilters.Decode("FlateDecode", FilterTestUtil.Zlib(data), null, DecodeLimits.Default);
        Assert.True(r.IsClean, r.Message);
        Assert.Equal(data.Length, r.Data.Length);
        Assert.True(data.AsSpan().SequenceEqual(r.Data.Span));
    }

    [Fact]
    public void FlateRespectsTheSizeCapEvenWithALargerPooledBuffer()
    {
        byte[] data = new byte[300_000];
        var limits = new DecodeLimits(100_000, DecodeLimits.Default.MaxExpansionRatio, DecodeLimits.Default.RatioAppliesAfterBytes);
        FilterResult r = StreamFilters.Decode("FlateDecode", FilterTestUtil.Zlib(data), null, limits);
        Assert.NotEqual(FilterIssues.None, r.Issues & FilterIssues.LimitExceeded);
        Assert.True(r.Data.Length <= 100_000);
    }

    [Fact]
    public void RepairingAFileWithABrokenStartxrefRaisesNoExceptions()
    {
        var b = new PdfBuilder();
        b.BasicPages();
        long x = b.XrefTable(4);
        b.Trailer("/Size 4 /Root 1 0 R", x + 11);
        byte[] damaged = b.ToArray();
        byte[] noStartxref = PdfBuilder.Replace(PdfBuilder.Simple(), "startxref", "startxxxx");

        foreach (byte[] file in new[] { damaged, noStartxref })
        {
            int thrown = 0;
            int me = Environment.CurrentManagedThreadId;
            void Count(object? s, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
            {
                if (Environment.CurrentManagedThreadId == me)
                {
                    thrown++;
                }
            }

            AppDomain.CurrentDomain.FirstChanceException += Count;
            try
            {
                using PdfDocument doc = PdfDocument.Open(file);
                Assert.Equal(1, doc.PageCount);
                Assert.Contains(doc.Diagnostics, d => d.Message.Contains("rebuilt", StringComparison.Ordinal));
            }
            finally
            {
                AppDomain.CurrentDomain.FirstChanceException -= Count;
            }

            Assert.Equal(0, thrown);
        }
    }

    [Fact]
    public void PageCountThenPagesGivesTheSameDiagnosticsAsPagesAlone()
    {
        // A page tree that visits one page twice: the repeat is skipped and reported once, whichever call comes first.
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R 3 0 R] /Count 2 >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] >>");
        long x = b.XrefTable(4);
        b.Trailer("/Size 4 /Root 1 0 R", x);
        byte[] file = b.ToArray();

        using PdfDocument a = PdfDocument.Open(file);
        _ = a.PageCount;
        _ = a.Pages.Count;
        using PdfDocument c = PdfDocument.Open(file);
        _ = c.Pages.Count;
        Assert.Equal(1, a.PageCount);
        Assert.Equal(c.Pages.Count, a.Pages.Count);
        Assert.Equal(c.Diagnostics.Count(d => d.Message.Contains("twice", StringComparison.Ordinal)), a.Diagnostics.Count(d => d.Message.Contains("twice", StringComparison.Ordinal)));
    }
}
