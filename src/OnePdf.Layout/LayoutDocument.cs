using OnePdf.Layout.Pagination;

namespace OnePdf.Layout;

/// <summary>
/// One run of pages with the same setup: size, margins, the content that flows over as many pages as it needs, and the header, footer,
/// background and foreground that repeat on each of them.
/// </summary>
public sealed class LayoutPage
{
    /// <summary>Page size before <see cref="Orientation"/> is applied. Default A4.</summary>
    public PageSize Size { get; set; } = PageSize.A4;

    /// <summary>Portrait (default) or landscape; the size's sides are swapped when they disagree.</summary>
    public PageOrientation Orientation { get; set; }

    /// <summary>Space between the page edge and the header, content and footer. Default 36 points on every side.</summary>
    public Insets Margin { get; set; } = Insets.All(36);

    /// <summary>Content direction for this run of pages. Default left to right.</summary>
    public LayoutDirection Direction { get; set; }

    /// <summary>Laid out at the top of every page inside the margins. Its height is taken off the content area.</summary>
    public Element? Header { get; set; }

    /// <summary>Laid out at the bottom of every page inside the margins. Its height is taken off the content area.</summary>
    public Element? Footer { get; set; }

    /// <summary>Drawn first on every page, at the full page size (margins ignored).</summary>
    public Element? Background { get; set; }

    /// <summary>Drawn last on every page, at the full page size (margins ignored): watermarks and stamps.</summary>
    public Element? Foreground { get; set; }

    /// <summary>The flowing content, usually a <see cref="Column"/>. Empty when null (one blank page).</summary>
    public Element? Content { get; set; }
}

/// <summary>
/// A document made of the element tree: pages in order, each described by a <see cref="LayoutPage"/>. Laying out writes each finished page
/// to the target document before the next is measured.
/// </summary>
public sealed class LayoutDocument
{
    /// <summary>The runs of pages, in order. Each contributes at least one page.</summary>
    public List<LayoutPage> Pages { get; } = [];

    /// <summary>Layout switches.</summary>
    public LayoutOptions Options { get; } = new();

    /// <summary>Notes recorded by the last <see cref="Render"/>: adjustments and rules that could not be honoured.</summary>
    public IReadOnlyList<LayoutDiagnostic> Diagnostics => _diagnostics;

    private List<LayoutDiagnostic> _diagnostics = [];

    /// <summary>
    /// Lays the document out and appends its pages to <paramref name="target"/>, which must be editable (for example from
    /// <see cref="PdfDocument.CreateEmpty"/>).
    /// </summary>
    /// <returns>The number of pages added.</returns>
    /// <exception cref="PdfLayoutException">Something cannot be placed even on an empty page.</exception>
    public int Render(PdfDocument target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return RenderCore(target, null);
    }

    /// <summary>Lays the document out into a fresh document and saves it to <paramref name="output"/>.</summary>
    /// <param name="output">Where the file goes.</param>
    /// <param name="options">Save options; pass a fixed <see cref="PdfSaveOptions.DocumentId"/> for reproducible bytes.</param>
    public void Save(Stream output, PdfSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        using PdfDocument doc = PdfDocument.CreateEmpty();
        Render(doc);
        doc.Save(output, options);
    }

    // Test hook: when `capture` is set, each page's placed tree is handed to it instead of being dropped after drawing.
    internal int RenderCore(PdfDocument target, Action<int, LayoutPage, Placed?, Placed?, Placed?>? capture)
    {
        using var ctx = new LayoutContext(target, Options, new PlainTextEngine());
        _diagnostics = ctx.Diagnostics;
        int pageNumber = 0;
        for (int s = 0; s < Pages.Count; s++)
        {
            LayoutPage section = Pages[s];
            ctx.Direction = section.Direction;
            pageNumber += RenderSection(ctx, target, section, s, pageNumber, capture);
        }

        return pageNumber;
    }

