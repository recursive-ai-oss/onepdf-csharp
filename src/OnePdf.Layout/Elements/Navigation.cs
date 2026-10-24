using OnePdf.Layout.Pagination;

namespace OnePdf.Layout;

/// <summary>
/// Makes its child a link: a click anywhere on the child's box goes to <see cref="Uri"/> or to the anchor named <see cref="Destination"/>.
/// When the child is split over pages, each piece is linked. The link covers the child's whole box, so a link around a paragraph covers
/// its full width. Links inside a <see cref="Rotate"/> or <see cref="Scale"/> are not recorded (a note says so).
/// </summary>
/// <remarks>
/// A link is data only: nothing is opened or fetched. If both properties are set, <see cref="Uri"/> wins. Set
/// <see cref="Element.KeepWithNext"/> on the outermost wrapper; wrappers that only add navigation pass a keep request from their child on.
/// </remarks>
public sealed class Link : SingleChildElement
{
    /// <summary>An absolute address the link opens, such as <c>https://example.com/</c>. Characters outside ASCII are percent-encoded.</summary>
    public string? Uri { get; set; }

    /// <summary>The <see cref="Anchor.Id"/> of an anchor in this document. It may be defined later in the document than the link.</summary>
    public string? Destination { get; set; }

    internal override bool AltTextIsFigure => false;

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume) => Wrap(LayoutChild(ctx, c, resume), null);

    internal override void DrawCore(in DrawContext d, Placed p)
    {
        // The link is a structure element holding what it covers, and its annotation is tied to it (ISO 32000-2 14.8.4.4.2).
        TagScope scope = d.Ctx.Tag.Open(this, p.Continued, p.Continues, "Link");
        DrawChildren(d, p);
        d.Ctx.AddLink(d, p.Width, p.Height, Uri, Destination, scope.Node?.Element, AltText ?? TextOf(p));
        d.Ctx.Tag.Close(scope);
    }

    // The link's description (its /Contents, which assistive technology reads) is the text it covers (ISO 14289-1 7.18.5).
    private static string? TextOf(Placed root)
    {
        var sb = new System.Text.StringBuilder();
        var stack = new Stack<Placed>();
        stack.Push(root);
        while (stack.Count > 0 && sb.Length < 200)
        {
            Placed p = stack.Pop();
            if (p.Element is Text t)
            {
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }

                if (t.Spans.Count > 0)
                {
                    foreach (TextSpan span in t.Spans)
                    {
                        sb.Append(span.Text);
                    }
                }
                else
                {
                    sb.Append(t.Content);
                }
            }

            for (int i = p.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(p.Children[i]);
            }
        }

        string text = string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length == 0 ? null : text.Length > 200 ? text[..200] : text;
    }
}

/// <summary>
/// Names a place in the document: the top of the child's first piece. <see cref="Link"/> elements go to it by name, from anywhere in the
/// document, before or after it. The name must be unique; a second use is ignored with a note.
/// </summary>
public sealed class Anchor : SingleChildElement
{
    /// <summary>The destination's name, used by <see cref="Link.Destination"/>. Nothing is recorded when null or empty. Not the same as <see cref="Element.Name"/>, which only labels errors.</summary>
    public string? Id { get; set; }

    internal sealed record Data(bool First);

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume) => Wrap(LayoutChild(ctx, c, resume), new Data(resume is null));

    internal override void DrawCore(in DrawContext d, Placed p)
    {
        if (((Data)p.Data!).First && Id is { Length: > 0 })
        {
            d.Ctx.DefineAnchor(d, Id);
        }

        DrawChildren(d, p);
    }
}

/// <summary>
/// An entry of the document outline (the bookmarks panel) going to the top of the child's first piece. Entries appear in document order;
/// <see cref="Level"/> says how deeply an entry is nested under the one before it.
/// </summary>
public sealed class Bookmark : SingleChildElement
{
    /// <summary>The text shown in the outline. Nothing is recorded when null or empty.</summary>
    public string? Title { get; set; }

    /// <summary>Nesting depth: 0 is top level. An entry more than one level below the previous one is lifted to one below it, with a note.</summary>
    public int Level { get; set; }

    internal sealed record Data(bool First);

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume) => Wrap(LayoutChild(ctx, c, resume), new Data(resume is null));

    internal override void DrawCore(in DrawContext d, Placed p)
    {
        if (((Data)p.Data!).First && Title is { Length: > 0 })
        {
            d.Ctx.AddBookmark(d, Title, Level);
        }

        DrawChildren(d, p);
    }
}

/// <summary>
/// A heading: the child stays on the same page as the start of whatever follows it, so a heading never ends a page. The level (1 to 6) is
/// written as the structure type of the document (<c>H1</c> to <c>H6</c>). <see cref="Element.KeepWithNext"/> is on by default.
/// </summary>
public sealed class Heading : SingleChildElement
{
    /// <summary>Creates a heading that keeps with the next element.</summary>
    public Heading() => KeepWithNext = true;

    private static readonly string[] Types = ["H1", "H2", "H3", "H4", "H5", "H6"];

    /// <summary>The heading level, 1 (most important) to 6; it becomes the structure type <c>H1</c> to <c>H6</c> (values outside the range are clamped). Default 1.</summary>
    public int Level { get; set; } = 1;

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume) => Wrap(LayoutChild(ctx, c, resume), null);

    internal override void DrawCore(in DrawContext d, Placed p)
    {
        TagScope scope = d.Ctx.Tag.Open(this, p.Continued, p.Continues, Types[Math.Clamp(Level, 1, 6) - 1]);
        DrawChildren(d, p);
        d.Ctx.Tag.Close(scope);
    }
}
