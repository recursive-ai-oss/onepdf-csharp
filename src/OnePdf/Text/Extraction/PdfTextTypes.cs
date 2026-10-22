namespace OnePdf.Text;

/// <summary>How plain text is produced from a page.</summary>
public enum PdfTextMode
{
    /// <summary>Blocks in reading order (structure tree when the file is tagged and it covers the page, otherwise geometry); blocks separated by a blank line, lines by a line feed, cells of a table row by a tab. The default.</summary>
    ReadingOrder = 0,

    /// <summary>Glyphs in the order the content stream drew them, split into words and lines where the position jumps.</summary>
    ContentOrder = 1,

    /// <summary>A monospace rendering that keeps the page's horizontal and vertical arrangement, in the manner of <c>pdftotext -layout</c>.</summary>
    Layout = 2,
}

/// <summary>The role of a block on the page.</summary>
public enum PdfTextRole
{
    /// <summary>Ordinary content.</summary>
    Body = 0,

    /// <summary>Text near the top of the page that repeats across pages (document-level extraction only).</summary>
    Header = 1,

    /// <summary>Text near the bottom of the page that repeats across pages (document-level extraction only).</summary>
    Footer = 2,
}

/// <summary>The base direction of a line.</summary>
public enum PdfTextDirection
{
    /// <summary>Left to right.</summary>
    LeftToRight = 0,

    /// <summary>Right to left.</summary>
    RightToLeft = 1,
}

/// <summary>Options for text extraction. All defaults are the documented defaults; the thresholds of the layout analysis are in <c>docs/text-layout.md</c>.</summary>
public sealed class PdfTextOptions
{
    /// <summary>The plain-text mode used by <see cref="PdfPageText.Text"/>. Default <see cref="PdfTextMode.ReadingOrder"/>.</summary>
    public PdfTextMode Mode { get; set; } = PdfTextMode.ReadingOrder;

    /// <summary>Replace the Latin ligature code points U+FB00–U+FB06 by their letters. Default true.</summary>
    public bool DecomposeLigatures { get; set; } = true;

    /// <summary>
    /// Replace presentation forms and compatibility characters by the characters they stand for: Kangxi and CJK radicals, CJK
    /// compatibility ideographs, Hebrew and Arabic presentation forms (the Unicode compatibility decomposition). Default true.
    /// </summary>
    public bool FoldCompatibilityForms { get; set; } = true;

    /// <summary>Join a word broken by a hyphen at the end of a line to the first word of the next line in plain text (the hyphen is dropped). Default false: hyphens are kept.</summary>
    public bool JoinHyphenatedWords { get; set; }

    /// <summary>Keep glyphs the clip removes and glyphs that lie outside the page. Default false: they are excluded.</summary>
    public bool IncludeClippedAndOffPage { get; set; }

    /// <summary>Collapse a glyph drawn again at almost the same place (fake bold, drop shadows). Default true.</summary>
    public bool CollapseDuplicates { get; set; } = true;

    /// <summary>Use the structure tree for the order of a tagged page when it covers the page. Default true.</summary>
    public bool UseStructureTree { get; set; } = true;

    /// <summary>Leave header and footer blocks (see <see cref="PdfTextRole"/>) out of <see cref="PdfPageText.Text"/> in reading-order mode. Default false: they are kept and flagged.</summary>
    public bool ExcludeHeadersAndFooters { get; set; }

    /// <summary>Flag headers and footers by their repetition across pages in whole-document extraction. Default true.</summary>
    public bool DetectHeadersAndFooters { get; set; } = true;

    /// <summary>
    /// Keep the glyphs in the result: <see cref="PdfPageText.Glyphs"/>, <see cref="PdfTextWord.GlyphIndices"/> and the glyph part of the JSON form. Default true.
    /// Glyphs are by far the largest part of a result (about 136 bytes each, a million for a 300-page book). With false the
    /// page keeps its blocks, lines, words and boxes, the glyph list is empty, the word glyph indices are empty, and only the plain
    /// text of <see cref="Mode"/> is available (it is built once the page is complete; <see cref="PdfPageText.GetText"/> with another mode throws <see cref="InvalidOperationException"/>).
    /// Use it when only text, words, lines and positions are wanted.
    /// </summary>
    public bool RetainGlyphs { get; set; } = true;

    /// <summary>Pages processed at once by whole-document extraction. 0 (the default) means min(4, processor count); 1 is sequential.</summary>
    public int MaxDegreeOfParallelism { get; set; }
}

/// <summary>A word: glyphs that touch or nearly touch in reading direction, without a space between them.</summary>
public sealed class PdfTextWord
{
    internal PdfTextWord(string text, PdfRectangle bounds, int[] glyphs, bool isVisible)
    {
        Text = text;
        Bounds = bounds;
        GlyphIndices = glyphs;
        IsVisible = isVisible;
    }

    /// <summary>The word's text in logical order.</summary>
    public string Text { get; }

    /// <summary>The bounding box in page space.</summary>
    public PdfRectangle Bounds { get; }

    /// <summary>Indices into <see cref="PdfPageText.Glyphs"/> of the glyphs that make up the word, in visual order along the reading direction.</summary>
    public IReadOnlyList<int> GlyphIndices { get; internal set; }

    /// <summary>False when every glyph of the word is invisible (for example an OCR text layer).</summary>
    public bool IsVisible { get; }
}

