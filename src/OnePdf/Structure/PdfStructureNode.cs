using System.Globalization;

namespace OnePdf;

/// <summary>
/// A structure element: <c>/S</c> type, text properties, attributes and children, as stored in the file. Reading never changes the document;
/// each setter and edit method changes it at once and needs <see cref="PdfOpenOptions.AllowEditing"/>.
/// </summary>
public sealed class PdfStructureNode : PdfStructureItem
{
    internal PdfStructureNode(PdfStructureTree tree, PdfObject raw, PdfDictionary dictionary)
        : base(tree, raw)
    {
        Dictionary = dictionary;
    }

    internal List<PdfStructureItem> Kids { get; } = [];

    /// <summary>The element's dictionary, for anything this class does not expose. Edit it directly only if you keep <c>/K</c>, <c>/P</c> and the parent tree consistent yourself.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The children in order: elements, marked-content references and object references.</summary>
    /// <exception cref="InvalidOperationException">The tree is stale (<see cref="PdfStructureTree.IsStale"/>).</exception>
    public IReadOnlyList<PdfStructureItem> Children
    {
        get
        {
            Tree.ThrowIfStale();
            return Kids;
        }
    }

    /// <summary>The structure type as written (<c>/S</c>), for example <c>P</c>, <c>TH</c> or a custom name; empty when the element has none.</summary>
    public string Type => Dictionary.GetName("S") ?? string.Empty;

    /// <summary>
    /// The standard structure type the element stands for: <see cref="Type"/> itself when it is a standard type, else what the
    /// role map leads to; null when the chain ends at a name that is not standard, is circular or is too long.
    /// </summary>
    public string? StandardType => Tree.Resolve(Type);

    /// <summary>The page the element belongs to: its own <c>/Pg</c>, else its nearest ancestor's, else the first page named by its content; -1 if none.</summary>
    public override int PageIndex
    {
        get
        {
            int guard = 0;
            for (PdfStructureNode? n = this; n is not null && guard++ < 100_000; n = n.Parent)
            {
                int own = Tree.PageOf(n.Dictionary.GetRaw("Pg"));
                if (own >= 0)
                {
                    return own;
                }
            }

            var stack = new Stack<PdfStructureNode>();
            stack.Push(this);
            int visited = 0;
            while (stack.Count > 0 && visited++ < 100_000)
            {
                PdfStructureNode n = stack.Pop();
                for (int i = n.Kids.Count - 1; i >= 0; i--)
                {
                    PdfStructureItem k = n.Kids[i];
                    if (k is PdfStructureNode kn)
                    {
                        stack.Push(kn);
                    }
                }

                foreach (PdfStructureItem k in n.Kids)
                {
                    int p = k switch { PdfMarkedContentReference m => m.Page, PdfObjectReference o => o.Page, _ => -1 };
                    if (p >= 0)
                    {
                        return p;
                    }
                }
            }

            return -1;
        }
    }

    /// <summary>The alternate description (<c>/Alt</c>); null when absent. Setting null removes it.</summary>
    public string? Alt
    {
        get => Text("Alt");
        set => SetText("Alt", value);
    }

    /// <summary>The replacement text (<c>/ActualText</c>); null when absent. Setting null removes it.</summary>
    public string? ActualText
    {
        get => Text("ActualText");
        set => SetText("ActualText", value);
    }

    /// <summary>The expansion of an abbreviation (<c>/E</c>); null when absent. Setting null removes it.</summary>
    public string? Expansion
    {
        get => Text("E");
        set => SetText("E", value);
    }

    /// <summary>The language (<c>/Lang</c>), for example <c>en-US</c>; null when absent. Setting null removes it.</summary>
    public string? Language
    {
        get => Text("Lang");
        set => SetText("Lang", value);
    }

    /// <summary>The title (<c>/T</c>); null when absent. Setting null removes it.</summary>
    public string? Title
    {
        get => Text("T");
        set => SetText("T", value);
    }

    /// <summary>The element identifier (<c>/ID</c>); null when absent. Read only: identifiers are listed in the tree's id tree.</summary>
    public string? Id => Text("ID");

    /// <summary>
    /// The scope of a table header cell (<c>/Scope</c> of the <c>Table</c> attributes); null when absent. Setting null removes it. The
    /// attribute is looked up in the element's attribute dictionaries and then in its classes; setting writes to the element's own.
    /// </summary>
    public PdfTagScope? Scope
    {
        get => GetAttribute("Table", "Scope") is PdfName n ? n.Value switch { "Row" => PdfTagScope.Row, "Column" => PdfTagScope.Column, "Both" => PdfTagScope.Both, _ => null } : null;
        set => SetAttribute("Table", "Scope", value is null ? null : new PdfName(value.Value.ToString()));
    }

