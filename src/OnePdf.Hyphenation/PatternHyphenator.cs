using System.Globalization;
using System.IO.Compression;
using System.Text;
using OnePdf.Layout;

namespace OnePdf.Hyphenation;

/// <summary>Settings for a <see cref="PatternHyphenator"/>.</summary>
public sealed record HyphenationOptions
{
    /// <summary>Words shorter than this (in UTF-16 units) are never hyphenated. Default 5.</summary>
    public int MinWordLength { get; init; } = 5;

    /// <summary>
    /// The fewest letters that must stay before a hyphen. Null (the default) uses the language's own typesetting minimum from its
    /// pattern file (2 for most languages), and 2 for custom patterns.
    /// </summary>
    public int? LeftMin { get; init; }

    /// <summary>
    /// The fewest letters that must stay after a hyphen. Null (the default) uses the language's own typesetting minimum from its
    /// pattern file (3 for American and British English and Portuguese, 2 for most others), and 2 for custom patterns.
    /// </summary>
    public int? RightMin { get; init; }
}

/// <summary>
/// The patterns of one language, immutable after construction and shared by every hyphenator built on them.
/// </summary>
internal sealed class PatternSet
{
    public PatternSet(IEnumerable<string> patterns, IEnumerable<string>? exceptions, int left, int right)
    {
        Patterns = [];
        Exceptions = [];
        foreach (string raw in patterns)
        {
            string p = raw.ToLowerInvariant();
            var letters = new StringBuilder(p.Length);
            var levels = new List<byte>(p.Length + 1);
            byte pending = 0;
            foreach (char c in p)
            {
                if (c >= '0' && c <= '9')
                {
                    pending = (byte)(c - '0');
                }
                else
                {
                    letters.Append(c);
                    levels.Add(pending);
                    pending = 0;
                }
            }

            levels.Add(pending);
            if (letters.Length == 0)
            {
                continue;
            }

            string key = letters.ToString();
            if (Patterns.TryGetValue(key, out byte[]? old))
            {
                for (int i = 0; i < old.Length; i++)
                {
                    levels[i] = Math.Max(levels[i], old[i]);
                }
            }

            Patterns[key] = [.. levels];
            if (key.Length > MaxLength)
            {
                MaxLength = key.Length;
            }
        }

        if (exceptions is not null)
        {
            foreach (string raw in exceptions)
            {
                var word = new StringBuilder(raw.Length);
                var points = new List<int>();
                foreach (char c in raw.ToLowerInvariant())
                {
                    if (c == '-')
                    {
                        if (word.Length > 0 && (points.Count == 0 || points[^1] != word.Length))
                        {
                            points.Add(word.Length);
                        }
                    }
                    else
                    {
                        word.Append(c);
                    }
                }

                // A trailing hyphen is not a break inside the word.
                if (points.Count > 0 && points[^1] >= word.Length)
                {
                    points.RemoveAt(points.Count - 1);
                }

                if (word.Length > 0)
                {
                    Exceptions[word.ToString()] = [.. points];
                }
            }
        }

        LeftMin = left;
        RightMin = right;
    }

    public Dictionary<string, byte[]> Patterns { get; }

    public Dictionary<string, int[]> Exceptions { get; }

    public int MaxLength { get; }

    public int LeftMin { get; }

    public int RightMin { get; }

    /// <summary>Reads the format written by tools/hyphenation/generate.py: a "left right" line, the patterns, an empty line, the exceptions.</summary>
    public static PatternSet Read(Stream compressed)
    {
        using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
        using var reader = new StreamReader(deflate, Encoding.UTF8);
        string[] head = (reader.ReadLine() ?? "2 2").Split(' ');
        int left = int.Parse(head[0], CultureInfo.InvariantCulture);
        int right = int.Parse(head[1], CultureInfo.InvariantCulture);
        var patterns = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is { Length: > 0 })
        {
            patterns.Add(line);
        }

        var exceptions = new List<string>();
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length > 0)
            {
                exceptions.Add(line);
            }
        }

        return new PatternSet(patterns, exceptions, left, right);
    }
}

/// <summary>
/// Finds hyphenation points with Liang's pattern algorithm (the method of TeX): the word is lower-cased and wrapped in word-boundary
/// marks, every pattern found in it raises the odd/even levels between its letters, and the highest level at each gap decides; an odd
/// level allows a hyphen. A word on the language's exception list takes its listed points instead of the patterns.
/// </summary>
/// <remarks>
/// <para>
/// Get one for a bundled language with <see cref="Hyphenators.For"/>, or build one from your own patterns with
/// <see cref="FromPatterns"/>. Use it as <c>TextDescriptor.Hyphenator(...)</c> or <c>TextParagraph.Hyphenator</c>. It is immutable, and safe
/// to share between threads and documents. Results are deterministic.
/// </para>
/// <para>
/// A hyphen is allowed between positions <c>i - 1</c> and <c>i</c> of the word when <c>i &gt;= LeftMin</c>, <c>length - i &gt;= RightMin</c>
/// and the word is at least <c>MinWordLength</c> long. Words over 255 UTF-16 units are not hyphenated.
/// </para>
/// </remarks>
public sealed class PatternHyphenator : IHyphenator
{
    private const int MaxWordLength = 255;

