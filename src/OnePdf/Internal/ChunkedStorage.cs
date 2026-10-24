namespace OnePdf;

/// <summary>
/// An append-only list stored in chunks, so growing it never copies what is already there and never creates a
/// large-object-heap array. The first chunk starts small and doubles until it reaches the chunk size; after that each new
/// chunk has the full size. Indexing returns a reference. Not thread-safe for writing; concurrent reads of a finished list are safe.
/// </summary>
internal sealed class ChunkedList<T>
{
    private readonly int _shift;
    private readonly int _mask;
    private T[][] _chunks;
    private int _chunkCount;
    private int _count;

    public ChunkedList(int chunkShift, int initialCapacity)
    {
        _shift = chunkShift;
        _mask = (1 << chunkShift) - 1;
        _chunks = new T[4][];
        _chunks[0] = new T[Math.Min(initialCapacity, 1 << chunkShift)];
        _chunkCount = 1;
    }

    public int Count => _count;

    public ref T this[int index]
    {
        get
        {
            // Bounds are checked by the array accesses; an index past Count but inside a chunk reads default.
            return ref _chunks[index >> _shift][index & _mask];
        }
    }

    public int Add(in T value)
    {
        int chunk = _count >> _shift;
        int offset = _count & _mask;
        if (chunk == 0)
        {
            T[] first = _chunks[0];
            if (offset == first.Length)
            {
                // Still the single growing chunk: double it, up to the chunk size.
                Array.Resize(ref first, Math.Min(first.Length * 2, 1 << _shift));
                _chunks[0] = first;
            }
        }
        else if (chunk == _chunkCount)
        {
            if (_chunkCount == _chunks.Length)
            {
                Array.Resize(ref _chunks, _chunkCount * 2);
            }

            _chunks[_chunkCount++] = new T[1 << _shift];
        }

        // The first chunk reaching its full size is followed by a fresh chunk on the next call (chunk == _chunkCount).
        _chunks[chunk][offset] = value;
        return _count++;
    }
}

/// <summary>
/// Append-only storage for variable-length runs of elements that must stay contiguous (path verbs, coordinates, character
/// codes, glyphs). A run is addressed by (chunk, start). Chunks never move once a later chunk exists, and a run longer than
/// the chunk size gets a chunk of its own, so memory is never copied on growth beyond the first small chunk.
/// </summary>
internal sealed class ChunkArena<T>
{
    private readonly int _maxChunk;
    private readonly List<T[]> _chunks = [];
    private int _current = -1;
    private int _used;

    public ChunkArena(int maxChunk, int initialCapacity)
    {
        _maxChunk = maxChunk;
        _chunks.Add(new T[Math.Min(initialCapacity, maxChunk)]);
        _current = 0;
    }

    /// <summary>Reserves <paramref name="length"/> contiguous elements and returns them to be filled.</summary>
    public Span<T> Reserve(int length, out int chunk, out int start)
    {
        if (length > _maxChunk)
        {
            // Its own exactly-sized chunk; the current chunk keeps filling afterwards.
            var big = new T[length];
            _chunks.Add(big);
            chunk = _chunks.Count - 1;
            start = 0;
            return big;
        }

        T[] cur = _chunks[_current];
        if (_used + length > cur.Length)
        {
            if (_current == 0 && cur.Length < _maxChunk && _used + length <= _maxChunk)
            {
                // The first chunk is still growing: double it (at most one small copy per doubling).
                int size = Math.Min(Math.Max(cur.Length * 2, _used + length), _maxChunk);
                Array.Resize(ref cur, size);
                _chunks[0] = cur;
            }
            else
            {
                cur = new T[_maxChunk];
                _chunks.Add(cur);
                _current = _chunks.Count - 1;
                _used = 0;
            }
        }

        chunk = _current;
        start = _used;
        _used += length;
        return cur.AsSpan(start, length);
    }

    public ReadOnlyMemory<T> Memory(int chunk, int start, int length) => _chunks[chunk].AsMemory(start, length);
}
