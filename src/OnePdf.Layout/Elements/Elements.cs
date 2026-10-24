using System.Globalization;
using OnePdf.Layout.Pagination;

namespace OnePdf.Layout;

/// <summary>Turns a child by an angle. The element's box is the bounding box of the turned child, which is laid out at its natural size and never splits.</summary>
public sealed class Rotate : SingleChildElement
{
    /// <summary>Clockwise angle in degrees. Default 0.</summary>
    public double Degrees { get; set; }

    internal sealed record Data(double ChildWidth, double ChildHeight, double A, double B, double C, double D, double E, double F);

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        double deg = Degrees % 360;
        double rad = deg * Math.PI / 180;
        double cos = Snap(Math.Cos(rad));
        double sin = Snap(Math.Sin(rad));
        bool quarter = Math.Abs(sin) > 0.5 && Math.Abs(cos) < 0.5;
        // A quarter turn swaps the roles of width and height, so the child gets the page's height as its width.
        double offeredW = quarter ? c.Height : c.Width;
        FlowFit f = LayoutChild(ctx, new Constraint(offeredW, double.PositiveInfinity, true), null);
        if (f.Placed is not Placed child)
        {
            return f;
        }

        double cw = child.Width;
        double ch = child.Height;
        double bw = Math.Abs(cw * cos) + Math.Abs(ch * sin);
        double bh = Math.Abs(cw * sin) + Math.Abs(ch * cos);
        if (bh > c.Height + 1e-9)
        {
            return ctx.NoFit(c, bw, bh, "The turned element is taller than the space offered");
        }

        // PDF turns counter-clockwise for positive angles; the child frame's centre goes to the box centre.
        double phi = -rad;
        double a = Snap(Math.Cos(phi));
        double b = Snap(Math.Sin(phi));
        var node = new Placed(this, 0, 0, bw, bh) { Data = new Data(cw, ch, a, b, -b, a, 0, 0) };
        child.X = 0;
        child.Y = 0;
        node.Children.Add(child);
        return FlowFit.Complete(bh, node);
    }

    internal override void DrawCore(in DrawContext d, Placed p)
    {
        var data = (Data)p.Data!;
        Placed child = p.Children[0];
        double cx = d.X + (p.Width / 2);
        double cy = d.PdfY(p.Height / 2);
        double e = cx - ((data.A * data.ChildWidth / 2) + (data.C * data.ChildHeight / 2));
        double f = cy - ((data.B * data.ChildWidth / 2) + (data.D * data.ChildHeight / 2));
        d.B.Save();
        d.B.Transform(data.A, data.B, data.C, data.D, e, f);
        child.Element.Draw(d.InFrame(data.ChildHeight), child);
        d.B.Restore();
    }

    private static double Snap(double v) => Math.Abs(v) < 1e-12 ? 0 : Math.Abs(v - 1) < 1e-12 ? 1 : Math.Abs(v + 1) < 1e-12 ? -1 : v;
}

/// <summary>Scales a child. The child is laid out in the space divided by the factors; the box is the scaled size. It never splits.</summary>
public sealed class Scale : SingleChildElement
{
    /// <summary>Horizontal factor, greater than zero. Default 1.</summary>
    public double X { get; set; } = 1;

    /// <summary>Vertical factor, greater than zero. Default 1.</summary>
    public double Y { get; set; } = 1;

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        if (!(X > 0) || !(Y > 0) || !double.IsFinite(X) || !double.IsFinite(Y))
        {
            throw new PdfLayoutException(ctx.PathText, c.Width, c.Height, double.NaN, double.NaN, "Scale factors must be finite and greater than zero");
        }

        FlowFit f = LayoutChild(ctx, new Constraint(c.Width / X, double.PositiveInfinity, true), null);
        if (f.Placed is not Placed child)
        {
            return f;
        }

        double w = child.Width * X;
        double h = child.Height * Y;
        if (h > c.Height + 1e-9)
        {
            return ctx.NoFit(c, w, h, "The scaled element is taller than the space offered");
        }

        var node = new Placed(this, 0, 0, w, h);
        child.X = 0;
        child.Y = 0;
        node.Children.Add(child);
        return FlowFit.Complete(h, node);
    }

    internal override void DrawCore(in DrawContext d, Placed p)
    {
        Placed child = p.Children[0];
        d.B.Save();
        d.B.Transform(X, 0, 0, Y, d.X, d.PdfY(p.Height));
        child.Element.Draw(d.InFrame(child.Height), child);
        d.B.Restore();
    }
}

