namespace OnePdf.Layout;

/// <summary>Where text sits relative to the baseline.</summary>
public enum ScriptPosition
{
    /// <summary>On the baseline.</summary>
    Normal = 0,

    /// <summary>Raised and smaller. The font's <c>sups</c> glyphs are used when it has them; otherwise the text is scaled and shifted.</summary>
    Superscript,

    /// <summary>Lowered and smaller. The font's <c>subs</c> glyphs are used when it has them; otherwise the text is scaled and shifted.</summary>
    Subscript,
}

/// <summary>How the lines of a paragraph are placed between its edges.</summary>
public enum TextAlignment
{
    /// <summary>The edge where lines start: left for left-to-right paragraphs, right for right-to-left ones.</summary>
    Start = 0,

    /// <summary>The edge where lines end.</summary>
    End,

    /// <summary>The left edge whatever the direction.</summary>
    Left,

    /// <summary>The right edge whatever the direction.</summary>
    Right,

    /// <summary>Centred.</summary>
    Center,

    /// <summary>Both edges. Every line but the last of a paragraph (and lines before a hard break) is widened at its spaces; those last lines align to the start.</summary>
    Justify,
}

/// <summary>The base direction of a paragraph (Unicode UAX #9 rule P2/P3).</summary>
public enum ParagraphDirection
{
    /// <summary>Taken from the first strong character of each paragraph, left to right when there is none.</summary>
    Auto = 0,

    /// <summary>Left to right.</summary>
    LeftToRight,

    /// <summary>Right to left.</summary>
    RightToLeft,
}

/// <summary>The look of a stretch of text. Immutable; make a changed copy with <c>with</c>.</summary>
public sealed record TextStyle
{
    /// <summary>
    /// Font families in order of preference, each a registered family name (matched without regard to case), an alias such as
    /// <c>Sans</c>, <c>Serif</c> or <c>Mono</c>, or the name of a standard font (<c>Helvetica</c>, <c>Times-Roman</c>, <c>Courier-Bold</c>, ...).
    /// A standard font is not shaped and cannot be combined with others. Characters the first family lacks come from the next ones, then from
    /// the registry's fallback families. Empty (the default) means the registry's default family.
    /// </summary>
    public IReadOnlyList<string> Families { get; init; } = [];

    /// <summary>Font size in points. Default 11. Must be positive and finite.</summary>
    public double Size { get; init; } = 11;

    /// <summary>Text and decoration colour. Default black.</summary>
    public LayoutColor Color { get; init; }

    /// <summary>Requested weight, 1 to 1000 (400 regular, 700 bold). The nearest registered face is used; weights are never synthesized.</summary>
    public int Weight { get; init; } = 400;

    /// <summary>Whether italic (or oblique) faces are wanted.</summary>
    public bool Italic { get; init; }

    /// <summary>Requested width class, 1 (ultra condensed) to 9 (ultra expanded), 5 normal.</summary>
    public int Width { get; init; } = 5;

    /// <summary>Extra space after every character, in points. Default 0. Negative tightens.</summary>
    public double LetterSpacing { get; init; }

    /// <summary>Draws a line under the text, at the position and thickness the font recommends (<c>post</c> table).</summary>
    public bool Underline { get; init; }

    /// <summary>Draws a line through the text, at the position and thickness the font recommends (<c>OS/2</c> table).</summary>
    public bool Strike { get; init; }

    /// <summary>Superscript or subscript. Default <see cref="ScriptPosition.Normal"/>.</summary>
    public ScriptPosition Position { get; init; }

    /// <summary>
    /// Line height as a multiple of the font size. When null (the default) the font's own line height (ascent, descent and line gap)
    /// is used. Extra space is split above and below the text. The tallest span on a line decides the line's height.
    /// </summary>
    public double? LineHeight { get; init; }

    /// <summary>A BCP 47 language tag such as <c>tr</c>, used to pick language-specific glyph forms and passed to the hyphenator. Null for none.</summary>
    public string? Language { get; init; }

    /// <summary>
    /// A link target carried with the text as data (a URI, or <c>#name</c> for a destination). The paragraph reports where the text landed
    /// (<see cref="TextLinkArea"/>); placing the annotation is the caller's job.
    /// </summary>
    public string? Link { get; init; }

    /// <summary>
    /// Which palette colour glyphs (colour emoji, colour icon fonts with OpenType <c>COLR</c> version 0 layers) are drawn with. Null, the default,
    /// takes <see cref="LayoutOptions.ColorPalette"/> of the document. Text in a font without colour glyphs is not affected.
    /// </summary>
    public ColorPalette? Palette { get; init; }

    internal void Validate()
    {
        if (!(Size > 0) || !double.IsFinite(Size) || Size > 10_000)
        {
            throw new ArgumentException("The font size must be positive, finite and at most 10000.");
        }

        if (!double.IsFinite(LetterSpacing) || Math.Abs(LetterSpacing) > 10_000)
        {
            throw new ArgumentException("The letter spacing must be finite.");
        }

        if (LineHeight is { } lh && (!(lh > 0) || !double.IsFinite(lh) || lh > 1000))
        {
            throw new ArgumentException("The line height must be positive and finite.");
        }
    }
}

/// <summary>A piece of paragraph text in one style.</summary>
/// <param name="Text">The characters. <c>\n</c>, <c>\r\n</c>, U+2028 and U+2029 end a line and start a new bidi paragraph; tabs count as spaces; other control characters are dropped; U+00AD is a soft hyphen.</param>
/// <param name="Style">The style, or null for the paragraph's <see cref="TextParagraph.DefaultStyle"/>.</param>
public sealed record TextSpan(string Text, TextStyle? Style = null)
{
    /// <summary>
    /// When set, the span stands for this object instead of text: <see cref="Text"/> is ignored and the object is laid out as one character in the
    /// line, in the font of <see cref="Style"/> (which gives the text metrics the object is aligned against). See <see cref="InlineObject"/>.
    /// </summary>
    public InlineObject? Inline { get; init; }

    /// <summary>A span that holds an inline object.</summary>
    /// <param name="inline">The object.</param>
    /// <param name="style">The style of the text around it, or null for the paragraph's <see cref="TextParagraph.DefaultStyle"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inline"/> is null.</exception>
    public static TextSpan ForInline(InlineObject inline, TextStyle? style = null) =>
        new(string.Empty, style) { Inline = inline ?? throw new ArgumentNullException(nameof(inline)) };
}

/// <summary>Where a styled run with a <see cref="TextStyle.Link"/> landed, in the coordinates the lines were drawn in (y up).</summary>
/// <param name="Target">The span's <see cref="TextStyle.Link"/>.</param>
/// <param name="X">Left edge.</param>
/// <param name="Y">Bottom edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height (the line's height).</param>
public readonly record struct TextLinkArea(string Target, double X, double Y, double Width, double Height);

/// <summary>
/// Finds the places where a word may be split with a hyphen. There is no bundled data: hyphenation is whatever the caller plugs in. Soft
/// hyphens (U+00AD) in the text work without a hyphenator.
/// </summary>
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
