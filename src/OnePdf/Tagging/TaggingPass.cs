using OnePdf.Content;
using OnePdf.Text;

namespace OnePdf.Tagging;

/// <summary>
/// One call of <see cref="PdfDocument.ApplyTagging"/>. Phase one reads every page and builds its new content without touching the
/// document's own objects (so a plan that cannot be applied stops with nothing changed); phase two writes the new content, the
/// structure tree and the parent tree.
/// </summary>
internal sealed class TaggingPass
{
    private sealed class Node
    {
        public PdfTagElement? Element;
        public int Parent = -1;
        public readonly List<object> Kids = []; // PdfTagElement or TagSlot
        public PdfReference Reference = null!;
    }

    private sealed class PageWork
    {
        public PdfPageText? Text;
        public readonly List<TagSlot> Slots = [];
        public int[] Owners = [];
        public HashSet<PdfTextLine>? Lines;
    }

    private readonly PdfDocument _doc;
    private readonly PdfTaggingPlan _plan;
    private readonly CancellationToken _ct;
    private readonly PdfTaggingResult _result = new();
    private readonly List<Node> _nodes = [];
    private readonly List<TagSlot> _slots = [];
    private readonly Dictionary<PdfTagElement, int> _index = new(ReferenceEqualityComparer.Instance);
    private readonly SortedDictionary<int, PageWork> _pages = [];
    private readonly List<PageOutcome> _outcomes = [];

    private TaggingPass(PdfDocument doc, PdfTaggingPlan plan, CancellationToken ct)
    {
        _doc = doc;
        _plan = plan;
        _ct = ct;
    }

    public static PdfTaggingResult Run(PdfDocument doc, PdfTaggingPlan plan, CancellationToken ct)
    {
        doc.EnsureEditable(0);
        return new TaggingPass(doc, plan, ct).Execute();
    }

    private PdfTaggingResult Execute()
    {
        if (!ReferenceEquals(_plan.Document, _doc))
        {
            throw new ArgumentException("The plan was made for another document.", nameof(_plan));
        }

        if (_plan.Applied)
        {
            throw new InvalidOperationException("The plan has been applied already.");
        }

        if (_doc.Catalog.GetDictionary("StructTreeRoot") is not null || _doc.Catalog.GetDictionary("MarkInfo")?.GetBool("Marked") == true)
        {
            throw new PdfTaggingException(PdfTaggingFailure.AlreadyTagged, "The document already has a structure tree or declares itself tagged; OnePDF tags untagged files only.");
        }

        Flatten();
        Resolve();
        int pageCount = _doc.Pages.Count;
        for (int i = 0; i < pageCount; i++)
        {
            _ct.ThrowIfCancellationRequested();
            _outcomes.Add(RunPage(i));
        }

        if (_doc.GetSignatures().Count > 0)
        {
            _result.WarningList.Add("The document has signatures. Tagging changes the signed content, so a full save invalidates them; an incremental save keeps the signed revision.");
        }

        Write();
        _plan.Applied = true;
        return _result;
    }

    // ---- the plan as nodes and slots ----

