namespace OnePdf.Layout;

/// <summary>A structure element the tagger has opened, with what later decisions need to know about it.</summary>
internal sealed class TagNode(PdfStructureElement element, string type, string? language, TagNode? parent)
{
    public PdfStructureElement Element { get; } = element;

    public string Type { get; } = type;

    /// <summary>The language in force for the element's content (its own <c>/Lang</c> or the one it inherits).</summary>
    public string? Language { get; } = language;

    public TagNode? Parent { get; } = parent;
}

/// <summary>What <see cref="Tagger.Open"/> returns, to be handed back to <see cref="Tagger.Close"/>.</summary>
internal readonly struct TagScope(TagNode? node, TagNode? saved, object? owner, TagNode? parent, bool continues)
{
    /// <summary>The element, or null when nothing was opened (tagging off, inside an artifact or a figure).</summary>
    public TagNode? Node { get; } = node;

    public TagNode? Saved { get; } = saved;

    public object? Owner { get; } = owner;

    public TagNode? Parent { get; } = parent;

    public bool Continues { get; } = continues;
}

/// <summary>
/// Builds the structure tree while pages are drawn (ISO 32000-2 14.7 and 14.8). Elements draw themselves in document order, so the tree
/// follows the element tree: an element that splits over pages keeps one structure element open across them, and its content on each page
/// binds to that element. Everything that is not tagged content is written as an artifact (14.8.2.2), and the two never nest.
/// </summary>
/// <remarks>
/// All members do nothing when tagging is off, so callers need not check. Marked-content sequences are only ever opened around one
/// leaf's painting (a paragraph, an image), never around a tree of elements, which keeps them inside any <c>Save</c>/<c>Restore</c>
/// pair the leaf draws within.
/// </remarks>
internal sealed class Tagger
{
    private readonly LayoutContext _ctx;
    private readonly ILayoutTarget _target;
    private readonly Dictionary<(object, TagNode), TagNode> _open = [];
    private readonly List<TagNode> _openList = [];
    private TagNode? _root;
    private int _artifactDepth;

    public Tagger(LayoutContext ctx, ILayoutTarget target, bool enabled)
    {
        _ctx = ctx;
        _target = target;
        Enabled = enabled && target.SupportsStructure;
    }

    /// <summary>True when a structure tree is being written.</summary>
    public bool Enabled { get; }

    /// <summary>The language in force at this point of the drawing (BCP 47), or null.</summary>
    public string? Language { get; set; }

    /// <summary>The innermost element content binds to, or the document element when none is open.</summary>
    public TagNode? Current { get; private set; }

    /// <summary>True when the innermost element is one whose text is its own content: a paragraph does not need a second paragraph inside it.</summary>
    public bool TextIsDirect => Current is { Type: "P" or "Lbl" or "Link" or "H" or "H1" or "H2" or "H3" or "H4" or "H5" or "H6" or "Figure" };

    /// <summary>True inside an artifact: nothing is tagged.</summary>
    public bool InArtifact => _artifactDepth > 0;

    /// <summary>Opens the <c>Document</c> element that holds everything.</summary>
    public void Start(string? language)
    {
        if (!Enabled)
        {
            return;
        }

        Language = language;
        PdfStructureElement e = _target.BeginStructure(null, "Document", null);
        _root = new TagNode(e, "Document", language, null);
        Current = _root;
    }

    /// <summary>Ends every element still open, innermost first, and then the document element.</summary>
    public void Finish()
    {
        if (!Enabled || _root is null)
        {
            return;
        }

        for (int i = _openList.Count - 1; i >= 0; i--)
        {
            _target.EndStructure(_openList[i].Element);
        }

        _openList.Clear();
        _open.Clear();
        _target.EndStructure(_root.Element);
        _root = null;
        Current = null;
    }

