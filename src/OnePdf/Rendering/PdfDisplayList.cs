using System.Globalization;

namespace OnePdf;

/// <summary>
/// A recording of everything an interpretation told its device, compact and replayable. Each paint item carries a
/// bounding box in output space; <see cref="Replay"/> uses them to skip what a clip rectangle cannot show.
/// </summary>
/// <remarks>
/// Record by passing the list as the device of <c>Interpret</c> (one thread, one interpretation at a time). Once recording
/// is finished the list is immutable in practice: any number of threads may call <see cref="Replay"/> and the read-only
/// members concurrently. Calling a device method on the list while others replay it is not supported.
/// Text form: see <c>docs/display-list-format.md</c> and <see cref="ToText"/>.
/// </remarks>
public sealed class PdfDisplayList : IPdfDevice
{
    private enum Kind : byte
    {
        BeginPage,
        EndPage,
        Fill,
        Stroke,
        FillStroke,
        Clip,
        TextClip,
        PopClip,
        Glyphs,
        Image,
        Shading,
        BeginGroup,
        EndGroup,
        BeginMask,
        EndMask,
        BeginMc,
        EndMc,
        BeginAnnot,
        EndAnnot,
        BeginType3,
        EndType3,
    }

    private struct Item
    {
        public Kind Kind;
        public bool EvenOdd;
        public int State;
        public int A;
        public int B;
        public int C;
        public int D;
        public int VerbChunk;
        public int PointChunk;
        public int Match;
        public double X0;
        public double Y0;
        public double X1;
        public double Y1;
    }

    private struct RunRecord
    {
        public string FontName;
        public PdfDictionary? Font;
        public double FontSize;
        public PdfTextRenderMode Mode;
        public double Tc;
        public double Tw;
        public double Th;
        public double Rise;
        public PdfMatrix Tm;
        public bool Vertical;
        public int CodeChunk;
        public int CodeStart;
        public int CodeLength;
        public int GlyphChunk;
        public int GlyphStart;
        public int GlyphCount;
        public double AdvX;
        public double AdvY;
    }

    // Storage grows in chunks (see ChunkedStorage.cs): a page with hundreds of thousands of items is recorded without
    // copying earlier data or allocating large arrays that are thrown away on the next doubling.
    private readonly ChunkedList<Item> _items = new(10, 256);
    private readonly ChunkArena<byte> _verbs = new(1 << 16, 1024);
    private readonly ChunkArena<double> _points = new(1 << 13, 2048);
    private readonly ChunkArena<byte> _codes = new(1 << 16, 1024);
    private readonly ChunkArena<PdfGlyph> _glyphs = new(1 << 9, 64);
    private readonly ChunkedList<PdfGraphicsState> _states = new(7, 16);
    private readonly StateComparer _stateComparer;
    private readonly HashSet<int> _stateIndex;
    private readonly ChunkedList<RunRecord> _runs = new(8, 64);
    private readonly List<PdfImage> _images = [];
    private readonly List<PdfShading> _shadings = [];
    private readonly List<PdfObject> _objects = [];
    private readonly List<string> _strings = [];
    private readonly List<PdfGroupInfo> _groups = [];
    private readonly List<PdfSoftMaskInfo> _masks = [];
    private readonly List<PdfAnnotationInfo> _annots = [];
    private readonly List<PdfType3GlyphInfo> _type3 = [];
    private readonly List<PdfPageInfo> _pages = [];
    private readonly List<int> _openClips = [];
    private readonly List<int> _openOther = [];
    private int _lastState = -1;

    /// <summary>Creates an empty recording.</summary>
    public PdfDisplayList()
    {
        _stateComparer = new StateComparer(_states);
        _stateIndex = new HashSet<int>(_stateComparer);
    }

    // Finds a state by value without storing it twice: the set holds indices into the state list, and a candidate that is not
    // stored yet is compared through the probe slot (index -1).
    private sealed class StateComparer(ChunkedList<PdfGraphicsState> states) : IEqualityComparer<int>
    {
        private PdfGraphicsState _probe;

        public void SetProbe(in PdfGraphicsState s) => _probe = s;

        public bool Equals(int a, int b) => (a < 0 ? _probe : states[a]).Equals(b < 0 ? _probe : states[b]);

        public int GetHashCode(int a) => (a < 0 ? _probe : states[a]).GetHashCode();
    }

    /// <summary>Number of recorded items (every device call is one item).</summary>
    public int ItemCount => _items.Count;

