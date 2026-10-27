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
    /// Keep the glyphs in the result: <see cref="PdfPageText.Glyphs"/>, <see cref="PdfTextWord.GlyphIndices"/> and the glyph part of the JSON form.
    /// Null (the default) means true for a single page (<see cref="PdfPage.ExtractText"/>, <see cref="PdfTextExtractor.ExtractPage"/>,
    /// <see cref="PdfTextExtractor.Analyze(PdfPageGlyphs, PdfTextOptions?, CancellationToken)"/>) and false for whole-document
    /// extraction (<see cref="PdfDocument.ExtractText"/>, <see cref="PdfTextExtractor.ExtractDocument"/>).
    /// Glyphs are by far the largest part of a result (about 136 bytes each, a million for a 300-page book), so a long document
    /// would not fit in memory with all of them kept. Without glyphs a page keeps its blocks, lines, words and boxes; the glyph list and
    /// the word glyph indices are empty; the text of every <see cref="PdfTextMode"/> stays available, because a page of a document result
    /// recomputes the modes it did not build from its source on first use (the document must still be open; a page analyzed from
    /// a bare glyph list with false cannot, and <see cref="PdfPageText.GetText"/> throws <see cref="InvalidOperationException"/> for other modes).
    /// <see cref="PdfPageText.ReadGlyphs"/> returns the glyph list of a page on request. Set true to keep every page's glyphs.
    /// </summary>
    public bool? RetainGlyphs { get; set; }

    /// <summary>Pages processed at once by whole-document extraction. 0 (the default) means min(4, processor count); 1 is sequential.</summary>
    public int MaxDegreeOfParallelism { get; set; }
}

/// <summary>A word: glyphs that touch or nearly touch in reading direction, without a space between them.</summary>
public sealed class PdfTextWord
{
    private string? _text;
    private string? _line;
    private readonly int _start;
    private readonly int _length;

    internal PdfTextWord(string text, PdfRectangle bounds, int[] glyphs, bool isVisible)
    {
        _text = text;
        Bounds = bounds;
        GlyphIndices = glyphs;
        IsVisible = isVisible;
    }

    /// <summary>A word whose text is a slice of its line's text; it is cut out on first use, so a page read only as plain text never allocates it.</summary>
    internal PdfTextWord(int start, int length, PdfRectangle bounds, int[] glyphs, bool isVisible)
    {
        _start = start;
        _length = length;
        Bounds = bounds;
        GlyphIndices = glyphs;
        IsVisible = isVisible;
    }

    internal void SetLine(string line) => _line = line;

    /// <summary>The word's text in logical order.</summary>
    public string Text => _text ??= _line!.Substring(_start, _length);

    /// <summary>The bounding box in page space.</summary>
    public PdfRectangle Bounds { get; }

    /// <summary>Indices into <see cref="PdfPageText.Glyphs"/> of the glyphs that make up the word, in visual order along the reading direction.</summary>
    public IReadOnlyList<int> GlyphIndices { get; internal set; }

    /// <summary>False when every glyph of the word is invisible (for example an OCR text layer).</summary>
    public bool IsVisible { get; }
}

/// <summary>A word kept compactly until someone reads it: where its text sits in the line's text, its box and its visibility.</summary>
internal readonly struct PdfTextWordRecord(int start, int length, PdfRectangle bounds, bool isVisible)
{
    internal int Start { get; } = start;

    internal int Length { get; } = length;

    internal PdfRectangle Bounds { get; } = bounds;

    internal bool IsVisible { get; } = isVisible;
}

/// <summary>A line: words on one baseline, in logical order (for right-to-left text that is right to left on the page).</summary>
public sealed class PdfTextLine
{
    private PdfTextWord[]? _words;
    private PdfTextWordRecord[]? _records;

    internal PdfTextLine(PdfTextWord[] words, PdfRectangle bounds, PdfTextDirection direction, bool isVertical, int[] cellStarts, string text)
    {
        _words = words;
        Bounds = bounds;
        Direction = direction;
        IsVertical = isVertical;
        CellStarts = cellStarts;
        Text = text;
    }

    /// <summary>
    /// A line whose words are made from <paramref name="records"/> when first read. Plain text needs only the line, so a document extracted without
    /// its glyphs never allocates a word object it does not use. The words are slices of <paramref name="text"/> with no glyph indices.
    /// </summary>
    internal PdfTextLine(PdfTextWordRecord[] records, PdfRectangle bounds, PdfTextDirection direction, bool isVertical, int[] cellStarts, string text)
    {
        _records = records;
        Bounds = bounds;
        Direction = direction;
        IsVertical = isVertical;
        CellStarts = cellStarts;
        Text = text;
    }

