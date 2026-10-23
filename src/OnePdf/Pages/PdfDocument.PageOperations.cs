using OnePdf.Internal;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace OnePdf;

// Page operations: reorder, delete, insert, rotate and duplicate pages, import pages from other documents, merge and split.
public sealed partial class PdfDocument
{
    // What earlier imports from one source copied, so a second import reuses the shared objects (see PageImporter).
    // Made on first use: most documents are only read, and opening one should not pay for this.
    private ConditionalWeakTable<PdfDocument, PageImporter>? _importers;

    internal ConditionalWeakTable<PdfDocument, PageImporter> Importers =>
        _importers ?? Interlocked.CompareExchange(ref _importers, new(), null) ?? _importers;

    internal PdfResolver RootResolver => _resolvers[0];

    internal IReadOnlyDictionary<string, PdfObject> NamedDestinationMap => NamedDestinationsLazy().Value;

    internal int ObjectMark => _nextObjectNumber;

    // Forgets the objects added since `mark`, for a copy that failed before anything referred to them.
    internal void RollbackObjects(int mark)
    {
        lock (_dirty)
        {
            for (int n = mark; n < _nextObjectNumber; n++)
            {
                _pinned!.TryRemove(n, out _);
                _dirty.Remove(n);
            }

            _nextObjectNumber = mark;
            ObjectCount = mark;
        }
    }

    internal void RaiseVersion(string version) => Version = version;

    // The views built from the page tree and the objects around it are made on first use; after a change they are made again.
    internal void ResetPageViews()
    {
        _pages = null;
        _pageLabels = null;
        _pageCount = new Lazy<int>(CountPagesLean, LazyThreadSafetyMode.ExecutionAndPublication);
        _treeWalkReported = false;
    }

    internal void ResetOutline() => _outline = null;

    internal void ResetNamedDestinations() => _namedDestinations = null;

    internal void ResetOptionalContent() => _ocgs = null;

    internal void ResetForm() => _form = null;

