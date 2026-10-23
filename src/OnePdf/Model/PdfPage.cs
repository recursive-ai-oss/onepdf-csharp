using System.Linq;
namespace OnePdf;

/// <summary>The attributes a page inherits from its ancestors in the page tree, resolved at the node that is a page.</summary>
internal readonly record struct PdfPageNode(PdfDictionary Dictionary, PdfObject? Resources, PdfObject? MediaBox, PdfObject? CropBox, PdfObject? Rotate);

/// <summary>
/// The pages of a document in reading order, zero-based. The page tree is walked once, iteratively, the first time the
/// collection is used; after that every lookup by index is direct. Pages are created on first access and kept.
/// </summary>
public sealed class PdfPageCollection : IReadOnlyList<PdfPage>
{
    private readonly PdfDocument _doc;
    private readonly List<PdfPageNode> _nodes;
    private readonly PdfPage?[] _pages;
    private Dictionary<int, int>? _byObject;

    internal PdfPageCollection(PdfDocument doc, List<PdfPageNode> nodes)
    {
        _doc = doc;
        _nodes = nodes;
        _pages = new PdfPage?[nodes.Count];
    }

    /// <summary>The number of pages.</summary>
    public int Count => _nodes.Count;

    internal PdfPageNode NodeAt(int index) => _nodes[index];

