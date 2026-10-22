using OnePdf.Content;
using OnePdf.Functions;

namespace OnePdf;

/// <summary>
/// A shading (ISO 32000-2 8.7.4.5): a colour that varies over an area, resolved and decoded for a device. Created by the
/// interpreter for the <c>sh</c> operator and for shading patterns, or by <see cref="Create"/>.
/// </summary>
/// <remarks>
/// <para><b>Space.</b> Geometry (<see cref="Coords"/>, <see cref="Matrix"/>, <see cref="BBox"/>, mesh vertices) is in the
/// shading's target space: the user space of the <c>sh</c> call, or the pattern space of a shading pattern. The device
/// state or pattern that delivers the shading carries the matrix to the output.</para>
/// <para><b>Evaluation.</b> <see cref="Evaluate"/> turns a parameter (types 1 to 3: the function input; types 4 to 7: the
/// interpolated vertex parameter) into a colour in <see cref="FinalSpace"/>. <see cref="EvaluateAt"/> does the whole job for
/// types 1 to 3 at a point of the target space. Both are deterministic, allocate nothing and are safe to call from any
/// number of threads at once.</para>
/// <para><b>Meshes.</b> Types 4 to 7 expose <see cref="Mesh"/>: triangles with a parameter per vertex. Interpolate the
/// parameters across each triangle and call <see cref="Evaluate"/> per pixel (or per vertex when the shading has no
/// function and the space is a device space). Patch meshes are also tessellated into triangles
/// (see <see cref="PdfShadingMesh"/>).</para>
/// </remarks>
public sealed class PdfShading
{
    private const int MaxComponents = 32;

    private readonly PdfFunction? _function;
    private readonly double[] _domain;
    private readonly double[] _coords;
    private readonly bool[] _extend;
    private readonly double[]? _background;
    private readonly PdfMatrix _inverse;
    private readonly bool _invertible;

    internal PdfShading(
        PdfDictionary dictionary, int type, PdfColorSpace space, PdfFunction? function, double[] domain, double[] coords, bool[] extend,
        PdfMatrix matrix, double[]? background, PdfBounds? bbox, bool antiAlias, PdfShadingMesh? mesh)
    {
        Dictionary = dictionary;
        ShadingType = type;
        ColorSpace = space;
        _function = function;
        _domain = domain;
        _coords = coords;
        _extend = extend;
        Matrix = matrix;
        _background = background;
        BBox = bbox;
        AntiAlias = antiAlias;
        Mesh = mesh;
        double det = matrix.Determinant;
        _invertible = det != 0 && double.IsFinite(det);
        if (_invertible)
        {
            double a = matrix.D / det, b = -matrix.B / det, c = -matrix.C / det, d = matrix.A / det;
            _inverse = new PdfMatrix(a, b, c, d, -((matrix.E * a) + (matrix.F * c)), -((matrix.E * b) + (matrix.F * d)));
        }

        Domain = Array.AsReadOnly(domain);
        Coords = Array.AsReadOnly(coords);
    }

    /// <summary>The shading dictionary (the stream's dictionary for types 4 to 7).</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The shading type, 1 to 7: function-based, axial, radial, free-form mesh, lattice mesh, Coons patches, tensor patches.</summary>
    public int ShadingType { get; }

    /// <summary>The shading's colour space as the file gives it (never Pattern).</summary>
    public PdfColorSpace ColorSpace { get; }

    /// <summary>The final space of the colours <see cref="Evaluate"/> returns.</summary>
    public PdfColorSpace FinalSpace => ColorSpace.FinalSpace;

    /// <summary>Number of values <see cref="Evaluate"/> writes: the component count of <see cref="FinalSpace"/>.</summary>
    public int ColorComponentCount => ColorSpace.FinalComponentCount;

    /// <summary>
    /// How many parameters <see cref="Evaluate"/> reads: 2 for type 1 (x and y in the function's domain), 1 for types 2, 3
    /// and for meshes with a function (a single parametric value per vertex), else the component count of
    /// <see cref="ColorSpace"/> (mesh vertices carry colour components).
    /// </summary>
    public int ParameterCount => ShadingType == 1 ? 2 : _function is not null ? 1 : ColorSpace.ComponentCount;

    /// <summary>True when colours come from a function; false only for meshes whose vertices carry colour components.</summary>
    public bool HasFunction => _function is not null;

    /// <summary>
    /// Type 1: <c>x0 x1 y0 y1</c> (default 0 1 0 1). Types 2 and 3: <c>t0 t1</c> (default 0 1). Types 4 to 7: empty.
    /// </summary>
    public IReadOnlyList<double> Domain { get; }

    /// <summary>Axial: <c>x0 y0 x1 y1</c>. Radial: <c>x0 y0 r0 x1 y1 r1</c>. Other types: empty.</summary>
    public IReadOnlyList<double> Coords { get; }

