namespace OnePdf.FontPrograms;

/// <summary>
/// Reads TrueType outlines from <c>glyf</c>/<c>loca</c>: simple glyphs, composites (flattened with an explicit stack, depth and size
/// bounded) and the on/off-curve quadratic point convention. Unhinted, except for fonts whose composites need their programs
/// (see <see cref="TrueTypeHinter"/> and <c>docs/pdf-fonts.md</c>): then the glyph and composite instructions run at one pixel per
/// font unit. Returns font units.
/// </summary>
internal sealed class TrueTypeGlyphs
{
    private const int MaxCompositeDepth = 8;
    private const int MaxComponents = 512;
    private const int MaxPoints = 1 << 18;

    private readonly ReadOnlyMemory<byte> _glyf;
    private readonly ReadOnlyMemory<byte> _loca;
    private readonly bool _longLoca;
    private readonly int _numGlyphs;
    private readonly TrueTypeHinter? _hinter;
    private readonly ReadOnlyMemory<byte> _hmtx;
    private readonly int _numHMetrics;
    private string? _hintFailure;

    public TrueTypeGlyphs(ReadOnlyMemory<byte> glyf, ReadOnlyMemory<byte> loca, bool longLoca, int numGlyphs, TrueTypeHinter? hinter = null, ReadOnlyMemory<byte> hmtx = default, int numHMetrics = 0)
    {
        _hinter = hinter;
        _hmtx = hmtx;
        _numHMetrics = Math.Min(numHMetrics, hmtx.Length / 4);
        _glyf = glyf;
        _loca = loca;
        _longLoca = longLoca;
        _numGlyphs = numGlyphs;
        long need = (numGlyphs + 1L) * (longLoca ? 4 : 2);
        if (loca.Length < need)
        {
            // Some fonts trim trailing empty glyphs from loca; entries past the end read as "no outline" below.
            _numGlyphs = Math.Max(0, (int)(loca.Length / (longLoca ? 4 : 2)) - 1);
        }
    }

    /// <summary>The reason the font's programs failed on the first glyph where they did (that glyph was drawn from its plain outline), or null.</summary>
    public string? HintFailure => Volatile.Read(ref _hintFailure);

    /// <summary>
    /// Loads and converts one glyph. Out-of-range ids and empty glyphs give <see cref="GlyphOutline.Empty"/>. When the font runs its
    /// programs and any of them fails for this glyph, the plain outline is returned and <see cref="HintFailure"/> set.
    /// </summary>
    public GlyphOutline Load(int glyphId)
    {
        if (_hinter is not null)
        {
            var budget = new TrueTypeHinter.HintBudget();
            var hinted = new PointBuffer();
            bool found = Append(hinted, glyphId, budget);
            if (budget.Failure is not null)
            {
                Interlocked.CompareExchange(ref _hintFailure, budget.Failure, null);
            }

            // Only a spent shared budget sends the whole glyph back to its plain outline; a program that fails on its own
            // leaves just its component unhinted (its points are untouched) and the rest keeps its fitted result.
            if (!budget.Exhausted)
            {
                return found ? hinted.ToOutline() : GlyphOutline.Empty;
            }
        }

        var pts = new PointBuffer();
        if (!Append(pts, glyphId, null))
        {
            return GlyphOutline.Empty;
        }

        return pts.ToOutline();
    }

    // Advance and left side bearing of a glyph from hmtx (OpenType 'hmtx': glyphs past numberOfHMetrics share the last advance).
    private void Metrics(int glyphId, out int advance, out int lsb, out bool known)
    {
        advance = 0;
        lsb = 0;
        known = false;
        if (_numHMetrics == 0)
        {
            return;
        }

        ReadOnlySpan<byte> h = _hmtx.Span;
        int i = Math.Min(glyphId, _numHMetrics - 1);
        advance = Be.U16(h, 4 * i);
        long lsbAt = glyphId < _numHMetrics ? (4L * glyphId) + 2 : (4L * _numHMetrics) + (2L * (glyphId - _numHMetrics));
        if (lsbAt + 2 <= h.Length)
        {
            lsb = Be.I16(h, (int)lsbAt);
            known = true;
        }
    }

