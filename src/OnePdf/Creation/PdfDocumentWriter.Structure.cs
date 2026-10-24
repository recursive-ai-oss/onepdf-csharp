namespace OnePdf;

/// <summary>Optional properties of a structure element (ISO 32000-2 14.7.2, Table 355).</summary>
public sealed class PdfStructureElementOptions
{
    /// <summary>Alternate description for content that has no text of its own, such as a figure (<c>/Alt</c>, ISO 32000-2 14.9.3).</summary>
    public string? AlternateText { get; init; }

    /// <summary>Text the element's content stands for, replacing what extraction would read from it (<c>/ActualText</c>, 14.9.4).</summary>
    public string? ActualText { get; init; }

    /// <summary>Language of the element's content, such as <c>de-CH</c> (<c>/Lang</c>, 14.9.2).</summary>
    public string? Language { get; init; }

    /// <summary>A title, for example for a section (<c>/T</c>).</summary>
    public string? Title { get; init; }

    /// <summary>The expansion of an abbreviation or acronym (<c>/E</c>, 14.9.5).</summary>
    public string? Expansion { get; init; }

    /// <summary>A unique identifier within the document, for references from other elements (<c>/ID</c>). Not interpreted.</summary>
    public string? Id { get; init; }

    /// <summary>One attribute object (<c>/A</c>, ISO 32000-2 14.7.5), for example from <see cref="PdfStructureAttributes"/>. Null for none.</summary>
    public PdfDictionary? Attributes { get; init; }
}

/// <summary>Builders for standard structure attribute objects (ISO 32000-2 14.8.5).</summary>
public static class PdfStructureAttributes
{
    /// <summary>The table attributes of a header cell (<c>TH</c>): <paramref name="scope"/> is <c>Row</c>, <c>Column</c> or <c>Both</c> (14.8.5.7).</summary>
    /// <exception cref="ArgumentException">The scope is not one of the three.</exception>
    public static PdfDictionary TableHeader(string scope, int columnSpan = 1, int rowSpan = 1)
    {
        if (scope is not ("Row" or "Column" or "Both"))
        {
            throw new ArgumentException("The scope is Row, Column or Both.", nameof(scope));
        }

        PdfDictionary d = Table(columnSpan, rowSpan);
        d.SetEntry("Scope", new PdfName(scope));
        return d;
    }

    /// <summary>The table attributes of a cell (<c>TD</c> or <c>TH</c>) that spans rows or columns.</summary>
    public static PdfDictionary Table(int columnSpan = 1, int rowSpan = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columnSpan, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rowSpan, 1);
        var d = new PdfDictionary();
        d.SetEntry("O", new PdfName("Table"));
        if (columnSpan > 1)
        {
            d.SetEntry("ColSpan", new PdfInteger(columnSpan));
        }

        if (rowSpan > 1)
        {
            d.SetEntry("RowSpan", new PdfInteger(rowSpan));
        }

        return d;
    }

    /// <summary>The list attribute that tells how items are marked: <paramref name="numbering"/> is one of <c>None</c>, <c>Unordered</c>, <c>Ordered</c>, <c>Disc</c>, <c>Circle</c>, <c>Square</c>, <c>Decimal</c>, <c>LowerRoman</c>, <c>UpperRoman</c>, <c>LowerAlpha</c>, <c>UpperAlpha</c> (14.8.5.4).</summary>
    public static PdfDictionary List(string numbering)
    {
        ArgumentException.ThrowIfNullOrEmpty(numbering);
        var d = new PdfDictionary();
        d.SetEntry("O", new PdfName("List"));
        d.SetEntry("ListNumbering", new PdfName(numbering));
        return d;
    }
}

/// <summary>
/// A node of the structure tree being written by a <see cref="PdfDocumentWriter"/>. Make one with
/// <see cref="PdfDocumentWriter.BeginStructureElement(string, PdfStructureElementOptions)"/>, bind page content to it with
/// <see cref="PdfStreamPageBuilder.BeginTaggedContent"/> or <see cref="PdfStreamPageBuilder.AddLink"/>, and end it with
/// <see cref="PdfDocumentWriter.EndStructureElement()"/>. The element is written when it ends.
/// </summary>
public sealed class PdfStructureElement
{
    internal PdfStructureElement(PdfDocumentWriter owner, string type, PdfReference reference, PdfDictionary dictionary, PdfStructureElementOptions? options, int parentNumber, PdfStructureElement? parent)
    {
        Owner = owner;
        Parent = parent;
        Type = type;
        Reference = reference;
        Dictionary = dictionary;
        Options = options;
        ParentNumber = parentNumber;
    }