    /// <summary>Axial and radial: whether the shading continues past the start (t0) as that colour. False otherwise.</summary>
    public bool ExtendStart => _extend[0];

    /// <summary>Axial and radial: whether the shading continues past the end (t1) as that colour. False otherwise.</summary>
    public bool ExtendEnd => _extend[1];

    /// <summary>Type 1: maps the domain rectangle into the target space (identity when absent and for the other types).</summary>
    public PdfMatrix Matrix { get; }

    /// <summary>
    /// The bounding box in the target space, or null. Nothing outside it is painted. For a shading pattern this clips the
    /// pattern's area; for <c>sh</c> it clips the painted region too.
    /// </summary>
    public PdfBounds? BBox { get; }

    /// <summary>The <c>/AntiAlias</c> flag (a hint a device may ignore). Default false.</summary>
    public bool AntiAlias { get; }

    /// <summary>True when the dictionary has a <c>/Background</c> colour.</summary>
    public bool HasBackground => _background is not null;

    /// <summary>
    /// The decoded mesh of types 4 to 7, or null for types 1 to 3.
    /// </summary>
    public PdfShadingMesh? Mesh { get; }

    /// <summary>
    /// Writes the <c>/Background</c> colour converted to <see cref="FinalSpace"/>. The background paints the area of a
    /// shading used as a <em>pattern</em> that the shading itself leaves uncovered; the <c>sh</c> operator ignores it
    /// (ISO 32000-2 8.7.4.3 Table 78).
    /// </summary>
    /// <param name="color">At least <see cref="ColorComponentCount"/> values.</param>
    /// <returns>False when there is no background.</returns>
    public bool TryGetBackground(Span<double> color)
    {
        if (_background is null)
        {
            return false;
        }

        ColorSpace.ToFinal(_background, color);
        return true;
    }

    /// <summary>
    /// Maps parameters to a colour in <see cref="FinalSpace"/>: runs the shading's function (inputs clipped to its domain,
    /// outputs to the space's range) or, without one, takes the parameters as components of <see cref="ColorSpace"/>, and
    /// converts Indexed, Separation and DeviceN colours.
    /// </summary>
    /// <param name="parameters"><see cref="ParameterCount"/> values; missing ones read as 0.</param>
    /// <param name="color">Receives <see cref="ColorComponentCount"/> values.</param>
    /// <exception cref="ArgumentException"><paramref name="color"/> is too short.</exception>
    public void Evaluate(ReadOnlySpan<double> parameters, Span<double> color)
    {
        if (color.Length < ColorComponentCount)
        {
            throw new ArgumentException("The span is shorter than ColorComponentCount.", nameof(color));
        }

        int n = ColorSpace.ComponentCount;
        if (_function is { } f)
        {
            Span<double> comps = stackalloc double[MaxComponents];
            f.Evaluate(parameters, comps[..f.OutputCount]);
            ColorSpace.ToFinal(comps[..n], color);
        }
        else
        {
            ColorSpace.ToFinal(parameters, color);
        }
    }

    /// <summary>
    /// Axial and radial shadings: the parameter <c>t</c> (in <see cref="Domain"/>) that colours the point
    /// (<paramref name="x"/>, <paramref name="y"/>) of the target space, honouring the extend flags.
    /// </summary>
    /// <returns>False when the shading paints nothing at that point (outside the unextended range, outside every circle, a
    /// degenerate axis) and for the other types.</returns>
    public bool TryGetParameter(double x, double y, out double t)
    {
        t = 0;
        if (ShadingType == 2)
        {
            return Axial(x, y, out t);
        }

        return ShadingType == 3 && Radial(x, y, out t);
    }

    /// <summary>
    /// Types 1 to 3: the colour at a point of the target space (type 1 also inside the inverse of <see cref="Matrix"/> only
    /// within its <see cref="Domain"/>).
    /// </summary>
    /// <param name="x">X in the target space.</param>
    /// <param name="y">Y in the target space.</param>
    /// <param name="color">Receives <see cref="ColorComponentCount"/> values; untouched when the result is false.</param>
    /// <returns>False when the shading paints nothing at that point (then the background, if any, shows) and for types 4 to 7.</returns>
    public bool EvaluateAt(double x, double y, Span<double> color)
    {
        if (ShadingType == 1)
        {
            if (!_invertible)
            {
                return false;
            }

            (double u, double v) = _inverse.Transform(x, y);
            if (!(u >= _domain[0] && u <= _domain[1] && v >= _domain[2] && v <= _domain[3]))
            {
                return false;
            }

            Span<double> p = stackalloc double[2];
            p[0] = u;
            p[1] = v;
            Evaluate(p, color);
            return true;
        }

        if (!TryGetParameter(x, y, out double t))
        {
            return false;
        }

        Span<double> one = stackalloc double[1];
        one[0] = t;
        Evaluate(one, color);
        return true;
    }

