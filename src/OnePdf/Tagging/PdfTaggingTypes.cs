using OnePdf.Text;

namespace OnePdf;

/// <summary>
/// The structure types a caller can give to content (ISO 32000-2 14.8.4, the standard types of ISO 32000-1 Tables 333 to 340 that
/// make sense for content one assigns by hand). The member names are the PDF names, so <c>Enum.TryParse</c> maps a role written as
/// text. The document root is not a choice: OnePDF writes one <c>Document</c> element around everything.
/// </summary>
public enum PdfTagRole
{
    /// <summary>A paragraph.</summary>
    P,

    /// <summary>A heading without a level. PDF/UA-1 does not allow it next to numbered headings, and PDF/UA-2 does not allow it at all.</summary>
    H,

    /// <summary>A first-level heading.</summary>
    H1,

    /// <summary>A second-level heading.</summary>
    H2,

    /// <summary>A third-level heading.</summary>
    H3,

    /// <summary>A fourth-level heading.</summary>
    H4,

    /// <summary>A fifth-level heading.</summary>
    H5,

    /// <summary>A sixth-level heading.</summary>
    H6,

    /// <summary>A list. Contains <see cref="LI"/> (and a <see cref="Caption"/>).</summary>
    L,

    /// <summary>A list item. Contains <see cref="Lbl"/> and <see cref="LBody"/>.</summary>
    LI,

    /// <summary>The label of a list item (a bullet or a number).</summary>
    Lbl,

    /// <summary>The body of a list item.</summary>
    LBody,

    /// <summary>A table. Contains rows (<see cref="TR"/>), optionally grouped in <see cref="THead"/>, <see cref="TBody"/> and <see cref="TFoot"/>.</summary>
    Table,

    /// <summary>The header rows of a table.</summary>
    THead,

    /// <summary>The body rows of a table.</summary>
    TBody,

    /// <summary>The footer rows of a table.</summary>
    TFoot,

    /// <summary>A table row. Contains <see cref="TH"/> and <see cref="TD"/> cells.</summary>
    TR,

    /// <summary>A header cell. <see cref="PdfTagElement.Scope"/> says what it heads.</summary>
    TH,

    /// <summary>A data cell.</summary>
    TD,

    /// <summary>A caption of a table, list or figure.</summary>
    Caption,

    /// <summary>A figure: a picture, chart or drawing. It needs <see cref="PdfTagElement.Alt"/> for PDF/UA.</summary>
    Figure,

    /// <summary>A formula. It needs <see cref="PdfTagElement.Alt"/> or <see cref="PdfTagElement.ActualText"/> for PDF/UA.</summary>
    Formula,

    /// <summary>A link. Give it the text it covers and the link annotation.</summary>
    Link,

    /// <summary>An inline run of text.</summary>
    Span,

    /// <summary>A generic block-level group.</summary>
    Div,

    /// <summary>A part of a document.</summary>
    Part,

    /// <summary>An article.</summary>
    Art,

    /// <summary>A section.</summary>
    Sect,

    /// <summary>A block quotation.</summary>
    BlockQuote,

    /// <summary>An inline quotation.</summary>
    Quote,

    /// <summary>A note, such as a footnote.</summary>
    Note,

    /// <summary>A reference to other text.</summary>
    Reference,

    /// <summary>Program code.</summary>
    Code,

    /// <summary>A form field (it holds the widget annotation).</summary>
    Form,

    /// <summary>A table of contents.</summary>
    TOC,

    /// <summary>An item of a table of contents.</summary>
    TOCI,

    /// <summary>
    /// An annotation other than a link or a form widget (a sticky note, a highlight, a stamp). Give it the annotation through
    /// <see cref="PdfTagContent.Annotation(PdfAnnotation)"/>: it is tied to the element with an object reference and a <c>/StructParent</c>
    /// (ISO 32000-2 14.8.4.5, ISO 14289-1 7.18.1). The annotation needs a <c>/Contents</c> entry for PDF/UA.
    /// </summary>
    Annot,
}

/// <summary>What a header cell is a header of (<c>/Scope</c> of the table attributes, ISO 32000-2 14.8.5.7).</summary>
public enum PdfTagScope
{
    /// <summary>The cells in its row.</summary>
    Row,

    /// <summary>The cells in its column.</summary>
    Column,

    /// <summary>Both its row and its column.</summary>
    Both,
}

/// <summary>What kind of artifact content is (ISO 32000-2 14.8.2.2.2).</summary>
public enum PdfArtifactKind
{
    /// <summary>An artifact without a type.</summary>
    Plain,

