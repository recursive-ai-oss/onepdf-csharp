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
            // A stream always knows its document (CreateStream), so it can be added even when the container does not belong to one yet.
            return (target ?? own!).Doc.AddObject(stream);
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
        // Most values are scalars or containers of scalars, which need neither a work stack nor a visited set; both are made when the
        // first nested container turns up.
        Stack<PdfObject>? stack = null;
        HashSet<PdfObject>? seen = null;
        PdfObject node = root;
        while (true)
        {
            if (seen is null || seen.Add(node))
            {
                node.OwnerPlusOne = owner + 1;
                switch (node)
                {
                    case PdfStream s:
                        Push(ref stack, ref seen, root, resolver, s.Dictionary);
                        break;

                    case PdfDictionary d:
                        if (resolver is not null)
                        {
                            if (d.Resolver is null)
                            {
                                d.Resolver = resolver;
                            }
                            else if (d.Resolver.Doc != resolver.Doc)
                            {
                                throw new ArgumentException("The value belongs to another document. Copy it into this one first.");
                            }
                        }

                        for (int i = 0; i < d.RawCount; i++)
                        {
                            Child(d.ValueAt(i), ref stack, ref seen, root, resolver);
                        }

                        break;

                    case PdfArray a:
                        if (resolver is not null)
                        {
                            if (a.Resolver is null)
                            {
                                a.Resolver = resolver;
                            }
                            else if (a.Resolver.Doc != resolver.Doc)
                            {
                                throw new ArgumentException("The value belongs to another document. Copy it into this one first.");
                            }
                        }

                        for (int i = 0; i < a.Count; i++)
                        {
                            Child(a.GetRaw(i), ref stack, ref seen, root, resolver);
                        }

                        break;
                }
            }

            if (stack is null || stack.Count == 0)
            {
                return;
            }

            node = stack.Pop();
        }
    }

    private static void Push(ref Stack<PdfObject>? stack, ref HashSet<PdfObject>? seen, PdfObject root, PdfResolver? resolver, PdfObject child)
    {
        stack ??= new Stack<PdfObject>();
        if (seen is null && resolver is not null)
        {
            // Everything visited so far is the root alone.
            seen = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance) { root };
        }

        stack.Push(child);
    }

    private static void Child(PdfObject child, ref Stack<PdfObject>? stack, ref HashSet<PdfObject>? seen, PdfObject root, PdfResolver? resolver)
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
            Push(ref stack, ref seen, root, resolver, child);
        }
    }
}
