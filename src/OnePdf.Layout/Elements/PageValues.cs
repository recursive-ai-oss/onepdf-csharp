using OnePdf.Layout.Pagination;

namespace OnePdf.Layout;

/// <summary>
/// What the content of one page contributed, and what came before it: the values a <see cref="DynamicText"/> shows. They are computed from
/// the pieces that actually landed on the page (after page breaking), so an item pushed to the next page takes its contribution with it.
/// </summary>
/// <remarks>
/// <para>Contributions come from <see cref="Accumulate"/> (amounts added to a named account) and <see cref="Marker"/> (a text recorded under a
/// key) elements in the page's flowing content. Header, footer, background and foreground do not contribute. An element that splits over
/// pages contributes on the page where it starts; header rows a table repeats contribute on every page they are repeated on.</para>
/// <para>Amounts are <see cref="decimal"/>, so sums of money carry no binary rounding. Names are compared exactly (ordinal). An account
/// nothing was added to reads as zero, a key nothing was recorded under as null.</para>
/// </remarks>
public sealed class PageValues
{
    private static readonly IReadOnlyDictionary<string, decimal> NoAmounts = new Dictionary<string, decimal>();
    private static readonly IReadOnlyDictionary<string, string> NoTexts = new Dictionary<string, string>();

    internal PageValues(
        int pageIndex,
        int sectionIndex,
        long pageNumber,
        string pageLabel,
        IReadOnlyDictionary<string, decimal>? before,
        IReadOnlyDictionary<string, decimal>? onPage,
        IReadOnlyDictionary<string, string>? markersBefore,
        IReadOnlyDictionary<string, string>? first,
        IReadOnlyDictionary<string, string>? last)
    {
        PageIndex = pageIndex;
        SectionIndex = sectionIndex;
        PageNumber = pageNumber;
        PageLabel = pageLabel;
        _before = before ?? NoAmounts;
        _onPage = onPage ?? NoAmounts;
        _markersBefore = markersBefore ?? NoTexts;
        _first = first ?? NoTexts;
        _last = last ?? NoTexts;
    }

    private readonly IReadOnlyDictionary<string, decimal> _before;
    private readonly IReadOnlyDictionary<string, decimal> _onPage;
    private readonly IReadOnlyDictionary<string, string> _markersBefore;
    private readonly IReadOnlyDictionary<string, string> _first;
    private readonly IReadOnlyDictionary<string, string> _last;

    /// <summary>Zero-based index of the page in the document.</summary>
    public int PageIndex { get; }

    /// <summary>Zero-based index of the section (<see cref="LayoutPage"/>) the page belongs to.</summary>
    public int SectionIndex { get; }

    /// <summary>The displayed page number (the section's numbering, as a number).</summary>
    public long PageNumber { get; }

    /// <summary>The page number as the section writes it (roman, letters, ...), without the label prefix.</summary>
    public string PageLabel { get; }

    /// <summary>The sum of the amounts added to <paramref name="account"/> by content on this page.</summary>
    public decimal Sum(string account) => _onPage.TryGetValue(account, out decimal v) ? v : 0m;

    /// <summary>The running sum of <paramref name="account"/> up to and including this page: what a footer carries forward.</summary>
    public decimal Total(string account) => BroughtForward(account) + Sum(account);

    /// <summary>The running sum of <paramref name="account"/> over the pages before this one: what a header brings forward. Zero on the first page.</summary>
    public decimal BroughtForward(string account) => _before.TryGetValue(account, out decimal v) ? v : 0m;

    /// <summary>The first text recorded under <paramref name="key"/> on this page, in reading order; null when none.</summary>
    public string? First(string key) => _first.TryGetValue(key, out string? v) ? v : null;

    /// <summary>The last text recorded under <paramref name="key"/> on this page, in reading order; null when none.</summary>
    public string? Last(string key) => _last.TryGetValue(key, out string? v) ? v : null;

    /// <summary>The text in force at the end of this page: the last one recorded on it, else the last one on an earlier page. Null before any.</summary>
    public string? Current(string key) => Last(key) ?? Entering(key);

    /// <summary>The text in force when this page begins: the last one recorded on an earlier page. Null before any.</summary>
    public string? Entering(string key) => _markersBefore.TryGetValue(key, out string? v) ? v : null;
}

