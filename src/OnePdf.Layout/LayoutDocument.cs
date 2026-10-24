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

    /// <summary>Header of the first page of this section, instead of <see cref="Header"/>. The ordinary header is used when null; put an empty <see cref="Spacer"/> here for no header.</summary>
    public Element? FirstHeader { get; set; }

    /// <summary>Footer of the first page of this section, instead of <see cref="Footer"/>. The ordinary footer is used when null.</summary>
    public Element? FirstFooter { get; set; }

    /// <summary>Header of pages whose displayed number is even (and that are not the first page of the section), instead of <see cref="Header"/>. The ordinary header is used when null.</summary>
    public Element? EvenHeader { get; set; }

    /// <summary>Footer of pages whose displayed number is even (and that are not the first page of the section), instead of <see cref="Footer"/>. The ordinary footer is used when null.</summary>
    public Element? EvenFooter { get; set; }

    /// <summary>How page numbers of this section are written, in <see cref="PageNumber"/> and in the document's page labels. Default <see cref="NumberingStyle.Arabic"/>.</summary>
    public NumberingStyle NumberStyle { get; set; }

    /// <summary>
    /// The number of the section's first page. When null the numbering continues from the section before (1 for the first section). A value
    /// below 1 counts as 1.
    /// </summary>
    public int? StartNumber { get; set; }

    /// <summary>Text put before the page number in the document's page labels, such as "A-" (not shown by <see cref="PageNumber"/>). Default none.</summary>
    public string? LabelPrefix { get; set; }
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

    /// <summary>Notes recorded by the last <see cref="Render(PdfDocument)"/>: adjustments and rules that could not be honoured.</summary>
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

    /// <summary>
    /// Lays the document out and writes each page to <paramref name="writer"/> as soon as it is laid out, so memory does not grow with the
    /// page count beyond the writer's own bookkeeping. The writer is not finished: call <see cref="PdfDocumentWriter.Finish"/> afterwards.
    /// </summary>
    /// <returns>The number of pages written.</returns>
    /// <exception cref="PdfLayoutException">Something cannot be placed even on an empty page.</exception>
    public int Render(PdfDocumentWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        return RenderCore(new WriterTarget(writer), null);
    }

    /// <summary>Lays the document out through a streaming writer and finishes the file on <paramref name="output"/>.</summary>
    /// <param name="output">Where the file goes. It is closed unless <see cref="PdfDocumentWriterOptions.LeaveOpen"/> is set.</param>
    /// <param name="options">Writer options; set a fixed <see cref="PdfDocumentWriterOptions.DocumentId"/> for reproducible bytes.</param>
    public void Save(Stream output, PdfDocumentWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        using var writer = new PdfDocumentWriter(output, options);
        Render(writer);
        writer.Finish();
    }

    // A document built in memory cannot fill a form after the pages that draw it are done, so the page counts come from a first run into a
    // writer that throws its output away. Links, anchors, bookmarks and page labels need a streaming writer and are left out here.
    internal int RenderCore(PdfDocument target, Action<int, LayoutPage, Placed?, Placed?, Placed?>? capture)
    {
        var counts = new int[Pages.Count];
        using (var counter = new PdfDocumentWriter(Stream.Null, new PdfDocumentWriterOptions { CompressStreams = false, LeaveOpen = true }))
        {
            RenderCore(new WriterTarget(counter), null, counts, countOnly: true);
        }

        return RenderCore(new DocumentTarget(target, counts), capture, null);
    }

    // When `capture` is set, each page's placed tree is handed to it as the page is finished.
    internal int RenderCore(ILayoutTarget target, Action<int, LayoutPage, Placed?, Placed?, Placed?>? capture, int[]? sectionCounts = null, bool countOnly = false)
    {
        using var ctx = new LayoutContext(target, Options, new ParagraphTextEngine(target, Options.Fonts ?? TextFontRegistry.Shared)) { CountOnly = countOnly };
        _diagnostics = ctx.Diagnostics;
        int pageNumber = 0;
        long nextNumber = 1;
        NumberingStyle previousStyle = NumberingStyle.Arabic;
        string previousPrefix = string.Empty;
        for (int s = 0; s < Pages.Count; s++)
        {
            LayoutPage section = Pages[s];
            ctx.Direction = section.Direction;
            long start = section.StartNumber is { } sn ? Math.Max(1, sn) : nextNumber;
            string prefix = section.LabelPrefix ?? string.Empty;

            // A page label range (ISO 32000-2 12.4.2) begins where the numbering does not simply carry on from the page before.
            bool label = s == 0
                ? section.NumberStyle != NumberingStyle.Arabic || prefix.Length > 0 || start != 1
                : start != nextNumber || section.NumberStyle != previousStyle || prefix != previousPrefix;
            int count = RenderSection(ctx, target, section, s, pageNumber, start, label, capture);
            target.SetTotal(s, count);
            if (sectionCounts is not null)
            {
                sectionCounts[s] = count;
            }

            pageNumber += count;
            nextNumber = start + count;
            previousStyle = section.NumberStyle;
            previousPrefix = prefix;
        }

        target.SetTotal(-1, pageNumber);
        ctx.CheckLinks();
        return pageNumber;
    }

    private int RenderSection(LayoutContext ctx, ILayoutTarget target, LayoutPage setup, int sectionIndex, int firstPage, long startNumber, bool writeLabel, Action<int, LayoutPage, Placed?, Placed?, Placed?>? capture)
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
            long number = startNumber + page;
            ctx.Page = new PageInfo(firstPage + page, sectionIndex, page, number, setup.NumberStyle);
            bool first = page == 0;
            bool even = number % 2 == 0;
            Element? headerElement = first && setup.FirstHeader is not null ? setup.FirstHeader : even && setup.EvenHeader is not null ? setup.EvenHeader : setup.Header;
            Element? footerElement = first && setup.FirstFooter is not null ? setup.FirstFooter : even && setup.EvenFooter is not null ? setup.EvenFooter : setup.Footer;
            header = LayoutBand(ctx, headerElement, "Header", firstPage + page, bodyW, bodyH);
            footer = LayoutBand(ctx, footerElement, "Footer", firstPage + page, bodyW, bodyH - (header?.Height ?? 0));
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
            if (ctx.CountOnly)
            {
                // Only the number of pages matters; elements made for another document could not be drawn here anyway.
                pagesDone++;
                return;
            }

            using TargetPage tp = target.BeginPage(size.Width, size.Height);
            ctx.OpenPage = tp;
            if (page == 0 && writeLabel && target.SupportsNavigation)
            {
                target.AddPageLabelRange(firstPage, Numbering.LabelStyle(setup.NumberStyle), setup.LabelPrefix ?? string.Empty, (int)Math.Min(startNumber, int.MaxValue));
            }

            PdfContentBuilder b = tp.Builder;
            var d = new DrawContext(b, ctx, size.Height, 0, 0);
            Placed? background = LayoutFull(ctx, setup.Background, "Background", firstPage + page, size);
            if (background is not null)
            {
                b.BeginArtifact(PdfArtifactType.Background);
                background.Element.Draw(d, background);
                b.EndMarkedContent();
            }

            if (header is not null)
            {
                b.BeginArtifact(PdfArtifactType.Pagination, PdfArtifactSubtype.Header);
                header.Element.Draw(d.At(m.Left, m.Top), header);
                b.EndMarkedContent();
            }

            Placed? body = fit.Placed as Placed;
            if (body is not null)
            {
                body.Element.Draw(d.At(m.Left, m.Top + (header?.Height ?? 0)), body);
            }

            if (footer is not null)
            {
                b.BeginArtifact(PdfArtifactType.Pagination, PdfArtifactSubtype.Footer);
                footer.Element.Draw(d.At(m.Left, size.Height - m.Bottom - footer.Height), footer);
                b.EndMarkedContent();
            }

            Placed? foreground = LayoutFull(ctx, setup.Foreground, "Foreground", firstPage + page, size);
            if (foreground is not null)
            {
                b.BeginArtifact(PdfArtifactType.Pagination, PdfArtifactSubtype.Watermark);
                foreground.Element.Draw(d, foreground);
                b.EndMarkedContent();
            }

            ctx.OpenPage = null;
            tp.Commit();
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

    private static Placed? LayoutBand(LayoutContext ctx, Element? element, string slot, int page, double width, double maxHeight)
    {
        if (element is null)
        {
            return null;
        }

        ctx.Push($"Page[{page}]");
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

    private static Placed? LayoutFull(LayoutContext ctx, Element? element, string slot, int page, PageSize size)
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
