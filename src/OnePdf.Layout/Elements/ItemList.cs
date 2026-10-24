using System.Globalization;
using OnePdf.Layout.Pagination;

namespace OnePdf.Layout;

/// <summary>One entry of an <see cref="ItemList"/>.</summary>
public sealed class ListItem
{
    /// <summary>Creates an entry holding <paramref name="content"/>. Put a nested <see cref="ItemList"/> in a <see cref="Column"/> after the entry's own text to nest.</summary>
    /// <exception cref="ArgumentNullException">The content is null.</exception>
    public ListItem(Element content) => Content = content ?? throw new ArgumentNullException(nameof(content));

    /// <summary>What the entry holds.</summary>
    public Element Content { get; }

    /// <summary>Text for this entry's marker, instead of the list's. Used as is: no suffix is added.</summary>
    public string? MarkerText { get; set; }

    /// <summary>An element for this entry's marker (a small image, say), instead of any text. It is given the marker column's width.</summary>
    public Element? Marker { get; set; }
}

/// <summary>Whether an <see cref="ItemList"/> marks its entries with a symbol or with numbers.</summary>
public enum ListKind
{
    /// <summary>A bullet symbol before each entry.</summary>
    Bulleted = 0,

    /// <summary>A number before each entry, in <see cref="ItemList.NumberStyle"/>.</summary>
    Numbered,
}

/// <summary>
/// Entries with markers (bullets or numbers) hanging in a column of their own. All entries share one marker column as wide as the widest
/// marker, so the text of every entry starts at the same place; markers are aligned to the end of that column, so "9." and "10." line
/// up on the period. An entry that continues on the next page repeats no marker.
/// </summary>
/// <remarks>
/// Nest lists by putting an <see cref="ItemList"/> inside the content of an entry (usually a <see cref="Column"/> with the entry's text
/// first). Default bullets change with depth: a bullet, a dash, then they repeat. In right-to-left content the marker column is on the right.
/// </remarks>
public sealed class ItemList : Element
{
    /// <summary>The entries, first at the top.</summary>
    public List<ListItem> Items { get; } = [];

    /// <summary>Bulleted or numbered. Default <see cref="ListKind.Bulleted"/>.</summary>
    public ListKind Kind { get; set; }

    /// <summary>For numbered lists, how numbers are written. Default <see cref="NumberingStyle.Arabic"/>.</summary>
    public NumberingStyle NumberStyle { get; set; }

    /// <summary>For numbered lists, the number of the first entry. Default 1.</summary>
    public int Start { get; set; } = 1;

    /// <summary>The symbol of a bulleted list. When null it depends on the depth of the list: "•", then "–", then "•" again, and so on.</summary>
    public string? Bullet { get; set; }

    /// <summary>Text put after the number of a numbered list. Default ".".</summary>
    public string Suffix { get; set; } = ".";

    /// <summary>
    /// Writes the marker for an entry from its number (<see cref="Start"/> plus its position), replacing the bullet or number and suffix.
    /// An entry's own <see cref="ListItem.MarkerText"/> still wins.
    /// </summary>
    public Func<int, string>? MarkerFormat { get; set; }

    /// <summary>The font family of the markers, as for <see cref="Text.FontName"/>. Null, the default, is the text default.</summary>
    public string? FontName { get; set; }

    /// <summary>Weight of the marker font. Default 400.</summary>
    public int Weight { get; set; } = 400;

    /// <summary>Marker size in points; use the size of the entries' text. Default 11.</summary>
    public double Size { get; set; } = 11;

    /// <summary>Marker colour. Default black.</summary>
    public LayoutColor Color { get; set; }

    /// <summary>Line distance of the marker as a multiple of its size; match the entries' text. Default 1.2.</summary>
    public double LineSpacing { get; set; } = 1.2;

    /// <summary>The narrowest the marker column may be, in points, before the gap. Default 0.</summary>
    public double MarkerWidth { get; set; }

    /// <summary>Space between the marker column and the entries' content, in points. Default 6.</summary>
    public double MarkerGap { get; set; } = 6;

    /// <summary>Space between neighbouring entries on one page, in points. Default 0. Not added at the top of a page.</summary>
    public double Spacing { get; set; }

    private sealed record ListResume(FlowCursor Cursor, double MarkerColumn);

    // The element a placed entry stands for: a marker and its content.
    private sealed class EntryElement : Element
    {
        internal override string TypeName => "ListItem";

        internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume) => throw new InvalidOperationException();

