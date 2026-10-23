using OnePdf.Internal;

namespace OnePdf;

/// <summary>Shared rules for changing objects: who may be changed, how new values are stored, and which indirect object a change belongs to.</summary>
internal static class EditSupport
{
    /// <summary>Throws unless the object, read through <paramref name="resolver"/>, may be changed. Objects that belong to no document always may.</summary>
    public static void EnsureEditable(PdfResolver? resolver) => resolver?.Doc.EnsureEditable(resolver.View);

    public static void MarkDirty(PdfResolver? resolver, int owner)
    {
        if (resolver is not null && owner >= 0)
        {
            resolver.Doc.MarkDirty(owner);
        }
    }

    /// <summary>
    /// Turns a value into what is stored: an existing indirect object becomes its reference, a loose stream joins the
    /// document of <paramref name="target"/>, and a direct container is adopted (its resolver and owner are set, throughout).
    /// </summary>
    public static PdfObject Prepare(PdfObject value, PdfResolver? target, int owner)
    {
        if (value is PdfReference)
        {
            return value;
        }

        PdfResolver? own = value switch
        {
            PdfDictionary d => d.Resolver,
            PdfArray a => a.Resolver,
            PdfStream s => s.Resolver,
            _ => null,
        };
        if (own is not null && target is not null && own.Doc != target.Doc)
        {
            throw new ArgumentException("The value belongs to another document. Copy it into this one first.", nameof(value));
        }

        if (value.Reference is { } indirect)
        {
            return indirect;
        }

        if (value is PdfStream stream)
        {
            if (target is null)
            {
                throw new ArgumentException("A stream is an indirect object: add it to a document with AddObject before storing it in another object.", nameof(value));
            }

            return target.Doc.AddObject(stream);
        }

        if (target is not null)
        {
            Adopt(value, target, owner);
        }

        return value;
    }

    /// <summary>
    /// Marks every container under <paramref name="root"/> (iteratively; cycles are tolerated) as stored inside indirect
    /// object <paramref name="owner"/> and, when <paramref name="resolver"/> is given, lets it resolve references through it.
    /// </summary>
    public static void Adopt(PdfObject root, PdfResolver? resolver, int owner)
    {
        var stack = new Stack<PdfObject>();
        HashSet<PdfObject>? seen = resolver is null ? null : new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        stack.Push(root);
        while (stack.Count > 0)
        {
            PdfObject node = stack.Pop();
            if (seen is not null && !seen.Add(node))
            {
                continue;
            }

            node.OwnerPlusOne = owner + 1;
            switch (node)
            {
                case PdfStream s:
                    stack.Push(s.Dictionary);
                    break;

                case PdfDictionary d:
                    Bind(d.Resolver, resolver, v => d.Resolver = v);
                    for (int i = 0; i < d.RawCount; i++)
                    {
                        Child(d.ValueAt(i), stack, resolver);
                    }

                    break;

                case PdfArray a:
                    Bind(a.Resolver, resolver, v => a.Resolver = v);
                    for (int i = 0; i < a.Count; i++)
                    {
                        Child(a.GetRaw(i), stack, resolver);
                    }

                    break;
            }
        }
    }

    private static void Bind(PdfResolver? current, PdfResolver? wanted, Action<PdfResolver> set)
    {
        if (wanted is null)
        {
            return;
        }

        if (current is null)
        {
            set(wanted);
        }
        else if (current.Doc != wanted.Doc)
        {
            throw new ArgumentException("The value belongs to another document. Copy it into this one first.");
        }
    }

    private static void Child(PdfObject child, Stack<PdfObject> stack, PdfResolver? resolver)
    {
        if (child is PdfStream)
        {
            if (resolver is not null)
            {
                throw new ArgumentException("A stream cannot be nested directly: add it with AddObject and store the reference.");
            }

            return;
        }

        if (child is PdfDictionary or PdfArray)
        {
            stack.Push(child);
        }
    }
}
