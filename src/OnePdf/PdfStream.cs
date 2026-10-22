using OnePdf.Internal;

namespace OnePdf;

/// <summary>
/// A stream object: a dictionary plus bytes. The bytes are read from the file only when asked for. A wrong or missing
/// <c>/Length</c> is repaired by looking for <c>endstream</c> and a diagnostic is recorded.
/// </summary>
public sealed class PdfStream : PdfObject
{
    private readonly PdfResolver _resolver;
    private readonly long _dataOffset;
    private StreamRange? _range;

    internal PdfStream(PdfDictionary dictionary, PdfResolver resolver, long dataOffset)
    {
        Dictionary = dictionary;
        _resolver = resolver;
        _dataOffset = dataOffset;
    }

    /// <summary>The stream dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>
    /// True when the data lives in an external file (<c>/F</c>). OnePDF never opens external files: the data reads as empty
    /// and a diagnostic is recorded.
    /// </summary>
    public bool IsExternal => Dictionary.ContainsKey("F");

    /// <summary>The names of the filters in the order they were applied when the stream was written (full names, abbreviations expanded).</summary>
    public IReadOnlyList<string> FilterNames
    {
        get
        {
            PdfObject? f = Dictionary.Get("Filter");
            if (f is PdfName n)
            {
                return [FilterSeam.Canonical(n.Value)];
            }

            if (f is PdfArray a)
            {
                var list = new List<string>(a.Count);
                for (int i = 0; i < a.Count; i++)
                {
                    if (a.GetName(i) is { } s)
                    {
                        list.Add(FilterSeam.Canonical(s));
                    }
                }

                return list;
            }

            return [];
        }
    }

    /// <summary>
    /// The first filter that <see cref="GetDecodedBytes"/> leaves undone (image codecs such as DCTDecode are handled by the
    /// image layer), or null when decoding is complete.
    /// </summary>
    public string? UndecodedFilter
    {
        get
        {
            foreach (string f in FilterNames)
            {
                if (!FilterSeam.CanDecode(f))
                {
                    return f;
                }
            }

            return null;
        }
    }

    internal PdfResolver Resolver => _resolver;

    /// <summary>The stream's bytes exactly as stored (still filtered). Empty for external streams. Do not modify.</summary>
    /// <exception cref="PdfLimitException">The stream is larger than <see cref="PdfLimits.MaxDecodedStreamBytes"/>.</exception>
    public ReadOnlyMemory<byte> GetRawBytes()
    {
        PdfDocument doc = _resolver.Doc;
        if (IsExternal)
        {
            doc.ReportWarning("Stream data is in an external file, which OnePDF never opens; read as empty.", Reference?.Number ?? -1, _dataOffset);
            return ReadOnlyMemory<byte>.Empty;
        }

        StreamRange r = _range ?? (_range = doc.ComputeStreamRange(this, _dataOffset));
        if (r.Length == 0)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        if (r.Length > doc.Limits.MaxDecodedStreamBytes || r.Length > Array.MaxLength)
        {
            throw new PdfLimitException($"A stream stores more than {doc.Limits.MaxDecodedStreamBytes} bytes.") { ObjectNumber = Reference?.Number ?? -1 };
        }

        return doc.ReadRange(r.Start, (int)r.Length);
    }

    /// <summary>
    /// The stream's bytes with filters applied, stopping before the first filter in <see cref="UndecodedFilter"/>.
    /// Damaged filter data yields the part that decoded, with a diagnostic. The result is shared and cached: do not modify.
    /// </summary>
    /// <exception cref="PdfLimitException">The decoded size or expansion ratio exceeds <see cref="PdfLimits"/>.</exception>
    public ReadOnlyMemory<byte> GetDecodedBytes(CancellationToken cancellationToken = default)
    {
        PdfDocument doc = _resolver.Doc;
        if (Reference is { } r && !IsExternal)
        {
            long key = ObjectKeys.Decoded(_resolver.View, r.Number);
            var state = (this, cancellationToken);
            var bytes = (byte[])doc.Cache.GetOrLoad(key, state, static s =>
            {
                byte[] d = s.Item1.DecodeCore(s.Item2);
                return (d, d.Length + 64);
            });
            return bytes;
        }

        return DecodeCore(cancellationToken);
    }

    /// <summary>Decodes without consulting the cache (used where the caller keeps its own copy).</summary>
    internal byte[] DecodeCore(CancellationToken ct)
    {
        PdfDocument doc = _resolver.Doc;
        ReadOnlyMemory<byte> raw = GetRawBytes();
        IReadOnlyList<string> filters = FilterNames;
        if (filters.Count == 0)
        {
            return raw.ToArray();
        }

        PdfObject? parms = Dictionary.Get("DecodeParms");
        ReadOnlyMemory<byte> data = raw;
        byte[]? current = null;
        int objNum = Reference?.Number ?? -1;
        for (int i = 0; i < filters.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!FilterSeam.CanDecode(filters[i]))
            {
                break;
            }

            PdfDictionary? p = parms switch
            {
                PdfDictionary d => i == 0 ? d : null,
                PdfArray a when i < a.Count => a[i] as PdfDictionary,
                _ => null,
            };
            current = FilterSeam.Decode(filters[i], data, p, doc.Limits, out string? problem);
            if (problem is not null)
            {
                doc.ReportWarning($"Filter /{filters[i]}: {problem}", objNum, _dataOffset);
            }

            data = current;
        }

        return current ?? raw.ToArray();
    }
}
