using System.Linq;
using OnePdf.Content;
using OnePdf.Redaction;
using OnePdf.Text;

namespace OnePdf.Tagging;

/// <summary>One content stream the interpreter ran for a page: the page's content, or a form XObject a <c>Do</c> painted.</summary>
internal sealed class TagCtx
{
    public ReadOnlyMemory<byte> Data;
    public PdfDictionary? Resources;
    public bool IsPage;

    /// <summary>Content in a soft mask is not painted on the page; it is left exactly as it is.</summary>
    public bool Passthrough;

    public McScope Scope = null!;
    public ResourceEdit Res = null!;
    public int PieceIndex;

    public readonly List<Entry> Entries = [];
    public readonly List<Unit> Units = [];
    public readonly List<Frame> Frames = [];

    /// <summary>The marked-content sequences open at this point: the index of the frame for an existing artifact sequence, else -1.</summary>
    public readonly List<int> McStack = [];

    public int PathStart = -1;
    public long OperatorCount;
    public List<TagEdit> Edits = [];
    public int Seq;

    /// <summary>The summary Finish leaves for the parent: every painted mark belongs to <see cref="SingleOwner"/>, or to several owners.</summary>
    public bool Mixed;
    public int SingleOwner = -1;
    public bool SawOwner;

    /// <summary>The copy of this stream that was made, if the stream turned out to need one.</summary>
    public FormCopy? CopyOwner;

    public byte[]? Output;

    public enum EntryKind : byte
    {
        Unit,
        Boundary,
        FrameOpen,
        FrameClose,
    }

    public readonly record struct Entry(EntryKind Kind, int Index);

    public enum UnitKind : byte
    {
        Text,
        Path,
        Shading,
        Do,
        Inline,
    }

    /// <summary>A painting operator (with the path it paints): the thing that has to be tagged or marked as an artifact.</summary>
    public sealed class Unit
    {
        public UnitKind Kind;
        public int Start;
        public int End;
        public int Owner = -1;
        public int Frame = -1;
        public FormCopy? Copy;
        public byte[]? CopyBytes;
        public List<TextPiece>? Pieces;
    }

    public readonly record struct TextPiece(int Owner, byte[] Bytes);

    /// <summary>An existing artifact sequence in the stream.</summary>
    public sealed class Frame
    {
        public int OpenStart;
        public int OpenEnd;
        public int CloseStart = -1;
        public int CloseEnd = -1;
        public int Parent = -1;
        public bool Dissolve;
        public byte[] Open = [];
    }

    public void AddEdit(int start, int end, byte[] bytes) => Edits.Add(new TagEdit(start, end, bytes, Seq++));
}

/// <summary>
/// Runs one page and works out where the marked content goes: it is the device the interpreter draws into (to learn who owns what each
/// operator painted) and the tap that tells it where each operator's bytes are. The content is changed by inserting marked-content
/// operators around painting operators; the original bytes stay as they are, except where one text operator shows glyphs of several
/// owners and is split, or where a form XObject has to be copied.
/// </summary>
internal sealed class ContentTagger : PdfDevice, IContentTap
{
    private const int MixedDo = int.MinValue + 1;

    private readonly PdfDocument _doc;
    private readonly int _pageIndex;
    private readonly IReadOnlyList<TagSlot> _slots;
    private readonly int[] _owners;
    private readonly List<TagSlot> _graphics = [];
    private readonly long _limit;
    private readonly TextGlyphDevice _inner;
    private readonly List<int> _runSources = [];
    private readonly List<(int Source, int Length)> _marks = [];
    private readonly List<TagCtx> _stack = [];
    private readonly List<PdfBounds> _clips = [];
    private readonly List<FormCopy> _copies = [];
    private readonly McScope _pageScope;
    private readonly ResourceEdit _pageResources;
    private readonly List<McScope> _pieceScopes = [];
    private PdfBounds _clip = PdfBounds.Unbounded;
    private double _height;
    private int _maskDepth;
    private bool _paintSeen;
    private int _paintSlot = -1;
    private TagCtx? _child;
    private int _pieces;
    private string? _failReason;
    private long _operators;
    private readonly List<TagCtx> _pageContexts = [];

