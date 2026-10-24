using System.Text;

namespace OnePdf;

/// <summary>
/// Collects the content of one page of a <see cref="PdfDocumentWriter"/> and writes the page when <see cref="Commit"/> is called.
/// Get one from <see cref="PdfDocumentWriter.BeginPage"/>. Coordinates are those of <see cref="PdfContentBuilder"/>: origin at the
/// lower left corner of the page, y up, in points.
/// </summary>
public sealed class PdfStreamPageBuilder : PdfContentBuilder
{
    private readonly PdfDocumentWriter _writer;
    private readonly PdfDictionary _page;
    private readonly List<int> _mcidParents = [];
    private readonly List<PdfReference> _annotations = [];
    private readonly double _width;
    private readonly double _height;
    private readonly int _rotate;

    internal PdfStreamPageBuilder(PdfDocumentWriter writer, PdfDocument workspace, PdfDictionary page, PdfReference reference, int index, double width, double height, int rotate)
        : base(workspace, null)
    {
        _writer = writer;
        _page = page;
        Reference = reference;
        PageIndex = index;
        _width = width;
        _height = height;
        _rotate = rotate;
    }

    /// <summary>The zero-based index this page will have.</summary>
    public int PageIndex { get; }

    /// <summary>Page width in points.</summary>
    public double Width => _width;

    /// <summary>Page height in points.</summary>
    public double Height => _height;

    /// <summary>The reference the page is written under.</summary>
    public PdfReference Reference { get; }

    /// <summary>True when <see cref="Commit"/> wrote the page.</summary>
    public bool IsCommitted { get; private set; }

    /// <summary>True when marked content or links on this page were bound to structure elements; such a page cannot be abandoned.</summary>
    internal bool HasStructure => _mcidParents.Count > 0 || _boundAnnotations > 0;

    private int _boundAnnotations;

    /// <summary>
    /// Begins a marked-content sequence for <paramref name="element"/> (<c>BDC</c> with the next <c>/MCID</c> of this page) and binds
    /// it to the element's content (ISO 32000-2 14.7.4). Draw the element's text, paths or images, then call
    /// <see cref="PdfContentBuilder.EndMarkedContent"/>. An element can take any number of sequences, on any number of pages.
    /// </summary>
    /// <param name="element">An open or not yet ended element of the same writer.</param>
    /// <param name="tag">The tag to write, by default the element's type. A custom type is normally mapped with <see cref="PdfDocumentWriter.MapRole"/>.</param>
    /// <returns>The marked-content id, unique on this page.</returns>
    /// <exception cref="ArgumentException">The element belongs to another writer.</exception>
    /// <exception cref="InvalidOperationException">The element has ended, or the page is closed.</exception>
    public int BeginTaggedContent(PdfStructureElement element, string? tag = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        CheckOpen();
        if (!ReferenceEquals(element.Owner, _writer))
        {
            throw new ArgumentException("The element belongs to another writer.", nameof(element));
        }

        int mcid = _mcidParents.Count;
        PdfDocumentWriter.BindContent(element, Reference.Number, mcid);
        BeginMarkedContent(tag ?? element.Type, mcid);
        _mcidParents.Add(element.Reference.Number);
        return mcid;
    }

    /// <summary>
    /// Adds a link annotation to an external address (a URI action, ISO 32000-2 12.6.4.7). The address is never fetched. Non-ASCII
    /// characters are written percent-encoded as UTF-8.
    /// </summary>
    /// <param name="area">The clickable rectangle in page coordinates.</param>
    /// <param name="uri">The address.</param>
    /// <param name="element">A <c>Link</c> structure element to tie the annotation to (an object reference in the tree), or null for an untagged link.</param>
    /// <param name="description">Text describing the link for assistive technology (<c>/Contents</c>), or null.</param>
    public void AddLink(PdfRectangle area, string uri, PdfStructureElement? element = null, string? description = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(uri);
        var action = new PdfDictionary();
        action.SetEntry("S", new PdfName("URI"));
        action.SetEntry("URI", new PdfString(Encoding.ASCII.GetBytes(PercentEncode(uri))));
        AddLinkAnnotation(area, action, element, description);
    }