    private bool TryGetData(int glyphId, out ReadOnlySpan<byte> data)
    {
        data = default;
        if ((uint)glyphId >= (uint)_numGlyphs)
        {
            return false;
        }

        ReadOnlySpan<byte> loca = _loca.Span;
        uint start, end;
        if (_longLoca)
        {
            start = Be.U32(loca, glyphId * 4);
            end = Be.U32(loca, (glyphId * 4) + 4);
        }
        else
        {
            start = (uint)Be.U16(loca, glyphId * 2) * 2;
            end = (uint)Be.U16(loca, (glyphId * 2) + 2) * 2;
        }

        if (end <= start)
        {
            return false;
        }

        ReadOnlySpan<byte> glyf = _glyf.Span;
        if (start >= glyf.Length)
        {
            return false;
        }

        // A glyph that claims to run past the table is clipped to it; the reads below stay bounds-checked.
        data = glyf.Slice((int)start, (int)(Math.Min(end, (uint)glyf.Length) - start));
        return data.Length >= 10;
    }

    private struct Frame
    {
        public int GlyphId;
        public int Pos;
        public bool More;
        public int StartPoint;
        public int InsPos;
        public int InsLen;
        public ushort Flags;
        public int Arg1;
        public int Arg2;
        public float A, B, C, D;
    }

    // Flattens the glyph and every component into one point list. Each composite gets a frame; a finished child is transformed in place
    // (matrix, then offset) so nested composites need no recursion.
    private bool Append(PointBuffer pts, int rootId, TrueTypeHinter.HintBudget? budget)
    {
        if (!TryGetData(rootId, out ReadOnlySpan<byte> root))
        {
            return false;
        }

        var stack = new List<Frame>(4);
        int components = 0;
        if (!BeginGlyph(pts, stack, rootId, root, default, budget))
        {
            return true;
        }

        while (stack.Count > 0)
        {
            int top = stack.Count - 1;
            Frame f = stack[top];
            if (!f.More)
            {
                stack.RemoveAt(top);
                if (budget is not null && f.InsLen > 0 && TryGetData(f.GlyphId, out ReadOnlySpan<byte> own))
                {
                    HintComposite(pts, f, own, budget);
                }

                if (top > 0)
                {
                    // The composite is complete: place it in its parent.
                    Place(pts, f, stack[top - 1].StartPoint);
                }

                continue;
            }

            if (++components > MaxComponents)
            {
                throw new FontProgramException("A composite glyph has too many components.");
            }

            TryGetData(f.GlyphId, out ReadOnlySpan<byte> data);
            int p = f.Pos;
            ushort flags = Be.U16(data, p);
            int child = Be.U16(data, p + 2);
            p += 4;
            int a1, a2;
            if ((flags & 0x1) != 0)
            {
                if ((flags & 0x2) != 0)
                {
                    a1 = Be.I16(data, p);
                    a2 = Be.I16(data, p + 2);
                }
                else
                {
                    a1 = Be.U16(data, p);
                    a2 = Be.U16(data, p + 2);
                }

                p += 4;
            }
            else
            {
                if ((flags & 0x2) != 0)
                {
                    a1 = Be.I8(data, p);
                    a2 = Be.I8(data, p + 1);
                }
                else
                {
                    a1 = Be.U8(data, p);
                    a2 = Be.U8(data, p + 1);
                }

                p += 2;
            }

            float a = 1, b = 0, c = 0, d = 1;
            if ((flags & 0x8) != 0)
            {
                a = d = Be.I16(data, p) / 16384f;
                p += 2;
            }
            else if ((flags & 0x40) != 0)
            {
                a = Be.I16(data, p) / 16384f;
                d = Be.I16(data, p + 2) / 16384f;
                p += 4;
            }
            else if ((flags & 0x80) != 0)
            {
                a = Be.I16(data, p) / 16384f;
                b = Be.I16(data, p + 2) / 16384f;
                c = Be.I16(data, p + 4) / 16384f;
                d = Be.I16(data, p + 6) / 16384f;
                p += 8;
            }

            f.Pos = p;
            f.More = (flags & 0x20) != 0;
            if (!f.More && (flags & 0x100) != 0 && p + 2 <= data.Length)
            {
                // WE_HAVE_INSTRUCTIONS: the composite's own program follows its last component (OpenType 'glyf').
                int n = Be.U16(data, p);
                if (p + 2 + n <= data.Length)
                {
                    f.InsPos = p + 2;
                    f.InsLen = n;
                }
            }

            stack[top] = f;

            var comp = new Frame { GlyphId = child, Flags = flags, Arg1 = a1, Arg2 = a2, A = a, B = b, C = c, D = d, StartPoint = pts.Count };
            if (!TryGetData(child, out ReadOnlySpan<byte> childData))
            {
                continue;
            }

            if (stack.Count >= MaxCompositeDepth)
            {
                throw new FontProgramException("Composite glyphs are nested too deeply.");
            }

            if (!BeginGlyph(pts, stack, child, childData, comp, budget))
            {
                // Simple glyph: already appended, place it now. The parent frame is the top of the stack.
                Place(pts, comp, f.StartPoint);
            }
        }

        return true;
    }