    /// <summary>The number of columns a table cell spans (<c>/ColSpan</c>); null when absent. Setting null removes it.</summary>
    public int? ColSpan
    {
        get => GetAttribute("Table", "ColSpan") is PdfInteger i && i.Value is >= 0 and <= int.MaxValue ? (int)i.Value : null;
        set => SetAttribute("Table", "ColSpan", value is null ? null : new PdfInteger(value.Value));
    }

    /// <summary>The number of rows a table cell spans (<c>/RowSpan</c>); null when absent. Setting null removes it.</summary>
    public int? RowSpan
    {
        get => GetAttribute("Table", "RowSpan") is PdfInteger i && i.Value is >= 0 and <= int.MaxValue ? (int)i.Value : null;
        set => SetAttribute("Table", "RowSpan", value is null ? null : new PdfInteger(value.Value));
    }

    /// <summary>The identifiers of the header cells that apply to a table cell (<c>/Headers</c>); empty when absent.</summary>
    public IReadOnlyList<string> Headers
    {
        get
        {
            var list = new List<string>();
            if (GetAttribute("Table", "Headers") is PdfArray a)
            {
                for (int i = 0; i < a.Count; i++)
                {
                    if (a[i] is PdfString s)
                    {
                        list.Add(s.GetText());
                    }
                }
            }

            return list;
        }
    }

    /// <summary>The numbering of a list (<c>/ListNumbering</c> of the <c>List</c> attributes), for example <c>Decimal</c> or <c>Disc</c>; null when absent. Setting null removes it.</summary>
    public string? ListNumbering
    {
        get => GetAttribute("List", "ListNumbering") is PdfName n ? n.Value : null;
        set => SetAttribute("List", "ListNumbering", value is null ? null : new PdfName(value));
    }

    /// <summary>The element's own attribute dictionaries (<c>/A</c>), as stored. Class attributes (<c>/C</c>) are not included.</summary>
    public IReadOnlyList<PdfDictionary> Attributes => OwnAttributes();

    /// <summary>
    /// Reads one attribute: from the element's own attribute dictionaries with this owner (<c>/O</c>), then from the classes it names
    /// (<c>/C</c> through the tree's class map). Returns null when it is not found.
    /// </summary>
    /// <param name="owner">The attribute owner, for example <c>Table</c>, <c>List</c> or <c>Layout</c>.</param>
    /// <param name="key">The attribute name.</param>
    public PdfObject? GetAttribute(string owner, string key)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(key);
        foreach (PdfDictionary a in OwnAttributes())
        {
            if (a.GetName("O") == owner && a.Get(key) is { } v)
            {
                return v;
            }
        }

        foreach (PdfDictionary a in Tree.ClassAttributes(Dictionary))
        {
            if (a.GetName("O") == owner && a.Get(key) is { } v)
            {
                return v;
            }
        }

