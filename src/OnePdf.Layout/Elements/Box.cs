using OnePdf.Layout.Pagination;

namespace OnePdf.Layout;

/// <summary>Base of elements that wrap exactly one child.</summary>
public abstract class SingleChildElement : Element
{
    /// <summary>The wrapped element.</summary>
    public Element? Child { get; set; }

    internal FlowFit LayoutChild(LayoutContext ctx, in Constraint c, object? resume)
    {
        if (Child is null)
        {
            return FlowFit.Complete(0, null);
        }

        return Child.Layout(ctx, c, resume);
    }
}

/// <summary>
/// A rectangle around one child: margin outside, then a background and borders, then padding, then the child. It also carries size rules
/// (fixed, minimum and maximum, aspect ratio), opacity and the alignment of the child inside it.
/// </summary>
/// <remarks>
/// Width is the offered width unless a width is set; height follows the child unless set. The size properties describe the box without its
/// margin. A box with a fixed height, an aspect ratio or a maximum height is atomic and cuts off a taller child (a note is recorded).
/// Otherwise the box splits with its child: the top margin, border and padding belong to the first piece, the bottom ones to the last, and
/// rounded corners are rounded only at the outer ends. Opacity is applied to what the box paints (no group), so overlapping paint inside
/// shows through.
/// </remarks>
public sealed class Box : SingleChildElement
{
    /// <summary>Space outside the box on each physical side, in points.</summary>
    public Insets Margin { get; set; }

    /// <summary>Space between the border and the child on each physical side, in points.</summary>
    public Insets Padding { get; set; }

    /// <summary>Margin at the start edge; replaces the left or right margin according to the direction when set.</summary>
    public double? MarginStart { get; set; }

    /// <summary>Margin at the end edge; replaces the left or right margin according to the direction when set.</summary>
    public double? MarginEnd { get; set; }

    /// <summary>Padding at the start edge; replaces the left or right padding according to the direction when set.</summary>
    public double? PaddingStart { get; set; }

    /// <summary>Padding at the end edge; replaces the left or right padding according to the direction when set.</summary>
    public double? PaddingEnd { get; set; }

    /// <summary>Top border.</summary>
    public Border BorderTop { get; set; }

    /// <summary>Right border.</summary>
    public Border BorderRight { get; set; }

    /// <summary>Bottom border.</summary>
    public Border BorderBottom { get; set; }

    /// <summary>Left border.</summary>
    public Border BorderLeft { get; set; }

    /// <summary>Border at the start edge; replaces the left or right border according to the direction when set.</summary>
    public Border? BorderStart { get; set; }

    /// <summary>Border at the end edge; replaces the left or right border according to the direction when set.</summary>
    public Border? BorderEnd { get; set; }

    /// <summary>Sets all four borders.</summary>
    public void SetBorders(Border border)
    {
        BorderTop = border;
        BorderRight = border;
        BorderBottom = border;
        BorderLeft = border;
    }

    /// <summary>Corner radius in points. Borders follow it only when all four are equal. Default 0.</summary>
    public double CornerRadius { get; set; }

    /// <summary>Fill colour behind the child, inside the border. None when null.</summary>
    public LayoutColor? Background { get; set; }

    /// <summary>Opacity 0..1 of what the box paints. Default 1.</summary>
    public double Opacity { get; set; } = 1;

    /// <summary>Fixed width in points (clamped to the offered width, with a note), or null for the offered width.</summary>
    public double? Width { get; set; }

    /// <summary>Smallest width in points.</summary>
    public double? MinWidth { get; set; }

    /// <summary>Largest width in points.</summary>
    public double? MaxWidth { get; set; }

    /// <summary>Fixed height in points, or null for the child's height.</summary>
    public double? Height { get; set; }

    /// <summary>Smallest height in points.</summary>
    public double? MinHeight { get; set; }

    /// <summary>Largest height in points.</summary>
    public double? MaxHeight { get; set; }

    /// <summary>Width divided by height. Sets the height from the width when no height is given and the width is known.</summary>
    public double? AspectRatio { get; set; }

    /// <summary>When true and the offered height is bounded, the box is as tall as it is offered (used for backgrounds in layers).</summary>
    public bool ExpandHeight { get; set; }

    /// <summary>Where the child sits when narrower than the box. Default <see cref="HorizontalAlignment.Start"/>.</summary>
    public HorizontalAlignment HorizontalAlignment { get; set; }

    /// <summary>Where the child sits when shorter than the box (only for a box on one page). Default <see cref="VerticalAlignment.Top"/>.</summary>
    public VerticalAlignment VerticalAlignment { get; set; }

    private sealed record BoxResume(object? Inner);

