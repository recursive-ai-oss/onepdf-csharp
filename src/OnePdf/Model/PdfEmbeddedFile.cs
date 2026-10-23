namespace OnePdf;

/// <summary>
/// A file embedded in the document (ISO 32000-2 7.11.4), found in the <c>EmbeddedFiles</c> name tree or attached by a file
/// attachment annotation. Listing reads only the file specification; the content is read when
/// <see cref="ReadContent"/> is called. A file specification that points at an external file has no content here: OnePDF
/// never opens files outside the document.
/// </summary>
public sealed class PdfEmbeddedFile
{
    private readonly PdfDocument _doc;
    private readonly string? _treeName;

    internal PdfEmbeddedFile(PdfDocument doc, PdfDictionary fileSpec, string? treeName, int pageIndex)
    {
        _doc = doc;
        Dictionary = fileSpec;
        _treeName = treeName;
        PageIndex = pageIndex;
    }

    /// <summary>The file specification dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The zero-based page of the attachment annotation this came from, or -1 when it was listed in the name tree.</summary>
    public int PageIndex { get; }

    /// <summary>The file name: <c>/UF</c>, else <c>/F</c>, else the name tree key; empty when none.</summary>
    public string Name => PdfDocument.FileSpecName(Dictionary) ?? _treeName ?? string.Empty;

    /// <summary>The description <c>/Desc</c>, or null.</summary>
    public string? Description => Dictionary.GetString("Desc")?.GetText();

    /// <summary>The stream with the file's bytes (<c>/EF</c> <c>/UF</c> or <c>/F</c>), or null for an external or empty specification.</summary>
    public PdfStream? Stream
    {
        get
        {
            PdfDictionary? ef = Dictionary.GetDictionary("EF");
            if (ef is null)
            {
                return null;
            }

            return (ef.Get("UF") ?? ef.Get("F") ?? ef.Get("Unix") ?? ef.Get("DOS") ?? ef.Get("Mac")) as PdfStream;
        }
    }

    /// <summary>True when the file's bytes are inside the document.</summary>
    public bool HasContent => Stream is not null;

    /// <summary>The MIME type from the stream's <c>/Subtype</c> (a name, so <c>text#2Fplain</c> reads as <c>text/plain</c>), or null.</summary>
    public string? MimeType => Stream?.Dictionary.GetName("Subtype");

    /// <summary>The size in bytes from the stream's <c>/Params</c> <c>/Size</c> as written, or null. Not checked against the data.</summary>
    public long? Size => Stream?.Dictionary.GetDictionary("Params") is { } p && p.ContainsKey("Size") ? p.GetInt("Size") : null;

    /// <summary>The creation date from <c>/Params</c>, when parseable.</summary>
    public DateTimeOffset? CreationDate => Date("CreationDate");

    /// <summary>The modification date from <c>/Params</c>, when parseable.</summary>
    public DateTimeOffset? ModificationDate => Date("ModDate");

    /// <summary>The MD5 checksum bytes from <c>/Params</c> <c>/CheckSum</c> as written, or null. Not verified.</summary>
    public ReadOnlyMemory<byte>? CheckSum => Stream?.Dictionary.GetDictionary("Params")?.GetString("CheckSum")?.Bytes;

    /// <summary>The relationship to the document, <c>/AFRelationship</c> (for example <c>Source</c>, <c>Data</c>, <c>Alternative</c>), or null.</summary>
    public string? Relationship => Dictionary.GetName("AFRelationship");

    /// <summary>
    /// Reads and decodes the file's bytes. Empty (with a diagnostic) when the specification has no embedded stream.
    /// </summary>
    /// <exception cref="PdfLimitException">The declared or decoded size exceeds <see cref="PdfLimits.MaxDecodedStreamBytes"/>.</exception>
    public ReadOnlyMemory<byte> ReadContent(CancellationToken cancellationToken = default)
    {
        PdfStream? s = Stream;
        if (s is null)
        {
            _doc.ReportWarning($"The file specification '{Name}' has no embedded stream (external files are never opened); read as empty.", Dictionary.Reference?.Number ?? -1, -1);
            return ReadOnlyMemory<byte>.Empty;
        }

        long declared = Size ?? 0;
        if (declared > _doc.Limits.MaxDecodedStreamBytes)
        {
            throw new PdfLimitException($"The embedded file '{Name}' declares {declared} bytes, more than the decoded stream limit.") { ObjectNumber = s.Reference?.Number ?? -1 };
        }

        cancellationToken.ThrowIfCancellationRequested();
        return s.GetDecodedBytes(cancellationToken);
    }

    private DateTimeOffset? Date(string key) =>
        Stream?.Dictionary.GetDictionary("Params")?.GetString(key) is { } s && PdfDate.TryParse(s.GetText(), out DateTimeOffset d) ? d : null;
}

/// <summary>A document-level script: a name with the JavaScript text. Only data; never run.</summary>
/// <param name="Name">The name tree key, or <c>OpenAction</c> for the script of the catalog's open action.</param>
/// <param name="Script">The script text.</param>
public sealed record PdfJavaScript(string Name, string Script);

