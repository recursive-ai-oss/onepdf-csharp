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

    // A glyph as stored: the public PdfGlyph carries a 40-byte outline view and three rarely used vertical-writing fields,
    // which made it more than twice this size. The outline is an index into a table that shares the cached glyph shapes, and
    // the vertical fields live in a side table that only vertical runs use. PdfGlyph is rebuilt exactly on replay.
    private struct GlyphRecord
    {
        public int Code;
        public int ByteLength;
        public int GlyphId;
        public int Outline;
        public int Vertical;
        public double OffsetX;
        public double OffsetY;
        public double Width;
        public string? Unicode;
    }

    private struct VerticalRecord
    {
        public double VectorX;
        public double VectorY;
        public double Advance;
    }

    // The text-state values that repeat from one run to the next, stored once and shared (like graphics states).
    private struct RunParams
    {
        public int Font;
        public double FontSize;
        public PdfTextRenderMode Mode;
        public double Tc;
        public double Tw;
        public double Th;
        public double Rise;
        public bool Vertical;

        // Bitwise, so that 0 and -0 stay different and a NaN equals itself.
        public readonly bool SameAs(in RunParams o) =>
            Font == o.Font && Mode == o.Mode && Vertical == o.Vertical && Bits(FontSize) == Bits(o.FontSize) && Bits(Tc) == Bits(o.Tc)
            && Bits(Tw) == Bits(o.Tw) && Bits(Th) == Bits(o.Th) && Bits(Rise) == Bits(o.Rise);

        public readonly int Hash() =>
            HashCode.Combine(Font, (int)Mode, Vertical, Bits(FontSize), Bits(Tc), HashCode.Combine(Bits(Tw), Bits(Th), Bits(Rise)));

        private static long Bits(double d) => BitConverter.DoubleToInt64Bits(d);
    }

    private struct RunRecord
    {
        public int Params;
        public PdfMatrix Tm;
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
    private readonly ChunkArena<GlyphRecord> _glyphs = new(1 << 10, 64);
    private readonly List<PdfPath> _outlines = [];
    private readonly Dictionary<OutlineKey, int> _outlineIndex = [];
    private readonly ChunkedList<VerticalRecord> _vertical = new(8, 16);
    private readonly List<(string Name, PdfDictionary? Dict)> _fonts = [];
    private readonly Dictionary<FontKey, int> _fontIndex = new(new FontKeyComparer());
    private readonly ChunkedList<RunParams> _params = new(8, 16);
    private readonly ParamsComparer _paramsComparer;
    private readonly HashSet<int> _paramsIndex;
    private int _lastParams = -1;
    private int _lastFont = -1;
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
        _paramsComparer = new ParamsComparer(_params);
        _paramsIndex = new HashSet<int>(_paramsComparer);
    }

    private sealed class ParamsComparer(ChunkedList<RunParams> table) : IEqualityComparer<int>
    {
        private RunParams _probe;

        public void SetProbe(in RunParams p) => _probe = p;

        public bool Equals(int a, int b) => (a < 0 ? _probe : table[a]).SameAs(b < 0 ? _probe : table[b]);

        public int GetHashCode(int a) => (a < 0 ? _probe : table[a]).Hash();
    }

    private readonly record struct FontKey(string Name, PdfDictionary? Dict);

    private sealed class FontKeyComparer : IEqualityComparer<FontKey>
    {
        public bool Equals(FontKey a, FontKey b) => ReferenceEquals(a.Dict, b.Dict) && string.Equals(a.Name, b.Name, StringComparison.Ordinal);

        public int GetHashCode(FontKey k) => HashCode.Combine(k.Name, k.Dict is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(k.Dict));
    }

    // Two outlines are the same when they view the same memory (a glyph shape from the font's cache, used again).
    private readonly record struct OutlineKey(ReadOnlyMemory<byte> Verbs, ReadOnlyMemory<double> Points);

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
        PdfGlyph[] glyphScratch = [];
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
                        ref readonly RunRecord r = ref _runs[it.A];
                        ref readonly RunParams rp = ref _params[r.Params];
                        (string fontName, PdfDictionary? font) = _fonts[rp.Font];
                        if (glyphScratch.Length < r.GlyphCount)
                        {
                            glyphScratch = new PdfGlyph[Math.Max(r.GlyphCount, Math.Max(16, glyphScratch.Length * 2))];
                        }

                        ReadOnlySpan<GlyphRecord> stored = _glyphs.Memory(r.GlyphChunk, r.GlyphStart, r.GlyphCount).Span;
                        for (int k = 0; k < stored.Length; k++)
                        {
                            glyphScratch[k] = Rebuild(in stored[k]);
                        }

                        var run = new PdfGlyphRun(
                            fontName, font, rp.FontSize, rp.Mode, rp.Tc, rp.Tw, rp.Th, rp.Rise, r.Tm, rp.Vertical,
                            _codes.Memory(r.CodeChunk, r.CodeStart, r.CodeLength), glyphScratch.AsMemory(0, r.GlyphCount), r.AdvX, r.AdvY);
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

    private PdfGlyph Rebuild(in GlyphRecord g)
    {
        PdfPath outline = g.Outline < 0 ? default : _outlines[g.Outline];
        if (g.Vertical < 0)
        {
            return new PdfGlyph(g.Code, g.ByteLength, g.OffsetX, g.OffsetY, g.Width, g.GlyphId, g.Unicode, outline);
        }

        ref VerticalRecord v = ref _vertical[g.Vertical];
        return new PdfGlyph(g.Code, g.ByteLength, g.OffsetX, g.OffsetY, g.Width, g.GlyphId, g.Unicode, outline, v.VectorX, v.VectorY, v.Advance);
    }

    private int OutlineIndex(in PdfPath outline)
    {
        if (outline.IsEmpty && outline.Points.IsEmpty)
        {
            return -1;
        }

        // Library-owned shapes are shared between uses; a caller's temporary memory is copied, once per glyph.
        if (!outline.IsImmutable)
        {
            _outlines.Add(outline.ToOwned());
            return _outlines.Count - 1;
        }

        var key = new OutlineKey(outline.VerbBytes, outline.Points);
        if (!_outlineIndex.TryGetValue(key, out int idx))
        {
            _outlines.Add(outline);
            idx = _outlines.Count - 1;
            _outlineIndex.Add(key, idx);
        }

        return idx;
    }

    private int FontIndex(string name, PdfDictionary? dict)
    {
        if (_lastFont >= 0 && ReferenceEquals(_fonts[_lastFont].Dict, dict) && string.Equals(_fonts[_lastFont].Name, name, StringComparison.Ordinal))
        {
            return _lastFont;
        }

        var key = new FontKey(name, dict);
        if (!_fontIndex.TryGetValue(key, out int idx))
        {
            _fonts.Add((name, dict));
            idx = _fonts.Count - 1;
            _fontIndex.Add(key, idx);
        }

        _lastFont = idx;
        return idx;
    }

    private int ParamsIndex(in RunParams p)
    {
        if (_lastParams >= 0 && _params[_lastParams].SameAs(p))
        {
            return _lastParams;
        }

        _paramsComparer.SetProbe(p);
        if (!_paramsIndex.TryGetValue(-1, out int idx))
        {
            idx = _params.Add(p);
            _paramsIndex.Add(idx);
        }

        _lastParams = idx;
        return idx;
    }

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
        Span<GlyphRecord> dest = _glyphs.Reserve(glyphs.Length, out int glyphChunk, out int glyphStart);
        for (int i = 0; i < glyphs.Length; i++)
        {
            ref readonly PdfGlyph g = ref glyphs[i];
            int vertical = -1;
            if (BitConverter.DoubleToInt64Bits(g.VectorX) != 0 || BitConverter.DoubleToInt64Bits(g.VectorY) != 0 || BitConverter.DoubleToInt64Bits(g.VerticalAdvance) != 0)
            {
                vertical = _vertical.Add(new VerticalRecord { VectorX = g.VectorX, VectorY = g.VectorY, Advance = g.VerticalAdvance });
            }

            dest[i] = new GlyphRecord
            {
                Code = g.Code,
                ByteLength = g.ByteLength,
                GlyphId = g.GlyphId,
                Outline = OutlineIndex(g.Outline),
                Vertical = vertical,
                OffsetX = g.OffsetX,
                OffsetY = g.OffsetY,
                Width = g.Width,
                Unicode = g.Unicode,
            };
        }

        var rp = new RunParams
        {
            Font = FontIndex(run.FontName, run.Font),
            FontSize = run.FontSize,
            Mode = run.RenderMode,
            Tc = run.CharSpacing,
            Tw = run.WordSpacing,
            Th = run.HorizontalScale,
            Rise = run.Rise,
            Vertical = run.IsVertical,
        };
        _runs.Add(new RunRecord
        {
            Params = ParamsIndex(rp),
            Tm = run.TextMatrix,
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
