using System.Linq;
namespace OnePdf;

/// <summary>The kind of a form field, from <c>/FT</c> and the field flags (ISO 32000-2 12.7.4).</summary>
public enum PdfFieldKind
{
    /// <summary>No or unknown field type.</summary>
    Unknown = 0,

    /// <summary>A text field (<c>Tx</c>).</summary>
    Text,

    /// <summary>A check box (<c>Btn</c>, neither push button nor radio flag).</summary>
    CheckBox,

    /// <summary>A radio button group (<c>Btn</c> with the radio flag).</summary>
    RadioButton,

    /// <summary>A push button (<c>Btn</c> with the push button flag).</summary>
    PushButton,

    /// <summary>A combo box (<c>Ch</c> with the combo flag).</summary>
    ComboBox,

    /// <summary>A list box (<c>Ch</c> without the combo flag).</summary>
    ListBox,

    /// <summary>A signature field (<c>Sig</c>).</summary>
    Signature,
}

/// <summary>One choice of a combo or list box.</summary>
/// <param name="Export">The export value (what the field's value becomes).</param>
/// <param name="Display">The text shown to the user.</param>
public sealed record PdfFieldOption(string Export, string Display);

/// <summary>
/// A node of the interactive form's field tree. Type, flags, value, default value, options and maximum length are inherited
/// from ancestors when the field does not have them itself (ISO 32000-2 12.7.4.1).
/// </summary>
public sealed class PdfFormField
{
    private readonly PdfDocument _doc;
    private readonly List<PdfFormField> _children = [];
    private readonly List<PdfDictionary> _widgets = [];

    internal PdfFormField(PdfDocument doc, PdfDictionary dictionary, PdfFormField? parent)
    {
        _doc = doc;
        Dictionary = dictionary;
        Parent = parent;
        string partial = PartialName;
        FullName = parent is null || parent.FullName.Length == 0 ? partial : partial.Length == 0 ? parent.FullName : parent.FullName + "." + partial;
    }

    /// <summary>The field dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The parent field, or null for a top-level field.</summary>
    public PdfFormField? Parent { get; }

    /// <summary>The partial name <c>/T</c>; empty when missing.</summary>
    public string PartialName => Dictionary.GetString("T")?.GetText() ?? string.Empty;

    /// <summary>The fully qualified name: the partial names from the root joined with dots (empty names are skipped).</summary>
    public string FullName { get; }

    /// <summary>Child fields in order (empty for a terminal field).</summary>
    public IReadOnlyList<PdfFormField> Children => _children;

    /// <summary>The widget annotation dictionaries of a terminal field: its <c>/Kids</c> that are not fields, or the field dictionary itself when it is merged with its single widget.</summary>
    public IReadOnlyList<PdfDictionary> Widgets => _widgets;

    internal void AddChild(PdfFormField f) => _children.Add(f);

    internal void AddWidget(PdfDictionary w) => _widgets.Add(w);

    private PdfObject? Inherited(string key)
    {
        PdfFormField? f = this;
        for (int depth = 0; f is not null && depth <= _doc.Limits.MaxNestingDepth; depth++)
        {
            if (f.Dictionary.Get(key) is { } v)
            {
                return v;
            }

            f = f.Parent;
        }

        return null;
    }

    /// <summary>The field flags <c>/Ff</c>, inherited.</summary>
    public long Flags => Inherited("Ff") is PdfInteger i ? i.Value : 0;

    /// <summary>True when the read-only flag is set.</summary>
    public bool IsReadOnly => (Flags & 1) != 0;

    /// <summary>True when the required flag is set.</summary>
    public bool IsRequired => (Flags & 2) != 0;

    /// <summary>True when a text field is multi-line.</summary>
    public bool IsMultiline => Kind == PdfFieldKind.Text && (Flags & (1 << 12)) != 0;

    /// <summary>True when a text field is a password field (its value must not be shown).</summary>
    public bool IsPassword => Kind == PdfFieldKind.Text && (Flags & (1 << 13)) != 0;

    /// <summary>True when a list box allows more than one selection.</summary>
    public bool IsMultiSelect => Kind == PdfFieldKind.ListBox && (Flags & (1 << 21)) != 0;

    /// <summary>The field type, inherited.</summary>
    public PdfFieldKind Kind
    {
        get
        {
            long ff = Flags;
            return Inherited("FT") is PdfName n
                ? n.Value switch
                {
                    "Tx" => PdfFieldKind.Text,
                    "Btn" => (ff & (1 << 16)) != 0 ? PdfFieldKind.PushButton : (ff & (1 << 15)) != 0 ? PdfFieldKind.RadioButton : PdfFieldKind.CheckBox,
                    "Ch" => (ff & (1 << 17)) != 0 ? PdfFieldKind.ComboBox : PdfFieldKind.ListBox,
                    "Sig" => PdfFieldKind.Signature,
                    _ => PdfFieldKind.Unknown,
                }
                : PdfFieldKind.Unknown;
        }
    }

