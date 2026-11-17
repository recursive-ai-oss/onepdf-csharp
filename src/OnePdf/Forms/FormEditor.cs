using System.Globalization;
using System.Linq;
using OnePdf.Appearance;

namespace OnePdf.Forms;

/// <summary>Renames fields and changes how their widgets look (ISO 32000-2 12.7.4, 12.5.6.19).</summary>
internal static class FormEditor
{
    private static PdfFormValueException Fail(PdfFormValueError reason, PdfFormField f, string message) =>
        new(reason, f.FullName, "Field '" + f.FullName + "': " + message);

    // ---- rename ----

    public static void Rename(PdfFormField f, string newName)
    {
        ArgumentNullException.ThrowIfNull(newName);
        PdfDocument doc = f.Doc;
        doc.EnsureEditable(0);
        PdfAcroForm form = doc.Form ?? throw new InvalidOperationException("The document has no form.");
        if (newName.Length == 0 || newName.Contains('.', StringComparison.Ordinal))
        {
            throw Fail(PdfFormValueError.InvalidName, f, "a partial name must not be empty or contain a period.");
        }

        if (string.Equals(f.PartialName, newName, StringComparison.Ordinal))
        {
            return;
        }

        // The subtree whose full names change: the field and every descendant (the tree was built with cycle detection).
        var subtree = new List<PdfFormField> { f };
        for (int i = 0; i < subtree.Count; i++)
        {
            subtree.AddRange(subtree[i].Children);
        }

        var inSubtree = new HashSet<PdfFormField>(subtree);
        var outside = new HashSet<string>(StringComparer.Ordinal);
        foreach (PdfFormField g in form.AllFieldList)
        {
            if (!inSubtree.Contains(g))
            {
                outside.Add(g.FullName);
            }
        }

        var names = new string[subtree.Count];
        for (int i = 0; i < subtree.Count; i++)
        {
            names[i] = FullNameWith(subtree[i], f, newName, doc.Limits.MaxNestingDepth);
            if (outside.Contains(names[i]))
            {
                throw Fail(PdfFormValueError.NameInUse, f, "renaming to '" + newName + "' would give '" + names[i] + "' to two fields.");
            }
        }

        f.Dictionary.SetEntry("T", PdfString.FromText(newName));
        for (int i = 0; i < subtree.Count; i++)
        {
            subtree[i].SetFullName(names[i]);
        }

        form.InvalidateNames();
    }

    // The full name g would have if f's partial name were newName: the non-empty partial names from the root, joined by periods.
    private static string FullNameWith(PdfFormField g, PdfFormField f, string newName, int maxDepth)
    {
        var parts = new List<string>();
        PdfFormField? cur = g;
        for (int depth = 0; cur is not null && depth <= maxDepth; depth++)
        {
            string p = ReferenceEquals(cur, f) ? newName : cur.PartialName;
            if (p.Length > 0)
            {
                parts.Add(p);
            }

            cur = cur.Parent;
        }

        parts.Reverse();
        return string.Join('.', parts);
    }

    // ---- appearance ----

    public static int SetAppearance(PdfFormField f, PdfFieldAppearance a, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(a);
        PdfDocument doc = f.Doc;
        doc.EnsureEditable(0);
        PdfAcroForm form = doc.Form ?? throw new InvalidOperationException("The document has no form.");
        if (a.TextColor is { IsTransparent: true })
        {
            throw new ArgumentException("The text colour cannot be transparent.", nameof(a));
        }

        if (a.FontSize is { } size && !(double.IsFinite(size) && size is >= 0 and <= 1000))
        {
            throw new ArgumentOutOfRangeException(nameof(a), size, "The font size must be between 0 and 1000.");
        }

        if (a.Background is null && a.BorderColor is null && a.FontSize is null && a.TextColor is null)
        {
            return 0;
        }

        // Terminal fields in scope: the field itself, or every terminal field below it.
        var targets = new List<PdfFormField>();
        var stack = new Stack<PdfFormField>();
        stack.Push(f);
        while (stack.Count > 0)
        {
            PdfFormField g = stack.Pop();
            if (g.Children.Count > 0)
            {
                for (int i = g.Children.Count - 1; i >= 0; i--)
                {
                    stack.Push(g.Children[i]);
                }
            }
            else if (g.Kind is PdfFieldKind.Unknown or PdfFieldKind.Signature)
            {
                if (ReferenceEquals(g, f))
                {
                    throw Fail(PdfFormValueError.WrongType, f, "a " + g.Kind + " field has no generated appearance.");
                }
            }
            else
            {
                targets.Add(g);
            }
        }

        FormSession session = form.Session;
        string? formDa = form.Dictionary.GetString("DA")?.GetText();
        double[]? color = a.TextColor?.Components;
        int rebuilt = 0;
        foreach (PdfFormField t in targets)
        {
            ct.ThrowIfCancellationRequested();

            // 12.7.4.3: /DA is inheritable. The terminal field gets its own string, built from what it inherits, so siblings under the
            // same parent keep theirs.
            if (a.FontSize is not null || color is not null)
            {
                string? inherited = (t.InheritedValue("DA") as PdfString)?.GetText() ?? formDa;
                t.Dictionary.SetEntry("DA", PdfString.FromText(ModifyDa(inherited, a.FontSize, color)));
            }

            foreach (PdfDictionary w in t.Widgets)
            {
                if (!ReferenceEquals(w, t.Dictionary) && (a.FontSize is not null || color is not null) && w.GetString("DA") is { } own)
                {
                    w.SetEntry("DA", PdfString.FromText(ModifyDa(own.GetText(), a.FontSize, color)));
                }

                if (a.Background is not null || a.BorderColor is not null)
                {
                    PdfDictionary mk = OwnMk(doc, w);
                    SetPaint(mk, "BG", a.Background);
                    SetPaint(mk, "BC", a.BorderColor);
                }

                rebuilt += Rebuild(doc, t, w, session) ? 1 : 0;
                session.Touched.Add(w);
            }
        }

        return rebuilt;
    }

