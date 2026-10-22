using System;

namespace OnePdf.Functions;

/// <summary>
/// Transcendental functions built from IEEE-754 addition, multiplication, division and square root only, in a fixed
/// evaluation order, so every platform and runtime returns the same bits. The platform math library may differ in the
/// last place from machine to machine; function results feed snapshots that must not. Accuracy is a few units in the
/// last place, which is far finer than any colour or coordinate needs.
/// </summary>
internal static class DetMath
{
    private const double Ln2 = 0.693147180559945309417232121458;
    // ln 2 split so that kf * Ln2Hi is exact for any integer |kf| < 2^20 (Ln2Hi has trailing zero bits).
    private const double Ln2Hi = 6.93147180369123816490e-01;
    private const double Ln2Lo = 1.90821492927058770002e-10;
    private const double Ln10 = 2.30258509299404568401799145468;
    private const double Pi = 3.14159265358979323846264338328;
    private const double HalfPi = 1.57079632679489661923132169164;
    private const double DegToRad = 0.0174532925199432957692369076849;
    private const double RadToDeg = 57.2957795130823208767981548141;

    /// <summary>Sine of an angle in degrees.</summary>
    public static double SinDeg(double deg)
    {
        if (!double.IsFinite(deg))
        {
            return 0;
        }

        return Rotate(deg, 0);
    }

    /// <summary>Cosine of an angle in degrees.</summary>
    public static double CosDeg(double deg)
    {
        if (!double.IsFinite(deg))
        {
            return 0;
        }

        return Rotate(deg, 1);
    }

    // Reduces to a quadrant, where exact multiples of 90 degrees give exact 0 and +-1.
    private static double Rotate(double deg, int phase)
    {
        double r = deg % 360.0;
        if (r < 0)
        {
            r += 360.0;
        }

        int q = (int)(r / 90.0);
        if (q > 3)
        {
            q = 3;
        }

        double t = r - (90.0 * q);
        int quad = (q + phase) & 3;
        if (t == 0)
        {
            return quad switch { 0 => 0, 1 => 1, 2 => 0, _ => -1 };
        }

        double x = t * DegToRad;
        double s = SinSeries(x);
        double c = CosSeries(x);
        return quad switch { 0 => s, 1 => c, 2 => -s, _ => -c };
    }

    // |x| <= pi/2 so 11 terms leave an error below 1e-19.
    private static double SinSeries(double x)
    {
        double x2 = x * x;
        double term = x;
        double sum = x;
        for (int k = 1; k <= 11; k++)
        {
            term = -term * x2 / ((2.0 * k) * ((2.0 * k) + 1.0));
            sum += term;
        }

        return sum;
    }

    private static double CosSeries(double x)
    {
        double x2 = x * x;
        double term = 1.0;
        double sum = 1.0;
        for (int k = 1; k <= 11; k++)
        {
            term = -term * x2 / (((2.0 * k) - 1.0) * (2.0 * k));
            sum += term;
        }

        return sum;
    }

    /// <summary>Arc tangent of num/den in degrees, in [0, 360). Both zero gives 0.</summary>
    public static double Atan2Deg(double num, double den)
    {
        if (!double.IsFinite(num) || !double.IsFinite(den) || (num == 0 && den == 0))
        {
            return 0;
        }

        double a = Atan2(num, den) * RadToDeg;
        if (a < 0)
        {
            a += 360.0;
        }

        if (a >= 360.0)
        {
            a = 0;
        }

        return a;
    }

    private static double Atan2(double y, double x)
    {
        if (x == 0)
        {
            return y > 0 ? HalfPi : -HalfPi;
        }

        if (y == 0)
        {
            return x > 0 ? 0 : Pi;
        }

        double ay = Math.Abs(y);
        double ax = Math.Abs(x);
        double a = ay <= ax ? Atan(ay / ax) : HalfPi - Atan(ax / ay);
        if (x < 0)
        {
            a = Pi - a;
        }

        return y < 0 ? -a : a;
    }

