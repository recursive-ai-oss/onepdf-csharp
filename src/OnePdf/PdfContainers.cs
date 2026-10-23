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
    private PdfResolver? _resolver;

    internal PdfDictionary(PdfResolver? resolver) => _resolver = resolver;

    /// <summary>
    /// Creates an empty dictionary that belongs to no document yet. References stored in it do not resolve (getters return
    /// null for them) until it is added to a document, directly with <see cref="PdfDocument.AddObject"/> or by being set into
    /// an object that already is.
    /// </summary>
    public PdfDictionary()
    {
    }

    internal PdfResolver? Resolver
    {
        get => _resolver;
        set => _resolver = value;
    }

    internal int RawCount => _n;

    internal void SetLengthInternal(long length) => Set("Length", new PdfInteger(length));

    /// <summary>Removes an entry without the checks of <see cref="RemoveEntry"/> (for dictionaries the writer builds itself).</summary>
    internal void Remove(string key)
    {
        int at = IndexOfKey(key);
        if (at < 0)
        {
            return;
        }

        for (int i = at; i < _n - 1; i++)
        {
            _keys[i] = _keys[i + 1];
            _vals[i] = _vals[i + 1];
        }

        _n--;
        _index = null;
        if (_n > IndexThreshold)
        {
            _index = new Dictionary<string, int>(_n * 2, StringComparer.Ordinal);
            for (int i = 0; i < _n; i++)
            {
                _index[_keys[i]] = i;
            }
        }
    }

    internal string KeyAt(int i) => _keys[i];

    internal PdfObject ValueAt(int i) => _vals[i];

    /// <summary>
    /// Sets <paramref name="key"/> to <paramref name="value"/>, replacing an existing entry in place; null (or the null object)
    /// removes the entry. An object that is already an indirect object of a document is stored as a reference to it; a stream
    /// that is not yet part of a document is added to this dictionary's document and stored as a reference.
    /// </summary>
    /// <exception cref="InvalidOperationException">The dictionary was read from a document that was not opened with <see cref="PdfOpenOptions.AllowEditing"/>, or from an earlier revision.</exception>
    /// <exception cref="ArgumentException">The value belongs to another document, or is a stream that cannot be added.</exception>
    public void SetEntry(string key, PdfObject? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        EditSupport.EnsureEditable(_resolver);
        if (value is null or PdfNull)
        {
            RemoveEntry(key);
            return;
        }

        value = EditSupport.Prepare(value, _resolver, OwnerPlusOne - 1);
        Set(key, value);
        EditSupport.MarkDirty(_resolver, OwnerPlusOne - 1);
    }

    /// <summary>Removes <paramref name="key"/>; returns false when it was not there. See <see cref="SetEntry"/> for the exceptions.</summary>
    public bool RemoveEntry(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        EditSupport.EnsureEditable(_resolver);
        int at = IndexOfKey(key);
        if (at < 0)
        {
            return false;
        }

        for (int i = at; i < _n - 1; i++)
        {
            _keys[i] = _keys[i + 1];
            _vals[i] = _vals[i + 1];
        }

        _n--;
        _keys[_n] = null!;
        _vals[_n] = null!;
        if (_index is not null)
        {
            _index.Clear();
            for (int i = 0; i < _n; i++)
            {
                _index[_keys[i]] = i;
            }
        }

        EditSupport.MarkDirty(_resolver, OwnerPlusOne - 1);
        return true;
    }

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
    private PdfResolver? _resolver;

    internal PdfArray(PdfResolver? resolver) => _resolver = resolver;

    /// <summary>Creates an empty array that belongs to no document yet (see <see cref="PdfDictionary()"/>).</summary>
    public PdfArray()
    {
    }

    internal PdfResolver? Resolver
    {
        get => _resolver;
        set => _resolver = value;
    }

    /// <summary>Appends <paramref name="value"/>; a null becomes the null object. Indirect objects are stored as references, as in <see cref="PdfDictionary.SetEntry"/>, which also lists the exceptions.</summary>
    public void Append(PdfObject? value) => Insert(_items.Count, value);

    /// <summary>Inserts <paramref name="value"/> before <paramref name="index"/> (0 to <see cref="Count"/>). See <see cref="Append"/>.</summary>
    public void Insert(int index, PdfObject? value)
    {
        EditSupport.EnsureEditable(_resolver);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)index, (uint)_items.Count);
        _items.Insert(index, EditSupport.Prepare(value ?? PdfNull.Instance, _resolver, OwnerPlusOne - 1));
        EditSupport.MarkDirty(_resolver, OwnerPlusOne - 1);
    }

    /// <summary>Replaces the element at <paramref name="index"/>. See <see cref="Append"/>.</summary>
    public void SetItem(int index, PdfObject? value)
    {
        EditSupport.EnsureEditable(_resolver);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)_items.Count);
        _items[index] = EditSupport.Prepare(value ?? PdfNull.Instance, _resolver, OwnerPlusOne - 1);
        EditSupport.MarkDirty(_resolver, OwnerPlusOne - 1);
    }

    /// <summary>Removes the element at <paramref name="index"/>.</summary>
    public void RemoveAt(int index)
    {
        EditSupport.EnsureEditable(_resolver);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)_items.Count);
        _items.RemoveAt(index);
        EditSupport.MarkDirty(_resolver, OwnerPlusOne - 1);
    }

    /// <summary>Removes every element.</summary>
    public void Clear()
    {
        EditSupport.EnsureEditable(_resolver);
        _items.Clear();
        EditSupport.MarkDirty(_resolver, OwnerPlusOne - 1);
    }

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