    /// <summary>
    /// Opens (or, for the continuation of an element that split, re-enters) a structure element under the current one.
    /// </summary>
    /// <param name="owner">What identifies the element across pages together with its parent.</param>
    /// <param name="continued">True when this is the continuation of a piece drawn on an earlier page.</param>
    /// <param name="continues">True when the element carries on to a later page, so it stays open.</param>
    /// <param name="type">The structure type, used only when the element is new.</param>
    /// <param name="alt">Alternate text (<c>/Alt</c>), or null.</param>
    /// <param name="attributes">An attribute object (<c>/A</c>), or null; used only when the element is new.</param>
    public TagScope Open(object owner, bool continued, bool continues, string type, string? alt = null, PdfDictionary? attributes = null)
    {
        if (!Enabled || _artifactDepth > 0 || _root is null || Current is { Type: "Figure" })
        {
            return default;
        }

        TagNode parent = Current ?? _root;
        TagNode? node = null;
        if (!(continued && _open.TryGetValue((owner, parent), out node)))
        {
            string? lang = Language is { Length: > 0 } && !string.Equals(Language, parent.Language, StringComparison.Ordinal) ? Language : null;
            PdfStructureElementOptions? options = alt is null && lang is null && attributes is null
                ? null
                : new PdfStructureElementOptions { AlternateText = alt, Language = lang, Attributes = attributes };
            PdfStructureElement e = _target.BeginStructure(parent.Element, type, options);
            node = new TagNode(e, type, lang ?? parent.Language, parent);
            _openList.Add(node);
            if (continues)
            {
                _open[(owner, parent)] = node;
            }
        }

        var scope = new TagScope(node, Current, owner, parent, continues);
        Current = node;
        return scope;
    }

    /// <summary>Leaves the element: it ends unless it continues on the next page.</summary>
    public void Close(in TagScope scope)
    {
        if (scope.Node is not { } node)
        {
            return;
        }

        Current = scope.Saved;
        if (scope.Continues)
        {
            return;
        }

        _open.Remove((scope.Owner!, scope.Parent!));
        End(node);
    }

    private void End(TagNode node)
    {
        // Anything still open below the element (a piece that never got its end) closes first, so the writer's order rule holds.
        for (int i = _openList.Count - 1; i >= 0; i--)
        {
            TagNode n = _openList[i];
            if (ReferenceEquals(n, node))
            {
                _openList.RemoveAt(i);
                break;
            }
        }

        for (int i = _openList.Count - 1; i >= 0; i--)
        {
            TagNode n = _openList[i];
            for (TagNode? a = n.Parent; a is not null; a = a.Parent)
            {
                if (ReferenceEquals(a, node))
                {
                    _openList.RemoveAt(i);
                    _target.EndStructure(n.Element);
                    break;
                }
            }
        }

        _target.EndStructure(node.Element);
    }

    /// <summary>
    /// Begins tagged content bound to the current element. Returns false, and writes nothing, when tagging is off or inside an artifact;
    /// pass the result to <see cref="EndContent"/>.
    /// </summary>
    public bool BeginContent()
    {
        if (!Enabled || _artifactDepth > 0 || _root is null || _ctx.OpenPage?.Stream is not { } page)
        {
            return false;
        }

        page.BeginTaggedContent((Current ?? _root).Element);
        return true;
    }

    /// <summary>Ends what <see cref="BeginContent"/> began.</summary>
    public void EndContent(bool began)
    {
        if (began)
        {
            _ctx.OpenPage!.Builder.EndMarkedContent();
        }
    }

    /// <summary>
    /// Starts an artifact. Artifacts do not nest: inside one this only counts, so the outermost call writes the marked content. Returns
    /// whether the call counted (false when tagging is off); pass it to <see cref="EndArtifact"/>.
    /// </summary>
    public bool BeginArtifact(PdfContentBuilder b, PdfArtifactType type = PdfArtifactType.Layout, PdfArtifactSubtype subtype = PdfArtifactSubtype.None)
    {
        if (!Enabled)
        {
            return false;
        }

        if (_artifactDepth++ == 0)
        {
            b.BeginArtifact(type, subtype);
        }

        return true;
    }

    /// <summary>Ends what <see cref="BeginArtifact"/> began.</summary>
    public void EndArtifact(PdfContentBuilder b, bool began)
    {
        if (began && --_artifactDepth == 0)
        {
            b.EndMarkedContent();
        }
    }
}
