using OnePdf.Layout.Pagination;

namespace OnePdf.Layout;

/// <summary>
/// Scales a child down, uniformly, until it fits the space offered. Content that already fits is laid out and drawn as if the wrapper were
/// not there, and nothing is ever scaled up. Text stays text: the scale is a transform on the page content, so extraction, tagging and links
/// inside the child still work.
/// </summary>
/// <remarks>
/// <para>The child is laid out at its natural size: as wide as the space offered (or its own width when that is wider) and as tall as it needs,
/// without splitting across pages. A child that is too wide or too tall is then laid out again at a smaller scale, in a space wider and taller by
/// the same factor, so text reflows as it would at that scale. The largest scale that fits is found by bisection, at least <see cref="MinScale"/>.</para>
/// <para>In a container that flows across pages, a child that does not fit the rest of a page moves to the next page first when it needs more
/// than the width allows alone; only on an empty page is it scaled to the height. A page break inside the child raises
/// <see cref="PdfLayoutException"/>, as in <see cref="Scale"/>.</para>
/// </remarks>
public sealed class ScaleToFit : SingleChildElement
{
    private const int Steps = 28;

    /// <summary>The smallest scale tried, greater than zero and at most 1. When even this does not fit, layout fails with <see cref="PdfLayoutException"/>. Default 0.05.</summary>
    public double MinScale { get; set; } = 0.05;

    internal override WidthRange WidthsCore(LayoutContext ctx) => UnboundedWidth(ctx);

    internal sealed record Data(double Factor);

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        if (!(MinScale > 0) || MinScale > 1)
        {
            throw new PdfLayoutException(ctx.PathText, c.Width, c.Height, double.NaN, double.NaN, "The minimum scale must be greater than zero and at most 1");
        }

        if (Child is null)
        {
            return FlowFit.Complete(0, null);
        }

        Placed? one = Try(ctx, c, 1);
        if (one is null)
        {
            return FlowFit.Complete(0, null);
        }

        if (Fits(one, 1, c.Width, c.Height))
        {
            return Finish(one, 1);
        }

        // Away from the top of a page only the width is a reason to scale; a child that is too tall for what is left goes to a fresh page first.
        double limitHeight = c.AtTop ? c.Height : double.PositiveInfinity;
        (Placed best, double s)? found = Search(ctx, c, c.Width, limitHeight, one);
        if (found is null)
        {
            // Content that fills whatever width it is given (a picture fitted to the width) never gets smaller by being offered more room, so
            // scale what was laid out at the offered width instead.
            double s1 = Math.Min(1, Math.Min(c.WidthBounded ? c.Width / one.Width : 1, double.IsFinite(limitHeight) ? limitHeight / one.Height : 1));
            if (s1 < MinScale)
            {
                return ctx.NoFit(c, one.Width, one.Height, "The element does not fit even when scaled down to the minimum scale");
            }

            found = (one, s1);
        }

        (Placed placed, double factor) = found.Value;
        if (!c.AtTop && placed.Height * factor > c.Height + 1e-9)
        {
            return FlowFit.NoFit(placed.Height * factor);
        }

        return Finish(placed, factor);
    }

    private (Placed, double)? Search(LayoutContext ctx, in Constraint c, double width, double height, Placed one)
    {
        if (Fits(one, 1, width, height))
        {
            return (one, 1);
        }

        double hi = 1;
        double lo = MinScale;
        Placed? atLo = Try(ctx, c, lo);
        if (atLo is null || !Fits(atLo, lo, width, height))
        {
            return null;
        }

        // The size at scale one is a good first guess when only one direction is too big, but text reflows, so bisect rather than divide.
        for (int i = 0; i < Steps && hi - lo > 1e-6; i++)
        {
            double mid = (lo + hi) / 2;
            Placed? p = Try(ctx, c, mid);
            if (p is not null && Fits(p, mid, width, height))
            {
                lo = mid;
                atLo = p;
            }
            else
            {
                hi = mid;
            }
        }

        return (atLo, lo);
    }

    // The child laid out for scale s: in a space 1/s as wide as offered, with unbounded height.
    private Placed? Try(LayoutContext ctx, in Constraint c, double s)
    {
        double w = c.WidthBounded ? c.Width / s : double.PositiveInfinity;
        FlowFit f = LayoutChild(ctx, new Constraint(w, double.PositiveInfinity, true), null);
        if (f.Kind == FlowKind.Partial)
        {
            throw new PdfLayoutException(ctx.PathText, c.Width, c.Height, double.NaN, double.NaN, "A scaled element cannot contain a page break");
        }

        return f.Placed as Placed;
    }

    private static bool Fits(Placed p, double s, double width, double height) =>
        p.Width * s <= width + 1e-9 && p.Height * s <= height + 1e-9;

    private FlowFit Finish(Placed child, double s)
    {
        double w = child.Width * s;
        double h = child.Height * s;
        var node = new Placed(this, 0, 0, w, h) { Data = new Data(s) };
        child.X = 0;
        child.Y = 0;
        node.Children.Add(child);
        return FlowFit.Complete(h, node);
    }

    internal override void DrawCore(in DrawContext d, Placed p)
    {
        double s = ((Data)p.Data!).Factor;
        Placed child = p.Children[0];
        if (s == 1)
        {
            DrawChildren(d, p);
            return;
        }

        double bottom = d.PdfY(p.Height);
        d.B.Save();
        d.B.Transform(s, 0, 0, s, d.X, bottom);
        child.Element.Draw(d.InScaledFrame(child.Height, s, bottom), child);
        d.B.Restore();
    }
}