    /// <summary>
    /// The bounding box, in output space, recorded for item <paramref name="index"/>. Items without geometry (brackets,
    /// marked content) have an unbounded box; a shading is bounded by its <c>/BBox</c> when it has one.
    /// </summary>
    public PdfBounds GetItemBounds(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _items.Count);
        ref Item it = ref _items[index];
        return new PdfBounds(it.X0, it.Y0, it.X1, it.Y1);
    }

    /// <summary>The device calls in recording order, as short names (for tests and diagnostics).</summary>
    public string GetItemKind(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _items.Count);
        return _items[index].Kind.ToString();
    }

    /// <summary>
    /// Plays the recording into <paramref name="device"/>. Paint items whose box misses <paramref name="clipRect"/> (output
    /// space) are skipped, as are whole clip and group brackets that miss it; everything else is delivered, so a device
    /// that only cares about that rectangle sees the same picture there as a full replay. Pass null for a full replay.
    /// Safe to call from several threads at once.
    /// </summary>
    public void Replay(IPdfDevice device, PdfBounds? clipRect = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        bool cull = clipRect.HasValue;
        PdfBounds clip = clipRect.GetValueOrDefault();
        PdfGraphicsState state = default;
        int stateLoaded = -1;
        for (int i = 0; i < _items.Count; i++)
        {
            ref readonly Item it = ref _items[i];
            switch (it.Kind)
            {
                case Kind.BeginPage:
                    {
                        PdfPageInfo p = _pages[it.A];
                        device.BeginPage(in p);
                        break;
                    }

                case Kind.EndPage:
                    device.EndPage();
                    break;
                case Kind.Fill:
                case Kind.Stroke:
                case Kind.FillStroke:
                case Kind.Clip:
                    {
                        if (cull && !Overlaps(in it, in clip))
                        {
                            if (it.Kind == Kind.Clip && it.Match > i)
                            {
                                SkipRegion(device, i, it.Match);
                                i = it.Match;
                            }

                            break;
                        }

                        Load(it.State, ref state, ref stateLoaded);
                        var path = new PdfPath(_verbs.Memory(it.VerbChunk, it.A, it.B), _points.Memory(it.PointChunk, it.C, it.D));
                        if (it.Kind == Kind.Fill)
                        {
                            device.FillPath(in path, it.EvenOdd, in state);
                        }
                        else if (it.Kind == Kind.Stroke)
                        {
                            device.StrokePath(in path, in state);
                        }
                        else if (it.Kind == Kind.FillStroke)
                        {
                            device.FillStrokePath(in path, it.EvenOdd, in state);
                        }
                        else
                        {
                            device.PushClip(in path, it.EvenOdd, in state);
                        }

                        break;
                    }

                case Kind.TextClip:
                    Load(it.State, ref state, ref stateLoaded);
                    device.PushTextClip(in state);
                    break;
                case Kind.PopClip:
                    device.PopClip();
                    break;
                case Kind.Glyphs:
                    {
                        if (cull && !Overlaps(in it, in clip))
                        {
                            break;
                        }

                        Load(it.State, ref state, ref stateLoaded);
                        RunRecord r = _runs[it.A];
                        var run = new PdfGlyphRun(
                            r.FontName, r.Font, r.FontSize, r.Mode, r.Tc, r.Tw, r.Th, r.Rise, r.Tm, r.Vertical,
                            _codes.Memory(r.CodeChunk, r.CodeStart, r.CodeLength), _glyphs.Memory(r.GlyphChunk, r.GlyphStart, r.GlyphCount), r.AdvX, r.AdvY);
                        device.ShowGlyphRun(in run, in state);
                        break;
                    }

                case Kind.Image:
                    {
                        if (cull && !Overlaps(in it, in clip))
                        {
                            break;
                        }

                        Load(it.State, ref state, ref stateLoaded);
                        PdfImage img = _images[it.A];
                        device.DrawImage(in img, in state);
                        break;
                    }

                case Kind.Shading:
                    if (cull && !Overlaps(in it, in clip))
                    {
                        break;
                    }

                    Load(it.State, ref state, ref stateLoaded);
                    device.PaintShading(_shadings[it.A], in state);
                    break;
                case Kind.BeginGroup:
                    {
                        if (cull && !Overlaps(in it, in clip) && it.Match > i)
                        {
                            SkipRegion(device, i, it.Match);
                            i = it.Match;
                            break;
                        }

                        Load(it.State, ref state, ref stateLoaded);
                        PdfGroupInfo g = _groups[it.A];
                        device.BeginGroup(in g, in state);
                        break;
                    }

                case Kind.EndGroup:
                    device.EndGroup();
                    break;
                case Kind.BeginMask:
                    {
                        Load(it.State, ref state, ref stateLoaded);
                        PdfSoftMaskInfo m = _masks[it.A];
                        device.BeginSoftMask(in m, in state);
                        break;
                    }

                case Kind.EndMask:
                    device.EndSoftMask();
                    break;
                case Kind.BeginMc:
                    device.BeginMarkedContent(_strings[it.A], it.B < 0 ? null : (PdfDictionary)_objects[it.B]);
                    break;
                case Kind.EndMc:
                    device.EndMarkedContent();
                    break;
                case Kind.BeginAnnot:
                    {
                        PdfAnnotationInfo a = _annots[it.A];
                        device.BeginAnnotation(in a);
                        break;
                    }

                case Kind.EndAnnot:
                    device.EndAnnotation();
                    break;
                case Kind.BeginType3:
                    {
                        if (cull && !Overlaps(in it, in clip) && it.Match > i)
                        {
                            SkipRegion(device, i, it.Match);
                            i = it.Match;
                            break;
                        }

                        Load(it.State, ref state, ref stateLoaded);
                        PdfType3GlyphInfo g3 = _type3[it.A];
                        device.BeginType3Glyph(in g3, in state);
                        break;
                    }

                case Kind.EndType3:
                    device.EndType3Glyph();
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>The text form of the recording (see <c>docs/display-list-format.md</c>).</summary>
    public string ToText()
    {
        using var sw = new StringWriter(CultureInfo.InvariantCulture);
        var writer = new PdfDisplayListTextWriter(sw);
        writer.WriteHeader();
        Replay(writer);
        return sw.ToString();
    }

    // A culled bracket still delivers the marked-content and annotation brackets inside it: those may straddle clip and
    // group boundaries, and a device must see them balanced.
    private void SkipRegion(IPdfDevice device, int from, int to)
    {
        for (int j = from + 1; j < to; j++)
        {
            switch (_items[j].Kind)
            {
                case Kind.BeginMc:
                    device.BeginMarkedContent(_strings[_items[j].A], _items[j].B < 0 ? null : (PdfDictionary)_objects[_items[j].B]);
                    break;
                case Kind.EndMc:
                    device.EndMarkedContent();
                    break;
                case Kind.BeginAnnot:
                    {
                        PdfAnnotationInfo a = _annots[_items[j].A];
                        device.BeginAnnotation(in a);
                        break;
                    }

                case Kind.EndAnnot:
                    device.EndAnnotation();
                    break;
                default:
                    break;
            }
        }
    }

    private static bool Overlaps(in Item it, in PdfBounds clip) =>
        it.X0 <= clip.Right && clip.Left <= it.X1 && it.Y0 <= clip.Top && clip.Bottom <= it.Y1;

    private void Load(int index, ref PdfGraphicsState state, ref int loaded)
    {
        if (index != loaded)
        {
            state = _states[index];
            loaded = index;
        }
    }

    // ---- recording ----

    private int Add(Kind kind, int state, in PdfBounds bounds)
    {
        var it = default(Item);
        it.Kind = kind;
        it.State = state;
        it.Match = -1;
        it.X0 = bounds.Left;
        it.Y0 = bounds.Bottom;
        it.X1 = bounds.Right;
        it.Y1 = bounds.Top;
        return _items.Add(it);
    }

    private int StateIndex(in PdfGraphicsState s)
    {
        if (_lastState >= 0 && _states[_lastState].Equals(s))
        {
            return _lastState;
        }

        _stateComparer.SetProbe(s);
        if (!_stateIndex.TryGetValue(-1, out int idx))
        {
            idx = _states.Add(s);
            _stateIndex.Add(idx);
        }

        _lastState = idx;
        return idx;
    }

    private int AddObject(PdfObject o)
    {
        _objects.Add(o);
        return _objects.Count - 1;
    }

    private int AddString(string s)
    {
        _strings.Add(s);
        return _strings.Count - 1;
    }

    private static PdfBounds Unbounded => PdfBounds.Unbounded;

    private void StorePath(in PdfPath p, ref Item it)
    {
        p.VerbBytes.Span.CopyTo(_verbs.Reserve(p.VerbBytes.Length, out it.VerbChunk, out it.A));
        p.Points.Span.CopyTo(_points.Reserve(p.Points.Length, out it.PointChunk, out it.C));
        it.B = p.VerbBytes.Length;
        it.D = p.Points.Length;
    }

    private int AddPathItem(Kind kind, in PdfPath path, bool evenOdd, in PdfGraphicsState state, double pad)
    {
        PdfBounds b = path.IsEmpty ? default : state.Ctm.TransformBounds(path.ControlBounds()).Inflate(pad);
        int idx = Add(kind, StateIndex(state), b);
        ref Item it = ref _items[idx];
        it.EvenOdd = evenOdd;
        StorePath(path, ref it);
        return idx;
    }

    private static double DeviceScale(in PdfMatrix m) =>
        Math.Max(Math.Sqrt((m.A * m.A) + (m.B * m.B)), Math.Sqrt((m.C * m.C) + (m.D * m.D)));

    /// <inheritdoc/>
    public void BeginPage(in PdfPageInfo page)
    {
        _pages.Add(page);
        int i = Add(Kind.BeginPage, 0, Unbounded);
        _items[i].A = _pages.Count - 1;
    }

    /// <inheritdoc/>
    public void EndPage() => Add(Kind.EndPage, 0, Unbounded);

    /// <inheritdoc/>
    public void FillPath(in PdfPath path, bool evenOdd, in PdfGraphicsState state) =>
        AddPathItem(Kind.Fill, path, evenOdd, state, 0);

    /// <inheritdoc/>
    public void StrokePath(in PdfPath path, in PdfGraphicsState state) =>
        AddPathItem(Kind.Stroke, path, false, state, StrokePad(state));

    /// <inheritdoc/>
    /// <remarks>Recorded as one item; its box is the stroke's, which contains the fill.</remarks>
    public void FillStrokePath(in PdfPath path, bool evenOdd, in PdfGraphicsState state) =>
        AddPathItem(Kind.FillStroke, path, evenOdd, state, StrokePad(state));

    private static double StrokePad(in PdfGraphicsState state)
    {
        double scale = DeviceScale(state.Ctm);
        double half = Math.Max(state.LineWidth * scale, 1) / 2;
        double pad = half * Math.Max(1, state.LineJoin == 0 ? state.MiterLimit : 1);
        if (state.LineCap == 2)
        {
            pad = Math.Max(pad, half * 1.5);
        }

        return pad;
    }

    /// <inheritdoc/>
    public void PushClip(in PdfPath path, bool evenOdd, in PdfGraphicsState state)
    {
        int i = AddPathItem(Kind.Clip, path, evenOdd, state, 0);
        if (path.IsEmpty)
        {
            // An empty clip path shows nothing; give it an empty box so culling can skip it.
            _items[i].X0 = _items[i].Y0 = _items[i].X1 = _items[i].Y1 = 0;
        }

        _openClips.Add(i);
    }

    /// <inheritdoc/>
    public void PushTextClip(in PdfGraphicsState state)
    {
        int i = Add(Kind.TextClip, StateIndex(state), Unbounded);
        _openClips.Add(i);
    }

    /// <inheritdoc/>
    public void PopClip()
    {
        int i = Add(Kind.PopClip, 0, Unbounded);
        CloseBracket(_openClips, i);
    }

    private void CloseBracket(List<int> stack, int endIndex)
    {
        if (stack.Count > 0)
        {
            int open = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            _items[open].Match = endIndex;
        }
    }

    /// <inheritdoc/>
    public void ShowGlyphRun(in PdfGlyphRun run, in PdfGraphicsState state)
    {
        double fs = Math.Abs(run.FontSize);
        double ex = fs * Math.Max(Math.Abs(run.HorizontalScale), 0.01);
        double x0 = Math.Min(0, run.AdvanceX) - ex;
        double x1 = Math.Max(0, run.AdvanceX) + ex;
        double y0 = Math.Min(0, run.AdvanceY) + run.Rise - fs;
        double y1 = Math.Max(0, run.AdvanceY) + run.Rise + (2 * fs);
        PdfMatrix full = run.TextMatrix.Then(state.Ctm);
        PdfBounds b = full.TransformBounds(new PdfBounds(x0, y0, x1, y1));
        if (run.RenderMode is PdfTextRenderMode.Stroke or PdfTextRenderMode.FillStroke or PdfTextRenderMode.StrokeClip or PdfTextRenderMode.FillStrokeClip)
        {
            b = b.Inflate(Math.Max(state.LineWidth * DeviceScale(state.Ctm), 1) * Math.Max(1, state.MiterLimit));
        }

        ReadOnlySpan<byte> codes = run.Codes.Span;
        codes.CopyTo(_codes.Reserve(codes.Length, out int codeChunk, out int codeStart));
        ReadOnlySpan<PdfGlyph> glyphs = run.Glyphs.Span;
        Span<PdfGlyph> dest = _glyphs.Reserve(glyphs.Length, out int glyphChunk, out int glyphStart);

        // Outlines handed over for the duration of the call are copied so the recording owns them; ToOwned returns an
        // immutable outline (a cached glyph shape) as it is, so every use of a glyph shares one copy.
        for (int i = 0; i < glyphs.Length; i++)
        {
            PdfGlyph g = glyphs[i];
            dest[i] = g.Outline.IsEmpty || g.Outline.IsImmutable ? g : g with { Outline = g.Outline.ToOwned() };
        }

        _runs.Add(new RunRecord
        {
            FontName = run.FontName,
            Font = run.Font,
            FontSize = run.FontSize,
            Mode = run.RenderMode,
            Tc = run.CharSpacing,
            Tw = run.WordSpacing,
            Th = run.HorizontalScale,
            Rise = run.Rise,
            Tm = run.TextMatrix,
            Vertical = run.IsVertical,
            CodeChunk = codeChunk,
            CodeStart = codeStart,
            CodeLength = codes.Length,
            GlyphChunk = glyphChunk,
            GlyphStart = glyphStart,
            GlyphCount = glyphs.Length,
            AdvX = run.AdvanceX,
            AdvY = run.AdvanceY,
        });
        int idx = Add(Kind.Glyphs, StateIndex(state), b);
        _items[idx].A = _runs.Count - 1;
    }

    /// <inheritdoc/>
    public void DrawImage(in PdfImage image, in PdfGraphicsState state)
    {
        PdfBounds b = state.Ctm.TransformBounds(new PdfBounds(0, 0, 1, 1));
        _images.Add(image);
        int idx = Add(Kind.Image, StateIndex(state), b);
        _items[idx].A = _images.Count - 1;
    }

    /// <inheritdoc/>
    public void PaintShading(PdfShading shading, in PdfGraphicsState state)
    {
        // Painted over the whole clip, so only the shading's own box can bound it.
        PdfBounds b = shading.BBox is { } box ? state.Ctm.TransformBounds(box) : Unbounded;
        _shadings.Add(shading);
        int idx = Add(Kind.Shading, StateIndex(state), b);
        _items[idx].A = _shadings.Count - 1;
    }

    /// <inheritdoc/>
    public void BeginGroup(in PdfGroupInfo group, in PdfGraphicsState state)
    {
        PdfBounds b = group.Bounds.IsFinite ? state.Ctm.TransformBounds(group.Bounds) : Unbounded;
        _groups.Add(group);
        int idx = Add(Kind.BeginGroup, StateIndex(state), b);
        _items[idx].A = _groups.Count - 1;
        _openOther.Add(idx);
    }

    /// <inheritdoc/>
    public void EndGroup() => CloseBracket(_openOther, Add(Kind.EndGroup, 0, Unbounded));

    /// <inheritdoc/>
    public void BeginSoftMask(in PdfSoftMaskInfo mask, in PdfGraphicsState state)
    {
        _masks.Add(mask);
        int idx = Add(Kind.BeginMask, StateIndex(state), Unbounded);
        _items[idx].A = _masks.Count - 1;
        _openOther.Add(idx);
    }

    /// <inheritdoc/>
    public void EndSoftMask() => CloseBracket(_openOther, Add(Kind.EndMask, 0, Unbounded));

    /// <inheritdoc/>
    public void BeginMarkedContent(string tag, PdfDictionary? properties)
    {
        int idx = Add(Kind.BeginMc, 0, Unbounded);
        _items[idx].A = AddString(tag);
        _items[idx].B = properties is null ? -1 : AddObject(properties);
        _openOther.Add(idx);
    }

    /// <inheritdoc/>
    public void EndMarkedContent() => CloseBracket(_openOther, Add(Kind.EndMc, 0, Unbounded));

    /// <inheritdoc/>
    public void BeginAnnotation(in PdfAnnotationInfo annotation)
    {
        _annots.Add(annotation);
        int idx = Add(Kind.BeginAnnot, 0, Unbounded);
        _items[idx].A = _annots.Count - 1;
        _openOther.Add(idx);
    }

    /// <inheritdoc/>
    public void EndAnnotation() => CloseBracket(_openOther, Add(Kind.EndAnnot, 0, Unbounded));

    /// <inheritdoc/>
    public void BeginType3Glyph(in PdfType3GlyphInfo glyph, in PdfGraphicsState state)
    {
        PdfBounds b = glyph.FontBBox is { } fb && fb.IsFinite ? state.Ctm.TransformBounds(fb) : Unbounded;
        _type3.Add(glyph);
        int idx = Add(Kind.BeginType3, StateIndex(state), b);
        _items[idx].A = _type3.Count - 1;
        _openOther.Add(idx);
    }

    /// <inheritdoc/>
    public void EndType3Glyph() => CloseBracket(_openOther, Add(Kind.EndType3, 0, Unbounded));
}