    // Returns true when the glyph is a composite and a frame was pushed; a simple glyph is appended to the points and false returned.
    private bool BeginGlyph(PointBuffer pts, List<Frame> stack, int glyphId, ReadOnlySpan<byte> data, Frame placement, TrueTypeHinter.HintBudget? budget)
    {
        short contours = Be.I16(data, 0);
        if (contours >= 0)
        {
            ReadSimple(pts, data, contours);
            if (budget is not null)
            {
                HintSimple(pts, glyphId, data, contours, budget);
            }

            return false;
        }

        placement.GlyphId = glyphId;
        placement.Pos = 10;
        placement.More = true;
        placement.StartPoint = pts.Count;
        stack.Add(placement);
        return true;
    }

    // A transformed child is moved into its parent: the 2x2 matrix first, then the offset (or point matching).
    private static void Place(PointBuffer pts, in Frame child, int parentStart)
    {
        int start = child.StartPoint;
        bool identity = child.A == 1 && child.B == 0 && child.C == 0 && child.D == 1;
        if (!identity)
        {
            for (int i = start; i < pts.Count; i++)
            {
                float x = pts.X[i];
                float y = pts.Y[i];
                pts.X[i] = (child.A * x) + (child.C * y);
                pts.Y[i] = (child.B * x) + (child.D * y);
            }
        }

        float dx, dy;
        if ((child.Flags & 0x2) != 0)
        {
            dx = child.Arg1;
            dy = child.Arg2;
            if ((child.Flags & 0x800) != 0 && (child.Flags & 0x1000) == 0)
            {
                // Apple-style: the offset is itself scaled by the component's matrix.
                float sx = MathF.Sqrt((child.A * child.A) + (child.B * child.B));
                float sy = MathF.Sqrt((child.C * child.C) + (child.D * child.D));
                dx *= sx;
                dy *= sy;
            }
        }
        else
        {
            // Point matching: attach point arg2 of the child to point arg1 of the composite built so far.
            int parentPt = parentStart + child.Arg1;
            int childPt = start + child.Arg2;
            if (parentPt >= start || childPt >= pts.Count || parentPt < 0)
            {
                throw new FontProgramException("A composite glyph matches points that do not exist.");
            }

            dx = pts.X[parentPt] - pts.X[childPt];
            dy = pts.Y[parentPt] - pts.Y[childPt];
        }

        if (dx != 0 || dy != 0)
        {
            for (int i = start; i < pts.Count; i++)
            {
                pts.X[i] += dx;
                pts.Y[i] += dy;
            }
        }
    }

