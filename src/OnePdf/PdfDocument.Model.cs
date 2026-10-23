using OnePdf.Internal;

namespace OnePdf;

public sealed partial class PdfDocument
{
    private Lazy<PdfPageCollection>? _pages;

    /// <summary>
    /// The pages, zero-based. The page tree is walked once, on first use, iteratively with cycle and limit protection
    /// (a node reached twice or a kid that is not a dictionary is skipped with a diagnostic).
    /// </summary>
    public PdfPageCollection Pages => PagesLazy().Value;

    /// <summary>True when the catalog's <c>/MarkInfo</c> says the document is tagged (<c>/Marked true</c>).</summary>
    public bool IsTagged => Catalog.GetDictionary("MarkInfo")?.GetBool("Marked") ?? false;

    /// <summary>The document language <c>/Lang</c> of the catalog (for example <c>en-UK</c>), or null.</summary>
    public string? Language => Catalog.GetString("Lang")?.GetText();

    /// <summary>The root of the logical structure tree (<c>/StructTreeRoot</c>) as a raw dictionary, or null. Reading order from it is the text extractor's business.</summary>
    public PdfDictionary? StructureTreeRoot => Catalog.GetDictionary("StructTreeRoot");

    // The lazies are made when first asked for: most documents are opened for a few of these, and each costs several allocations.
    private const LazyThreadSafetyMode LazyMode = LazyThreadSafetyMode.ExecutionAndPublication;

    private Lazy<PdfPageCollection> PagesLazy() =>
        _pages ?? Interlocked.CompareExchange(ref _pages, new Lazy<PdfPageCollection>(() => new PdfPageCollection(this, WalkPages(Catalog, "document")), LazyMode), null) ?? _pages!;

    private Lazy<PdfPageLabels?> PageLabelsLazy() =>
        _pageLabels ?? Interlocked.CompareExchange(ref _pageLabels, new Lazy<PdfPageLabels?>(LoadPageLabels, LazyMode), null) ?? _pageLabels!;

    private Lazy<DocumentInfo> InfoLazy() =>
        _info ?? Interlocked.CompareExchange(ref _info, new Lazy<DocumentInfo>(() => new DocumentInfo(Trailer.GetDictionary("Info") ?? new PdfDictionary(null)), LazyMode), null) ?? _info!;

    private Lazy<XmpMetadata?> XmpLazy() =>
        _xmp ?? Interlocked.CompareExchange(ref _xmp, new Lazy<XmpMetadata?>(LoadXmp, LazyMode), null) ?? _xmp!;

    private Lazy<IReadOnlyList<PdfOutlineItem>> OutlineLazy() =>
        _outline ?? Interlocked.CompareExchange(ref _outline, new Lazy<IReadOnlyList<PdfOutlineItem>>(LoadOutline, LazyMode), null) ?? _outline!;

    private Lazy<Dictionary<string, PdfObject>> NamedDestinationsLazy() =>
        _namedDestinations ?? Interlocked.CompareExchange(ref _namedDestinations, new Lazy<Dictionary<string, PdfObject>>(LoadNamedDestinations, LazyMode), null) ?? _namedDestinations!;

    private Lazy<IReadOnlyList<PdfEmbeddedFile>> EmbeddedFilesLazy() =>
        _embeddedFiles ?? Interlocked.CompareExchange(ref _embeddedFiles, new Lazy<IReadOnlyList<PdfEmbeddedFile>>(LoadEmbeddedFiles, LazyMode), null) ?? _embeddedFiles!;

    private Lazy<IReadOnlyList<PdfJavaScript>> JavaScriptLazy() =>
        _javaScript ?? Interlocked.CompareExchange(ref _javaScript, new Lazy<IReadOnlyList<PdfJavaScript>>(LoadJavaScript, LazyMode), null) ?? _javaScript!;

    private Lazy<IReadOnlyList<PdfOptionalContentGroup>> OptionalContentLazy() =>
        _ocgs ?? Interlocked.CompareExchange(ref _ocgs, new Lazy<IReadOnlyList<PdfOptionalContentGroup>>(LoadOptionalContent, LazyMode), null) ?? _ocgs!;

