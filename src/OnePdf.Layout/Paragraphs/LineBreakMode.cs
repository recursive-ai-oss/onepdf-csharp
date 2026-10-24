namespace OnePdf.Layout;

/// <summary>How a paragraph is divided into lines.</summary>
public enum LineBreakMode
{
    /// <summary>First fit: every line takes as much text as fits. Fast, and the default.</summary>
    Greedy = 0,

    /// <summary>
    /// Total fit after Knuth and Plass ("Breaking Paragraphs into Lines", Software: Practice and Experience 11, 1981): the breaks of the
    /// whole paragraph are chosen together so that the sum of the lines' demerits is least. Lines are evened out, hyphens are used where they
    /// pay for themselves, and a loose line is not bought with a tight one. See <see cref="KnuthPlassOptions"/>.
    /// </summary>
    KnuthPlass,
}

/// <summary>
/// The knobs of <see cref="LineBreakMode.KnuthPlass"/>. The names and default values follow the model of the paper and Knuth's
/// <i>Digital Typography</i> (chapter 3) and <i>The TeXbook</i> (chapters 14 and 15): a line gets a badness from how far its
/// spaces must stretch or shrink, and demerits from badness, penalties and neighbours.
/// </summary>
/// <remarks>
/// <para>How a line is judged. Spaces stretch by half and shrink by a third of their width. The adjustment ratio <c>r</c> is the needed
/// change over the available change, the badness is <c>100·|r|³</c> (at most 10000), and a line with <c>r &lt; -1</c> is too full. In justified
/// text a short line without a space to stretch has the worst badness. In left, right or centred text a line counts as ragged: it cannot
/// shrink, and its slack is measured against a quarter of the line width. The last line of a paragraph
/// and any line before a hard line end are free to be short. The line demerits are <c>(LinePenalty + badness)² + penalty²</c> (a
/// negative penalty is subtracted), plus the extra demerits below.</para>
/// <para>Passes. When the paragraph has hyphenation points and <see cref="PreTolerance"/> is not negative, a first pass without hyphens
/// accepts lines up to <see cref="PreTolerance"/>; if no solution exists, a second pass with hyphens accepts up to
/// <see cref="Tolerance"/>; if that fails too, the same with <see cref="EmergencyStretch"/>; and a last pass accepts any line that is not
/// too full and, where even that is impossible
/// (a word wider than the line), puts the unbreakable piece on a line of its own, as greedy does. Each paragraph line (separated by a hard
/// line end) is solved on its own. If the search grows beyond its bounds (see <see cref="MaxItems"/>) that paragraph line falls back to
/// greedy; every fallback adds a message to <see cref="ShapedParagraph.Warnings"/>.</para>
/// <para>Limits. Memory and time are proportional to the number of break opportunities times the number of candidate line starts within
/// one line width; more than 4096 candidates at once, or more than <see cref="MaxItems"/> clusters in a paragraph line, give up to greedy.</para>
/// </remarks>
public sealed record KnuthPlassOptions
{
    /// <summary>The most clusters in one paragraph line (between hard line ends) that are optimised; longer ones are broken greedily. Default 500000.</summary>
    public const int MaxItems = 500_000;

    /// <summary>The defaults.</summary>
    public static KnuthPlassOptions Default { get; } = new();

    /// <summary>
    /// Extra stretch, as a share of the line width, that every line may take in the second attempt of the main pass: a paragraph that
    /// has no setting within <see cref="Tolerance"/> is tried again with this much more room in each line (what TeX calls emergency
    /// stretch) before any line is accepted however bad it is. 0 turns it off. Default 0.1.
    /// </summary>
    public double EmergencyStretch { get; init; } = 0.1;

    /// <summary>The worst badness accepted in the main pass, 0 to 10000. Default 200 (<c>r</c> up to about 1.26).</summary>
    public int Tolerance { get; init; } = 200;

    /// <summary>
    /// The worst badness accepted in the first pass, which has no hyphenation points. Negative skips that pass, so hyphens are always
    /// considered. Default 100: a paragraph that sets well without hyphens gets none.
    /// </summary>
    public int PreTolerance { get; init; } = 100;

    /// <summary>Added to every line's badness before squaring: the price of one more line. Default 10.</summary>
    public double LinePenalty { get; init; } = 10;

    /// <summary>Penalty for ending a line at a soft hyphen or hyphenation point, from -10000 (exclusive) to 10000. 10000 forbids such breaks. Default 50.</summary>
    public double HyphenPenalty { get; init; } = 50;

    /// <summary>Penalty for ending a line after a hyphen that is in the text (<c>well-known</c>), same range. Default 50.</summary>
    public double ExplicitHyphenPenalty { get; init; } = 50;

    /// <summary>Extra demerits when two lines in a row end in a hyphen. Default 10000.</summary>
    public double ConsecutiveHyphenDemerits { get; init; } = 10000;

    /// <summary>Extra demerits when the last but one line ends in a hyphen. Default 5000.</summary>
    public double FinalHyphenDemerits { get; init; } = 5000;

    /// <summary>Extra demerits when neighbouring lines differ in looseness by more than one class (very loose, loose, decent, tight). Default 10000.</summary>
    public double AdjacentFitnessDemerits { get; init; } = 10000;

    /// <summary>
    /// Asks for this many lines more (positive) or fewer (negative) than the optimum, if the paragraph can be set so; the nearest possible
    /// count is used otherwise. Default 0.
    /// </summary>
    public int Looseness { get; init; }

    internal void Validate()
    {
        if (Tolerance is < 0 or > 10000)
        {
            throw new ArgumentException("The tolerance must be between 0 and 10000.");
        }

        if (PreTolerance > 10000)
        {
            throw new ArgumentException("The pre-tolerance must be at most 10000.");
        }

        if (!(EmergencyStretch >= 0) || EmergencyStretch > 10)
        {
            throw new ArgumentException("The emergency stretch must be between 0 and 10.");
        }

        CheckFinite(LinePenalty, nameof(LinePenalty));
        CheckPenalty(HyphenPenalty, nameof(HyphenPenalty));
        CheckPenalty(ExplicitHyphenPenalty, nameof(ExplicitHyphenPenalty));
        CheckFinite(ConsecutiveHyphenDemerits, nameof(ConsecutiveHyphenDemerits));
        CheckFinite(FinalHyphenDemerits, nameof(FinalHyphenDemerits));
        CheckFinite(AdjacentFitnessDemerits, nameof(AdjacentFitnessDemerits));
        if (Looseness is < -1000 or > 1000)
        {
            throw new ArgumentException("The looseness must be between -1000 and 1000.");
        }
    }

    private static void CheckFinite(double v, string name)
    {
        if (!double.IsFinite(v) || Math.Abs(v) > 1e6)
        {
            throw new ArgumentException($"{name} must be finite and at most 1000000 in size.");
        }
    }

    private static void CheckPenalty(double v, string name)
    {
        if (!double.IsFinite(v) || v <= -10000 || v > 10000)
        {
            throw new ArgumentException($"{name} must be above -10000 and at most 10000.");
        }
    }
}