/// <summary>Moves a child when drawing, without changing the space it takes. What moves out of its box is deliberate overflow.</summary>
public sealed class Translate : SingleChildElement
{
    /// <summary>Points to the right (to the start side in right-to-left content). Default 0.</summary>
    public double X { get; set; }

    /// <summary>Points downward. Default 0.</summary>
    public double Y { get; set; }

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        FlowFit f = LayoutChild(ctx, c, resume);
        if (f.Placed is not Placed child)
        {
            return f;
        }

        var node = new Placed(this, 0, 0, child.Width, child.Height);
        child.X = ctx.Direction == LayoutDirection.RightToLeft ? -X : X;
        child.Y = Y;
        node.Children.Add(child);
        return f.Kind == FlowKind.Partial ? FlowFit.Partial(f.Height, node, f.Resume) : FlowFit.Complete(f.Height, node);
    }
}

/// <summary>
/// Keeps its child on one page. If the child does not fit in what is left of a page it moves to the next. A child taller than a whole
/// page splits anyway and a note is recorded.
/// </summary>
public sealed class KeepTogether : SingleChildElement
{
    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        FlowFit f = LayoutChild(ctx, c, resume);
        if (f.Kind != FlowKind.Partial)
        {
            return f;
        }

        if (!c.AtTop)
        {
            return FlowFit.NoFit(double.NaN);
        }

        ctx.Note("The element is taller than a page, so it is split although it should be kept together.");
        return f;
    }
}

/// <summary>Moves its child to the next page unless at least <see cref="Height"/> points are left; the child is then placed normally.</summary>
public sealed class EnsureSpace : SingleChildElement
{
    /// <summary>Points that must be free. Default 0.</summary>
    public double Height { get; set; }

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        if (c.Height < Height - 1e-9)
        {
            if (!c.AtTop)
            {
                return FlowFit.NoFit(Height);
            }

            ctx.Note("The page is shorter than the space this element asked for.");
        }

        return LayoutChild(ctx, c, resume);
    }
}

/// <summary>A forced page break. Whatever follows in the same column starts a new page. At the top of a page it does nothing, so no blank page appears.</summary>
public sealed class PageBreak : Element
{
    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume) => FlowFit.Break();
}

/// <summary>Empty space. Never splits.</summary>
public sealed class Spacer : Element
{
    /// <summary>Width in points; the offered width when null.</summary>
    public double? Width { get; set; }

    /// <summary>Height in points. Default 0.</summary>
    public double Height { get; set; }

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        double h = Math.Max(0, Height);
        if (h > c.Height + 1e-9)
        {
            return ctx.NoFit(c, Width ?? c.Width, h);
        }

        double w = Width ?? (c.WidthBounded ? c.Width : 0);
        return FlowFit.Complete(h, new Placed(this, 0, 0, w, h));
    }
}

/// <summary>What a <see cref="Canvas"/> callback draws on: a builder whose origin is the lower-left corner of the canvas box.</summary>
public sealed class CanvasSurface
{
    internal CanvasSurface(PdfContentBuilder builder, double width, double height)
    {
        Builder = builder;
        Width = width;
        Height = height;
    }

    /// <summary>The content builder. Coordinates are points, x to the right and y upward from the box's lower-left corner; drawing is clipped to the box.</summary>
    public PdfContentBuilder Builder { get; }

    /// <summary>Box width in points.</summary>
    public double Width { get; }

    /// <summary>Box height in points.</summary>
    public double Height { get; }
}

/// <summary>A box drawn by a callback with the content builder. Never splits. The callback runs once per page the canvas is drawn on.</summary>
public sealed class Canvas : Element
{
    /// <summary>Width in points; the offered width when null.</summary>
    public double? Width { get; set; }

    /// <summary>Height in points. Default 0.</summary>
    public double Height { get; set; }

    /// <summary>Draws into the box.</summary>
    public Action<CanvasSurface>? Paint { get; set; }

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        double h = Math.Max(0, Height);
        double w = Width ?? (c.WidthBounded ? c.Width : 0);
        if (h > c.Height + 1e-9)
        {
            return ctx.NoFit(c, w, h);
        }

        return FlowFit.Complete(h, new Placed(this, 0, 0, w, h));
    }

    internal override void DrawCore(in DrawContext d, Placed p)
    {
        if (Paint is null)
        {
            return;
        }

        PdfContentBuilder b = d.B;
        b.Save();
        b.Translate(d.X, d.PdfY(p.Height));
        b.Rectangle(0, 0, p.Width, p.Height);
        b.Clip();
        b.EndPath();
        Paint(new CanvasSurface(b, p.Width, p.Height));
        b.Restore();
    }
}

