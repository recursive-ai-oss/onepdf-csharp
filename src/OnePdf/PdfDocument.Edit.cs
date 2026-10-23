using System.Collections.Concurrent;
using OnePdf.Internal;

namespace OnePdf;

// The mutable view. A document opened with PdfOpenOptions.AllowEditing keeps every object it reads in a table (so a change
// to a parsed object cannot be lost when the object cache drops it) and remembers which indirect objects changed.
public sealed partial class PdfDocument
{
    private ConcurrentDictionary<int, PdfObject>? _pinned;
    private readonly HashSet<int> _dirty = [];
    private int _nextObjectNumber;

    /// <summary>
    /// True when the document was opened with <see cref="PdfOpenOptions.AllowEditing"/>. Only then can objects be changed.
    /// </summary>
    /// <remarks>
    /// Concurrency of the mutable view: any number of threads may read an editable document as before, but changing it is for one
    /// thread at a time, and not while other threads read or save it. Pages, outlines, fonts and the other views built from the
    /// objects are made on first use and are not refreshed by later changes; take them again after editing (<see cref="Info"/>
    /// and <see cref="Xmp"/> are refreshed by <see cref="SetInfo"/> and <see cref="SetXmp"/>).
    /// </remarks>
    public bool IsEditable => _pinned is not null;

    internal void EnsureEditable(int view)
    {
        if (_pinned is null)
        {
            throw new InvalidOperationException("The document was opened read-only. Open it with PdfOpenOptions.AllowEditing to change it.");
        }

        if (view != 0)
        {
            throw new InvalidOperationException("Objects of an earlier revision cannot be changed.");
        }

        ThrowIfDisposed();
    }

    internal void MarkDirty(int owner)
    {
        lock (_dirty)
        {
            _dirty.Add(owner);
        }
    }

    internal int[] DirtyNumbers()
    {
        lock (_dirty)
        {
            int[] list = [.. _dirty];
            Array.Sort(list);
            return list;
        }
    }

    private PdfObject ResolvePinned(ConcurrentDictionary<int, PdfObject> pins, int number)
    {
        if (pins.TryGetValue(number, out PdfObject? pinned))
        {
            return pinned;
        }

        var o = (PdfObject)Cache.GetOrLoad(ObjectKeys.Object(0, number), (this, number, view: 0), static s => s.Item1.LoadObject(s.number, s.view));
        if (o is PdfNull)
        {
            return o;
        }

        EditSupport.Adopt(o, null, number);
        return pins.GetOrAdd(number, o);
    }

    /// <summary>
    /// Makes <paramref name="value"/> a new indirect object under the next free object number and returns its reference.
    /// Containers inside it join the document, so references stored in them resolve. The object is written by the next save.
    /// </summary>
    /// <param name="value">A dictionary, array, stream (see <see cref="CreateStream"/>) or simple object not yet part of a document.</param>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    /// <exception cref="ArgumentException">The value is a reference or null object, is already an indirect object, or belongs to another document.</exception>
    /// <exception cref="PdfLimitException">The document would exceed <see cref="PdfLimits.MaxObjects"/> objects.</exception>
    public PdfReference AddObject(PdfObject value)
    {
        ArgumentNullException.ThrowIfNull(value);
        EnsureEditable(0);
        if (value is PdfReference or PdfNull)
        {
            throw new ArgumentException("A reference or the null object cannot be an indirect object.", nameof(value));
        }

        if (value.Reference is not null)
        {
            throw new ArgumentException("The value is already an indirect object.", nameof(value));
        }

        PdfResolver root = _resolvers[0];
        if ((value as PdfStream)?.Resolver is { } sr && sr.Doc != this)
        {
            throw new ArgumentException("The stream belongs to another document.", nameof(value));
        }

        int n;
        lock (_dirty)
        {
            if (_nextObjectNumber >= Limits.MaxObjects)
            {
                throw new PdfLimitException($"The document would have more than {Limits.MaxObjects} objects.");
            }

            n = _nextObjectNumber++;
            ObjectCount = _nextObjectNumber;
        }

        var reference = new PdfReference(n, 0);
        EditSupport.Adopt(value, root, n);
        value.Reference = reference;
        value.SourceOffset = -1;
        _pinned![n] = value;
        MarkDirty(n);
        return reference;
    }