    public ContentTagger(PdfDocument doc, PdfPage page, int pageIndex, IReadOnlyList<TagSlot> slots, int[] owners)
    {
        _doc = doc;
        _pageIndex = pageIndex;
        _slots = slots;
        _owners = owners;
        _limit = doc.Limits.MaxDecodedStreamBytes;
        _inner = new TextGlyphDevice(pageIndex);
        _pageScope = new McScope { PageIndex = pageIndex };
        _pageResources = new ResourceEdit(doc, page.Resources);
        foreach (TagSlot s in slots)
        {
            if (s.IsGraphics && s.PageIndex == pageIndex)
            {
                _graphics.Add(s);
            }
        }
    }

    public string? FailReason => _failReason;

    /// <summary>How many glyphs the page's content showed that the extraction would take (see <see cref="PdfTextGlyph.SourceIndex"/>).</summary>
    public int GlyphCount => _inner.Count;

    public void Abandon() => _inner.Abandon();

    public PageOutcome Outcome()
    {
        var o = new PageOutcome
        {
            PageIndex = _pageIndex,
            Resources = _pageResources,
            Scope = _pageScope,
            PieceScopes = _pieceScopes,
            Copies = _copies,
            ParsedOperators = _operators,
        };
        foreach (TagCtx c in _pageContexts)
        {
            o.AnyEdit |= c.Edits.Count > 0;
        }

        if (o.AnyEdit)
        {
            o.Streams = [.. _pageContexts.Select(static c => c.Output!)];
        }

        return o;
    }

    public void CannotVerify(string reason) => _failReason ??= reason;

    // ---- device events: the owner of what an operator painted ----

    public override void BeginPage(in PdfPageInfo page)
    {
        _inner.BeginPage(in page);
        _height = page.Height;
    }

    public override void BeginSoftMask(in PdfSoftMaskInfo mask, in PdfGraphicsState state)
    {
        _maskDepth++;
        _inner.BeginSoftMask(in mask, in state);
    }

    public override void EndSoftMask()
    {
        _maskDepth--;
        _inner.EndSoftMask();
    }

    public override void BeginType3Glyph(in PdfType3GlyphInfo glyph, in PdfGraphicsState state) => _inner.BeginType3Glyph(in glyph, in state);

    public override void EndType3Glyph() => _inner.EndType3Glyph();

    public override void BeginAnnotation(in PdfAnnotationInfo annotation) => _inner.BeginAnnotation(in annotation);

    public override void EndAnnotation() => _inner.EndAnnotation();

    public override void PushClip(in PdfPath path, bool evenOdd, in PdfGraphicsState state)
    {
        _clips.Add(_clip);
        if (path.IsEmpty)
        {
            _clip = new PdfBounds(0, 0, 0, 0);
            return;
        }

        PdfBounds b = state.Ctm.TransformBounds(path.ControlBounds());
        if (!b.IsFinite)
        {
            return;
        }

        PdfBounds up = ToUp(b);
        _clip = new PdfBounds(Math.Max(_clip.Left, up.Left), Math.Max(_clip.Bottom, up.Bottom), Math.Min(_clip.Right, up.Right), Math.Min(_clip.Top, up.Top));
    }

    public override void PushTextClip(in PdfGraphicsState state) => _clips.Add(_clip);

    public override void PopClip()
    {
        if (_clips.Count > 0)
        {
            _clip = _clips[^1];
            _clips.RemoveAt(_clips.Count - 1);
        }
    }

    public override void ShowGlyphRun(in PdfGlyphRun run, in PdfGraphicsState state)
    {
        _runSources.Clear();
        _inner.RunSources = _runSources;
        _inner.ShowGlyphRun(in run, in state);
        ReadOnlySpan<PdfGlyph> glyphs = run.Glyphs.Span;
        bool taken = _runSources.Count == glyphs.Length;
        for (int i = 0; i < glyphs.Length; i++)
        {
            _marks.Add((taken ? _runSources[i] : -1, glyphs[i].ByteLength));
        }
    }