    internal sealed class BoxData
    {
        public double Ml, Mr, Mt, Mb;
        public double BoxX, BoxY, BoxW, BoxH;
        public Border Top, Right, Bottom, Left;
        public bool First, Last;
    }

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        var r = (BoxResume?)resume;
        bool first = r is null;
        LayoutDirection dir = ctx.Direction;
        bool ltr = dir == LayoutDirection.LeftToRight;

        double ml = ltr ? MarginStart ?? Margin.Left : MarginEnd ?? Margin.Left;
        double mr = ltr ? MarginEnd ?? Margin.Right : MarginStart ?? Margin.Right;
        double pl = ltr ? PaddingStart ?? Padding.Left : PaddingEnd ?? Padding.Left;
        double pr = ltr ? PaddingEnd ?? Padding.Right : PaddingStart ?? Padding.Right;
        Border bl = (ltr ? BorderStart : BorderEnd) ?? BorderLeft;
        Border br = (ltr ? BorderEnd : BorderStart) ?? BorderRight;
        Border bt = BorderTop;
        Border bb = BorderBottom;
        ml = Math.Max(0, ml);
        mr = Math.Max(0, mr);
        pl = Math.Max(0, pl);
        pr = Math.Max(0, pr);
        double mt = Math.Max(0, Margin.Top);
        double mb = Math.Max(0, Margin.Bottom);
        double pt = Math.Max(0, Padding.Top);
        double pb = Math.Max(0, Padding.Bottom);
        double btw = Math.Max(0, bt.Width);
        double bbw = Math.Max(0, bb.Width);
        double blw = Math.Max(0, bl.Width);
        double brw = Math.Max(0, br.Width);

        // Width of the bordered box (margin excluded).
        double? boxW = null;
        if (Width is { } fw)
        {
            boxW = fw;
        }
        else if (c.WidthBounded)
        {
            boxW = Math.Max(0, c.Width - ml - mr);
        }

        if (boxW is { } bwv)
        {
            if (MinWidth is { } minw)
            {
                bwv = Math.Max(bwv, minw);
            }

            if (MaxWidth is { } maxw)
            {
                bwv = Math.Min(bwv, maxw);
            }

            if (c.WidthBounded && bwv > Math.Max(0, c.Width - ml - mr) + 1e-9)
            {
                ctx.Note("The box is wider than the space offered and was narrowed to fit.");
                bwv = Math.Max(0, c.Width - ml - mr);
            }

            boxW = bwv;
        }

        double innerW = boxW is { } bw2 ? Math.Max(0, bw2 - blw - brw - pl - pr) : double.PositiveInfinity;

        double? fixedH = Height;
        if (fixedH is null && AspectRatio is { } ar && ar > 0 && boxW is { } bw3)
        {
            fixedH = bw3 / ar;
        }

        if (fixedH is null && ExpandHeight && c.HeightBounded)
        {
            fixedH = Math.Max(0, c.Height - mt - mb);
        }

        if (fixedH is { } fh0)
        {
            if (MinHeight is { } minh)
            {
                fh0 = Math.Max(fh0, minh);
            }

            if (MaxHeight is { } maxh)
            {
                fh0 = Math.Min(fh0, maxh);
            }

            fixedH = fh0;
        }

        double topExtra = (first ? mt + btw + pt : 0);
        double bottomExtra = mb + bbw + pb;
        FlowFit childFit;
        double boxH;
        bool last;
        bool cut = false;
        object? childResume = r?.Inner;

        if (fixedH is { } fixedHeight)
        {
            // Atomic: the whole box must fit the page.
            double total = mt + fixedHeight + mb;
            if (total > c.Height + 1e-9)
            {
                return ctx.NoFit(c, boxW ?? double.NaN, total, "The box is taller than the space offered");
            }

            double innerH = Math.Max(0, fixedHeight - btw - bbw - pt - pb);
            childFit = LayoutChild(ctx, new Constraint(innerW, innerH, true), childResume);
            cut = childFit.Kind == FlowKind.Partial;
            if (cut)
            {
                ctx.Note("The content is taller than the box and is cut off.");
            }

            boxH = fixedHeight;
            last = true;
            first = true;
            topExtra = mt + btw + pt;
        }
        else
        {
            double avail = c.HeightBounded ? c.Height - topExtra - bottomExtra : double.PositiveInfinity;
            bool limited = false;
            if (MaxHeight is { } maxh2)
            {
                double cap = maxh2 - btw - bbw - pt - pb;
                if (cap < avail)
                {
                    avail = Math.Max(0, cap);
                    limited = true;
                }
            }

            if (avail < 0)
            {
                return ctx.NoFit(c, boxW ?? double.NaN, topExtra + bottomExtra, "The box's margin, border and padding do not fit the space offered");
            }

            childFit = LayoutChild(ctx, new Constraint(innerW, avail, c.AtTop), childResume);
            if (childFit.Kind == FlowKind.NoFit)
            {
                return ctx.NoFit(c, boxW ?? double.NaN, childFit.Requested + topExtra + bottomExtra);
            }

            if (childFit.Kind == FlowKind.Break)
            {
                return childFit;
            }

            if (childFit.Kind == FlowKind.Partial && !limited && c.HeightBounded)
            {
                // The child splits, so this piece is not the last and needs no bottom margin, border or padding: offer that space too.
                FlowFit wider = LayoutChild(ctx, new Constraint(innerW, c.Height - topExtra, c.AtTop), childResume);
                if (wider.Kind == FlowKind.Partial)
                {
                    childFit = wider;
                }
            }

            if (limited && childFit.Kind == FlowKind.Partial)
            {
                ctx.Note("The content is taller than the box's maximum height and is cut off.");
                cut = true;
            }

            last = childFit.Kind != FlowKind.Partial || cut;
            double childH = childFit.Height;
            boxH = (first ? btw + pt : 0) + childH + (last ? bbw + pb : 0);
            if (last && first && MinHeight is { } minh2)
            {
                boxH = Math.Max(boxH, minh2);
            }
        }