    private void Flatten()
    {
        PdfLimits limits = _doc.Limits;
        _nodes.Add(new Node()); // 0: the document element
        foreach (object item in _plan.Items)
        {
            if (item is PdfTagContent c)
            {
                _nodes[0].Kids.Add(NewSlot(c, null, 0, false, PdfArtifactKind.Plain));
            }
            else
            {
                _nodes[0].Kids.Add(item);
            }
        }

        var stack = new Stack<(PdfTagElement Element, int Parent)>();
        for (int i = _plan.Items.Count - 1; i >= 0; i--)
        {
            if (_plan.Items[i] is PdfTagElement e)
            {
                stack.Push((e, 0));
            }
        }

        while (stack.Count > 0)
        {
            (PdfTagElement el, int parent) = stack.Pop();
            if (_nodes.Count > limits.MaxObjects / 2)
            {
                throw new PdfLimitException("The structure tree holds more elements than the object limit allows.");
            }

            PdfTagRole? parentRole = parent == 0 ? null : _nodes[parent].Element!.Role;
            if (!ParentAllows(parentRole, el.Role))
            {
                throw new PdfTaggingException(PdfTaggingFailure.InvalidNesting, $"A {TagText.RoleName(el.Role)} element cannot be a child of {(parentRole is null ? "the document" : "a " + TagText.RoleName(parentRole.Value))} element.");
            }

            int self = _nodes.Count;
            var node = new Node { Element = el, Parent = parent };
            _nodes.Add(node);
            _index[el] = self;
            foreach (object item in el.Items)
            {
                if (item is PdfTagContent c)
                {
                    if (RestrictsContent(el.Role))
                    {
                        throw new PdfTaggingException(PdfTaggingFailure.InvalidNesting, $"A {TagText.RoleName(el.Role)} element cannot hold content directly; it holds {ChildrenOf(el.Role)} elements.");
                    }

                    node.Kids.Add(NewSlot(c, el, self, false, PdfArtifactKind.Plain));
                }
                else
                {
                    node.Kids.Add(item);
                }
            }

            for (int i = el.Items.Count - 1; i >= 0; i--)
            {
                if (el.Items[i] is PdfTagElement child)
                {
                    stack.Push((child, self));
                }
            }
        }

        foreach ((PdfTagContent content, PdfArtifactKind kind, bool soft) in _plan.Artifacts)
        {
            NewSlot(content, null, -1, true, kind).Soft = soft;
        }
    }

    private TagSlot NewSlot(PdfTagContent content, PdfTagElement? element, int node, bool artifact, PdfArtifactKind kind)
    {
        var slot = new TagSlot
        {
            Id = _slots.Count,
            Element = element,
            Artifact = artifact,
            Content = content,
            Kind = kind,
            PageIndex = content.PageIndex,
            Tag = element is null ? "Span" : TagText.RoleName(element.Role),
            NodeIndex = node,
        };
        _slots.Add(slot);
        return slot;
    }

    private static bool ParentAllows(PdfTagRole? parent, PdfTagRole child)
    {
        bool placed = child switch
        {
            PdfTagRole.TR => parent is PdfTagRole.Table or PdfTagRole.THead or PdfTagRole.TBody or PdfTagRole.TFoot,
            PdfTagRole.TH or PdfTagRole.TD => parent == PdfTagRole.TR,
            PdfTagRole.THead or PdfTagRole.TBody or PdfTagRole.TFoot => parent == PdfTagRole.Table,
            PdfTagRole.LI => parent == PdfTagRole.L,
            PdfTagRole.Lbl or PdfTagRole.LBody => parent == PdfTagRole.LI,
            PdfTagRole.TOCI => parent == PdfTagRole.TOC,
            _ => true,
        };
        if (!placed || parent is null)
        {
            return placed;
        }

        return parent.Value switch
        {
            PdfTagRole.L => child is PdfTagRole.LI or PdfTagRole.Caption or PdfTagRole.L,
            PdfTagRole.Table => child is PdfTagRole.TR or PdfTagRole.THead or PdfTagRole.TBody or PdfTagRole.TFoot or PdfTagRole.Caption,
            PdfTagRole.THead or PdfTagRole.TBody or PdfTagRole.TFoot => child == PdfTagRole.TR,
            PdfTagRole.TR => child is PdfTagRole.TH or PdfTagRole.TD,
            PdfTagRole.LI => child is PdfTagRole.Lbl or PdfTagRole.LBody,
            PdfTagRole.TOC => child is PdfTagRole.TOCI or PdfTagRole.TOC or PdfTagRole.Caption,
            _ => true,
        };
    }

    private static bool RestrictsContent(PdfTagRole role) =>
        role is PdfTagRole.L or PdfTagRole.Table or PdfTagRole.THead or PdfTagRole.TBody or PdfTagRole.TFoot or PdfTagRole.TR or PdfTagRole.LI or PdfTagRole.TOC;

    private static string ChildrenOf(PdfTagRole role) => role switch
    {
        PdfTagRole.L => "LI",
        PdfTagRole.Table => "TR, THead, TBody and TFoot",
        PdfTagRole.THead or PdfTagRole.TBody or PdfTagRole.TFoot => "TR",
        PdfTagRole.TR => "TH and TD",
        PdfTagRole.LI => "Lbl and LBody",
        _ => "TOCI",
    };

