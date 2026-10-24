using System.Globalization;
using System.Text;
using OnePdf.Writing;

namespace OnePdf;

/// <summary>Options of <see cref="PdfDocumentWriter"/>.</summary>
public sealed class PdfDocumentWriterOptions
{
    /// <summary>Flate-compress page content and other unfiltered streams as they are written. Default true.</summary>
    public bool CompressStreams { get; init; } = true;

    /// <summary>The PDF version in the header. Default <c>1.7</c>. Cross-reference streams need at least 1.5.</summary>
    public string Version { get; init; } = "1.7";

    /// <summary>
    /// How the cross-reference data is written. <see cref="PdfXrefFormat.Auto"/> (default) and <see cref="PdfXrefFormat.Stream"/> write a
    /// compressed cross-reference stream; <see cref="PdfXrefFormat.Table"/> writes a classic table (offsets must then fit ten digits).
    /// Object streams are not used: objects are written as they become final.
    /// </summary>
    public PdfXrefFormat XrefFormat { get; init; }

    /// <summary>Sixteen bytes for both strings of the trailer's <c>/ID</c>, so the same calls always give the same file. Default null: random.</summary>
    public byte[]? DocumentId { get; init; }

    /// <summary>The document language (catalog <c>/Lang</c>, a BCP 47 tag such as <c>en-US</c>), or null for none.</summary>
    public string? Language { get; init; }

    /// <summary>Ask viewers to show the document title instead of the file name (<c>/ViewerPreferences /DisplayDocTitle</c>). Default false.</summary>
    public bool DisplayDocumentTitle { get; init; }

    /// <summary>Leave the output stream open after <see cref="PdfDocumentWriter.Finish"/> or disposal. Default false.</summary>
    public bool LeaveOpen { get; init; }

    /// <summary>Limits for the objects the writer builds (object count, image pixels, stream sizes). Default <see cref="PdfLimits.Default"/>.</summary>
    public PdfLimits Limits { get; init; } = PdfLimits.Default;
}

/// <summary>
/// Creates a document and writes it to a stream as it goes. Each page is written when it is committed, together with the objects only
/// it needs; images, forms and annotations are written when they are made. Memory therefore does not grow with the page count beyond
/// a few bytes per page (an object offset, the page-tree entry, the parent-tree entry and the outline entry if any).
/// </summary>
/// <remarks>
/// <para>What is kept until <see cref="Finish"/>: the fonts (their subsets are cut when everything is known), forms reserved with
/// <see cref="ReserveForm"/> (for content that is known last, such as a page-count placeholder), the page tree, the outline, named
/// destinations, the structure tree's parent tree, the catalog and the information dictionary.</para>
/// <para>Fonts, images and forms are made with this writer's methods and can be used on any page. Content is drawn with the same
/// <see cref="PdfContentBuilder"/> API as for pages of an edited document, plus marked content and structure elements
/// (<see cref="BeginStructureElement"/>, <see cref="PdfStreamPageBuilder.BeginTaggedContent"/>) for a tagged document (ISO 32000-2 14.7, 14.8).</para>
/// <para>One page is open at a time. A writer is for one thread. Encryption is not supported on this path: write the file, then save
/// it through <see cref="PdfDocument"/> with encryption if needed. If the writer is disposed without <see cref="Finish"/>, the output
/// is an incomplete file.</para>
/// </remarks>
public sealed partial class PdfDocumentWriter : IDisposable
{
    private const int PagesPerNode = 64;

    private readonly PdfDocumentWriterOptions _options;
    private readonly Stream _output;
    private readonly PdfDocument _workspace;
    private readonly StreamingSaver _saver;
    private readonly HashSet<int> _held = [];
    private readonly SortedDictionary<string, string> _info = new(StringComparer.Ordinal);
    private readonly List<int> _pageNumbers = [];
    private readonly List<int> _nodeNumbers = [];
    private readonly List<int> _nodeCounts = [];
    private readonly List<int> _currentNodeKids = [];
    private int _currentNode = -1;
    private PdfDictionary? _currentNodeDict;
    private readonly List<(PdfFormXObject Form, bool Done)> _reservedForms = [];
    private readonly List<(string Title, int Level, int PageIndex, double Top)> _bookmarks = [];
    private readonly List<PdfPageLabelRange> _labelRanges = [];
    private readonly SortedDictionary<string, (int PageIndex, double Top)> _destinations = new(StringComparer.Ordinal);
    private PdfStreamPageBuilder? _openPage;
    private bool _finished;
    private bool _disposed;