    private bool Axial(double x, double y, out double t)
    {
        t = 0;
        double x0 = _coords[0], y0 = _coords[1];
        double dx = _coords[2] - x0, dy = _coords[3] - y0;
        double len2 = (dx * dx) + (dy * dy);
        if (!(len2 > 0))
        {
            return false;
        }

        // ISO 32000-2 8.7.4.5.3: s is the position of the point's projection on the axis, 0 at the start, 1 at the end.
        double s = (((x - x0) * dx) + ((y - y0) * dy)) / len2;
        if (s < 0)
        {
            if (!_extend[0])
            {
                return false;
            }

            s = 0;
        }
        else if (s > 1)
        {
            if (!_extend[1])
            {
                return false;
            }

            s = 1;
        }

        t = _domain[0] + (s * (_domain[1] - _domain[0]));
        return true;
    }

    private bool Radial(double x, double y, out double t)
    {
        t = 0;
        double x0 = _coords[0], y0 = _coords[1], r0 = _coords[2];
        double cdx = _coords[3] - x0, cdy = _coords[4] - y0, dr = _coords[5] - r0;
        double pdx = x - x0, pdy = y - y0;

        // The circle at parameter s has centre c0 + s (c1 - c0) and radius r0 + s (r1 - r0). A point lies on it when
        // a s^2 - 2 b s + c = 0 (ISO 32000-2 8.7.4.5.4); the larger valid s is painted last and so wins.
        double a = (cdx * cdx) + (cdy * cdy) - (dr * dr);
        double b = (pdx * cdx) + (pdy * cdy) + (r0 * dr);
        double c = (pdx * pdx) + (pdy * pdy) - (r0 * r0);
        double scale = (cdx * cdx) + (cdy * cdy) + (dr * dr);
        if (!(scale > 0))
        {
            return false;
        }

        if (Math.Abs(a) <= 1e-12 * scale)
        {
            // The circles' radii change as fast as their centres move: one root.
            if (b == 0)
            {
                return false;
            }

            return TryRadialRoot(c / (2 * b), r0, dr, out t);
        }

        double disc = (b * b) - (a * c);
        if (disc < 0)
        {
            return false;
        }

        double root = Math.Sqrt(disc);
        double s1 = (b + root) / a;
        double s2 = (b - root) / a;
        double hi = Math.Max(s1, s2);
        double lo = Math.Min(s1, s2);
        return TryRadialRoot(hi, r0, dr, out t) || TryRadialRoot(lo, r0, dr, out t);
    }

    private bool TryRadialRoot(double s, double r0, double dr, out double t)
    {
        t = 0;
        if (!double.IsFinite(s) || r0 + (s * dr) < 0)
        {
            return false;
        }

        if (s < 0)
        {
            if (!_extend[0])
            {
                return false;
            }

            s = 0;
        }
        else if (s > 1)
        {
            if (!_extend[1])
            {
                return false;
            }

            s = 1;
        }

        t = _domain[0] + (s * (_domain[1] - _domain[0]));
        return true;
    }

    /// <summary>
    /// Builds a shading from a shading dictionary (types 1 to 3) or stream (types 4 to 7), as found in a <c>/Shading</c>
    /// resource or a shading pattern's <c>/Shading</c> entry.
    /// </summary>
    /// <param name="document">The document that owns the object.</param>
    /// <param name="shading">The dictionary or stream.</param>
    /// <param name="resources">Resources used to look up a named colour space, or null.</param>
    /// <returns>The shading, or null when it is unusable; the reason is recorded in the document's diagnostics.</returns>
    /// <exception cref="PdfLimitException">A mesh stream is larger than <see cref="PdfLimits.MaxDecodedStreamBytes"/>.</exception>
    public static PdfShading? Create(PdfDocument document, PdfObject shading, PdfDictionary? resources = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(shading);
        PdfShading? s = Load(document, shading, resources, null, default, out string? problem);
        if (s is null)
        {
            document.ReportContent(PdfSeverity.Warning, problem ?? "A shading is unusable.", shading.Reference?.Number ?? -1);
        }

        return s;
    }