    // 0 <= x <= 1. Two half-angle reductions bring the argument under 0.2, then a series.
    private static double Atan(double x)
    {
        double h = x / (1.0 + Math.Sqrt(1.0 + (x * x)));
        h = h / (1.0 + Math.Sqrt(1.0 + (h * h)));
        double h2 = h * h;
        double term = h;
        double sum = h;
        for (int k = 1; k <= 14; k++)
        {
            term = -term * h2;
            sum += term / ((2.0 * k) + 1.0);
        }

        return 4.0 * sum;
    }

    /// <summary>Natural logarithm; x must be positive and finite (callers check).</summary>
    public static double Ln(double x)
    {
        if (!(x > 0) || double.IsInfinity(x))
        {
            return x > 0 ? double.PositiveInfinity : double.NaN;
        }

        int e = 0;
        if (x < 2.2250738585072014E-308)
        {
            x *= 18014398509481984.0; // 2^54
            e = -54;
        }

        long bits = BitConverter.DoubleToInt64Bits(x);
        e += (int)((bits >> 52) & 0x7FF) - 1023;
        double m = BitConverter.Int64BitsToDouble((bits & 0x000FFFFFFFFFFFFFL) | 0x3FF0000000000000L);
        if (m > 1.4142135623730951)
        {
            m *= 0.5;
            e++;
        }

        double s = (m - 1.0) / (m + 1.0);
        double s2 = s * s;
        double term = s;
        double sum = s;
        for (int k = 1; k <= 14; k++)
        {
            term *= s2;
            sum += term / ((2.0 * k) + 1.0);
        }

        return (e * Ln2) + (2.0 * sum);
    }

    /// <summary>Base-10 logarithm; x must be positive and finite.</summary>
    public static double Log10(double x) => Ln(x) / Ln10;

    /// <summary>e raised to x. Overflow gives infinity, underflow 0.</summary>
    public static double Exp(double x)
    {
        if (double.IsNaN(x))
        {
            return double.NaN;
        }

        if (x > 709.78)
        {
            return double.PositiveInfinity;
        }

        if (x < -745.2)
        {
            return 0;
        }

        double kf = Math.Floor((x / Ln2) + 0.5);
        double r = (x - (kf * Ln2Hi)) - (kf * Ln2Lo);
        double term = 1.0;
        double sum = 1.0;
        for (int i = 1; i <= 20; i++)
        {
            term = term * r / i;
            sum += term;
        }

        int k = (int)kf;
        // Two steps keep the scale factor itself in range for results near the subnormal limit.
        int k1 = k / 2;
        return Math.ScaleB(Math.ScaleB(sum, k1), k - k1);
    }

    /// <summary>
    /// <paramref name="x"/> to the power <paramref name="y"/>. Small integer exponents multiply exactly (so x^1 is x and x^2 is x*x);
    /// others use exp(y ln x). A negative base with a fractional exponent is NaN.
    /// </summary>
    public static double Pow(double x, double y)
    {
        if (y == 0)
        {
            return 1.0;
        }

        if (double.IsNaN(x) || double.IsNaN(y))
        {
            return double.NaN;
        }

        bool integral = y == Math.Floor(y);
        if (integral && Math.Abs(y) <= 1024)
        {
            long n = (long)Math.Abs(y);
            double result = 1.0;
            double b = x;
            while (n > 0)
            {
                if ((n & 1) != 0)
                {
                    result *= b;
                }

                n >>= 1;
                if (n > 0)
                {
                    b *= b;
                }
            }

            return y < 0 ? 1.0 / result : result;
        }

        if (x == 0)
        {
            return y > 0 ? 0 : double.PositiveInfinity;
        }

        if (x < 0)
        {
            if (!integral)
            {
                return double.NaN;
            }

            // Huge integral exponent: the magnitude is exp(y ln|x|), the sign follows parity.
            double mag = Exp(y * Ln(-x));
            return (y % 2.0) != 0 ? -mag : mag;
        }

        if (double.IsInfinity(x))
        {
            return y > 0 ? double.PositiveInfinity : 0;
        }

        return Exp(y * Ln(x));
    }
}