    /// <summary>The raw value <c>/V</c>, inherited: a string, name, array, stream or (signatures) dictionary; null when none.</summary>
    public PdfObject? RawValue => Inherited("V");

    /// <summary>
    /// The value as text: a text string, a name without the slash (check boxes and radio buttons: <c>Yes</c>, <c>Off</c>), the
    /// text of a rich text or text stream, or the first selected value of a choice field; null when there is no value or it is a
    /// signature dictionary.
    /// </summary>
    public string? Value => Texts(RawValue) is { Count: > 0 } t ? t[0] : null;

    /// <summary>All selected values of a choice field (one or several), or the single value as a one-item list; empty when none.</summary>
    public IReadOnlyList<string> Values => Texts(RawValue);

    /// <summary>The default value <c>/DV</c> as text, inherited.</summary>
    public string? DefaultValue => Texts(Inherited("DV")) is { Count: > 0 } t ? t[0] : null;

    /// <summary>True for a signature field that has a value (a signature dictionary): the field is signed. Only presence is reported; nothing is verified.</summary>
    public bool IsSigned => Kind == PdfFieldKind.Signature && RawValue is PdfDictionary;

    /// <summary>The maximum length <c>/MaxLen</c> of a text field, inherited; null when not set.</summary>
    public int? MaxLength => Inherited("MaxLen") is PdfInteger i ? (int)Math.Clamp(i.Value, 0, int.MaxValue) : null;

    /// <summary>The choices of a combo or list box (<c>/Opt</c>, inherited); empty otherwise.</summary>
    public IReadOnlyList<PdfFieldOption> Options
    {
        get
        {
            var list = new List<PdfFieldOption>();
            if (Inherited("Opt") is PdfArray a)
            {
                int n = Math.Min(a.Count, _doc.Limits.MaxObjects);
                for (int i = 0; i < n; i++)
                {
                    switch (a[i])
                    {
                        case PdfString s:
                            list.Add(new PdfFieldOption(s.GetText(), s.GetText()));
                            break;
                        case PdfArray pair when pair.Count >= 2 && pair[0] is PdfString e && pair[1] is PdfString d:
                            list.Add(new PdfFieldOption(e.GetText(), d.GetText()));
                            break;
                    }
                }
            }

            return list;
        }
    }

    private List<string> Texts(PdfObject? v)
    {
        var list = new List<string>();
        switch (v)
        {
            case PdfString s:
                list.Add(s.GetText());
                break;
            case PdfName n:
                list.Add(n.Value);
                break;
            case PdfStream st:
                if (PdfDocument.TextOf(st) is { } t)
                {
                    list.Add(t);
                }

                break;
            case PdfArray a:
                for (int i = 0; i < Math.Min(a.Count, _doc.Limits.MaxObjects); i++)
                {
                    if (a[i] is PdfString es)
                    {
                        list.Add(es.GetText());
                    }
                    else if (a[i] is PdfName en)
                    {
                        list.Add(en.Value);
                    }
                }

                break;
        }

        return list;
    }
}

/// <summary>The interactive form of the document (<c>/AcroForm</c>, ISO 32000-2 12.7). Values are data; nothing is calculated or validated, no script runs.</summary>
public sealed class PdfAcroForm
{
    private readonly PdfDocument _doc;
    private readonly List<PdfFormField> _roots;
    private readonly List<PdfFormField> _all;

    internal PdfAcroForm(PdfDocument doc, PdfDictionary dictionary, List<PdfFormField> roots, List<PdfFormField> all)
    {
        _doc = doc;
        Dictionary = dictionary;
        _roots = roots;
        _all = all;
    }

    /// <summary>The AcroForm dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The top-level fields in order.</summary>
    public IReadOnlyList<PdfFormField> Fields => _roots;

    /// <summary>Every field of the tree, parents before their children (depth-first), in document order.</summary>
    public IReadOnlyList<PdfFormField> AllFields => _all;

    /// <summary>Every field without child fields: the ones that hold values.</summary>
    public IEnumerable<PdfFormField> TerminalFields => _all.Where(static f => f.Children.Count == 0);

    /// <summary>The field with this fully qualified name, or null.</summary>
    public PdfFormField? FindField(string fullName) => _all.FirstOrDefault(f => string.Equals(f.FullName, fullName, StringComparison.Ordinal));

