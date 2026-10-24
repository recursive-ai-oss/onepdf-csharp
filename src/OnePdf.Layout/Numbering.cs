using System.Globalization;
using System.Text;

namespace OnePdf.Layout;

/// <summary>How a counter is written: page numbers, list markers.</summary>
public enum NumberingStyle
{
    /// <summary>1, 2, 3, ...</summary>
    Arabic = 0,

    /// <summary>a, b, ..., z, aa, ab, ...</summary>
    LowerLetters,

    /// <summary>A, B, ..., Z, AA, AB, ...</summary>
    UpperLetters,

    /// <summary>i, ii, iii, iv, ... Numbers above 3999 are written in decimal.</summary>
    LowerRoman,

    /// <summary>I, II, III, IV, ... Numbers above 3999 are written in decimal.</summary>
    UpperRoman,
}

internal static class Numbering
{
    /// <summary>Writes <paramref name="n"/> in <paramref name="style"/>. Zero and negative numbers are always decimal, since the other styles have no sign or zero.</summary>
    public static string Format(long n, NumberingStyle style)
    {
        if (n <= 0 || style == NumberingStyle.Arabic)
        {
            return n.ToString(CultureInfo.InvariantCulture);
        }

        switch (style)
        {
            case NumberingStyle.LowerLetters:
                return Letters(n, 'a');
            case NumberingStyle.UpperLetters:
                return Letters(n, 'A');
            default:
                if (n > 3999)
                {
                    return n.ToString(CultureInfo.InvariantCulture);
                }

                string roman = Roman((int)n);
                return style == NumberingStyle.LowerRoman ? roman.ToLowerInvariant() : roman;
        }
    }

    public static PdfPageLabelStyle LabelStyle(NumberingStyle style) => style switch
    {
        NumberingStyle.LowerLetters => PdfPageLabelStyle.LowerLetters,
        NumberingStyle.UpperLetters => PdfPageLabelStyle.UpperLetters,
        NumberingStyle.LowerRoman => PdfPageLabelStyle.LowerRoman,
        NumberingStyle.UpperRoman => PdfPageLabelStyle.UpperRoman,
        _ => PdfPageLabelStyle.Arabic,
    };

    // Bijective base 26, as in spreadsheets: 26 is z, 27 is aa.
    private static string Letters(long n, char a)
    {
        var sb = new StringBuilder();
        while (n > 0)
        {
            n--;
            sb.Insert(0, (char)(a + (int)(n % 26)));
            n /= 26;
        }

        return sb.ToString();
    }

    private static string Roman(int n)
    {
        ReadOnlySpan<int> values = [1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1];
        string[] symbols = ["M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I"];
        var sb = new StringBuilder();
        for (int i = 0; i < values.Length; i++)
        {
            while (n >= values[i])
            {
                sb.Append(symbols[i]);
                n -= values[i];
            }
        }

        return sb.ToString();
    }
}
