using System.Globalization;
using System.Text;
using OnePdf.Layout.Pagination;

namespace OnePdf.Layout;

/// <summary>
/// One line of text with page numbers in it, such as "3/17" or "Page 3 of 17", shaped as a single run once the page counts are known.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Format"/> holds the text with the placeholders <c>{page}</c> (the number of the page in its section's numbering style),
/// <c>{total}</c> (the page count of the whole document, in decimal) and <c>{sectionTotal}</c> (the page count of the section, in the
/// section's numbering style). <c>{{</c> and <c>}}</c> are literal braces. An unknown placeholder or an unbalanced brace raises
/// <see cref="ArgumentException"/> when the format is set.
/// </para>
/// <para>
/// The element is one line, as wide as the space it is offered and as high as the line height of its font. Counts that are not known while
/// pages are laid out are drawn in a form that is filled once, at the end of the document (or of the section when only
/// <c>{sectionTotal}</c> is used). The whole text is then shaped as one run with the face and embedding of <see cref="Text"/> and aligned
/// in the box by its real width, so there is no reserved gap and kerning across the run holds. The document is laid out once.
/// </para>
/// <para>
/// Where no width is offered (an auto-sized row item) the width is that of the format with each placeholder drawn as
/// <see cref="ReservedDigits"/> zeros; that is the only use of <see cref="ReservedDigits"/>. Text wider than its box is condensed to fit
/// and a note is recorded. The run is shaped left to right so digits keep their order; <see cref="Alignment"/> still mirrors in a
/// right-to-left layout. Tagged, it is a paragraph with the final text, or an artifact inside a header or footer.
/// </para>
/// </remarks>
public sealed class PageNumberText : Element
{
    private string _format = string.Empty;
    private List<(string Literal, int Kind)> _parts = [];

    /// <summary>
    /// The text with placeholders <c>{page}</c>, <c>{total}</c> and <c>{sectionTotal}</c>; <c>{{</c> and <c>}}</c> are literal braces.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    /// <exception cref="ArgumentException">A placeholder is unknown or a brace is unbalanced.</exception>
    public string Format
    {
        get => _format;
        set
        {
            _parts = Parse(value ?? throw new ArgumentNullException(nameof(value)));
            _format = value;
        }
    }

    /// <summary>Font family, as for <see cref="Text.FontName"/>. Null, the default, is the text default.</summary>
    public string? FontName { get; set; }

    /// <summary>Weight of the font, 400 regular or 700 bold. Default 400.</summary>
    public int Weight { get => _weight ?? 400; set => _weight = value; }

    /// <summary>Whether an italic face is wanted. Default false.</summary>
    public bool Italic { get => _italic ?? false; set => _italic = value; }

    /// <summary>Extra space after every character in points. Default 0.</summary>
    public double LetterSpacing { get => _letterSpacing ?? 0; set => _letterSpacing = value; }

    /// <summary>The palette colour glyphs are drawn with; the document's when null.</summary>
    public ColorPalette? Palette { get; set; }

    /// <summary>Font size in points. Default 11.</summary>
    public double Size { get => _size ?? 11; set => _size = value; }

    /// <summary>Text colour. Default black.</summary>
    public LayoutColor Color { get => _color ?? LayoutColor.Black; set => _color = value; }

    /// <summary>Line distance as a multiple of the font size. Default 1.2.</summary>
    public double LineSpacing { get => _lineSpacing ?? 1.2; set => _lineSpacing = value; }

    private int? _weight;
    private bool? _italic;
    private double? _letterSpacing;
    private double? _size;
    private LayoutColor? _color;
    private double? _lineSpacing;

    internal void InheritFrom(SpanFormat d)
    {
        if (FontName is null && d.Families is { Length: > 0 } fam)
        {
            FontName = fam[0];
        }
        _weight ??= d.Weight;
        _italic ??= d.Italic;
        _letterSpacing ??= d.LetterSpacing;
        _size ??= d.Size;
        _color ??= d.Color;
        _lineSpacing ??= d.LineHeight;
        Palette ??= d.Palette;
    }