    /// <summary>True when <c>/NeedAppearances</c> is set: appearance streams may be stale.</summary>
    public bool NeedAppearances => Dictionary.GetBool("NeedAppearances");

    /// <summary>The signature flags <c>/SigFlags</c> (bit 1: the form has signatures, bit 2: append only).</summary>
    public int SignatureFlags => (int)Math.Clamp(Dictionary.GetInt("SigFlags"), 0, int.MaxValue);

    /// <summary>True when the form carries XFA data.</summary>
    public bool HasXfa => Dictionary.Get("XFA") is PdfStream or PdfArray;

    /// <summary>
    /// The XFA packets concatenated as raw bytes (the <c>/XFA</c> stream, or the streams of the array in order), or null when
    /// there are none or they exceed <see cref="PdfLimits.MaxMetadataBytes"/>. XFA is never interpreted.
    /// </summary>
    public ReadOnlyMemory<byte>? GetXfaXml(CancellationToken cancellationToken = default)
    {
        PdfObject? x = Dictionary.Get("XFA");
        var streams = new List<PdfStream>();
        if (x is PdfStream one)
        {
            streams.Add(one);
        }
        else if (x is PdfArray a)
        {
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] is PdfStream s)
                {
                    streams.Add(s);
                }
            }
        }

        if (streams.Count == 0)
        {
            return null;
        }

        using var ms = new MemoryStream();
        foreach (PdfStream s in streams)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlyMemory<byte> d = s.GetDecodedBytes(cancellationToken);
            if (ms.Length + d.Length > _doc.Limits.MaxMetadataBytes)
            {
                _doc.ReportWarning("The XFA data is larger than the metadata limit; ignored.", s.Reference?.Number ?? -1, -1);
                return null;
            }

            ms.Write(d.Span);
        }

        return ms.ToArray();
    }
}

public sealed partial class PdfDocument
{
    /// <summary>The interactive form, or null when the catalog has no <c>/AcroForm</c>.</summary>
    public PdfAcroForm? Form => _form.Value;

    private Lazy<PdfAcroForm?> _form = null!;

    private PdfAcroForm? LoadForm()
    {
        PdfDictionary? af = Catalog.GetDictionary("AcroForm");
        if (af is null)
        {
            return null;
        }

        var roots = new List<PdfFormField>();
        var all = new List<PdfFormField>();
        var visited = new HashSet<int>();
        var stack = new Stack<(PdfDictionary Dict, PdfFormField? Parent, int Depth)>();
        if (af.Get("Fields") is PdfArray fields)
        {
            for (int i = Math.Min(fields.Count, Limits.MaxObjects) - 1; i >= 0; i--)
            {
                if (fields[i] is PdfDictionary d)
                {
                    stack.Push((d, null, 1));
                }
            }
        }

        while (stack.Count > 0)
        {
            (PdfDictionary dict, PdfFormField? parent, int depth) = stack.Pop();
            if (dict.Reference is { } r && !visited.Add(r.Number))
            {
                _diag.Warn("A form field is reachable twice (a cycle or shared field); the repeat was skipped.", r.Number);
                continue;
            }

            if (all.Count >= Limits.MaxObjects)
            {
                _diag.Warn("The form has more fields than the object limit allows; the rest was ignored.");
                break;
            }

            var field = new PdfFormField(this, dict, parent);
            all.Add(field);
            if (parent is null)
            {
                roots.Add(field);
            }
            else
            {
                parent.AddChild(field);
            }

            if (dict.Get("Kids") is not PdfArray kids)
            {
                if (dict.GetName("Subtype") == "Widget")
                {
                    field.AddWidget(dict);
                }

                continue;
            }

            var childFields = new List<PdfDictionary>();
            for (int i = 0; i < Math.Min(kids.Count, Limits.MaxObjects); i++)
            {
                if (kids[i] is not PdfDictionary kid)
                {
                    continue;
                }

                // 12.7.4.2: a kid with a partial name (or of its own kids) is a field; otherwise it is a widget of this one.
                if (kid.ContainsKey("T") || kid.ContainsKey("Kids"))
                {
                    childFields.Add(kid);
                }
                else
                {
                    field.AddWidget(kid);
                }
            }

            if (depth >= Limits.MaxNestingDepth && childFields.Count > 0)
            {
                _diag.Warn("Form fields are nested too deeply; the deeper fields were ignored.", dict.Reference?.Number ?? -1);
                continue;
            }

            for (int i = childFields.Count - 1; i >= 0; i--)
            {
                stack.Push((childFields[i], field, depth + 1));
            }
        }

        return new PdfAcroForm(this, af, roots, all);
    }
}
