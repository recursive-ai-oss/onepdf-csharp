using System.Buffers;

namespace OnePdf.Internal;

/// <summary>
/// Positional, thread-safe random access to the bytes of a document. Async-capable so that range sources can be added
/// without changing callers.
/// </summary>
internal interface IPdfSource : IDisposable
{
    /// <summary>Total length in bytes, fixed when the source was opened.</summary>
    long Length { get; }

    /// <summary>Reads up to <c>destination.Length</c> bytes at <paramref name="position"/>; returns fewer only at the end of the data.</summary>
    int Read(long position, Span<byte> destination);

    /// <summary>Asynchronous form of <see cref="Read"/>.</summary>
    ValueTask<int> ReadAsync(long position, Memory<byte> destination, CancellationToken cancellationToken);

    /// <summary>The whole content when it is already in memory.</summary>
    bool TryGetMemory(out ReadOnlyMemory<byte> memory);

    /// <summary>Throws <see cref="PdfSourceChangedException"/> when the underlying file or stream no longer matches what was recorded at open.</summary>
    void EnsureUnchanged();
}

/// <summary>A caller-supplied or pooled in-memory buffer.</summary>
internal sealed class MemorySource : IPdfSource
{
    private readonly ReadOnlyMemory<byte> _data;
    private byte[]? _pooled;

    public MemorySource(ReadOnlyMemory<byte> data) => _data = data;

    /// <summary>Takes ownership of a buffer rented from <see cref="ArrayPool{T}.Shared"/>.</summary>
    public MemorySource(byte[] pooled, int length)
    {
        _pooled = pooled;
        _data = pooled.AsMemory(0, length);
    }

    public long Length => _data.Length;

    public int Read(long position, Span<byte> destination)
    {
        if (position < 0 || position >= _data.Length)
        {
            return 0;
        }

        ReadOnlySpan<byte> src = _data.Span.Slice((int)position);
        int n = Math.Min(src.Length, destination.Length);
        src[..n].CopyTo(destination);
        return n;
    }

    public ValueTask<int> ReadAsync(long position, Memory<byte> destination, CancellationToken cancellationToken) =>
        new(Read(position, destination.Span));

    public bool TryGetMemory(out ReadOnlyMemory<byte> memory)
    {
        memory = _data;
        return true;
    }

    public void EnsureUnchanged()
    {
    }

    public void Dispose()
    {
        byte[]? p = Interlocked.Exchange(ref _pooled, null);
        if (p is not null)
        {
            ArrayPool<byte>.Shared.Return(p);
        }
    }
}

/// <summary>A file read with positional reads; any number of threads may read at once.</summary>
internal sealed class FileSource : IPdfSource
{
    private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _handle;
    private readonly long _length;
    private readonly DateTime _lastWriteUtc;
    private const long MtimeCheckMilliseconds = 100;

    // A file this small is read whole at open: parsing then runs over memory with one read instead of a dozen small positional ones.
    private const int SnapshotLimit = 64 * 1024;
    private readonly byte[]? _snapshot;
    private long _lastMtimeCheck;

    public FileSource(string path)
    {
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
        _length = RandomAccess.GetLength(_handle);
        _lastWriteUtc = File.GetLastWriteTimeUtc(_handle);
        _lastMtimeCheck = Environment.TickCount64;
        if (_length is > 0 and <= SnapshotLimit)
        {
            // A snapshot has its own array (not a pooled one): streams and slices handed to callers stay valid after the document is disposed.
            byte[] all = new byte[_length];
            int total = 0;
            while (total < all.Length)
            {
                int n = RandomAccess.Read(_handle, all.AsSpan(total), total);
                if (n <= 0)
                {
                    break;
                }

                total += n;
            }

            // A file that shrank while it was read falls back to positional reads, which report the change.
            _snapshot = total == all.Length ? all : null;
        }
    }

    public long Length => _length;

    public int Read(long position, Span<byte> destination)
    {
        if (_snapshot is { } snap)
        {
            if (position < 0 || position >= snap.Length)
            {
                return 0;
            }

            int count = Math.Min(snap.Length - (int)position, destination.Length);
            snap.AsSpan((int)position, count).CopyTo(destination);
            return count;
        }

        int total = 0;
        while (total < destination.Length)
        {
            int n = RandomAccess.Read(_handle, destination[total..], position + total);
            if (n <= 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    public async ValueTask<int> ReadAsync(long position, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (_snapshot is not null)
        {
            return Read(position, destination.Span);
        }

        int total = 0;
        while (total < destination.Length)
        {
            int n = await RandomAccess.ReadAsync(_handle, destination[total..], position + total, cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    public bool TryGetMemory(out ReadOnlyMemory<byte> memory)
    {
        memory = _snapshot;
        return _snapshot is not null;
    }

    public void EnsureUnchanged()
    {
        // A length change is caught on every call (one cheap query on the open handle). The modification time is
        // re-read at most every MtimeCheckMilliseconds: a same-size rewrite is noticed within that time. Both are read from the
        // open handle, so a file replaced by renaming another over it is not a change: this handle still shows the file that was opened.
        bool changed = RandomAccess.GetLength(_handle) != _length;
        long now = Environment.TickCount64;
        if (!changed && now - Volatile.Read(ref _lastMtimeCheck) >= MtimeCheckMilliseconds)
        {
            changed = File.GetLastWriteTimeUtc(_handle) != _lastWriteUtc;
            Volatile.Write(ref _lastMtimeCheck, now);
        }

        if (changed)
        {
            throw new PdfSourceChangedException("The file changed after the document was opened.");
        }
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>A seekable stream used in place; reads are serialized.</summary>
internal sealed class StreamSource : IPdfSource
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly long _length;

    public StreamSource(Stream stream, bool leaveOpen)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _length = stream.Length;
    }

    public long Length => _length;

    public int Read(long position, Span<byte> destination)
    {
        _gate.Wait();
        try
        {
            CheckLength();
            _stream.Position = position;
            int total = 0;
            while (total < destination.Length)
            {
                int n = _stream.Read(destination[total..]);
                if (n <= 0)
                {
                    break;
                }

                total += n;
            }

            return total;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<int> ReadAsync(long position, Memory<byte> destination, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CheckLength();
            _stream.Position = position;
            int total = 0;
            while (total < destination.Length)
            {
                int n = await _stream.ReadAsync(destination[total..], cancellationToken).ConfigureAwait(false);
                if (n <= 0)
                {
                    break;
                }

                total += n;
            }

            return total;
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool TryGetMemory(out ReadOnlyMemory<byte> memory)
    {
        memory = default;
        return false;
    }

    public void EnsureUnchanged()
    {
        _gate.Wait();
        try
        {
            CheckLength();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void CheckLength()
    {
        if (_stream.Length != _length)
        {
            throw new PdfSourceChangedException("The stream changed length after the document was opened.");
        }
    }

    public void Dispose()
    {
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }

        _gate.Dispose();
    }
}
