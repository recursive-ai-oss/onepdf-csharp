namespace OnePdf;

/// <summary>
/// An affine transform in the PDF convention: a point <c>(x, y)</c> maps to <c>(a·x + c·y + e, b·x + d·y + f)</c>.
/// Products are evaluated in a fixed order with plain multiplications and additions (never fused), so results are the
/// same on every platform.
/// </summary>
/// <param name="A">Scale x / first row, first column.</param>
/// <param name="B">Second column of the first row.</param>
/// <param name="C">First column of the second row.</param>
/// <param name="D">Scale y / second row, second column.</param>
/// <param name="E">Translation x.</param>
/// <param name="F">Translation y.</param>
public readonly record struct PdfMatrix(double A, double B, double C, double D, double E, double F)
{
    /// <summary>The identity transform.</summary>
    public static PdfMatrix Identity => new(1, 0, 0, 1, 0, 0);

    /// <summary>A translation.</summary>
    public static PdfMatrix Translation(double tx, double ty) => new(1, 0, 0, 1, tx, ty);

    /// <summary>
    /// The transform that applies this matrix first and <paramref name="next"/> second (the PDF product <c>this × next</c>).
    /// </summary>
    public PdfMatrix Then(in PdfMatrix next)
    {
        double a = (A * next.A) + (B * next.C);
        double b = (A * next.B) + (B * next.D);
        double c = (C * next.A) + (D * next.C);
        double d = (C * next.B) + (D * next.D);
        double e = (E * next.A) + (F * next.C) + next.E;
        double f = (E * next.B) + (F * next.D) + next.F;
        return new PdfMatrix(a, b, c, d, e, f);
    }

    /// <summary>Maps a point.</summary>
    public (double X, double Y) Transform(double x, double y) =>
        ((A * x) + (C * y) + E, (B * x) + (D * y) + F);

    /// <summary>The determinant of the linear part.</summary>
    public double Determinant => (A * D) - (B * C);

    /// <summary>True when every element is a finite number.</summary>
    public bool IsFinite => double.IsFinite(A) && double.IsFinite(B) && double.IsFinite(C) && double.IsFinite(D) && double.IsFinite(E) && double.IsFinite(F);

    /// <summary>
    /// The bounds of the image of <paramref name="r"/> under this matrix.
    /// </summary>
    public PdfBounds TransformBounds(in PdfBounds r)
    {
        (double x0, double y0) = Transform(r.Left, r.Bottom);
        (double x1, double y1) = Transform(r.Right, r.Bottom);
        (double x2, double y2) = Transform(r.Right, r.Top);
        (double x3, double y3) = Transform(r.Left, r.Top);
        return new PdfBounds(
            Math.Min(Math.Min(x0, x1), Math.Min(x2, x3)),
            Math.Min(Math.Min(y0, y1), Math.Min(y2, y3)),
            Math.Max(Math.Max(x0, x1), Math.Max(x2, x3)),
            Math.Max(Math.Max(y0, y1), Math.Max(y2, y3)));
    }
}

