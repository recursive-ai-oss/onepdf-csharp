using OnePdf.Layout.Pagination;
using OnePdf.Layout.Svg;

namespace OnePdf.Layout;

/// <summary>
/// A vector picture from an SVG document, drawn as PDF paths, shadings and real text (see <see cref="SvgImage"/> for what is supported). It
/// is placed like an <see cref="Image"/>: <see cref="ImageFit.FitWidth"/> and <see cref="ImageFit.FitHeight"/> size the box to the picture, the other fits
/// fill the box they are offered. Never splits: a picture taller than a page raises <see cref="PdfLayoutException"/>. In a tagged document it is a
/// <c>Figure</c> whose alternate text is <see cref="Element.AltText"/>, or else the SVG's <c>title</c> (then <c>desc</c>).
/// </summary>
public sealed class SvgPicture : Element
{
    /// <summary>The parsed document. Required.</summary>
    public SvgImage? Source { get; set; }

    /// <summary>How the picture fills its box. Default <see cref="ImageFit.FitWidth"/>.</summary>
    public ImageFit Fit { get; set; }

    /// <summary>CSS pixels per inch that give the natural size. Default 96, so one pixel is 0.75 point.</summary>
    public double Dpi { get; set; } = 96;

    /// <summary>For Contain: where the picture sits horizontally. Default centred.</summary>
    public HorizontalAlignment HorizontalAlignment { get; set; } = HorizontalAlignment.Center;

    /// <summary>For Contain: where the picture sits vertically. Default centred.</summary>
    public VerticalAlignment VerticalAlignment { get; set; } = VerticalAlignment.Middle;

    internal sealed record Data(double X, double Y, double W, double H, bool Clip);

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        SvgImage svg = Source ?? throw new PdfLayoutException(ctx.PathText, c.Width, c.Height, double.NaN, double.NaN, "The SVG picture has no source");
        if (AltText is null && !Artifact && ctx.LayoutArtifactDepth == 0 && ctx.Options.Tagged)
        {
            ctx.NoteOnce(this, "The SVG has no alternate text (AltText) and no title or description; assistive technology cannot describe it. Set AltText, or Artifact for a decorative picture.");
        }

        double dpi = Dpi > 0 ? Dpi : 96;
        double nw = svg.WidthPx * 72.0 / dpi;
        double nh = svg.HeightPx * 72.0 / dpi;
        double ratio = nh / nw;
        double boxW;
        double boxH;
        double dx = 0;
        double dy = 0;
        double dw;
        double dh;
        bool clip = false;
        switch (Fit)
        {
            case ImageFit.FitHeight:
                dh = c.HeightBounded ? c.Height : nh;
                dw = dh / ratio;
                if (c.WidthBounded && dw > c.Width)
                {
                    dw = c.Width;
                    dh = dw * ratio;
                }

                boxW = dw;
                boxH = dh;
                break;
            case ImageFit.FitWidth:
                dw = c.WidthBounded ? c.Width : nw;
                dh = dw * ratio;
                boxW = dw;
                boxH = dh;
                break;
            default:
                if (c.WidthBounded && c.HeightBounded)
                {
                    boxW = c.Width;
                    boxH = c.Height;
                }
                else if (c.WidthBounded)
                {
                    boxW = c.Width;
                    boxH = boxW * ratio;
                }
                else if (c.HeightBounded)
                {
                    boxH = c.Height;
                    boxW = boxH / ratio;
                }
                else
                {
                    boxW = nw;
                    boxH = nh;
                }

                if (Fit == ImageFit.Stretch)
                {
                    dw = boxW;
                    dh = boxH;
                }
                else
                {
                    double s = Fit == ImageFit.Contain ? Math.Min(boxW / nw, boxH / nh) : Math.Max(boxW / nw, boxH / nh);
                    dw = nw * s;
                    dh = nh * s;
                    dx = Element.AlignX(HorizontalAlignment, ctx.Direction, boxW - dw);
                    dy = Element.AlignY(VerticalAlignment, boxH - dh);
                    clip = Fit == ImageFit.Cover;
                    if (clip)
                    {
                        dx = (boxW - dw) / 2;
                        dy = (boxH - dh) / 2;
                    }
                }

                break;
        }

        if (boxH > c.Height + 1e-9)
        {
            return ctx.NoFit(c, boxW, boxH, "The SVG picture is taller than the space offered");
        }

        return FlowFit.Complete(boxH, new Placed(this, 0, 0, boxW, boxH) { Data = new Data(dx, dy, dw, dh, clip) });
    }

    internal override void DrawCore(in DrawContext d, Placed p)
    {
        var data = (Data)p.Data!;
        SvgImage svg = Source!;
        bool tagged = d.Ctx.Tag.BeginContent();
        (double, double, double, double)? clip = data.Clip ? (d.X, d.PdfY(p.Height), p.Width, p.Height) : null;
        svg.PaintDirect(d.B, new LayoutSvgHost(d.Ctx), d.X + data.X, d.PdfY(data.Y + data.H), data.W, data.H, clip, CancellationToken.None);
        d.Ctx.Tag.EndContent(tagged);
    }

    internal override bool FigureByDefault => true;
}