        // An entry is an LI holding a Lbl (the marker, on the first piece only) and an LBody (ISO 32000-2 14.8.4.3.3).
        internal override void DrawCore(in DrawContext d, Placed p)
        {
            Tagger tag = d.Ctx.Tag;
            TagScope item = tag.Open(this, p.Continued, p.Continues, "LI");
            int next = 0;
            if (p.Data is true && p.Children.Count > 0)
            {
                TagScope label = tag.Open(LabelKey, false, false, "Lbl");
                Placed marker = p.Children[0];
                marker.Element.Draw(d.At(marker.X, marker.Y), marker);
                tag.Close(label);
                next = 1;
            }

            if (next < p.Children.Count)
            {
                TagScope body = tag.Open(BodyKey, p.Continued, p.Continues, "LBody");
                for (int i = next; i < p.Children.Count; i++)
                {
                    Placed child = p.Children[i];
                    child.Element.Draw(d.At(child.X, child.Y), child);
                }

                tag.Close(body);
            }

            tag.Close(item);
        }
    }

    private static readonly object LabelKey = new();

    private static readonly object BodyKey = new();

    private static readonly EntryElement Entry = new();

    private string MarkerOf(LayoutContext ctx, int index)
    {
        ListItem item = Items[index];
        if (item.MarkerText is { } own)
        {
            return own;
        }

        int number = (int)Math.Clamp((long)Start + index, int.MinValue, int.MaxValue);
        if (MarkerFormat is { } format)
        {
            return format(number) ?? string.Empty;
        }

        if (Kind == ListKind.Numbered)
        {
            return Numbering.Format(number, NumberStyle) + Suffix;
        }

        return Bullet ?? (ctx.ListDepth % 2 == 0 ? "\u2022" : "\u2013");
    }

    private Text MarkerElement(LayoutContext ctx, int index) => new()
    {
        Content = MarkerOf(ctx, index),
        FontName = FontName,
        Weight = Weight,
        Size = Size,
        Color = Color,
        LineSpacing = LineSpacing,
        Alignment = HorizontalAlignment.End,
    };

    private double MeasureMarkers(LayoutContext ctx)
    {
        double widest = Math.Max(0, MarkerWidth);
        for (int i = 0; i < Items.Count; i++)
        {
            ListItem item = Items[i];
            if (item.Marker is { } element)
            {
                ctx.PushItem(i);
                try
                {
                    widest = Math.Max(widest, element.MeasureWidths(ctx).Max);
                }
                finally
                {
                    ctx.Pop();
                }
            }
            else
            {
                widest = Math.Max(widest, ctx.Text.Prepare(MarkerElement(ctx, i), 1e6, ctx).NaturalWidth);
            }
        }

        return widest;
    }

    internal override WidthRange WidthsCore(LayoutContext ctx)
    {
        double marker = MeasureMarkers(ctx) + Math.Max(0, MarkerGap);
        double lo = 0;
        double hi = 0;
        ctx.ListDepth++;
        try
        {
            for (int i = 0; i < Items.Count; i++)
            {
                ctx.PushItem(i);
                try
                {
                    WidthRange w = Items[i].Content.MeasureWidths(ctx);
                    lo = Math.Max(lo, w.Min);
                    hi = Math.Max(hi, w.Max);
                }
                finally
                {
                    ctx.Pop();
                }
            }
        }
        finally
        {
            ctx.ListDepth--;
        }

        return new WidthRange(marker + lo, marker + hi);
    }

    internal override void DrawCore(in DrawContext d, Placed p)
    {
        PdfDictionary? attributes = null;
        if (!p.Continued && d.Ctx.Tag.Enabled)
        {
            attributes = PdfStructureAttributes.List(Kind == ListKind.Bulleted ? "Unordered" : NumberStyle switch
            {
                NumberingStyle.LowerLetters => "LowerAlpha",
                NumberingStyle.UpperLetters => "UpperAlpha",
                NumberingStyle.LowerRoman => "LowerRoman",
                NumberingStyle.UpperRoman => "UpperRoman",
                _ => "Decimal",
            });
        }

        TagScope scope = d.Ctx.Tag.Open(this, p.Continued, p.Continues, "L", null, attributes);
        DrawChildren(d, p);
        d.Ctx.Tag.Close(scope);
    }

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        var r = (ListResume?)resume;
        double markerColumn = r?.MarkerColumn ?? MeasureMarkers(ctx);
        var start = r?.Cursor ?? new FlowCursor(0, null);
        var source = new Source(this, ctx, c, markerColumn);
        FlowPage page = FlowPaginator.Fill(source, Spacing, c.Height, c.AtTop, start, ctx.Rules);
        if (page.NoFit)
        {
            return ctx.NoFit(c, c.Width, page.NoFitRequested);
        }

        double width = c.Width;
        if (!c.WidthBounded)
        {
            width = 0;
            foreach (FlowPlacement fp in page.Placements)
            {
                width = Math.Max(width, ((Placed)fp.Placed!).Width);
            }
        }

        var node = new Placed(this, 0, 0, width, page.Height);
        foreach (FlowPlacement fp in page.Placements)
        {
            var entry = (Placed)fp.Placed!;
            entry.X = 0;
            entry.Y = fp.Y;
            node.Children.Add(entry);
        }

        return page.Complete ? FlowFit.Complete(page.Height, node) : FlowFit.Partial(page.Height, node, new ListResume(page.Next, markerColumn));
    }

    private sealed class Source(ItemList list, LayoutContext ctx, Constraint c, double measuredMarkerColumn) : IFlowSource
    {
        // In a column too narrow for its markers the marker column takes at most half the width and the gap a quarter, so content keeps some room.
        private readonly double markerColumn = c.WidthBounded ? Math.Min(measuredMarkerColumn, c.Width / 2) : measuredMarkerColumn;

        private double Gap => c.WidthBounded ? Math.Min(Math.Max(0, list.MarkerGap), c.Width / 4) : Math.Max(0, list.MarkerGap);

        public int Count => list.Items.Count;

        public bool KeepWithNext(int index) => false;

        public FlowFit Fit(int index, double available, bool atTop, object? resume, FlowRules rules)
        {
            ListItem item = list.Items[index];
            bool rtl = ctx.Direction == LayoutDirection.RightToLeft;
            double indent = markerColumn + Gap;
            double contentWidth = c.WidthBounded ? Math.Max(0, c.Width - indent) : double.PositiveInfinity;
            ctx.PushItem(index);
            try
            {
                FlowFit content;
                ctx.ListDepth++;
                try
                {
                    content = item.Content.Layout(ctx, new Constraint(contentWidth, available, atTop), resume);
                }
                finally
                {
                    ctx.ListDepth--;
                }

                if (content.Kind == FlowKind.NoFit)
                {
                    return content;
                }

                Placed? contentNode = content.Placed as Placed;
                double height = content.Height;
                Placed? marker = null;
                if (resume is null)
                {
                    marker = LayoutMarker(item, index, available, atTop);
                    if (marker is null && !double.IsNaN(markerHeightRequest))
                    {
                        return FlowFit.NoFit(markerHeightRequest);
                    }

                    height = Math.Max(height, marker?.Height ?? 0);
                }

                double width = c.WidthBounded ? c.Width : indent + (contentNode?.Width ?? 0);
                var node = new Placed(Entry, 0, 0, width, height) { Data = marker is not null, Continued = resume is not null, Continues = content.Kind == FlowKind.Partial };
                if (marker is not null)
                {
                    // The marker column holds the marker at its end, so numbers of different lengths line up on their last digit.
                    double free = markerColumn - marker.Width;
                    double mx = rtl ? width - markerColumn + free : free;
                    marker.X = Math.Max(0, mx);
                    marker.Y = 0;
                    node.Children.Add(marker);
                }

                if (contentNode is not null)
                {
                    contentNode.X = rtl ? 0 : indent;
                    contentNode.Y = 0;
                    node.Children.Add(contentNode);
                }

                return content.Kind == FlowKind.Partial ? FlowFit.Partial(height, node, content.Resume) : FlowFit.Complete(height, node);
            }
            finally
            {
                ctx.Pop();
            }
        }

        private double markerHeightRequest = double.NaN;

        private Placed? LayoutMarker(ListItem item, int index, double available, bool atTop)
        {
            markerHeightRequest = double.NaN;
            ctx.Push("Marker");
            try
            {
                Element element = item.Marker ?? list.MarkerElement(ctx, index);
                FlowFit f = element.Layout(ctx, new Constraint(markerColumn, available, atTop), null);
                if (f.Kind == FlowKind.NoFit)
                {
                    markerHeightRequest = f.Requested;
                    return null;
                }

                return f.Placed as Placed;
            }
            finally
            {
                ctx.Pop();
            }
        }
    }
}