/// <summary>A rectangle with <c>Left &lt;= Right</c> and <c>Bottom &lt;= Top</c> in the coordinate system it is used in.</summary>
/// <param name="Left">Smaller x.</param>
/// <param name="Bottom">Smaller y.</param>
/// <param name="Right">Larger x.</param>
/// <param name="Top">Larger y.</param>
public readonly record struct PdfBounds(double Left, double Bottom, double Right, double Top)
{
    /// <summary>Width.</summary>
    public double Width => Right - Left;

    /// <summary>Height.</summary>
    public double Height => Top - Bottom;

    /// <summary>A rectangle that contains everything.</summary>
    public static PdfBounds Unbounded => new(double.NegativeInfinity, double.NegativeInfinity, double.PositiveInfinity, double.PositiveInfinity);

    /// <summary>True when the two rectangles overlap or touch.</summary>
    public bool Intersects(in PdfBounds other) =>
        Left <= other.Right && other.Left <= Right && Bottom <= other.Top && other.Bottom <= Top;

    /// <summary>True when every coordinate is a finite number.</summary>
    public bool IsFinite => double.IsFinite(Left) && double.IsFinite(Bottom) && double.IsFinite(Right) && double.IsFinite(Top);

    /// <summary>The rectangle grown by <paramref name="amount"/> on every side.</summary>
    public PdfBounds Inflate(double amount) => new(Left - amount, Bottom - amount, Right + amount, Top + amount);

    /// <summary>The smallest rectangle containing both.</summary>
    public PdfBounds Union(in PdfBounds o) =>
        new(Math.Min(Left, o.Left), Math.Min(Bottom, o.Bottom), Math.Max(Right, o.Right), Math.Max(Top, o.Top));

    /// <summary>Builds a rectangle from two corners given in any order.</summary>
    public static PdfBounds FromCorners(double x0, double y0, double x1, double y1) =>
        new(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
}

/// <summary>The kinds of path segment. Points per verb: Move 1, Line 1, Curve 3, Close 0.</summary>
public enum PdfPathVerb : byte
{
    /// <summary>Starts a subpath at one point.</summary>
    Move = 0,

    /// <summary>A straight segment to one point.</summary>
    Line = 1,

    /// <summary>A cubic Bézier to three points (two controls, then the end).</summary>
    Curve = 2,

    /// <summary>Closes the current subpath.</summary>
    Close = 3,
}

/// <summary>
/// A path in the user space of the call it arrives with (apply the call's CTM to reach the output). The memory a device
/// receives is only valid for the duration of the call; use <see cref="ToOwned"/> to keep it.
/// </summary>
public readonly struct PdfPath
{
    /// <summary>Creates a path over the given memory (not copied).</summary>
    public PdfPath(ReadOnlyMemory<byte> verbs, ReadOnlyMemory<double> points)
    {
        VerbBytes = verbs;
        Points = points;
    }

    // Paths built by the library over arrays it never writes to again (cached glyph outlines) say so, so a recording can
    // share them instead of copying them for every use.
    internal PdfPath(ReadOnlyMemory<byte> verbs, ReadOnlyMemory<double> points, bool immutable)
    {
        VerbBytes = verbs;
        Points = points;
        IsImmutable = immutable;
    }

    /// <summary>True when the memory belongs to the library and is never modified or reused, so it stays valid after the call.</summary>
    internal bool IsImmutable { get; }

    /// <summary>The verbs, one <see cref="PdfPathVerb"/> per byte.</summary>
    public ReadOnlyMemory<byte> VerbBytes { get; }

    /// <summary>The coordinates, x then y for each point, in verb order.</summary>
    public ReadOnlyMemory<double> Points { get; }

    /// <summary>True when the path has no segments.</summary>
    public bool IsEmpty => VerbBytes.IsEmpty;

    /// <summary>A copy that owns its memory (a path whose memory the library never changes is returned as it is).</summary>
    public PdfPath ToOwned() => IsImmutable ? this : new(VerbBytes.ToArray(), Points.ToArray());

    /// <summary>The bounds of all points, control points included (a conservative bound of the curve). Empty paths give a zero rectangle.</summary>
    public PdfBounds ControlBounds()
    {
        ReadOnlySpan<double> p = Points.Span;
        if (p.Length < 2)
        {
            return default;
        }

        double l = p[0], b = p[1], r = p[0], t = p[1];
        for (int i = 2; i + 1 < p.Length; i += 2)
        {
            l = Math.Min(l, p[i]);
            r = Math.Max(r, p[i]);
            b = Math.Min(b, p[i + 1]);
            t = Math.Max(t, p[i + 1]);
        }

        return new PdfBounds(l, b, r, t);
    }
}

/// <summary>Grows a path one segment at a time into reusable buffers.</summary>
internal sealed class PathBuilder
{
    /// <summary>Segments beyond this count are dropped; no real page needs more.</summary>
    public const int MaxSegments = 1 << 20;

    private byte[] _verbs = new byte[64];
    private double[] _pts = new double[256];
    private int _nv;
    private int _np;

    public bool IsEmpty => _nv == 0;

    public bool Overflowed { get; private set; }

    public bool HasCurrentPoint { get; private set; }

    public double CurX { get; private set; }

    public double CurY { get; private set; }

    private double _startX;
    private double _startY;

    public void Clear()
    {
        _nv = 0;
        _np = 0;
        HasCurrentPoint = false;
        Overflowed = false;
    }

    private bool Reserve(int points)
    {
        if (_nv >= MaxSegments)
        {
            Overflowed = true;
            return false;
        }

        if (_nv == _verbs.Length)
        {
            Array.Resize(ref _verbs, _verbs.Length * 2);
        }

        if (_np + (points * 2) > _pts.Length)
        {
            Array.Resize(ref _pts, Math.Max(_pts.Length * 2, _np + (points * 2)));
        }

        return true;
    }

    public void MoveTo(double x, double y)
    {
        if (!Reserve(1))
        {
            return;
        }

        _verbs[_nv++] = (byte)PdfPathVerb.Move;
        _pts[_np++] = x;
        _pts[_np++] = y;
        CurX = _startX = x;
        CurY = _startY = y;
        HasCurrentPoint = true;
    }

    public void LineTo(double x, double y)
    {
        if (!Reserve(1))
        {
            return;
        }

        _verbs[_nv++] = (byte)PdfPathVerb.Line;
        _pts[_np++] = x;
        _pts[_np++] = y;
        CurX = x;
        CurY = y;
    }

    public void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        if (!Reserve(3))
        {
            return;
        }

        _verbs[_nv++] = (byte)PdfPathVerb.Curve;
        _pts[_np++] = x1;
        _pts[_np++] = y1;
        _pts[_np++] = x2;
        _pts[_np++] = y2;
        _pts[_np++] = x3;
        _pts[_np++] = y3;
        CurX = x3;
        CurY = y3;
    }

    public void Close()
    {
        if (!Reserve(0))
        {
            return;
        }

        _verbs[_nv++] = (byte)PdfPathVerb.Close;
        CurX = _startX;
        CurY = _startY;
    }

    /// <summary>A view of the path built so far; valid until the builder is changed.</summary>
    public PdfPath View() => new(_verbs.AsMemory(0, _nv), _pts.AsMemory(0, _np));
}
