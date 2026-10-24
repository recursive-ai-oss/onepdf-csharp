namespace OnePdf.Layout;

public partial class Slot
{
    /// <summary>
    /// Adds <paramref name="amount"/> to the running account <paramref name="account"/> on the page where the content starts, for
    /// <see cref="PageValues"/>. Wrap the cell or row that carries the amount: it moves to the next page together with the content.
    /// </summary>
    /// <param name="account">The account's name.</param>
    /// <param name="amount">The amount to add; negative subtracts.</param>
    public Slot Accumulate(string account, decimal amount) => Wrap(new Accumulate { Account = account ?? throw new ArgumentNullException(nameof(account)), Amount = amount });

    /// <summary>
    /// Records <paramref name="text"/> under <paramref name="key"/> on the page where the content starts: the first, last and current text of a
    /// page are then available through <see cref="PageValues"/>, for running headers.
    /// </summary>
    /// <param name="key">The key, such as <c>section</c>.</param>
    /// <param name="text">The text recorded.</param>
    public Slot Marker(string key, string text) => Wrap(new Marker { Key = key ?? throw new ArgumentNullException(nameof(key)), Text = text ?? throw new ArgumentNullException(nameof(text)) });

    /// <summary>
    /// A line of text computed for each page from the values of that page (see <see cref="PageValues"/>), after the page has been laid out.
    /// Use it for "Carried forward" lines, page subtotals and running headers in headers, footers and the body.
    /// </summary>
    /// <param name="format">Gives the text of a page; format numbers and dates here. It should have no side effects.</param>
    /// <example>
    /// <code>
    /// page.Footer().DynamicText(v =&gt; "Carried forward: " + v.Total("amount").ToString("N2", culture)).AlignRight();
    /// </code>
    /// </example>
    public DynamicTextDescriptor DynamicText(Func<PageValues, string?> format)
    {
        ArgumentNullException.ThrowIfNull(format);
        var element = new DynamicText(format);
        Put(element);
        return new DynamicTextDescriptor(element, InheritedAlignment);
    }
}

/// <summary>The style of a <see cref="Slot.DynamicText(Func{PageValues, string})"/>. Settings must be made while the delegate that handed out the slot is running.</summary>
public sealed class DynamicTextDescriptor
{
    private readonly DynamicText _element;

    internal DynamicTextDescriptor(DynamicText element, HorizontalAlignment inherited)
    {
        _element = element;
        element.Alignment = inherited;
    }

    /// <summary>The font size in points. Default 11.</summary>
    public DynamicTextDescriptor FontSize(double points)
    {
        _element.Size = points;
        return this;
    }

    /// <summary>The text colour. Default black.</summary>
    public DynamicTextDescriptor FontColor(LayoutColor color)
    {
        _element.Color = color;
        return this;
    }

    /// <summary>Weight 700.</summary>
    public DynamicTextDescriptor Bold() => Weight(700);

    /// <summary>A weight from 1 to 1000 (400 regular, 700 bold).</summary>
    public DynamicTextDescriptor Weight(int weight)
    {
        _element.Weight = weight;
        return this;
    }

    /// <summary>The font family (a registered family or standard font name).</summary>
    public DynamicTextDescriptor FontFamily(string family)
    {
        _element.FontName = family ?? throw new ArgumentNullException(nameof(family));
        return this;
    }

    /// <summary>Aligns the text in its box: start (default), end, left, right or centre.</summary>
    public DynamicTextDescriptor Align(HorizontalAlignment alignment)
    {
        _element.Alignment = alignment;
        return this;
    }

    /// <summary>Aligns the text to the start of its box (left in left-to-right content).</summary>
    public DynamicTextDescriptor AlignStart() => Align(HorizontalAlignment.Start);

    /// <summary>Aligns the text to the end of its box (right in left-to-right content).</summary>
    public DynamicTextDescriptor AlignEnd() => Align(HorizontalAlignment.End);

    /// <summary>Aligns the text to the left of its box.</summary>
    public DynamicTextDescriptor AlignLeft() => Align(HorizontalAlignment.Left);

    /// <summary>Aligns the text to the right of its box.</summary>
    public DynamicTextDescriptor AlignRight() => Align(HorizontalAlignment.Right);

    /// <summary>Centres the text in its box.</summary>
    public DynamicTextDescriptor AlignCenter() => Align(HorizontalAlignment.Center);

    /// <summary>A typical text, used to size the element in an unbounded space. Default "0".</summary>
    public DynamicTextDescriptor Sample(string sample)
    {
        _element.Sample = sample ?? throw new ArgumentNullException(nameof(sample));
        return this;
    }
}
