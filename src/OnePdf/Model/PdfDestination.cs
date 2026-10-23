using System.Text;

namespace OnePdf;

/// <summary>
/// A destination inside a document (ISO 32000-2 12.3.2): a page and a view of it. Read from an explicit destination array or
/// resolved from a name. Only data: nothing is navigated.
/// </summary>
public sealed class PdfDestination
{
    internal PdfDestination(string fitType, int pageIndex, PdfDictionary? page, string? name, double?[] values, bool isRemote)
    {
        FitType = fitType;
        PageIndex = pageIndex;
        Page = page;
        Name = name;
        IsRemotePage = isRemote;
        Left = values[0];
        Bottom = values[1];
        Right = values[2];
        Top = values[3];
        Zoom = values[4];
    }

    /// <summary>The view type without the slash: <c>XYZ</c>, <c>Fit</c>, <c>FitH</c>, <c>FitV</c>, <c>FitR</c>, <c>FitB</c>, <c>FitBH</c> or <c>FitBV</c>. <c>Fit</c> when the array is malformed.</summary>
    public string FitType { get; }

    /// <summary>The zero-based index of the target page in this document, or -1 when the page is not found (or the target is in another file, see <see cref="IsRemotePage"/>).</summary>
    public int PageIndex { get; }

    /// <summary>The target page dictionary when it is an object of this document, else null.</summary>
    public PdfDictionary? Page { get; }

    /// <summary>The name the destination was looked up by, if it came from a named destination; null for an explicit array.</summary>
    public string? Name { get; }

    /// <summary>True when the destination array names a page by number (a destination in another file); <see cref="PageIndex"/> then holds that zero-based number.</summary>
    public bool IsRemotePage { get; }

    /// <summary>The left coordinate for XYZ, FitV, FitR and FitBV; null when absent or null in the file.</summary>
    public double? Left { get; }

    /// <summary>The bottom coordinate for FitR.</summary>
    public double? Bottom { get; }

    /// <summary>The right coordinate for FitR.</summary>
    public double? Right { get; }

    /// <summary>The top coordinate for XYZ, FitH, FitR and FitBH.</summary>
    public double? Top { get; }

    /// <summary>The zoom factor for XYZ; null or 0 in the file mean "unchanged" and read as null.</summary>
    public double? Zoom { get; }
}

/// <summary>
/// An action (ISO 32000-2 12.6) as data. OnePDF never performs an action: no URI is fetched, no script run, no file launched.
/// </summary>
public sealed class PdfAction
{
    private readonly PdfDocument _doc;
    private Lazy<PdfDestination?> _destination = null!;

