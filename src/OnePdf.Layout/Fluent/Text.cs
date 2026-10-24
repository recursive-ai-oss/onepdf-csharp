namespace OnePdf.Layout;

public partial class Slot
{
    /// <summary>A paragraph of text in one style. Style it with the returned descriptor; add differently styled spans with <see cref="TextDescriptor.Span(string)"/>.</summary>
    /// <param name="content">The text. <c>\n</c> starts a new line and a new bidirectional paragraph.</param>
    /// <example>
    /// <code>
    /// col.Item().Text("Quarterly report").FontSize(20).Bold();
    /// </code>
    /// </example>
    public TextDescriptor Text(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var d = new TextDescriptor(this, InheritedAlignment);
        d.AddText(content);
        Put(d.Build());
        d.GoLive();
        return d;
    }

    /// <summary>Text made of spans, line breaks and page numbers, composed by a delegate.</summary>
    /// <param name="compose">Sets the default style and adds spans and page numbers.</param>
    /// <remarks>
    /// Page numbers are not inline characters of a paragraph: a text that contains <see cref="TextDescriptor.CurrentPageNumber"/>,
    /// <see cref="TextDescriptor.TotalPages"/> or <see cref="TextDescriptor.SectionPages"/> is laid out as one line, from a row of its
    /// pieces (a compact <see cref="Row"/>), and does not wrap. Spaces at the edges of a piece become fixed gaps one space wide in the
    /// piece's size. The line is as wide as its pieces, so alignment on the text or on a slot around it places it. This fits the usual
    /// "Page 3 of 12" in a footer.
    /// </remarks>
    /// <example>
    /// <code>
    /// footer.AlignCenter().Text(t =>
    /// {
    ///     t.FontSize(9);
    ///     t.Span("Page ");
    ///     t.CurrentPageNumber();
    ///     t.Span(" of ");
    ///     t.TotalPages();
    /// });
    /// </code>
    /// </example>
    public void Text(Action<TextDescriptor> compose)
    {
        ArgumentNullException.ThrowIfNull(compose);
        var d = new TextDescriptor(this, InheritedAlignment);
        compose(d);
        Put(d.Build());
        d.GoLive();
    }
}

/// <summary>The settings a piece of text can differ in. A null setting inherits the surrounding default.</summary>
internal sealed class SpanFormat
{
    public string[]? Families;
    public double? Size;
    public LayoutColor? Color;
    public int? Weight;
    public bool? Italic;
    public bool? Underline;
    public bool? Strike;
    public double? LetterSpacing;
    public ScriptPosition? Position;
    public string? Language;
    public double? LineHeight;
    public int? ReservedDigits;
    public InlineDescriptor? Inline;

    public bool HasStyle => Families is not null || Size is not null || Color is not null || Weight is not null || Italic is not null
        || Underline is not null || Strike is not null || LetterSpacing is not null || Position is not null || Language is not null || LineHeight is not null;
}

/// <summary>Style settings shared by text, spans and page numbers.</summary>
/// <typeparam name="TSelf">The descriptor type returned from each call.</typeparam>
public abstract class TextFormatDescriptor<TSelf>
    where TSelf : TextFormatDescriptor<TSelf>
{
    private protected TextFormatDescriptor(SpanFormat format) => Format = format;

    internal SpanFormat Format { get; }

    /// <summary>Called after a setting changed, so the element is rebuilt.</summary>
    private protected abstract void Changed();

    private protected TSelf Set(Action<SpanFormat> change)
    {
        change(Format);
        Changed();
        return (TSelf)this;
    }

    /// <summary>The font size in points. Default 11.</summary>
    public TSelf FontSize(double points) => Set(f => f.Size = points);

    /// <summary>The text colour. Default black.</summary>
    public TSelf FontColor(LayoutColor color) => Set(f => f.Color = color);

    /// <summary>Weight 700.</summary>
    public TSelf Bold() => Set(f => f.Weight = 700);

    /// <summary>A weight from 1 to 1000 (400 regular, 700 bold). The nearest registered face is used; weights are never synthesized.</summary>
    public TSelf Weight(int weight) => Set(f => f.Weight = weight);
}