    public override void FillPath(in PdfPath path, bool evenOdd, in PdfGraphicsState state) => PaintPath(in path, in state, false);

    public override void StrokePath(in PdfPath path, in PdfGraphicsState state) => PaintPath(in path, in state, true);

    public override void FillStrokePath(in PdfPath path, bool evenOdd, in PdfGraphicsState state) => PaintPath(in path, in state, true);

    private void PaintPath(in PdfPath path, in PdfGraphicsState state, bool stroke)
    {
        if (_maskDepth > 0 || path.IsEmpty)
        {
            return;
        }

        PdfBounds b = state.Ctm.TransformBounds(path.ControlBounds());
        if (stroke && b.IsFinite)
        {
            PdfMatrix c = state.Ctm;
            double scale = Math.Max(Math.Sqrt((c.A * c.A) + (c.B * c.B)), Math.Sqrt((c.C * c.C) + (c.D * c.D)));
            double lw = double.IsFinite(state.LineWidth) ? Math.Max(0, state.LineWidth) : 0;
            b = b.Inflate(lw / 2 * scale);
        }

        Note(ToUp(b));
    }

    public override void DrawImage(in PdfImage image, in PdfGraphicsState state)
    {
        if (_maskDepth == 0)
        {
            Note(ToUp(state.Ctm.TransformBounds(new PdfBounds(0, 0, 1, 1))));
        }
    }

    public override void PaintShading(PdfShading shading, in PdfGraphicsState state)
    {
        if (_maskDepth > 0)
        {
            return;
        }

        PdfBounds b = _clip;
        if (shading.BBox is { } bb)
        {
            PdfBounds sb = ToUp(state.Ctm.TransformBounds(new PdfBounds(bb.Left, bb.Bottom, bb.Right, bb.Top)));
            b = new PdfBounds(Math.Max(b.Left, sb.Left), Math.Max(b.Bottom, sb.Bottom), Math.Min(b.Right, sb.Right), Math.Min(b.Top, sb.Top));
        }

        Note(b);
    }

    private PdfBounds ToUp(PdfBounds b) => new(b.Left, _height - b.Top, b.Right, _height - b.Bottom);

    // The graphics area a mark lies in. A mark in two areas is the plan's mistake and stops the tagging.
    private void Note(PdfBounds up)
    {
        _paintSeen = true;
        _paintSlot = -1;
        if (!up.IsFinite)
        {
            return;
        }

        const double Tol = 1.0;
        foreach (TagSlot g in _graphics)
        {
            PdfRectangle a = g.Content.Area;
            if (up.Left >= a.Left - Tol && up.Right <= a.Right + Tol && up.Bottom >= a.Bottom - Tol && up.Top <= a.Top + Tol)
            {
                if (_paintSlot >= 0 && _paintSlot != g.Id)
                {
                    throw new PdfTaggingException(PdfTaggingFailure.DuplicateContent, $"Page {_pageIndex + 1}: a painted mark lies inside two of the plan's graphics areas.", _pageIndex);
                }

                _paintSlot = g.Id;
            }
        }
    }

    // ---- the tap ----

    public void StreamBegin(ReadOnlyMemory<byte> data, PdfDictionary? resources)
    {
        bool isPage = _stack.Count == 0;
        var ctx = new TagCtx { Data = data, Resources = resources, IsPage = isPage, Passthrough = _maskDepth > 0 || (_stack.Count > 0 && _stack[^1].Passthrough) };
        if (isPage)
        {
            ctx.Scope = _pageScope;
            ctx.Res = _pageResources;
            ctx.PieceIndex = _pieces++;
            _pieceScopes.Add(_pageScope);
        }
        else
        {
            ctx.Scope = new McScope { PageIndex = _pageIndex };
            ctx.Res = new ResourceEdit(_doc, resources);
        }

        _stack.Add(ctx);
        _child = null;
    }

    public void StreamEnd()
    {
        TagCtx ctx = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        if (!ctx.Passthrough)
        {
            Finish(ctx);
        }

        if (ctx.IsPage)
        {
            _pageContexts.Add(ctx);
            _child = null;
        }
        else
        {
            _child = ctx.Passthrough ? null : ctx;
        }
    }

