using System.Globalization;
using OnePdf.Layout.Pagination;

namespace OnePdf.Layout;

/// <summary>Which number a <see cref="PageNumber"/> shows.</summary>
public enum PageNumberKind
{
    /// <summary>The number of the page it is on, written in the section's numbering style.</summary>
    Current = 0,

    /// <summary>How many pages the whole document has, in decimal. It is known only when the last page is done.</summary>
    DocumentTotal,

    /// <summary>How many pages the section has, in the section's numbering style. Known when the section ends.</summary>
    SectionTotal,
}

/// <summary>
/// A page number for headers, footers and content: "3", or the "12" of "page 3 of 12". The current number is ordinary <see cref="Text"/>
/// laid out for each page. A total is not known while pages are being laid out, so it is a form drawn on every page and filled in once at
/// the end; the form is as wide as <see cref="ReservedDigits"/> digits and the number is right-aligned inside it, so it always ends at
/// the same place.
/// </summary>
/// <remarks>
/// <para>Put the elements of "Page N of M" side by side in a <see cref="Row"/> with <see cref="Row.Spacing"/> for the gaps: a space at the end
/// of a text does not count towards its width. A total with more digits than reserved is squeezed horizontally to fit and a note is
/// recorded.</para>
/// <para>A total is shaped and drawn like <see cref="Text"/> with the same font, size and colour, so it uses the same embedded face as the text
/// beside it and sits on the same baseline. Only the digits it needs are embedded.</para>
/// </remarks>
public sealed class PageNumber : Element
{
    /// <summary>Which number to show. Default <see cref="PageNumberKind.Current"/>.</summary>
    public PageNumberKind Kind { get; set; }

    /// <summary>Font family, as for <see cref="Text.FontName"/>. Null, the default, is the text default.</summary>
    public string? FontName { get; set; }

    /// <summary>Weight of the font, 400 regular or 700 bold. Default 400.</summary>
    public int Weight { get; set; } = 400;

    /// <summary>Font size in points. Default 11.</summary>
    public double Size { get; set; } = 11;

    /// <summary>Text colour. Default black.</summary>
    public LayoutColor Color { get; set; }

    /// <summary>Line distance as a multiple of the font size. Default 1.2.</summary>
    public double LineSpacing { get; set; } = 1.2;

    /// <summary>Where the number (for a total: its form) sits when the element is wider than it. Default <see cref="HorizontalAlignment.Start"/>.</summary>
    public HorizontalAlignment Alignment { get; set; }

    /// <summary>For totals, how many digits the form has room for. Default 4. The form is that many times the width of "0".</summary>
    public int ReservedDigits { get; set; } = 4;

    internal sealed record Data(PdfFormXObject Form, double BoxWidth);

    private Text MakeText(string content, HorizontalAlignment alignment) => new()
    {
        Content = content,
        FontName = FontName,
        Weight = Weight,
        Size = Size,
        Color = Color,
        LineSpacing = LineSpacing,
        Alignment = alignment,
    };

    private int Digits => Math.Clamp(ReservedDigits, 1, 12);

    private Text CurrentText(LayoutContext ctx) => MakeText(Numbering.Format(ctx.Page.Number, ctx.Page.Style), Alignment);

    internal override WidthRange WidthsCore(LayoutContext ctx)
    {
        if (Kind == PageNumberKind.Current)
        {
            double w = ctx.Text.Prepare(CurrentText(ctx), 1e6, ctx).NaturalWidth;
            return new WidthRange(w, w);
        }

        double box = ctx.Text.Prepare(MakeText(new string('0', Digits), HorizontalAlignment.Start), 1e6, ctx).NaturalWidth;
        return new WidthRange(box, box);
    }

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        if (Kind == PageNumberKind.Current)
        {
            return CurrentText(ctx).Layout(ctx, c, null);
        }

        ITextBlock probe = ctx.Text.Prepare(MakeText(new string('0', Digits), HorizontalAlignment.Start), 1e6, ctx);
        double box = probe.NaturalWidth;
        double lh = probe.LineHeight(0);
        if (lh > c.Height + 1e-9)
        {
            return ctx.NoFit(c, box, lh, "A line of text is taller than the space offered");
        }

        PdfFormXObject form = TotalForm(ctx, box, lh);
        double width = c.WidthBounded ? c.Width : box;
        return FlowFit.Complete(lh, new Placed(this, 0, 0, width, lh) { Data = new Data(form, box) });
    }

    private PdfFormXObject TotalForm(LayoutContext ctx, double boxWidth, double lh)
    {
        int section = Kind == PageNumberKind.SectionTotal ? ctx.Page.Section : -1;
        NumberingStyle style = Kind == PageNumberKind.SectionTotal ? ctx.Page.Style : NumberingStyle.Arabic;
        string key = string.Create(CultureInfo.InvariantCulture, $"{FontName}|{Weight}|{Size}|{Color.R},{Color.G},{Color.B}|{Digits}|{LineSpacing}|{section}|{(int)style}");
        int digits = Digits;
        return ctx.TotalForm(key, new PdfRectangle(0, 0, boxWidth, lh), section, (fb, count) =>
        {
            // The number is shaped and drawn like any other text, so it uses the same (embedded) face as the text beside it and only the
            // digits it needs are subset into the file.
            Text number = MakeText(Numbering.Format(count, style), HorizontalAlignment.Start);
            LayoutDirection saved = ctx.Direction;
            ctx.Direction = LayoutDirection.LeftToRight;
            try
            {
                double w = ctx.Text.Prepare(number, 1e6, ctx).NaturalWidth;
                double x = boxWidth - w;
                bool squeeze = w > boxWidth;
                ITextBlock block = ctx.Text.Prepare(number, w + 0.01, ctx);
                var d = new DrawContext(fb, ctx, lh, squeeze ? 0 : x, 0);
                if (squeeze)
                {
                    // More digits than reserved: squeeze rather than clip.
                    fb.Save();
                    fb.Scale(boxWidth / w, 1);
                    block.Draw(d, new Placed(number, 0, 0, w, lh), 0, 1);
                    fb.Restore();
                    ctx.Note($"The total page count has more digits than the {digits} reserved; it is drawn condensed.");
                }
                else
                {
                    block.Draw(d, new Placed(number, 0, 0, w, lh), 0, 1);
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
        double x = d.X + AlignX(Alignment, d.Ctx.Direction, p.Width - data.BoxWidth);
        b.Save();
        b.Translate(x, d.PdfY(p.Height));
        b.DrawForm(data.Form);
        b.Restore();
        tag.EndContent(tagged);
        tag.Close(scope);
    }
}