    /// <summary>
    /// Adds a link annotation to a named destination (<see cref="PdfDocumentWriter.AddNamedDestination"/>), which may be defined
    /// later in the document.
    /// </summary>
    public void AddLinkToDestination(PdfRectangle area, string destinationName, PdfStructureElement? element = null, string? description = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationName);
        var action = new PdfDictionary();
        action.SetEntry("S", new PdfName("GoTo"));
        action.SetEntry("D", new PdfString(Encoding.UTF8.GetBytes(destinationName)));
        AddLinkAnnotation(area, action, element, description);
    }

    private void AddLinkAnnotation(PdfRectangle area, PdfDictionary action, PdfStructureElement? element, string? description)
    {
        CheckOpen();
        if (area.IsEmpty || !double.IsFinite(area.Left + area.Bottom + area.Right + area.Top))
        {
            throw new ArgumentException("The link area must be finite and not empty.", nameof(area));
        }

        if (element is not null && !ReferenceEquals(element.Owner, _writer))
        {
            throw new ArgumentException("The element belongs to another writer.", nameof(element));
        }

        var d = new PdfDictionary();
        d.SetEntry("Type", new PdfName("Annot"));
        d.SetEntry("Subtype", new PdfName("Link"));
        var rect = new PdfArray();
        foreach (double v in (double[])[area.Left, area.Bottom, area.Right, area.Top])
        {
            rect.Append(new PdfReal(Math.Round(v, 4)));
        }

        d.SetEntry("Rect", rect);
        var border = new PdfArray();
        for (int i = 0; i < 3; i++)
        {
            border.Append(new PdfInteger(0));
        }

        d.SetEntry("Border", border);
        d.SetEntry("F", new PdfInteger(4)); // Print (ISO 32000-2 12.5.3)
        d.SetEntry("P", Reference);
        d.SetEntry("A", action);
        if (description is not null)
        {
            d.SetEntry("Contents", PdfString.FromText(description));
        }

        if (element is not null)
        {
            int key = _writer.NextStructureKey();
            d.SetEntry("StructParent", new PdfInteger(key));
            _writer.AddParentTreeEntry(key, element.Reference);
            PdfReference annotation = Document.AddObject(d);
            PdfDocumentWriter.BindObject(element, Reference.Number, annotation.Number);
            _boundAnnotations++;
            _annotations.Add(annotation);
            return;
        }

        _annotations.Add(Document.AddObject(d));
    }

    private static string PercentEncode(string uri)
    {
        var sb = new StringBuilder(uri.Length);
        foreach (byte b in Encoding.UTF8.GetBytes(uri))
        {
            if (b is > 0x20 and < 0x7F && b != (byte)'\\')
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Writes the page and what it needs, and makes room for the next page. Open marked-content sequences and graphics states are closed.
    /// </summary>
    /// <exception cref="InvalidOperationException">The builder is already closed.</exception>
    public void Commit()
    {
        CheckOpen();
        byte[] content = Finish();
        PdfDocument doc = Document;

        PdfReference contents = doc.AddObject(doc.CreateStream(content, false));
        var resources = new PdfDictionary();
        AddCategory(resources, "Font", FontResources);
        AddCategory(resources, "XObject", XObjectResources);
        AddCategory(resources, "ExtGState", StateResources);

        _page.SetEntry("Type", new PdfName("Page"));
        _page.SetEntry("Parent", new PdfReference(_writer.CurrentNodeNumber, 0));
        var box = new PdfArray();
        box.Append(new PdfInteger(0));
        box.Append(new PdfInteger(0));
        box.Append(new PdfReal(_width));
        box.Append(new PdfReal(_height));
        _page.SetEntry("MediaBox", box);
        _page.SetEntry("Resources", resources);
        _page.SetEntry("Contents", contents);
        if (_rotate != 0)
        {
            _page.SetEntry("Rotate", new PdfInteger(_rotate));
        }

        if (_annotations.Count > 0)
        {
            var annots = new PdfArray();
            foreach (PdfReference a in _annotations)
            {
                annots.Append(a);
            }

            _page.SetEntry("Annots", annots);
        }

        if (_mcidParents.Count > 0)
        {
            var parents = new PdfArray();
            foreach (int n in _mcidParents)
            {
                parents.Append(new PdfReference(n, 0));
            }

            int key = _writer.NextStructureKey();
            _writer.AddParentTreeEntry(key, doc.AddObject(parents));
            _page.SetEntry("StructParents", new PdfInteger(key));
        }

        if (_mcidParents.Count > 0 || _boundAnnotations > 0)
        {
            _page.SetEntry("Tabs", new PdfName("S")); // tab order follows the structure (ISO 32000-2 12.5.2)
        }

        IsClosed = true;
        IsCommitted = true;
        _writer.PageCommitted(this);
    }

    private static void AddCategory(PdfDictionary resources, string category, IReadOnlyList<(string Name, PdfObject Value)> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var d = new PdfDictionary();
        foreach ((string name, PdfObject value) in items)
        {
            d.SetEntry(name, value);
        }

        resources.SetEntry(category, d);
    }
}