    private static void ReadSimple(PointBuffer pts, ReadOnlySpan<byte> d, int contours)
    {
        int p = 10;
        if (contours == 0)
        {
            return;
        }

        if (p + (2L * contours) > d.Length)
        {
            throw Be.Truncated();
        }

        int count = 0;
        int prevEnd = -1;
        int basePoint = pts.Count;
        for (int i = 0; i < contours; i++)
        {
            int end = Be.U16(d, p + (2 * i));
            if (end <= prevEnd)
            {
                throw new FontProgramException("Glyph contour end points are not increasing.");
            }

            prevEnd = end;
            count = end + 1;
            if (basePoint + count > MaxPoints)
            {
                throw new FontProgramException("A glyph has too many points.");
            }

            pts.AddContourEnd(basePoint + end);
        }

        p += 2 * contours;
        int insLen = Be.U16(d, p);
        p += 2 + insLen;
        pts.Reserve(count);

        // Flags, with the repeat shorthand. A run may not pass the point count.
        var flags = new byte[count];
        for (int i = 0; i < count;)
        {
            byte fl = Be.U8(d, p++);
            flags[i++] = fl;
            if ((fl & 0x8) != 0)
            {
                int rep = Be.U8(d, p++);
                if (i + rep > count)
                {
                    throw new FontProgramException("A glyph flag run passes the last point.");
                }

                for (; rep > 0; rep--)
                {
                    flags[i++] = fl;
                }
            }
        }

        int x = 0;
        for (int i = 0; i < count; i++)
        {
            byte fl = flags[i];
            if ((fl & 0x2) != 0)
            {
                int v = Be.U8(d, p++);
                x += (fl & 0x10) != 0 ? v : -v;
            }
            else if ((fl & 0x10) == 0)
            {
                x += Be.I16(d, p);
                p += 2;
            }

            pts.X[basePoint + i] = x;
        }

        int y = 0;
        for (int i = 0; i < count; i++)
        {
            byte fl = flags[i];
            if ((fl & 0x4) != 0)
            {
                int v = Be.U8(d, p++);
                y += (fl & 0x20) != 0 ? v : -v;
            }
            else if ((fl & 0x20) == 0)
            {
                y += Be.I16(d, p);
                p += 2;
            }

            pts.Y[basePoint + i] = y;
            pts.OnCurve[basePoint + i] = (fl & 0x1) != 0;
        }

        pts.Count = basePoint + count;
    }

    // Runs a simple glyph's own program on its points, before the glyph is placed in a composite.
    private void HintSimple(PointBuffer pts, int glyphId, ReadOnlySpan<byte> d, int contours, TrueTypeHinter.HintBudget budget)
    {
        if (contours == 0 || budget.Exhausted)
        {
            return;
        }

        int insPos = 10 + (2 * contours);
        int insLen = Be.U16(d, insPos);
        if (insLen == 0 || insPos + 2 + insLen > d.Length)
        {
            return;
        }

        int total = pts.Count;
        int first = total - (Be.U16(d, 10 + (2 * (contours - 1))) + 1);
        var ends = new int[contours];
        for (int i = 0; i < contours; i++)
        {
            ends[i] = Be.U16(d, 10 + (2 * i));
        }

        PhantomPoints(glyphId, d, out float left, out float right);
        if (!_hinter!.TryHint(pts.X, pts.Y, pts.OnCurve, first, total - first, ends, d.Slice(insPos + 2, insLen), left, right, budget))
        {
            budget.Failure ??= "a glyph program did not run";
        }
    }

    // A composite's program runs on the placed points of all its components.
    private void HintComposite(PointBuffer pts, in Frame f, ReadOnlySpan<byte> d, TrueTypeHinter.HintBudget budget)
    {
        if (budget.Exhausted)
        {
            return;
        }

        int first = f.StartPoint;
        int count = pts.Count - first;
        if (count <= 0 || f.InsPos + f.InsLen > d.Length)
        {
            return;
        }

        int[] ends = pts.EndsFrom(first);
        PhantomPoints(f.GlyphId, d, out float left, out float right);
        if (!_hinter!.TryHint(pts.X, pts.Y, pts.OnCurve, first, count, ends, d.Slice(f.InsPos, f.InsLen), left, right, budget))
        {
            budget.Failure ??= "a composite glyph program did not run";
        }
    }

