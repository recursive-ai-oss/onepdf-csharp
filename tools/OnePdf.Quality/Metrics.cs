using System.Text;

namespace OnePdf.Quality;

/// <summary>
/// The two extraction metrics. Both compare a page's extracted text with its truth text after splitting both into tokens: runs of
/// non-white-space characters, with every ideograph, kana and hangul character a token of its own (those scripts have no spaces).
/// <list type="bullet">
/// <item><b>Content error rate</b> (order-free): the tokens of each side are sorted ordinally and joined with single spaces; the result is the
/// Levenshtein distance between the two strings divided by the length of the truth string. Reading order does not matter, only which characters
/// were found and how they were grouped into words.</item>
/// <item><b>Reading-order error</b>: the Levenshtein distance between the two token sequences (tokens as symbols) divided by the number of truth tokens.</item>
/// </list>
/// Both are in the range 0 to well above 1 (an extraction that adds a lot of text can exceed 1); lower is better.
/// </summary>
internal static class Metrics
{
    internal static bool IsCjk(int cp) =>
        (cp >= 0x2E80 && cp <= 0x9FFF) || (cp >= 0xAC00 && cp <= 0xD7AF) || (cp >= 0xF900 && cp <= 0xFAFF) ||
        (cp >= 0xFF00 && cp <= 0xFFEF) || (cp >= 0x20000 && cp <= 0x3FFFF);

    internal static List<string> Tokens(string text)
    {
        List<string> tokens = [];
        StringBuilder cur = new();
        for (int i = 0; i < text.Length; i++)
        {
            int cp = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];
            int len = cp > 0xFFFF ? 2 : 1;
            if (char.IsWhiteSpace(text[i]))
            {
                Flush();
            }
            else if (IsCjk(cp))
            {
                Flush();
                tokens.Add(text.Substring(i, len));
            }
            else
            {
                cur.Append(text, i, len);
            }

            i += len - 1;
        }

        Flush();
        return tokens;

        void Flush()
        {
            if (cur.Length > 0)
            {
                tokens.Add(cur.ToString());
                cur.Clear();
            }
        }
    }

    /// <summary>Distance and the denominator (truth size) for one page.</summary>
    internal readonly record struct Score(long ContentDistance, long ContentSize, long OrderDistance, long OrderSize)
    {
        public static Score operator +(Score a, Score b) =>
            new(a.ContentDistance + b.ContentDistance, a.ContentSize + b.ContentSize, a.OrderDistance + b.OrderDistance, a.OrderSize + b.OrderSize);

        internal double ContentRate => ContentSize == 0 ? 0 : (double)ContentDistance / ContentSize;

        internal double OrderRate => OrderSize == 0 ? 0 : (double)OrderDistance / OrderSize;
    }

    internal static Score ScorePage(string truth, string extracted)
    {
        List<string> t = Tokens(truth);
        List<string> e = Tokens(extracted);

        string ts = SortedJoin(t);
        string es = SortedJoin(e);
        long content = Levenshtein<char>(ts.AsSpan(), es.AsSpan());
        long order = Levenshtein<string>(t.ToArray(), e.ToArray());
        return new Score(content, ts.Length, order, t.Count);
    }

    private static string SortedJoin(List<string> tokens)
    {
        string[] a = [.. tokens];
        Array.Sort(a, StringComparer.Ordinal);
        return string.Join(' ', a);
    }

    internal static long Levenshtein<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b) where T : IEquatable<T>
    {
        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        int[] prev = new int[b.Length + 1];
        int[] cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            T ai = a[i - 1];
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = ai.Equals(b[j - 1]) ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
            }

            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }
}
