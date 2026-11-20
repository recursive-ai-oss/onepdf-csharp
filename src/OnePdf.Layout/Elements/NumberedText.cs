using OnePdf.Layout.Pagination;

namespace OnePdf.Layout;

/// <summary>
/// Text that holds page numbers. When it fits on one line of the space offered it is laid out as <see cref="Compact"/>, a row of its pieces
/// in which each number is exactly as wide as its digits; otherwise it is <see cref="Flow"/>, a paragraph in which each number is an object
/// of reserved width (<see cref="PageNumberSlot"/>), so the text breaks, justifies and splits across pages like any other.
/// </summary>
internal sealed class NumberedText : Element
{
    /// <summary>The paragraph form, which can always be used.</summary>
    public required Text Flow { get; init; }

    /// <summary>The one-line form, or null when the text has a line break or an inline object.</summary>
    public Element? Compact { get; init; }

    internal override void PushChildren(Stack<Element> into)
    {
        into.Push(Flow);
        if (Compact is not null)
        {
            into.Push(Compact);
        }
    }

    // Widths for table columns: the one-line form's width is what the text always measured, and the paragraph can be narrower.
    internal override WidthRange WidthsCore(LayoutContext ctx)
    {
        WidthRange flow = Flow.MeasureWidths(ctx);
        if (Compact is null)
        {
            return flow;
        }

        WidthRange one = Compact.MeasureWidths(ctx);
        return new WidthRange(Math.Min(flow.Min, one.Max), one.Max);
    }

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        // A continuation is always the paragraph's; a fresh start takes the one-line form when it fits.
        Element chosen = resume is null && Compact is not null && (!c.WidthBounded || Compact.MeasureWidths(ctx).Max <= c.Width + 1e-6) ? Compact : Flow;
        FlowFit f = chosen.Layout(ctx, c, resume);
        if (f.Placed is not Placed child)
        {
            return f;
        }

        var node = new Placed(this, 0, 0, child.Width, child.Height);
        child.X = 0;
        child.Y = 0;
        node.Children.Add(child);
        return f.Kind == FlowKind.Partial ? FlowFit.Partial(f.Height, node, f.Resume) : FlowFit.Complete(f.Height, node);
    }
}

/// <summary>
/// A page number inside wrapping text, as an inline object. It is laid out once with the paragraph, as a box as wide as the digits the
/// number reserves; the number is laid out and drawn when the box is, on the page it lands on.
/// </summary>
internal sealed class PageNumberSlot : Element
{
    public required PageNumber Number { get; init; }

    internal override WidthRange WidthsCore(LayoutContext ctx)
    {
        double w = Number.ReservedBox(ctx).Width;
        return new WidthRange(w, w);
    }

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        Placed reserved = Number.ReservedBox(ctx);
        var node = new Placed(this, 0, 0, reserved.Width, reserved.Height);

        // The box of zeros stands in for the line the number is drawn on, so the paragraph finds the baseline to put the object on.
        node.Children.Add(reserved);
        return FlowFit.Complete(reserved.Height, node);
    }

    internal override void DrawCore(in DrawContext d, Placed p)
    {
        // Laid out now, not with the paragraph: the page and the section are the ones being drawn.
        LayoutContext ctx = d.Ctx;
        FlowFit f = Number.Layout(ctx, new Constraint(p.Width, double.PositiveInfinity, true), null);
        if (f.Placed is not Placed number)
        {
            return;
        }

        // More digits than reserved: squeeze the number into its room, keeping its left edge, as a total is squeezed in its form.
        if (number.Element is Text text)
        {
            double natural = ctx.Text.Prepare(text, 1e6, ctx).NaturalWidth;
            if (natural > p.Width + 1e-6 && natural > 0)
            {
                ctx.NoteOnce(this, "A page number has more digits than the room reserved for it in running text (ReservedDigits); it is drawn condensed.");
                FlowFit wide = Number.Layout(ctx, new Constraint(natural + 0.01, double.PositiveInfinity, true), null);
                if (wide.Placed is Placed full)
                {
                    double s = p.Width / natural;
                    d.B.Save();
                    d.B.Transform(s, 0, 0, 1, d.X * (1 - s), 0);
                    full.Element.Draw(d, full);
                    d.B.Restore();
                    return;
                }
            }
        }

        number.Element.Draw(d, number);
    }
}