/// <summary>
/// Style and content of a text. Returned by <see cref="Slot.Text(string)"/> and given to the delegate of <see cref="Slot.Text(Action{TextDescriptor})"/>.
/// Settings apply to the whole text; <see cref="Span(string)"/> pieces can differ. Settings can be made in any order.
/// </summary>
/// <remarks>
/// The default font is Liberation Sans, embedded as a subset of the glyphs used. A family name is a registered family (see
/// <see cref="DocumentComposer.Fonts"/>), <c>Sans</c>, <c>Serif</c> or <c>Mono</c>, or the name of a standard font such as <c>Helvetica</c>
/// (unshaped, not embedded). Settings must be made while the delegate that handed out the slot is running.
/// </remarks>
public sealed class TextDescriptor : TextFormatDescriptor<TextDescriptor>
{
    private readonly Slot _slot;
    private readonly List<(string? Text, SpanFormat Format, int Kind)> _segments = [];
    private HorizontalAlignment _alignment;
    private readonly HorizontalAlignment _inheritedAlignment;
    private bool _alignmentSet;
    private bool _justify;
    private double _indent;
    private IHyphenator? _hyphenator;
    private LineBreakMode? _lineBreakMode;
    private KnuthPlassOptions? _knuthPlass;
    private bool _live;

    internal TextDescriptor(Slot slot, HorizontalAlignment inherited)
        : base(new SpanFormat())
    {
        _slot = slot;
        _inheritedAlignment = inherited;
    }

    private protected override void Changed() => Rebuild();

    internal void GoLive() => _live = true;

    private void Rebuild()
    {
        if (_live)
        {
            _slot.Replace(Build());
        }
    }

    private TextDescriptor Edit(Action change)
    {
        change();
        Rebuild();
        return this;
    }

    internal void AddText(string text) => _segments.Add((text, new SpanFormat(), 0));

    /// <summary>The font family (see the remarks of this type). Several families name fallbacks, tried in order for characters the first lacks.</summary>
    public TextDescriptor FontFamily(params string[] families) => Edit(() => Format.Families = families.Length == 0 ? null : families);

    /// <summary>Uses an italic face.</summary>
    public TextDescriptor Italic() => Edit(() => Format.Italic = true);

    /// <summary>Draws a line under the text where the font recommends.</summary>
    public TextDescriptor Underline() => Edit(() => Format.Underline = true);

    /// <summary>Draws a line through the text where the font recommends.</summary>
    public TextDescriptor Strike() => Edit(() => Format.Strike = true);

    /// <summary>Extra space after every character in points (negative tightens).</summary>
    public TextDescriptor LetterSpacing(double points) => Edit(() => Format.LetterSpacing = points);

    /// <summary>Line distance as a multiple of the font size. Default 1.2.</summary>
    public TextDescriptor LineHeight(double factor) => Edit(() => Format.LineHeight = factor);

    /// <summary>The language of the text, a BCP 47 tag such as <c>tr</c> or <c>ar</c>: selects language-specific glyph forms, is passed to the hyphenator and recorded in the structure tree.</summary>
    public TextDescriptor Language(string tag) => Edit(() => Format.Language = tag ?? throw new ArgumentNullException(nameof(tag)));

    /// <summary>Lines start at the start edge (left in left-to-right text). This is the default, unless the slot the text is in is aligned: <c>AlignCenter().Text(...)</c> centres the lines.</summary>
    public TextDescriptor AlignStart() => Edit(() => { _alignment = HorizontalAlignment.Start; _alignmentSet = true; _justify = false; });

    /// <summary>Lines are centred.</summary>
    public TextDescriptor AlignCenter() => Edit(() => { _alignment = HorizontalAlignment.Center; _alignmentSet = true; _justify = false; });

    /// <summary>Lines end at the end edge.</summary>
    public TextDescriptor AlignEnd() => Edit(() => { _alignment = HorizontalAlignment.End; _alignmentSet = true; _justify = false; });

    /// <summary>Lines start at the left edge whatever the direction.</summary>
    public TextDescriptor AlignLeft() => Edit(() => { _alignment = HorizontalAlignment.Left; _alignmentSet = true; _justify = false; });