    /// <summary>
    /// Replaces the content of indirect object <paramref name="number"/> (which must exist or have been added) with
    /// <paramref name="value"/>, keeping its generation. References to the number now lead to the new value.
    /// </summary>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    /// <exception cref="ArgumentException">The value is a reference or null object, is already an indirect object, belongs to another document, or the number is out of range.</exception>
    public void SetObject(int number, PdfObject value)
    {
        ArgumentNullException.ThrowIfNull(value);
        EnsureEditable(0);
        if (number <= 0 || number >= _nextObjectNumber)
        {
            throw new ArgumentOutOfRangeException(nameof(number), "There is no such object number in this document.");
        }

        if (value is PdfReference or PdfNull || value.Reference is not null)
        {
            throw new ArgumentException("The value must be a new object that is not yet indirect (a reference and the null object are not allowed).", nameof(value));
        }

        if ((value as PdfStream)?.Resolver is { } sr && sr.Doc != this)
        {
            throw new ArgumentException("The stream belongs to another document.", nameof(value));
        }

        int generation = _merged!.TryGetValue(number, out XrefEntry e) && e.Type != 2 ? e.B : 0;
        EditSupport.Adopt(value, _resolvers[0], number);
        value.Reference = new PdfReference(number, generation);
        value.SourceOffset = -1;
        _pinned![number] = value;
        MarkDirty(number);
    }

    /// <summary>
    /// Frees indirect object <paramref name="number"/>. References to it read as null from then on; a full save drops them, an
    /// incremental save leaves them in place (they are valid references to a free object).
    /// </summary>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    public void RemoveObject(int number)
    {
        EnsureEditable(0);
        if (number <= 0 || number >= _nextObjectNumber)
        {
            throw new ArgumentOutOfRangeException(nameof(number), "There is no such object number in this document.");
        }

        _pinned![number] = PdfNull.Instance;
        MarkDirty(number);
    }

    /// <summary>
    /// Creates a stream that is not yet part of the document, holding <paramref name="data"/> unfiltered or Flate-compressed.
    /// Store it with <see cref="AddObject"/>, or set it into a dictionary or array, which adds it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    public PdfStream CreateStream(ReadOnlySpan<byte> data, bool compress = false)
    {
        EnsureEditable(0);
        PdfResolver root = _resolvers[0];
        var stream = new PdfStream(new PdfDictionary(root), root, 0);
        stream.SetData(data, compress);
        return stream;
    }

    /// <summary>
    /// Sets one entry of the document information dictionary (for example <c>Title</c>, <c>Author</c>, <c>Producer</c>) to a text
    /// string, creating the dictionary when the document has none; null removes the entry.
    /// </summary>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    public void SetInfo(string key, string? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        EnsureEditable(0);
        PdfDictionary? info = Trailer.GetDictionary("Info");
        if (info is null)
        {
            if (value is null)
            {
                return;
            }

            info = new PdfDictionary();
            Trailer.SetEntry("Info", AddObject(info));
        }

        info.SetEntry(key, value is null ? null : PdfString.FromText(value));
        _info = null;
    }

    /// <summary>
    /// Replaces the XMP metadata packet of the catalog (<c>/Metadata</c>) with <paramref name="xml"/>, creating the stream when the
    /// document has none. The packet is stored unfiltered so readers that look for it in the raw file find it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    public void SetXmp(ReadOnlySpan<byte> xml)
    {
        EnsureEditable(0);
        PdfDictionary catalog = Catalog;
        if (catalog.GetStream("Metadata") is { } existing)
        {
            existing.SetData(xml);
        }
        else
        {
            PdfStream stream = CreateStream(xml);
            stream.Dictionary.SetEntry("Type", new PdfName("Metadata"));
            stream.Dictionary.SetEntry("Subtype", new PdfName("XML"));
            catalog.SetEntry("Metadata", stream);
        }

        _xmp = null;
    }
}