    /// <summary>A running head (type Pagination, subtype Header).</summary>
    Header,

    /// <summary>A running foot or page number (type Pagination, subtype Footer).</summary>
    Footer,

    /// <summary>A watermark (type Pagination, subtype Watermark).</summary>
    Watermark,

    /// <summary>Layout decoration such as rules and backgrounds (type Layout).</summary>
    Layout,

    /// <summary>Page-level material such as a trim mark (type Page).</summary>
    Page,
}

/// <summary>
/// A piece of the page that a structure element is made of, named in terms of what the text extraction returned (or an area or an
/// annotation). Make one with the static methods and give it to <see cref="PdfTagElement.Add(PdfTagContent)"/>.
/// </summary>
/// <remarks>
/// The text references need the page extracted with <see cref="PdfTextOptions.RetainGlyphs"/> true, from the same document that is
/// tagged, and the document unchanged since. Each glyph, each painted mark and each annotation can belong to one reference only.
/// </remarks>
public sealed class PdfTagContent
{
    internal enum Kinds
    {
        Block,
        Lines,
        Words,
        Graphics,
        Annotation,
    }

    internal PdfTagContent(Kinds kind)
    {
        Kind = kind;
    }

    internal Kinds Kind { get; }

    internal PdfPageText? Page { get; init; }

    internal PdfTextBlock? SourceBlock { get; init; }

    internal PdfTextLine? Line { get; init; }

    internal int First { get; init; }

    internal int Count { get; init; }

    internal int PageIndex { get; init; } = -1;

    internal PdfRectangle Area { get; init; }

    internal PdfAnnotation? SourceAnnotation { get; init; }