/// <summary>
/// Adds an amount to a named account when its child is placed, for the running totals of <see cref="PageValues"/>. It takes the child's
/// size and draws nothing itself. Wrap the cell or row that carries the amount: the amount then counts on the page where the child
/// starts, and moves with it when a keep rule or a page break pushes the child to the next page.
/// </summary>
public sealed class Accumulate : SingleChildElement
{
    /// <summary>The account's name. Nothing is added when null or empty.</summary>
    public string? Account { get; set; }

    /// <summary>The amount to add (negative to subtract). Default 0.</summary>
    public decimal Amount { get; set; }

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume) => Wrap(LayoutChild(ctx, c, resume), null);
}

/// <summary>
/// Records a text under a key when its child is placed, so <see cref="PageValues"/> can give the first, the last and the current one of a
/// page: the section title of a running header, the first and last entry of a dictionary page. It takes the child's size and draws nothing
/// itself, and counts on the page where the child starts.
/// </summary>
public sealed class Marker : SingleChildElement
{
    /// <summary>The key. Nothing is recorded when null or empty.</summary>
    public string? Key { get; set; }

    /// <summary>The text recorded. Nothing is recorded when null.</summary>
    public string? Text { get; set; }

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume) => Wrap(LayoutChild(ctx, c, resume), null);
}

/// <summary>
/// One line of text computed for each page from its <see cref="PageValues"/>: "Carried forward: 1,234.50", "Section 2 - Payments",
/// "apple - azure". The callback runs when the page is drawn, after the page has been laid out, so it sees what really landed on the page;
/// culture and number format are the callback's.
/// </summary>
/// <remarks>
/// <para>The element's height is one line of the chosen font, fixed before the content of the page is known (a header's height must be known
/// to place the body). Its width is the offered width, or, in an unbounded space, that of <see cref="Sample"/>. The text is one line: wider
/// than its box it is squeezed horizontally and a note is recorded. In a header, footer, background or foreground it is an artifact like
/// the rest of that band; in the body it is ordinary tagged text.</para>
/// <para>The callback may run more than once for a page only if the document is drawn more than once; it should have no side effects.</para>
/// </remarks>
public sealed class DynamicText : Element
{
    /// <summary>Creates a dynamic text. <paramref name="format"/> returns the text for a page; null counts as empty.</summary>
    public DynamicText(Func<PageValues, string?> format) => Format = format ?? throw new ArgumentNullException(nameof(format));

    /// <summary>Gives the text of a page.</summary>
    public Func<PageValues, string?> Format { get; }

    /// <summary>A typical text, used to size the element in an unbounded space. Default "0".</summary>
    public string Sample { get; set; } = "0";

    /// <summary>Font family, as for <see cref="Text.FontName"/>. Null is the text default.</summary>
    public string? FontName { get; set; }

    /// <summary>Weight of the font, 400 regular or 700 bold. Default 400.</summary>
    public int Weight { get; set; } = 400;

    /// <summary>Whether the face is italic. Default false.</summary>
    public bool Italic { get; set; }

    /// <summary>Font size in points. Default 11.</summary>
    public double Size { get; set; } = 11;

    /// <summary>Text colour. Default black.</summary>
    public LayoutColor Color { get; set; }

    /// <summary>Line distance as a multiple of the font size. Default 1.2.</summary>
    public double LineSpacing { get; set; } = 1.2;

    /// <summary>Where the text sits in the element's width. Default <see cref="HorizontalAlignment.Start"/>.</summary>
    public HorizontalAlignment Alignment { get; set; }

    private Text MakeText(string content, HorizontalAlignment alignment) => new()
    {
        Content = content,
        FontName = FontName,
        Weight = Weight,
        Italic = Italic,
        Size = Size,
        Color = Color,
        LineSpacing = LineSpacing,
        Alignment = alignment,
    };

    internal override WidthRange WidthsCore(LayoutContext ctx)
    {
        double w = ctx.Text.Prepare(MakeText(Sample.Length == 0 ? "0" : Sample, HorizontalAlignment.Start), 1e6, ctx).NaturalWidth;
        return new WidthRange(w, w);
    }

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        ITextBlock probe = ctx.Text.Prepare(MakeText(Sample.Length == 0 ? "0" : Sample, HorizontalAlignment.Start), 1e6, ctx);
        double lh = probe.LineHeight(0);
        double width = c.WidthBounded ? c.Width : probe.NaturalWidth;
        if (lh > c.Height + 1e-9)
        {
            return ctx.NoFit(c, width, lh, "A line of text is taller than the space offered");
        }

