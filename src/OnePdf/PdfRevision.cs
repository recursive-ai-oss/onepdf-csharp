using OnePdf.Internal;

namespace OnePdf;

/// <summary>
/// One revision of a file: the original document or one incremental update. Objects read through a revision are as
/// they were when that revision was saved, so earlier states stay reachable.
/// </summary>
public sealed class PdfRevision
{
    private readonly PdfDocument _doc;
    private readonly int _view;
    private readonly XrefSection _section;
    private readonly Lazy<long> _end;

    internal PdfRevision(PdfDocument doc, int index, int view, XrefSection section)
    {
        _doc = doc;
        Index = index;
        _view = view;
        _section = section;
        _end = new Lazy<long>(() => section.Offset < 0 ? doc.FileLength : doc.FindRevisionEnd(section.Offset), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Zero for the original, counting up for each update.</summary>
    public int Index { get; }

    /// <summary>Byte offset of this revision's cross-reference section, or -1 for a file that was rebuilt by scanning.</summary>
    public long XrefOffset => _section.Offset;

    /// <summary>True when the section is a cross-reference stream rather than a table.</summary>
    public bool IsXrefStream => _section.IsStream;

    /// <summary>This revision's own trailer (or cross-reference stream dictionary).</summary>
    public PdfDictionary Trailer => _section.Trailer;

    /// <summary>
    /// The offset just past this revision's last <c>%%EOF</c>. Bytes before it are the file as saved at that revision, which is
    /// what an incremental save appends to.
    /// </summary>
    public long EndOffset => _end.Value;

    /// <summary>The catalog as of this revision, or null if it does not resolve to a dictionary.</summary>
    public PdfDictionary? Catalog => Resolve(_section.Trailer.GetRaw("Root") ?? _doc.Trailer.GetRaw("Root")) as PdfDictionary;

    /// <summary>Number of pages in this revision's page tree.</summary>
    public int PageCount => _doc.CountPages(Catalog, $"revision {Index}");

    /// <summary>The object with this number as of this revision, or null when it is absent, free or null there.</summary>
    public PdfObject? GetObject(int objectNumber)
    {
        PdfObject o = _doc.ResolveChain(objectNumber, _view);
        return o is PdfNull ? null : o;
    }

    /// <summary>Follows a reference as of this revision.</summary>
    public PdfObject? Resolve(PdfObject? obj)
    {
        if (obj is PdfReference r)
        {
            obj = _doc.ResolveReference(r, _view);
        }

        return obj is PdfNull ? null : obj;
    }
}
