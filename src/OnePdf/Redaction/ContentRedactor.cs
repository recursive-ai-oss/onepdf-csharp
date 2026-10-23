using System.Linq;
using OnePdf.Forms;
using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OnePdf.Content;
using OnePdf.Writing;

namespace OnePdf.Redaction;

/// <summary>A growable output buffer that refuses to outgrow the decoded-stream limit.</summary>
internal sealed class OutBuf(long limit)
{
    private byte[] _b = new byte[256];
    private int _n;

    public int Length => _n;

    public ReadOnlySpan<byte> Span => _b.AsSpan(0, _n);

    private void Grow(int add)
    {
        long need = (long)_n + add;
        if (need > limit || need > Array.MaxLength)
        {
            throw new PdfLimitException("Redacted content exceeds the decoded stream size limit.");
        }

        if (need > _b.Length)
        {
            Array.Resize(ref _b, (int)Math.Min(Math.Max(need, (long)_b.Length * 2), Array.MaxLength));
        }
    }

    public void Add(ReadOnlySpan<byte> data)
    {
        Grow(data.Length);
        data.CopyTo(_b.AsSpan(_n));
        _n += data.Length;
    }

    public void Add(byte b)
    {
        Grow(1);
        _b[_n++] = b;
    }

    public void Add(string ascii)
    {
        Grow(ascii.Length);
        foreach (char c in ascii)
        {
            _b[_n++] = (byte)c;
        }
    }

    public void Replace(int start, int end, ReadOnlySpan<byte> with)
    {
        byte[] tail = _b.AsSpan(end, _n - end).ToArray();
        _n = start;
        Add(with);
        Add(tail);
    }

    public byte[] ToArray() => _b.AsSpan(0, _n).ToArray();
}

/// <summary>
/// Edits a resource dictionary without touching the original, which other pages or forms may share: the first change copies it
/// (and the category being changed), later changes go into the copy.
/// </summary>
internal sealed class ResourceEdit(PdfDocument doc, PdfDictionary? effective)
{
    private PdfDictionary? _owned;
    private readonly HashSet<string> _copiedCategories = [];
    private int _counter;
    private readonly Dictionary<(string, string), int> _uses = [];
    private readonly HashSet<(string, string)> _touched = [];

    public PdfDictionary? Effective => effective;

    /// <summary>Counts a reference to <paramref name="name"/> that stays in the rewritten content.</summary>
    public void Use(string category, string name) => _uses[(category, name)] = _uses.GetValueOrDefault((category, name)) + 1;

    /// <summary>Drops a counted reference.</summary>
    public void Unuse(string category, string name) => _uses[(category, name)] = _uses.GetValueOrDefault((category, name)) - 1;

    /// <summary>Notes that a use of <paramref name="name"/> was replaced or dropped, so the entry goes if nothing else uses it.</summary>
    public void Touch(string category, string name) => _touched.Add((category, name));

    /// <summary>Removes the entries whose every use was replaced, so the unredacted originals do not stay reachable through the resources.</summary>
    public void Prune()
    {
        foreach ((string cat, string name) in _touched)
        {
            if (_uses.GetValueOrDefault((cat, name)) > 0)
            {
                continue;
            }

            PdfDictionary owned = GetOwned();
            if (owned.GetDictionary(cat) is null)
            {
                continue;
            }

            EnsureCategory(owned, cat).RemoveEntry(name);
        }
    }

    public PdfDictionary? Owned => _owned;

    public bool Edited => _owned is not null;

    public PdfDictionary GetOwned()
    {
        if (_owned is null)
        {
            var copy = new PdfDictionary();
            if (effective is not null)
            {
                foreach (string k in (string[])[.. effective.Keys])
                {
                    if (effective.GetRaw(k) is { } raw)
                    {
                        copy.SetEntry(k, FormFiller.Clone(doc, raw));
                    }
                }
            }

            _owned = copy;
        }

        return _owned;
    }

    private PdfDictionary EnsureCategory(PdfDictionary owned, string category)
    {
        if (_copiedCategories.Contains(category) && owned.GetDictionary(category) is { } have)
        {
            return have;
        }

        var copy = new PdfDictionary();
        if (owned.GetDictionary(category) is { } old)
        {
            foreach (string k in (string[])[.. old.Keys])
            {
                if (old.GetRaw(k) is { } raw)
                {
                    copy.SetEntry(k, FormFiller.Clone(doc, raw));
                }
            }
        }

        owned.SetEntry(category, copy);
        _copiedCategories.Add(category);
        return owned.GetDictionary(category)!;
    }

    /// <summary>Adds an entry under a new, unused name and returns the name.</summary>
    public string Add(string category, string prefix, PdfObject value)
    {
        PdfDictionary target = EnsureCategory(GetOwned(), category);
        string name;
        do
        {
            name = prefix + (++_counter).ToString(CultureInfo.InvariantCulture);
        }
        while (target.ContainsKey(name));
        target.SetEntry(name, value);
        return name;
    }
}

/// <summary>
/// Rewrites the content streams of one page so that nothing that is drawn inside the redacted areas remains. It is both the
/// device the interpreter draws into (to learn what each operator painted and where) and the tap that tells it where each
/// operator's bytes are, so the rewrite is made from the interpreter's own reading of the content: the same fonts, widths,
/// matrices and clips decide what is under an area as decide what is drawn.
/// </summary>
internal sealed class ContentRedactor : PdfDevice, IContentTap
{
    private static readonly string[] StrippedProperties = ["ActualText", "Alt", "E"];