        return FlowFit.Complete(lh, new Placed(this, 0, 0, width, lh));
    }

    internal override void DrawCore(in DrawContext d, Placed p)
    {
        LayoutContext ctx = d.Ctx;
        string text = Format(ctx.Values) ?? string.Empty;
        if (text.Length == 0)
        {
            return;
        }

        double w = ctx.Text.Prepare(MakeText(text, HorizontalAlignment.Start), 1e6, ctx).NaturalWidth;
        if (w <= p.Width + 1e-6)
        {
            FlowFit f = MakeText(text, Alignment).Layout(ctx, new Constraint(p.Width, double.PositiveInfinity, true), null);
            if (f.Placed is Placed tp)
            {
                tp.Element.Draw(d.At(tp.X, tp.Y), tp);
            }

            return;
        }

        // Wider than its box: squeeze rather than clip or wrap, as a reserved page total does.
        ctx.NoteOnce(this, "A dynamic text is wider than its box on some page; it is drawn condensed.");
        FlowFit g = MakeText(text, HorizontalAlignment.Start).Layout(ctx, new Constraint(w + 0.01, double.PositiveInfinity, true), null);
        if (g.Placed is Placed sp)
        {
            PdfContentBuilder b = d.B;
            b.Save();
            b.Translate(d.X, d.PdfY(p.Height));
            b.Scale(p.Width / w, 1);
            sp.Element.Draw(new DrawContext(b, ctx, p.Height, 0, 0), sp);
            b.Restore();
        }
    }
}

/// <summary>Collects the contributions of the pages as they are drawn, in order, and hands each page its <see cref="PageValues"/>.</summary>
internal sealed class DynamicState
{
    private IReadOnlyDictionary<string, decimal>? _running;
    private IReadOnlyDictionary<string, string>? _markers;

    /// <summary>Walks the placed body of a page in reading order and returns its values; later pages see this page's contributions.</summary>
    public PageValues Page(Placed? body, PageInfo info, string label)
    {
        Dictionary<string, decimal>? sums = null;
        Dictionary<string, string>? first = null;
        Dictionary<string, string>? last = null;
        if (body is not null)
        {
            var stack = new Stack<Placed>();
            stack.Push(body);
            while (stack.Count > 0)
            {
                Placed p = stack.Pop();
                if (!p.Continued)
                {
                    if (p.Element is Accumulate { Account: { Length: > 0 } account } a)
                    {
                        sums ??= new Dictionary<string, decimal>(StringComparer.Ordinal);
                        sums.TryGetValue(account, out decimal now);
                        try
                        {
                            sums[account] = checked(now + a.Amount);
                        }
                        catch (OverflowException)
                        {
                            throw new PdfLayoutException($"Page[{info.Index}]", double.NaN, double.NaN, double.NaN, double.NaN, $"The account '{account}' overflows");
                        }
                    }
                    else if (p.Element is Marker { Key: { Length: > 0 } key, Text: { } text })
                    {
                        first ??= new Dictionary<string, string>(StringComparer.Ordinal);
                        last ??= new Dictionary<string, string>(StringComparer.Ordinal);
                        first.TryAdd(key, text);
                        last[key] = text;
                    }
                }

                for (int i = p.Children.Count - 1; i >= 0; i--)
                {
                    stack.Push(p.Children[i]);
                }
            }
        }

        var values = new PageValues(info.Index, info.Section, info.Number, label, _running, sums, _markers, first, last);
        if (sums is not null)
        {
            var merged = _running is null ? new Dictionary<string, decimal>(StringComparer.Ordinal) : new Dictionary<string, decimal>(_running, StringComparer.Ordinal);
            foreach ((string k, decimal v) in sums)
            {
                merged.TryGetValue(k, out decimal before);
                try
                {
                    merged[k] = checked(before + v);
                }
                catch (OverflowException)
                {
                    throw new PdfLayoutException($"Page[{info.Index}]", double.NaN, double.NaN, double.NaN, double.NaN, $"The account '{k}' overflows");
                }
            }

            _running = merged;
        }

        if (last is not null)
        {
            var merged = _markers is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(_markers, StringComparer.Ordinal);
            foreach ((string k, string v) in last)
            {
                merged[k] = v;
            }

            _markers = merged;
        }

        return values;
    }
}
