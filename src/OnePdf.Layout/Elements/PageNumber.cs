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
/// A page number for headers, footers and content: "3", or the "12" of "page 3 of 12". The current number is plain text laid out for each
/// page. A total is not known while pages are being laid out, so it is a form drawn on every page and filled in once at the end; the form
/// is as wide as <see cref="ReservedDigits"/> digits and the number is right-aligned inside it, so it always ends at the same place.
/// </summary>
/// <remarks>
/// A total with more digits than reserved is squeezed horizontally to fit and a note is recorded. Put the elements of "Page N of M"
/// side by side in a <see cref="Row"/>.
/// </remarks>
public sealed class PageNumber : Element
{
    /// <summary>Which number to show. Default <see cref="PageNumberKind.Current"/>.</summary>
    public PageNumberKind Kind { get; set; }

    /// <summary>A standard font name such as Helvetica. Default Helvetica.</summary>
    public string FontName { get; set; } = "Helvetica";

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

    internal sealed record Data(string? Text, PdfFormXObject? Form, double BoxWidth, double Baseline, double LineHeight, PdfCreatedFont Font);

    private double Lh => Size * (LineSpacing > 0 ? LineSpacing : 1.2);

    internal override WidthRange WidthsCore(LayoutContext ctx)
    {
        double w = NaturalWidth(ctx, out _);
        return new WidthRange(w, w);
    }

    private double NaturalWidth(LayoutContext ctx, out string? text)
    {
        PdfContentBuilder m = ctx.Measurer;
        m.SetFont(ctx.Font(FontName), Size);
        if (Kind == PageNumberKind.Current)
        {
            text = Numbering.Format(ctx.Page.Number, ctx.Page.Style);
            return m.MeasureText(text);
        }

        text = null;
        return m.MeasureText(new string('0', Math.Clamp(ReservedDigits, 1, 12)));
    }

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        PdfCreatedFont font = ctx.Font(FontName);
        double natural = NaturalWidth(ctx, out string? text);
        double lh = Lh;
        if (lh > c.Height + 1e-9)
        {
            return ctx.NoFit(c, natural, lh, "A line of text is taller than the space offered");
        }

        double width = c.WidthBounded ? c.Width : natural;
        double glyphs = (font.Ascent - font.Descent) * Size;
        double baseline = ((lh - glyphs) / 2) + (font.Ascent * Size);
        PdfFormXObject? form = null;
        if (Kind != PageNumberKind.Current)
        {
            form = TotalForm(ctx, font, natural, lh, baseline);
        }

        return FlowFit.Complete(lh, new Placed(this, 0, 0, width, lh) { Data = new Data(text, form, natural, baseline, lh, font) });
    }

    private PdfFormXObject TotalForm(LayoutContext ctx, PdfCreatedFont font, double boxWidth, double lh, double baseline)
    {
        int section = Kind == PageNumberKind.SectionTotal ? ctx.Page.Section : -1;
        NumberingStyle style = Kind == PageNumberKind.SectionTotal ? ctx.Page.Style : NumberingStyle.Arabic;
        string key = string.Create(CultureInfo.InvariantCulture, $"{FontName}|{Size}|{Color.R},{Color.G},{Color.B}|{Math.Clamp(ReservedDigits, 1, 12)}|{LineSpacing}|{section}|{(int)style}");
        double size = Size;
        LayoutColor color = Color;
        return ctx.TotalForm(key, new PdfRectangle(0, 0, boxWidth, lh), section, (fb, count) =>
        {
            fb.SetFont(font, size);
            fb.SetFillRgb(color.R, color.G, color.B);
            string text = Numbering.Format(count, style);
            double w = fb.MeasureText(text);
            double y = lh - baseline;
            if (w > boxWidth)
            {
                // More digits than reserved: squeeze rather than clip.
                fb.Save();
                fb.Scale(boxWidth / w, 1);
                fb.DrawText(text, 0, y);
                fb.Restore();
                ctx.Note($"The total page count has more digits than the {Math.Clamp(ReservedDigits, 1, 12)} reserved; it is drawn condensed.");
            }
            else
            {
                fb.DrawText(text, boxWidth - w, y);
            }
        });
    }

    internal override void DrawCore(in DrawContext d, Placed p)
    {
        var data = (Data)p.Data!;
        PdfContentBuilder b = d.B;
        if (data.Form is { } form)
        {
            double x = d.X + AlignX(Alignment, d.Ctx.Direction, p.Width - data.BoxWidth);
            b.Save();
            b.Translate(x, d.PdfY(p.Height));
            b.DrawForm(form);
            b.Restore();
            return;
        }

        PdfTextAlignment align = Alignment switch
        {
            HorizontalAlignment.Center => PdfTextAlignment.Center,
            HorizontalAlignment.Right => PdfTextAlignment.Right,
            HorizontalAlignment.Left => PdfTextAlignment.Left,
            HorizontalAlignment.End => d.Ctx.Direction == LayoutDirection.LeftToRight ? PdfTextAlignment.Right : PdfTextAlignment.Left,
            _ => d.Ctx.Direction == LayoutDirection.LeftToRight ? PdfTextAlignment.Left : PdfTextAlignment.Right,
        };
        b.SetFont(data.Font, Size);
        b.SetFillRgb(Color.R, Color.G, Color.B);
        b.DrawTextAligned(data.Text!, d.X, d.PdfY(data.Baseline), p.Width, align);
    }
}