    public void Operator(ContentOp op, int start, int end)
    {
        TagCtx ctx = _stack[^1];
        try
        {
            if (!ctx.Passthrough)
            {
                Handle(ctx, op, start, end);
            }
        }
        finally
        {
            _marks.Clear();
            _paintSeen = false;
            _paintSlot = -1;
            _child = null;
        }
    }

    private static bool IsPaint(ContentOp op) => op is ContentOp.FillNz or ContentOp.FillNzOld or ContentOp.FillEo or ContentOp.Stroke or ContentOp.CloseStroke
        or ContentOp.FillStroke or ContentOp.FillStrokeEo or ContentOp.CloseFillStroke or ContentOp.CloseFillStrokeEo;

    private static bool IsConstruct(ContentOp op) => op is ContentOp.Move or ContentOp.Line or ContentOp.Curve or ContentOp.CurveV or ContentOp.CurveY or ContentOp.Close or ContentOp.Rect;

    private void Handle(TagCtx ctx, ContentOp op, int start, int end)
    {
        _operators++;
        if (IsConstruct(op))
        {
            if (ctx.PathStart < 0)
            {
                ctx.PathStart = start;
            }

            return;
        }

        if (IsPaint(op))
        {
            int from = ctx.PathStart >= 0 ? ctx.PathStart : start;
            ctx.PathStart = -1;
            AddUnit(ctx, TagCtx.UnitKind.Path, from, end, _paintSeen ? _paintSlot : -1);
            return;
        }

        switch (op)
        {
            case ContentOp.EndPath:
                ctx.PathStart = -1;
                return;
            case ContentOp.Shading:
                AddUnit(ctx, TagCtx.UnitKind.Shading, start, end, _paintSeen ? _paintSlot : -1);
                return;
            case ContentOp.BeginInline:
                AddUnit(ctx, TagCtx.UnitKind.Inline, start, end, _paintSeen ? _paintSlot : -1);
                return;
            case ContentOp.Do:
                HandleDo(ctx, start, end);
                return;
            case ContentOp.ShowText:
            case ContentOp.ShowTextArray:
            case ContentOp.NextLineShow:
            case ContentOp.SpacedNextLineShow:
                HandleText(ctx, op, start, end);
                return;
            case ContentOp.Save:
            case ContentOp.Restore:
            case ContentOp.BeginText:
            case ContentOp.EndText:
                ctx.PathStart = -1;
                ctx.Entries.Add(new TagCtx.Entry(TagCtx.EntryKind.Boundary, 0));
                return;
            case ContentOp.BeginMarked:
            case ContentOp.BeginMarkedProps:
                BeginMarked(ctx, op, start, end);
                return;
            case ContentOp.EndMarked:
                EndMarked(ctx, start, end);
                return;
        }
    }

    private static TagCtx.Unit AddUnit(TagCtx ctx, TagCtx.UnitKind kind, int start, int end, int owner)
    {
        var u = new TagCtx.Unit { Kind = kind, Start = start, End = end, Owner = owner, Frame = InnermostFrame(ctx) };
        ctx.Entries.Add(new TagCtx.Entry(TagCtx.EntryKind.Unit, ctx.Units.Count));
        ctx.Units.Add(u);
        return u;
    }

    private static int InnermostFrame(TagCtx ctx)
    {
        for (int i = ctx.McStack.Count - 1; i >= 0; i--)
        {
            if (ctx.McStack[i] >= 0)
            {
                return ctx.McStack[i];
            }
        }

        return -1;
    }

    // ---- existing marked content ----

