using System.Text;
using OneFont;

namespace OnePdf.Layout;

/// <summary>
/// Inline content to be laid out as a paragraph: styled spans plus the paragraph-level settings. Immutable once built (the span list is copied).
/// </summary>
/// <remarks>
/// Text is normalised before layout: line ends (<c>\n</c>, <c>\r\n</c>, <c>\r</c>, U+2028, U+2029) separate lines that each form their own bidi
/// paragraph and are never justified; tabs become spaces; other control characters are dropped; soft hyphens (U+00AD) are removed and remembered
/// as places where a line may end with a hyphen.
/// </remarks>
public sealed class TextParagraph
{
    /// <summary>The most characters one paragraph may hold.</summary>
    public const int MaxLength = 1 << 24;

    /// <summary>Creates a paragraph from styled spans.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="spans"/> is null.</exception>
    /// <exception cref="ArgumentException">The spans hold more than <see cref="MaxLength"/> characters.</exception>
    public TextParagraph(IEnumerable<TextSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        Spans = [.. spans];
        long total = 0;
        foreach (TextSpan s in Spans)
        {
            ArgumentNullException.ThrowIfNull(s);
            ArgumentNullException.ThrowIfNull(s.Text, nameof(spans));
            total += s.Text.Length;
        }

        if (total > MaxLength)
        {
            throw new ArgumentException($"A paragraph holds at most {MaxLength} characters.", nameof(spans));
        }
    }

    /// <summary>Creates a paragraph of one span.</summary>
    public TextParagraph(string text, TextStyle? style = null)
        : this([new TextSpan(text ?? throw new ArgumentNullException(nameof(text)), style)])
    {
    }

    /// <summary>The spans in logical order.</summary>
    public IReadOnlyList<TextSpan> Spans { get; }

    /// <summary>The style of spans that give none. Default: 11 pt, default family, black.</summary>
    public TextStyle DefaultStyle { get; init; } = new();

    /// <summary>The base direction. Default <see cref="ParagraphDirection.Auto"/>.</summary>
    public ParagraphDirection Direction { get; init; }

    /// <summary>Default <see cref="TextAlignment.Start"/>.</summary>
    public TextAlignment Alignment { get; init; }

    /// <summary>Indent of the first line of the paragraph, on its start side, in points. Default 0.</summary>
    public double FirstLineIndent { get; init; }

    /// <summary>Where words may be hyphenated besides the soft hyphens in the text. Null (the default) means nowhere.</summary>
    public IHyphenator? Hyphenator { get; init; }

    /// <summary>
    /// When a justified line has no space to widen, spread the extra width between its characters instead. Default false: such a line
    /// stays at the start edge, which is what happens to a single long word.
    /// </summary>
    public bool JustifyWithLetterSpacing { get; init; }

    /// <summary>Shapes the text: chooses faces, shapes clusters, finds break opportunities. The result lays out at any width.</summary>
    /// <param name="fonts">The fonts to resolve families against.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fonts"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A style has a size, spacing or line height that is not finite or not positive where it must be.</exception>
    /// <exception cref="FontFormatException">A font file is damaged.</exception>
    /// <exception cref="InvalidOperationException">A family cannot be resolved and the default family is not registered either.</exception>
    public ShapedParagraph Shape(TextFontRegistry fonts)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        if (!double.IsFinite(FirstLineIndent))
        {
            throw new ArgumentException("The indent must be finite.");
        }

        DefaultStyle.Validate();
        return new ShapedParagraph(this, fonts);
    }
}

/// <summary>A cleaned-up paragraph text with the span structure it came from.</summary>
internal sealed class NormalizedText
{
    public string Text { get; private set; } = string.Empty;

    /// <summary>Start index in <see cref="Text"/> of each span that kept characters (ascending), and its style.</summary>
    public List<int> SpanStarts { get; } = [];

    public List<TextStyle> Styles { get; } = [];

    /// <summary>Indexes in <see cref="Text"/> before which a soft hyphen stood.</summary>
    public List<int> SoftHyphens { get; } = [];

    public static NormalizedText Create(TextParagraph p)
    {
        var n = new NormalizedText();
        var sb = new StringBuilder();
        foreach (TextSpan span in p.Spans)
        {
            TextStyle style = span.Style ?? p.DefaultStyle;
            style.Validate();
            int start = sb.Length;
            string t = span.Text;
            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                switch (c)
                {
                    case '\r':
                        if (i + 1 < t.Length && t[i + 1] == '\n')
                        {
                            i++;
                        }

                        sb.Append('\n');
                        break;
                    case '\n' or '\u2028' or '\u2029':
                        sb.Append('\n');
                        break;
                    case '\u00AD':
                        n.SoftHyphens.Add(sb.Length);
                        break;
                    case '\t':
                        sb.Append(' ');
                        break;
                    default:
                        if (!char.IsControl(c))
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            if (sb.Length > start)
            {
                n.SpanStarts.Add(start);
                n.Styles.Add(style);
            }
        }

        if (n.SpanStarts.Count == 0)
        {
            n.SpanStarts.Add(0);
            n.Styles.Add(p.DefaultStyle);
        }

        n.Text = sb.ToString();
        return n;
    }
}