    /// <summary>
    /// Creates a new, empty document that can be edited: no pages, no information dictionary, PDF 1.7. Add pages with
    /// <see cref="InsertBlankPage"/> or <see cref="ImportPages"/> and write it with <see cref="Save(Stream, PdfSaveOptions?, CancellationToken)"/>.
    /// </summary>
    /// <param name="options">Limits and other options; editing is always enabled. Null for the defaults.</param>
    public static PdfDocument CreateEmpty(PdfOpenOptions? options = null)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        string[] bodies = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>"];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 3\n0000000000 65535 f \n");
        foreach (int o in offsets)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{o:D10} 00000 n \n");
        }

        sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size 3 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Open(Encoding.Latin1.GetBytes(sb.ToString()), (options ?? new PdfOpenOptions()) with { AllowEditing = true });
    }

    private List<PageEntry> CurrentPages()
    {
        PdfPageCollection pages = Pages;
        var list = new List<PageEntry>(pages.Count);
        for (int i = 0; i < pages.Count; i++)
        {
            PdfPageNode n = pages.NodeAt(i);
            list.Add(new PageEntry(n.Dictionary, i)
            {
                Resources = n.Resources,
                MediaBox = n.MediaBox,
                CropBox = n.CropBox,
                Rotate = n.Rotate,
                Label = PageLabelDescription.Of(this, i),
            });
        }

        return list;
    }

    private void CommitPages(List<PageEntry> pages)
    {
        PageTreeBuilder.Rebuild(this, pages);
        ResetPageViews();
        ResetOutline();
        ResetNamedDestinations();
        ResetForm();
    }

    /// <summary>
    /// Inserts a new page with no content at <paramref name="index"/>; the pages from there on move up. The page has an empty
    /// <c>/Resources</c> dictionary and a <c>/MediaBox</c> of the given size in points (1/72 inch).
    /// </summary>
    /// <param name="index">The new page's zero-based position, from 0 to <see cref="PageCount"/> (which appends).</param>
    /// <param name="width">The width in points; finite and greater than zero.</param>
    /// <param name="height">The height in points; finite and greater than zero.</param>
    /// <returns>The new page. Page objects obtained before the call are out of date: take them again from <see cref="Pages"/>.</returns>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The index or a size is out of range.</exception>
    public PdfPage InsertBlankPage(int index, double width, double height)
    {
        EnsureEditable(0);
        List<PageEntry> pages = CurrentPages();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, pages.Count);
        if (!double.IsFinite(width) || width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "The width must be finite and greater than zero.");
        }

        if (!double.IsFinite(height) || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "The height must be finite and greater than zero.");
        }

        var page = new PdfDictionary();
        page.SetEntry("Type", new PdfName("Page"));
        var box = new PdfArray();
        box.Append(new PdfInteger(0));
        box.Append(new PdfInteger(0));
        box.Append(new PdfReal(width));
        box.Append(new PdfReal(height));
        page.SetEntry("MediaBox", box);
        page.SetEntry("Resources", new PdfDictionary());
        pages.Insert(index, new PageEntry(page, 0));
        CommitPages(pages);
        return Pages[index];
    }

    /// <summary>Deletes the page at <paramref name="index"/>. See <see cref="RemovePages"/>.</summary>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The index is out of range.</exception>
    public void RemovePage(int index) => RemovePages([index]);

    /// <summary>
    /// Deletes pages. Bookmarks, links, named destinations and the open action that led to a deleted page are repaired: a link
    /// becomes inert, a bookmark is removed (a heading with other bookmarks below it stays, without a target), and
    /// destinations are dropped. Form fields whose widgets were all on deleted pages are removed. The structure tree and the
    /// article threads refer to pages in ways that cannot be repaired cheaply and would keep the deleted content in the file,
    /// so they are dropped (an Info diagnostic says so). Page labels are renumbered to the new order. A full save leaves
    /// out everything that only the deleted pages used.
    /// </summary>
    /// <param name="indexes">Zero-based indices of the pages to delete; repeats are ignored.</param>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An index is out of range.</exception>
    public void RemovePages(IEnumerable<int> indexes)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        EnsureEditable(0);
        List<PageEntry> pages = CurrentPages();
        var doomed = new HashSet<int>();
        foreach (int i in indexes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(i);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(i, pages.Count);
            doomed.Add(i);
        }

        if (doomed.Count == 0)
        {
            return;
        }

        var removedNumbers = new HashSet<int>();
        var removedPages = new List<PdfDictionary>();
        var kept = new List<PageEntry>(pages.Count - doomed.Count);
        var remaining = new List<PdfDictionary>();
        for (int i = 0; i < pages.Count; i++)
        {
            if (doomed.Contains(i))
            {
                removedPages.Add(pages[i].Dictionary);
                if (pages[i].Dictionary.Reference is { } r)
                {
                    removedNumbers.Add(r.Number);
                }
            }
            else
            {
                kept.Add(pages[i]);
                remaining.Add(pages[i].Dictionary);
            }
        }

        PageRemovalFixer.Apply(this, removedNumbers, removedPages, remaining);
        CommitPages(kept);
    }

    /// <summary>Moves a page so that it ends up at <paramref name="toIndex"/>; the pages in between shift by one.</summary>
    /// <param name="fromIndex">The page's current zero-based index.</param>
    /// <param name="toIndex">The page's index after the move.</param>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An index is out of range.</exception>
    public void MovePage(int fromIndex, int toIndex)
    {
        EnsureEditable(0);
        List<PageEntry> pages = CurrentPages();
        ArgumentOutOfRangeException.ThrowIfNegative(fromIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(fromIndex, pages.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(toIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(toIndex, pages.Count);
        if (fromIndex == toIndex)
        {
            return;
        }

        PageEntry moving = pages[fromIndex];
        pages.RemoveAt(fromIndex);
        pages.Insert(toIndex, moving);
        CommitPages(pages);
    }

    /// <summary>
    /// Puts the pages in a new order: <c>newOrder[i]</c> is the current index of the page that becomes page <c>i</c>.
    /// Every current page must appear exactly once. Bookmarks, links and destinations follow their pages.
    /// </summary>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    /// <exception cref="ArgumentException">The list is not a permutation of the page indices.</exception>
    public void ReorderPages(IReadOnlyList<int> newOrder)
    {
        ArgumentNullException.ThrowIfNull(newOrder);
        EnsureEditable(0);
        List<PageEntry> pages = CurrentPages();
        if (newOrder.Count != pages.Count)
        {
            throw new ArgumentException("The order must list every page exactly once.", nameof(newOrder));
        }

        var seen = new bool[pages.Count];
        var result = new List<PageEntry>(pages.Count);
        foreach (int i in newOrder)
        {
            if (i < 0 || i >= pages.Count || seen[i])
            {
                throw new ArgumentException("The order must list every page exactly once.", nameof(newOrder));
            }

            seen[i] = true;
            result.Add(pages[i]);
        }

        CommitPages(result);
    }

    /// <summary>
    /// Turns a page clockwise by <paramref name="degrees"/> in addition to its current <c>/Rotate</c> (7.7.3.3); the result is
    /// stored in the page dictionary, normalised to 0, 90, 180 or 270.
    /// </summary>
    /// <param name="index">The page's zero-based index.</param>
    /// <param name="degrees">A multiple of 90, positive (clockwise) or negative.</param>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="degrees"/> is not a multiple of 90.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The index is out of range.</exception>
    public void RotatePage(int index, int degrees)
    {
        EnsureEditable(0);
        PdfPage page = Pages[index];
        SetPageRotation(index, (int)(((long)page.Rotate + degrees) % 360));
    }

    /// <summary>Sets the page's <c>/Rotate</c> (the clockwise angle at which it is displayed) to <paramref name="degrees"/>, normalised to 0, 90, 180 or 270.</summary>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="degrees"/> is not a multiple of 90.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The index is out of range.</exception>
    public void SetPageRotation(int index, int degrees)
    {
        EnsureEditable(0);
        if (degrees % 90 != 0)
        {
            throw new ArgumentException("A page can only be rotated by multiples of 90 degrees.", nameof(degrees));
        }

        PdfPage page = Pages[index];
        int normalised = ((degrees % 360) + 360) % 360;
        page.Dictionary.SetEntry("Rotate", new PdfInteger(normalised));
        _pages = null; // the page objects carry the angle
    }

    /// <summary>
    /// Inserts a copy of a page right after it. The copy has its own annotations (so its links and widgets are its own, and form
    /// fields in it are renamed with a numeric suffix); the content and resources are shared with the original.
    /// </summary>
    /// <returns>The new page.</returns>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The index is out of range.</exception>
    public PdfPage DuplicatePage(int index)
    {
        EnsureEditable(0);
        return ImportPages(this, [index], index + 1)[0];
    }

    /// <summary>
    /// Copies pages of another document into this one, with everything they use: content, resources (fonts, images, colour
    /// spaces, patterns), annotations with their appearance streams, and the attributes they inherited from the source's page
    /// tree, which are written into the copied pages. Each object of the source is copied once however many pages use it,
    /// and a later import from the same source reuses what the earlier one copied (when the source is not editable).
    /// </summary>
    /// <remarks>
    /// Also carried: bookmarks that lead to an imported page and named destinations that imported links use, form fields
    /// (merged under this document's <c>/AcroForm</c>; a top-level name already in use gets a suffix <c>_2</c>, <c>_3</c> and so
    /// on, and the source's default appearance, quadding, default resources and <c>/NeedAppearances</c> come along),
    /// optional content groups with their default visibility, and page labels. Links to pages that were not imported lose
    /// their target. The structure tree (tagged PDF) is pruned to the imported pages and
    /// added to this document's tree (<see cref="PdfImportOptions.ImportStructure"/>); a page copied within the document
    /// (<see cref="DuplicatePage"/>) is not tagged. Not carried: article threads, thumbnails, document-level scripts and
    /// signatures (a signature field arrives without its value). The result is held in memory; nothing refers to the
    /// source afterwards, so it may be disposed. Encryption of either document plays no part until <see cref="Save(Stream, PdfSaveOptions?, CancellationToken)"/>.
    /// If the import fails the page list is unchanged; a failure while the pages are being copied (a limit, cancellation) also leaves no trace in the document.
    /// </remarks>
    /// <param name="source">The document to copy from. It may be this document.</param>
    /// <param name="pageIndexes">Zero-based page indices in the source, in the order they are inserted; a page may be listed twice. Null for all pages.</param>
    /// <param name="insertAt">Where the first copied page goes; -1 (default) appends.</param>
    /// <param name="options">What else to carry; null for the defaults.</param>
    /// <param name="cancellationToken">Checked between objects.</param>
    /// <returns>The new pages, in order.</returns>
    /// <exception cref="InvalidOperationException">This document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An index is out of range.</exception>
    /// <exception cref="PdfLimitException">The copy would exceed <see cref="PdfLimits"/> of this document.</exception>
    public IReadOnlyList<PdfPage> ImportPages(PdfDocument source, IEnumerable<int>? pageIndexes = null, int insertAt = -1, PdfImportOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        EnsureEditable(0);
        source.ThrowIfDisposed();
        options ??= new PdfImportOptions();
        int sourceCount = source.Pages.Count;
        List<int> indexes = [];
        if (pageIndexes is null)
        {
            for (int i = 0; i < sourceCount; i++)
            {
                indexes.Add(i);
            }
        }
        else
        {
            indexes.AddRange(pageIndexes);
        }

        foreach (int i in indexes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(i);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(i, sourceCount);
        }

        List<PageEntry> pages = CurrentPages();
        if (insertAt < 0)
        {
            insertAt = pages.Count;
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(insertAt, pages.Count);
        if (indexes.Count == 0)
        {
            return [];
        }

        List<ImportedPage> imported = PageImporter.For(source, this).Import(indexes, options, cancellationToken);
        var entries = new List<PageEntry>(imported.Count);
        foreach (ImportedPage p in imported)
        {
            entries.Add(new PageEntry(p.Dictionary, p.Origin) { Label = p.Label });
        }

        pages.InsertRange(insertAt, entries);
        CommitPages(pages);
        var result = new PdfPage[imported.Count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = Pages[insertAt + i];
        }

        return result;
    }

    /// <summary>
    /// Makes a new document of all the pages of <paramref name="sources"/>, in order, using <see cref="ImportPages"/> for each
    /// (so bookmarks, form fields and the rest are carried the way it describes). The new document has no information
    /// dictionary; set one with <see cref="SetInfo"/>. Its limits are those of the first source. The sources are not changed
    /// and may be disposed afterwards.
    /// </summary>
    /// <param name="sources">The documents to join.</param>
    /// <param name="options">What else to carry; null for the defaults.</param>
    /// <param name="cancellationToken">Checked between objects.</param>
    /// <returns>A new editable document held in memory; the caller disposes it.</returns>
    /// <exception cref="PdfLimitException">The result would exceed the limits.</exception>
    public static PdfDocument Merge(IEnumerable<PdfDocument> sources, PdfImportOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        PdfDocument[] list = [.. sources];
        PdfDocument target = CreateEmpty(new PdfOpenOptions { Limits = list.Length > 0 ? list[0].Limits : PdfLimits.Default });
        try
        {
            foreach (PdfDocument s in list)
            {
                if (s.Pages.Count > 0)
                {
                    target.ImportPages(s, null, -1, options, cancellationToken);
                }
            }

            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Splits a document into new documents, one per range of pages, each holding only what its pages reach (a font used by
    /// pages in two parts is copied into both). Every part is made as <see cref="ImportPages"/> describes. The source is not
    /// changed and may be disposed afterwards.
    /// </summary>
    /// <param name="source">The document to split.</param>
    /// <param name="ranges">Consecutive page runs; they may overlap or leave pages out.</param>
    /// <param name="cancellationToken">Checked between objects.</param>
    /// <returns>New editable documents held in memory, in the order of the ranges; the caller disposes them.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A range is empty or reaches past the last page.</exception>
    public static IReadOnlyList<PdfDocument> Split(PdfDocument source, IEnumerable<PdfPageRange> ranges, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(ranges);
        int count = source.Pages.Count;
        PdfPageRange[] list = [.. ranges];
        foreach (PdfPageRange r in list)
        {
            if (r.Start < 0 || r.Count < 1 || r.End > count || r.End < r.Start)
            {
                throw new ArgumentOutOfRangeException(nameof(ranges), $"The range {r.Start}+{r.Count} is not inside the {count} pages.");
            }
        }

        var parts = new List<PdfDocument>(list.Length);
        try
        {
            foreach (PdfPageRange r in list)
            {
                PdfDocument part = CreateEmpty(new PdfOpenOptions { Limits = source.Limits });
                parts.Add(part);
                var run = new int[r.Count];
                for (int i = 0; i < run.Length; i++)
                {
                    run[i] = r.Start + i;
                }

                part.ImportPages(source, run, -1, null, cancellationToken);
            }

            return parts;
        }
        catch
        {
            foreach (PdfDocument p in parts)
            {
                p.Dispose();
            }

            throw;
        }
    }

    /// <summary>Splits a document into parts of <paramref name="pagesPerPart"/> pages each (the last may be shorter). See <see cref="Split(PdfDocument, IEnumerable{PdfPageRange}, CancellationToken)"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pagesPerPart"/> is less than 1.</exception>
    public static IReadOnlyList<PdfDocument> Split(PdfDocument source, int pagesPerPart, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(pagesPerPart, 1);
        int count = source.Pages.Count;
        var ranges = new List<PdfPageRange>();
        for (int start = 0; start < count; start += pagesPerPart)
        {
            ranges.Add(new PdfPageRange(start, Math.Min(pagesPerPart, count - start)));
        }

        return Split(source, ranges, cancellationToken);
    }
}
