namespace OnePdf.Fonts;

/// <summary>
/// One substitute font file shipped in the package. The bytes are the upstream release file, unmodified.
/// </summary>
public sealed class SubstituteFace
{
    private readonly string _resource;
    private byte[]? _data;

    internal SubstituteFace(string name, string family, string resource, bool bold, bool italic)
    {
        Name = name;
        Family = family;
        _resource = resource;
        IsBold = bold;
        IsItalic = italic;
    }

    /// <summary>The face's file name without extension, for example <c>LiberationSans-BoldItalic</c>.</summary>
    public string Name { get; }

    /// <summary>The font family, for example <c>Liberation Sans</c> or <c>Noto Sans Math</c>.</summary>
    public string Family { get; }

    /// <summary>True for the bold faces.</summary>
    public bool IsBold { get; }

    /// <summary>True for the italic faces (the Courier, Helvetica and Times oblique styles map to these).</summary>
    public bool IsItalic { get; }

    /// <summary>
    /// Opens a new read-only stream over the embedded font file (a TrueType font). The caller disposes it. Throws
    /// <see cref="InvalidOperationException"/> if the resource is missing, which only a damaged package can cause.
    /// </summary>
    public Stream OpenRead() =>
        typeof(SubstituteFace).Assembly.GetManifestResourceStream(_resource)
        ?? throw new InvalidOperationException("The embedded font resource '" + _resource + "' is missing.");

    /// <summary>
    /// The font file's bytes, read from the embedded resource on first use and kept for the life of the process.
    /// Callers must not modify the returned memory's contents; it is shared (it is exposed read-only).
    /// </summary>
    public ReadOnlyMemory<byte> GetData()
    {
        byte[]? d = Volatile.Read(ref _data);
        if (d is null)
        {
            using Stream s = OpenRead();
            d = new byte[checked((int)s.Length)];
            s.ReadExactly(d);
            Volatile.Write(ref _data, d);
        }

        return d;
    }

    /// <summary>Returns <see cref="Name"/>.</summary>
    public override string ToString() => Name;
}
