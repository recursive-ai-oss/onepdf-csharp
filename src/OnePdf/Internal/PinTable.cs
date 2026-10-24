namespace OnePdf.Internal;

/// <summary>
/// The objects an editable document keeps by number. A plain dictionary behind a lock: <c>Dictionary&lt;int, reference type&gt;</c> is
/// precompiled in the runtime, while <c>ConcurrentDictionary</c> over the same types is compiled by the JIT on first use, which is a
/// visible part of a first document's start-up. Operations are short, so the lock is not contended in practice.
/// </summary>
internal sealed class PinTable
{
    private readonly Dictionary<int, PdfObject> _map = [];

    public bool TryGetValue(int number, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PdfObject? value)
    {
        lock (_map)
        {
            return _map.TryGetValue(number, out value);
        }
    }

    public PdfObject this[int number]
    {
        set
        {
            lock (_map)
            {
                _map[number] = value;
            }
        }
    }

    public bool TryRemove(int number, out PdfObject? value)
    {
        lock (_map)
        {
            return _map.Remove(number, out value);
        }
    }

    /// <summary>Adds <paramref name="value"/> unless the number is taken; returns what the table holds afterwards.</summary>
    public PdfObject GetOrAdd(int number, PdfObject value)
    {
        lock (_map)
        {
            if (_map.TryGetValue(number, out PdfObject? existing))
            {
                return existing;
            }

            _map[number] = value;
            return value;
        }
    }
}
