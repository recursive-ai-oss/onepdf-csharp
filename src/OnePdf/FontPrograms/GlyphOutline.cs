namespace OnePdf.FontPrograms;

/// <summary>
/// A glyph outline in font units, y up: figures made of line, quadratic and cubic segments. The coordinates are exactly what the
/// font program produced; the font matrix is not applied. Instances are immutable once built and may be shared between threads.
/// </summary>
internal sealed class GlyphOutline
{
    public const byte Move = 0;
    public const byte Line = 1;
    public const byte Quad = 2;
    public const byte Cubic = 3;
    public const byte Close = 4;

    public static readonly GlyphOutline Empty = new([], []);

    public GlyphOutline(byte[] verbs, float[] coords)
    {
        Verbs = verbs;
        Coords = coords;
    }

    /// <summary>One verb per segment: <see cref="Move"/> and <see cref="Line"/> take 2 coordinates, <see cref="Quad"/> 4, <see cref="Cubic"/> 6, <see cref="Close"/> none.</summary>
    public byte[] Verbs { get; }

    /// <summary>The coordinates the verbs consume, x then y.</summary>
    public float[] Coords { get; }

    public bool IsEmpty => Verbs.Length == 0;

    /// <summary>The box around all points, control points included. Empty outlines give zeros.</summary>
    public (float MinX, float MinY, float MaxX, float MaxY) ControlBox()
    {
        if (Coords.Length < 2)
        {
            return default;
        }

        float minX = Coords[0], maxX = Coords[0], minY = Coords[1], maxY = Coords[1];
        for (int i = 2; i + 1 < Coords.Length; i += 2)
        {
            minX = Math.Min(minX, Coords[i]);
            maxX = Math.Max(maxX, Coords[i]);
            minY = Math.Min(minY, Coords[i + 1]);
            maxY = Math.Max(maxY, Coords[i + 1]);
        }

        return (minX, minY, maxX, maxY);
    }

    /// <summary>Returns a copy moved by (<paramref name="dx"/>, <paramref name="dy"/>).</summary>
    public GlyphOutline Translated(float dx, float dy)
    {
        if (IsEmpty || (dx == 0 && dy == 0))
        {
            return this;
        }

        var c = new float[Coords.Length];
        for (int i = 0; i + 1 < c.Length; i += 2)
        {
            c[i] = Coords[i] + dx;
            c[i + 1] = Coords[i + 1] + dy;
        }

        return new GlyphOutline(Verbs, c);
    }

    /// <summary>Concatenates the figures of two outlines.</summary>
    public static GlyphOutline Combine(GlyphOutline a, GlyphOutline b)
    {
        if (a.IsEmpty)
        {
            return b;
        }

        if (b.IsEmpty)
        {
            return a;
        }

        if ((long)a.Verbs.Length + b.Verbs.Length > FontLimits.MaxOutlineVerbs)
        {
            throw new FontProgramException("A composed glyph has too many segments.");
        }

        return new GlyphOutline([.. a.Verbs, .. b.Verbs], [.. a.Coords, .. b.Coords]);
    }
}

/// <summary>Collects outline segments with a hard cap, so a hostile font cannot grow it without bound.</summary>
internal sealed class OutlineBuilder
{
    private byte[] _verbs = new byte[32];
    private float[] _coords = new float[96];
    private int _v;
    private int _c;
    private bool _open;

    public bool IsOpen => _open;

    public void MoveTo(float x, float y)
    {
        if (_open)
        {
            Close();
        }

        Add(GlyphOutline.Move, 2);
        _coords[_c++] = x;
        _coords[_c++] = y;
        _open = true;
    }

    public void LineTo(float x, float y)
    {
        Add(GlyphOutline.Line, 2);
        _coords[_c++] = x;
        _coords[_c++] = y;
    }

    public void QuadTo(float cx, float cy, float x, float y)
    {
        Add(GlyphOutline.Quad, 4);
        _coords[_c++] = cx;
        _coords[_c++] = cy;
        _coords[_c++] = x;
        _coords[_c++] = y;
    }

    public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
    {
        Add(GlyphOutline.Cubic, 6);
        _coords[_c++] = c1x;
        _coords[_c++] = c1y;
        _coords[_c++] = c2x;
        _coords[_c++] = c2y;
        _coords[_c++] = x;
        _coords[_c++] = y;
    }

    public void Close()
    {
        if (_open)
        {
            Add(GlyphOutline.Close, 0);
            _open = false;
        }
    }

    public GlyphOutline Build()
    {
        Close();
        for (int i = 0; i < _c; i++)
        {
            // Arithmetic in a charstring can overflow; refuse the glyph rather than hand infinities to a device.
            if (!float.IsFinite(_coords[i]) || MathF.Abs(_coords[i]) > 1e7f)
            {
                throw new FontProgramException("A glyph outline has coordinates out of range.");
            }
        }

        if (_v == 0)
        {
            return GlyphOutline.Empty;
        }

        return new GlyphOutline(_verbs.AsSpan(0, _v).ToArray(), _coords.AsSpan(0, _c).ToArray());
    }

    private void Add(byte verb, int coords)
    {
        if (_v >= FontLimits.MaxOutlineVerbs)
        {
            throw new FontProgramException("A glyph has too many segments.");
        }

        if (_v == _verbs.Length)
        {
            Array.Resize(ref _verbs, _verbs.Length * 2);
        }

        if (_c + coords > _coords.Length)
        {
            Array.Resize(ref _coords, Math.Max(_coords.Length * 2, _c + coords));
        }

        _verbs[_v++] = verb;
    }
}
