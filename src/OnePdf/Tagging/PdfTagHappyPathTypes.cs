using System.Linq;
using OnePdf.Text;

namespace OnePdf;

/// <summary>What kind of structure a caller gives a block of extracted text in <see cref="PdfDocument.Tag"/>.</summary>
public enum PdfBlockKind
{
    /// <summary>Not content: a running head, a decoration, a page number. Written as an artifact (the default value).</summary>
    Artifact = 0,

    /// <summary>A heading; <see cref="PdfBlockRole.Level"/> says which of H1 to H6.</summary>
    Heading,

    /// <summary>A paragraph of running text (P).</summary>
    Paragraph,

    /// <summary>One list item, or a block holding several (see <see cref="PdfTagOptions.SplitListBlocks"/>). Consecutive list items share one L.</summary>
    ListItem,

    /// <summary>A table: each line of the block is a row and each cell of a line (<see cref="PdfTextLine.CellStarts"/>) a cell.</summary>
    Table,

    /// <summary>More rows of the table before it, for a table that continues in another block or on another page. Its lines are body rows.</summary>
    TableContinued,

    /// <summary>A figure whose own text is the block (a chart with its labels, a captioned picture); <see cref="PdfBlockRole.Alt"/> is its alternate text.</summary>
    Figure,
}

/// <summary>
/// The role a caller gives one block in <see cref="PdfDocument.Tag"/>. The default value, <see cref="Artifact"/>, means the block is not
/// content. OnePDF never chooses a role: whatever the caller returns is what is written.
/// </summary>
public readonly struct PdfBlockRole : IEquatable<PdfBlockRole>
{
    private PdfBlockRole(PdfBlockKind kind, int level, string? alt)
    {
        Kind = kind;
        Level = level;
        Alt = alt;
    }

    /// <summary>What kind of structure the block is.</summary>
    public PdfBlockKind Kind { get; }

    /// <summary>The heading level 1 to 6 for <see cref="PdfBlockKind.Heading"/>, otherwise 0.</summary>
    public int Level { get; }

    /// <summary>The alternate text of a <see cref="PdfBlockKind.Figure"/>, otherwise null.</summary>
    public string? Alt { get; }

    /// <summary>The block is not content and becomes an artifact.</summary>
    public static PdfBlockRole Artifact => default;

    /// <summary>The block is a paragraph.</summary>
    public static PdfBlockRole Paragraph => new(PdfBlockKind.Paragraph, 0, null);

    /// <summary>The block is a list item (or several, see <see cref="PdfTagOptions.SplitListBlocks"/>).</summary>
    public static PdfBlockRole ListItem => new(PdfBlockKind.ListItem, 0, null);

    /// <summary>The block is a table.</summary>
    public static PdfBlockRole Table => new(PdfBlockKind.Table, 0, null);

    /// <summary>The block continues the table before it.</summary>
    public static PdfBlockRole TableContinued => new(PdfBlockKind.TableContinued, 0, null);

    /// <summary>The block is a heading.</summary>
    /// <param name="level">1 to 6.</param>
    /// <exception cref="ArgumentOutOfRangeException">The level is not 1 to 6.</exception>
    public static PdfBlockRole Heading(int level)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 6);
        return new PdfBlockRole(PdfBlockKind.Heading, level, null);
    }

    /// <summary>The block is a figure with the given alternate text.</summary>
    /// <param name="alt">What the figure shows, for a reader who cannot see it.</param>
    /// <exception cref="ArgumentException">The text is null or blank (a figure with nothing to say is an artifact).</exception>
    public static PdfBlockRole Figure(string alt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alt);
        return new PdfBlockRole(PdfBlockKind.Figure, 0, alt);
    }

    /// <inheritdoc />
    public bool Equals(PdfBlockRole other) => Kind == other.Kind && Level == other.Level && Alt == other.Alt;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PdfBlockRole r && Equals(r);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Kind, Level, Alt);

    /// <summary>Compares two roles.</summary>
    public static bool operator ==(PdfBlockRole left, PdfBlockRole right) => left.Equals(right);

    /// <summary>Compares two roles.</summary>
    public static bool operator !=(PdfBlockRole left, PdfBlockRole right) => !left.Equals(right);
}

/// <summary>A block of the extraction as the role delegate of <see cref="PdfDocument.Tag"/> sees it.</summary>
public sealed class PdfTagBlock
{
    internal PdfTagBlock(PdfPageText page, int blockIndex)
    {
        Page = page;
        BlockIndex = blockIndex;
        Block = page.Blocks[blockIndex];
    }

    /// <summary>The page the block is on.</summary>
    public PdfPageText Page { get; }

