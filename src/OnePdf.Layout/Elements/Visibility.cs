using OnePdf.Layout.Pagination;

namespace OnePdf.Layout;

/// <summary>Where the page being laid out sits in the document, for <see cref="ShowIf.Predicate"/>.</summary>
/// <param name="Index">Zero-based index of the page in the whole document.</param>
/// <param name="Section">Zero-based index of the section (the <see cref="LayoutPage"/>) the page belongs to.</param>
/// <param name="IndexInSection">Zero-based index of the page inside its section.</param>
/// <param name="Number">The displayed page number (it follows <see cref="LayoutPage.StartNumber"/>).</param>
public readonly record struct PagePosition(int Index, int Section, int IndexInSection, long Number)
{
    /// <summary>True for the first page of the document.</summary>
    public bool IsFirstPage => Index == 0;

    /// <summary>True for the first page of its section.</summary>
    public bool IsFirstInSection => IndexInSection == 0;

    /// <summary>True when the displayed page number is even.</summary>
    public bool IsEvenNumber => Number % 2 == 0;
}

/// <summary>
/// Shows its child only the first time the content around it is laid out; on a repeat of that content the child is left out and takes no
/// space. See <see cref="SkipOnce"/> for the opposite.
/// </summary>
/// <remarks>
/// <para>What repeats, and so what "once" means:</para>
/// <list type="bullet">
/// <item>A header, footer, background or foreground of a <see cref="LayoutPage"/> is laid out again on every page of the section. The first page of each section is the first time; later pages of the section are repeats (the next section starts over).</item>
/// <item>The header and footer rows of a <see cref="Table"/> are laid out again on every piece of the table. The piece that starts the table, wherever it lands, is the first time; pieces that continue it are repeats.</item>
/// <item>Anywhere else (flowing content, cells of body rows, a box that splits over pages) nothing is repeated, so the child is simply shown, and a child that itself splits over pages shows all its pieces. Putting it there changes nothing.</item>
/// </list>
/// <para>Hidden content takes no space, draws nothing and writes no structure elements. The decision depends only on where the element is laid out, never on earlier calls, so laying a tree out twice gives the same pages.</para>
/// </remarks>
public sealed class ShowOnce : SingleChildElement
{
    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume) =>
        Child is null || (ctx.Repeated && resume is null) ? FlowFit.Complete(0, null) : Wrap(LayoutChild(ctx, c, resume), null);
}

/// <summary>
/// Hides its child the first time the content around it is laid out and shows it on every repeat of that content: a "continued" note in a
/// repeated header, a running title that the first page does not need. The opposite of <see cref="ShowOnce"/>; read there what repeats.
/// </summary>
/// <remarks>
/// The child takes no space and writes no structure elements while hidden. Where nothing repeats (flowing content, cells of body rows) the
/// child is always hidden for the same reason, except that a child that splits over pages keeps showing its continuation pieces.
/// </remarks>
public sealed class SkipOnce : SingleChildElement
{
    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume) =>
        Child is null || (!ctx.Repeated && resume is null) ? FlowFit.Complete(0, null) : Wrap(LayoutChild(ctx, c, resume), null);
}

/// <summary>
/// Includes its child when a condition holds and leaves it out otherwise. Left out, the child takes no space, draws nothing and writes no
/// structure elements.
/// </summary>
/// <remarks>
/// Set <see cref="Condition"/> for a fixed yes or no, or <see cref="Predicate"/> to decide per page. The predicate is called while the element
/// is laid out, with the position of the page being laid out (<see cref="PagePosition"/>); for a header, footer, background or foreground that is
/// the page the band goes on, and for flowing content the page the element is placed on. It may be called more than once per page, so it
/// should be free of side effects. "Is this the last page" is not available, because the page count is not known while pages are laid out.
/// When both are set, both must hold.
/// </remarks>
public sealed class ShowIf : SingleChildElement
{
    /// <summary>The fixed condition. Default true.</summary>
    public bool Condition { get; set; } = true;

    /// <summary>Decides per page; null for no per-page rule.</summary>
    public Func<PagePosition, bool>? Predicate { get; set; }

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        if (Child is null || (resume is null && !Included(ctx)))
        {
            return FlowFit.Complete(0, null);
        }

        return Wrap(LayoutChild(ctx, c, resume), null);
    }

    private bool Included(LayoutContext ctx)
    {
        if (!Condition)
        {
            return false;
        }

        PageInfo p = ctx.Page;
        return Predicate is null || Predicate(new PagePosition(p.Index, p.Section, p.IndexInSection, p.Number));
    }
}