        var childPlaced = childFit.Placed as Placed;
        double childW = childPlaced?.Width ?? 0;
        double finalBoxW = boxW ?? Math.Min(Math.Max(childW + blw + brw + pl + pr, MinWidth ?? 0), MaxWidth ?? double.PositiveInfinity);
        if (boxW is null)
        {
            innerW = Math.Max(0, finalBoxW - blw - brw - pl - pr);
        }

        double nodeW = finalBoxW + ml + mr;
        double nodeH = (first ? mt : 0) + boxH + (last ? mb : 0);
        var data = new BoxData
        {
            Ml = ml, Mr = mr, Mt = first ? mt : 0, Mb = last ? mb : 0,
            BoxX = ml, BoxY = first ? mt : 0, BoxW = finalBoxW, BoxH = boxH,
            Top = btw > 0 ? bt : default, Right = brw > 0 ? br : default, Bottom = bbw > 0 ? bb : default, Left = blw > 0 ? bl : default,
            First = first, Last = last,
        };
        var node = new Placed(this, 0, 0, nodeW, nodeH) { Data = data };
        if (childPlaced is not null)
        {
            double cx = ml + blw + pl;
            double cy = (first ? mt + btw + pt : 0);
            double innerH2 = boxH - (first ? btw + pt : 0) - (last ? bbw + pb : 0);
            cx += AlignX(HorizontalAlignment, dir, innerW - childW);
            if (first && last)
            {
                cy += AlignY(VerticalAlignment, innerH2 - childPlaced.Height);
            }

            childPlaced.X = cx;
            childPlaced.Y = cy;
            node.Children.Add(childPlaced);
        }

