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

    /// <summary>Content direction for this run of pages. When null, <see cref="LayoutDocument.Direction"/>.</summary>
    public LayoutDirection? Direction { get; set; }

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
public sealed partial class LayoutDocument
{
    /// <summary>The runs of pages, in order. Each contributes at least one page.</summary>
    public List<LayoutPage> Pages { get; } = [];

    /// <summary>Layout switches.</summary>
    public LayoutOptions Options { get; } = new();

    /// <summary>
    /// The content direction of every section that does not set its own. <see cref="LayoutDirection.RightToLeft"/> mirrors rows, table columns, list
    /// markers, start and end alignment, insets and borders, and gives paragraphs a right-to-left base direction. Default left to right.
    /// </summary>
    public LayoutDirection Direction { get; set; }

    /// <summary>
    /// The document title: the information dictionary's <c>Title</c>, and (in <see cref="Save(Stream, PdfDocumentWriterOptions?)"/>) <c>DisplayDocTitle</c>, so viewers show it instead of
    /// the file name. Tagged documents should have one. Null writes none.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>The information dictionary's <c>Author</c> (and, in a PDF/A or PDF/UA save, the XMP <c>dc:creator</c>). Null writes none.</summary>
    public string? Author { get; set; }

    /// <summary>The information dictionary's <c>Subject</c> (in a PDF/A or PDF/UA save, the XMP <c>dc:description</c>). Null writes none.</summary>
    public string? Subject { get; set; }

    /// <summary>The information dictionary's <c>Keywords</c> (in a PDF/A or PDF/UA save, the XMP <c>pdf:Keywords</c>). Null writes none.</summary>
    public string? Keywords { get; set; }

    /// <summary>
    /// The language of the document, a BCP 47 tag such as <c>en-US</c>: the catalog's <c>/Lang</c> when written by <see cref="Save(Stream, PdfDocumentWriterOptions?)"/> (set it in the
    /// writer's options when you render into your own writer) and the language every structure element inherits. Elements override it with
    /// <see cref="Element.Language"/>. Null for none.
    /// </summary>
    public string? Language { get; set; }

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
        if (Title is not null)
        {
            writer.SetInfo("Title", Title);
        }

        if (Author is not null)
        {
            writer.SetInfo("Author", Author);
        }

        if (Subject is not null)
        {
            writer.SetInfo("Subject", Subject);
        }

        if (Keywords is not null)
        {
            writer.SetInfo("Keywords", Keywords);
        }

