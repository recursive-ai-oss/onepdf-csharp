namespace OnePdf.Layout.Svg.Dom;

/// <summary>
/// A path as move, line, cubic and close operations in user units. Quadratics and elliptical arcs are converted to cubics when they are
/// added, because PDF has nothing else.
/// </summary>
internal sealed class SvgPath
{
    public enum Op : byte { Move, Line, Cubic, Close }

    private readonly List<Op> _ops = [];
    private readonly List<double> _pts = [];
    private double _cx;
    private double _cy;
    private double _sx;
    private double _sy;

    public IReadOnlyList<Op> Ops => _ops;

    public IReadOnlyList<double> Points => _pts;

    public bool IsEmpty => _ops.Count == 0;

    public int Count => _ops.Count;

    public void MoveTo(double x, double y)
    {
        _ops.Add(Op.Move);
        _pts.Add(x);
        _pts.Add(y);
        _cx = _sx = x;
        _cy = _sy = y;
    }

    public void LineTo(double x, double y)
    {
        EnsureStart();
        _ops.Add(Op.Line);
        _pts.Add(x);
        _pts.Add(y);
        _cx = x;
        _cy = y;
    }

    public void CubicTo(double c1x, double c1y, double c2x, double c2y, double x, double y)
    {
        EnsureStart();
        _ops.Add(Op.Cubic);
        _pts.Add(c1x);
        _pts.Add(c1y);
        _pts.Add(c2x);
        _pts.Add(c2y);
        _pts.Add(x);
        _pts.Add(y);
        _cx = x;
        _cy = y;
    }

    public void QuadTo(double qx, double qy, double x, double y)
    {
        EnsureStart();
        // Degree elevation: the cubic's control points sit two thirds of the way from each end to the quadratic's.
        CubicTo(_cx + (2.0 / 3 * (qx - _cx)), _cy + (2.0 / 3 * (qy - _cy)), x + (2.0 / 3 * (qx - x)), y + (2.0 / 3 * (qy - y)), x, y);
    }

    public void Close()
    {
        if (_ops.Count == 0 || _ops[^1] == Op.Close)
        {
            return;
        }

        _ops.Add(Op.Close);
        _cx = _sx;
        _cy = _sy;
    }

    private void EnsureStart()
    {
        if (_ops.Count == 0)
        {
            MoveTo(0, 0);
        }
        else if (_ops[^1] == Op.Close)
        {
            // A segment after a close starts a new subpath at the point the close returned to (SVG 1.1 8.3.3).
            MoveTo(_cx, _cy);
        }
    }

    /// <summary>An elliptical arc by the endpoint parameterization (SVG 1.1 F.6.5), as up to four cubics per quarter turn rounded up.</summary>
    public void ArcTo(double rx, double ry, double rotationDegrees, bool large, bool sweep, double x, double y)
    {
        EnsureStart();
        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        if (_cx == x && _cy == y)
        {
            return;
        }

        if (rx < 1e-12 || ry < 1e-12)
        {
            LineTo(x, y);
            return;
        }

        double phi = rotationDegrees * Math.PI / 180;
        double cos = Math.Cos(phi);
        double sin = Math.Sin(phi);
        double hx = (_cx - x) / 2;
        double hy = (_cy - y) / 2;
        double x1 = (cos * hx) + (sin * hy);
        double y1 = (-sin * hx) + (cos * hy);
        double lambda = (x1 * x1 / (rx * rx)) + (y1 * y1 / (ry * ry));
        if (lambda > 1)
        {
            double s = Math.Sqrt(lambda);
            rx *= s;
            ry *= s;
        }

        double num = (rx * rx * ry * ry) - (rx * rx * y1 * y1) - (ry * ry * x1 * x1);
        double den = (rx * rx * y1 * y1) + (ry * ry * x1 * x1);
        double coef = den == 0 ? 0 : Math.Sqrt(Math.Max(0, num / den)) * (large == sweep ? -1 : 1);
        double cxp = coef * rx * y1 / ry;
        double cyp = -coef * ry * x1 / rx;
        double ccx = (cos * cxp) - (sin * cyp) + ((_cx + x) / 2);
        double ccy = (sin * cxp) + (cos * cyp) + ((_cy + y) / 2);
        double t1 = Math.Atan2((y1 - cyp) / ry, (x1 - cxp) / rx);
        double t2 = Math.Atan2((-y1 - cyp) / ry, (-x1 - cxp) / rx);
        double delta = t2 - t1;
        if (sweep && delta < 0)
        {
            delta += 2 * Math.PI;
        }
        else if (!sweep && delta > 0)
        {
            delta -= 2 * Math.PI;
        }

        int pieces = Math.Clamp((int)Math.Ceiling((Math.Abs(delta) / (Math.PI / 2)) - 1e-4), 1, 4);
        double step = delta / pieces;
        // The control-point distance for a circular arc of the given sweep; the ellipse is the circle scaled and turned.
        double k = 4.0 / 3 * Math.Tan(step / 4);
        double ta = t1;
        double px = _cx;
        double py = _cy;
        for (int i = 0; i < pieces; i++)
        {
            double tb = ta + step;
            double cosA = Math.Cos(ta);
            double sinA = Math.Sin(ta);
            double cosB = Math.Cos(tb);
            double sinB = Math.Sin(tb);
            double ex = i == pieces - 1 ? x : ccx + (cos * rx * cosB) - (sin * ry * sinB);
            double ey = i == pieces - 1 ? y : ccy + (sin * rx * cosB) + (cos * ry * sinB);
            // Derivative of the ellipse point at each end, scaled by k.
            double d1x = (-cos * rx * sinA) - (sin * ry * cosA);
            double d1y = (-sin * rx * sinA) + (cos * ry * cosA);
            double d2x = (-cos * rx * sinB) - (sin * ry * cosB);
            double d2y = (-sin * rx * sinB) + (cos * ry * cosB);
            CubicTo(px + (k * d1x), py + (k * d1y), ex - (k * d2x), ey - (k * d2y), ex, ey);
            px = ex;
            py = ey;
            ta = tb;
        }
    }

