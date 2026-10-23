using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using OnePdf.Internal;

namespace OnePdf;

/// <summary>
/// An open PDF document. Reading is lazy and thread-safe: any number of threads may resolve objects at once. The
/// document holds its source until disposed; a document that is forgotten releases a file handle through the handle's
/// own finalizer.
/// </summary>
/// <remarks>
/// Damage is repaired where the intent is unambiguous and each repair is recorded in <see cref="Diagnostics"/>; see
/// <c>docs/repair.md</c> for the rules. With <see cref="PdfOpenOptions.Strict"/> a repair throws instead.
/// </remarks>
public sealed partial class PdfDocument : IDisposable
{
    private readonly IPdfSource _source;
    private readonly DiagnosticBag _diag;
    private readonly PdfOpenOptions _options;
    private readonly Lazy<int> _pageCount;
    private int _disposed;

    private PdfDocument(IPdfSource source, PdfOpenOptions options)
    {
        _source = source;
        _options = options;
        Limits = options.Limits;
        _diag = new DiagnosticBag(options.Strict);
        Cache = new ObjectCache(Math.Max(0, options.ObjectCacheBytes));
        _scan = new Lazy<ScanResult>(ScanFile, LazyThreadSafetyMode.ExecutionAndPublication);
        _pageCount = new Lazy<int>(CountPagesLean, LazyThreadSafetyMode.ExecutionAndPublication);
        Initialize();
    }

    /// <summary>The bounds in force for this document.</summary>
    public PdfLimits Limits { get; }

    /// <summary>The PDF version from the file header (for example <c>1.7</c>), or null when the header is missing.</summary>
    public string? Version { get; private set; }

    /// <summary>
    /// The trailer of the newest revision. Keys missing from it that older revisions have (<c>Root</c>, <c>Info</c>,
    /// <c>ID</c>, <c>Encrypt</c>) are filled in.
    /// </summary>
    public PdfDictionary Trailer { get; private set; } = null!;

    /// <summary>The document catalog (<c>/Root</c> of the trailer).</summary>
    public PdfDictionary Catalog => Trailer.GetDictionary("Root") ?? throw new PdfFormatException("The document has no catalog.");

    /// <summary>True when the trailer has an <c>/Encrypt</c> entry. Strings and streams of such a file are decrypted transparently; see <see cref="PdfOpenOptions.Password"/>.</summary>
    public bool IsEncrypted => Trailer.ContainsKey("Encrypt");

    /// <summary>Repairs and irregularities found so far. Grows as objects are read lazily; each call returns a snapshot.</summary>
    public IReadOnlyList<PdfDiagnostic> Diagnostics => _diag.Snapshot();

    /// <summary>
    /// The revisions of the file, oldest first: the original and each incremental update. A file that had to be rebuilt
    /// by scanning, and a file with a single section, has one. A linearized file is read as an ordinary file, so its
    /// first-page section counts as a revision of its own.
    /// </summary>
    public IReadOnlyList<PdfRevision> Revisions { get; private set; } = [];

    /// <summary>One more than the highest object number the cross-reference data knows.</summary>
    public int ObjectCount { get; private set; }

    /// <summary>
    /// The number of pages found by walking <c>/Pages</c> from the catalog. Cycles and non-dictionary children are skipped
    /// with a diagnostic. Computed once.
    /// </summary>
    public int PageCount => _pageCount.Value;

    internal ObjectCache Cache { get; private set; }

    internal long FileLength => _source.Length;

    /// <summary>Opens a document from a file. The file is opened once and read by position.</summary>
    /// <exception cref="PdfFormatException">The file is not a PDF or is too damaged to repair (any repair in strict mode).</exception>
    /// <exception cref="PdfLimitException">A bound in <see cref="PdfOpenOptions.Limits"/> was exceeded.</exception>
    public static PdfDocument Open(string path, PdfOpenOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Create(new FileSource(path), options);
    }

    /// <summary>
    /// Opens a document from a stream. A seekable stream is used in place and must not be touched elsewhere while the
    /// document is open (reads are serialized). A non-seekable stream is copied into memory first. The stream is
    /// closed on disposal unless <see cref="PdfOpenOptions.LeaveOpen"/> is set.
    /// </summary>
    public static PdfDocument Open(Stream stream, PdfOpenOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new PdfOpenOptions();
        if (stream.CanSeek)
        {
            return Create(new StreamSource(stream, options.LeaveOpen), options);
        }

        MemorySource copy = CopyToMemory(stream);
        CloseUnlessLeftOpen(stream, options);
        return Create(copy, options);
    }

    /// <summary>Opens a document from memory. The memory is referenced, not copied, and must stay unchanged while the document is open.</summary>
    public static PdfDocument Open(ReadOnlyMemory<byte> data, PdfOpenOptions? options = null) => Create(new MemorySource(data), options);

    /// <summary>
    /// Opens a document from a stream, reading a non-seekable stream asynchronously into memory. For a seekable stream
    /// the work after the call is the same as <see cref="Open(Stream, PdfOpenOptions?)"/>.
    /// </summary>
    public static async Task<PdfDocument> OpenAsync(Stream stream, PdfOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new PdfOpenOptions();
        if (stream.CanSeek)
        {
            return Open(stream, options);
        }

        MemorySource copy = await CopyToMemoryAsync(stream, cancellationToken).ConfigureAwait(false);
        CloseUnlessLeftOpen(stream, options);
        return Create(copy, options);
    }