    // ---- references to glyphs, marks and annotations ----

    private void Resolve()
    {
        int pageCount = _doc.Pages.Count;
        Dictionary<PdfDictionary, int>? annotationPages = null;
        var annotationsSeen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (TagSlot slot in _slots)
        {
            PdfTagContent c = slot.Content;
            if (c.Kind == PdfTagContent.Kinds.Annotation)
            {
                if (annotationPages is null)
                {
                    annotationPages = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
                    for (int p = 0; p < pageCount; p++)
                    {
                        foreach (PdfAnnotation a in _doc.Pages[p].Annotations)
                        {
                            annotationPages.TryAdd(a.Dictionary, p);
                        }
                    }
                }

                PdfDictionary ad = c.SourceAnnotation!.Dictionary;
                if (!annotationPages.TryGetValue(ad, out int ap) || ad.Reference is null)
                {
                    throw new PdfTaggingException(PdfTaggingFailure.ForeignContent, "An annotation of the plan is not an indirect object of one of this document's pages.");
                }

                if (!annotationsSeen.Add(ad))
                {
                    throw new PdfTaggingException(PdfTaggingFailure.DuplicateContent, "An annotation is referenced more than once.", ap);
                }

                slot.PageIndex = ap;
                Work(ap).Slots.Add(slot);
                continue;
            }

            if (slot.PageIndex < 0 || slot.PageIndex >= pageCount)
            {
                throw new PdfTaggingException(PdfTaggingFailure.ForeignContent, $"The plan names page {slot.PageIndex + 1}, which the document does not have.", slot.PageIndex);
            }

            PageWork pw = Work(slot.PageIndex);
            pw.Slots.Add(slot);
            if (c.Page is { } text)
            {
                if (!ReferenceEquals(text.Document, _doc))
                {
                    throw new PdfTaggingException(PdfTaggingFailure.ForeignContent, "A reference is to a page that was not extracted from this document.", slot.PageIndex);
                }

                pw.Text ??= text;
            }
        }

        foreach ((int pageIndex, PageWork pw) in _pages)
        {
            if (pw.Text is null)
            {
                continue;
            }

            PdfPageText text = pw.Text;
            if (text.SourceGlyphCount > 0 && text.Glyphs.Count == 0 || text.GlyphsWereDropped)
            {
                throw new PdfTaggingException(PdfTaggingFailure.ForeignContent, "The page was extracted without its glyphs; extract with RetainGlyphs set to true.", pageIndex);
            }

            pw.Owners = new int[text.SourceGlyphCount];
            Array.Fill(pw.Owners, -1);
            foreach (bool soft in (bool[])[false, true])
            {
                foreach (TagSlot slot in pw.Slots)
                {
                    if (slot.Soft == soft && slot.Content.Kind is PdfTagContent.Kinds.Block or PdfTagContent.Kinds.Lines or PdfTagContent.Kinds.Words)
                    {
                        Claim(pw, slot, pageIndex);
                    }
                }
            }

            Inherit(pw, text);
        }
    }

    private PageWork Work(int pageIndex)
    {
        if (!_pages.TryGetValue(pageIndex, out PageWork? pw))
        {
            _pages[pageIndex] = pw = new PageWork();
        }

        return pw;
    }

