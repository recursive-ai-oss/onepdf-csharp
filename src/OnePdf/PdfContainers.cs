using OnePdf.Internal;

namespace OnePdf;

/// <summary>
/// A dictionary. Getters resolve indirect references and treat null values as absent; <see cref="GetRaw"/> returns
/// the stored object, an unresolved <see cref="PdfReference"/> included.
/// </summary>
public sealed class PdfDictionary : PdfObject
{
    // Most dictionaries have a handful of entries, so they are two parallel arrays searched linearly (keys are mostly
    // interned names, so the reference check usually settles it); a dictionary of more than IndexThreshold entries also
    // gets a hash index. Entries keep the order they were set in; setting an existing key replaces its value in place.
    private const int IndexThreshold = 8;
    private string[] _keys = [];
    private PdfObject[] _vals = [];
    private int _n;
    private Dictionary<string, int>? _index;
    private readonly PdfResolver? _resolver;

    internal PdfDictionary(PdfResolver? resolver) => _resolver = resolver;

    /// <summary>Number of entries.</summary>
    public int Count => _n;

    /// <summary>The keys, without the leading slash, in the order they were read.</summary>
    public IEnumerable<string> Keys
    {
        get
        {
            for (int i = 0; i < _n; i++)
            {
                yield return _keys[i];
            }
        }
    }

    /// <summary>The raw entries (references unresolved), in the order they were read.</summary>
    public IEnumerable<KeyValuePair<string, PdfObject>> Entries
    {
        get
        {
            for (int i = 0; i < _n; i++)
            {
                yield return new KeyValuePair<string, PdfObject>(_keys[i], _vals[i]);
            }
        }
    }

    private int IndexOfKey(string key)
    {
        if (_index is not null)
        {
            return _index.TryGetValue(key, out int at) ? at : -1;
        }

        string[] keys = _keys;
        for (int i = 0; i < _n; i++)
        {
            if ((object)keys[i] == key)
            {
                return i;
            }
        }

        for (int i = 0; i < _n; i++)
        {
            if (string.Equals(keys[i], key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The resolved value for <paramref name="key"/>, or null when absent or null.</summary>
    public PdfObject? this[string key] => Get(key);

    internal void Set(string key, PdfObject value)
    {
        int at = IndexOfKey(key);
        if (at >= 0)
        {
            _vals[at] = value;
            return;
        }

        if (_n == _keys.Length)
        {
            int size = _n == 0 ? 6 : _n * 2;
            Array.Resize(ref _keys, size);
            Array.Resize(ref _vals, size);
        }

        _keys[_n] = key;
        _vals[_n] = value;
        _index?.Add(key, _n);
        _n++;
        if (_index is null && _n > IndexThreshold)
        {
            _index = new Dictionary<string, int>(_n * 2, StringComparer.Ordinal);
            for (int i = 0; i < _n; i++)
            {
                _index[_keys[i]] = i;
            }
        }
    }

    /// <summary>True when the dictionary has an entry for <paramref name="key"/>, even a null one.</summary>
    public bool ContainsKey(string key) => IndexOfKey(key) >= 0;

    /// <summary>The stored object for <paramref name="key"/> without following references, or null when absent.</summary>
    public PdfObject? GetRaw(string key)
    {
        int at = IndexOfKey(key);
        return at < 0 ? null : _vals[at];
    }

    /// <summary>The value for <paramref name="key"/> with references followed, or null when absent or null.</summary>
    public PdfObject? Get(string key)
    {
        int at = IndexOfKey(key);
        return at < 0 ? null : Unwrap(_vals[at], _resolver);
    }

    /// <summary>An integer value (a real is truncated), or <paramref name="defaultValue"/>.</summary>
    public long GetInt(string key, long defaultValue = 0) => Get(key) switch
    {
        PdfInteger i => i.Value,
        PdfReal r when double.IsFinite(r.Value) && Math.Abs(r.Value) < 9e18 => (long)r.Value,
        _ => defaultValue,
    };

    /// <summary>A numeric value, or <paramref name="defaultValue"/>.</summary>
    public double GetDouble(string key, double defaultValue = 0) => Get(key) switch
    {
        PdfInteger i => i.Value,
        PdfReal r => r.Value,
        _ => defaultValue,
    };

    /// <summary>A boolean value, or <paramref name="defaultValue"/>.</summary>
    public bool GetBool(string key, bool defaultValue = false) => Get(key) is PdfBoolean b ? b.Value : defaultValue;

    /// <summary>A name value without the slash, or null.</summary>
    public string? GetName(string key) => Get(key) is PdfName n ? n.Value : null;

    /// <summary>A string value, or null.</summary>
    public PdfString? GetString(string key) => Get(key) as PdfString;

    /// <summary>An array value, or null.</summary>
    public PdfArray? GetArray(string key) => Get(key) as PdfArray;

    /// <summary>A dictionary value; for a stream, its dictionary. Null otherwise.</summary>
    public PdfDictionary? GetDictionary(string key) => Get(key) switch
    {
        PdfDictionary d => d,
        PdfStream s => s.Dictionary,
        _ => null,
    };

    /// <summary>A stream value, or null.</summary>
    public PdfStream? GetStream(string key) => Get(key) as PdfStream;
}

/// <summary>An array. Indexers and getters resolve indirect references; <see cref="GetRaw"/> does not.</summary>
public sealed class PdfArray : PdfObject
{
    private readonly List<PdfObject> _items = [];
    private readonly PdfResolver? _resolver;

    internal PdfArray(PdfResolver? resolver) => _resolver = resolver;

    /// <summary>Number of elements.</summary>
    public int Count => _items.Count;

    /// <summary>The resolved element, or null when it is null.</summary>
    public PdfObject? this[int index] => Unwrap(_items[index], _resolver);

    internal void Add(PdfObject value) => _items.Add(value);

    /// <summary>The stored element without following references.</summary>
    public PdfObject GetRaw(int index) => _items[index];

    /// <summary>The elements with references followed (null for null).</summary>
    public IEnumerable<PdfObject?> Items
    {
        get
        {
            for (int i = 0; i < _items.Count; i++)
            {
                yield return Unwrap(_items[i], _resolver);
            }
        }
    }

    /// <summary>An integer element (a real is truncated), or <paramref name="defaultValue"/>.</summary>
    public long GetInt(int index, long defaultValue = 0) => this[index] switch
    {
        PdfInteger i => i.Value,
        PdfReal r when double.IsFinite(r.Value) && Math.Abs(r.Value) < 9e18 => (long)r.Value,
        _ => defaultValue,
    };

    /// <summary>A numeric element, or <paramref name="defaultValue"/>.</summary>
    public double GetDouble(int index, double defaultValue = 0) => this[index] switch
    {
        PdfInteger i => i.Value,
        PdfReal r => r.Value,
        _ => defaultValue,
    };

    /// <summary>A name element without the slash, or null.</summary>
    public string? GetName(int index) => this[index] is PdfName n ? n.Value : null;
}