    private int RenderSection(LayoutContext ctx, PdfDocument target, LayoutPage setup, int sectionIndex, int firstPage, Action<int, LayoutPage, Placed?, Placed?, Placed?>? capture)
    {
        PageSize size = setup.Size.Oriented(setup.Orientation);
        Insets m = setup.Margin;
        double bodyW = size.Width - m.Left - m.Right;
        double bodyH = size.Height - m.Top - m.Bottom;
        if (!(bodyW > 0) || !(bodyH > 0))
        {
            throw new PdfLayoutException($"Page[{sectionIndex}]", size.Width, size.Height, m.Left + m.Right, m.Top + m.Bottom, "The margins leave no room on the page");
        }

        Element content = setup.Content ?? new Spacer();
        int pagesDone = 0;

        // Header and footer are laid out per page, so page-dependent content can be added later; their heights are needed first.
        Placed? header = null;
        Placed? footer = null;
        double HeaderFooterHeight(int page)
        {
            header = LayoutBand(ctx, setup.Header, "Header", sectionIndex, bodyW, bodyH);
            footer = LayoutBand(ctx, setup.Footer, "Footer", sectionIndex, bodyW, bodyH - (header?.Height ?? 0));
            return bodyH - (header?.Height ?? 0) - (footer?.Height ?? 0);
        }

        FlowFit Body(double available, bool atTop, object? resume, FlowRules rules)
        {
            ctx.Push($"Page[{firstPage + pagesDone}]");
            ctx.Push("Content");
            try
            {
                return content.Layout(ctx, new Constraint(bodyW, available, atTop), resume);
            }
            finally
            {
                ctx.Pop();
                ctx.Pop();
            }
        }

        void OnPage(int page, FlowFit fit)
        {
            PdfPage pdfPage = target.InsertBlankPage(target.Pages.Count, size.Width, size.Height);
            using PdfPageContentBuilder b = pdfPage.BeginContent();
            var d = new DrawContext(b, ctx, size.Height, 0, 0);
            Placed? background = LayoutFull(ctx, setup.Background, "Background", sectionIndex, firstPage + page, size);
            if (background is not null)
            {
                background.Element.Draw(d, background);
            }

            if (header is not null)
            {
                header.Element.Draw(d.At(m.Left, m.Top), header);
            }

            Placed? body = fit.Placed as Placed;
            if (body is not null)
            {
                body.Element.Draw(d.At(m.Left, m.Top + (header?.Height ?? 0)), body);
            }

            if (footer is not null)
            {
                footer.Element.Draw(d.At(m.Left, size.Height - m.Bottom - footer.Height), footer);
            }

            Placed? foreground = LayoutFull(ctx, setup.Foreground, "Foreground", sectionIndex, firstPage + page, size);
            if (foreground is not null)
            {
                foreground.Element.Draw(d, foreground);
            }

            b.Commit();
            capture?.Invoke(firstPage + page, setup, body, header, footer);
            pagesDone++;
        }

        int count = PageSequencer.Run(
            Body,
            ctx.Rules,
            Options.MaxPages,
            HeaderFooterHeight,
            OnPage,
            msg => new PdfLayoutException($"Page[{firstPage + pagesDone}]", bodyW, bodyH, double.NaN, double.NaN, msg));
        return count;
    }

    private static Placed? LayoutBand(LayoutContext ctx, Element? element, string slot, int section, double width, double maxHeight)
    {
        if (element is null)
        {
            return null;
        }

        ctx.Push($"Page[{section}]");
        ctx.Push(slot);
        try
        {
            FlowFit f = element.Layout(ctx, new Constraint(width, Math.Max(0, maxHeight), true), null);
            if (f.Kind == FlowKind.Partial)
            {
                throw new PdfLayoutException(ctx.PathText, width, maxHeight, double.NaN, double.NaN, $"The {slot.ToLowerInvariant()} is taller than the page");
            }

            return f.Placed as Placed;
        }
        finally
        {
            ctx.Pop();
            ctx.Pop();
        }
    }

    private static Placed? LayoutFull(LayoutContext ctx, Element? element, string slot, int section, int page, PageSize size)
    {
        if (element is null)
        {
            return null;
        }

        ctx.Push($"Page[{page}]");
        ctx.Push(slot);
        try
        {
            FlowFit f = element.Layout(ctx, new Constraint(size.Width, size.Height, true), null);
            return f.Placed as Placed;
        }
        finally
        {
            ctx.Pop();
            ctx.Pop();
        }
    }
}