    /// <summary>Lines end at the right edge whatever the direction.</summary>
    public TextDescriptor AlignRight() => Edit(() => { _alignment = HorizontalAlignment.Right; _alignmentSet = true; _justify = false; });

    /// <summary>Widens the spaces of every line but the last of each paragraph so that lines fill the width.</summary>
    public TextDescriptor Justify() => Edit(() => _justify = true);

    /// <summary>Indents the first line, on its start side, by this many points.</summary>
    public TextDescriptor FirstLineIndent(double points) => Edit(() => _indent = points);

    /// <summary>Plugs in hyphenation points besides soft hyphens (U+00AD). There is no bundled data.</summary>
    public TextDescriptor Hyphenator(IHyphenator hyphenator) => Edit(() => _hyphenator = hyphenator);

    /// <summary>
    /// Breaks this text's lines by total fit (Knuth and Plass) instead of first fit, which looks better for justified text and with
    /// hyphenation, at a higher cost on long paragraphs. See <see cref="KnuthPlassOptions"/> for the knobs and their defaults.
    /// </summary>
    /// <param name="options">The knobs, or null for the document's (and then the default) ones.</param>
    public TextDescriptor OptimalLineBreaks(KnuthPlassOptions? options = null) => Edit(() => { _lineBreakMode = LineBreakMode.KnuthPlass; _knuthPlass = options; });

    /// <summary>Breaks this text's lines by first fit, whatever the document default is.</summary>
    public TextDescriptor GreedyLineBreaks() => Edit(() => { _lineBreakMode = LineBreakMode.Greedy; _knuthPlass = null; });

    /// <summary>Adds a piece of text that can differ in style from the rest.</summary>
    /// <returns>The span's own settings.</returns>
    public TextSpanDescriptor Span(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var f = new SpanFormat();
        _segments.Add((text, f, 0));
        Rebuild();
        return new TextSpanDescriptor(f, Rebuild);
    }

    /// <summary>Adds a piece of text followed by a line break.</summary>
    public TextSpanDescriptor Line(string text) => Span((text ?? throw new ArgumentNullException(nameof(text))) + "\n");

    /// <summary>Adds the number of the page the text is on, written in the section's numbering style. See the remarks of <see cref="Slot.Text(Action{TextDescriptor})"/>.</summary>
    public PageNumberDescriptor CurrentPageNumber() => AddNumber(1);

    /// <summary>Adds the page count of the whole document, filled in once the last page is done.</summary>
    public PageNumberDescriptor TotalPages() => AddNumber(2);

    /// <summary>Adds the page count of the current section, in the section's numbering style.</summary>
    public PageNumberDescriptor SectionPages() => AddNumber(3);

    /// <summary>
    /// Puts a small element tree in the line, as one unbreakable unit that flows with the text: a padded, bordered badge with a short text, a
    /// picture in a box, a formula built from rows and texts. The delegate fills a slot, which is laid out once (at its widest natural width
    /// unless <see cref="InlineDescriptor.Width(double)"/> says otherwise) and never splits.
    /// </summary>
    /// <param name="compose">Fills the slot with the content.</param>
    /// <returns>The object's own settings: size, alignment, description, link.</returns>
    /// <example>
    /// <code>
    /// text.Span("Status: ");
    /// text.Inline(s => s.Background(Colors.LightGrey).Rounded(3).Padding(4, 0).Text("NEW").FontSize(8).Bold()).AlignMiddle();
    /// </code>
    /// </example>
    public InlineDescriptor Inline(Action<Slot> compose)
    {
        ArgumentNullException.ThrowIfNull(compose);
        var slot = new Slot();
        compose(slot);
        slot.Seal();
        return AddInline(new InlineDescriptor(slot.Held ?? new Spacer(), Rebuild));
    }