        return last ? FlowFit.Complete(nodeH, node) : FlowFit.Partial(nodeH, node, new BoxResume(childFit.Resume));
    }

    internal override void DrawCore(in DrawContext d, Placed p)
    {
        var data = (BoxData)p.Data!;
        PdfContentBuilder b = d.B;
        bool hasPaint = Background is not null || data.Top.Width > 0 || data.Right.Width > 0 || data.Bottom.Width > 0 || data.Left.Width > 0;
        bool faded = Opacity < 1 && hasPaint;
        if (faded)
        {
            b.Save();
            b.SetOpacity(Math.Max(0, Opacity));
        }

        double x = data.BoxX;
        double y = data.BoxY;
        double w = data.BoxW;
        double h = data.BoxH;
        double radius = Math.Max(0, Math.Min(CornerRadius, Math.Min(w, h) / 2));
        double rTop = data.First ? radius : 0;
        double rBottom = data.Last ? radius : 0;

        if (Background is { } bg)
        {
            b.SetFillRgb(bg.R, bg.G, bg.B);
            if (radius > 0)
            {
                RoundedRect(d, x, y, w, h, rTop, rBottom);
                b.Fill();
            }
            else
            {
                d.FillRect(x, y, w, h);
            }
        }

        if (faded)
        {
            // Children paint at full opacity: the fade covers the box's own paint only.
            b.Restore();
            faded = false;
        }

        DrawChildren(d, p);
        DrawBorders(d, data, x, y, w, h, radius, rTop, rBottom);
    }

    private static void DrawBorders(in DrawContext d, BoxData data, double x, double y, double w, double h, double radius, double rTop, double rBottom)
    {
        PdfContentBuilder b = d.B;
        Border t = data.First ? data.Top : default;
        Border bo = data.Last ? data.Bottom : default;
        Border l = data.Left;
        Border r = data.Right;
        if (t.Width <= 0 && bo.Width <= 0 && l.Width <= 0 && r.Width <= 0)
        {
            return;
        }

        bool uniform = t == r && r == bo && bo == l && t.Width > 0;
        if (radius > 0 && uniform)
        {
            double half = t.Width / 2;
            b.Save();
            ApplyStrokeStyle(b, t);
            RoundedRect(d, x + half, y + half, w - t.Width, h - t.Width, Math.Max(0, rTop - half), Math.Max(0, rBottom - half));
            b.Stroke();
            b.Restore();
            return;
        }

        // Top and bottom run the full width; left and right run between them.
        double yFrom = data.First ? y + t.Width : y;
        double yTo = data.Last ? y + h - bo.Width : y + h;
        if (t.Width > 0)
        {
            Side(d, t, 0, x, y, w, h, yFrom, yTo);
        }

        if (bo.Width > 0)
        {
            Side(d, bo, 1, x, y, w, h, yFrom, yTo);
        }

        if (l.Width > 0)
        {
            Side(d, l, 2, x, y, w, h, yFrom, yTo);
        }

        if (r.Width > 0)
        {
            Side(d, r, 3, x, y, w, h, yFrom, yTo);
        }
    }

    // Draws one side of the box rectangle (x, y, w, h). edge: 0 top, 1 bottom, 2 left, 3 right. Solid sides are filled strips inside the
    // box; dashed and dotted sides are stroked along the strip's centre line.
    private static void Side(in DrawContext d, Border side, int edge, double x, double y, double w, double h, double yFrom, double yTo)
    {
        PdfContentBuilder b = d.B;
        double bw = side.Width;
        double half = bw / 2;
        b.Save();
        if (side.Style == BorderStyle.Solid)
        {
            b.SetFillRgb(side.Color.R, side.Color.G, side.Color.B);
            switch (edge)
            {
                case 0: d.FillRect(x, y, w, bw); break;
                case 1: d.FillRect(x, y + h - bw, w, bw); break;
                case 2: d.FillRect(x, yFrom, bw, yTo - yFrom); break;
                default: d.FillRect(x + w - bw, yFrom, bw, yTo - yFrom); break;
            }
        }
        else
        {
            ApplyStrokeStyle(b, side);
            switch (edge)
            {
                case 0:
                    b.MoveTo(d.X + x, d.PdfY(y + half));
                    b.LineTo(d.X + x + w, d.PdfY(y + half));
                    break;
                case 1:
                    b.MoveTo(d.X + x, d.PdfY(y + h - half));
                    b.LineTo(d.X + x + w, d.PdfY(y + h - half));
                    break;
                case 2:
                    b.MoveTo(d.X + x + half, d.PdfY(yFrom));
                    b.LineTo(d.X + x + half, d.PdfY(yTo));
                    break;
                default:
                    b.MoveTo(d.X + x + w - half, d.PdfY(yFrom));
                    b.LineTo(d.X + x + w - half, d.PdfY(yTo));
                    break;
            }

            b.Stroke();
        }

        b.Restore();
    }

    private static void ApplyStrokeStyle(PdfContentBuilder b, Border side)
    {
        b.SetStrokeRgb(side.Color.R, side.Color.G, side.Color.B);
        b.SetLineWidth(side.Width);
        switch (side.Style)
        {
            case BorderStyle.Dashed:
                b.SetDash([side.Width * 3, side.Width * 3]);
                break;
            case BorderStyle.Dotted:
                b.SetLineCap(PdfLineCap.Round);
                b.SetDash([0, side.Width * 2]);
                break;
        }
    }

    // A rectangle with the top corners rounded by rTop and the bottom corners by rBottom, in the frame's y-down layout space.
    private static void RoundedRect(in DrawContext d, double x, double y, double w, double h, double rTop, double rBottom)
    {
        const double K = 0.5522847498;
        PdfContentBuilder b = d.B;
        double left = d.X + x;
        double right = left + w;
        double top = d.PdfY(y);
        double bottom = top - h;
        b.MoveTo(left + rTop, top);
        b.LineTo(right - rTop, top);
        if (rTop > 0)
        {
            b.CurveTo(right - rTop + (K * rTop), top, right, top - rTop + (K * rTop), right, top - rTop);
        }

        b.LineTo(right, bottom + rBottom);
        if (rBottom > 0)
        {
            b.CurveTo(right, bottom + rBottom - (K * rBottom), right - rBottom + (K * rBottom), bottom, right - rBottom, bottom);
        }

        b.LineTo(left + rBottom, bottom);
        if (rBottom > 0)
        {
            b.CurveTo(left + rBottom - (K * rBottom), bottom, left, bottom + rBottom - (K * rBottom), left, bottom + rBottom);
        }

        b.LineTo(left, top - rTop);
        if (rTop > 0)
        {
            b.CurveTo(left, top - rTop + (K * rTop), left + rTop - (K * rTop), top, left + rTop, top);
        }

        b.ClosePath();
    }
}