    private void BeginMarked(TagCtx ctx, ContentOp op, int start, int end)
    {
        ctx.PathStart = -1;
        string? tag = ReadMarked(ctx, op, start, end, out PdfDictionary? props);
        if (props is not null && props.ContainsKey("MCID"))
        {
            // Marked content with an identifier belongs to a structure tree that is not there: the file is half tagged, and
            // identifiers of ours would clash with those.
            throw new PdfTaggingException(PdfTaggingFailure.AlreadyTagged, $"Page {_pageIndex + 1} has marked content with an /MCID, so the file has been tagged before.", _pageIndex);
        }

        if (tag == "Artifact")
        {
            var f = new TagCtx.Frame { OpenStart = start, OpenEnd = end, Parent = InnermostFrame(ctx) };
            f.Open = [.. ctx.Data.Span[start..end]];
            ctx.Frames.Add(f);
            ctx.McStack.Add(ctx.Frames.Count - 1);
            ctx.Entries.Add(new TagCtx.Entry(TagCtx.EntryKind.FrameOpen, ctx.Frames.Count - 1));
        }
        else
        {
            ctx.McStack.Add(-1);
            ctx.Entries.Add(new TagCtx.Entry(TagCtx.EntryKind.Boundary, 0));
        }
    }

    private static void EndMarked(TagCtx ctx, int start, int end)
    {
        ctx.PathStart = -1;
        if (ctx.McStack.Count > 0)
        {
            int f = ctx.McStack[^1];
            ctx.McStack.RemoveAt(ctx.McStack.Count - 1);
            if (f >= 0)
            {
                ctx.Frames[f].CloseStart = start;
                ctx.Frames[f].CloseEnd = end;
                ctx.Entries.Add(new TagCtx.Entry(TagCtx.EntryKind.FrameClose, f));
                return;
            }
        }

        ctx.Entries.Add(new TagCtx.Entry(TagCtx.EntryKind.Boundary, 0));
    }

    private string? ReadMarked(TagCtx ctx, ContentOp op, int start, int end, out PdfDictionary? props)
    {
        props = null;
        var lx = new ContentLexer(ctx.Data.Slice(start, end - start), _doc.Limits.MaxNestingDepth);
        var ops = new List<Operand>();
        while (lx.Next(out Operand o, out _) == ContentToken.Operand)
        {
            ops.Add(o);
        }

        if (ops.Count == 0)
        {
            return null;
        }

        if (op == ContentOp.BeginMarked)
        {
            return ops[^1].Name;
        }

        if (ops.Count < 2)
        {
            return null;
        }

        Operand p = ops[^1];
        if (p.Kind == OperandKind.Name)
        {
            props = ctx.Resources?.GetDictionary("Properties")?.GetDictionary(p.Name!);
        }
        else if (p.Kind == OperandKind.Object)
        {
            props = lx.ToObject(in p) as PdfDictionary;
        }

        return ops[^2].Name;
    }

    // ---- Do ----

    private void HandleDo(TagCtx ctx, int start, int end)
    {
        ctx.PathStart = -1;
        TagCtx? child = _child;
        if (child is null)
        {
            AddUnit(ctx, TagCtx.UnitKind.Do, start, end, _paintSeen ? _paintSlot : -1);
            return;
        }

        if (!child.Mixed)
        {
            TagCtx.Unit single = AddUnit(ctx, TagCtx.UnitKind.Do, start, end, child.SawOwner ? child.SingleOwner : -1);
            _ = single;
            return;
        }

        // The form holds content of several owners: it is copied, with the marked content inside the copy, and the Do names the copy.
        string? name = ReadFirstName(ctx, start, end);
        PdfStream? original = name is null ? null : ctx.Resources?.GetDictionary("XObject")?.Get(name) as PdfStream;
        if (original is null || name is null)
        {
            CannotVerify("a form XObject could not be found again after it was read");
            return;
        }

        var copy = new FormCopy
        {
            Original = original,
            Data = child.Data,
            Edits = child.Edits,
            Scope = child.Scope,
            Resources = child.Res,
            ParentResources = ctx.Res,
            PageIndex = _pageIndex,
        };
        child.Scope.Copy = copy;
        child.CopyOwner = copy;
        copy.NameInParent = ctx.Res.Add("XObject", "TgF", new PdfInteger(0));
        copy.ParentCtx = ctx;
        copy.DoPosition = start;
        _copies.Add(copy);
        TagCtx.Unit u = AddUnit(ctx, TagCtx.UnitKind.Do, start, end, MixedDo);
        u.Copy = copy;
        u.CopyBytes = TagText.Ascii("\n/" + copy.NameInParent + " Do\n");
    }