    /// <summary>All the glyphs of a block of the extraction.</summary>
    /// <param name="page">The page the block is from.</param>
    /// <param name="block">A block of <paramref name="page"/>.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static PdfTagContent Block(PdfPageText page, PdfTextBlock block)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(block);
        return new PdfTagContent(Kinds.Block) { Page = page, SourceBlock = block, PageIndex = page.PageIndex };
    }

    /// <summary>All the glyphs of a block of the extraction, named by its index in <see cref="PdfPageText.Blocks"/>.</summary>
    /// <param name="page">The page the block is from.</param>
    /// <param name="blockIndex">The zero-based index of the block.</param>
    /// <exception cref="ArgumentNullException">The page is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">There is no such block.</exception>
    public static PdfTagContent Block(PdfPageText page, int blockIndex)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentOutOfRangeException.ThrowIfNegative(blockIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(blockIndex, page.Blocks.Count);
        return Block(page, page.Blocks[blockIndex]);
    }

    /// <summary>A run of lines of a block, for example one list item of a block that holds a whole list.</summary>
    /// <param name="page">The page the block is from.</param>
    /// <param name="block">A block of <paramref name="page"/>.</param>
    /// <param name="firstLine">The zero-based index of the first line.</param>
    /// <param name="count">How many lines; at least 1.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The lines are not all in the block.</exception>
    public static PdfTagContent Lines(PdfPageText page, PdfTextBlock block, int firstLine, int count = 1)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(block);
        CheckRange(firstLine, count, block.Lines.Count);
        return new PdfTagContent(Kinds.Lines) { Page = page, SourceBlock = block, First = firstLine, Count = count, PageIndex = page.PageIndex };
    }

    /// <summary>A run of words of a line, for example the bullet of a list item, or a link's text.</summary>
    /// <param name="page">The page the line is from.</param>
    /// <param name="line">A line of <paramref name="page"/>.</param>
    /// <param name="firstWord">The zero-based index of the first word in <see cref="PdfTextLine.Words"/>.</param>
    /// <param name="count">How many words; at least 1.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The words are not all in the line.</exception>
    public static PdfTagContent Words(PdfPageText page, PdfTextLine line, int firstWord, int count = 1)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(line);
        CheckRange(firstWord, count, line.Words.Count);
        return new PdfTagContent(Kinds.Words) { Page = page, Line = line, First = firstWord, Count = count, PageIndex = page.PageIndex };
    }

    /// <summary>One cell of a line that the extraction split into cells (<see cref="PdfTextLine.CellStarts"/>): the words from the cell's start up to the next one.</summary>
    /// <param name="page">The page the line is from.</param>
    /// <param name="line">A line of <paramref name="page"/>.</param>
    /// <param name="cellIndex">The zero-based index into <see cref="PdfTextLine.CellStarts"/>.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The line has no such cell.</exception>
    public static PdfTagContent Cell(PdfPageText page, PdfTextLine line, int cellIndex)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(line);
        IReadOnlyList<int> starts = line.CellStarts;
        ArgumentOutOfRangeException.ThrowIfNegative(cellIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(cellIndex, starts.Count);
        int first = starts[cellIndex];
        int end = cellIndex + 1 < starts.Count ? starts[cellIndex + 1] : line.Words.Count;
        CheckRange(first, end - first, line.Words.Count);
        return new PdfTagContent(Kinds.Words) { Page = page, Line = line, First = first, Count = end - first, PageIndex = page.PageIndex };
    }

    /// <summary>
    /// The marks that are not text and lie inside an area of the page: the paths, images and shadings painted there (a picture, a
    /// chart, a rule that is part of a figure). A mark belongs to the area when its bounding box lies inside it, give or take one point;
    /// a stroked path counts with half its line width.
    /// </summary>
    /// <param name="page">The page, from the extraction (it names the page and the document).</param>
    /// <param name="area">The area in upright page space, the space of <see cref="PdfTextBlock.Bounds"/>: points, origin at the bottom left of the page as a viewer shows it.</param>
    /// <exception cref="ArgumentNullException">The page is null.</exception>
    /// <exception cref="ArgumentException">The area is empty or not finite.</exception>
    public static PdfTagContent Graphics(PdfPageText page, PdfRectangle area)
    {
        ArgumentNullException.ThrowIfNull(page);
        CheckArea(area);
        return new PdfTagContent(Kinds.Graphics) { Page = page, PageIndex = page.PageIndex, Area = area };
    }

    /// <summary>As <see cref="Graphics(PdfPageText, PdfRectangle)"/>, naming the page by its zero-based index.</summary>
    /// <param name="pageIndex">The zero-based page index.</param>
    /// <param name="area">The area in upright page space.</param>
    /// <exception cref="ArgumentOutOfRangeException">The index is negative.</exception>
    /// <exception cref="ArgumentException">The area is empty or not finite.</exception>
    public static PdfTagContent Graphics(int pageIndex, PdfRectangle area)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        CheckArea(area);
        return new PdfTagContent(Kinds.Graphics) { PageIndex = pageIndex, Area = area };
    }

    /// <summary>
    /// An annotation of the document, typically the link annotation of a <see cref="PdfTagRole.Link"/> element. It is tied to the
    /// element with an object reference and given a <c>/StructParent</c>.
    /// </summary>
    /// <param name="annotation">An annotation of one of the document's pages; it must be an indirect object.</param>
    /// <exception cref="ArgumentNullException">The annotation is null.</exception>
    public static PdfTagContent Annotation(PdfAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        return new PdfTagContent(Kinds.Annotation) { SourceAnnotation = annotation };
    }

    private static void CheckRange(int first, int count, int available)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(first);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        if (first > available - count)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "The range extends past the end.");
        }
    }

    private static void CheckArea(PdfRectangle r)
    {
        if (!double.IsFinite(r.Left) || !double.IsFinite(r.Bottom) || !double.IsFinite(r.Right) || !double.IsFinite(r.Top) || r.IsEmpty)
        {
            throw new ArgumentException("The area is empty or not finite.", nameof(r));
        }
    }
}

/// <summary>
/// One element of the structure tree to write: a role, the content it is made of and its child elements, in reading order. Make
/// elements with <see cref="PdfTaggingPlan.Add(PdfTagRole, PdfTagContent[])"/> and <see cref="Add(PdfTagRole, PdfTagContent[])"/>.
/// </summary>
public sealed class PdfTagElement
{
    private readonly PdfTaggingPlan _plan;
    private readonly List<object> _items = [];

    internal PdfTagElement(PdfTaggingPlan plan, PdfTagElement? parent, PdfTagRole role, int depth)
    {
        _plan = plan;
        Parent = parent;
        Role = role;
        Depth = depth;
    }

    /// <summary>The role.</summary>
    public PdfTagRole Role { get; }

    /// <summary>The element this one is a child of, or null for a top-level element.</summary>
    public PdfTagElement? Parent { get; }

    /// <summary>Alternate text (<c>/Alt</c>, ISO 32000-2 14.9.3): what a figure shows, in words. PDF/UA needs it on figures and formulas.</summary>
    public string? Alt { get; set; }

    /// <summary>Replacement text (<c>/ActualText</c>, 14.9.4) for content whose characters do not extract as the text they stand for.</summary>
    public string? ActualText { get; set; }

    /// <summary>The language of the content, a BCP 47 tag such as <c>de-CH</c> (<c>/Lang</c>, 14.9.2). Null means the document's language.</summary>
    public string? Lang { get; set; }