/// <summary>
/// Makes the element no larger than its content: where an element would take all the width it is offered (text, a box around text), this one
/// is as wide as the content needs, or as wide as the space offered when the content needs more. The height is the content's own, as it is
/// for every element.
/// </summary>
/// <remarks>
/// The child is asked for its widest unbroken width (<see cref="Element"/> measurement, as table columns do) and laid out in at most that
/// width. A child that splits across pages keeps that width on every page. Put the wrapper outside the box that should hug its content:
/// <c>Shrink().Border(1).Padding(4).Text("Short")</c>.
/// </remarks>
public sealed class Shrink : SingleChildElement
{
    // Text is laid out at exactly its measured width on the second pass; a little room keeps rounding from wrapping its last word.
    private const double Room = 1e-6;

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        if (Child is null)
        {
            return FlowFit.Complete(0, null);
        }

        double widest = Child.MeasureWidths(ctx).Max;
        double width = double.IsFinite(widest) ? Math.Min(c.Width, widest + Room) : c.Width;
        FlowFit f = LayoutChild(ctx, new Constraint(width, c.Height, c.AtTop), resume);
        return Wrap(f, null);
    }
}

/// <summary>How much room an <see cref="Unconstrained"/> element takes in its parent.</summary>
public enum UnconstrainedSpace
{
    /// <summary>None: the parent places its other content as if the element were not there. The child is drawn at the element's origin and overflows freely.</summary>
    None = 0,

    /// <summary>The width offered and the child's own height (at most the height offered): the parent keeps the room the child would normally have had, whatever size the child really takes.</summary>
    Offered,
}

/// <summary>
/// Lays its child out as if the space it was offered were unlimited: the child gets its natural size, may be wider or taller than the parent, and
/// is drawn from the element's top-left corner without being clipped. It never splits across pages.
/// </summary>
/// <remarks>
/// What the parent reserves for the element is <see cref="Space"/>: by default nothing, so siblings are placed and paginated as if the child were
/// absent. A page break inside the child raises <see cref="PdfLayoutException"/>.
/// </remarks>
public sealed class Unconstrained : SingleChildElement
{
    /// <summary>How much room the element takes in its parent. Default <see cref="UnconstrainedSpace.None"/>.</summary>
    public UnconstrainedSpace Space { get; set; }

    internal override WidthRange WidthsCore(LayoutContext ctx) => new(0, 0);

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        if (Child is null)
        {
            return FlowFit.Complete(0, null);
        }

        FlowFit f = LayoutChild(ctx, new Constraint(double.PositiveInfinity, double.PositiveInfinity, true), null);
        if (f.Kind == FlowKind.Partial)
        {
            throw new PdfLayoutException(ctx.PathText, c.Width, c.Height, double.NaN, double.NaN, "An unconstrained element cannot contain a page break");
        }

        if (f.Placed is not Placed child)
        {
            return f;
        }

        double w = 0;
        double h = 0;
        if (Space == UnconstrainedSpace.Offered)
        {
            w = c.WidthBounded ? c.Width : child.Width;
            h = Math.Min(child.Height, c.Height);
        }

        var node = new Placed(this, 0, 0, w, h);
        child.X = 0;
        child.Y = 0;
        node.Children.Add(child);
        return FlowFit.Complete(h, node);
    }
}