    private string? ReadFirstName(TagCtx ctx, int start, int end)
    {
        var lx = new ContentLexer(ctx.Data.Slice(start, end - start), _doc.Limits.MaxNestingDepth);
        string? first = null;
        while (lx.Next(out Operand o, out _) == ContentToken.Operand)
        {
            if (o.Kind == OperandKind.Name)
            {
                first = o.Name;
            }
        }

        return first;
    }

    // ---- text ----

    private void HandleText(TagCtx ctx, ContentOp op, int start, int end)
    {
        ctx.PathStart = -1;
        int n = _marks.Count;
        if (n == 0)
        {
            AddUnit(ctx, TagCtx.UnitKind.Text, start, end, -1);
            return;
        }

        int[] owners = new int[n];
        int last = int.MinValue;
        for (int i = 0; i < n; i++)
        {
            int s = _marks[i].Source;
            if (s < 0)
            {
                owners[i] = int.MinValue;
                continue;
            }

            owners[i] = s < _owners.Length ? _owners[s] : -1;
            last = owners[i];
        }

        // A glyph the extraction does not take (nothing to see, no size) goes with the glyph before it, or the one after.
        int carry = int.MinValue;
        for (int i = 0; i < n; i++)
        {
            if (owners[i] == int.MinValue)
            {
                owners[i] = carry;
            }
            else
            {
                carry = owners[i];
            }
        }

        carry = last == int.MinValue ? -1 : last;
        for (int i = n - 1; i >= 0; i--)
        {
            if (owners[i] == int.MinValue)
            {
                owners[i] = carry;
            }
            else
            {
                carry = owners[i];
            }
        }

        bool same = true;
        for (int i = 1; i < n && same; i++)
        {
            same = owners[i] == owners[0];
        }

        TagCtx.Unit u = AddUnit(ctx, TagCtx.UnitKind.Text, start, end, owners[0]);
        if (!same)
        {
            u.Pieces = SplitText(ctx, op, start, end, owners);
        }
    }

    private sealed class Tok
    {
        public bool IsNum;
        public double Num;
        public List<byte>? Str;
    }

