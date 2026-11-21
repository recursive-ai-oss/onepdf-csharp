namespace OnePdf.Layout;

/// <summary>
/// The text settings a page (or the whole document) gives all its text unless the text says otherwise. Passed to
/// <see cref="PageComposer.DefaultTextStyle(Action{DefaultTextStyleDescriptor})"/> and <see cref="DocumentComposer.DefaultTextStyle(Action{DefaultTextStyleDescriptor})"/>.
/// A setting made here is inherited field by field: text that sets its own size keeps it and still gets the page's font and colour.
/// </summary>
public sealed class DefaultTextStyleDescriptor : TextFormatDescriptor<DefaultTextStyleDescriptor>
{
    internal DefaultTextStyleDescriptor(SpanFormat format)
        : base(format)
    {
    }

    private protected override void Changed()
    {
    }

    /// <summary>The font family, as for <see cref="TextDescriptor.FontFamily(string[])"/>; several families name fallbacks.</summary>
    /// <param name="families">Registered family names, aliases (<c>Sans</c>, <c>Serif</c>, <c>Mono</c>) or standard font names.</param>
    public DefaultTextStyleDescriptor FontFamily(params string[] families) => Set(f => f.Families = families.Length == 0 ? null : families);

    /// <summary>Uses italic faces.</summary>
    public DefaultTextStyleDescriptor Italic() => Set(f => f.Italic = true);

    /// <summary>Draws a line under the text.</summary>
    public DefaultTextStyleDescriptor Underline() => Set(f => f.Underline = true);

    /// <summary>Draws a line through the text.</summary>
    public DefaultTextStyleDescriptor Strike() => Set(f => f.Strike = true);

    /// <summary>Extra space after every character, in points.</summary>
    /// <param name="points">Extra advance; negative tightens.</param>
    public DefaultTextStyleDescriptor LetterSpacing(double points) => Set(f => f.LetterSpacing = points);

    /// <summary>Line distance as a multiple of the font size.</summary>
    /// <param name="factor">The multiple; text sets 1.2 when nothing else is said.</param>
    public DefaultTextStyleDescriptor LineHeight(double factor) => Set(f => f.LineHeight = factor);

    /// <summary>Turns an OpenType feature on or off for all text (see <see cref="TextDescriptor.Feature(string, bool)"/>).</summary>
    /// <param name="tag">Four printable ASCII characters.</param>
    /// <param name="on">True to turn it on (the default), false to turn it off.</param>
    public DefaultTextStyleDescriptor Feature(string tag, bool on = true) => Set(f => f.AddFeature(new TextFeature(tag, on ? 1 : 0)));

    /// <summary>Sets an OpenType feature to a value (see <see cref="TextDescriptor.Feature(string, int)"/>).</summary>
    /// <param name="tag">Four printable ASCII characters.</param>
    /// <param name="value">0 off, 1 on, or an alternate number.</param>
    public DefaultTextStyleDescriptor Feature(string tag, int value) => Set(f => f.AddFeature(new TextFeature(tag, value)));

    /// <summary>Turns the standard ligatures (<c>liga</c>, <c>clig</c>) on or off.</summary>
    /// <param name="on">False separates "fi", "fl" and the like into their letters.</param>
    public DefaultTextStyleDescriptor Ligatures(bool on = true) => Set(f =>
    {
        f.AddFeature(new TextFeature("liga", on ? 1 : 0));
        f.AddFeature(new TextFeature("clig", on ? 1 : 0));
    });

    /// <summary>Turns pair kerning (<c>kern</c>) on or off.</summary>
    /// <param name="on">False uses the plain advance widths.</param>
    public DefaultTextStyleDescriptor Kerning(bool on = true) => Set(f => f.AddFeature(new TextFeature("kern", on ? 1 : 0)));
}

/// <summary>Which style fields of a fluent-built <see cref="TextStyle"/> were not set by the author and so may come from a page default.</summary>
[Flags]
internal enum TextInherit
{
    None = 0,

    /// <summary>The style came from the fluent API, so a page default's features join its own.</summary>
    Marked = 1,
    Families = 2,
    Size = 4,
    Color = 8,
    Palette = 16,
    Weight = 32,
    Italic = 64,
    Underline = 128,
    Strike = 256,
    LetterSpacing = 512,
    LineHeight = 1024,
}

/// <summary>Applies the default text style of a page to the elements the fluent API built for it.</summary>
internal static class PageTextStyle
{
    /// <summary>The fields a merged span format leaves unset.</summary>
    public static TextInherit Unset(SpanFormat m)
    {
        TextInherit f = TextInherit.Marked;
        f |= m.Families is null ? TextInherit.Families : 0;
        f |= m.Size is null ? TextInherit.Size : 0;
        f |= m.Color is null ? TextInherit.Color : 0;
        f |= m.Palette is null ? TextInherit.Palette : 0;
        f |= m.Weight is null ? TextInherit.Weight : 0;
        f |= m.Italic is null ? TextInherit.Italic : 0;
        f |= m.Underline is null ? TextInherit.Underline : 0;
        f |= m.Strike is null ? TextInherit.Strike : 0;
        f |= m.LetterSpacing is null ? TextInherit.LetterSpacing : 0;
        f |= m.LineHeight is null ? TextInherit.LineHeight : 0;
        return f;
    }