    private readonly PatternSet _set;

    internal PatternHyphenator(string? language, PatternSet set, HyphenationOptions? options)
    {
        Language = language;
        _set = set;
        HyphenationOptions o = options ?? new HyphenationOptions();
        MinWordLength = Math.Max(2, o.MinWordLength);
        LeftMin = Math.Max(1, o.LeftMin ?? set.LeftMin);
        RightMin = Math.Max(1, o.RightMin ?? set.RightMin);
    }

    /// <summary>The bundled language this hyphenator was made for (for example <c>en-us</c>), or null for custom patterns.</summary>
    public string? Language { get; }

    /// <summary>Words shorter than this are not hyphenated.</summary>
    public int MinWordLength { get; }

    /// <summary>The fewest letters kept before a hyphen.</summary>
    public int LeftMin { get; }

    /// <summary>The fewest letters kept after a hyphen.</summary>
    public int RightMin { get; }

    /// <summary>Builds a hyphenator from TeX-style patterns (for example <c>hy3ph</c>, <c>.ex5am</c>) and exceptions with hyphens at the allowed points (<c>ta-ble</c>).</summary>
    /// <param name="patterns">Patterns: letters with digits between them, <c>.</c> for a word boundary. Matched case-insensitively.</param>
    /// <param name="exceptions">Words with <c>-</c> at each allowed break; they replace the patterns for that word. May be null.</param>
    /// <param name="options">Minimums; null for the defaults (word length 5, 2 letters each side).</param>
    /// <returns>The hyphenator.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="patterns"/> is null.</exception>
    public static PatternHyphenator FromPatterns(IEnumerable<string> patterns, IEnumerable<string>? exceptions = null, HyphenationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        return new PatternHyphenator(null, new PatternSet(patterns, exceptions, 2, 2), options);
    }

    /// <summary>Gives the hyphenation points of <paramref name="word"/> (<paramref name="language"/> is ignored: this hyphenator has one language).</summary>
    /// <param name="word">A word of letters.</param>
    /// <param name="language">Ignored.</param>
    /// <returns>Ascending indexes at which a hyphen may be put before the character; empty when there are none.</returns>
    public IReadOnlyList<int> GetBreaks(string word, string? language) => GetBreaks(word);

    /// <summary>Gives the hyphenation points of <paramref name="word"/>.</summary>
    /// <param name="word">A word of letters. Null gives no points.</param>
    /// <returns>Ascending indexes at which a hyphen may be put before the character; empty when there are none.</returns>
    public int[] GetBreaks(string? word) => word is null ? [] : GetBreaks(word.AsSpan());

    /// <summary>Gives the hyphenation points of <paramref name="word"/>.</summary>
    /// <param name="word">A word of letters.</param>
    /// <returns>Ascending indexes at which a hyphen may be put before the character; empty when there are none.</returns>
    public int[] GetBreaks(ReadOnlySpan<char> word)
    {
        int n = word.Length;
        if (n < MinWordLength || n < LeftMin + RightMin || n > MaxWordLength)
        {
            return [];
        }

        // s is ".word." in lower case; levels[k] is the level just before s[k].
        Span<char> s = stackalloc char[n + 2];
        s[0] = '.';
        for (int i = 0; i < n; i++)
        {
            s[i + 1] = char.ToLowerInvariant(word[i]);
        }

        s[n + 1] = '.';
        Span<byte> levels = stackalloc byte[n + 3];
        levels.Clear();

        if (_set.Exceptions.Count > 0
            && _set.Exceptions.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(s.Slice(1, n), out int[]? listed))
        {
            var kept = new List<int>(listed.Length);
            foreach (int p in listed)
            {
                if (p >= LeftMin && n - p >= RightMin)
                {
                    kept.Add(p);
                }
            }

            return [.. kept];
        }

        Dictionary<string, byte[]>.AlternateLookup<ReadOnlySpan<char>> lookup = _set.Patterns.GetAlternateLookup<ReadOnlySpan<char>>();
        int total = n + 2;
        for (int i = 0; i < total; i++)
        {
            int max = Math.Min(_set.MaxLength, total - i);
            for (int len = 1; len <= max; len++)
            {
                if (lookup.TryGetValue(s.Slice(i, len), out byte[]? lv))
                {
                    for (int k = 0; k < lv.Length; k++)
                    {
                        if (lv[k] > levels[i + k])
                        {
                            levels[i + k] = lv[k];
                        }
                    }
                }
            }
        }

        var result = new List<int>();
        for (int j = LeftMin; j <= n - RightMin; j++)
        {
            if ((levels[j + 1] & 1) == 1)
            {
                result.Add(j);
            }
        }

        return [.. result];
    }
}