    // Cuts one text operator into runs of glyphs with the same owner. Each run is a TJ of its own; the numbers of the original TJ stay
    // where they were, so the glyphs sit exactly where they did (the text position carries on from one TJ to the next).
    private List<TagCtx.TextPiece> SplitText(TagCtx ctx, ContentOp op, int start, int end, int[] owners)
    {
        var lx = new ContentLexer(ctx.Data.Slice(start, end - start), _doc.Limits.MaxNestingDepth);
        var operands = new List<Operand>();
        var items = new List<Operand>();
        int arrayAt = -1;
        while (lx.Next(out Operand o, out _) == ContentToken.Operand)
        {
            if (o.Kind == OperandKind.Array)
            {
                arrayAt = operands.Count;
                items.Clear();
                foreach (Operand it in lx.Items(in o))
                {
                    items.Add(it);
                }
            }

            operands.Add(o);
        }

        List<Operand> elements;
        string prefix = string.Empty;
        switch (op)
        {
            case ContentOp.ShowTextArray:
                if (arrayAt < 0)
                {
                    throw new PdfTaggingException(PdfTaggingFailure.ContentUnreadable, $"Page {_pageIndex + 1}: a TJ operator has no array.", _pageIndex);
                }

                elements = items;
                break;
            case ContentOp.ShowText:
                elements = [operands[^1]];
                break;
            case ContentOp.NextLineShow:
                elements = [operands[^1]];
                prefix = "\nT* ";
                break;
            default:
                prefix = "\n" + ContentRedactor.FormatNum(operands[^3].Num) + " Tw " + ContentRedactor.FormatNum(operands[^2].Num) + " Tc T* ";
                elements = [operands[^1]];
                break;
        }

        var pieces = new List<TagCtx.TextPiece>();
        var toks = new List<Tok>();
        var pending = new List<double>();
        int curOwner = int.MinValue;
        bool first = true;
        int gi = 0;
        foreach (Operand it in elements)
        {
            if (it.Kind == OperandKind.Number)
            {
                if (double.IsFinite(it.Num))
                {
                    pending.Add(it.Num);
                }

                continue;
            }

            if (it.Kind != OperandKind.String)
            {
                continue;
            }

            ReadOnlySpan<byte> s = it.Str.Span;
            int pos = 0;
            while (pos < s.Length)
            {
                if (gi >= _marks.Count || _marks[gi].Length <= 0 || pos + _marks[gi].Length > s.Length)
                {
                    throw new PdfTaggingException(PdfTaggingFailure.ContentUnreadable, $"Page {_pageIndex + 1}: a text operator's glyphs do not match its string.", _pageIndex);
                }

                int len = _marks[gi].Length;
                int owner = owners[gi++];
                if (owner != curOwner)
                {
                    if (curOwner != int.MinValue)
                    {
                        EmitPiece(pieces, toks, curOwner, ref first, prefix);
                    }

                    curOwner = owner;
                }

                foreach (double d in pending)
                {
                    AddNum(toks, d);
                }

                pending.Clear();
                if (toks.Count > 0 && !toks[^1].IsNum)
                {
                    toks[^1].Str!.AddRange(s.Slice(pos, len).ToArray());
                }
                else
                {
                    toks.Add(new Tok { Str = [.. s.Slice(pos, len).ToArray()] });
                }

                pos += len;
            }
        }

        if (gi != _marks.Count)
        {
            throw new PdfTaggingException(PdfTaggingFailure.ContentUnreadable, $"Page {_pageIndex + 1}: a text operator's glyphs do not match its string.", _pageIndex);
        }

        foreach (double d in pending)
        {
            AddNum(toks, d);
        }

        EmitPiece(pieces, toks, curOwner, ref first, prefix);
        return pieces;
    }

    private static void AddNum(List<Tok> toks, double v)
    {
        if (toks.Count > 0 && toks[^1].IsNum)
        {
            toks[^1].Num += v;
        }
        else
        {
            toks.Add(new Tok { IsNum = true, Num = v });
        }
    }

    private void EmitPiece(List<TagCtx.TextPiece> pieces, List<Tok> toks, int owner, ref bool first, string prefix)
    {
        var o = new OutBuf(_limit);
        if (first)
        {
            o.Add(prefix);
            first = false;
        }

        o.Add("\n[");
        foreach (Tok t in toks)
        {
            if (t.IsNum)
            {
                o.Add(ContentRedactor.FormatNum(t.Num));
                o.Add((byte)' ');
            }
            else
            {
                TagText.WriteString(o, t.Str!);
            }
        }

        o.Add("] TJ\n");
        pieces.Add(new TagCtx.TextPiece(owner, o.ToArray()));
        toks.Clear();
    }

    // ---- the end of a stream: dissolve, wrap, summarize ----

