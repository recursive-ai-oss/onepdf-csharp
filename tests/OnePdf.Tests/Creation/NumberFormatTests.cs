using System.Globalization;

namespace OnePdf.Tests.Creation;

/// <summary>The content stream's number format: four decimals, halves away from zero, no trailing zeros, never "-0".</summary>
public class NumberFormatTests
{
    // The definition the fast path must agree with, character for character.
    private static string Reference(double v)
    {
        double r = Math.Round(Math.Clamp(v, -1e12, 1e12), 4, MidpointRounding.AwayFromZero);
        return r == 0 ? "0" : r.ToString("0.####", CultureInfo.InvariantCulture);
    }

    [Fact]
    public void EdgeValuesFormatAsDefined()
    {
        double[] values =
        [
            0, -0.0, 0.00004, -0.00004, 0.00005, -0.00005, 0.00015, 0.12345, 0.99995, 1, -1, 1.5, 100, 12345.6789, 0.1, 0.2 + 0.1,
            595.2756, 841.8898, 1e8 + 0.12345, 999_999_999.9999, 999_999_999.99995, 1e9, -1e9, 1e9 + 0.5, 123_456_789_012.3456, 1e12, 5e12, -5e12,
            double.Epsilon, 1e-300, 4503599627370496.5,
        ];
        foreach (double v in values)
        {
            Assert.Equal(Reference(v), PdfContentBuilder.Num(v));
        }
    }

    [Fact]
    public void RandomValuesFormatAsDefined()
    {
        var rng = new Random(20260101);
        for (int i = 0; i < 400_000; i++)
        {
            double magnitude = Math.Pow(10, rng.Next(-6, 12));
            double v = (rng.NextDouble() - 0.5) * 2 * magnitude;
            switch (i % 4)
            {
                case 1:
                    v = Math.Round(v, 4); // exact four-decimal values
                    break;
                case 2:
                    v = (Math.Floor(v * 10000) + 0.5) / 10000; // halves of the last place
                    break;
                case 3:
                    v = Math.Round(v, 2);
                    break;
            }

            Assert.Equal(Reference(v), PdfContentBuilder.Num(v));
        }
    }

    [Fact]
    public void NonFiniteNumbersAreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfContentBuilder.Num(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfContentBuilder.Num(double.PositiveInfinity));
    }
}