        return RenderCore(new WriterTarget(writer), null, null, false, PlanToc());
    }

    /// <summary>Lays the document out through a streaming writer and finishes the file on <paramref name="output"/>.</summary>
    /// <param name="output">Where the file goes. It is closed unless <see cref="PdfDocumentWriterOptions.LeaveOpen"/> is set.</param>
    /// <param name="options">Writer options; set a fixed <see cref="PdfDocumentWriterOptions.DocumentId"/> for reproducible bytes.</param>
    public void Save(Stream output, PdfDocumentWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        options ??= new PdfDocumentWriterOptions();
        if ((Language is not null && options.Language is null) || (Title is not null && !options.DisplayDocumentTitle))
        {
            options = new PdfDocumentWriterOptions
            {
                CompressStreams = options.CompressStreams,
                Version = options.Version,
                XrefFormat = options.XrefFormat,
                DocumentId = options.DocumentId,
                Language = options.Language ?? Language,
                DisplayDocumentTitle = options.DisplayDocumentTitle || Title is not null,
                LeaveOpen = options.LeaveOpen,
                Limits = options.Limits,
            };
        }

        using var writer = new PdfDocumentWriter(output, options);
        Render(writer);
        writer.Finish();
    }

    // A document built in memory cannot fill a form after the pages that draw it are done, so the page counts come from a first run into a
    // writer that throws its output away. Links, anchors, bookmarks and page labels need a streaming writer and are left out here.
    internal int RenderCore(PdfDocument target, Action<int, LayoutPage, Placed?, Placed?, Placed?>? capture)
    {
        TocState? toc = PlanToc();
        var counts = new int[Pages.Count];
        using (var counter = new PdfDocumentWriter(Stream.Null, new PdfDocumentWriterOptions { CompressStreams = false, LeaveOpen = true }))
        {
            RenderCore(new WriterTarget(counter), null, counts, countOnly: true, toc);
        }

        return RenderCore(new DocumentTarget(target, counts), capture, null, false, toc);
    }

    // A table of contents shows page numbers that depend on its own length. Pages are laid out without drawing until the entries a run finds
    // are the entries the table drew in it; the run that writes the file then draws the settled table. Documents without a table skip all
    // of this.
    private TocState? PlanToc()
    {
        bool wanted = Options.TableOfContents ?? HasTableOfContents();
        if (!wanted)
        {
            return null;
        }

        var state = new TocState();
        for (int run = 0; run < TocState.MaxRuns; run++)
        {
            using var counter = new PdfDocumentWriter(Stream.Null, new PdfDocumentWriterOptions { CompressStreams = false, LeaveOpen = true });
            RenderCore(new WriterTarget(counter), null, null, countOnly: true, state);
            if (TocState.Same(state.Output, state.Input))
            {
                return state;
            }

            state.Advance();
        }

        state.Unsettled = true;
        return state;
    }

    private bool HasTableOfContents()
    {
        foreach (LayoutPage p in Pages)
        {
            if (TocState.Contains(p.Content) || TocState.Contains(p.Header) || TocState.Contains(p.Footer) || TocState.Contains(p.FirstHeader)
                || TocState.Contains(p.FirstFooter) || TocState.Contains(p.EvenHeader) || TocState.Contains(p.EvenFooter)
                || TocState.Contains(p.Background) || TocState.Contains(p.Foreground))
            {
                return true;
            }
        }

        return false;
    }

    // When `capture` is set, each page's placed tree is handed to it as the page is finished.
    internal int RenderCore(ILayoutTarget target, Action<int, LayoutPage, Placed?, Placed?, Placed?>? capture, int[]? sectionCounts = null, bool countOnly = false, TocState? toc = null)
    {
        toc?.Reset();
        using var ctx = new LayoutContext(target, Options, new ParagraphTextEngine(target, Options.Fonts ?? TextFontRegistry.Shared)) { CountOnly = countOnly, Toc = toc };
        _diagnostics = ctx.Diagnostics;
        if (!countOnly)
        {
            ctx.Tag.Start(Language);
        }

        int pageNumber = 0;
        long nextNumber = 1;
        NumberingStyle previousStyle = NumberingStyle.Arabic;
        string previousPrefix = string.Empty;
        for (int s = 0; s < Pages.Count; s++)
        {
            LayoutPage section = Pages[s];
            ctx.Direction = section.Direction ?? Direction;
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
        if (toc is not null && !countOnly)
        {
            if (toc.Unsettled || !TocState.Same(toc.Output, toc.Input))
            {
                ctx.Note("The page numbers of the table of contents did not settle; some may be off by the length of the table.");
            }
        }

        ctx.Tag.Finish();
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
            if (ctx.Toc is { } toc && fit.Placed is Placed found)
            {
                string label = (setup.LabelPrefix ?? string.Empty) + Numbering.Format(startNumber + page, setup.NumberStyle);
                toc.Collect(found, firstPage + page, label, !ctx.CountOnly);
            }

            if (ctx.CountOnly)
            {
                // Only the number of pages matters; elements made for another document could not be drawn here anyway.
                pagesDone++;
                return;
            }

            using TargetPage tp = target.BeginPage(size.Width, size.Height);
            ctx.OpenPage = tp;
            ctx.Values = ctx.Dynamic.Page(fit.Placed as Placed, new PageInfo(firstPage + page, sectionIndex, page, startNumber + page, setup.NumberStyle), Numbering.Format(startNumber + page, setup.NumberStyle));
            if (page == 0 && writeLabel && target.SupportsNavigation)
            {
                target.AddPageLabelRange(firstPage, Numbering.LabelStyle(setup.NumberStyle), setup.LabelPrefix ?? string.Empty, (int)Math.Min(startNumber, int.MaxValue));
            }

            PdfContentBuilder b = tp.Builder;
            var d = new DrawContext(b, ctx, size.Height, 0, 0);
            Placed? background = LayoutFull(ctx, setup.Background, "Background", firstPage + page, size);
            Tagger tag = ctx.Tag;
            if (background is not null)
            {
                bool a = tag.BeginArtifact(b, PdfArtifactType.Background);
                background.Element.Draw(d, background);
                tag.EndArtifact(b, a);
            }

            if (header is not null)
            {
                bool a = tag.BeginArtifact(b, PdfArtifactType.Pagination, PdfArtifactSubtype.Header);
                header.Element.Draw(d.At(m.Left, m.Top), header);
                tag.EndArtifact(b, a);
            }

            Placed? body = fit.Placed as Placed;
            if (body is not null)
            {
                body.Element.Draw(d.At(m.Left, m.Top + (header?.Height ?? 0)), body);
            }

            ctx.Toc?.EndPage();

            if (footer is not null)
            {
                bool a = tag.BeginArtifact(b, PdfArtifactType.Pagination, PdfArtifactSubtype.Footer);
                footer.Element.Draw(d.At(m.Left, size.Height - m.Bottom - footer.Height), footer);
                tag.EndArtifact(b, a);
            }

            Placed? foreground = LayoutFull(ctx, setup.Foreground, "Foreground", firstPage + page, size);
            if (foreground is not null)
            {
                bool a = tag.BeginArtifact(b, PdfArtifactType.Pagination, PdfArtifactSubtype.Watermark);
                foreground.Element.Draw(d, foreground);
                tag.EndArtifact(b, a);
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
        ctx.LayoutArtifactDepth++;
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
            ctx.LayoutArtifactDepth--;
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
        ctx.LayoutArtifactDepth++;
        try
        {
            FlowFit f = element.Layout(ctx, new Constraint(size.Width, size.Height, true), null);
            return f.Placed as Placed;
        }
        finally
        {
            ctx.LayoutArtifactDepth--;
            ctx.Pop();
            ctx.Pop();
        }
    }
}