    /// <summary>For a header cell (<see cref="PdfTagRole.TH"/>): what it is the header of. Null writes no scope.</summary>
    public PdfTagScope? Scope { get; set; }

    /// <summary>For a table cell: how many columns it spans. Default 1.</summary>
    public int ColSpan { get; set; } = 1;

    /// <summary>For a table cell: how many rows it spans. Default 1.</summary>
    public int RowSpan { get; set; } = 1;

    internal int Depth { get; }

    /// <summary>The children and content, in order: each item is a <see cref="PdfTagElement"/> or a <see cref="PdfTagContent"/>.</summary>
    internal IReadOnlyList<object> Items => _items;

    /// <summary>Adds a child element with the given content.</summary>
    /// <param name="role">The child's role.</param>
    /// <param name="content">Content that belongs to the child directly, in order; may be empty when the child gets children of its own.</param>
    /// <returns>The new child, for setting properties and adding more.</returns>
    /// <exception cref="PdfLimitException">The tree would be deeper than <see cref="PdfLimits.MaxNestingDepth"/> or hold more elements than <see cref="PdfLimits.MaxObjects"/> allows.</exception>
    public PdfTagElement Add(PdfTagRole role, params PdfTagContent[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        PdfTagElement child = _plan.NewElement(this, role, Depth + 1);
        _items.Add(child);
        foreach (PdfTagContent c in content)
        {
            child.Add(c);
        }

        return child;
    }

    /// <summary>Adds content to this element, after what it already holds.</summary>
    /// <param name="content">The content.</param>
    /// <returns>This element.</returns>
    public PdfTagElement Add(PdfTagContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _items.Add(content);
        return this;
    }
}

/// <summary>
/// What to write into an untagged document: the structure tree, built by the caller from the extraction, plus the title and
/// language. Make one with <see cref="PdfDocument.CreateTaggingPlan"/> and apply it with <see cref="PdfDocument.ApplyTagging"/>.
/// OnePDF writes what the plan says and judges nothing: it does not decide roles. Everything painted that the plan does not
/// reference becomes an artifact.
/// </summary>
public sealed class PdfTaggingPlan
{
    private readonly List<object> _items = [];
    private readonly List<(PdfTagContent Content, PdfArtifactKind Kind, bool Soft)> _artifacts = [];
    private int _elements;

    internal PdfTaggingPlan(PdfDocument document)
    {
        Document = document;
    }

    /// <summary>The document title. It becomes the <c>/Title</c> of the information dictionary, and a PDF/UA save puts it in the XMP packet. Null keeps the title the file has.</summary>
    public string? Title { get; set; }

    /// <summary>The language of the document, a BCP 47 tag such as <c>en-US</c>, written as the catalog's <c>/Lang</c>. Null keeps the language the file has.</summary>
    public string? Language { get; set; }

    /// <summary>Notes about a plan made by <see cref="PdfDocument.PlanTagging"/>, for example a link annotation that covers no text. Empty for plans built by hand.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    internal void AddWarnings(IEnumerable<string> warnings) => _warnings.AddRange(warnings);

    private readonly List<string> _warnings = [];

    internal PdfDocument Document { get; }

    internal bool Applied { get; set; }

    internal IReadOnlyList<object> Items => _items;

    internal IReadOnlyList<(PdfTagContent Content, PdfArtifactKind Kind, bool Soft)> Artifacts => _artifacts;

    /// <summary>Adds a top-level element (a child of the document element OnePDF writes) with the given content.</summary>
    /// <param name="role">The element's role.</param>
    /// <param name="content">Content that belongs to it directly, in order.</param>
    /// <returns>The new element, for setting properties and adding more.</returns>
    /// <exception cref="PdfLimitException">The tree would hold more elements than <see cref="PdfLimits.MaxObjects"/> allows.</exception>
    public PdfTagElement Add(PdfTagRole role, params PdfTagContent[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        PdfTagElement e = NewElement(null, role, 1);
        _items.Add(e);
        foreach (PdfTagContent c in content)
        {
            e.Add(c);
        }

        return e;
    }

    /// <summary>
    /// Marks content as an artifact: running heads and feet, page numbers, rules and backgrounds, anything that is not part of the
    /// document's content. Content the plan does not mention is an artifact anyway; naming it only adds the kind.
    /// </summary>
    /// <param name="content">The content to mark.</param>
    /// <param name="kind">The kind of artifact.</param>
    /// <returns>This plan.</returns>
    public PdfTaggingPlan Artifact(PdfTagContent content, PdfArtifactKind kind = PdfArtifactKind.Plain)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Kind == PdfTagContent.Kinds.Annotation)
        {
            throw new ArgumentException("An annotation cannot be an artifact.", nameof(content));
        }