    /// <summary>The structure type (<c>/S</c>), such as <c>P</c>, <c>H1</c>, <c>Table</c>, <c>TD</c>, <c>L</c>, <c>LI</c>, <c>Figure</c>, <c>Link</c>.</summary>
    public string Type { get; }

    /// <summary>The indirect reference the element is written under.</summary>
    public PdfReference Reference { get; }

    /// <summary>True once <see cref="PdfDocumentWriter.EndStructureElement()"/> has written it.</summary>
    public bool IsEnded { get; internal set; }

    internal PdfDocumentWriter Owner { get; }

    internal PdfDictionary Dictionary { get; }

    internal PdfStructureElementOptions? Options { get; }

    internal int ParentNumber { get; }

    internal PdfStructureElement? Parent { get; }

    // Children begun under this element and not yet ended; an element cannot end before them.
    internal int OpenChildren { get; set; }

    // The object number of the page the element's first content item is on (its /Pg), or -1.
    internal int PageNumber { get; set; } = -1;

    internal List<StructureKid> Kids { get; } = [];
}

// One entry of an element's /K: a child element, a marked-content id on the element's own page, a marked-content reference on another
// page, or an object reference (an annotation). Eight bytes each, so a large tree costs little while it is open.
internal readonly record struct StructureKid(byte Kind, int A, int B)
{
    public const byte Child = 0;
    public const byte Mcid = 1;
    public const byte Mcr = 2;
    public const byte Objr = 3;
}

public sealed partial class PdfDocumentWriter
{
    private readonly List<PdfStructureElement> _structureStack = [];
    private readonly List<int> _topLevel = [];
    private readonly List<(int Key, PdfObject Value)> _parentTree = [];
    private readonly Dictionary<string, string> _roleMap = new(StringComparer.Ordinal); // written in ordinal key order
    private PdfReference? _structureRoot;
    private int _nextStructureKey;

    /// <summary>The innermost structure element that has been begun and not ended, or null.</summary>
    public PdfStructureElement? CurrentStructureElement => _structureStack.Count == 0 ? null : _structureStack[^1];

    /// <summary>
    /// Begins a structure element (ISO 32000-2 14.7.2) as a child of <see cref="CurrentStructureElement"/>, or as a top-level element
    /// when none is open. Using any makes the document tagged: the catalog gets <c>/MarkInfo</c> and a <c>/StructTreeRoot</c>.
    /// Typical shape: a <c>Document</c> element holding <c>H1</c>, <c>P</c>, <c>Table</c> (with <c>TR</c>, <c>TH</c>, <c>TD</c>), <c>L</c>
    /// (with <c>LI</c>, <c>Lbl</c>, <c>LBody</c>) and <c>Figure</c> children.
    /// </summary>
    /// <param name="type">The structure type; a standard type (Table 365/ISO 32000-2 14.8.4) or a custom one mapped with <see cref="MapRole"/>.</param>
    /// <param name="options">Optional properties.</param>
    /// <exception cref="ArgumentException">The type is empty.</exception>
    public PdfStructureElement BeginStructureElement(string type, PdfStructureElementOptions? options = null) => BeginStructureElementCore(CurrentStructureElement, type, options);

    /// <summary>
    /// Begins a structure element under an explicit parent instead of the innermost open element, so a producer whose content continues on
    /// later pages can keep several elements open at once and end them in any order (an element still ends only after its own children).
    /// Children are listed under their parent in the order they were begun.
    /// </summary>
    /// <param name="parent">An element of this writer that has not ended, or null for a top-level element.</param>
    /// <param name="type">The structure type, as for <see cref="BeginStructureElement(string, PdfStructureElementOptions?)"/>.</param>
    /// <param name="options">Optional properties.</param>
    /// <exception cref="ArgumentException">The type is empty, or the parent belongs to another writer.</exception>
    /// <exception cref="InvalidOperationException">The parent has ended.</exception>
    public PdfStructureElement BeginStructureElement(PdfStructureElement? parent, string type, PdfStructureElementOptions? options = null)
    {
        if (parent is not null)
        {
            if (!ReferenceEquals(parent.Owner, this))
            {
                throw new ArgumentException("The parent belongs to another writer.", nameof(parent));
            }

            if (parent.IsEnded)
            {
                throw new InvalidOperationException("The parent structure element has ended.");
            }
        }

        return BeginStructureElementCore(parent, type, options);
    }