    /// <summary>Where the text sits in its box. Default <see cref="HorizontalAlignment.End"/>.</summary>
    public HorizontalAlignment Alignment { get; set; } = HorizontalAlignment.End;

    /// <summary>How many digits each placeholder counts as when no width is offered. Default 4, limited to 1 to 12.</summary>
    public int ReservedDigits { get; set; } = 4;

    private const int Page = 1;
    private const int Total = 2;
    private const int SectionTotal = 3;

    private sealed record Data(PdfFormXObject Form, double BoxWidth);

    private static List<(string Literal, int Kind)> Parse(string format)
    {
        var parts = new List<(string, int)>();
        var lit = new StringBuilder();
        for (int i = 0; i < format.Length; i++)
        {
            char ch = format[i];
            if (ch == '{')
            {
                if (i + 1 < format.Length && format[i + 1] == '{')
                {
                    lit.Append('{');
                    i++;
                    continue;
                }

                int end = format.IndexOf('}', i + 1);
                if (end < 0)
                {
                    throw new ArgumentException("The format has a '{' that is never closed; write '{{' for a literal brace.", nameof(format));
                }

                string name = format.Substring(i + 1, end - i - 1);
                int kind = name switch { "page" => Page, "total" => Total, "sectionTotal" => SectionTotal, _ => 0 };
                if (kind == 0)
                {
                    throw new ArgumentException($"Unknown placeholder '{{{name}}}'; use {{page}}, {{total}} or {{sectionTotal}}.", nameof(format));
                }

                if (lit.Length > 0)
                {
                    parts.Add((lit.ToString(), 0));
                    lit.Clear();
                }

                parts.Add((string.Empty, kind));
                i = end;
            }
            else if (ch == '}')
            {
                if (i + 1 < format.Length && format[i + 1] == '}')
                {
                    lit.Append('}');
                    i++;
                    continue;
                }

                throw new ArgumentException("The format has a '}' without a '{'; write '}}' for a literal brace.", nameof(format));
            }
            else
            {
                lit.Append(ch);
            }
        }

        if (lit.Length > 0)
        {
            parts.Add((lit.ToString(), 0));
        }

        return parts;
    }

    private bool Uses(int kind)
    {
        foreach ((string _, int k) in _parts)
        {
            if (k == kind)
            {
                return true;
            }
        }

        return false;
    }

    private int Digits => Math.Clamp(ReservedDigits, 1, 12);

    private string Build(Func<int, string> number)
    {
        var sb = new StringBuilder();
        foreach ((string literal, int kind) in _parts)
        {
            sb.Append(kind == 0 ? literal : number(kind));
        }

        return sb.ToString();
    }

    private Text MakeText(string content, HorizontalAlignment alignment)
    {
        var t = new Text
        {
            Content = content,
            FontName = FontName,
            Weight = Weight,
            Italic = Italic,
            Palette = Palette,
            Size = Size,
            Color = Color,
            LineSpacing = LineSpacing,
            Alignment = alignment,
        };
        if (LetterSpacing != 0)
        {
            t.Content = string.Empty;
            t.Spans.Add(new TextSpan(content, new TextStyle
            {
                Families = FontName is null ? [] : [FontName],
                Size = Size,
                Color = Color,
                Palette = Palette,
                Weight = Weight,
                Italic = Italic,
                LetterSpacing = LetterSpacing,
                LineHeight = LineSpacing,
            }));
        }

        return t;
    }

    private string Reserved() => Build(_ => new string('0', Digits));

    private string Known(LayoutContext ctx) => Build(k => k == Page ? Numbering.Format(ctx.Page.Number, ctx.Page.Style) : new string('0', Digits));

