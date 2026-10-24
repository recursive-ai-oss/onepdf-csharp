using System.Linq;
using OnePdf.Filters;
using OnePdf.Internal;
using OnePdf.Security;

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

    // Data set by the caller (or of a stream that was created in memory): stored form, never encrypted, never read from the file.
    private byte[]? _memory;

    internal PdfStream(PdfDictionary dictionary, PdfResolver resolver, long dataOffset)
    {
        Dictionary = dictionary;
        _resolver = resolver;
        _dataOffset = dataOffset;
    }

    /// <summary>True when the stored bytes were set in memory rather than read from the file.</summary>
    internal bool HasMemoryData => _memory is not null;

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
                return [Canonical(n.Value)];
            }

            if (f is PdfArray a)
            {
                var list = new List<string>(a.Count);
                for (int i = 0; i < a.Count; i++)
                {
                    if (a.GetName(i) is { } s)
                    {
                        list.Add(Canonical(s));
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
                if (f != "Crypt" && !StreamFilters.IsSupported(f))
                {
                    return f;
                }
            }

            return null;
        }
    }

    internal PdfResolver Resolver => _resolver;

    /// <summary>Maps the inline-image abbreviations to full filter names.</summary>
    private static string Canonical(string name) => name switch
    {
        "Fl" => "FlateDecode",
        "AHx" => "ASCIIHexDecode",
        "A85" => "ASCII85Decode",
        "LZW" => "LZWDecode",
        "RL" => "RunLengthDecode",
        "CCF" => "CCITTFaxDecode",
        "DCT" => "DCTDecode",
        _ => name,
    };

    /// <summary>The stream's bytes exactly as stored (still filtered). Empty for external streams. Do not modify.</summary>
    /// <exception cref="PdfLimitException">The stream is larger than <see cref="PdfLimits.MaxDecodedStreamBytes"/>.</exception>
    public ReadOnlyMemory<byte> GetRawBytes()
    {
        if (_memory is not null)
        {
            return _memory;
        }

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
    public ReadOnlyMemory<byte> GetDecodedBytes(CancellationToken cancellationToken = default) => GetDecodedBytesSized(0, cancellationToken);

    /// <summary>
    /// As <see cref="GetDecodedBytes"/>, with the expected size of the fully decoded data (0 when unknown) so a large Flate or LZW
    /// result is allocated once. A wrong expectation changes nothing about the result.
    /// </summary>
    internal ReadOnlyMemory<byte> GetDecodedBytesSized(long expectedSize, CancellationToken cancellationToken)
    {
        PdfDocument doc = _resolver.Doc;
        if (Reference is { } r && !IsExternal && _memory is null)
        {
            long key = ObjectKeys.Decoded(_resolver.View, r.Number);
            var state = (this, cancellationToken, expectedSize);
            return (ReadOnlyMemory<byte>)doc.Cache.GetOrLoad(key, state, static s =>
            {
                ReadOnlyMemory<byte> d = s.Item1.DecodeCore(s.Item2, s.Item3);
                return (d, d.Length + 64);
            });
        }

        return DecodeCore(cancellationToken, expectedSize);
    }

    /// <summary>The stored (still filtered) bytes with the document's encryption removed.</summary>
    internal ReadOnlyMemory<byte> GetStoredBytes(IReadOnlyList<string> filters) => GetStoredBytes(filters, out _);

    /// <summary>As above; <paramref name="issues"/> reports damaged ciphertext (the result is then only what decrypted).</summary>
    internal ReadOnlyMemory<byte> GetStoredBytes(IReadOnlyList<string> filters, out DecryptIssues issues)
    {
        issues = DecryptIssues.None;
        ReadOnlyMemory<byte> raw = GetRawBytes();
        if (raw.Length > 0 && _memory is null && Reference is { } self && !IsExternal)
        {
            raw = _resolver.Doc.DecryptStreamData(this, raw, self, filters, out issues);
        }

        return raw;
    }

    /// <summary>Decodes without consulting the cache (used where the caller keeps its own copy).</summary>
    internal ReadOnlyMemory<byte> DecodeCore(CancellationToken ct, long expectedSize = 0)
    {
        PdfDocument doc = _resolver.Doc;
        IReadOnlyList<string> filters = FilterNames;
        ReadOnlyMemory<byte> raw = GetStoredBytes(filters);
        FilterResult r = DecodeStored(raw, filters, expectedSize, ct);
        int objNum = Reference?.Number ?? -1;
        if ((r.Issues & FilterIssues.LimitExceeded) != 0)
        {
            throw new PdfLimitException(r.Message ?? "A stream decodes to more than the configured limits allow.") { ObjectNumber = objNum };
        }

        // Stopping at an image codec is not damage; everything else is a repair worth recording.
        if ((r.Issues & ~FilterIssues.UnsupportedFilter) != 0)
        {
            doc.ReportWarning(r.Message ?? "The stream data is damaged.", objNum, -1);
        }

        return r.Data;
    }

    /// <summary>Runs the filter chain over already decrypted stored bytes; no diagnostics, no exceptions for damage or limits (they are in the result).</summary>
    private FilterResult DecodeStored(ReadOnlyMemory<byte> raw, IReadOnlyList<string> filters, long expectedSize, CancellationToken ct)
    {
        PdfDocument doc = _resolver.Doc;
        if (filters.Count == 0)
        {
            return new FilterResult(raw, FilterIssues.None, null, -1);
        }

        FilterParameters?[] parameters = FilterParametersReader.ForChain(Dictionary.Get("DecodeParms"), filters);

        PdfLimits lim = doc.Limits;
        var limits = new DecodeLimits(Math.Min(lim.MaxDecodedStreamBytes, Array.MaxLength), lim.MaxExpansionRatio, lim.ExpansionCheckThreshold, DiscardOnLimit: true);
        if (filters.Contains("Crypt"))
        {
            // The Crypt filter only names the decryption that was just applied; it is not part of the decode chain.
            var keptNames = new List<string>(filters.Count);
            var keptParams = new List<FilterParameters?>(filters.Count);
            for (int i = 0; i < filters.Count; i++)
            {
                if (filters[i] != "Crypt")
                {
                    keptNames.Add(filters[i]);
                    keptParams.Add(parameters[i]);
                }
            }

            if (keptNames.Count == 0)
            {
                return new FilterResult(raw, FilterIssues.None, null, -1);
            }

            filters = keptNames;
            parameters = [.. keptParams];
        }

        return StreamFilters.DecodeChain(filters, parameters, raw, limits, expectedSize, ct);
    }

    /// <summary>
    /// Reads and decodes the stream the way a save and an interpreter would, and says why the content cannot be relied on: unreadable or
    /// undecryptable bytes, a limit, or filter data that is cut short or invalid. Returns null when the stream is sound. Repairs that lose
    /// nothing (a wrong <c>/Length</c> found by scanning, a missing or wrong checksum, a missing end marker, trailing bytes) are not problems.
    /// External streams are not read.
    /// </summary>
    internal string? FindUnreadableReason(CancellationToken ct)
    {
        if (IsExternal)
        {
            return null;
        }

        IReadOnlyList<string> filters = FilterNames;
        ReadOnlyMemory<byte> stored;
        DecryptIssues dec;
        try
        {
            stored = GetStoredBytes(filters, out dec);
        }
        catch (PdfException ex)
        {
            return "its stored bytes cannot be read: " + ex.Message;
        }

        if (dec != DecryptIssues.None)
        {
            return $"it cannot be decrypted cleanly ({dec})";
        }

        FilterResult r = DecodeStored(stored, filters, 0, ct);
        const FilterIssues Lossy = FilterIssues.Truncated | FilterIssues.DataError | FilterIssues.LimitExceeded | FilterIssues.BadParameters;
        if ((r.Issues & Lossy) != 0)
        {
            return "its data cannot be decoded: " + (r.Message ?? r.Issues.ToString());
        }

        return null;
    }

    /// <summary>
    /// Replaces the stored bytes with <paramref name="stored"/> exactly as given (a copy is kept). The caller keeps <c>/Filter</c> and
    /// <c>/DecodeParms</c> in step with them; <c>/Length</c> is set. Encryption is applied when the document is saved.
    /// </summary>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>, or the stream is from an earlier revision.</exception>
    public void SetStoredBytes(ReadOnlySpan<byte> stored)
    {
        EditSupport.EnsureEditable(_resolver);
        _memory = stored.ToArray();
        _range = null;
        Dictionary.RemoveEntry("F");
        Dictionary.SetEntry("Length", new PdfInteger(_memory.Length));
        EditSupport.MarkDirty(_resolver, OwnerPlusOne - 1);
    }

    /// <summary>
    /// Replaces the stream's content with <paramref name="data"/>, unfiltered, or Flate-compressed when <paramref name="compress"/> is
    /// true; <c>/Filter</c> and <c>/DecodeParms</c> are rewritten to match.
    /// </summary>
    /// <exception cref="InvalidOperationException">See <see cref="SetStoredBytes"/>.</exception>
    public void SetData(ReadOnlySpan<byte> data, bool compress = false)
    {
        EditSupport.EnsureEditable(_resolver);
        Dictionary.RemoveEntry("DecodeParms");
        if (compress && data.Length > 0)
        {
            Dictionary.SetEntry("Filter", new PdfName("FlateDecode"));
            SetStoredBytes(Writing.FlateWriter.Compress(data));
        }
        else
        {
            Dictionary.RemoveEntry("Filter");
            SetStoredBytes(data);
        }
    }
}