    private void Claim(PageWork pw, TagSlot slot, int pageIndex)
    {
        PdfPageText text = pw.Text!;
        PdfTagContent c = slot.Content;
        IReadOnlyList<PdfTextGlyph> glyphs = text.Glyphs;
        int claimed = 0;

        void Take(PdfTextWord word)
        {
            foreach (int gi in word.GlyphIndices)
            {
                if ((uint)gi >= (uint)glyphs.Count)
                {
                    throw new PdfTaggingException(PdfTaggingFailure.ForeignContent, "A word refers to a glyph the page does not have.", pageIndex);
                }

                int src = glyphs[gi].SourceIndex;
                if (src < 0 || src >= pw.Owners.Length)
                {
                    throw new PdfTaggingException(PdfTaggingFailure.ForeignContent, "A glyph of the page did not come from the document's content.", pageIndex);
                }

                if (pw.Owners[src] >= 0)
                {
                    if (slot.Soft)
                    {
                        continue;
                    }

                    if (!_slots[pw.Owners[src]].Soft)
                    {
                        throw new PdfTaggingException(PdfTaggingFailure.DuplicateContent, $"Page {pageIndex + 1}: a glyph ('{glyphs[gi].Text}') is referenced more than once.", pageIndex);
                    }
                }

                pw.Owners[src] = slot.Id;
                claimed++;
            }
        }

        void TakeLine(PdfTextLine line)
        {
            foreach (PdfTextWord w in line.Words)
            {
                Take(w);
            }
        }

        switch (c.Kind)
        {
            case PdfTagContent.Kinds.Block:
                CheckBlock(text, c.SourceBlock!, pageIndex);
                foreach (PdfTextLine line in c.SourceBlock!.Lines)
                {
                    TakeLine(line);
                }

                break;
            case PdfTagContent.Kinds.Lines:
                CheckBlock(text, c.SourceBlock!, pageIndex);
                for (int i = c.First; i < c.First + c.Count; i++)
                {
                    TakeLine(c.SourceBlock!.Lines[i]);
                }

                break;
            default:
                if (pw.Lines is null)
                {
                    pw.Lines = new HashSet<PdfTextLine>(ReferenceEqualityComparer.Instance);
                    foreach (PdfTextBlock b in text.Blocks)
                    {
                        foreach (PdfTextLine l in b.Lines)
                        {
                            pw.Lines.Add(l);
                        }
                    }
                }

                if (!pw.Lines.Contains(c.Line!))
                {
                    throw new PdfTaggingException(PdfTaggingFailure.ForeignContent, "A line of the plan is not a line of the page it names.", pageIndex);
                }

                for (int i = c.First; i < c.First + c.Count; i++)
                {
                    Take(c.Line!.Words[i]);
                }

                break;
        }

        if (claimed == 0 && !slot.Soft)
        {
            _result.WarningList.Add($"Page {pageIndex + 1}: a reference to text matched no glyphs.");
        }
    }

    private static void CheckBlock(PdfPageText text, PdfTextBlock block, int pageIndex)
    {
        foreach (PdfTextBlock b in text.Blocks)
        {
            if (ReferenceEquals(b, block))
            {
                return;
            }
        }

        throw new PdfTaggingException(PdfTaggingFailure.ForeignContent, "A block of the plan is not a block of the page it names.", pageIndex);
    }

    // A blank glyph (a space the font draws) that nobody claimed goes with the text next to it, so a paragraph is not cut up by its spaces.
    private static void Inherit(PageWork pw, PdfPageText text)
    {
        IReadOnlyList<PdfTextGlyph> glyphs = text.Glyphs;
        int prev = -1;
        for (int i = 0; i < glyphs.Count; i++)
        {
            int src = glyphs[i].SourceIndex;
            if (src < 0 || src >= pw.Owners.Length)
            {
                continue;
            }

            if (pw.Owners[src] < 0 && IsBlank(glyphs[i].Text) && prev >= 0)
            {
                pw.Owners[src] = prev;
            }

            prev = pw.Owners[src];
        }

        int next = -1;
        for (int i = glyphs.Count - 1; i >= 0; i--)
        {
            int src = glyphs[i].SourceIndex;
            if (src < 0 || src >= pw.Owners.Length)
            {
                continue;
            }

            if (pw.Owners[src] < 0 && IsBlank(glyphs[i].Text) && next >= 0)
            {
                pw.Owners[src] = next;
            }

            next = pw.Owners[src];
        }
    }

    private static bool IsBlank(string t)
    {
        foreach (char ch in t)
        {
            if (!char.IsWhiteSpace(ch))
            {
                return false;
            }
        }

        return true;
    }

    // ---- phase one: a page ----