/// <summary>An optional content group (a layer, ISO 32000-2 8.11).</summary>
public sealed class PdfOptionalContentGroup
{
    internal PdfOptionalContentGroup(PdfDictionary dictionary, bool visible)
    {
        Dictionary = dictionary;
        IsVisibleByDefault = visible;
    }

    /// <summary>The group dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The group name <c>/Name</c> as text; empty when missing.</summary>
    public string Name => Dictionary.GetString("Name")?.GetText() ?? string.Empty;

    /// <summary>The state in the default configuration: <c>/BaseState</c> then the <c>/ON</c> and <c>/OFF</c> lists.</summary>
    public bool IsVisibleByDefault { get; }
}

public sealed partial class PdfDocument
{
    /// <summary>The files of the <c>EmbeddedFiles</c> name tree, in tree order. Content is not read.</summary>
    public IReadOnlyList<PdfEmbeddedFile> EmbeddedFiles => EmbeddedFilesLazy().Value;

    private Lazy<IReadOnlyList<PdfEmbeddedFile>>? _embeddedFiles;

    private List<PdfEmbeddedFile> LoadEmbeddedFiles()
    {
        var list = new List<PdfEmbeddedFile>();
        PdfDictionary? tree = Catalog.GetDictionary("Names")?.GetDictionary("EmbeddedFiles");
        foreach (KeyValuePair<PdfObject, PdfObject> kv in ReadTree(tree, false, "embedded files"))
        {
            if (Resolve(kv.Value) is PdfDictionary spec)
            {
                list.Add(new PdfEmbeddedFile(this, spec, ((PdfString)kv.Key).GetText(), -1));
            }
        }

        return list;
    }

    /// <summary>
    /// The name tree files followed by the files of file attachment annotations on every page. Reads every page's annotation
    /// array, so it costs more than <see cref="EmbeddedFiles"/>.
    /// </summary>
    public IReadOnlyList<PdfEmbeddedFile> GetAllEmbeddedFiles(CancellationToken cancellationToken = default)
    {
        var list = new List<PdfEmbeddedFile>(EmbeddedFiles);
        foreach (PdfPage page in Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (PdfAnnotation a in page.Annotations)
            {
                if (a.AttachedFile is { } f)
                {
                    list.Add(f);
                }
            }
        }

        return list;
    }

    /// <summary>
    /// Document-level scripts as text: the <c>JavaScript</c> name tree and a JavaScript open action. They are data and are
    /// never executed.
    /// </summary>
    public IReadOnlyList<PdfJavaScript> JavaScript => JavaScriptLazy().Value;

    private Lazy<IReadOnlyList<PdfJavaScript>>? _javaScript;

    private List<PdfJavaScript> LoadJavaScript()
    {
        var list = new List<PdfJavaScript>();
        PdfDictionary? tree = Catalog.GetDictionary("Names")?.GetDictionary("JavaScript");
        foreach (KeyValuePair<PdfObject, PdfObject> kv in ReadTree(tree, false, "JavaScript"))
        {
            if (Resolve(kv.Value) is PdfDictionary a && TextOf(a.Get("JS")) is { } script)
            {
                list.Add(new PdfJavaScript(((PdfString)kv.Key).GetText(), script));
            }
        }

        if (Catalog.Get("OpenAction") is PdfDictionary open && open.GetName("S") == "JavaScript" && TextOf(open.Get("JS")) is { } openScript)
        {
            list.Add(new PdfJavaScript("OpenAction", openScript));
        }

        return list;
    }

    /// <summary>The optional content groups of <c>/OCProperties</c> with their default visibility; empty when the document has none.</summary>
    public IReadOnlyList<PdfOptionalContentGroup> OptionalContentGroups => OptionalContentLazy().Value;

    private Lazy<IReadOnlyList<PdfOptionalContentGroup>>? _ocgs;

    private List<PdfOptionalContentGroup> LoadOptionalContent()
    {
        var list = new List<PdfOptionalContentGroup>();
        PdfDictionary? props = Catalog.GetDictionary("OCProperties");
        if (props?.GetArray("OCGs") is not { } groups)
        {
            return list;
        }

        PdfDictionary? config = props.GetDictionary("D");
        bool baseOn = config?.GetName("BaseState") != "OFF";
        HashSet<int> on = RefNumbers(config?.GetArray("ON"));
        HashSet<int> off = RefNumbers(config?.GetArray("OFF"));
        int n = Math.Min(groups.Count, Limits.MaxObjects);
        for (int i = 0; i < n; i++)
        {
            if (groups[i] is PdfDictionary g)
            {
                int num = g.Reference?.Number ?? -1;
                bool visible = baseOn ? !off.Contains(num) : on.Contains(num);
                list.Add(new PdfOptionalContentGroup(g, visible));
            }
        }

        return list;

        static HashSet<int> RefNumbers(PdfArray? a)
        {
            var set = new HashSet<int>();
            for (int i = 0; a is not null && i < a.Count; i++)
            {
                if (a.GetRaw(i) is PdfReference r)
                {
                    set.Add(r.Number);
                }
            }

            return set;
        }
    }
}
