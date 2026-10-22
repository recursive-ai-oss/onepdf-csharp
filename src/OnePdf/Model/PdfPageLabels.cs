using System.Globalization;
using System.Text;

namespace OnePdf;

/// <summary>The numbering style of a page label range (ISO 32000-2 12.4.2).</summary>
public enum PdfPageLabelStyle
{
    /// <summary>No number, only the prefix.</summary>
    None = 0,

    /// <summary>Decimal arabic numerals.</summary>
    Arabic = 1,

    /// <summary>Uppercase roman numerals.</summary>
    UpperRoman = 2,

    /// <summary>Lowercase roman numerals.</summary>
    LowerRoman = 3,

    /// <summary>Uppercase letters: A to Z, then AA to ZZ, AAA and so on.</summary>
    UpperLetters = 4,

    /// <summary>Lowercase letters.</summary>
    LowerLetters = 5,
}

/// <summary>One range of pages with the same labelling scheme.</summary>
/// <param name="FirstPage">The zero-based index of the first page of the range.</param>
/// <param name="Style">The numbering style.</param>
/// <param name="Prefix">The prefix put before each number (may be empty).</param>
/// <param name="Start">The number of the first page of the range (at least 1).</param>
public sealed record PdfPageLabelRange(int FirstPage, PdfPageLabelStyle Style, string Prefix, int Start);

/// <summary>The page labels of a document, from the <c>/PageLabels</c> number tree.</summary>
public sealed class PdfPageLabels
{
    private readonly PdfPageLabelRange[] _ranges;

    internal PdfPageLabels(PdfPageLabelRange[] ranges) => _ranges = ranges;

    /// <summary>The ranges in page order. When the first range does not start at page 0, pages before it are labelled as decimal numbers (the specification requires a range at 0).</summary>
    public IReadOnlyList<PdfPageLabelRange> Ranges => _ranges;

    /// <summary>
    /// The label of a page given its zero-based index: the prefix plus the number in the range's style, counting from the
    /// range's start. Pages before the first range get their one-based decimal number.
    /// </summary>
    public string GetLabel(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        PdfPageLabelRange? range = null;
        int lo = 0, hi = _ranges.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_ranges[mid].FirstPage <= pageIndex)
            {
                range = _ranges[mid];
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (range is null)
        {
            return (pageIndex + 1).ToString(CultureInfo.InvariantCulture);
        }

        long n = (long)range.Start + (pageIndex - range.FirstPage);
        return range.Prefix + Format(range.Style, n);
    }

    private static string Format(PdfPageLabelStyle style, long n) => style switch
    {
        PdfPageLabelStyle.Arabic => n.ToString(CultureInfo.InvariantCulture),
        PdfPageLabelStyle.UpperRoman => Roman(n, upper: true),
        PdfPageLabelStyle.LowerRoman => Roman(n, upper: false),
        PdfPageLabelStyle.UpperLetters => Letters(n, 'A'),
        PdfPageLabelStyle.LowerLetters => Letters(n, 'a'),
        _ => string.Empty,
    };

    // Standard subtractive numerals; numbers of 4000 and more repeat M, which is all the specification leaves open.
    private static string Roman(long n, bool upper)
    {
        if (n <= 0)
        {
            return n.ToString(CultureInfo.InvariantCulture);
        }

        ReadOnlySpan<int> values = [1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1];
        string[] symbols = ["M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I"];
        var sb = new StringBuilder();
        if (n >= 4000)
        {
            sb.Append('M', (int)Math.Min((n / 1000) - 3, 100_000));
            n %= 1000;
            n += 3000;
        }

        for (int i = 0; i < values.Length; i++)
        {
            while (n >= values[i])
            {
                sb.Append(symbols[i]);
                n -= values[i];
            }
        }

        string s = sb.ToString();
        return upper ? s : s.ToLowerInvariant();
    }

    // 1 to 26 is A to Z, 27 to 52 is AA to ZZ, 53 to 78 is AAA to ZZZ (12.4.2).
    private static string Letters(long n, char a)
    {
        if (n <= 0)
        {
            return n.ToString(CultureInfo.InvariantCulture);
        }

        long repeat = ((n - 1) / 26) + 1;
        char c = (char)(a + (int)((n - 1) % 26));
        return new string(c, (int)Math.Min(repeat, 100_000));
    }
}

public sealed partial class PdfDocument
{
    /// <summary>The page labels, or null when the document defines none.</summary>
    public PdfPageLabels? PageLabels => _pageLabels.Value;

    private Lazy<PdfPageLabels?> _pageLabels = null!;

    private PdfPageLabels? LoadPageLabels()
    {
        PdfDictionary? root = Catalog.GetDictionary("PageLabels");
        if (root is null)
        {
            return null;
        }

        var ranges = new List<PdfPageLabelRange>();
        foreach (KeyValuePair<PdfObject, PdfObject> kv in ReadTree(root, true, "page labels"))
        {
            long first = ((PdfInteger)kv.Key).Value;
            if (first < 0 || first > int.MaxValue || Resolve(kv.Value) is not PdfDictionary d)
            {
                continue;
            }

            PdfPageLabelStyle style = d.GetName("S") switch
            {
                "D" => PdfPageLabelStyle.Arabic,
                "R" => PdfPageLabelStyle.UpperRoman,
                "r" => PdfPageLabelStyle.LowerRoman,
                "A" => PdfPageLabelStyle.UpperLetters,
                "a" => PdfPageLabelStyle.LowerLetters,
                _ => PdfPageLabelStyle.None,
            };
            long start = d.GetInt("St", 1);
            ranges.Add(new PdfPageLabelRange((int)first, style, d.GetString("P")?.GetText() ?? string.Empty, (int)Math.Clamp(start < 1 ? 1 : start, 1, int.MaxValue)));
        }

        // Tree order is key order for well-formed files; sort anyway so lookup by binary search holds for sloppy ones.
        ranges.Sort(static (x, y) => x.FirstPage.CompareTo(y.FirstPage));
        return ranges.Count == 0 ? null : new PdfPageLabels([.. ranges]);
    }
}