    /// <summary>The zero-based page index.</summary>
    public int PageIndex => Page.PageIndex;

    /// <summary>The zero-based index of the block in <see cref="PdfPageText.Blocks"/>.</summary>
    public int BlockIndex { get; }

    /// <summary>The block.</summary>
    public PdfTextBlock Block { get; }

    /// <summary>The text of the block, one line per line.</summary>
    public string Text => string.Join('\n', Block.Lines.Select(static l => l.Text));
}

/// <summary>A figure the caller names by its place on a page, for <see cref="PdfTagOptions.Figures"/>.</summary>
/// <param name="PageIndex">The zero-based page index.</param>
/// <param name="Area">The area in upright page space (the space of <see cref="PdfTextBlock.Bounds"/>); the images and paths inside it are the figure.</param>
/// <param name="Alt">The alternate text.</param>
public readonly record struct PdfTagFigure(int PageIndex, PdfRectangle Area, string Alt);

/// <summary>
/// Settings of <see cref="PdfDocument.Tag"/> and <see cref="PdfDocument.PlanTagging"/>. Each default can be changed here; what the
/// options do not reach is reached by building the plan by hand (<see cref="PdfDocument.CreateTaggingPlan"/>) or by changing the plan
/// <see cref="PdfDocument.PlanTagging"/> returns.
/// </summary>
public sealed class PdfTagOptions
{
    /// <summary>The document title. Default null: the title already in the document is kept.</summary>
    public string? Title { get; set; }

    /// <summary>The document language as a BCP 47 tag, for example <c>en-US</c>. Default null: the language already in the document is kept.</summary>
    public string? Language { get; set; }

    /// <summary>Figures named by area: each becomes a Figure element with its alternate text, placed in reading order among the blocks of its page by its top edge. Default empty.</summary>
    public IList<PdfTagFigure> Figures { get; } = [];

    /// <summary>How many of the first rows of a table are header rows (TH). Default 1. Continued tables (<see cref="PdfBlockKind.TableContinued"/>) have none.</summary>
    public int TableHeaderRows { get; set; } = 1;

    /// <summary>How many of the first cells of every other row are row headers (TH). Default 0.</summary>
    public int TableHeaderColumns { get; set; }

    /// <summary>The scope of header-row cells. Default <see cref="PdfTagScope.Column"/>.</summary>
    public PdfTagScope HeaderRowScope { get; set; } = PdfTagScope.Column;

    /// <summary>The scope of row-header cells. Default <see cref="PdfTagScope.Row"/>.</summary>
    public PdfTagScope HeaderColumnScope { get; set; } = PdfTagScope.Row;

    /// <summary>
    /// False (the default): blocks the extraction found to be running headers or footers are pagination artifacts and the role delegate
    /// is not asked about them. True: they are asked like any other block.
    /// </summary>
    public bool KeepHeadersAndFooters { get; set; }

    /// <summary>True (the default): a leading bullet or number word of a list item becomes its Lbl and the rest its LBody. False: the whole item is the LBody.</summary>
    public bool SplitListLabels { get; set; } = true;

    /// <summary>
    /// True (the default): a list-item block with several lines is cut into items at each line that starts with a label word, and lines
    /// before the first label form an item of their own. False: one block is one item. A block with no label word is always one item.
    /// </summary>
    public bool SplitListBlocks { get; set; } = true;

    /// <summary>Decides whether a word is a list label. Default null: the built-in test (a bullet character, a number, one letter or a roman numeral followed by a dot or a bracket, or in brackets).</summary>
    public Func<string, bool>? IsListLabel { get; set; }

    /// <summary>True (the default): a link annotation becomes a Link element, with an object reference, around the words its rectangle covers. False: links are left alone.</summary>
    public bool TagLinks { get; set; } = true;

    /// <summary>
    /// True (the default): every annotation that is not a link, a form widget or a popup (a sticky note, a highlight, a stamp) becomes an
    /// <see cref="PdfTagRole.Annot"/> element with an object reference, after the page's text. False: they are left alone.
    /// </summary>
    public bool TagAnnotations { get; set; } = true;

    /// <summary>
    /// Called once for every element the happy path creates (also L, LI, Lbl, LBody, TR, TH, TD and Link) with the block it was made
    /// from, before the element gets its children: set <see cref="PdfTagElement.Alt"/>, <see cref="PdfTagElement.Lang"/>, <see cref="PdfTagElement.ActualText"/>, a scope or spans.
    /// Default null.
    /// </summary>
    public Action<PdfTagElement, PdfTagBlock>? Customize { get; set; }
}
