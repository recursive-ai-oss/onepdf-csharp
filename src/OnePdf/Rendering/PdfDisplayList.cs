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
        public int CodeStart;
        public int CodeLength;
        public int GlyphStart;
        public int GlyphCount;
        public double AdvX;
        public double AdvY;
    }

    private Item[] _items = new Item[256];
    private int _count;
    private byte[] _verbs = new byte[1024];
    private int _verbCount;
    private double[] _points = new double[2048];
    private int _pointCount;
    private byte[] _codes = new byte[1024];
    private int _codeCount;
    private PdfGlyph[] _glyphs = new PdfGlyph[256];
    private int _glyphCount;
    private readonly List<PdfGraphicsState> _states = [];
    private readonly Dictionary<PdfGraphicsState, int> _stateIndex = [];
    private readonly List<RunRecord> _runs = [];
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

    /// <summary>Number of recorded items (every device call is one item).</summary>
    public int ItemCount => _count;

    /// <summary>
    /// The bounding box, in output space, recorded for item <paramref name="index"/>. Items without geometry (brackets,
    /// marked content) have an unbounded box; a shading is bounded by its <c>/BBox</c> when it has one.
    /// </summary>
    public PdfBounds GetItemBounds(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);
        ref Item it = ref _items[index];
        return new PdfBounds(it.X0, it.Y0, it.X1, it.Y1);
    }

    /// <summary>The device calls in recording order, as short names (for tests and diagnostics).</summary>
    public string GetItemKind(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);
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
        for (int i = 0; i < _count; i++)
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
                        var path = new PdfPath(_verbs.AsMemory(it.A, it.B), _points.AsMemory(it.C, it.D));
                        if (it.Kind == Kind.Fill)
                        {
                            device.FillPath(in path, it.EvenOdd, in state);
                        }
                        else if (it.Kind == Kind.Stroke)
                        {
                            device.StrokePath(in path, in state);
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
                            _codes.AsMemory(r.CodeStart, r.CodeLength), _glyphs.AsMemory(r.GlyphStart, r.GlyphCount), r.AdvX, r.AdvY);
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
        if (_count == _items.Length)
        {
            Array.Resize(ref _items, _items.Length * 2);
        }

        ref Item it = ref _items[_count];
        it = default;
        it.Kind = kind;
        it.State = state;
        it.Match = -1;
        it.X0 = bounds.Left;
        it.Y0 = bounds.Bottom;
        it.X1 = bounds.Right;
        it.Y1 = bounds.Top;
        return _count++;
    }

    private int StateIndex(in PdfGraphicsState s)
    {
        if (_lastState >= 0 && _states[_lastState].Equals(s))
        {
            return _lastState;
        }

        if (!_stateIndex.TryGetValue(s, out int idx))
        {
            idx = _states.Count;
            _states.Add(s);
            _stateIndex[s] = idx;
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

    private (int VerbStart, int PointStart) StorePath(in PdfPath p)
    {
        ReadOnlySpan<byte> v = p.VerbBytes.Span;
        ReadOnlySpan<double> pts = p.Points.Span;
        if (_verbCount + v.Length > _verbs.Length)
        {
            Array.Resize(ref _verbs, Math.Max(_verbs.Length * 2, _verbCount + v.Length));
        }

        if (_pointCount + pts.Length > _points.Length)
        {
            Array.Resize(ref _points, Math.Max(_points.Length * 2, _pointCount + pts.Length));
        }

        int vs = _verbCount, ps = _pointCount;
        v.CopyTo(_verbs.AsSpan(_verbCount));
        pts.CopyTo(_points.AsSpan(_pointCount));
        _verbCount += v.Length;
        _pointCount += pts.Length;
        return (vs, ps);
    }

    private int AddPathItem(Kind kind, in PdfPath path, bool evenOdd, in PdfGraphicsState state, double pad)
    {
        PdfBounds b = path.IsEmpty ? default : state.Ctm.TransformBounds(path.ControlBounds()).Inflate(pad);
        (int vs, int ps) = StorePath(path);
        int idx = Add(kind, StateIndex(state), b);
        ref Item it = ref _items[idx];
        it.EvenOdd = evenOdd;
        it.A = vs;
        it.B = path.VerbBytes.Length;
        it.C = ps;
        it.D = path.Points.Length;
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
    public void StrokePath(in PdfPath path, in PdfGraphicsState state)
    {
        double scale = DeviceScale(state.Ctm);
        double half = Math.Max(state.LineWidth * scale, 1) / 2;
        double pad = half * Math.Max(1, state.LineJoin == 0 ? state.MiterLimit : 1);
        if (state.LineCap == 2)
        {
            pad = Math.Max(pad, half * 1.5);
        }

        AddPathItem(Kind.Stroke, path, false, state, pad);
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
        if (_codeCount + codes.Length > _codes.Length)
        {
            Array.Resize(ref _codes, Math.Max(_codes.Length * 2, _codeCount + codes.Length));
        }

        codes.CopyTo(_codes.AsSpan(_codeCount));
        ReadOnlySpan<PdfGlyph> glyphs = run.Glyphs.Span;
        if (_glyphCount + glyphs.Length > _glyphs.Length)
        {
            Array.Resize(ref _glyphs, Math.Max(_glyphs.Length * 2, _glyphCount + glyphs.Length));
        }

        // Outlines handed over for the duration of the call are copied so the recording owns them.
        for (int i = 0; i < glyphs.Length; i++)
        {
            PdfGlyph g = glyphs[i];
            _glyphs[_glyphCount + i] = g.Outline.IsEmpty ? g : g with { Outline = g.Outline.ToOwned() };
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
            CodeStart = _codeCount,
            CodeLength = codes.Length,
            GlyphStart = _glyphCount,
            GlyphCount = glyphs.Length,
            AdvX = run.AdvanceX,
            AdvY = run.AdvanceY,
        });
        _codeCount += codes.Length;
        _glyphCount += glyphs.Length;
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