    internal override WidthRange WidthsCore(LayoutContext ctx)
    {
        double w = ctx.Text.Prepare(MakeText(Reserved(), HorizontalAlignment.Start), 1e6, ctx).NaturalWidth;
        return new WidthRange(w, w);
    }

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        bool needsCount = Uses(Total) || Uses(SectionTotal);
        if (!needsCount)
        {
            // Everything is known on this page: ordinary text.
            return MakeText(Build(_ => Numbering.Format(ctx.Page.Number, ctx.Page.Style)), Alignment).Layout(ctx, c, null);
        }

        ITextBlock probe = ctx.Text.Prepare(MakeText(Reserved(), HorizontalAlignment.Start), 1e6, ctx);
        double lh = probe.LineHeight(0);
        double width = c.WidthBounded ? c.Width : probe.NaturalWidth;
        if (lh > c.Height + 1e-9)
        {
            return ctx.NoFit(c, width, lh, "A line of text is taller than the space offered");
        }

        PdfFormXObject form = MakeForm(ctx, width, lh);
        return FlowFit.Complete(lh, new Placed(this, 0, 0, width, lh) { Data = new Data(form, width) });
    }

    private PdfFormXObject MakeForm(LayoutContext ctx, double boxWidth, double lh)
    {
        PageInfo page = ctx.Page;
        LayoutDirection direction = ctx.Direction;
        bool wantsTotal = Uses(Total);
        int section = wantsTotal ? -1 : page.Section;
        string key = string.Create(CultureInfo.InvariantCulture,
            $"pnt|{page.Index}|{_format}|{FontName}|{Weight}|{Italic}|{LetterSpacing}|{Size}|{Color.R},{Color.G},{Color.B}|{LineSpacing}|{Alignment}|{(int)direction}|{boxWidth}");
        return ctx.TotalForm(key, new PdfRectangle(0, 0, boxWidth, lh), section, (fb, count) =>
        {
            // {total} is the one count known at the end of the document; the section's own count was recorded when its section ended.
            long sectionCount = wantsTotal ? ctx.SectionCount(page.Section) : count;
            long documentCount = count;
            string content = Build(k => k switch
            {
                Page => Numbering.Format(page.Number, page.Style),
                Total => documentCount.ToString(CultureInfo.InvariantCulture),
                _ => Numbering.Format(sectionCount, page.Style),
            });
            Text text = MakeText(content, HorizontalAlignment.Start);
            LayoutDirection saved = ctx.Direction;

            // Shaped left to right so that digits and the separators between them keep their order; the box is aligned below.
            ctx.Direction = LayoutDirection.LeftToRight;
            try
            {
                double w = ctx.Text.Prepare(text, 1e6, ctx).NaturalWidth;
                bool squeeze = w > boxWidth + 1e-6;
                ITextBlock block = ctx.Text.Prepare(text, w + 0.01, ctx);
                double x = squeeze ? 0 : AlignX(Alignment, direction, boxWidth - w);
                var d = new DrawContext(fb, ctx, lh, x, 0);
                if (squeeze)
                {
                    fb.Save();
                    fb.Scale(boxWidth / w, 1);
                    block.Draw(d, new Placed(text, 0, 0, w, lh), 0, 1);
                    fb.Restore();
                    ctx.Note("A page number text is wider than the space it was given; it is drawn condensed.");
                }
                else
                {
                    block.Draw(d, new Placed(text, 0, 0, w, lh), 0, 1);
                }
            }
            finally
            {
                ctx.Direction = saved;
            }
        });
    }

    internal override void DrawCore(in DrawContext d, Placed p)
    {
        var data = (Data)p.Data!;
        PdfContentBuilder b = d.B;
        Tagger tag = d.Ctx.Tag;
        TagScope scope = tag.TextIsDirect ? default : tag.Open(this, p.Continued, p.Continues, "P");
        bool tagged = tag.BeginContent();
        b.Save();
        b.Translate(d.X + AlignX(HorizontalAlignment.Start, d.Ctx.Direction, p.Width - data.BoxWidth), d.PdfY(p.Height));
        b.DrawForm(data.Form);
        b.Restore();
        tag.EndContent(tagged);
        tag.Close(scope);
    }
}