    /// <summary>The words in logical order.</summary>
    public IReadOnlyList<PdfTextWord> Words => _words ?? MakeWords();

    private PdfTextWord[] MakeWords()
    {
        PdfTextWordRecord[]? records = Volatile.Read(ref _records);
        if (records is null)
        {
            // Another thread has published the words already (it does so before it drops the records).
            return _words!;
        }

        var made = new PdfTextWord[records.Length];
        for (int i = 0; i < made.Length; i++)
        {
            PdfTextWordRecord r = records[i];
            made[i] = new PdfTextWord(r.Start, r.Length, r.Bounds, [], r.IsVisible);
            made[i].SetLine(Text);
        }

        // Whichever thread publishes first wins, so every reader sees the same word objects.
        PdfTextWord[] words = Interlocked.CompareExchange(ref _words, made, null) ?? made;
        Volatile.Write(ref _records, null);
        return words;
    }

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

    /// <summary>A result whose glyphs were never kept (see <see cref="PdfTextOptions.RetainGlyphs"/>).</summary>
    internal static PdfPageText WithoutGlyphs(int pageIndex, double width, double height, PdfTextBlock[] blocks, IReadOnlyList<PdfDiagnostic> diagnostics, PdfTextOptions options, bool wordsAlreadyClear = false)
    {
        var page = new PdfPageText(pageIndex, width, height, [], blocks, false, diagnostics, options);
        page._glyphsDropped = true;
        if (!wordsAlreadyClear)
        {
            page.ClearWordGlyphs();
        }

        return page;
    }

    /// <summary>Zero-based page index.</summary>
    public int PageIndex { get; }

    /// <summary>The document the page was read from, or null for a page analyzed from a bare glyph list.</summary>
    internal PdfDocument? Document { get; set; }

    /// <summary>How many glyphs the page's content showed before exclusions and duplicate removal (see <see cref="PdfTextGlyph.SourceIndex"/>).</summary>
    internal int SourceGlyphCount { get; set; }

    /// <summary>True when the glyphs were dropped after the analysis, so words carry no glyph indices.</summary>
    internal bool GlyphsWereDropped => _glyphsDropped;

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

        // The reading-order text is built from the blocks alone, so it stays available after the glyphs are gone.
        // The other modes need the glyphs: read the page again from its source (the result is deterministic).
        if (_glyphsDropped && mode != PdfTextMode.ReadingOrder)
        {
            if (_reload is null)
            {
                throw new InvalidOperationException("The glyphs of this page were not retained and it has no source to read them from again; only the text of the mode in the options is available.");
            }

            return cache[i] = _reload.ExtractPageWith(PageIndex, true, default).GetText(mode);
        }

        return cache[i] = PlainText.Build(this, mode, _options);
    }

    private bool _glyphsDropped;
    private PdfTextExtractor? _reload;

    internal void SetReload(PdfTextExtractor extractor) => _reload = extractor;

    /// <summary>
    /// The glyphs of this page. When they were kept (<see cref="PdfTextOptions.RetainGlyphs"/>) this is <see cref="Glyphs"/>; otherwise the page is
    /// read again from its document, which must still be open. The indices in <see cref="PdfTextWord.GlyphIndices"/> of a page extracted with
    /// glyphs refer to this list; a page extracted without glyphs has no word indices.
    /// </summary>
    /// <param name="cancellationToken">Cancels the re-read.</param>
    /// <exception cref="InvalidOperationException">The glyphs were dropped and the page has no source (it came from <see cref="PdfTextExtractor.Analyze(PdfPageGlyphs, PdfTextOptions?, CancellationToken)"/>).</exception>
    public IReadOnlyList<PdfTextGlyph> ReadGlyphs(CancellationToken cancellationToken = default)
    {
        if (!_glyphsDropped)
        {
            return Glyphs;
        }

        if (_reload is null)
        {
            throw new InvalidOperationException("The glyphs of this page were not retained and it has no source to read them from again.");
        }

        return _reload.ExtractPageWith(PageIndex, true, cancellationToken).Glyphs;
    }

    /// <summary>
    /// Lets go of the glyphs (<see cref="PdfTextOptions.RetainGlyphs"/> false) once the page is analyzed. The text of the options' mode is
    /// built first when it needs them; reading-order text needs only the blocks and is built on first use, after headers and footers are flagged.
    /// </summary>
    internal void DropGlyphs()
    {
        if (_glyphsDropped)
        {
            return;
        }

        if (_options.Mode != PdfTextMode.ReadingOrder)
        {
            _ = GetText(_options.Mode);
        }

        _glyphsDropped = true;
        GlyphArray = [];
        Glyphs = GlyphArray;
        ClearWordGlyphs();
    }

    private void ClearWordGlyphs()
    {
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