    private void Finish(TagCtx ctx)
    {
        if (ctx.IsPage)
        {
            ctx.OperatorCount = _operators;
        }

        // An existing artifact sequence that encloses something that gets a role cannot stay (artifact content may not hold tagged
        // content), so it goes and its unowned content is marked again.
        foreach (TagCtx.Unit u in ctx.Units)
        {
            if (u.Owner != -1)
            {
                for (int f = u.Frame; f >= 0; f = ctx.Frames[f].Parent)
                {
                    ctx.Frames[f].Dissolve = true;
                }
            }
        }

        bool open = false;
        int curKey = 0;
        int curEnd = 0;

        void Close()
        {
            if (open)
            {
                ctx.AddEdit(curEnd, curEnd, TagText.Emc);
                ctx.Scope.AddedOperators++;
                open = false;
            }
        }

        byte[] Open(int key, int position)
        {
            ctx.Scope.AddedOperators++;
            if (key >= 0)
            {
                TagSlot slot = _slots[key];
                if (slot.IsArtifact)
                {
                    ctx.Scope.ArtifactSequences++;
                    return TagText.Artifact(slot.Kind);
                }

                int mcid = ctx.Scope.Allocate(key, position, ctx.PieceIndex);
                return TagText.Mcid(slot.Tag, mcid);
            }

            ctx.Scope.ArtifactSequences++;
            if (key <= -2)
            {
                byte[] kept = ctx.Frames[-2 - key].Open;
                byte[] withEnd = new byte[kept.Length + 1];
                kept.CopyTo(withEnd, 0);
                withEnd[^1] = (byte)'\n';
                return withEnd;
            }

            return TagText.Artifact(PdfArtifactKind.Plain);
        }

        void Note(int key)
        {
            int effective = key >= 0 ? key : -1;
            if (!ctx.SawOwner)
            {
                ctx.SawOwner = true;
                ctx.SingleOwner = effective;
            }
            else if (ctx.SingleOwner != effective)
            {
                ctx.Mixed = true;
            }
        }

        foreach (TagCtx.Entry e in ctx.Entries)
        {
            switch (e.Kind)
            {
                case TagCtx.EntryKind.Boundary:
                    Close();
                    break;
                case TagCtx.EntryKind.FrameOpen:
                    Close();
                    if (ctx.Frames[e.Index].Dissolve)
                    {
                        ctx.AddEdit(ctx.Frames[e.Index].OpenStart, ctx.Frames[e.Index].OpenEnd, []);
                    }

                    break;
                case TagCtx.EntryKind.FrameClose:
                    Close();
                    if (ctx.Frames[e.Index].Dissolve)
                    {
                        ctx.AddEdit(ctx.Frames[e.Index].CloseStart, ctx.Frames[e.Index].CloseEnd, []);
                    }

                    break;
                default:
                    {
                        TagCtx.Unit u = ctx.Units[e.Index];
                        if (RetainedFrame(ctx, u.Frame))
                        {
                            // Already inside an artifact sequence that stays: nothing to add.
                            Note(-1);
                            break;
                        }

                        if (u.Copy is not null)
                        {
                            Close();
                            ctx.Mixed = true;
                            ctx.SawOwner = true;
                            ctx.AddEdit(u.Start, u.End, u.CopyBytes!);
                            break;
                        }

                        int baseKey = u.Frame >= 0 ? -2 - u.Frame : -1;
                        if (u.Pieces is null)
                        {
                            int key = u.Owner >= 0 ? u.Owner : baseKey;
                            Note(key);
                            if (open && curKey == key)
                            {
                                curEnd = u.End;
                            }
                            else
                            {
                                Close();
                                ctx.AddEdit(u.Start, u.Start, Open(key, u.Start));
                                open = true;
                                curKey = key;
                                curEnd = u.End;
                            }

                            break;
                        }

                        var buf = new OutBuf(_limit);
                        for (int i = 0; i < u.Pieces.Count; i++)
                        {
                            TagCtx.TextPiece p = u.Pieces[i];
                            int key = p.Owner >= 0 ? p.Owner : baseKey;
                            Note(key);
                            if (i == 0)
                            {
                                if (!(open && curKey == key))
                                {
                                    Close();
                                    buf.Add(Open(key, u.Start));
                                    open = true;
                                    curKey = key;
                                }
                            }
                            else if (curKey != key)
                            {
                                buf.Add(TagText.Emc);
                                ctx.Scope.AddedOperators++;
                                buf.Add(Open(key, u.Start));
                                curKey = key;
                            }

                            buf.Add(p.Bytes);
                        }

                        curEnd = u.End;
                        ctx.AddEdit(u.Start, u.End, buf.ToArray());
                        break;
                    }
            }
        }

        Close();
        if (ctx.IsPage)
        {
            ctx.Output = TagText.Apply(ctx.Data, ctx.Edits, _limit);
        }
    }

    private static bool RetainedFrame(TagCtx ctx, int frame)
    {
        for (int f = frame; f >= 0; f = ctx.Frames[f].Parent)
        {
            if (!ctx.Frames[f].Dissolve)
            {
                return true;
            }
        }

        return false;
    }
}