    /// <summary>A JPEG or PNG picture of a fixed size in the line (an icon, a logo). Describe it with <see cref="InlineDescriptor.AltText(string)"/> or mark it with <see cref="InlineDescriptor.Artifact"/>.</summary>
    /// <param name="data">The file's bytes (the signature decides JPEG or PNG).</param>
    /// <param name="width">Width in points.</param>
    /// <param name="height">Height in points.</param>
    /// <exception cref="ArgumentException">The bytes are neither JPEG nor PNG, or a size is not positive.</exception>
    public InlineDescriptor InlineImage(ReadOnlyMemory<byte> data, double width, double height)
    {
        var image = new Image();
        ReadOnlySpan<byte> s = data.Span;
        if (s.Length >= 8 && s[0] == 0x89 && s[1] == 0x50 && s[2] == 0x4E && s[3] == 0x47)
        {
            image.Png = data;
        }
        else if (s.Length >= 3 && s[0] == 0xFF && s[1] == 0xD8)
        {
            image.Jpeg = data;
        }
        else
        {
            throw new ArgumentException("The image is neither a JPEG nor a PNG file.", nameof(data));
        }

        return AddPicture(image, width, height);
    }

    /// <summary>A picture already added to the output, of a fixed size in the line.</summary>
    public InlineDescriptor InlineImage(PdfCreatedImage image, double width, double height) =>
        AddPicture(new Image { Created = image ?? throw new ArgumentNullException(nameof(image)) }, width, height);

    private InlineDescriptor AddPicture(Image image, double width, double height)
    {
        if (!(width > 0) || !(height > 0) || !double.IsFinite(width) || !double.IsFinite(height))
        {
            throw new ArgumentException("The size of an inline picture must be positive and finite.");
        }

        image.Fit = ImageFit.Stretch;
        return AddInline(new InlineDescriptor(image, Rebuild, true).WithSize(width, height));
    }

    /// <summary>A box of a fixed size drawn by a callback with the content builder, in the line (a checkbox, a small chart). It is decoration unless it has a description.</summary>
    /// <param name="width">Width in points.</param>
    /// <param name="height">Height in points.</param>
    /// <param name="paint">Draws into the box; coordinates start at its lower-left corner and drawing is clipped to it.</param>
    public InlineDescriptor InlineCanvas(double width, double height, Action<CanvasSurface> paint)
    {
        ArgumentNullException.ThrowIfNull(paint);
        if (!(width > 0) || !(height > 0) || !double.IsFinite(width) || !double.IsFinite(height))
        {
            throw new ArgumentException("The size of an inline canvas must be positive and finite.");
        }

        return AddInline(new InlineDescriptor(new Canvas { Width = width, Height = height, Paint = paint }, Rebuild, true).WithSize(width, height));
    }

    private InlineDescriptor AddInline(InlineDescriptor d)
    {
        d.Format.Inline = d;
        _segments.Add((null, d.Format, 4));
        Rebuild();
        return d;
    }

    private PageNumberDescriptor AddNumber(int kind)
    {
        var f = new SpanFormat();
        _segments.Add((null, f, kind));
        Rebuild();
        return new PageNumberDescriptor(f, Rebuild);
    }

    // ---- building ----

    private SpanFormat Merge(SpanFormat span)
    {
        SpanFormat d = Format;
        return new SpanFormat
        {
            Families = span.Families ?? d.Families,
            Size = span.Size ?? d.Size,
            Color = span.Color ?? d.Color,
            Weight = span.Weight ?? d.Weight,
            Italic = span.Italic ?? d.Italic,
            Underline = span.Underline ?? d.Underline,
            Strike = span.Strike ?? d.Strike,
            LetterSpacing = span.LetterSpacing ?? d.LetterSpacing,
            Position = span.Position ?? d.Position,
            Language = span.Language ?? d.Language,
            LineHeight = span.LineHeight ?? d.LineHeight,
            ReservedDigits = span.ReservedDigits ?? d.ReservedDigits,
            Inline = span.Inline,
        };
    }

    private static TextStyle StyleOf(SpanFormat merged) => new()
    {
        Families = merged.Families ?? [],
        Size = merged.Size ?? 11,
        Color = merged.Color ?? LayoutColor.Black,
        Weight = merged.Weight ?? 400,
        Italic = merged.Italic ?? false,
        Underline = merged.Underline ?? false,
        Strike = merged.Strike ?? false,
        LetterSpacing = merged.LetterSpacing ?? 0,
        Position = merged.Position ?? ScriptPosition.Normal,
        Language = merged.Language,
        LineHeight = merged.LineHeight ?? 1.2,
    };