    private readonly PdfDocument _doc;
    private readonly PdfPage _page;
    private readonly PdfRedactionOptions _options;
    private readonly IReadOnlyList<PdfRectangle> _regions;
    private readonly PdfImageDecoderRegistry _decoders;
    private readonly CancellationToken _ct;
    private readonly long _outLimit;
    private readonly List<PdfBounds> _rects = [];
    private readonly Dictionary<string, PdfReference> _variants = [];
    private readonly Dictionary<(int, double, double, double, double, double, double), (ImageRedactor.Outcome, PdfReference?)> _imageCache = [];
    private readonly List<Ctx> _stack = [];
    private readonly List<Ctx> _pageContexts = [];
    private readonly List<McEntry> _mc = [];
    private readonly List<PdfBounds> _clips = [];
    private PdfBounds _clip = PdfBounds.Unbounded;

    // What the operator being executed has painted so far.
    private readonly List<GlyphMark> _marks = [];
    private bool _anyGlyphHit;
    private bool _paintHit;
    private PdfMatrix _paintCtm;
    private bool _imageHit;
    private PdfMatrix _imageCtm;
    private bool _shadingHit;
    private int _skip;
    private bool _inAnnotation;
    private Ctx? _child;
    private PdfDictionary? _currentAnnotation;
    private string? _failReason;
    private int _variantCount;

    public ContentRedactor(PdfDocument doc, PdfPage page, PdfRedactionOptions options, IReadOnlyList<PdfRectangle> regions, CancellationToken ct)
    {
        _doc = doc;
        _page = page;
        _options = options;
        _regions = regions;
        _ct = ct;
        _decoders = options.Decoders ?? PdfImageDecoderRegistry.Default;
        _outLimit = doc.Limits.MaxDecodedStreamBytes;
        PageResources = new ResourceEdit(doc, page.Resources);
    }

    // ---- results ----

    public ResourceEdit PageResources { get; }

    public int GlyphsRemoved { get; private set; }

    public int PathsRemoved { get; private set; }

    public int ImagesBlanked { get; private set; }

    public int ImagesRemoved { get; private set; }

    public int AlternateTextsRemoved { get; private set; }

    public HashSet<int> RedactedMcids { get; } = [];

    public Dictionary<PdfDictionary, PdfReference> AnnotationAppearances { get; } = new(ReferenceEqualityComparer.Instance);

    public string? FailReason => _failReason;

    /// <summary>The new page content streams (one per content piece the interpreter ran), or null when nothing changed.</summary>
    public List<byte[]>? PageStreams
    {
        get
        {
            // A page that was read is always written back from what was read, so nothing the reader could not decode survives either.
            if (_pageContexts.Count == 0)
            {
                return null;
            }

            return [.. _pageContexts.Select(static c => c.Out.ToArray())];
        }
    }

    public bool Changed => _pageContexts.Exists(static c => c.Changed) || PageResources.Edited || AnnotationAppearances.Count > 0;

    public void CannotVerify(string reason) => _failReason ??= reason;

    // ---- the contexts (one per content stream the interpreter runs) ----

    private sealed class McEntry
    {
        public Ctx Owner = null!;
        public int Mcid = -1;
        public bool Hit;
        public Patch? Patch;
    }

    private sealed class Patch
    {
        public int InStart;
        public int InEnd;
        public int OutStart;
        public int OutEnd;
        public McEntry Entry = null!;
    }

    private sealed class Ctx
    {
        public ReadOnlyMemory<byte> Data;
        public bool IsPage;
        public OutBuf Out = null!;
        public ResourceEdit Res = null!;
        public int Copied;
        public bool Changed;
        public bool Pending;
        public bool PathClip;
        public readonly List<(int Start, int End)> PathOps = [];
        public readonly List<Patch> Patches = [];
        public int McBase;
    }

    private struct GlyphMark
    {
        public int Len;
        public bool Hit;
        public double Adjust;
    }

    public void StreamBegin(ReadOnlyMemory<byte> data, PdfDictionary? resources)
    {
        bool isPage = _stack.Count == 0 && !_inAnnotation;
        var ctx = new Ctx
        {
            Data = data,
            IsPage = isPage,
            Out = new OutBuf(_outLimit),
            Res = isPage ? PageResources : new ResourceEdit(_doc, resources),
            McBase = _mc.Count,
        };
        _stack.Add(ctx);
        _child = null;
    }

    public void StreamEnd()
    {
        Ctx ctx = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        FlushPath(ctx);
        // Bytes after the last executed operator were never run: a string or array cut off by the end of the data, or operands
        // without their operator. They may hold text that was never checked against the areas, so unless they are only white space
        // and comments they are dropped (ISO 32000-2 7.8.2: an operator is only meaningful with its operator keyword).
        if (IsBlank(ctx.Data.Span[ctx.Copied..]))
        {
            EmitThrough(ctx, ctx.Copied, ctx.Data.Length);
        }
        else
        {
            ctx.Changed = true;
        }

        while (_mc.Count > ctx.McBase)
        {
            _mc.RemoveAt(_mc.Count - 1);
        }

        for (int i = ctx.Patches.Count - 1; i >= 0; i--)
        {
            Patch p = ctx.Patches[i];
            if (p.Entry.Hit)
            {
                StripProperties(ctx, p);
            }
        }

        if (ctx.IsPage)
        {
            _pageContexts.Add(ctx);
            _child = null;
        }
        else
        {
            _child = ctx;
        }
    }