    internal static PdfShading? Load(PdfDocument doc, PdfObject obj, PdfDictionary? resources, ColorSpaceCache? spaces, CancellationToken ct, out string? problem)
    {
        PdfDictionary? d = obj switch
        {
            PdfStream st => st.Dictionary,
            PdfDictionary dd => dd,
            _ => null,
        };
        if (d is null)
        {
            problem = "A shading must be a dictionary or a stream.";
            return null;
        }

        int type = (int)d.GetInt("ShadingType", 0);
        if (type is < 1 or > 7)
        {
            problem = "A shading has a missing or unknown /ShadingType.";
            return null;
        }

        if ((type >= 4) != (obj is PdfStream))
        {
            problem = type >= 4 ? "A mesh shading (types 4 to 7) must be a stream." : "A shading of type 1 to 3 must be a dictionary.";
            return null;
        }

        PdfObject? csObj = d.Get("ColorSpace");
        PdfColorSpace? cs = spaces is not null ? spaces.Resolve(csObj, resources) : csObj is null ? null : PdfColorSpace.Resolve(doc, csObj, resources);
        if (cs is null || cs.Kind == PdfColorSpaceKind.Pattern)
        {
            problem = "A shading has a missing, unusable or Pattern /ColorSpace.";
            return null;
        }

        int n = cs.ComponentCount;
        if (n < 1 || n > MaxComponents)
        {
            problem = "A shading's colour space has an unsupported number of components.";
            return null;
        }

        double[]? background = null;
        if (PdfFunction.ReadNumbers(d, "Background") is { } bg && bg.Length >= n)
        {
            background = bg[..n];
        }

        PdfBounds? bbox = ContentInterpreter.ReadRect(d.GetArray("BBox"));
        bool antiAlias = d.GetBool("AntiAlias");

        PdfObject? fnObj = d.Get("Function");
        PdfFunction? fn = null;
        if (fnObj is not null)
        {
            fn = FunctionSet.Create(fnObj, n, doc.Limits, out string? fnProblem);
            if (fn is null)
            {
                problem = fnProblem ?? "A shading's /Function is unusable.";
                return null;
            }

            if (fn.OutputCount > MaxComponents)
            {
                problem = "A shading's function returns too many values.";
                return null;
            }

            if (cs.Kind == PdfColorSpaceKind.Indexed)
            {
                problem = "A shading in an Indexed colour space must not have a /Function.";
                return null;
            }
        }
        else if (type <= 3)
        {
            problem = "A shading of type 1 to 3 needs a /Function.";
            return null;
        }

        double[] noNumbers = [];
        bool[] noExtend = [false, false];
        switch (type)
        {
            case 1:
            {
                double[] dom = ReadFinite(d, "Domain", 4) ?? [0, 1, 0, 1];
                dom = [Math.Min(dom[0], dom[1]), Math.Max(dom[0], dom[1]), Math.Min(dom[2], dom[3]), Math.Max(dom[2], dom[3])];
                PdfMatrix m = ContentInterpreter.ReadMatrix(d.GetArray("Matrix"));
                if (fn!.InputCount != 2)
                {
                    problem = "A type 1 shading's function must take two inputs.";
                    return null;
                }

                problem = null;
                return new PdfShading(d, 1, cs, fn, dom, noNumbers, noExtend, m, background, bbox, antiAlias, null);
            }

            case 2 or 3:
            {
                int need = type == 2 ? 4 : 6;
                double[]? coords = ReadFinite(d, "Coords", need);
                if (coords is null)
                {
                    problem = $"A type {type} shading needs {need} finite numbers in /Coords.";
                    return null;
                }

                if (type == 3 && (coords[2] < 0 || coords[5] < 0))
                {
                    problem = "A radial shading has a negative radius.";
                    return null;
                }

                double[] dom = ReadFinite(d, "Domain", 2) ?? [0, 1];
                if (fn!.InputCount != 1)
                {
                    problem = "A shading's function must take one input.";
                    return null;
                }

                bool[] ext = [false, false];
                if (d.GetArray("Extend") is { Count: >= 2 } ea)
                {
                    ext[0] = ea[0] is PdfBoolean { Value: true };
                    ext[1] = ea[1] is PdfBoolean { Value: true };
                }

                problem = null;
                return new PdfShading(d, type, cs, fn, dom, coords, ext, PdfMatrix.Identity, background, bbox, antiAlias, null);
            }

            default:
            {
                var stream = (PdfStream)obj;
                ReadOnlyMemory<byte> data = stream.GetDecodedBytes(ct);
                PdfShadingMesh? mesh = MeshDecoder.Decode(d, type, fn is not null ? 1 : n, data.Span, out problem);
                if (mesh is null)
                {
                    return null;
                }

                return new PdfShading(d, type, cs, fn, noNumbers, noNumbers, noExtend, PdfMatrix.Identity, background, bbox, antiAlias, mesh);
            }
        }
    }

    private static double[]? ReadFinite(PdfDictionary d, string key, int count)
    {
        double[]? a = PdfFunction.ReadNumbers(d, key);
        if (a is null || a.Length < count)
        {
            return null;
        }

        for (int i = 0; i < count; i++)
        {
            if (!double.IsFinite(a[i]))
            {
                return null;
            }
        }

        return a.Length == count ? a : a[..count];
    }
}