    private void PhantomPoints(int glyphId, ReadOnlySpan<byte> d, out float left, out float right)
    {
        Metrics(glyphId, out int advance, out int lsb, out bool known);
        int xMin = Be.I16(d, 2);
        left = known ? xMin - lsb : 0;
        right = left + advance;
    }

    private sealed class PointBuffer
    {
        private int[] _ends = new int[8];

        public float[] X { get; private set; } = new float[64];

        public float[] Y { get; private set; } = new float[64];

        public bool[] OnCurve { get; private set; } = new bool[64];

        public int Count { get; set; }

        public int ContourCount { get; private set; }

        public void Reserve(int more)
        {
            int need = Count + more;
            if (need > X.Length)
            {
                int n = Math.Max(need, X.Length * 2);
                X = Grow(X, n);
                Y = Grow(Y, n);
                bool[] on = new bool[n];
                OnCurve.AsSpan(0, Count).CopyTo(on);
                OnCurve = on;
            }
        }

        // Contour end points (relative to <first>) of the contours that start at or after point <first>.
        public int[] EndsFrom(int first)
        {
            int n = 0;
            for (int i = ContourCount - 1; i >= 0 && _ends[i] >= first; i--)
            {
                n++;
            }

            var r = new int[n];
            for (int i = 0; i < n; i++)
            {
                r[i] = _ends[ContourCount - n + i] - first;
            }

            return r;
        }

        public void AddContourEnd(int endPoint)
        {
            if (ContourCount == _ends.Length)
            {
                Array.Resize(ref _ends, _ends.Length * 2);
            }

            _ends[ContourCount++] = endPoint;
        }

        public GlyphOutline ToOutline()
        {
            var b = new OutlineBuilder();
            int start = 0;
            for (int c = 0; c < ContourCount; c++)
            {
                int end = _ends[c];
                if (end >= start && end < Count)
                {
                    Contour(b, start, end);
                }

                start = end + 1;
            }

            return b.Build();
        }

        private static float[] Grow(float[] a, int n)
        {
            var r = new float[n];
            a.CopyTo(r, 0);
            return r;
        }

        // Quadratic contour: two off-curve points in a row imply an on-curve point at their midpoint.
        private void Contour(OutlineBuilder b, int s, int e)
        {
            int n = e - s + 1;
            if (n < 2)
            {
                return;
            }

            float sx, sy;
            int first;
            if (OnCurve[s])
            {
                sx = X[s];
                sy = Y[s];
                first = s + 1;
            }
            else if (OnCurve[e])
            {
                sx = X[e];
                sy = Y[e];
                first = s;
                n--;
            }
            else
            {
                sx = (X[s] + X[e]) * 0.5f;
                sy = (Y[s] + Y[e]) * 0.5f;
                first = s;
            }

            b.MoveTo(sx, sy);
            bool haveCtl = false;
            float cx = 0, cy = 0;
            int count = OnCurve[s] ? n - 1 : n;
            for (int k = 0; k < count; k++)
            {
                int i = first + k;
                float px = X[i], py = Y[i];
                if (OnCurve[i])
                {
                    if (haveCtl)
                    {
                        b.QuadTo(cx, cy, px, py);
                        haveCtl = false;
                    }
                    else
                    {
                        b.LineTo(px, py);
                    }
                }
                else
                {
                    if (haveCtl)
                    {
                        float mx = (cx + px) * 0.5f, my = (cy + py) * 0.5f;
                        b.QuadTo(cx, cy, mx, my);
                    }

                    cx = px;
                    cy = py;
                    haveCtl = true;
                }
            }

            if (haveCtl)
            {
                b.QuadTo(cx, cy, sx, sy);
            }

            b.Close();
        }
    }
}