    internal Element Build()
    {
        bool hasNumbers = false;
        foreach ((string? _, SpanFormat _, int kind) in _segments)
        {
            hasNumbers |= kind is 1 or 2 or 3;
        }

        if (hasNumbers)
        {
            foreach ((string? _, SpanFormat _, int kind) in _segments)
            {
                if (kind == 4)
                {
                    throw new InvalidOperationException("A text with page numbers is laid out as a row of pieces and cannot hold inline objects.");
                }
            }
        }

        return hasNumbers ? BuildRow() : BuildText(_segments, 0, _segments.Count);
    }

    private Text BuildText(List<(string? Text, SpanFormat Format, int Kind)> segs, int from, int to)
    {
        SpanFormat d = Format;
        var t = new Text
        {
            FontName = d.Families is { Length: > 0 } fam ? fam[0] : null,
            Size = d.Size ?? 11,
            Color = d.Color ?? LayoutColor.Black,
            Weight = d.Weight ?? 400,
            Italic = d.Italic ?? false,
            LineSpacing = d.LineHeight ?? 1.2,
            Alignment = _alignmentSet ? _alignment : _inheritedAlignment,
            Justify = _justify,
            FirstLineIndent = _indent,
            Hyphenator = _hyphenator,
            LineBreakMode = _lineBreakMode,
            KnuthPlass = _knuthPlass,
            Language = d.Language,
        };

        // The element's own properties carry the text when nothing needs a per-span style; otherwise every span gets a complete style.
        bool plain = !(d.Underline ?? false) && !(d.Strike ?? false) && (d.LetterSpacing ?? 0) == 0 && (d.Families?.Length ?? 0) <= 1;
        for (int i = from; plain && i < to; i++)
        {
            plain = !segs[i].Format.HasStyle && segs[i].Kind != 4;
        }

        if (plain)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = from; i < to; i++)
            {
                sb.Append(segs[i].Text);
            }

            t.Content = sb.ToString();
            return t;
        }

        for (int i = from; i < to; i++)
        {
            TextStyle style = StyleOf(Merge(segs[i].Format));
            t.Spans.Add(segs[i].Kind == 4 ? TextSpan.ForInline(segs[i].Format.Inline!.Create(), style) : new TextSpan(segs[i].Text!, style));
        }

        return t;
    }

    // A text with page numbers is a row of texts and numbers; spaces at the edges of a text become gaps, since a trailing space has no width.
    private Element BuildRow()
    {
        var row = new Row();
        int i = 0;
        var items = new List<RowItem>();
        while (i < _segments.Count)
        {
            if (_segments[i].Kind != 0)
            {
                SpanFormat m = Merge(_segments[i].Format);
                items.Add(new RowItem(new PageNumber
                {
                    Kind = _segments[i].Kind switch { 1 => PageNumberKind.Current, 2 => PageNumberKind.DocumentTotal, _ => PageNumberKind.SectionTotal },
                    FontName = m.Families is { Length: > 0 } f ? f[0] : null,
                    Weight = m.Weight ?? 400,
                    Size = m.Size ?? 11,
                    Color = m.Color ?? LayoutColor.Black,
                    LineSpacing = m.LineHeight ?? 1.2,
                    ReservedDigits = m.ReservedDigits ?? 4,
                }));
                i++;
                continue;
            }

            int j = i;
            while (j < _segments.Count && _segments[j].Kind == 0)
            {
                j++;
            }

            AddTextRun(items, i, j);
            i = j;
        }

        row.Items.AddRange(items);
        row.Compact = true;
        if (_alignment == HorizontalAlignment.Start)
        {
            return row;
        }

        // A compact row is as wide as its pieces; a box around it places it.
        return new Box { HorizontalAlignment = _alignment, Child = row };
    }

    private void AddTextRun(List<RowItem> items, int from, int to)
    {
        // Trim the spaces at the edges of the run, remembering how wide they were.
        var segs = new List<(string? Text, SpanFormat Format, int Kind)>(_segments.GetRange(from, to - from));
        double lead = 0;
        double trail = 0;
        while (segs.Count > 0)
        {
            string s = segs[0].Text!;
            string trimmed = s.TrimStart(' ');
            lead += (s.Length - trimmed.Length) * SpaceWidth(segs[0].Format);
            if (trimmed.Length > 0)
            {
                segs[0] = (trimmed, segs[0].Format, 0);
                break;
            }

            segs.RemoveAt(0);
        }

        while (segs.Count > 0)
        {
            int last = segs.Count - 1;
            string s = segs[last].Text!;
            string trimmed = s.TrimEnd(' ');
            trail += (s.Length - trimmed.Length) * SpaceWidth(segs[last].Format);
            if (trimmed.Length > 0)
            {
                segs[last] = (trimmed, segs[last].Format, 0);
                break;
            }

            segs.RemoveAt(last);
        }

        if (lead > 0 || (segs.Count == 0 && trail > 0))
        {
            items.Add(RowItem.Constant(lead + (segs.Count == 0 ? trail : 0), new Spacer()));
        }

        if (segs.Count > 0)
        {
            items.Add(new RowItem(BuildText(segs, 0, segs.Count)));
            if (trail > 0)
            {
                items.Add(RowItem.Constant(trail, new Spacer()));
            }
        }
    }

    // Liberation Sans, Times and Helvetica spaces are 0.278 to 0.25 em; the gap only has to look like a space.
    private double SpaceWidth(SpanFormat f) => 0.278 * (Merge(f).Size ?? 11);
}