    private PageOutcome RunPage(int pageIndex)
    {
        PdfPage page = _doc.Pages[pageIndex];
        _pages.TryGetValue(pageIndex, out PageWork? pw);
        var tagger = new ContentTagger(_doc, page, pageIndex, _slots, pw?.Owners ?? []);
        try
        {
            ContentInterpreter.Run(page, tagger, PdfGlyphSource.InterpretOptions, tagger, _ct);
            if (tagger.FailReason is { } why)
            {
                throw new PdfTaggingException(PdfTaggingFailure.ContentUnreadable, $"Page {pageIndex + 1} cannot be tagged safely: {why}.", pageIndex);
            }

            if (pw?.Text is { } text && text.SourceGlyphCount != tagger.GlyphCount)
            {
                throw new PdfTaggingException(PdfTaggingFailure.ContentChanged, $"Page {pageIndex + 1}: the page shows {tagger.GlyphCount} glyphs now but the extraction saw {text.SourceGlyphCount}; extract again.", pageIndex);
            }

            PageOutcome outcome = tagger.Outcome();
            long added = outcome.Scope.AddedOperators;
            foreach (FormCopy c in outcome.Copies)
            {
                added += c.Scope.AddedOperators;
            }

            if (outcome.ParsedOperators + added > _doc.Limits.MaxOperatorsPerPage)
            {
                throw new PdfLimitException($"Page {pageIndex + 1} would have more content operators than the limit allows once it is tagged.");
            }

            return outcome;
        }
        catch (PdfException e) when (e is not (PdfTaggingException or PdfLimitException))
        {
            throw new PdfTaggingException(PdfTaggingFailure.ContentUnreadable, $"Page {pageIndex + 1} cannot be tagged safely: {e.Message}", pageIndex);
        }
        finally
        {
            tagger.Abandon();
        }
    }

    // ---- phase two ----

    private static int[] OrderOf(FormCopy copy)
    {
        var chain = new List<int>();
        FormCopy? c = copy;
        int guard = 0;
        while (c is not null && guard++ < 1024)
        {
            chain.Add(c.DoPosition);
            TagCtx parent = c.ParentCtx;
            if (parent.IsPage)
            {
                chain.Add(parent.PieceIndex);
                c = null;
            }
            else
            {
                c = parent.CopyOwner ?? throw new PdfTaggingException(PdfTaggingFailure.ContentUnreadable, "A form copy has no copied parent.");
            }
        }

        chain.Reverse();
        return [.. chain];
    }