    /// <summary>The tight bounding box of the path after <paramref name="m"/>: curve extremes are found, not just control points. False for an empty path.</summary>
    public bool TryBounds(in Affine m, out double outX0, out double outY0, out double outX1, out double outY1)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        int p = 0;
        double cx = 0;
        double cy = 0;
        double sx = 0;
        double sy = 0;
        bool any = false;
        void Add(double x, double y)
        {
            x0 = Math.Min(x0, x);
            x1 = Math.Max(x1, x);
            y0 = Math.Min(y0, y);
            y1 = Math.Max(y1, y);
            any = true;
        }

        foreach (Op op in _ops)
        {
            switch (op)
            {
                case Op.Move:
                    (cx, cy) = m.Apply(_pts[p], _pts[p + 1]);
                    sx = cx;
                    sy = cy;
                    p += 2;
                    Add(cx, cy);
                    break;
                case Op.Line:
                    (cx, cy) = m.Apply(_pts[p], _pts[p + 1]);
                    p += 2;
                    Add(cx, cy);
                    break;
                case Op.Cubic:
                {
                    (double ax, double ay) = m.Apply(_pts[p], _pts[p + 1]);
                    (double bx, double by) = m.Apply(_pts[p + 2], _pts[p + 3]);
                    (double ex, double ey) = m.Apply(_pts[p + 4], _pts[p + 5]);
                    p += 6;
                    Add(ex, ey);
                    foreach (double t in Extremes(cx, ax, bx, ex))
                    {
                        Add(Cubic(cx, ax, bx, ex, t), Cubic(cy, ay, by, ey, t));
                    }

                    foreach (double t in Extremes(cy, ay, by, ey))
                    {
                        Add(Cubic(cx, ax, bx, ex, t), Cubic(cy, ay, by, ey, t));
                    }

                    cx = ex;
                    cy = ey;
                    break;
                }

                default:
                    cx = sx;
                    cy = sy;
                    break;
            }
        }

        outX0 = x0;
        outY0 = y0;
        outX1 = x1;
        outY1 = y1;
        return any;
    }

    private static double Cubic(double p0, double p1, double p2, double p3, double t)
    {
        double u = 1 - t;
        return (u * u * u * p0) + (3 * u * u * t * p1) + (3 * u * t * t * p2) + (t * t * t * p3);
    }

    // The parameters in (0,1) where the cubic's derivative along one axis is zero.
    private static List<double> Extremes(double p0, double p1, double p2, double p3)
    {
        var r = new List<double>(2);
        double a = -p0 + (3 * p1) - (3 * p2) + p3;
        double b = 2 * (p0 - (2 * p1) + p2);
        double c = p1 - p0;
        if (Math.Abs(a) < 1e-12)
        {
            if (Math.Abs(b) > 1e-12)
            {
                double t = -c / b;
                if (t > 0 && t < 1)
                {
                    r.Add(t);
                }
            }

            return r;
        }

        double disc = (b * b) - (4 * a * c);
        if (disc < 0)
        {
            return r;
        }

        double sq = Math.Sqrt(disc);
        foreach (double t in (double[])[(-b + sq) / (2 * a), (-b - sq) / (2 * a)])
        {
            if (t > 0 && t < 1)
            {
                r.Add(t);
            }
        }

        return r;
    }
}