/// <summary>The style of one span of text. Returned by <see cref="TextDescriptor.Span(string)"/>; unset settings come from the text's own.</summary>
public sealed class TextSpanDescriptor : TextFormatDescriptor<TextSpanDescriptor>
{
    private readonly Action _changed;

    internal TextSpanDescriptor(SpanFormat format, Action changed)
        : base(format)
    {
        _changed = changed;
    }

    private protected override void Changed() => _changed();

    /// <summary>The font family of this span; several families name fallbacks.</summary>
    public TextSpanDescriptor FontFamily(params string[] families) => Set(f => f.Families = families.Length == 0 ? null : families);

    /// <summary>Uses an italic face.</summary>
    public TextSpanDescriptor Italic() => Set(f => f.Italic = true);

    /// <summary>Draws a line under the span.</summary>
    public TextSpanDescriptor Underline() => Set(f => f.Underline = true);

    /// <summary>Draws a line through the span.</summary>
    public TextSpanDescriptor Strike() => Set(f => f.Strike = true);

    /// <summary>Extra space after every character in points.</summary>
    public TextSpanDescriptor LetterSpacing(double points) => Set(f => f.LetterSpacing = points);

    /// <summary>Raised and smaller, with the font's <c>sups</c> glyphs when it has them.</summary>
    public TextSpanDescriptor Superscript() => Set(f => f.Position = ScriptPosition.Superscript);

    /// <summary>Lowered and smaller, with the font's <c>subs</c> glyphs when it has them.</summary>
    public TextSpanDescriptor Subscript() => Set(f => f.Position = ScriptPosition.Subscript);

    /// <summary>The language of this span, a BCP 47 tag.</summary>
    public TextSpanDescriptor Language(string tag) => Set(f => f.Language = tag ?? throw new ArgumentNullException(nameof(tag)));

    /// <summary>Line distance of lines this span is on, as a multiple of the font size; the tallest span of a line decides.</summary>
    public TextSpanDescriptor LineHeight(double factor) => Set(f => f.LineHeight = factor);
}

/// <summary>The style of a page number inside a text. Returned by <see cref="TextDescriptor.CurrentPageNumber"/> and its siblings.</summary>
public sealed class PageNumberDescriptor : TextFormatDescriptor<PageNumberDescriptor>
{
    private readonly Action _changed;

    internal PageNumberDescriptor(SpanFormat format, Action changed)
        : base(format)
    {
        _changed = changed;
    }

    private protected override void Changed() => _changed();

    /// <summary>The font family (a registered family or standard font name).</summary>
    public PageNumberDescriptor FontFamily(string family) => Set(f => f.Families = [family ?? throw new ArgumentNullException(nameof(family))]);

    /// <summary>For totals, how many digits the reserved space holds. Default 4.</summary>
    public PageNumberDescriptor ReservedDigits(int digits) => Set(f => f.ReservedDigits = digits);
}