    private void Write()
    {
        long limit = _doc.Limits.MaxDecodedStreamBytes;

        // The form copies, innermost first (a copy is made before the form that uses it).
        foreach (PageOutcome o in _outcomes)
        {
            foreach (FormCopy copy in o.Copies)
            {
                Materialize(copy, limit);
                _result.FormsCopied++;
            }
        }

        // Where the sequences of every slot are.
        foreach (PageOutcome o in _outcomes)
        {
            Collect(o.Scope, [], pageScope: true);
            foreach (FormCopy copy in o.Copies)
            {
                Collect(copy.Scope, OrderOf(copy), pageScope: false);
            }
        }

        foreach (TagSlot s in _slots)
        {
            s.Items.Sort(static (a, b) => CompareOrder(a.Order, b.Order));
        }

        // Elements.
        PdfReference root = _doc.AddObject(new PdfDictionary());
        foreach (Node n in _nodes)
        {
            n.Reference = _doc.AddObject(new PdfDictionary());
        }

        // Keys of the parent tree.
        int nextKey = 0;
        foreach (PageOutcome o in _outcomes)
        {
            if (o.Scope.Slots.Count > 0)
            {
                o.Scope.StructParents = nextKey++;
            }

            foreach (FormCopy copy in o.Copies)
            {
                if (copy.Scope.Slots.Count > 0)
                {
                    copy.Scope.StructParents = nextKey++;
                    copy.Dictionary?.SetEntry("StructParents", new PdfInteger(copy.Scope.StructParents));
                }
            }
        }

        var treeEntries = new List<(PdfObject Key, PdfObject Value)>();
        foreach (PageOutcome o in _outcomes)
        {
            PdfPage page = _doc.Pages[o.PageIndex];
            PdfDictionary pd = page.Dictionary;
            if (o.Streams is { } streams)
            {
                if (streams.Count == 1)
                {
                    pd.SetEntry("Contents", _doc.AddObject(_doc.CreateStream(streams[0], compress: true)));
                }
                else
                {
                    var arr = new PdfArray();
                    foreach (byte[] s in streams)
                    {
                        arr.Append(_doc.AddObject(_doc.CreateStream(s, compress: true)));
                    }

                    pd.SetEntry("Contents", arr);
                }

                _result.PagesChanged++;
            }

            if (o.Resources.Owned is { } owned)
            {
                pd.SetEntry("Resources", owned);
            }

            if (o.Scope.StructParents >= 0)
            {
                pd.SetEntry("StructParents", new PdfInteger(o.Scope.StructParents));
                treeEntries.Add((new PdfInteger(o.Scope.StructParents), ParentArray(o.Scope)));
            }

            foreach (FormCopy copy in o.Copies)
            {
                if (copy.Scope.StructParents >= 0)
                {
                    treeEntries.Add((new PdfInteger(copy.Scope.StructParents), ParentArray(copy.Scope)));
                }
            }

            _result.ContentSequences += CountSequences(o.Scope);
            _result.ArtifactSequences += o.Scope.ArtifactSequences;
            foreach (FormCopy copy in o.Copies)
            {
                _result.ContentSequences += CountSequences(copy.Scope);
                _result.ArtifactSequences += copy.Scope.ArtifactSequences;
            }
        }

        // The elements' dictionaries.
        for (int i = 0; i < _nodes.Count; i++)
        {
            Node n = _nodes[i];
            PdfDictionary d = _doc.GetObject(n.Reference.Number) as PdfDictionary ?? throw new InvalidOperationException("A new element is missing.");
            d.SetEntry("Type", new PdfName("StructElem"));
            d.SetEntry("S", new PdfName(n.Element is null ? "Document" : TagText.RoleName(n.Element.Role)));
            d.SetEntry("P", i == 0 ? root : _nodes[n.Parent].Reference);
            WriteKids(d, n, treeEntries);
            if (n.Element is { } el)
            {
                if (!string.IsNullOrEmpty(el.Alt))
                {
                    d.SetEntry("Alt", PdfString.FromText(el.Alt));
                }

                if (!string.IsNullOrEmpty(el.ActualText))
                {
                    d.SetEntry("ActualText", PdfString.FromText(el.ActualText));
                }

                if (!string.IsNullOrEmpty(el.Lang))
                {
                    d.SetEntry("Lang", PdfString.FromText(el.Lang));
                }

                PdfDictionary? attr = Attributes(el);
                if (attr is not null)
                {
                    d.SetEntry("A", attr);
                }
            }
        }

        _result.ElementsWritten = _nodes.Count;
        nextKey = Math.Max(nextKey, _annotationKey);
        treeEntries.Sort(static (a, b) => ((PdfInteger)a.Key).Value.CompareTo(((PdfInteger)b.Key).Value));
        PdfDictionary parentTree = NumberTree.Build(treeEntries, names: false, d => _doc.AddObject(d));
        var rootDict = (PdfDictionary)_doc.GetObject(root.Number)!;
        rootDict.SetEntry("Type", new PdfName("StructTreeRoot"));
        var rootK = new PdfArray();
        rootK.Append(_nodes[0].Reference);
        rootDict.SetEntry("K", rootK);
        rootDict.SetEntry("ParentTree", parentTree);
        rootDict.SetEntry("ParentTreeNextKey", new PdfInteger(nextKey));

        PdfDictionary catalog = _doc.Catalog;
        catalog.SetEntry("StructTreeRoot", root);
        var mark = new PdfDictionary();
        mark.SetEntry("Marked", new PdfBoolean(true));
        catalog.SetEntry("MarkInfo", mark);
        if (!string.IsNullOrWhiteSpace(_plan.Language))
        {
            catalog.SetEntry("Lang", PdfString.FromText(_plan.Language));
        }

        if (!string.IsNullOrWhiteSpace(_plan.Title))
        {
            _doc.SetInfo("Title", _plan.Title);
        }

        _doc.ResetPageViews();
    }