    private static void SetPaint(PdfDictionary mk, string key, PdfFieldColor? c)
    {
        if (c is not { } v)
        {
            return;
        }

        if (v.IsTransparent)
        {
            mk.RemoveEntry(key);
        }
        else
        {
            mk.SetEntry(key, Cos.Array(v.Components));
        }
    }

    // The widget's own /MK as a direct dictionary: an indirect one may be shared with other widgets, so it is copied first.
    private static PdfDictionary OwnMk(PdfDocument doc, PdfDictionary w)
    {
        if (w.GetRaw("MK") is PdfDictionary { Reference: null } direct)
        {
            return direct;
        }

        var copy = new PdfDictionary();
        if (w.GetDictionary("MK") is { } old)
        {
            foreach (string k in old.Keys.ToArray())
            {
                if (old.GetRaw(k) is { } v)
                {
                    copy.SetEntry(k, FormFiller.Clone(doc, v));
                }
            }
        }

        w.SetEntry("MK", copy);
        return w.GetDictionary("MK")!;
    }

    // Stores new normal appearances. Check boxes and radio buttons: every state of /AP /N is redrawn (the generator draws the frame
    // and, for an on-state, the mark chosen by /MK /CA), so authored art for the states is replaced by generated art.
    private static bool Rebuild(PdfDocument doc, PdfFormField t, PdfDictionary w, FormSession session)
    {
        if (t.Kind is not (PdfFieldKind.CheckBox or PdfFieldKind.RadioButton) || w.GetDictionary("AP")?.GetDictionary("N") is not { } states)
        {
            return FormFiller.StoreNormal(doc, w, session);
        }

        PdfObject? savedAs = w.GetRaw("AS");
        bool any = false;
        try
        {
            foreach (string state in states.Keys.ToArray())
            {
                w.SetEntry("AS", new PdfName(state));
                if (FormFiller.Generate(doc, w) is { } gen)
                {
                    states.SetEntry(state, FormFiller.StoreForm(doc, gen, session));
                    any = true;
                }
            }
        }
        finally
        {
            w.SetEntry("AS", savedAs);
        }

        return any;
    }

    // Replaces the size of the last Tf and the last g/rg/k colour of a default appearance string, keeping every other operator.
    // Without a Tf the font is /Helv (the usual AcroForm default) so that a size can be stated.
    internal static string ModifyDa(string? da, double? size, double[]? color)
    {
        var tok = new List<string>((da ?? string.Empty).Split([' ', '\t', '\r', '\n', '\f', '\0'], StringSplitOptions.RemoveEmptyEntries));
        if (size is { } s)
        {
            int tf = tok.FindLastIndex(static x => x == "Tf");
            if (tf >= 2)
            {
                tok[tf - 1] = Num(s);
            }
            else
            {
                tok.InsertRange(0, ["/Helv", Num(s), "Tf"]);
            }
        }

        if (color is not null)
        {
            string[] fresh = [.. color.Select(Num), color.Length switch { 1 => "g", 3 => "rg", _ => "k" }];
            int at = -1, len = 0;
            for (int i = 0; i < tok.Count; i++)
            {
                int n = tok[i] switch { "g" => 1, "rg" => 3, "k" => 4, _ => 0 };
                if (n > 0 && i >= n)
                {
                    at = i - n;
                    len = n + 1;
                }
            }

            if (at >= 0)
            {
                tok.RemoveRange(at, len);
                tok.InsertRange(at, fresh);
            }
            else
            {
                tok.AddRange(fresh);
            }
        }

        return string.Join(' ', tok);
    }

    private static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}