    /// <summary>Starts a document that is written to <paramref name="output"/>.</summary>
    /// <param name="output">A writable stream. It needs not be seekable.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    /// <exception cref="ArgumentException">The stream is not writable, or the version is not of the form <c>1.7</c>.</exception>
    public PdfDocumentWriter(Stream output, PdfDocumentWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(output));
        }

        _options = options ?? new PdfDocumentWriterOptions();
        if (_options.DocumentId is { Length: not 16 })
        {
            throw new ArgumentException("DocumentId must be 16 bytes.", nameof(options));
        }

        string v = _options.Version;
        if (v.Length != 3 || v[0] < '1' || v[0] > '2' || v[1] != '.' || v[2] < '0' || v[2] > '9')
        {
            throw new ArgumentException("Version must look like 1.7.", nameof(options));
        }

        if (_options.XrefFormat != PdfXrefFormat.Table && string.CompareOrdinal(v, "1.5") < 0)
        {
            throw new ArgumentException("Cross-reference streams need PDF 1.5 or later; use PdfXrefFormat.Table or a later Version.", nameof(options));
        }

        _output = output;
        _workspace = PdfDocument.CreateEmpty(new PdfOpenOptions { Limits = _options.Limits });
        _saver = new StreamingSaver(_workspace, new PdfSaveOptions { CompressStreams = _options.CompressStreams }, output, v);
    }

    /// <summary>The number of pages committed so far.</summary>
    public int PageCount => _pageNumbers.Count;

    /// <summary>The number of bytes written to the stream so far (the last buffer may not be flushed yet).</summary>
    public long BytesWritten => _saver.Position;

    /// <summary>Messages about repairs and substitutions while building (for example a character a font has no glyph for).</summary>
    public IReadOnlyList<PdfDiagnostic> Diagnostics => _workspace.Diagnostics;

    // ---- document properties ----

    /// <summary>Sets an entry of the information dictionary (<c>Title</c>, <c>Author</c>, <c>Subject</c>, <c>Keywords</c>, <c>Creator</c>, <c>Producer</c>); null removes it.</summary>
    /// <exception cref="InvalidOperationException">The writer is finished.</exception>
    public void SetInfo(string key, string? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        CheckNotFinished();
        if (value is null)
        {
            _info.Remove(key);
        }
        else
        {
            _info[key] = value;
        }
    }

    // ---- fonts, images, forms ----

    /// <summary>Adds one of the standard 14 fonts; see <see cref="PdfDocument.AddStandardFont"/>.</summary>
    public PdfCreatedFont AddStandardFont(string name)
    {
        CheckNotFinished();
        return Hold(() => _workspace.AddStandardFont(name));
    }

    /// <summary>Adds a TrueType or OpenType font, embedded as a subset cut at <see cref="Finish"/>; see <see cref="PdfDocument.AddFont(ReadOnlyMemory{byte}, PdfEmbedFontOptions?)"/>.</summary>
    public PdfCreatedFont AddFont(ReadOnlyMemory<byte> fontData, PdfEmbedFontOptions? options = null)
    {
        CheckNotFinished();
        return Hold(() => _workspace.AddFont(fontData, options));
    }

    /// <summary>Adds a JPEG image, written at once; see <see cref="PdfDocument.AddJpegImage"/>.</summary>
    public PdfCreatedImage AddJpegImage(ReadOnlyMemory<byte> jpeg)
    {
        CheckNotFinished();
        PdfCreatedImage image = _workspace.AddJpegImage(jpeg);
        Flush();
        return image;
    }

    /// <summary>Adds a PNG image, written at once; see <see cref="PdfDocument.AddPngImage"/>.</summary>
    public PdfCreatedImage AddPngImage(ReadOnlyMemory<byte> png)
    {
        CheckNotFinished();
        PdfCreatedImage image = _workspace.AddPngImage(png);
        Flush();
        return image;
    }

    /// <summary>Adds an image from raw samples, written at once; see <see cref="PdfDocument.AddRawImage"/>.</summary>
    public PdfCreatedImage AddRawImage(int width, int height, PdfImageColor color, ReadOnlySpan<byte> samples, ReadOnlySpan<byte> alpha = default)
    {
        CheckNotFinished();
        PdfCreatedImage image = _workspace.AddRawImage(width, height, color, samples, alpha);
        Flush();
        return image;
    }

    /// <summary>
    /// Starts a Form XObject (repeated artwork, a watermark, a letterhead). Draw into the builder and call its
    /// <see cref="PdfFormContentBuilder.Commit"/>: the form is written then, and can be drawn on any page with
    /// <see cref="PdfContentBuilder.DrawForm(PdfFormXObject)"/>.
    /// </summary>
    public PdfFormContentBuilder BeginForm(PdfRectangle boundingBox)
    {
        CheckNotFinished();
        PdfFormContentBuilder b = _workspace.BeginForm(boundingBox);
        b.AfterCommit = Flush;
        return b;
    }

    /// <summary>
    /// Reserves a Form XObject whose content is made later and returns it, so pages can draw it now. Fill it with
    /// <see cref="BeginForm(PdfFormXObject)"/> before <see cref="Finish"/>. This is how "page X of Y" works: every page draws the
    /// reserved form where the page count goes, and the form is filled with the total when it is known.
    /// </summary>
    /// <exception cref="ArgumentException">The box is empty.</exception>
    public PdfFormXObject ReserveForm(PdfRectangle boundingBox)
    {
        CheckNotFinished();
        if (boundingBox.IsEmpty || !double.IsFinite(boundingBox.Left + boundingBox.Bottom + boundingBox.Right + boundingBox.Top))
        {
            throw new ArgumentException("The bounding box must be finite and not empty.", nameof(boundingBox));
        }

        PdfReference r = Hold(() => _workspace.AddObject(new PdfDictionary()));
        var form = new PdfFormXObject(_workspace, r, boundingBox);
        _reservedForms.Add((form, false));
        return form;
    }

    /// <summary>Fills a form made by <see cref="ReserveForm"/>. Call <see cref="PdfFormContentBuilder.Commit"/> on the result; a form can be filled once.</summary>
    /// <exception cref="ArgumentException">The form is not one this writer reserved, or was already filled.</exception>
    public PdfFormContentBuilder BeginForm(PdfFormXObject reserved)
    {
        ArgumentNullException.ThrowIfNull(reserved);
        CheckNotFinished();
        int at = _reservedForms.FindIndex(f => ReferenceEquals(f.Form, reserved));
        if (at < 0 || _reservedForms[at].Done)
        {
            throw new ArgumentException("The form was not reserved by this writer, or is already filled.", nameof(reserved));
        }

        var b = new PdfFormContentBuilder(_workspace, reserved.BoundingBox, reserved);
        b.AfterCommit = () =>
        {
            _reservedForms[at] = (reserved, true);
            _held.Remove(reserved.Reference.Number);
            Flush();
        };
        return b;
    }

    // ---- pages ----

    /// <summary>Starts the next page. The previous page must be committed (or disposed) first.</summary>
    /// <param name="width">Page width in points; between 3 and 14400.</param>
    /// <param name="height">Page height in points; between 3 and 14400.</param>
    /// <param name="rotate">Clockwise display rotation: 0, 90, 180 or 270.</param>
    /// <exception cref="InvalidOperationException">A page is still open, or the writer is finished.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The size or rotation is not valid.</exception>
    public PdfStreamPageBuilder BeginPage(double width, double height, int rotate = 0)
    {
        CheckNotFinished();
        if (_openPage is { IsClosed: false })
        {
            throw new InvalidOperationException("The previous page is still open: commit or dispose it first.");
        }

        DropAbandonedPage();

        if (!(width >= 3 && width <= 14400) || !(height >= 3 && height <= 14400))
        {
            throw new ArgumentOutOfRangeException(nameof(width), "A page is between 3 and 14400 points on each side.");
        }

        if (rotate is not (0 or 90 or 180 or 270))
        {
            throw new ArgumentOutOfRangeException(nameof(rotate));
        }

        if (_currentNode < 0)
        {
            _currentNodeDict = new PdfDictionary();
            _currentNode = Hold(() => _workspace.AddObject(_currentNodeDict)).Number;
        }

        PdfDictionary pageDict = new();
        PdfReference pageRef = Hold(() => _workspace.AddObject(pageDict));
        _openPage = new PdfStreamPageBuilder(this, _workspace, pageDict, pageRef, _pageNumbers.Count, width, height, rotate);
        return _openPage;
    }

    // A page builder that was disposed without Commit leaves its reserved page object behind; drop it. Structure bound to the page's
    // content would then point at a page that does not exist, so that case is an error.
    private void DropAbandonedPage()
    {
        if (_openPage is { IsClosed: true, IsCommitted: false } abandoned)
        {
            if (abandoned.HasStructure)
            {
                throw new InvalidOperationException("A page whose content or links were bound to structure elements was discarded without Commit; the structure tree would refer to a page that does not exist.");
            }

            _held.Remove(abandoned.Reference.Number);
            _workspace.TakeNewObject(abandoned.Reference.Number);
            _openPage = null;
        }
    }

    internal int CurrentNodeNumber => _currentNode;

    // Called by the page builder once the page's dictionary is complete.
    internal void PageCommitted(PdfStreamPageBuilder page)
    {
        _held.Remove(page.Reference.Number);
        _pageNumbers.Add(page.Reference.Number);
        _currentNodeKids.Add(page.Reference.Number);
        if (_currentNodeKids.Count == PagesPerNode)
        {
            CloseNode();
        }

        Flush();
    }

    private void CloseNode()
    {
        if (_currentNode < 0)
        {
            return;
        }

        var kids = new PdfArray();
        foreach (int n in _currentNodeKids)
        {
            kids.Append(new PdfReference(n, 0));
        }

        PdfDictionary d = _currentNodeDict!;
        d.SetEntry("Type", new PdfName("Pages"));
        d.SetEntry("Parent", new PdfReference(2, 0));
        d.SetEntry("Kids", kids);
        d.SetEntry("Count", new PdfInteger(_currentNodeKids.Count));
        _nodeNumbers.Add(_currentNode);
        _nodeCounts.Add(_currentNodeKids.Count);
        _held.Remove(_currentNode);
        _currentNodeKids.Clear();
        _currentNode = -1;
        _currentNodeDict = null;
    }

    // ---- outline and destinations ----

    /// <summary>
    /// Adds a bookmark (an outline entry) that goes to a place on a page. Bookmarks are kept until <see cref="Finish"/>; their
    /// nesting is given by <paramref name="level"/>, which starts at 0 and may rise by at most one from the previous entry.
    /// </summary>
    /// <param name="title">The text shown in the outline.</param>
    /// <param name="level">Nesting depth, 0 for top level.</param>
    /// <param name="pageIndex">Zero-based index of a page already begun (the open page counts).</param>
    /// <param name="top">The y coordinate (in the page's default space) the view scrolls to.</param>
    /// <exception cref="ArgumentOutOfRangeException">The level jumps by more than one, or the page does not exist yet.</exception>
    public void AddBookmark(string title, int level, int pageIndex, double top)
    {
        ArgumentNullException.ThrowIfNull(title);
        CheckNotFinished();
        int previous = _bookmarks.Count == 0 ? -1 : _bookmarks[^1].Level;
        if (level < 0 || level > previous + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(level), "A bookmark's level is 0 or at most one deeper than the previous bookmark's.");
        }

        CheckPageExists(pageIndex);
        if (!double.IsFinite(top))
        {
            throw new ArgumentOutOfRangeException(nameof(top));
        }

        _bookmarks.Add((title, level, pageIndex, top));
    }

    /// <summary>
    /// Defines a named destination, which links (<see cref="PdfStreamPageBuilder.AddLinkToDestination"/>) can name even before the
    /// destination is defined.
    /// </summary>
    /// <exception cref="ArgumentException">The name is empty or already defined.</exception>
    public void AddNamedDestination(string name, int pageIndex, double top)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        CheckNotFinished();
        CheckPageExists(pageIndex);
        if (!double.IsFinite(top))
        {
            throw new ArgumentOutOfRangeException(nameof(top));
        }

        if (!_destinations.TryAdd(name, (pageIndex, top)))
        {
            throw new ArgumentException($"The destination '{name}' is already defined.", nameof(name));
        }
    }

    /// <summary>
    /// Starts a page label range (ISO 32000-2 12.4.2) at a page: from there on, viewers show the pages as the prefix followed by a number in the
    /// given style, counting up from <paramref name="start"/>, until the next range begins. Ranges must be added in page order. If the first
    /// range does not begin at page 0, pages before it are labelled with plain decimal numbers.
    /// </summary>
    /// <param name="firstPageIndex">Zero-based index of a page already begun (the open page counts), after the previous range's page.</param>
    /// <param name="style">The numbering style; <see cref="PdfPageLabelStyle.None"/> gives the prefix alone.</param>
    /// <param name="prefix">Text put before the number, or null for none.</param>
    /// <param name="start">The number of the first page of the range, at least 1.</param>
    /// <exception cref="ArgumentOutOfRangeException">The page does not exist yet, is not after the previous range, or <paramref name="start"/> is below 1.</exception>
    public void AddPageLabelRange(int firstPageIndex, PdfPageLabelStyle style, string? prefix = null, int start = 1)
    {
        CheckNotFinished();
        CheckPageExists(firstPageIndex);
        if (_labelRanges.Count > 0 && firstPageIndex <= _labelRanges[^1].FirstPage)
        {
            throw new ArgumentOutOfRangeException(nameof(firstPageIndex), "Page label ranges are added in page order.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(start, 1);
        _labelRanges.Add(new PdfPageLabelRange(firstPageIndex, style, prefix ?? string.Empty, start));
    }

    private PdfDictionary? PageLabelsDictionary()
    {
        if (_labelRanges.Count == 0)
        {
            return null;
        }

        var nums = new PdfArray();
        if (_labelRanges[0].FirstPage != 0)
        {
            nums.Append(new PdfInteger(0));
            var plain = new PdfDictionary();
            plain.SetEntry("S", new PdfName("D"));
            nums.Append(plain);
        }

        foreach (PdfPageLabelRange r in _labelRanges)
        {
            nums.Append(new PdfInteger(r.FirstPage));
            var d = new PdfDictionary();
            string? code = r.Style switch
            {
                PdfPageLabelStyle.Arabic => "D",
                PdfPageLabelStyle.UpperRoman => "R",
                PdfPageLabelStyle.LowerRoman => "r",
                PdfPageLabelStyle.UpperLetters => "A",
                PdfPageLabelStyle.LowerLetters => "a",
                _ => null,
            };
            if (code is not null)
            {
                d.SetEntry("S", new PdfName(code));
            }

            if (r.Prefix.Length > 0)
            {
                d.SetEntry("P", PdfString.FromText(r.Prefix));
            }

            if (code is not null && r.Start != 1)
            {
                d.SetEntry("St", new PdfInteger(r.Start));
            }

            nums.Append(d);
        }

        var tree = new PdfDictionary();
        tree.SetEntry("Nums", nums);
        return tree;
    }

    private void CheckPageExists(int pageIndex)
    {
        int begun = _pageNumbers.Count + (_openPage is { IsClosed: false } ? 1 : 0);
        if (pageIndex < 0 || pageIndex >= begun)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), "The page has not been begun yet.");
        }
    }

    internal int PageObjectNumber(int pageIndex) => pageIndex < _pageNumbers.Count ? _pageNumbers[pageIndex] : _openPage!.Reference.Number;

    // ---- finishing ----

    /// <summary>
    /// Writes everything that was kept (fonts, page tree, structure tree, outline, catalog, information) and the cross-reference
    /// section, and flushes the stream. The writer cannot be used afterwards.
    /// </summary>
    /// <exception cref="InvalidOperationException">A page is open, a structure element is not ended, or a reserved form was never filled.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled; the output is then incomplete.</exception>
    public void Finish(CancellationToken cancellationToken = default)
    {
        CheckNotFinished();
        if (_openPage is { IsClosed: false })
        {
            throw new InvalidOperationException("A page is still open.");
        }

        DropAbandonedPage();

        if (_reservedForms.Exists(f => !f.Done))
        {
            throw new InvalidOperationException("A form reserved with ReserveForm was never filled.");
        }

        CheckStructureComplete();
        cancellationToken.ThrowIfCancellationRequested();

        CloseNode();
        CreationState.FinishFonts(_workspace);
        PdfReference? outlines = WriteOutlines();
        PdfReference? names = WriteDestinations();
        PdfReference? structRoot = WriteStructureTree();
        cancellationToken.ThrowIfCancellationRequested();

        // The page tree: the root (object 2) points at the nodes of up to 64 pages each.
        var rootKids = new PdfArray();
        foreach (int n in _nodeNumbers)
        {
            rootKids.Append(new PdfReference(n, 0));
        }

        var pages = new PdfDictionary();
        pages.SetEntry("Type", new PdfName("Pages"));
        pages.SetEntry("Kids", rootKids);
        pages.SetEntry("Count", new PdfInteger(_pageNumbers.Count));

        var catalog = new PdfDictionary();
        catalog.SetEntry("Type", new PdfName("Catalog"));
        catalog.SetEntry("Pages", new PdfReference(2, 0));
        if (_options.Language is { } lang)
        {
            catalog.SetEntry("Lang", PdfString.FromText(lang));
        }

        if (PageLabelsDictionary() is { } labels)
        {
            catalog.SetEntry("PageLabels", labels);
        }

        if (outlines is not null)
        {
            catalog.SetEntry("Outlines", outlines);
            catalog.SetEntry("PageMode", new PdfName("UseOutlines"));
        }

        if (names is not null)
        {
            var n = new PdfDictionary();
            n.SetEntry("Dests", names);
            catalog.SetEntry("Names", n);
        }

        if (structRoot is not null)
        {
            catalog.SetEntry("StructTreeRoot", structRoot);
            var mark = new PdfDictionary();
            mark.SetEntry("Marked", new PdfBoolean(true));
            catalog.SetEntry("MarkInfo", mark);
        }

        if (_options.DisplayDocumentTitle)
        {
            var vp = new PdfDictionary();
            vp.SetEntry("DisplayDocTitle", new PdfBoolean(true));
            catalog.SetEntry("ViewerPreferences", vp);
        }

        PdfReference? infoRef = null;
        if (_info.Count > 0)
        {
            var info = new PdfDictionary();
            foreach (KeyValuePair<string, string> kv in _info)
            {
                info.SetEntry(kv.Key, PdfString.FromText(kv.Value));
            }

            infoRef = _workspace.AddObject(info);
        }

        _held.Clear();
        Flush();
        _saver.Write(1, catalog);
        _saver.Write(2, pages);
        _saver.Finish(new PdfReference(1, 0), infoRef, _options.DocumentId, _options.XrefFormat != PdfXrefFormat.Table);
        _output.Flush();
        _finished = true;
    }

    /// <summary>Releases the writer. The output stream is closed unless <see cref="PdfDocumentWriterOptions.LeaveOpen"/> is set. Without <see cref="Finish"/> the output is incomplete.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _workspace.Dispose();
        if (!_options.LeaveOpen)
        {
            _output.Dispose();
        }
    }

    // ---- plumbing ----

    private void CheckNotFinished()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished)
        {
            throw new InvalidOperationException("The document is finished.");
        }
    }

    private T Hold<T>(Func<T> create)
    {
        var before = new HashSet<int>(_workspace.DirtyNumbers());
        T result = create();
        foreach (int n in _workspace.DirtyNumbers())
        {
            if (!before.Contains(n))
            {
                _held.Add(n);
            }
        }

        return result;
    }

    // Writes every new object that is final (not held back) and lets the workspace forget it.
    internal void Flush()
    {
        foreach (int n in _workspace.DirtyNumbers())
        {
            if (_held.Contains(n))
            {
                continue;
            }

            if (_workspace.TakeNewObject(n) is { } obj)
            {
                _saver.Write(n, obj);
            }
        }
    }

    // Allocates an object number for something written later.
    internal PdfReference Reserve(PdfDictionary placeholder)
    {
        return Hold(() => _workspace.AddObject(placeholder));
    }

    internal void Release(int number) => _held.Remove(number);

    private PdfReference? WriteOutlines()
    {
        if (_bookmarks.Count == 0)
        {
            return null;
        }

        int count = _bookmarks.Count;
        var dicts = new PdfDictionary[count];
        var refs = new PdfReference[count];
        for (int i = 0; i < count; i++)
        {
            dicts[i] = new PdfDictionary();
            refs[i] = _workspace.AddObject(dicts[i]);
        }

        var root = new PdfDictionary();
        PdfReference rootRef = _workspace.AddObject(root);
        root.SetEntry("Type", new PdfName("Outlines"));

        // Parent, siblings and counts from the levels (ISO 32000-2 12.3.3). All entries are open, so /Count is the number of descendants.
        var parentOf = new int[count];
        var lastChild = new Dictionary<int, int>(); // parent index (-1 root) -> last child so far
        var firstChild = new Dictionary<int, int>();
        var descendants = new int[count];
        var stack = new List<int>();
        for (int i = 0; i < count; i++)
        {
            int level = _bookmarks[i].Level;
            stack.RemoveRange(level, stack.Count - level);
            int parent = level == 0 ? -1 : stack[level - 1];
            parentOf[i] = parent;
            if (lastChild.TryGetValue(parent, out int prev))
            {
                dicts[prev].SetEntry("Next", refs[i]);
                dicts[i].SetEntry("Prev", refs[prev]);
            }
            else
            {
                firstChild[parent] = i;
            }

            lastChild[parent] = i;
            stack.Add(i);
            for (int a = parent; a >= 0; a = parentOf[a])
            {
                descendants[a]++;
            }
        }

        for (int i = 0; i < count; i++)
        {
            (string title, _, int pageIndex, double top) = _bookmarks[i];
            PdfDictionary d = dicts[i];
            d.SetEntry("Title", PdfString.FromText(title));
            d.SetEntry("Parent", parentOf[i] < 0 ? rootRef : refs[parentOf[i]]);
            if (firstChild.TryGetValue(i, out int fc))
            {
                d.SetEntry("First", refs[fc]);
                d.SetEntry("Last", refs[lastChild[i]]);
                d.SetEntry("Count", new PdfInteger(descendants[i]));
            }

            d.SetEntry("Dest", Destination(pageIndex, top));
        }

        root.SetEntry("First", refs[firstChild[-1]]);
        root.SetEntry("Last", refs[lastChild[-1]]);
        root.SetEntry("Count", new PdfInteger(count));
        return rootRef;
    }

    private PdfArray Destination(int pageIndex, double top)
    {
        var dest = new PdfArray();
        dest.Append(new PdfReference(_pageNumbers[pageIndex], 0));
        dest.Append(new PdfName("XYZ"));
        dest.Append(null);
        dest.Append(new PdfReal(top));
        dest.Append(null);
        return dest;
    }

    private PdfReference? WriteDestinations()
    {
        if (_destinations.Count == 0)
        {
            return null;
        }

        var entries = new List<(PdfObject Key, PdfObject Value)>(_destinations.Count);
        foreach (KeyValuePair<string, (int PageIndex, double Top)> kv in _destinations)
        {
            entries.Add((new PdfString(Encoding.UTF8.GetBytes(kv.Key)), Destination(kv.Value.PageIndex, kv.Value.Top)));
        }

        // Keys sort as byte strings (ISO 32000-2 7.9.6), which the ordinal sort of UTF-8 text equals up to surrogates; sort on bytes to be exact.
        entries.Sort(static (a, b) => ((PdfString)a.Key).Bytes.Span.SequenceCompareTo(((PdfString)b.Key).Bytes.Span));
        PdfDictionary root = NumberTree.Build(entries, names: true, d => _workspace.AddObject(d));
        return _workspace.AddObject(root);
    }
}