    private Lazy<PdfAcroForm?> FormLazy() =>
        _form ?? Interlocked.CompareExchange(ref _form, new Lazy<PdfAcroForm?>(LoadForm, LazyMode), null) ?? _form!;

    private readonly record struct Inherited(PdfObject? Resources, PdfObject? MediaBox, PdfObject? CropBox, PdfObject? Rotate);

    /// <summary>
    /// Walks a page tree iteratively and returns its pages in order with the inheritable attributes (ISO 32000-2 7.7.3.4)
    /// resolved to the nearest ancestor's. A node reached twice is skipped (a cycle or a shared page), as is a kid that is not
    /// a dictionary; a node without <c>/Kids</c> is a page unless its <c>/Type</c> is <c>/Pages</c>. Depth is limited by
    /// <see cref="PdfLimits.MaxNestingDepth"/>, node count by <see cref="PdfLimits.MaxObjects"/>.
    /// </summary>
    internal List<PdfPageNode> WalkPages(PdfDictionary? catalog, string scope) =>
        WalkPagesCore(catalog, scope, countOnly: false, quiet: scope == "document" && _treeWalkReported, out _);

    // Set once the document's pages have been counted by the lean walk, which already recorded the tree's diagnostics.
    private volatile bool _treeWalkReported;

    /// <summary>
    /// The page count without building page nodes: the same walk and the same skipping rules as <see cref="WalkPages"/>, but
    /// the inheritable attributes (which would load every page's resources) are not resolved.
    /// </summary>
    private int CountPagesLean()
    {
        if (_pages is { IsValueCreated: true } created)
        {
            return created.Value.Count;
        }

        WalkPagesCore(Catalog, "document", countOnly: true, quiet: false, out int count);
        _treeWalkReported = true;
        return count;
    }

    private List<PdfPageNode> WalkPagesCore(PdfDictionary? catalog, string scope, bool countOnly, bool quiet, out int count)
    {
        count = 0;
        DiagnosticBag? diag = quiet ? null : _diag;
        var pages = new List<PdfPageNode>();
        PdfDictionary? root = catalog?.GetDictionary("Pages");
        if (root is null)
        {
            diag?.Warn($"The catalog of the {scope} has no page tree.");
            return pages;
        }

        var visited = new HashSet<int>();
        var stack = new Stack<(PdfDictionary Node, Inherited Inh, int Depth)>();
        stack.Push((root, default, 0));
        long nodes = 0;
        while (stack.Count > 0)
        {
            (PdfDictionary node, Inherited inh, int depth) = stack.Pop();
            if (node.Reference is { } r && !visited.Add(r.Number))
            {
                diag?.Warn("The page tree visits an object twice (a cycle or a shared page); the repeat was skipped.", r.Number);
                continue;
            }

            if (++nodes > Limits.MaxObjects)
            {
                throw new PdfLimitException("The page tree has more nodes than the object limit allows.");
            }

            if (depth > Limits.MaxNestingDepth)
            {
                diag?.Warn("The page tree is nested too deeply; the deeper pages were ignored.", node.Reference?.Number ?? -1);
                continue;
            }

            Inherited here = countOnly ? default : new Inherited(
                node.Get("Resources") ?? inh.Resources,
                node.Get("MediaBox") ?? inh.MediaBox,
                node.Get("CropBox") ?? inh.CropBox,
                node.Get("Rotate") ?? inh.Rotate);

            PdfArray? kids = node.GetArray("Kids");
            if (kids is null)
            {
                if (node.GetName("Type") != "Pages")
                {
                    count++;
                    if (!countOnly)
                    {
                        pages.Add(new PdfPageNode(node, here.Resources, here.MediaBox, here.CropBox, here.Rotate));
                    }
                }

                continue;
            }

            for (int i = kids.Count - 1; i >= 0; i--)
            {
                if (kids[i] is PdfDictionary child)
                {
                    stack.Push((child, here, depth + 1));
                }
                else
                {
                    diag?.Warn("A page tree entry is not a dictionary; skipped.", node.Reference?.Number ?? -1);
                }
            }
        }

        if (!countOnly)
        {
            count = pages.Count;
        }

        return pages;
    }
}