    private PdfStructureElement BeginStructureElementCore(PdfStructureElement? parent, string type, PdfStructureElementOptions? options)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        CheckNotFinished();
        if (_structureRoot is null)
        {
            _structureRoot = Reserve(new PdfDictionary());
        }

        var dict = new PdfDictionary();
        PdfReference reference = Reserve(dict);
        var element = new PdfStructureElement(this, type, reference, dict, options, parent?.Reference.Number ?? _structureRoot.Number, parent);
        if (parent is null)
        {
            _topLevel.Add(reference.Number);
        }
        else
        {
            parent.Kids.Add(new StructureKid(StructureKid.Child, reference.Number, 0));
            parent.OpenChildren++;
        }

        _structureStack.Add(element);
        return element;
    }

    /// <summary>Ends the innermost open structure element and writes it.</summary>
    /// <exception cref="InvalidOperationException">No element is open.</exception>
    public void EndStructureElement()
    {
        CheckNotFinished();
        if (_structureStack.Count == 0)
        {
            throw new InvalidOperationException("No structure element is open.");
        }

        EndStructureElementCore(_structureStack[^1]);
    }

    /// <summary>Ends a specific open structure element and writes it. Its own children must have ended first.</summary>
    /// <exception cref="ArgumentException">The element belongs to another writer.</exception>
    /// <exception cref="InvalidOperationException">The element has already ended, or has children that have not.</exception>
    public void EndStructureElement(PdfStructureElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        CheckNotFinished();
        if (!ReferenceEquals(element.Owner, this))
        {
            throw new ArgumentException("The element belongs to another writer.", nameof(element));
        }

        if (element.IsEnded)
        {
            throw new InvalidOperationException("The structure element has already ended.");
        }

        if (element.OpenChildren > 0)
        {
            throw new InvalidOperationException($"The structure element '{element.Type}' still has open children.");
        }

        EndStructureElementCore(element);
    }

    private void EndStructureElementCore(PdfStructureElement e)
    {
        _structureStack.RemoveAt(_structureStack.LastIndexOf(e));
        if (e.Parent is { } parent)
        {
            parent.OpenChildren--;
        }
        PdfDictionary d = e.Dictionary;
        d.SetEntry("Type", new PdfName("StructElem"));
        d.SetEntry("S", new PdfName(e.Type));
        d.SetEntry("P", new PdfReference(e.ParentNumber, 0));
        if (e.PageNumber >= 0)
        {
            d.SetEntry("Pg", new PdfReference(e.PageNumber, 0));
        }

        if (e.Kids.Count == 1)
        {
            d.SetEntry("K", KidObject(e, e.Kids[0]));
        }
        else if (e.Kids.Count > 1)
        {
            var k = new PdfArray();
            foreach (StructureKid kid in e.Kids)
            {
                k.Append(KidObject(e, kid));
            }

            d.SetEntry("K", k);
        }

        if (e.Options is { } o)
        {
            SetText(d, "Alt", o.AlternateText);
            SetText(d, "ActualText", o.ActualText);
            SetText(d, "Lang", o.Language);
            SetText(d, "T", o.Title);
            SetText(d, "E", o.Expansion);
            if (o.Id is not null)
            {
                d.SetEntry("ID", PdfString.FromText(o.Id));
            }

            if (o.Attributes is { } attributes)
            {
                d.SetEntry("A", attributes);
            }
        }

        e.IsEnded = true;
        e.Kids.Clear();
        e.Kids.TrimExcess();
        Release(e.Reference.Number);
        Flush();
    }

    /// <summary>
    /// Maps a custom structure type to a standard one (the catalog's <c>/RoleMap</c>, ISO 32000-2 14.7.4), so readers understand
    /// elements of your own types.
    /// </summary>
    /// <exception cref="ArgumentException">A name is empty.</exception>
    public void MapRole(string customType, string standardType)
    {
        ArgumentException.ThrowIfNullOrEmpty(customType);
        ArgumentException.ThrowIfNullOrEmpty(standardType);
        CheckNotFinished();
        _roleMap[customType] = standardType;
    }

    private static void SetText(PdfDictionary d, string key, string? value)
    {
        if (value is not null)
        {
            d.SetEntry(key, PdfString.FromText(value));
        }
    }

    private static PdfObject KidObject(PdfStructureElement owner, StructureKid kid)
    {
        switch (kid.Kind)
        {
            case StructureKid.Child:
                return new PdfReference(kid.A, 0);
            case StructureKid.Mcid:
                return new PdfInteger(kid.A);
            default:
                {
                    var d = new PdfDictionary();
                    d.SetEntry("Type", new PdfName(kid.Kind == StructureKid.Mcr ? "MCR" : "OBJR"));
                    d.SetEntry("Pg", new PdfReference(kid.B, 0));
                    if (kid.Kind == StructureKid.Mcr)
                    {
                        d.SetEntry("MCID", new PdfInteger(kid.A));
                    }
                    else
                    {
                        d.SetEntry("Obj", new PdfReference(kid.A, 0));
                    }

                    return d;
                }
        }
    }

    // Binds a marked-content id on a page to an element (the element's /K) and returns nothing: the page builder records the parent.
    internal static void BindContent(PdfStructureElement element, int pageNumber, int mcid)
    {
        if (element.IsEnded)
        {
            throw new InvalidOperationException("The structure element has ended; content cannot be added to it.");
        }

        if (element.PageNumber < 0)
        {
            element.PageNumber = pageNumber;
        }

        element.Kids.Add(element.PageNumber == pageNumber ? new StructureKid(StructureKid.Mcid, mcid, 0) : new StructureKid(StructureKid.Mcr, mcid, pageNumber));
    }

    internal static void BindObject(PdfStructureElement element, int pageNumber, int objectNumber)
    {
        if (element.IsEnded)
        {
            throw new InvalidOperationException("The structure element has ended; content cannot be added to it.");
        }

        if (element.PageNumber < 0)
        {
            element.PageNumber = pageNumber;
        }

        element.Kids.Add(new StructureKid(StructureKid.Objr, objectNumber, pageNumber));
    }

    internal int NextStructureKey() => _nextStructureKey++;

    internal void AddParentTreeEntry(int key, PdfObject value) => _parentTree.Add((key, value));

    private void CheckStructureComplete()
    {
        if (_structureStack.Count > 0)
        {
            throw new InvalidOperationException($"The structure element '{_structureStack[^1].Type}' was never ended.");
        }
    }

    private PdfReference? WriteStructureTree()
    {
        if (_structureRoot is null)
        {
            return null;
        }

        _parentTree.Sort(static (a, b) => a.Key.CompareTo(b.Key));
        var entries = new List<(PdfObject Key, PdfObject Value)>(_parentTree.Count);
        foreach ((int key, PdfObject value) in _parentTree)
        {
            entries.Add((new PdfInteger(key), value));
        }

        PdfDictionary treeRoot = NumberTree.Build(entries, names: false, d => _workspace.AddObject(d));
        PdfReference treeRef = _workspace.AddObject(treeRoot);

        PdfDictionary root = (PdfDictionary)_workspace.Resolve(_structureRoot)!;
        root.SetEntry("Type", new PdfName("StructTreeRoot"));
        if (_topLevel.Count == 1)
        {
            root.SetEntry("K", new PdfReference(_topLevel[0], 0));
        }
        else if (_topLevel.Count > 1)
        {
            var k = new PdfArray();
            foreach (int n in _topLevel)
            {
                k.Append(new PdfReference(n, 0));
            }

            root.SetEntry("K", k);
        }

        root.SetEntry("ParentTree", treeRef);
        root.SetEntry("ParentTreeNextKey", new PdfInteger(_nextStructureKey));
        if (_roleMap.Count > 0)
        {
            var map = new PdfDictionary();
            foreach (string key in OrdinalKeys(_roleMap))
            {
                map.SetEntry(key, new PdfName(_roleMap[key]));
            }

            root.SetEntry("RoleMap", map);
        }

        return _structureRoot;
    }
}
