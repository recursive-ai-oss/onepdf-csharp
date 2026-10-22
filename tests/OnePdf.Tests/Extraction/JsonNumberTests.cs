using System.Globalization;
using OnePdf.Text;

namespace OnePdf.Tests.Extraction;

public class JsonNumberTests
{
    // The form the writer produced before the fast path: round to three decimals, then the "0.###" format.
    private static string Reference(double v)
    {
        if (!double.IsFinite(v))
        {
            return "0";
        }

        double r = Math.Round(v, 3, MidpointRounding.ToEven);
        return r == 0 ? "0" : r.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string Fast(double v)
    {
        Span<byte> b = stackalloc byte[64];
        int n = PdfExtractionJson.FormatNum(v, b);
        return n < 0 ? Reference(v) : System.Text.Encoding.ASCII.GetString(b.Slice(0, n));
    }

    [Fact]
    public void FastFormattingMatchesTheFormatString()
    {
        double[] edge =
        [
            0, -0.0, 1, -1, 0.5, -0.5, 0.0005, -0.0005, 0.0015, 0.0025, 0.0004999, 1.0005, 2.0005, 999.9995, 1000, 12.3456789, -12.3456789, 100.1, 0.1, 0.2, 0.3,
            612, 792, 1e11, 1e12, 1e12 + 0.5, 123456789012.345, -123456789012.345, 1e15, 1e20, 1e300, double.MaxValue, double.Epsilon, double.NaN,
            double.PositiveInfinity, double.NegativeInfinity, 0.001, 0.01, 0.999, 0.9995, 9.9995, 4503599627370.495,
        ];
        foreach (double v in edge)
        {
            Assert.Equal(Reference(v), Fast(v));
        }

        Random rng = new(4242);
        for (int i = 0; i < 400_000; i++)
        {
            double scale = Math.Pow(10, rng.Next(-5, 12));
            double v = (rng.NextDouble() - 0.5) * scale;
            Assert.Equal(Reference(v), Fast(v));
            double k = Math.Round(v * 1000) / 1000 + (rng.Next(3) - 1) * 0.0005; // on and around the rounding boundaries
            Assert.Equal(Reference(k), Fast(k));
        }
    }
}