        _artifacts.Add((content, kind, false));
        return this;
    }

    /// <summary>
    /// Marks every block the extraction flagged as a running header or footer (<see cref="PdfTextBlock.Role"/>, set by whole-document
    /// extraction) as an artifact of type Pagination, unless the plan gives that content a role of its own.
    /// </summary>
    /// <param name="pages">The pages of the extraction (read with <see cref="PdfTextOptions.RetainGlyphs"/> true).</param>
    /// <returns>This plan.</returns>
    public PdfTaggingPlan ArtifactHeadersAndFooters(IEnumerable<PdfPageText> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        foreach (PdfPageText page in pages)
        {
            foreach (PdfTextBlock b in page.Blocks)
            {
                if (b.Role != PdfTextRole.Body)
                {
                    _artifacts.Add((PdfTagContent.Block(page, b), b.Role == PdfTextRole.Header ? PdfArtifactKind.Header : PdfArtifactKind.Footer, true));
                }
            }
        }

        return this;
    }

    internal PdfTagElement NewElement(PdfTagElement? parent, PdfTagRole role, int depth)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        PdfLimits limits = Document.Limits;
        if (depth > limits.MaxNestingDepth)
        {
            throw new PdfLimitException($"The structure tree would be deeper than {limits.MaxNestingDepth} levels.");
        }

        if (++_elements > limits.MaxObjects / 2)
        {
            throw new PdfLimitException("The structure tree would hold more elements than the object limit allows.");
        }

        return new PdfTagElement(this, parent, role, depth);
    }
}

/// <summary>Why a tagging plan was refused.</summary>
public enum PdfTaggingFailure
{
    /// <summary>The document already has a structure tree or declares itself tagged (<c>/MarkInfo /Marked true</c>). OnePDF tags untagged files only.</summary>
    AlreadyTagged,

    /// <summary>A reference points at a page, block or annotation of another document, at an extraction made without glyphs, or at something that is not in this document.</summary>
    ForeignContent,

    /// <summary>The same glyph, mark or annotation is referenced more than once.</summary>
    DuplicateContent,

    /// <summary>The element nesting is impossible: for example a row outside a table or a list item outside a list.</summary>
    InvalidNesting,

    /// <summary>The page's text no longer matches the extraction the plan was made from (the document changed after the extraction).</summary>
    ContentChanged,

    /// <summary>A page's content could not be read completely (an undecodable stream, a limit), so not every mark can be accounted for.</summary>
    ContentUnreadable,
}

/// <summary>A tagging plan was refused. Nothing in the document was changed (objects added on the way stay unreferenced).</summary>
public sealed class PdfTaggingException : PdfException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="reason">Why the plan was refused.</param>
    /// <param name="message">The message.</param>
    /// <param name="pageIndex">The zero-based page the problem is on, or -1.</param>
    public PdfTaggingException(PdfTaggingFailure reason, string message, int pageIndex = -1)
        : base(message)
    {
        Reason = reason;
        PageIndex = pageIndex;
    }

    /// <summary>Why the plan was refused.</summary>
    public PdfTaggingFailure Reason { get; }

    /// <summary>The zero-based index of the page the problem is on, or -1.</summary>
    public int PageIndex { get; }
}

/// <summary>What <see cref="PdfDocument.ApplyTagging"/> wrote.</summary>
public sealed class PdfTaggingResult
{
    internal List<string> WarningList { get; } = [];

    /// <summary>How many structure elements were written, the document element included.</summary>
    public int ElementsWritten { get; internal set; }

    /// <summary>How many marked-content sequences were inserted for structure elements.</summary>
    public int ContentSequences { get; internal set; }

    /// <summary>How many artifact sequences were inserted.</summary>
    public int ArtifactSequences { get; internal set; }

    /// <summary>How many pages had their content rewritten.</summary>
    public int PagesChanged { get; internal set; }

    /// <summary>How many form XObjects were copied because their content belongs to more than one element.</summary>
    public int FormsCopied { get; internal set; }

    /// <summary>How many annotations were tied into the tree.</summary>
    public int AnnotationsTagged { get; internal set; }

    /// <summary>Things worth knowing: an element that ended up without content, a reference that matched nothing, signatures that the change invalidates.</summary>
    public IReadOnlyList<string> Warnings => WarningList;
}