    /// <summary>The settings of <paramref name="page"/> over those of <paramref name="document"/>; null when neither sets anything.</summary>
    public static SpanFormat? Combine(SpanFormat? document, SpanFormat? page)
    {
        if (document is null || page is null)
        {
            return page ?? document;
        }

        return new SpanFormat
        {
            Families = page.Families ?? document.Families,
            Size = page.Size ?? document.Size,
            Color = page.Color ?? document.Color,
            Palette = page.Palette ?? document.Palette,
            Weight = page.Weight ?? document.Weight,
            Italic = page.Italic ?? document.Italic,
            Underline = page.Underline ?? document.Underline,
            Strike = page.Strike ?? document.Strike,
            LetterSpacing = page.LetterSpacing ?? document.LetterSpacing,
            LineHeight = page.LineHeight ?? document.LineHeight,
            Features = page.Features is null || document.Features is null ? page.Features ?? document.Features : [.. document.Features, .. page.Features],
        };
    }

    /// <summary>Fills the unset text settings of everything on <paramref name="page"/>.</summary>
    public static void Apply(LayoutPage page, SpanFormat defaults)
    {
        var seen = new HashSet<Element>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<Element>();
        foreach (Element? root in (Element?[])[page.Header, page.Footer, page.FirstHeader, page.FirstFooter, page.EvenHeader, page.EvenFooter, page.Background, page.Foreground, page.Content])
        {
            if (root is not null)
            {
                stack.Push(root);
            }
        }

        while (stack.Count > 0)
        {
            Element e = stack.Pop();
            if (!seen.Add(e))
            {
                continue;
            }

            switch (e)
            {
                case Text t:
                    t.InheritFrom(defaults);
                    for (int i = 0; i < t.Spans.Count; i++)
                    {
                        TextSpan span = t.Spans[i];
                        if (span.Style is { Inherit: not TextInherit.None } style)
                        {
                            t.Spans[i] = span with { Style = Fill(style, defaults) };
                        }

                        if (span.Inline is InlineElement inline)
                        {
                            stack.Push(inline.Child);
                        }
                    }

                    break;
                case PageNumber n:
                    n.InheritFrom(defaults);
                    break;
                case Barcode barcode:
                    barcode.InheritFrom(defaults);
                    break;
                case PageNumberText n:
                    n.InheritFrom(defaults);
                    break;
                case DynamicText n:
                    n.InheritFrom(defaults);
                    break;
                case ItemList list:
                    list.InheritFrom(defaults);
                    foreach (ListItem item in list.Items)
                    {
                        if (item.Marker is not null)
                        {
                            stack.Push(item.Marker);
                        }
                    }

                    break;
                case TableOfContents toc:
                    InheritToc(toc.Style, defaults);
                    break;
            }

            // A component is composed when it is laid out; its content is the component's own business.
            if (e is not Component)
            {
                e.PushChildren(stack);
            }
        }
    }

    private static void InheritToc(TocLevelStyle s, SpanFormat d)
    {
        if (s.FontName is null && d.Families is { Length: > 0 } fam)
        {
            s.FontName = fam[0];
        }

        s.Weight ??= d.Weight;
        s.Italic ??= d.Italic;
        s.Size ??= d.Size;
        s.Color ??= d.Color;
        s.LineSpacing ??= d.LineHeight;
    }

    private static TextStyle Fill(TextStyle s, SpanFormat d)
    {
        TextInherit f = s.Inherit;
        IReadOnlyList<TextFeature> features = s.Features;
        if (d.Features is { Length: > 0 })
        {
            features = s.Features.Count == 0 ? d.Features : [.. d.Features, .. s.Features];
        }

        return s with
        {
            Inherit = TextInherit.None,
            Families = f.HasFlag(TextInherit.Families) && d.Families is { } fam ? fam : s.Families,
            Size = f.HasFlag(TextInherit.Size) && d.Size is { } size ? size : s.Size,
            Color = f.HasFlag(TextInherit.Color) && d.Color is { } color ? color : s.Color,
            Palette = f.HasFlag(TextInherit.Palette) && d.Palette is { } palette ? palette : s.Palette,
            Weight = f.HasFlag(TextInherit.Weight) && d.Weight is { } weight ? weight : s.Weight,
            Italic = f.HasFlag(TextInherit.Italic) && d.Italic is { } italic ? italic : s.Italic,
            Underline = f.HasFlag(TextInherit.Underline) && d.Underline is { } underline ? underline : s.Underline,
            Strike = f.HasFlag(TextInherit.Strike) && d.Strike is { } strike ? strike : s.Strike,
            LetterSpacing = f.HasFlag(TextInherit.LetterSpacing) && d.LetterSpacing is { } spacing ? spacing : s.LetterSpacing,
            LineHeight = f.HasFlag(TextInherit.LineHeight) && d.LineHeight is { } lh ? lh : s.LineHeight,
            Features = features,
        };
    }
}