    private static int CountSequences(McScope s) => s.Slots.Count;

    private static int CompareOrder(int[] a, int[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            if (a[i] != b[i])
            {
                return a[i].CompareTo(b[i]);
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    private void Collect(McScope scope, int[] prefix, bool pageScope)
    {
        for (int m = 0; m < scope.Slots.Count; m++)
        {
            int[] order = new int[prefix.Length + (pageScope ? 3 : 2)];
            prefix.CopyTo(order, 0);
            int at = prefix.Length;
            if (pageScope)
            {
                order[at++] = scope.Pieces[m];
            }

            order[at++] = scope.Positions[m];
            order[at] = m;
            _slots[scope.Slots[m]].Items.Add(new ContentItem(scope, m, order));
        }
    }

    private PdfReference ParentArray(McScope scope)
    {
        var arr = new PdfArray();
        foreach (int slotId in scope.Slots)
        {
            TagSlot s = _slots[slotId];
            arr.Append(_nodes[s.NodeIndex].Reference);
        }

        return _doc.AddObject(arr);
    }

    private void Materialize(FormCopy copy, long limit)
    {
        byte[] bytes = TagText.Apply(copy.Data, copy.Edits, limit);
        PdfStream stream = _doc.CreateStream(bytes, compress: true);
        PdfDictionary nd = stream.Dictionary;
        foreach (string k in (string[])[.. copy.Original.Dictionary.Keys])
        {
            if (k is "Length" or "Filter" or "DecodeParms" or "F" or "FFilter" or "FDecodeParms" or "DL" or "Resources" or "StructParents")
            {
                continue;
            }

            if (copy.Original.Dictionary.GetRaw(k) is { } raw)
            {
                nd.SetEntry(k, Forms.FormFiller.Clone(_doc, raw));
            }
        }

        if (copy.Resources.Edited)
        {
            nd.SetEntry("Resources", copy.Resources.GetOwned());
        }
        else if (copy.Original.Dictionary.GetRaw("Resources") is { } res)
        {
            nd.SetEntry("Resources", Forms.FormFiller.Clone(_doc, res));
        }

        copy.Dictionary = nd;
        copy.Reference = _doc.AddObject(stream);
        if (copy.ParentResources.Owned?.GetDictionary("XObject") is { } xo)
        {
            xo.SetEntry(copy.NameInParent, copy.Reference);
        }
    }

    private void WriteKids(PdfDictionary d, Node n, List<(PdfObject Key, PdfObject Value)> treeEntries)
    {
        var k = new PdfArray();
        int elementPage = -1;
        foreach (object kid in n.Kids)
        {
            if (kid is TagSlot s)
            {
                foreach (ContentItem it in s.Items)
                {
                    if (it.Scope.Copy is null)
                    {
                        elementPage = it.Scope.PageIndex;
                        break;
                    }
                }

                if (elementPage >= 0)
                {
                    break;
                }
            }
        }

        foreach (object kid in n.Kids)
        {
            if (kid is PdfTagElement child)
            {
                k.Append(_nodes[_index[child]].Reference);
                continue;
            }

            var slot = (TagSlot)kid;
            if (slot.Content.Kind == PdfTagContent.Kinds.Annotation)
            {
                PdfDictionary ad = slot.Content.SourceAnnotation!.Dictionary;
                var objr = new PdfDictionary();
                objr.SetEntry("Type", new PdfName("OBJR"));
                objr.SetEntry("Pg", _doc.Pages[slot.PageIndex].Dictionary.Reference!);
                objr.SetEntry("Obj", ad.Reference!);
                k.Append(objr);
                int key = NextAnnotationKey++;
                ad.SetEntry("StructParent", new PdfInteger(key));
                AddLinkDescription(ad, n);
                treeEntries.Add((new PdfInteger(key), n.Reference));
                _result.AnnotationsTagged++;
                continue;
            }

            if (slot.Items.Count == 0 && !slot.IsArtifact && slot.Content.Kind != PdfTagContent.Kinds.Annotation)
            {
                _result.WarningList.Add($"A {slot.Tag} element has a reference that matched no painted content.");
            }

            foreach (ContentItem it in slot.Items)
            {
                if (it.Scope.Copy is null && it.Scope.PageIndex == elementPage)
                {
                    k.Append(new PdfInteger(it.Mcid));
                }
                else
                {
                    var mcr = new PdfDictionary();
                    mcr.SetEntry("Type", new PdfName("MCR"));
                    mcr.SetEntry("Pg", _doc.Pages[it.Scope.PageIndex].Dictionary.Reference!);
                    if (it.Scope.Copy is { } copy)
                    {
                        mcr.SetEntry("Stm", copy.Reference!);
                    }

                    mcr.SetEntry("MCID", new PdfInteger(it.Mcid));
                    k.Append(mcr);
                }
            }
        }

        d.SetEntry("K", k);
        if (elementPage >= 0)
        {
            d.SetEntry("Pg", _doc.Pages[elementPage].Dictionary.Reference!);
        }
    }

    // ISO 14289-1 7.18.5: a link annotation carries /Contents saying what the link is. A tagged link without one gets the element's
    // alternate description, else the link's own words, so the description is what a sighted reader sees.
    private static void AddLinkDescription(PdfDictionary annotation, Node n)
    {
        if (annotation.GetName("Subtype") != "Link" || !string.IsNullOrWhiteSpace(annotation.GetString("Contents")?.GetText()))
        {
            return;
        }

        string text = n.Element?.Alt ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            var sb = new System.Text.StringBuilder();
            foreach (object kid in n.Kids)
            {
                if (kid is TagSlot { Content: { } c } && c.Kind != PdfTagContent.Kinds.Annotation && c.Kind != PdfTagContent.Kinds.Graphics)
                {
                    AppendWords(sb, c);
                }
            }

            text = sb.ToString().Trim();
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            annotation.SetEntry("Contents", PdfString.FromText(text));
        }
    }

    private static void AppendWords(System.Text.StringBuilder sb, PdfTagContent c)
    {
        void Line(PdfTextLine line, int from, int count)
        {
            for (int i = from; i < from + count && i < line.Words.Count; i++)
            {
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(line.Words[i].Text);
            }
        }

        switch (c.Kind)
        {
            case PdfTagContent.Kinds.Words when c.Line is { } line:
                Line(line, c.First, c.Count);
                break;
            case PdfTagContent.Kinds.Lines when c.SourceBlock is { } block:
                for (int i = c.First; i < c.First + c.Count && i < block.Lines.Count; i++)
                {
                    Line(block.Lines[i], 0, block.Lines[i].Words.Count);
                }

                break;
            case PdfTagContent.Kinds.Block when c.SourceBlock is { } whole:
                foreach (PdfTextLine l in whole.Lines)
                {
                    Line(l, 0, l.Words.Count);
                }

                break;
        }
    }

    // Annotation keys follow the page and form keys, which were counted first.
    private int NextAnnotationKey
    {
        get => _annotationKey >= 0 ? _annotationKey : _annotationKey = FirstFreeKey();
        set => _annotationKey = value;
    }

    private int _annotationKey = -1;

    private int FirstFreeKey()
    {
        int max = -1;
        foreach (PageOutcome o in _outcomes)
        {
            max = Math.Max(max, o.Scope.StructParents);
            foreach (FormCopy c in o.Copies)
            {
                max = Math.Max(max, c.Scope.StructParents);
            }
        }

        return max + 1;
    }

    private static PdfDictionary? Attributes(PdfTagElement el)
    {
        int col = Math.Max(1, el.ColSpan), row = Math.Max(1, el.RowSpan);
        if (el.Role == PdfTagRole.TH && el.Scope is { } scope)
        {
            return PdfStructureAttributes.TableHeader(scope.ToString(), col, row);
        }

        if (el.Role is PdfTagRole.TH or PdfTagRole.TD && (col > 1 || row > 1))
        {
            return PdfStructureAttributes.Table(col, row);
        }

        return null;
    }
}
