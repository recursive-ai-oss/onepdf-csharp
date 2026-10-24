using System.Collections.Concurrent;
using OnePdf.Layout;

namespace OnePdf.Hyphenation;

/// <summary>
/// The bundled hyphenation patterns. Each language is read from an embedded, compressed resource the first time it is asked for and
/// kept for the life of the process; nothing is loaded for languages you do not use.
/// </summary>
/// <remarks>
/// <para>
/// Bundled language ids: <c>bg</c>, <c>cy</c>, <c>da</c>, <c>de-1901</c>, <c>de-1996</c>, <c>de-ch-1901</c>, <c>el-monoton</c>, <c>en-gb</c>,
/// <c>en-us</c>, <c>es</c>, <c>et</c>, <c>fr</c>, <c>ga</c>, <c>gl</c>, <c>it</c>, <c>la</c>, <c>lt</c>, <c>nb</c>, <c>nl</c>, <c>nn</c>, <c>no</c>,
/// <c>pl</c>, <c>pt</c>, <c>sk</c>, <c>sl</c>, <c>uk</c>. Language tags are mapped to them: <c>en</c> and its regions mean American
/// English except the British-spelling regions (GB, AU, NZ, IE, ZA, IN, SG, HK); <c>de</c> means the reformed spelling, with
/// <c>de-1901</c> the traditional one and <c>de-CH</c> the Swiss traditional patterns (<c>de-CH-1996</c> is reformed); <c>el</c> is
/// monotonic Greek; every other tag is matched by its primary subtag (<c>pt-BR</c> is <c>pt</c>). Tags are case-insensitive and may use
/// <c>_</c>.
/// </para>
/// <para>
/// Not bundled, because the pattern files' terms are not permissive enough for this package: Swedish, Finnish, Icelandic, Catalan,
/// Croatian, Russian, Turkish, Esperanto, Czech, Hungarian, Latvian and Romanian. <see cref="PatternHyphenator.FromPatterns"/> takes patterns
/// you hold under terms you accept.
/// </para>
/// </remarks>
public static class Hyphenators
{
    private static readonly string[] Ids =
    [
        "bg", "cy", "da", "de-1901", "de-1996", "de-ch-1901", "el-monoton", "en-gb", "en-us", "es", "et", "fr", "ga", "gl", "it", "la",
        "lt", "nb", "nl", "nn", "no", "pl", "pt", "sk", "sl", "uk",
    ];

    private static readonly string[] BritishRegions = ["gb", "au", "nz", "ie", "za", "in", "sg", "hk"];

    private static readonly ConcurrentDictionary<string, Lazy<PatternSet>> Loaded = new(StringComparer.Ordinal);

    /// <summary>The ids of the bundled languages, in alphabetical order.</summary>
    public static IReadOnlyList<string> Languages => Ids;

    /// <summary>Tells whether patterns are bundled for a language tag.</summary>
    /// <param name="language">A BCP 47 tag such as <c>en-GB</c> or <c>pt_BR</c>.</param>
    /// <returns>True when <see cref="For"/> would return a hyphenator.</returns>
    public static bool IsSupported(string? language) => Resolve(language) is not null;

    /// <summary>Maps a language tag to the id of the bundled patterns that serve it.</summary>
    /// <param name="language">A BCP 47 tag; null or empty gives null.</param>
    /// <returns>An id from <see cref="Languages"/>, or null when no patterns are bundled for the language.</returns>
    public static string? Resolve(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        string[] parts = language.Trim().ToLowerInvariant().Replace('_', '-').Split('-');
        string primary = parts[0];
        switch (primary)
        {
            case "en":
                foreach (string part in parts.AsSpan(1))
                {
                    if (part.Length == 2 && Array.IndexOf(BritishRegions, part) >= 0)
                    {
                        return "en-gb";
                    }
                }

                return "en-us";
            case "de":
                bool swiss = parts.Length > 1 && parts[1] == "ch";
                if (Array.IndexOf(parts, "1901") >= 0)
                {
                    return swiss ? "de-ch-1901" : "de-1901";
                }

                return swiss && Array.IndexOf(parts, "1996") < 0 ? "de-ch-1901" : "de-1996";
            case "el":
                return "el-monoton";
            default:
                return Array.IndexOf(Ids, primary) >= 0 ? primary : null;
        }
    }

    /// <summary>Gives a hyphenator for one language.</summary>
    /// <param name="language">A BCP 47 tag; see <see cref="Resolve"/> for how tags map to patterns.</param>
    /// <param name="options">Minimums; null uses the language's own and a minimum word length of 5.</param>
    /// <returns>The hyphenator, or null when no patterns are bundled for the language. The patterns are loaded on first use.</returns>
    public static PatternHyphenator? For(string? language, HyphenationOptions? options = null)
    {
        string? id = Resolve(language);
        return id is null ? null : new PatternHyphenator(id, Load(id), options);
    }

    /// <summary>
    /// Gives a hyphenator that picks the patterns from the language of each word's style (what <c>TextDescriptor.Language</c> sets),
    /// so one object serves a multilingual document.
    /// </summary>
    /// <param name="options">Minimums for every language; null uses each language's own and a minimum word length of 5.</param>
    /// <param name="defaultLanguage">The language to use for text with no language set. Null (the default) leaves such text unhyphenated.</param>
    /// <returns>A hyphenator that gives no points for languages with no bundled patterns.</returns>
    public static IHyphenator Auto(HyphenationOptions? options = null, string? defaultLanguage = null) =>
        new AutoHyphenator(options, defaultLanguage);

    private static PatternSet Load(string id) =>
        Loaded.GetOrAdd(id, static key => new Lazy<PatternSet>(() => Read(key), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private static PatternSet Read(string id)
    {
        using Stream stream = typeof(Hyphenators).Assembly.GetManifestResourceStream("OnePdf.Hyphenation." + id + ".bin")
            ?? throw new InvalidOperationException("The pattern data for '" + id + "' is missing from the package.");
        return PatternSet.Read(stream);
    }

    private sealed class AutoHyphenator : IHyphenator
    {
        private readonly HyphenationOptions? _options;
        private readonly string? _default;
        private readonly ConcurrentDictionary<string, PatternHyphenator> _byId = new(StringComparer.Ordinal);

        public AutoHyphenator(HyphenationOptions? options, string? defaultLanguage)
        {
            _options = options;
            _default = defaultLanguage;
        }

        public IReadOnlyList<int> GetBreaks(string word, string? language)
        {
            string? id = Resolve(string.IsNullOrWhiteSpace(language) ? _default : language);
            if (id is null)
            {
                return [];
            }

            PatternHyphenator h = _byId.GetOrAdd(id, key => new PatternHyphenator(key, Load(key), _options));
            return h.GetBreaks(word.AsSpan());
        }
    }
}