    /// <summary>The page at a zero-based index.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative or not less than <see cref="Count"/>.</exception>
    public PdfPage this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _nodes.Count);
            PdfPage? p = Volatile.Read(ref _pages[index]);
            if (p is null)
            {
                p = new PdfPage(_doc, index, _nodes[index]);
                p = Interlocked.CompareExchange(ref _pages[index], p, null) ?? p;
            }

            return p;
        }
    }

    /// <summary>
    /// The zero-based index of the page whose dictionary is the indirect object with this number, or -1 when no page is.
    /// </summary>
    public int IndexOfObject(int objectNumber)
    {
        Dictionary<int, int>? map = Volatile.Read(ref _byObject);
        if (map is null)
        {
            map = new Dictionary<int, int>(_nodes.Count);
            for (int i = 0; i < _nodes.Count; i++)
            {
                if (_nodes[i].Dictionary.Reference is { } r)
                {
                    map.TryAdd(r.Number, i);
                }
            }

            Volatile.Write(ref _byObject, map);
        }

        return map.GetValueOrDefault(objectNumber, -1);
    }

    /// <inheritdoc/>
    public IEnumerator<PdfPage> GetEnumerator()
    {
        for (int i = 0; i < _nodes.Count; i++)
        {
            yield return this[i];
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// A page with the attributes it inherits from the page tree resolved (ISO 32000-2 7.7.3.4): <c>Resources</c>,
/// <c>MediaBox</c>, <c>CropBox</c> and <c>Rotate</c> come from the nearest ancestor that has them.
/// </summary>
public sealed class PdfPage
{
    private readonly PdfPageNode _node;
    private Lazy<IReadOnlyList<PdfAnnotation>> _annotations = null!;

    internal PdfPage(PdfDocument document, int index, PdfPageNode node)
    {
        Document = document;
        Index = index;
        _node = node;
        Dictionary = node.Dictionary;
        int objNum = node.Dictionary.Reference?.Number ?? -1;

        PdfRectangle? media = PdfRectangle.From(node.MediaBox);
        if (media is null)
        {
            document.ReportWarning(node.MediaBox is null ? "The page has no MediaBox; US Letter was assumed." : "The page's MediaBox is not four numbers; US Letter was assumed.", objNum, -1);
            media = new PdfRectangle(0, 0, 612, 792);
        }

        MediaBox = media.Value;
        PdfRectangle? crop = PdfRectangle.From(node.CropBox);
        if (node.CropBox is not null && crop is null)
        {
            document.ReportWarning("The page's CropBox is not four numbers; the MediaBox was used.", objNum, -1);
        }

        // The visible region is the CropBox clipped to the MediaBox (7.7.3.3); a CropBox outside it is unusable.
        CropBox = crop is { } c ? (c.Intersect(MediaBox) ?? MediaBox) : MediaBox;
        BleedBox = Box("BleedBox", CropBox);
        TrimBox = Box("TrimBox", CropBox);
        ArtBox = Box("ArtBox", CropBox);

        long rot = node.Rotate switch { PdfInteger i => i.Value, PdfReal r when double.IsFinite(r.Value) && Math.Abs(r.Value) < 1e9 => (long)r.Value, _ => 0 };
        if (rot % 90 != 0)
        {
            document.ReportWarning($"The page's Rotate {rot} is not a multiple of 90; 0 was used.", objNum, -1);
            rot = 0;
        }

        Rotate = (int)(((rot % 360) + 360) % 360);
        double uu = node.Dictionary.GetDouble("UserUnit", 1);
        UserUnit = double.IsFinite(uu) && uu > 0 ? uu : 1;
        Resources = node.Resources as PdfDictionary ?? (node.Resources is PdfStream rs ? rs.Dictionary : new PdfDictionary(null));
        _annotations = new Lazy<IReadOnlyList<PdfAnnotation>>(LoadAnnotations, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Extracts this page's text: words, lines and blocks in reading order, and <see cref="Text.PdfPageText.Text"/> in the mode the
    /// options choose. Headers and footers are only flagged by whole-document extraction (<see cref="PdfDocument.ExtractText"/>).
    /// </summary>
    /// <param name="options">Options; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the extraction with <see cref="OperationCanceledException"/>.</param>
    public Text.PdfPageText ExtractText(Text.PdfTextOptions? options = null, CancellationToken cancellationToken = default) =>
        Text.PdfTextExtractor.Create(Document, options).ExtractPage(Index, cancellationToken);

    private PdfRectangle Box(string key, PdfRectangle fallback) => PdfRectangle.From(Dictionary.Get(key)) ?? fallback;

    /// <summary>The document this page belongs to.</summary>
    public PdfDocument Document { get; }

    /// <summary>The page dictionary. Inherited attributes are not copied into it; use the properties of this class for those.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The zero-based position of the page in the document.</summary>
    public int Index { get; }

    /// <summary>The resource dictionary, inherited if need be. Empty (never null) when the page and its ancestors have none.</summary>
    public PdfDictionary Resources { get; }

    /// <summary>The boundary of the physical medium, inherited. US Letter with a diagnostic when missing or malformed.</summary>
    public PdfRectangle MediaBox { get; }

    /// <summary>The visible region: the inherited CropBox clipped to the <see cref="MediaBox"/>; the MediaBox when absent or when it misses the MediaBox.</summary>
    public PdfRectangle CropBox { get; }

    /// <summary>The bleed box; defaults to <see cref="CropBox"/>. Not inherited.</summary>
    public PdfRectangle BleedBox { get; }

    /// <summary>The trim box; defaults to <see cref="CropBox"/>. Not inherited.</summary>
    public PdfRectangle TrimBox { get; }

    /// <summary>The art box; defaults to <see cref="CropBox"/>. Not inherited.</summary>
    public PdfRectangle ArtBox { get; }

    /// <summary>
    /// The clockwise rotation applied when displaying, inherited: 0, 90, 180 or 270. Negative and larger multiples of 90 are
    /// normalized; a value that is not a multiple of 90 reads as 0 with a diagnostic.
    /// </summary>
    public int Rotate { get; }

    /// <summary>Size of a default user space unit in 1/72 inch. 1 unless <c>/UserUnit</c> is a positive number.</summary>
    public double UserUnit { get; }

    /// <summary>
    /// The page's label (for example <c>iv</c> or <c>A-3</c>) from the document's page labels, or null when the document
    /// defines none.
    /// </summary>
    public string? Label => Document.PageLabels?.GetLabel(Index);

    /// <summary>
    /// The content streams in drawing order: the single <c>/Contents</c> stream, or the streams of the array. Entries that
    /// are not streams are skipped.
    /// </summary>
    public IReadOnlyList<PdfStream> ContentStreams
    {
        get
        {
            PdfObject? c = Dictionary.Get("Contents");
            if (c is PdfStream s)
            {
                return [s];
            }

            var list = new List<PdfStream>();
            if (c is PdfArray a)
            {
                for (int i = 0; i < a.Count; i++)
                {
                    if (a[i] is PdfStream cs)
                    {
                        list.Add(cs);
                    }
                }
            }

            return list;
        }
    }

    /// <summary>The annotations of the page in array order (including links and widgets). Entries that are not dictionaries are skipped with a diagnostic. Computed once.</summary>
    public IReadOnlyList<PdfAnnotation> Annotations => _annotations.Value;

    /// <summary>The annotations whose subtype is <c>Link</c>.</summary>
    public IReadOnlyList<PdfAnnotation> Links => [.. Annotations.Where(static a => a.Subtype == "Link")];

    private List<PdfAnnotation> LoadAnnotations()
    {
        var list = new List<PdfAnnotation>();
        if (Dictionary.Get("Annots") is not PdfArray arr)
        {
            return list;
        }

        int n = Math.Min(arr.Count, Document.Limits.MaxObjects);
        for (int i = 0; i < n; i++)
        {
            if (arr[i] is PdfDictionary d)
            {
                list.Add(new PdfAnnotation(Document, this, d));
            }
            else if (arr[i] is not null)
            {
                Document.ReportWarning("A page annotation entry is not a dictionary; skipped.", Dictionary.Reference?.Number ?? -1, -1);
            }
        }

        return list;
    }
}
