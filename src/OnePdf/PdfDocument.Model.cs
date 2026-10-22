namespace OnePdf;

public sealed partial class PdfDocument
{
    private Lazy<PdfPageCollection> _pages = null!;

    /// <summary>
    /// The pages, zero-based. The page tree is walked once, on first use, iteratively with cycle and limit protection
    /// (a node reached twice or a kid that is not a dictionary is skipped with a diagnostic).
    /// </summary>
    public PdfPageCollection Pages => _pages.Value;

    /// <summary>True when the catalog's <c>/MarkInfo</c> says the document is tagged (<c>/Marked true</c>).</summary>
    public bool IsTagged => Catalog.GetDictionary("MarkInfo")?.GetBool("Marked") ?? false;

    /// <summary>The document language <c>/Lang</c> of the catalog (for example <c>en-UK</c>), or null.</summary>
    public string? Language => Catalog.GetString("Lang")?.GetText();

    /// <summary>The root of the logical structure tree (<c>/StructTreeRoot</c>) as a raw dictionary, or null. Reading order from it is the text extractor's business.</summary>
    public PdfDictionary? StructureTreeRoot => Catalog.GetDictionary("StructTreeRoot");

    private void InitModel()
    {
        const LazyThreadSafetyMode mode = LazyThreadSafetyMode.ExecutionAndPublication;
        _pages = new Lazy<PdfPageCollection>(() => new PdfPageCollection(this, WalkPages(Catalog, "document")), mode);
        _pageLabels = new Lazy<PdfPageLabels?>(LoadPageLabels, mode);
        _info = new Lazy<DocumentInfo>(() => new DocumentInfo(Trailer.GetDictionary("Info") ?? new PdfDictionary(null)), mode);
        _xmp = new Lazy<XmpMetadata?>(LoadXmp, mode);
        _outline = new Lazy<IReadOnlyList<PdfOutlineItem>>(LoadOutline, mode);
        _namedDestinations = new Lazy<Dictionary<string, PdfObject>>(LoadNamedDestinations, mode);
        _embeddedFiles = new Lazy<IReadOnlyList<PdfEmbeddedFile>>(LoadEmbeddedFiles, mode);
        _javaScript = new Lazy<IReadOnlyList<PdfJavaScript>>(LoadJavaScript, mode);
        _ocgs = new Lazy<IReadOnlyList<PdfOptionalContentGroup>>(LoadOptionalContent, mode);
        _form = new Lazy<PdfAcroForm?>(LoadForm, mode);
    }

    private readonly record struct Inherited(PdfObject? Resources, PdfObject? MediaBox, PdfObject? CropBox, PdfObject? Rotate);

    /// <summary>
    /// Walks a page tree iteratively and returns its pages in order with the inheritable attributes (ISO 32000-2 7.7.3.4)
    /// resolved to the nearest ancestor's. A node reached twice is skipped (a cycle or a shared page), as is a kid that is not
    /// a dictionary; a node without <c>/Kids</c> is a page unless its <c>/Type</c> is <c>/Pages</c>. Depth is limited by
    /// <see cref="PdfLimits.MaxNestingDepth"/>, node count by <see cref="PdfLimits.MaxObjects"/>.
    /// </summary>
    internal List<PdfPageNode> WalkPages(PdfDictionary? catalog, string scope)
    {
        var pages = new List<PdfPageNode>();
        PdfDictionary? root = catalog?.GetDictionary("Pages");
        if (root is null)
        {
            _diag.Warn($"The catalog of the {scope} has no page tree.");
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
                _diag.Warn("The page tree visits an object twice (a cycle or a shared page); the repeat was skipped.", r.Number);
                continue;
            }

            if (++nodes > Limits.MaxObjects)
            {
                throw new PdfLimitException("The page tree has more nodes than the object limit allows.");
            }

            if (depth > Limits.MaxNestingDepth)
            {
                _diag.Warn("The page tree is nested too deeply; the deeper pages were ignored.", node.Reference?.Number ?? -1);
                continue;
            }

            var here = new Inherited(
                node.Get("Resources") ?? inh.Resources,
                node.Get("MediaBox") ?? inh.MediaBox,
                node.Get("CropBox") ?? inh.CropBox,
                node.Get("Rotate") ?? inh.Rotate);

            PdfArray? kids = node.GetArray("Kids");
            if (kids is null)
            {
                if (node.GetName("Type") != "Pages")
                {
                    pages.Add(new PdfPageNode(node, here.Resources, here.MediaBox, here.CropBox, here.Rotate));
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
                    _diag.Warn("A page tree entry is not a dictionary; skipped.", node.Reference?.Number ?? -1);
                }
            }
        }

        return pages;
    }
}