        return null;
    }

    /// <summary>
    /// Sets (or, with null, removes) one attribute in the element's own attribute dictionary for this owner, creating the dictionary when
    /// needed. A dictionary left with only its owner is removed.
    /// </summary>
    /// <exception cref="InvalidOperationException">The document is not editable, or the element is removed.</exception>
    public void SetAttribute(string owner, string key, PdfObject? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        ArgumentException.ThrowIfNullOrEmpty(key);
        Tree.EnsureEditable(this);
        PdfDictionary? target = null;
        foreach (PdfDictionary a in OwnAttributes())
        {
            if (a.GetName("O") == owner)
            {
                target = a;
                break;
            }
        }

        if (value is null)
        {
            if (target is null)
            {
                return;
            }

            target.RemoveEntry(key);
            if (target.Count == 1 && target.ContainsKey("O"))
            {
                RemoveAttributeDictionary(target);
            }

            return;
        }

        if (target is null)
        {
            target = new PdfDictionary();
            target.SetEntry("O", new PdfName(owner));
            PdfObject? existing = Dictionary.GetRaw("A");
            PdfObject? resolved = Dictionary.Get("A");
            if (resolved is null)
            {
                Dictionary.SetEntry("A", target);
            }
            else if (resolved is PdfArray arr)
            {
                arr.Append(target);
            }
            else
            {
                var both = new PdfArray();
                both.Append(existing);
                both.Append(target);
                Dictionary.SetEntry("A", both);
            }
        }

        target.SetEntry(key, value);
    }

    /// <summary>Sets the structure type (<c>/S</c>). Whether the type is standard, or mapped by the role map, is for the checker to say.</summary>
    /// <exception cref="ArgumentException">The type is empty.</exception>
    /// <exception cref="InvalidOperationException">The document is not editable, or the element is removed.</exception>
    public void SetType(string type)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        Tree.EnsureEditable(this);
        Dictionary.SetEntry("S", new PdfName(type));
    }

    /// <summary>Sets the structure type to a standard type.</summary>
    /// <exception cref="InvalidOperationException">The document is not editable, or the element is removed.</exception>
    public void SetType(PdfTagRole role) => SetType(role.ToString());

    /// <summary>
    /// Wraps <paramref name="count"/> children from <paramref name="first"/> in a new element of <paramref name="type"/>, which takes their
    /// place. Marked content moved into it is owned by the new element in the parent tree.
    /// </summary>
    /// <returns>The new element.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The range is not within the children, or is empty.</exception>
    /// <exception cref="InvalidOperationException">The document is not editable, or the element is removed.</exception>
    public PdfStructureNode WrapChildren(string type, int first, int count) => Tree.Wrap(this, type, first, count);

    /// <summary>
    /// Inserts a new, empty structure element of <paramref name="type"/> among the children at <paramref name="index"/> (-1 or the child count
    /// appends). The element has <c>/P</c> set to this element and no <c>/K</c>; it inherits this element's <c>/Pg</c>. Fill it with
    /// <see cref="PdfStructureItem.MoveTo"/> or leave it empty, for example a <c>TD</c> standing for a table cell the producer left out
    /// (ISO 14289-1 7.5 needs every row to have the same number of cells). No parent-tree entry is needed until content is moved into it.
    /// A <c>TH</c> inserted this way has no <see cref="Scope"/>; set it (<c>Column</c> for a cell in a header row, <c>Row</c> for a row header),
    /// because a table with a header cell without one fails ISO 14289-1 7.5 unless its data cells name their headers with <c>/Headers</c>.
    /// <see cref="PdfDocument.CheckPdfUA(CancellationToken)"/> reports such cells and <see cref="PdfDocument.PreparePdfUA"/> sets the scope where the row makes it plain.
    /// </summary>
    /// <returns>The new element.</returns>
    /// <exception cref="ArgumentException"><paramref name="type"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the children.</exception>
    /// <exception cref="InvalidOperationException">The document is not editable, or the element is removed or stale.</exception>
    public PdfStructureNode InsertChild(string type, int index = -1) => Tree.InsertEmpty(this, type, index);

    /// <summary>
    /// The text of the element's marked content and of everything below it, in tree order, from text extraction (the marked-content
    /// identifier of each glyph). Spaces between glyphs are inferred from their positions. Marked content in form XObjects is matched by
    /// identifier alone, so an identifier used both on the page and in a form can mix their text.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    public string GetText(CancellationToken cancellationToken = default) => Tree.TextOf(this, cancellationToken);

    private string? Text(string key) => Dictionary.Get(key) is PdfString s ? s.GetText() : null;

    private void SetText(string key, string? value)
    {
        Tree.EnsureEditable(this);
        if (value is null)
        {
            Dictionary.RemoveEntry(key);
        }
        else
        {
            Dictionary.SetEntry(key, PdfString.FromText(value));
        }
    }

    private List<PdfDictionary> OwnAttributes()
    {
        var list = new List<PdfDictionary>();
        PdfObject? a = Dictionary.Get("A");
        if (a is PdfDictionary d)
        {
            list.Add(d);
        }
        else if (a is PdfArray arr)
        {
            for (int i = 0; i < arr.Count && i < 1024; i++)
            {
                if (arr[i] is PdfDictionary item)
                {
                    list.Add(item);
                }
            }
        }

        return list;
    }

    private void RemoveAttributeDictionary(PdfDictionary target)
    {
        PdfObject? a = Dictionary.Get("A");
        if (ReferenceEquals(a, target))
        {
            Dictionary.RemoveEntry("A");
        }
        else if (a is PdfArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                if (ReferenceEquals(arr[i], target))
                {
                    arr.RemoveAt(i);
                    break;
                }
            }

            if (arr.Count == 0)
            {
                Dictionary.RemoveEntry("A");
            }
        }
    }

    /// <summary>A short description for debugging: the type and the child counts.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Type} ({Kids.Count} children)");
}