/// <summary>A reusable piece of the tree: compose it from other elements once, use it as one element.</summary>
public abstract class Component : Element
{
    private Element? _composed;

    /// <summary>Builds the elements this component stands for. Called once per component object.</summary>
    protected abstract Element Compose();

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        _composed ??= Compose() ?? throw new InvalidOperationException("Compose returned null.");
        return _composed.Layout(ctx, c, resume);
    }
}

/// <summary>
/// A raster image. <see cref="ImageFit.FitWidth"/> and <see cref="ImageFit.FitHeight"/> size the box to the picture;
/// <see cref="ImageFit.Contain"/>, <see cref="ImageFit.Cover"/> and <see cref="ImageFit.Stretch"/> fill the box they are offered, so put the image
/// inside a <see cref="Box"/> with a size to control it. Never splits: an image taller than a page raises <see cref="PdfLayoutException"/>.
/// </summary>
public sealed class Image : Element
{
    /// <summary>JPEG file bytes, added to the output document once. Ignored when <see cref="Created"/> is set.</summary>
    public ReadOnlyMemory<byte> Jpeg { get; set; }

    /// <summary>An image already added to the output document, to share one image between many elements.</summary>
    public PdfCreatedImage? Created { get; set; }

    /// <summary>How the picture fills its box. Default <see cref="ImageFit.FitWidth"/>.</summary>
    public ImageFit Fit { get; set; }

    /// <summary>Pixels per inch that give the natural size. Default 72, so one pixel is one point.</summary>
    public double Dpi { get; set; } = 72;

    /// <summary>For Contain: where the picture sits horizontally. Default centred.</summary>
    public HorizontalAlignment HorizontalAlignment { get; set; } = HorizontalAlignment.Center;

    /// <summary>For Contain: where the picture sits vertically. Default centred.</summary>
    public VerticalAlignment VerticalAlignment { get; set; } = VerticalAlignment.Middle;

    internal sealed record Data(PdfCreatedImage Image, double X, double Y, double W, double H, bool Clip);

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        PdfCreatedImage img = ctx.ImageFor(this);
        double dpi = Dpi > 0 ? Dpi : 72;
        double nw = img.Width * 72.0 / dpi;
        double nh = img.Height * 72.0 / dpi;
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
            {
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
            }

            case ImageFit.FitWidth:
            {
                dw = c.WidthBounded ? c.Width : nw;
                dh = dw * ratio;
                boxW = dw;
                boxH = dh;
                break;
            }

            default:
            {
                // The box is what is offered; an unbounded side follows the other side's proportions.
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
        }

        if (boxH > c.Height + 1e-9)
        {
            return ctx.NoFit(c, boxW, boxH, "The image is taller than the space offered");
        }

        return FlowFit.Complete(boxH, new Placed(this, 0, 0, boxW, boxH) { Data = new Data(img, dx, dy, dw, dh, clip) });
    }

    internal override void DrawCore(in DrawContext d, Placed p)
    {
        var data = (Data)p.Data!;
        PdfContentBuilder b = d.B;
        if (data.Clip)
        {
            b.Save();
            b.Rectangle(d.X, d.PdfY(p.Height), p.Width, p.Height);
            b.Clip();
            b.EndPath();
        }

        b.DrawImage(data.Image, d.X + data.X, d.PdfY(data.Y + data.H), data.W, data.H);
        if (data.Clip)
        {
            b.Restore();
        }
    }
}

/// <summary>
/// Plain text in one standard font, wrapped greedily at spaces. It splits between lines with widow and orphan control. This element is
/// measured with the standard fonts' own widths; it is the simple text path and will be superseded by a shaped paragraph element.
/// </summary>
public sealed class Text : Element
{
    /// <summary>The text. '\n' starts a new paragraph.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>A standard font name such as Helvetica, Times-Roman or Courier-Bold. Default Helvetica.</summary>
    public string FontName { get; set; } = "Helvetica";

    /// <summary>Font size in points. Default 11.</summary>
    public double Size { get; set; } = 11;

    /// <summary>Text colour. Default black.</summary>
    public LayoutColor Color { get; set; }

    /// <summary>Line placement. Default <see cref="HorizontalAlignment.Start"/>.</summary>
    public HorizontalAlignment Alignment { get; set; }

    /// <summary>Line distance as a multiple of the font size. Default 1.2.</summary>
    public double LineSpacing { get; set; } = 1.2;

    internal sealed record Fragment(ITextBlock Block, int First, int Count, PdfCreatedFont Font);

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        PdfCreatedFont font = ctx.Font(FontName);
        ITextBlock block = ctx.Text.Prepare(this, font, c.WidthBounded ? c.Width : 1e6, ctx);
        int total = block.LineCount;
        int first = resume is int k ? k : 0;
        int left = total - first;
        double width = c.WidthBounded ? c.Width : block.NaturalWidth;