    internal PdfAction(PdfDocument doc, PdfDictionary dictionary)
    {
        _doc = doc;
        Dictionary = dictionary;
        _destination = new Lazy<PdfDestination?>(() => doc.ResolveDestination(dictionary.GetRaw("D") is { } d ? doc.Resolve(d) : null, 0), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The action dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The action type <c>/S</c> without the slash (<c>GoTo</c>, <c>GoToR</c>, <c>URI</c>, <c>Launch</c>, <c>JavaScript</c>, <c>Named</c>, <c>SubmitForm</c> and so on), or null.</summary>
    public string? ActionType => Dictionary.GetName("S");

    /// <summary>For GoTo, GoToR and GoToE actions the destination <c>/D</c>, resolved; otherwise null.</summary>
    public PdfDestination? Destination => ActionType is "GoTo" or "GoToR" or "GoToE" ? _destination.Value : null;

    /// <summary>For a URI action the address as written (<c>/URI</c> is a 7-bit string; decoded as UTF-8). Never fetched. Null otherwise.</summary>
    public string? Uri => ActionType == "URI" && Dictionary.Get("URI") is PdfString s ? Encoding.UTF8.GetString(s.Bytes.Span) : null;

    /// <summary>For Launch, GoToR, GoToE and SubmitForm actions the target file name or URL from <c>/F</c>; otherwise null.</summary>
    public string? FileName => ActionType is "Launch" or "GoToR" or "GoToE" or "SubmitForm" or "ImportData" ? PdfDocument.FileSpecName(Dictionary.Get("F")) : null;

    /// <summary>For a Named action the action name <c>/N</c> (for example <c>NextPage</c>); otherwise null.</summary>
    public string? NamedAction => ActionType == "Named" ? Dictionary.GetName("N") : null;

    /// <summary>For a JavaScript action the script text (<c>/JS</c> string or stream). Never run. Null otherwise.</summary>
    public string? JavaScript => ActionType == "JavaScript" ? PdfDocument.TextOf(Dictionary.Get("JS")) : null;

    /// <summary>
    /// The actions chained with <c>/Next</c>, in order, flattened (a chain of arrays and single actions). Each action is listed
    /// once, so a cycle ends the list; at most <see cref="PdfLimits.MaxNestingDepth"/> actions are followed.
    /// </summary>
    public IReadOnlyList<PdfAction> Next
    {
        get
        {
            var list = new List<PdfAction>();
            var seen = new HashSet<int>();
            var stack = new Stack<PdfObject?>();
            stack.Push(Dictionary.Get("Next"));
            while (stack.Count > 0 && list.Count < _doc.Limits.MaxNestingDepth)
            {
                PdfObject? o = stack.Pop();
                if (o is PdfDictionary d)
                {
                    if (d == Dictionary || (d.Reference is { } r && !seen.Add(r.Number)))
                    {
                        continue;
                    }

                    var a = new PdfAction(_doc, d);
                    list.Add(a);
                    stack.Push(d.Get("Next"));
                }
                else if (o is PdfArray arr)
                {
                    for (int i = Math.Min(arr.Count, _doc.Limits.MaxNestingDepth) - 1; i >= 0; i--)
                    {
                        stack.Push(arr[i]);
                    }
                }
            }

            return list;
        }
    }
}

public sealed partial class PdfDocument
{
    private Lazy<Dictionary<string, PdfObject>>? _namedDestinations;

    /// <summary>The names of the document's named destinations (catalog <c>/Dests</c> and the <c>/Names</c> <c>/Dests</c> name tree), in no particular order.</summary>
    public IReadOnlyCollection<string> NamedDestinationNames => NamedDestinationsLazy().Value.Keys;

    /// <summary>Resolves a named destination, or null when the name is not defined or leads nowhere.</summary>
    public PdfDestination? GetNamedDestination(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return ResolveDestination(new PdfString(Encoding.UTF8.GetBytes(name)), 0);
    }

    private Dictionary<string, PdfObject> LoadNamedDestinations()
    {
        var map = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
        PdfDictionary cat = Catalog;
        if (cat.GetDictionary("Dests") is { } old)
        {
            foreach (string key in old.Keys)
            {
                if (map.Count >= Limits.MaxObjects)
                {
                    break;
                }

                if (old.GetRaw(key) is { } v)
                {
                    map.TryAdd(key, v);
                }
            }
        }

        PdfDictionary? tree = cat.GetDictionary("Names")?.GetDictionary("Dests");
        foreach (KeyValuePair<PdfObject, PdfObject> kv in ReadTree(tree, false, "named destinations"))
        {
            map.TryAdd(((PdfString)kv.Key).GetText(), kv.Value);
        }

        return map;
    }

    /// <summary>
    /// Turns a destination value (an explicit array, a dictionary with <c>/D</c>, or a name or string to look up) into a
    /// <see cref="PdfDestination"/>. A named destination that resolves to another name is followed up to the recursion limit.
    /// </summary>
    internal PdfDestination? ResolveDestination(PdfObject? value, int depth)
    {
        string? name = null;
        for (int hops = 0; hops <= Limits.MaxRecursionDepth; hops++)
        {
            value = Resolve(value);
            switch (value)
            {
                case PdfString s:
                    name = s.GetText();
                    value = NamedDestinationsLazy().Value.GetValueOrDefault(name);
                    continue;
                case PdfName n:
                    name = n.Value;
                    value = NamedDestinationsLazy().Value.GetValueOrDefault(n.Value);
                    continue;
                case PdfDictionary d:
                    value = d.GetRaw("D");
                    continue;
                case PdfArray arr:
                    return ParseDestinationArray(arr, name);
                default:
                    return null;
            }
        }

        return null;
    }

    private PdfDestination? ParseDestinationArray(PdfArray arr, string? name)
    {
        if (arr.Count < 1)
        {
            return null;
        }

        int pageIndex = -1;
        PdfDictionary? page = null;
        bool remote = false;
        if (arr.GetRaw(0) is PdfReference r)
        {
            pageIndex = Pages.IndexOfObject(r.Number);
            page = Resolve(r) as PdfDictionary;
        }
        else if (arr[0] is PdfInteger pi)
        {
            remote = true;
            pageIndex = (int)Math.Clamp(pi.Value, -1, int.MaxValue);
        }
        else if (arr[0] is PdfDictionary pd)
        {
            page = pd;
            pageIndex = pd.Reference is { } pr ? Pages.IndexOfObject(pr.Number) : -1;
        }

        string fit = arr.Count > 1 ? arr.GetName(1) ?? "Fit" : "Fit";
        double?[] v = new double?[5]; // left, bottom, right, top, zoom
        double? Num(int i) => i < arr.Count && arr[i] is PdfInteger or PdfReal ? arr.GetDouble(i) : null;
        switch (fit)
        {
            case "XYZ":
                v[0] = Num(2);
                v[3] = Num(3);
                v[4] = Num(4) is { } z && z != 0 ? z : null;
                break;
            case "FitH" or "FitBH":
                v[3] = Num(2);
                break;
            case "FitV" or "FitBV":
                v[0] = Num(2);
                break;
            case "FitR":
                v[0] = Num(2);
                v[1] = Num(3);
                v[2] = Num(4);
                v[3] = Num(5);
                break;
        }

        return new PdfDestination(fit, pageIndex, page, name, v, remote);
    }

    /// <summary>The file name of a file specification: a string, or the <c>/UF</c>, <c>/F</c>, <c>/Unix</c>, <c>/DOS</c> or <c>/Mac</c> entry of a dictionary.</summary>
    internal static string? FileSpecName(PdfObject? spec)
    {
        if (spec is PdfString s)
        {
            return s.GetText();
        }

        if (spec is PdfDictionary d)
        {
            foreach (string key in (ReadOnlySpan<string>)["UF", "F", "Unix", "DOS", "Mac"])
            {
                if (d.Get(key) is PdfString fs)
                {
                    return fs.GetText();
                }
            }
        }

        return null;
    }

    /// <summary>A text string or text stream as a .NET string.</summary>
    internal static string? TextOf(PdfObject? o) => o switch
    {
        PdfString s => s.GetText(),
        PdfStream st => new PdfString(st.GetDecodedBytes().ToArray()).GetText(),
        _ => null,
    };
}