    private static bool IsBlank(ReadOnlySpan<byte> s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            byte b = s[i];
            if (b == (byte)'%')
            {
                while (i + 1 < s.Length && s[i + 1] is not ((byte)'\n' or (byte)'\r'))
                {
                    i++;
                }
            }
            else if (b is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)'\f' or 0))
            {
                return false;
            }
        }

        return true;
    }

    private static void EmitThrough(Ctx ctx, int from, int to, bool raw = false)
    {
        if (to > from)
        {
            CopyClean(ctx, from, to, raw);
        }

        ctx.Copied = Math.Max(ctx.Copied, to);
    }

    // Copies input bytes to the output without the comments in them: a comment can hold text that was on the page. Strings and hex
    // strings are skipped over so a percent sign inside one is kept. Inline image data is copied as it is (it is binary).
    private static void CopyClean(Ctx ctx, int from, int to, bool raw)
    {
        ReadOnlySpan<byte> s = ctx.Data.Span[from..to];
        if (raw || s.IndexOf((byte)'%') < 0)
        {
            ctx.Out.Add(s);
            return;
        }

        int depth = 0;
        bool hex = false;
        for (int i = 0; i < s.Length; i++)
        {
            byte b = s[i];
            if (depth > 0)
            {
                if (b == (byte)'\\' && i + 1 < s.Length)
                {
                    ctx.Out.Add(b);
                    ctx.Out.Add(s[++i]);
                    continue;
                }

                depth += b == (byte)'(' ? 1 : b == (byte)')' ? -1 : 0;
            }
            else if (hex)
            {
                hex = b != (byte)'>';
            }
            else if (b == (byte)'(')
            {
                depth = 1;
            }
            else if (b == (byte)'<' && !(i + 1 < s.Length && s[i + 1] == (byte)'<'))
            {
                hex = true;
            }
            else if (b == (byte)'<')
            {
                ctx.Out.Add(b);
                ctx.Out.Add(s[++i]);
                continue;
            }
            else if (b == (byte)'%')
            {
                while (i + 1 < s.Length && s[i + 1] is not ((byte)'\n' or (byte)'\r'))
                {
                    i++;
                }

                ctx.Out.Add((byte)' ');
                continue;
            }

            ctx.Out.Add(b);
        }
    }

    private static void FlushPath(Ctx ctx)
    {
        if (!ctx.Pending)
        {
            return;
        }

        foreach ((int s, int e) in ctx.PathOps)
        {
            CopyClean(ctx, s, e, raw: false);
            ctx.Out.Add((byte)'\n');
        }

        ctx.PathOps.Clear();
        ctx.Pending = false;
        ctx.PathClip = false;
    }

    private static bool IsConstruct(ContentOp op) => op is ContentOp.Move or ContentOp.Line or ContentOp.Curve or ContentOp.CurveV or ContentOp.CurveY or ContentOp.Close or ContentOp.Rect;

    private static bool IsPaint(ContentOp op) => op is ContentOp.FillNz or ContentOp.FillNzOld or ContentOp.FillEo or ContentOp.Stroke or ContentOp.CloseStroke
        or ContentOp.FillStroke or ContentOp.FillStrokeEo or ContentOp.CloseFillStroke or ContentOp.CloseFillStrokeEo or ContentOp.EndPath;

    // Operators that only set how the pending path will be painted; they can run ahead of it without changing the result.
    private static bool IsPaintState(ContentOp op) => op is ContentOp.FillGray or ContentOp.StrokeGray or ContentOp.FillRgb or ContentOp.StrokeRgb
        or ContentOp.FillCmyk or ContentOp.StrokeCmyk or ContentOp.LineWidth or ContentOp.LineCap or ContentOp.LineJoin or ContentOp.MiterLimit
        or ContentOp.Dash or ContentOp.Flatness or ContentOp.RenderingIntent;

    public void Operator(ContentOp op, int start, int end)
    {
        Ctx ctx = _stack[^1];
        try
        {
            Handle(ctx, op, start, end);
        }
        finally
        {
            _marks.Clear();
            _anyGlyphHit = false;
            _paintHit = false;
            _imageHit = false;
            _shadingHit = false;
            _child = null;
        }
    }

    private void Handle(Ctx ctx, ContentOp op, int start, int end)
    {
        if (start < ctx.Copied)
        {
            start = ctx.Copied;
        }

        if (start > ctx.Copied)
        {
            if (!ctx.Pending)
            {
                EmitThrough(ctx, ctx.Copied, start);
            }
            else
            {
                ctx.Copied = start;
            }
        }

        if (IsConstruct(op) || (ctx.Pending && op is ContentOp.ClipNz or ContentOp.ClipEo))
        {
            if (!ctx.Pending)
            {
                ctx.Pending = true;
                ctx.PathClip = false;
                ctx.PathOps.Clear();
            }

            if (op is ContentOp.ClipNz or ContentOp.ClipEo)
            {
                ctx.PathClip = true;
            }

            ctx.PathOps.Add((start, end));
            ctx.Copied = end;
            return;
        }

        if (IsPaint(op))
        {
            if (!_paintHit)
            {
                FlushPath(ctx);
                EmitThrough(ctx, start, end);
                return;
            }

            PathsRemoved++;
            ctx.Changed = true;
            MarkHit();
            bool clips = ctx.Pending && ctx.PathClip;
            if (_options.VectorMode == PdfRedactionVectorMode.ClipOut && ctx.Pending && !ctx.PathClip && op != ContentOp.EndPath)
            {
                EmitClippedOut(ctx, start, end);
                return;
            }

            if (ctx.Pending)
            {
                if (clips)
                {
                    FlushPath(ctx);
                    ctx.Out.Add("n\n");
                }
                else
                {
                    ctx.PathOps.Clear();
                    ctx.Pending = false;
                }
            }
            else
            {
                // The path's operators were already written (something else came between); the paint becomes a plain end of path.
                ctx.Out.Add("n\n");
            }

            ctx.Copied = end;
            return;
        }

        if (ctx.Pending && !IsPaintState(op))
        {
            FlushPath(ctx);
        }

        switch (op)
        {
            case ContentOp.ShowText:
            case ContentOp.ShowTextArray:
            case ContentOp.NextLineShow:
            case ContentOp.SpacedNextLineShow:
                if (_anyGlyphHit)
                {
                    RewriteText(ctx, op, start, end);
                    return;
                }

                break;
            case ContentOp.Do:
                if (HandleDo(ctx, start, end))
                {
                    return;
                }

                break;
            case ContentOp.Shading:
                if (_shadingHit)
                {
                    PathsRemoved++;
                    ctx.Changed = true;
                    MarkHit();
                    ctx.Copied = end;
                    return;
                }

                break;
            case ContentOp.BeginInline:
                if (_imageHit)
                {
                    ImagesRemoved++;
                    ctx.Changed = true;
                    MarkHit();
                    ctx.Copied = end;
                    return;
                }

                break;
            case ContentOp.BeginMarked:
            case ContentOp.BeginMarkedProps:
                {
                    var entry = new McEntry { Owner = ctx };
                    if (op == ContentOp.BeginMarkedProps)
                    {
                        PdfDictionary? props = ReadProperties(ctx, start, end, out _, out string? pn);
                        entry.Mcid = props is not null && props.Get("MCID") is PdfInteger mi && mi.Value is >= 0 and <= int.MaxValue ? (int)mi.Value : -1;
                        if (pn is not null)
                        {
                            ctx.Res.Use("Properties", pn);
                        }
                    }

                    int outStart = ctx.Out.Length;
                    EmitThrough(ctx, start, end);
                    var patch = new Patch { InStart = start, InEnd = end, OutStart = outStart, OutEnd = ctx.Out.Length, Entry = entry };
                    entry.Patch = patch;
                    ctx.Patches.Add(patch);
                    _mc.Add(entry);
                    return;
                }

            case ContentOp.EndMarked:
                if (_mc.Count > ctx.McBase)
                {
                    _mc.RemoveAt(_mc.Count - 1);
                }

                break;
        }

        EmitThrough(ctx, start, end, raw: op == ContentOp.BeginInline);
    }

    private void MarkHit()
    {
        foreach (McEntry e in _mc)
        {
            e.Hit = true;
            if (e.Mcid >= 0 && e.Owner.IsPage)
            {
                RedactedMcids.Add(e.Mcid);
            }
        }
    }

    // ---- device events ----

    public override void BeginPage(in PdfPageInfo page)
    {
        foreach (PdfRectangle r in _regions)
        {
            PdfBounds b = page.BaseMatrix.TransformBounds(new PdfBounds(r.Left, r.Bottom, r.Right, r.Top));
            _rects.Add(b);
        }
    }

    public override void BeginType3Glyph(in PdfType3GlyphInfo glyph, in PdfGraphicsState state) => _skip++;

    public override void EndType3Glyph() => _skip--;

    public override void BeginSoftMask(in PdfSoftMaskInfo mask, in PdfGraphicsState state) => _skip++;

    public override void EndSoftMask() => _skip--;

    public override void BeginAnnotation(in PdfAnnotationInfo annotation)
    {
        _inAnnotation = true;
        _currentAnnotation = annotation.Annotation;
    }

    public override void EndAnnotation()
    {
        _inAnnotation = false;
        if (_child is { } c && _currentAnnotation is { } a)
        {
            // The appearance stream was rewritten; the annotation will point at the new copy.
            if (c.Changed || c.Res.Edited)
            {
                AnnotationAppearances[a] = MakeFormVariant(null, c);
            }
        }

        _child = null;
        _currentAnnotation = null;
    }

    public override void PushClip(in PdfPath path, bool evenOdd, in PdfGraphicsState state)
    {
        _clips.Add(_clip);
        if (path.IsEmpty)
        {
            _clip = new PdfBounds(0, 0, 0, 0);
            return;
        }

        PdfBounds b = state.Ctm.TransformBounds(path.ControlBounds());
        if (b.IsFinite)
        {
            _clip = new PdfBounds(Math.Max(_clip.Left, b.Left), Math.Max(_clip.Bottom, b.Bottom), Math.Min(_clip.Right, b.Right), Math.Min(_clip.Top, b.Top));
        }
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

    private bool Meets(in PdfBounds b)
    {
        if (!b.IsFinite)
        {
            return true;
        }

        foreach (PdfBounds r in _rects)
        {
            if (b.Left < r.Right && b.Right > r.Left && b.Bottom < r.Top && b.Top > r.Bottom)
            {
                return true;
            }
        }

        return false;
    }

    public override void FillPath(in PdfPath path, bool evenOdd, in PdfGraphicsState state) => PaintEvent(in path, in state, stroke: false);

    public override void StrokePath(in PdfPath path, in PdfGraphicsState state) => PaintEvent(in path, in state, stroke: true);

    public override void FillStrokePath(in PdfPath path, bool evenOdd, in PdfGraphicsState state) => PaintEvent(in path, in state, stroke: true);

    private void PaintEvent(in PdfPath path, in PdfGraphicsState state, bool stroke)
    {
        if (_skip > 0 || path.IsEmpty)
        {
            return;
        }

        PdfBounds b = state.Ctm.TransformBounds(path.ControlBounds());
        if (stroke)
        {
            // Half the line width, widened for miter joins and projecting caps; a hairline still paints a device pixel.
            PdfMatrix c = state.Ctm;
            double scale = Math.Max(Math.Sqrt((c.A * c.A) + (c.B * c.B)), Math.Sqrt((c.C * c.C) + (c.D * c.D)));
            double lw = double.IsFinite(state.LineWidth) ? Math.Max(0, state.LineWidth) : 0;
            double miter = state.LineJoin == 0 && double.IsFinite(state.MiterLimit) ? Math.Max(1, state.MiterLimit) : 1.5;
            b = b.Inflate(Math.Max(1, (lw / 2) * miter * scale));
        }

        if (Meets(in b))
        {
            _paintHit = true;
            _paintCtm = state.Ctm;
        }
    }

    public override void PaintShading(PdfShading shading, in PdfGraphicsState state)
    {
        if (_skip > 0)
        {
            return;
        }

        PdfBounds b = _clip;
        if (shading.BBox is { } bb)
        {
            PdfBounds sb = state.Ctm.TransformBounds(new PdfBounds(bb.Left, bb.Bottom, bb.Right, bb.Top));
            b = new PdfBounds(Math.Max(b.Left, sb.Left), Math.Max(b.Bottom, sb.Bottom), Math.Min(b.Right, sb.Right), Math.Min(b.Top, sb.Top));
        }

        if (Meets(in b))
        {
            _shadingHit = true;
        }
    }

    public override void DrawImage(in PdfImage image, in PdfGraphicsState state)
    {
        if (_skip > 0)
        {
            return;
        }

        PdfBounds b = state.Ctm.TransformBounds(new PdfBounds(0, 0, 1, 1));
        if (Meets(in b))
        {
            _imageHit = true;
            _imageCtm = state.Ctm;
        }
    }

    public override void ShowGlyphRun(in PdfGlyphRun run, in PdfGraphicsState state)
    {
        if (_skip > 0)
        {
            return;
        }

        double fs = run.FontSize, th = run.HorizontalScale;
        var scale = new PdfMatrix(fs * th, 0, 0, fs, 0, run.Rise);
        ReadOnlySpan<PdfGlyph> glyphs = run.Glyphs.Span;
        Span<double> q = stackalloc double[8];
        for (int i = 0; i < glyphs.Length; i++)
        {
            ref readonly PdfGlyph g = ref glyphs[i];
            bool none = g.Code == 0 && g.GlyphId == -1 && g.Unicode is null && g.Width == 0;
            double ox = g.OffsetX, oy = g.OffsetY;
            if (run.IsVertical)
            {
                ox += g.VectorX * fs * th;
                oy += g.VectorY * fs;
            }

            PdfMatrix m = scale.Then(PdfMatrix.Translation(ox, oy)).Then(run.TextMatrix).Then(state.Ctm);
            bool hit = false;
            if (m.IsFinite)
            {
                double adv = run.IsVertical ? Math.Abs(g.VerticalAdvance) : g.Width;
                double x0, x1, y0, y1;
                if (run.IsVertical)
                {
                    x0 = -0.5;
                    x1 = 0.5;
                    y0 = -adv;
                    y1 = 0;
                }
                else
                {
                    // The box extraction uses for a glyph: the advance wide, from 0.2 em below the baseline to 0.9 em above.
                    x0 = 0;
                    x1 = adv;
                    y0 = -0.2;
                    y1 = 0.9;
                }

                if (!(x1 - x0 > 0))
                {
                    x0 = -0.25;
                    x1 = 0.25;
                }

                if (!g.Outline.IsEmpty)
                {
                    PdfBounds ob = g.Outline.ControlBounds();
                    if (ob.IsFinite)
                    {
                        x0 = Math.Min(x0, ob.Left);
                        x1 = Math.Max(x1, ob.Right);
                        y0 = Math.Min(y0, ob.Bottom);
                        y1 = Math.Max(y1, ob.Top);
                    }
                }

                if (none)
                {
                    x0 = x1 = 0;
                    y0 = y1 = 0;
                }

                (q[0], q[1]) = m.Transform(x0, y0);
                (q[2], q[3]) = m.Transform(x1, y0);
                (q[4], q[5]) = m.Transform(x1, y1);
                (q[6], q[7]) = m.Transform(x0, y1);
                hit = GlyphHit(q, none);
            }

            double adj = 0;
            if (!none && fs != 0 && double.IsFinite(fs))
            {
                bool word = g.ByteLength == 1 && g.Code == 32;
                double spacing = run.CharSpacing + (word ? run.WordSpacing : 0);
                double w = run.IsVertical ? g.VerticalAdvance : g.Width;
                if (!double.IsFinite(w))
                {
                    w = 0;
                }

                adj = -1000.0 * ((w * fs) + spacing) / fs;
                if (!double.IsFinite(adj))
                {
                    adj = 0;
                }
            }

            _marks.Add(new GlyphMark { Len = g.ByteLength, Hit = hit, Adjust = adj });
            if (hit)
            {
                _anyGlyphHit = true;
            }
        }
    }

    private bool GlyphHit(ReadOnlySpan<double> q, bool point)
    {
        const double Tol = 0.01;
        foreach (PdfBounds r in _rects)
        {
            if (point)
            {
                if (q[0] > r.Left && q[0] < r.Right && q[1] > r.Bottom && q[1] < r.Top)
                {
                    return true;
                }

                continue;
            }

            if (_options.FullyContainedOnly)
            {
                bool all = true;
                for (int k = 0; k < 4 && all; k++)
                {
                    all = q[2 * k] >= r.Left - Tol && q[2 * k] <= r.Right + Tol && q[(2 * k) + 1] >= r.Bottom - Tol && q[(2 * k) + 1] <= r.Top + Tol;
                }

                if (all)
                {
                    return true;
                }

                continue;
            }

            if (QuadMeetsRect(q, r, Tol))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Convex quadrilateral against an axis-aligned rectangle: they must overlap by more than <paramref name="tol"/> on every separating axis.</summary>
    internal static bool QuadMeetsRect(ReadOnlySpan<double> q, in PdfBounds r, double tol)
    {
        double minX = Math.Min(Math.Min(q[0], q[2]), Math.Min(q[4], q[6])), maxX = Math.Max(Math.Max(q[0], q[2]), Math.Max(q[4], q[6]));
        double minY = Math.Min(Math.Min(q[1], q[3]), Math.Min(q[5], q[7])), maxY = Math.Max(Math.Max(q[1], q[3]), Math.Max(q[5], q[7]));
        if (maxX <= r.Left + tol || minX >= r.Right - tol || maxY <= r.Bottom + tol || minY >= r.Top - tol)
        {
            return false;
        }

        for (int k = 0; k < 2; k++)
        {
            int n = k + 1;
            double ex = q[2 * n] - q[2 * k], ey = q[(2 * n) + 1] - q[(2 * k) + 1];
            double nx = -ey, ny = ex;
            double len = Math.Sqrt((nx * nx) + (ny * ny));
            if (len < 1e-12)
            {
                continue;
            }

            nx /= len;
            ny /= len;
            double qmin = double.PositiveInfinity, qmax = double.NegativeInfinity;
            for (int j = 0; j < 4; j++)
            {
                double v = (q[2 * j] * nx) + (q[(2 * j) + 1] * ny);
                qmin = Math.Min(qmin, v);
                qmax = Math.Max(qmax, v);
            }

            double cmin = double.PositiveInfinity, cmax = double.NegativeInfinity;
            for (int c = 0; c < 4; c++)
            {
                double v = (((c & 1) == 0 ? r.Left : r.Right) * nx) + (((c & 2) == 0 ? r.Bottom : r.Top) * ny);
                cmin = Math.Min(cmin, v);
                cmax = Math.Max(cmax, v);
            }

            if (qmax <= cmin + tol || cmax <= qmin + tol)
            {
                return false;
            }
        }

        return true;
    }

    // ---- text ----

    private sealed class Tok
    {
        public bool IsNum;
        public double Num;
        public List<byte>? Str;
    }

    private void RewriteText(Ctx ctx, ContentOp op, int start, int end)
    {
        ReadOnlyMemory<byte> slice = ctx.Data.Slice(start, end - start);
        var lx = new ContentLexer(slice, _doc.Limits.MaxNestingDepth);
        var operands = new List<Operand>();
        var items = new List<Operand>();
        int arrayAt = -1;
        while (true)
        {
            ContentToken t = lx.Next(out Operand o, out _);
            if (t != ContentToken.Operand)
            {
                break;
            }

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

        var elements = new List<Operand>();
        double aw = 0, ac = 0;
        switch (op)
        {
            case ContentOp.ShowTextArray:
                if (arrayAt < 0)
                {
                    throw new PdfFormatException("A TJ operator has no array.");
                }

                elements = items;
                break;
            case ContentOp.ShowText:
            case ContentOp.NextLineShow:
                elements.Add(operands[^1]);
                break;
            default:
                aw = operands[^3].Num;
                ac = operands[^2].Num;
                elements.Add(operands[^1]);
                break;
        }

        var toks = new List<Tok>();
        int gi = 0;
        int removed = 0;
        foreach (Operand it in elements)
        {
            if (it.Kind == OperandKind.Number)
            {
                AddNum(toks, it.Num);
            }
            else if (it.Kind == OperandKind.String)
            {
                ReadOnlySpan<byte> s = it.Str.Span;
                var cur = new List<byte>();
                int pos = 0;
                while (pos < s.Length)
                {
                    if (gi >= _marks.Count || pos + _marks[gi].Len > s.Length || _marks[gi].Len <= 0)
                    {
                        throw new PdfFormatException("A text operator's glyphs do not match its string.");
                    }

                    GlyphMark mk = _marks[gi++];
                    if (mk.Hit)
                    {
                        removed++;
                        AddStr(toks, cur);
                        cur = [];
                        AddNum(toks, mk.Adjust);
                    }
                    else
                    {
                        for (int k = 0; k < mk.Len; k++)
                        {
                            cur.Add(s[pos + k]);
                        }
                    }

                    pos += mk.Len;
                }

                AddStr(toks, cur);
            }
        }

        if (gi != _marks.Count)
        {
            throw new PdfFormatException("A text operator's glyphs do not match its string.");
        }

        GlyphsRemoved += removed;
        ctx.Changed = true;
        MarkHit();
        OutBuf o2 = ctx.Out;
        if (op == ContentOp.NextLineShow)
        {
            o2.Add("T* ");
        }
        else if (op == ContentOp.SpacedNextLineShow)
        {
            o2.Add(FormatNum(aw) + " Tw " + FormatNum(ac) + " Tc T* ");
        }

        o2.Add((byte)'[');
        foreach (Tok t in toks)
        {
            if (t.IsNum)
            {
                o2.Add(FormatNum(t.Num));
                o2.Add((byte)' ');
            }
            else
            {
                WriteString(o2, t.Str!);
            }
        }

        o2.Add("] TJ\n");
        ctx.Copied = end;
    }

    private static void AddNum(List<Tok> toks, double v)
    {
        if (!double.IsFinite(v))
        {
            return;
        }

        if (toks.Count > 0 && toks[^1].IsNum)
        {
            toks[^1].Num += v;
        }
        else
        {
            toks.Add(new Tok { IsNum = true, Num = v });
        }
    }

    private static void AddStr(List<Tok> toks, List<byte> s)
    {
        if (s.Count == 0)
        {
            return;
        }

        if (toks.Count > 0 && !toks[^1].IsNum)
        {
            toks[^1].Str!.AddRange(s);
        }
        else
        {
            toks.Add(new Tok { Str = [.. s] });
        }
    }

    internal static string FormatNum(double v)
    {
        if (!double.IsFinite(v) || Math.Abs(v) < 5e-7)
        {
            return "0";
        }

        string t = v.ToString("0.######", CultureInfo.InvariantCulture);
        return t == "-0" ? "0" : t;
    }

    private static void WriteString(OutBuf o, List<byte> s)
    {
        o.Add((byte)'(');
        foreach (byte b in s)
        {
            if (b is (byte)'(' or (byte)')' or (byte)'\\')
            {
                o.Add((byte)'\\');
                o.Add(b);
            }
            else if (b is < 32 or > 126)
            {
                o.Add((byte)'\\');
                o.Add((byte)('0' + ((b >> 6) & 7)));
                o.Add((byte)('0' + ((b >> 3) & 7)));
                o.Add((byte)('0' + (b & 7)));
            }
            else
            {
                o.Add(b);
            }
        }

        o.Add((byte)')');
    }

    // ---- XObjects ----

    private bool HandleDo(Ctx ctx, int start, int end)
    {
        string? name = ReadFirstName(ctx, start, end);
        if (name is null)
        {
            return false;
        }

        PdfStream? xo = ctx.Res.Effective?.GetDictionary("XObject")?.Get(name) as PdfStream;
        if (xo is null)
        {
            return false;
        }

        if (_imageHit && xo.Dictionary.GetName("Subtype") == "Image")
        {
            PdfMatrix c = _imageCtm;
            var key = (xo.Reference?.Number ?? -1, c.A, c.B, c.C, c.D, c.E, c.F);
            if (!_imageCache.TryGetValue(key, out (ImageRedactor.Outcome, PdfReference?) hit))
            {
                if (++_variantCount > 100_000)
                {
                    throw new PdfLimitException("Too many redacted copies of images or forms.");
                }

                ImageRedactor.Result r;
                try
                {
                    r = ImageRedactor.Redact(_doc, xo, ctx.Res.Effective, c, _rects, _decoders, _ct);
                }
                catch (PdfFormatException)
                {
                    r = new ImageRedactor.Result(ImageRedactor.Outcome.Removed, null, "its data is damaged", 0);
                }

                if (r.Outcome == ImageRedactor.Outcome.Replaced)
                {
                    ImagesBlanked++;
                }
                else if (r.Outcome == ImageRedactor.Outcome.Removed)
                {
                    ImagesRemoved++;
                }

                hit = (r.Outcome, r.Reference);
                _imageCache[key] = hit;
            }

            switch (hit.Item1)
            {
                case ImageRedactor.Outcome.Unchanged:
                    ctx.Res.Use("XObject", name);
                    return false;
                case ImageRedactor.Outcome.Replaced:
                    {
                        MarkHit();
                        ctx.Res.Touch("XObject", name);
                        string newName = ctx.Res.Add("XObject", "OPRi", hit.Item2!);
                        ctx.Out.Add("/" + newName + " Do\n");
                        ctx.Copied = end;
                        ctx.Changed = true;
                        return true;
                    }

                default:
                    MarkHit();
                    ctx.Res.Touch("XObject", name);
                    ctx.Copied = end;
                    ctx.Changed = true;
                    return true;
            }
        }

        if (_child is { } child && (child.Changed || child.Res.Edited) && xo.Dictionary.GetName("Subtype") == "Form")
        {
            PdfReference variant = MakeFormVariant(xo, child);
            ctx.Res.Touch("XObject", name);
            string newName = ctx.Res.Add("XObject", "OPRf", variant);
            ctx.Out.Add("/" + newName + " Do\n");
            ctx.Copied = end;
            ctx.Changed = true;
            return true;
        }

        ctx.Res.Use("XObject", name);
        return false;
    }

    private PdfReference MakeFormVariant(PdfStream? original, Ctx child)
    {
        byte[] bytes = child.Out.ToArray();
        string sig = child.Res.Owned is null ? string.Empty : "r";
        string key = (original?.Reference?.Number ?? -1).ToString(CultureInfo.InvariantCulture) + ":" + Convert.ToHexString(SHA256.HashData(bytes)) + sig;
        if (child.Res.Owned is null && _variants.TryGetValue(key, out PdfReference? known))
        {
            return known;
        }

        if (++_variantCount > 100_000)
        {
            throw new PdfLimitException("Too many redacted copies of images or forms.");
        }

        child.Res.Prune();
        PdfStream ns = _doc.CreateStream(bytes, compress: true);
        PdfDictionary nd = ns.Dictionary;
        if (original is not null)
        {
            foreach (string k in (string[])[.. original.Dictionary.Keys])
            {
                if (k is "Length" or "Filter" or "DecodeParms" or "F" or "FFilter" or "FDecodeParms" or "DL" or "Resources" or "Metadata" or "PieceInfo" or "OPI" or "Thumb")
                {
                    continue;
                }

                if (original.Dictionary.GetRaw(k) is { } raw)
                {
                    nd.SetEntry(k, FormFiller.Clone(_doc, raw));
                }
            }
        }
        else
        {
            nd.SetEntry("Type", new PdfName("XObject"));
            nd.SetEntry("Subtype", new PdfName("Form"));
            nd.SetEntry("BBox", new PdfArray());
        }

        if (child.Res.GetOwned() is { } owned)
        {
            nd.SetEntry("Resources", owned);
        }

        PdfReference reference = _doc.AddObject(ns);
        _variants[key] = reference;
        return reference;
    }

    // ---- ClipOut ----

    private void EmitClippedOut(Ctx ctx, int start, int end)
    {
        // q  <page-sized rectangle> <each area as a hole> W* n  <the path>  Q  (even-odd: the areas are outside the clip)
        PdfMatrix? inv = Invert(_paintCtm);
        if (inv is null)
        {
            ctx.PathOps.Clear();
            ctx.Pending = false;
            ctx.Copied = end;
            return;
        }

        var sb = new StringBuilder("q\n");
        sb.Append("-1000000 -1000000 m 1000000 -1000000 l 1000000 1000000 l -1000000 1000000 l h\n");
        foreach (PdfBounds r in _rects)
        {
            (double x0, double y0) = inv.Value.Transform(r.Left, r.Bottom);
            (double x1, double y1) = inv.Value.Transform(r.Right, r.Bottom);
            (double x2, double y2) = inv.Value.Transform(r.Right, r.Top);
            (double x3, double y3) = inv.Value.Transform(r.Left, r.Top);
            sb.Append(FormatNum(x0)).Append(' ').Append(FormatNum(y0)).Append(" m ")
              .Append(FormatNum(x1)).Append(' ').Append(FormatNum(y1)).Append(" l ")
              .Append(FormatNum(x2)).Append(' ').Append(FormatNum(y2)).Append(" l ")
              .Append(FormatNum(x3)).Append(' ').Append(FormatNum(y3)).Append(" l h\n");
        }

        sb.Append("W* n\n");
        ctx.Out.Add(sb.ToString());
        FlushPath(ctx);
        EmitThrough(ctx, start, end);
        ctx.Out.Add("\nQ\n");
    }

    private static PdfMatrix? Invert(PdfMatrix m)
    {
        double det = m.Determinant;
        if (!(Math.Abs(det) > 1e-12) || !double.IsFinite(det))
        {
            return null;
        }

        var r = new PdfMatrix(m.D / det, -m.B / det, -m.C / det, m.A / det, ((m.C * m.F) - (m.D * m.E)) / det, ((m.B * m.E) - (m.A * m.F)) / det);
        return r.IsFinite ? r : null;
    }

    // ---- marked content ----

    private string? ReadFirstName(Ctx ctx, int start, int end)
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

    private PdfDictionary? ReadProperties(Ctx ctx, int start, int end, out string? tag, out string? propName)
    {
        tag = null;
        propName = null;
        var lx = new ContentLexer(ctx.Data.Slice(start, end - start), _doc.Limits.MaxNestingDepth);
        var ops = new List<Operand>();
        while (lx.Next(out Operand o, out _) == ContentToken.Operand)
        {
            ops.Add(o);
        }

        if (ops.Count < 2)
        {
            return null;
        }

        tag = ops[^2].Name;
        Operand p = ops[^1];
        if (p.Kind == OperandKind.Name)
        {
            propName = p.Name;
            return ctx.Res.Effective?.GetDictionary("Properties")?.GetDictionary(p.Name!);
        }

        return p.Kind == OperandKind.Object ? lx.ToObject(in p) as PdfDictionary : null;
    }

    private void StripProperties(Ctx ctx, Patch patch)
    {
        PdfDictionary? props = ReadProperties(ctx, patch.InStart, patch.InEnd, out string? tag, out string? propName);
        if (props is null || tag is null)
        {
            return;
        }

        bool has = false;
        foreach (string k in StrippedProperties)
        {
            has |= props.ContainsKey(k);
        }

        if (!has)
        {
            return;
        }

        AlternateTextsRemoved++;
        ctx.Changed = true;
        var clean = new PdfDictionary();
        foreach (string k in (string[])[.. props.Keys])
        {
            if (Array.IndexOf(StrippedProperties, k) < 0 && props.GetRaw(k) is { } raw)
            {
                clean.SetEntry(k, FormFiller.Clone(_doc, raw));
            }
        }

        var o = new OutBuf(_outLimit);
        o.Add("/" + tag + " ");
        if (propName is not null)
        {
            ctx.Res.Unuse("Properties", propName);
            ctx.Res.Touch("Properties", propName);
            string newName = ctx.Res.Add("Properties", "OPRp", clean);
            o.Add("/" + newName + " BDC");
        }
        else
        {
            using var ms = new MemoryStream();
            var sink = new ByteSink(ms);
            new ObjectWriter(_doc, IdentityMapper.Instance, new PdfSaveOptions()).WriteValue(sink, clean, null, 0, 0);
            sink.Flush();
            o.Add(ms.ToArray());
            o.Add(" BDC");
        }

        ctx.Out.Replace(patch.OutStart, patch.OutEnd, o.Span);
    }
}