        // How many of the remaining lines fit.
        int fit = 0;
        double used = 0;
        while (fit < left && used + block.LineHeight(first + fit) <= c.Height + 1e-9)
        {
            used += block.LineHeight(first + fit);
            fit++;
        }

        int take = fit;
        if (fit < left)
        {
            FlowRules r = ctx.Rules;
            bool starting = first == 0;
            if (starting && total < r.Orphans + r.Widows)
            {
                take = 0;
            }
            else
            {
                int remaining = left - take;
                if (remaining < r.Widows)
                {
                    take = left - r.Widows;
                }

                if (starting && take < r.Orphans)
                {
                    take = 0;
                }

                if (take < 0)
                {
                    take = 0;
                }
            }

            if (take == 0)
            {
                if (!c.AtTop)
                {
                    return FlowFit.NoFit(block.LineHeight(first));
                }

                // A fresh page cannot do better: place what fits and ignore the widow and orphan rules.
                take = fit;
                if (take == 0)
                {
                    return ctx.NoFit(c, width, block.LineHeight(first), "A line of text is taller than the space offered");
                }

                ctx.Note("The paragraph cannot honour widow and orphan control on a page this short.");
            }
        }

        double h = 0;
        for (int i = 0; i < take; i++)
        {
            h += block.LineHeight(first + i);
        }

        var node = new Placed(this, 0, 0, width, h) { Data = new Fragment(block, first, take, font) };
        return take == left ? FlowFit.Complete(h, node) : FlowFit.Partial(h, node, first + take);
    }

    internal override void DrawCore(in DrawContext d, Placed p)
    {
        var f = (Fragment)p.Data!;
        PdfContentBuilder b = d.B;
        b.SetFont(f.Font, Size);
        b.SetFillRgb(Color.R, Color.G, Color.B);
        f.Block.Draw(d, p, f.First, f.Count);
    }
}

/// <summary>Measures and draws wrapped text; the paragraph engine implements the same pair later.</summary>
internal interface ITextEngine
{
    ITextBlock Prepare(Text text, PdfCreatedFont font, double width, LayoutContext ctx);
}

/// <summary>Wrapped lines of one text element at one width.</summary>
internal interface ITextBlock
{
    int LineCount { get; }

    double NaturalWidth { get; }

    double LineHeight(int line);

    void Draw(in DrawContext d, Placed p, int first, int count);
}

/// <summary>Greedy wrapping on the standard fonts through the content builder's own measurement.</summary>
internal sealed class PlainTextEngine : ITextEngine
{
    public ITextBlock Prepare(Text text, PdfCreatedFont font, double width, LayoutContext ctx)
    {
        PdfContentBuilder m = ctx.Measurer;
        m.SetFont(font, text.Size);
        IReadOnlyList<string> lines = m.WrapText(text.Content ?? string.Empty, Math.Max(width, 1));
        double natural = 0;
        foreach (string line in lines)
        {
            natural = Math.Max(natural, m.MeasureText(line));
        }

        return new Block(text, font, lines, natural);
    }

    private sealed class Block(Text text, PdfCreatedFont font, IReadOnlyList<string> lines, double natural) : ITextBlock
    {
        public int LineCount => lines.Count;

        public double NaturalWidth => natural;

        public double LineHeight(int line) => text.Size * text.LineSpacing;

        public void Draw(in DrawContext d, Placed p, int first, int count)
        {
            double lh = text.Size * text.LineSpacing;
            double glyphs = (font.Ascent - font.Descent) * text.Size;
            PdfTextAlignment align = text.Alignment switch
            {
                HorizontalAlignment.Center => PdfTextAlignment.Center,
                HorizontalAlignment.Right => PdfTextAlignment.Right,
                HorizontalAlignment.Left => PdfTextAlignment.Left,
                HorizontalAlignment.End => d.Ctx.Direction == LayoutDirection.LeftToRight ? PdfTextAlignment.Right : PdfTextAlignment.Left,
                _ => d.Ctx.Direction == LayoutDirection.LeftToRight ? PdfTextAlignment.Left : PdfTextAlignment.Right,
            };
            for (int i = 0; i < count; i++)
            {
                double top = i * lh;
                double baseline = top + ((lh - glyphs) / 2) + (font.Ascent * text.Size);
                string line = lines[first + i];
                if (line.Length > 0)
                {
                    d.B.DrawTextAligned(line, d.X, d.PdfY(baseline), p.Width, align);
                }
            }
        }
    }
}