/// <summary>A line: words on one baseline, in logical order (for right-to-left text that is right to left on the page).</summary>
public sealed class PdfTextLine
{
    internal PdfTextLine(PdfTextWord[] words, PdfRectangle bounds, PdfTextDirection direction, bool isVertical, int[] cellStarts, string text)
    {
        Words = words;
        Bounds = bounds;
        Direction = direction;
        IsVertical = isVertical;
        CellStarts = cellStarts;
        Text = text;
    }

    /// <summary>The words in logical order.</summary>
    public IReadOnlyList<PdfTextWord> Words { get; }

    /// <summary>The bounding box in page space.</summary>
    public PdfRectangle Bounds { get; }

    /// <summary>The base direction used to put the line in logical order.</summary>
    public PdfTextDirection Direction { get; }

    /// <summary>True for vertical writing (a column of glyphs).</summary>
    public bool IsVertical { get; }

    /// <summary>Indices into <see cref="Words"/> where a table cell (or any run separated from the previous one by a wide gap) starts; always begins with 0 for a non-empty line.</summary>
    public IReadOnlyList<int> CellStarts { get; }

    /// <summary>The text of the line, words separated by a space and cells by a tab.</summary>
    public string Text { get; }
}

/// <summary>A block: a paragraph, heading, list item, table or similar region, with its lines in order.</summary>
public sealed class PdfTextBlock
{
    internal PdfTextBlock(PdfTextLine[] lines, PdfRectangle bounds, bool isTable)
    {
        Lines = lines;
        Bounds = bounds;
        IsTable = isTable;
    }

    /// <summary>The lines in reading order.</summary>
    public IReadOnlyList<PdfTextLine> Lines { get; }

    /// <summary>The bounding box in page space.</summary>
    public PdfRectangle Bounds { get; }

    /// <summary>True when the block was recognised as a grid of short cells; its lines are rows.</summary>
    public bool IsTable { get; }

    /// <summary>Header, footer or body; set by whole-document extraction.</summary>
    public PdfTextRole Role { get; internal set; }
}

/// <summary>The extraction result for one page.</summary>
public sealed class PdfPageText
{
    private readonly PdfTextOptions _options;
    private string?[]? _textCache;

    internal PdfPageText(int pageIndex, double width, double height, PdfTextGlyph[] glyphs, PdfTextBlock[] blocks, bool usedStructureTree,
        IReadOnlyList<PdfDiagnostic> diagnostics, PdfTextOptions options)
    {
        PageIndex = pageIndex;
        Width = width;
        Height = height;
        Glyphs = glyphs;
        GlyphArray = glyphs;
        Blocks = blocks;
        UsedStructureTree = usedStructureTree;
        Diagnostics = diagnostics;
        _options = options;
    }

    /// <summary>Zero-based page index.</summary>
    public int PageIndex { get; }

    /// <summary>Width of the upright page in points.</summary>
    public double Width { get; }

    /// <summary>Height of the upright page in points.</summary>
    public double Height { get; }

    /// <summary>The glyphs that took part in extraction (after exclusions, ligature decomposition and <c>/ActualText</c>), in content order.</summary>
    public IReadOnlyList<PdfTextGlyph> Glyphs { get; private set; }

    /// <summary>The same glyphs as an array, for the code that reads them by index without going through the interface.</summary>
    internal PdfTextGlyph[] GlyphArray { get; private set; }

    /// <summary>Blocks in reading order.</summary>
    public IReadOnlyList<PdfTextBlock> Blocks { get; }

    /// <summary>True when the structure tree decided the order.</summary>
    public bool UsedStructureTree { get; }

    /// <summary>Notes about this page, such as a structure tree that did not cover enough of the page.</summary>
    public IReadOnlyList<PdfDiagnostic> Diagnostics { get; }

    /// <summary>The words of all blocks in reading order.</summary>
    public IEnumerable<PdfTextWord> Words
    {
        get
        {
            foreach (PdfTextBlock b in Blocks)
            {
                foreach (PdfTextLine l in b.Lines)
                {
                    foreach (PdfTextWord w in l.Words)
                    {
                        yield return w;
                    }
                }
            }
        }
    }

    /// <summary>The page's plain text in the mode of the options it was extracted with.</summary>
    public string Text => GetText(_options.Mode);

    /// <summary>The page's plain text in the given mode. Computed on first use and cached.</summary>
    /// <exception cref="InvalidOperationException">The glyphs were not retained (<see cref="PdfTextOptions.RetainGlyphs"/> is false) and <paramref name="mode"/> is not the mode of the options.</exception>
    public string GetText(PdfTextMode mode)
    {
        string?[] cache = _textCache ??= new string?[3];
        int i = (int)mode;
        if ((uint)i >= 3)
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (cache[i] is { } hit)
        {
            return hit;
        }

        if (_glyphsDropped)
        {
            throw new InvalidOperationException("The glyphs of this page were not retained; only the text of the mode in the options is available.");
        }

        return cache[i] = PlainText.Build(this, mode, _options);
    }

    private bool _glyphsDropped;

    /// <summary>Builds the text of the options' mode, then lets go of the glyphs (<see cref="PdfTextOptions.RetainGlyphs"/> false). Call after headers and footers are flagged.</summary>
    internal void DropGlyphs()
    {
        if (_glyphsDropped)
        {
            return;
        }

        _ = GetText(_options.Mode);
        _glyphsDropped = true;
        GlyphArray = [];
        Glyphs = GlyphArray;
        foreach (PdfTextBlock b in Blocks)
        {
            foreach (PdfTextLine l in b.Lines)
            {
                foreach (PdfTextWord w in l.Words)
                {
                    w.GlyphIndices = [];
                }
            }
        }
    }
}
