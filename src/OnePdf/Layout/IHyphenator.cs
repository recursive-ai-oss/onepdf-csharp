namespace OnePdf.Layout;

/// <summary>
/// Finds the places where a word may be split with a hyphen. There is no bundled data: hyphenation is whatever the caller plugs in. Soft
/// hyphens (U+00AD) in the text work without a hyphenator.
/// </summary>
/// <remarks>
/// The interface lives in the core assembly, in the <c>OnePdf.Layout</c> namespace, so that both layout packages
/// (<c>RecursiveAI.OnePdf.Layout</c> and <c>RecursiveAI.OnePdf.Layout.Core</c>) and the hyphenation package share one type.
/// </remarks>
public interface IHyphenator
{
    /// <summary>Gives the hyphenation points of one word.</summary>
    /// <param name="word">A run of letters (and combining marks), at least two characters long.</param>
    /// <param name="language">The BCP 47 tag of the word's style, or null.</param>
    /// <returns>
    /// UTF-16 indexes into <paramref name="word"/>, each between 1 and <c>word.Length - 1</c>: a hyphen may be put before the character at the index.
    /// Out-of-range or repeated values are ignored. Must give the same answer for the same arguments.
    /// </returns>
    IReadOnlyList<int> GetBreaks(string word, string? language);
}