    /// <summary>Like <see cref="Open(string, PdfOpenOptions?)"/> but returns false instead of throwing for unreadable documents. I/O errors still throw.</summary>
    public static bool TryOpen(string path, [NotNullWhen(true)] out PdfDocument? document, [NotNullWhen(false)] out PdfException? error, PdfOpenOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return TryCreate(() => Open(path, options), out document, out error);
    }

    /// <summary>Like <see cref="Open(Stream, PdfOpenOptions?)"/> but returns false instead of throwing for unreadable documents.</summary>
    public static bool TryOpen(Stream stream, [NotNullWhen(true)] out PdfDocument? document, [NotNullWhen(false)] out PdfException? error, PdfOpenOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return TryCreate(() => Open(stream, options), out document, out error);
    }

    /// <summary>Like <see cref="Open(ReadOnlyMemory{byte}, PdfOpenOptions?)"/> but returns false instead of throwing for unreadable documents.</summary>
    public static bool TryOpen(ReadOnlyMemory<byte> data, [NotNullWhen(true)] out PdfDocument? document, [NotNullWhen(false)] out PdfException? error, PdfOpenOptions? options = null) =>
        TryCreate(() => Open(data, options), out document, out error);

    private static bool TryCreate(Func<PdfDocument> open, [NotNullWhen(true)] out PdfDocument? document, [NotNullWhen(false)] out PdfException? error)
    {
        try
        {
            document = open();
            error = null;
            return true;
        }
        catch (PdfFormatException e)
        {
            document = null;
            error = e;
        }
        catch (PdfLimitException e)
        {
            document = null;
            error = e;
        }
        catch (PdfPasswordException e)
        {
            document = null;
            error = e;
        }
        catch (PdfEncryptionException e)
        {
            document = null;
            error = e;
        }

        return false;
    }

    private static PdfDocument Create(IPdfSource source, PdfOpenOptions? options)
    {
        bool ok = false;
        try
        {
            var doc = new PdfDocument(source, options ?? new PdfOpenOptions());
            ok = true;
            return doc;
        }
        finally
        {
            if (!ok)
            {
                source.Dispose();
            }
        }
    }

    private static void CloseUnlessLeftOpen(Stream s, PdfOpenOptions o)
    {
        if (!o.LeaveOpen)
        {
            s.Dispose();
        }
    }

    private static MemorySource CopyToMemory(Stream stream)
    {
        byte[] buf = ArrayPool<byte>.Shared.Rent(1 << 16);
        int n = 0;
        try
        {
            while (true)
            {
                if (n == buf.Length)
                {
                    buf = Grow(buf, n);
                }

                int got = stream.Read(buf, n, buf.Length - n);
                if (got <= 0)
                {
                    break;
                }

                n += got;
            }

            var src = new MemorySource(buf, n);
            buf = null!;
            return src;
        }
        finally
        {
            if (buf is not null)
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }
    }

    private static async Task<MemorySource> CopyToMemoryAsync(Stream stream, CancellationToken ct)
    {
        byte[] buf = ArrayPool<byte>.Shared.Rent(1 << 16);
        int n = 0;
        try
        {
            while (true)
            {
                if (n == buf.Length)
                {
                    buf = Grow(buf, n);
                }

                int got = await stream.ReadAsync(buf.AsMemory(n), ct).ConfigureAwait(false);
                if (got <= 0)
                {
                    break;
                }

                n += got;
            }

            var src = new MemorySource(buf, n);
            buf = null!;
            return src;
        }
        finally
        {
            if (buf is not null)
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }
    }

    private static byte[] Grow(byte[] buf, int used)
    {
        if (buf.Length >= Array.MaxLength)
        {
            throw new PdfLimitException("The document is larger than a .NET array can hold.");
        }

        byte[] bigger = ArrayPool<byte>.Shared.Rent((int)Math.Min(Array.MaxLength, (long)buf.Length * 2));
        buf.AsSpan(0, used).CopyTo(bigger);
        ArrayPool<byte>.Shared.Return(buf);
        return bigger;
    }

    /// <summary>
    /// The indirect object with this number in the newest revision, or null when it does not exist, is free or is null.
    /// </summary>
    public PdfObject? GetObject(int objectNumber)
    {
        PdfObject o = ResolveChain(objectNumber, 0);
        return o is PdfNull ? null : o;
    }

    /// <summary>Follows an indirect reference in the newest revision; any other object is returned as is. Null for a dangling reference or null.</summary>
    public PdfObject? Resolve(PdfObject? obj)
    {
        if (obj is PdfReference r)
        {
            obj = ResolveReference(r, 0);
        }

        return obj is PdfNull ? null : obj;
    }

    /// <summary>Releases the source (closing the file or, unless left open, the stream).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _source.Dispose();
            _decryptor?.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    internal void ReportWarning(string message, int objectNumber, long offset) => _diag.Warn(message, objectNumber, offset);

    /// <summary>Counts the pages of a page tree (the same walk as <see cref="Pages"/>).</summary>
    internal int CountPages(PdfDictionary? catalog, string scope) => WalkPages(catalog, scope).Count;
}
