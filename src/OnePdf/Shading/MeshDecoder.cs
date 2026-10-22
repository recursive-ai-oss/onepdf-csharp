namespace OnePdf;

/// <summary>
/// The decoded data of a mesh shading (types 4 to 7): vertices with a parameter each, and triangles over them.
/// Coordinates are in the shading's target space.
/// </summary>
/// <remarks>
/// <para>A vertex is <see cref="VertexStride"/> doubles: x, y, then the shading's parameters
/// (<see cref="PdfShading.ParameterCount"/> of them: one value that the shading's function maps to a colour, or the colour
/// components). Interpolate x, y and the parameters across a triangle, then call <see cref="PdfShading.Evaluate"/>.</para>
/// <para>For types 6 and 7 the patches are kept as data (<see cref="PatchPoints"/>, <see cref="PatchParameters"/>) and also
/// tessellated: each patch becomes a (<see cref="PatchDivisions"/> + 1)² grid of vertices evaluated on the patch surface
/// (the tensor-product Bézier of ISO 32000-2 8.7.4.5.8; Coons patches get the interior points the specification derives
/// from the boundary), with parameters interpolated bilinearly from the corners. Triangles are written in the painting order
/// the specification needs where a patch folds over itself: later triangles belong to larger v, then larger u, and a patch
/// later in the stream paints over an earlier one. <see cref="PatchDivisions"/> is 8 for meshes up to 8192 patches and drops to 4 or 2 for larger ones, so
/// that the tessellation stays bounded; a device that wants finer geometry can subdivide the patch data itself.</para>
/// <para>Limits: at most 2<sup>20</sup> triangles, and vertex storage of at most 2<sup>23</sup> doubles. Data beyond that is
/// dropped and <see cref="IsTruncated"/> is set (the interpreter also records a diagnostic).</para>
/// </remarks>
public sealed class PdfShadingMesh
{
    internal PdfShadingMesh(int stride, double[] vertices, int vertexCount, int[] triangles, int patchCount, int divisions, double[] patchPoints, double[] patchParameters, bool truncated)
    {
        VertexStride = stride;
        VertexCount = vertexCount;
        Vertices = vertices.AsMemory(0, vertexCount * stride);
        TriangleCount = triangles.Length / 3;
        Triangles = triangles;
        PatchCount = patchCount;
        PatchDivisions = divisions;
        PatchPoints = patchPoints;
        PatchParameters = patchParameters;
        IsTruncated = truncated;
    }

    /// <summary>Doubles per vertex: 2 plus the parameter count.</summary>
    public int VertexStride { get; }

    /// <summary>Number of vertices.</summary>
    public int VertexCount { get; }

    /// <summary>The vertices, <see cref="VertexStride"/> doubles each: x, y, parameters.</summary>
    public ReadOnlyMemory<double> Vertices { get; }

    /// <summary>Number of triangles.</summary>
    public int TriangleCount { get; }

    /// <summary>Vertex indices, three per triangle.</summary>
    public ReadOnlyMemory<int> Triangles { get; }

    /// <summary>Types 6 and 7: number of patches (0 for types 4 and 5).</summary>
    public int PatchCount { get; }

    /// <summary>Types 6 and 7: grid divisions per patch side used for the tessellation (0 for types 4 and 5).</summary>
    public int PatchDivisions { get; }

    /// <summary>
    /// Types 6 and 7: 32 doubles per patch, the 16 control points <c>p<sub>ij</sub></c> (i = column, j = row) in the order
    /// <c>p00 p01 p02 p03 p10 p11 … p33</c> as x, y pairs. Coons patches carry their derived interior points.
    /// </summary>
    public ReadOnlyMemory<double> PatchPoints { get; }

    /// <summary>
    /// Types 6 and 7: the parameters of each patch's four corners, in the order c00 c03 c33 c30, per patch
    /// (4 × <see cref="PdfShading.ParameterCount"/> doubles).
    /// </summary>
    public ReadOnlyMemory<double> PatchParameters { get; }

    /// <summary>True when the stream held more data than the limits allow and the rest was dropped.</summary>
    public bool IsTruncated { get; }
}

/// <summary>Decodes the data streams of shading types 4 to 7 (ISO 32000-2 8.7.4.5.5 to 8.7.4.5.8).</summary>
internal static class MeshDecoder
{
    internal const int MaxTriangles = 1 << 20;
    internal const int MaxVertexDoubles = 1 << 23;
    internal const int MaxPatches = 1 << 17;

    // Stream order of patch control points as (column i, row j), ISO 32000-2 Table 86; Coons patches use the first 12.
    private static readonly byte[] Order =
    [
        0, 0, 0, 1, 0, 2, 0, 3, 1, 3, 2, 3, 3, 3, 3, 2, 3, 1, 3, 0, 2, 0, 1, 0, 1, 1, 1, 2, 2, 2, 2, 1,
    ];

    private ref struct Bits
    {
        private readonly ReadOnlySpan<byte> _data;
        private long _pos;

        public Bits(ReadOnlySpan<byte> data)
        {
            _data = data;
            _pos = 0;
        }

        public readonly long Remaining => ((long)_data.Length * 8) - _pos;

        public void Align() => _pos = (_pos + 7) & ~7L;

        public uint Read(int bits)
        {
            ulong v = 0;
            int need = bits;
            while (need > 0)
            {
                int b = _data[(int)(_pos >> 3)];
                int off = (int)(_pos & 7);
                int avail = 8 - off;
                int take = Math.Min(avail, need);
                v = (v << take) | (uint)((b >> (avail - take)) & ((1 << take) - 1));
                _pos += take;
                need -= take;
            }

            return (uint)v;
        }
    }

    private readonly struct Format
    {
        public readonly int Flag, Coord, Comp, Np;
        public readonly double X0, XScale, Y0, YScale;
        public readonly double[] CMin, CScale;

        public Format(int flag, int coord, int comp, int np, double[] decode)
        {
            Flag = flag;
            Coord = coord;
            Comp = comp;
            Np = np;
            double cmax = coord == 32 ? 4294967295.0 : (double)((1L << coord) - 1);
            X0 = decode[0];
            XScale = (decode[1] - decode[0]) / cmax;
            Y0 = decode[2];
            YScale = (decode[3] - decode[2]) / cmax;
            double kmax = (double)((1L << comp) - 1);
            CMin = new double[np];
            CScale = new double[np];
            for (int i = 0; i < np; i++)
            {
                CMin[i] = decode[4 + (2 * i)];
                CScale[i] = (decode[5 + (2 * i)] - decode[4 + (2 * i)]) / kmax;
            }
        }

        public void ReadPoint(ref Bits r, scoped Span<double> xy)
        {
            xy[0] = X0 + (r.Read(Coord) * XScale);
            xy[1] = Y0 + (r.Read(Coord) * YScale);
        }

        public void ReadColor(ref Bits r, scoped Span<double> c)
        {
            for (int i = 0; i < Np; i++)
            {
                c[i] = CMin[i] + (r.Read(Comp) * CScale[i]);
            }
        }
    }

    /// <summary>
    /// Decodes the stream data. <paramref name="np"/> is the parameter count per vertex (1 with a function, else the colour
    /// components). Returns null with <paramref name="problem"/> set when the dictionary is unusable; a mesh that was cut
    /// short or truncated is returned with <paramref name="problem"/> describing it.
    /// </summary>
    public static PdfShadingMesh? Decode(PdfDictionary d, int type, int np, ReadOnlySpan<byte> data, out string? problem)
    {
        problem = null;
        int bpc = (int)d.GetInt("BitsPerCoordinate", 0);
        int bpk = (int)d.GetInt("BitsPerComponent", 0);
        int bpf = (int)d.GetInt("BitsPerFlag", 0);
        if (bpc is < 1 or > 32 || bpk is < 1 or > 16 || (type != 5 && bpf is < 1 or > 8))
        {
            problem = "A mesh shading has missing or unsupported /BitsPerCoordinate, /BitsPerComponent or /BitsPerFlag.";
            return null;
        }

        double[]? decode = Functions.PdfFunction.ReadNumbers(d, "Decode");
        if (decode is null || decode.Length < 4 + (2 * np))
        {
            problem = "A mesh shading's /Decode array is missing or too short.";
            return null;
        }

        for (int i = 0; i < 4 + (2 * np); i++)
        {
            if (!double.IsFinite(decode[i]))
            {
                problem = "A mesh shading's /Decode array has non-finite values.";
                return null;
            }
        }

        int stride = 2 + np;
        var fmt = new Format(type == 5 ? 0 : bpf, bpc, bpk, np, decode);
        switch (type)
        {
            case 4:
                return DecodeFreeForm(in fmt, stride, data, ref problem);
            case 5:
            {
                long vpr = d.GetInt("VerticesPerRow", 0);
                if (vpr < 2 || vpr > MaxTriangles)
                {
                    problem = "A lattice mesh shading needs /VerticesPerRow of at least 2.";
                    return null;
                }

                return DecodeLattice(in fmt, stride, (int)vpr, data, ref problem);
            }

            default:
                return DecodePatches(in fmt, stride, type == 7, data, ref problem);
        }
    }

    private static PdfShadingMesh DecodeFreeForm(in Format f, int stride, ReadOnlySpan<byte> data, ref string? problem)
    {
        int vertexBytes = (int)(((long)f.Flag + (2L * f.Coord) + ((long)f.Np * f.Comp) + 7) / 8);
        int maxV = (int)Math.Min(Math.Min(data.Length / vertexBytes, MaxTriangles), MaxVertexDoubles / stride);
        bool truncated = data.Length / vertexBytes > maxV;
        var verts = new double[maxV * stride];
        var tris = new List<int>();
        var r = new Bits(data);
        int count = 0;
        int ia = -1, ib = -1, ic = -1;
        bool badFlag = false;
        Span<double> v = stackalloc double[2 + f.Np];
        while (count < maxV && r.Remaining >= (long)vertexBytes * 8)
        {
            int flag = (int)(r.Read(f.Flag) & 3);
            f.ReadPoint(ref r, v);
            f.ReadColor(ref r, v[2..]);
            r.Align();
            v.CopyTo(verts.AsSpan(count * stride));
            int cur = count++;
            if (flag == 0)
            {
                // Two more vertices complete the triangle; their edge flags are ignored.
                if (count + 2 > maxV || r.Remaining < 2L * vertexBytes * 8)
                {
                    break;
                }

                for (int k = 0; k < 2; k++)
                {
                    _ = r.Read(f.Flag);
                    f.ReadPoint(ref r, v);
                    f.ReadColor(ref r, v[2..]);
                    r.Align();
                    v.CopyTo(verts.AsSpan(count * stride));
                    count++;
                }

                ia = cur;
                ib = cur + 1;
                ic = cur + 2;
            }
            else if (ia >= 0 && flag == 1)
            {
                (ia, ib, ic) = (ib, ic, cur);
            }
            else if (ia >= 0 && flag == 2)
            {
                (ib, ic) = (ic, cur);
            }
            else
            {
                badFlag = true;
                continue;
            }

            tris.Add(ia);
            tris.Add(ib);
            tris.Add(ic);
        }

        if (badFlag)
        {
            problem = "A mesh shading has vertices with an invalid edge flag or a continuation without a first triangle; they were skipped.";
        }

        if (truncated)
        {
            problem = "A mesh shading has more vertices than the limit allows; the rest was dropped.";
        }

        return new PdfShadingMesh(stride, verts, count, tris.ToArray(), 0, 0, [], [], truncated);
    }

    private static PdfShadingMesh DecodeLattice(in Format f, int stride, int perRow, ReadOnlySpan<byte> data, ref string? problem)
    {
        int vertexBytes = (int)(((2L * f.Coord) + ((long)f.Np * f.Comp) + 7) / 8);
        long available = data.Length / vertexBytes;
        long cap = Math.Min(MaxTriangles, MaxVertexDoubles / stride);
        bool truncated = available > cap;
        int total = (int)Math.Min(available, cap);
        long rows = total / perRow;
        // Two triangles per lattice cell; the triangle limit may bind before the vertex limit.
        while (rows > 1 && 2 * (rows - 1) * (perRow - 1) > MaxTriangles)
        {
            rows--;
            truncated = true;
        }

        int used = (int)(rows * perRow);
        var verts = new double[used * stride];
        var r = new Bits(data);
        Span<double> v = stackalloc double[2 + f.Np];
        for (int i = 0; i < used; i++)
        {
            f.ReadPoint(ref r, v);
            f.ReadColor(ref r, v[2..]);
            r.Align();
            v.CopyTo(verts.AsSpan(i * stride));
        }

        int[] tris = rows < 2 ? [] : new int[(int)(2 * (rows - 1) * (perRow - 1) * 3)];
        int t = 0;
        for (int i = 0; i + 1 < rows; i++)
        {
            for (int j = 0; j + 1 < perRow; j++)
            {
                int a = (i * perRow) + j, b = a + 1, c = a + perRow, e = c + 1;

                // ISO 32000-2 8.7.4.5.6: (V[i,j], V[i,j+1], V[i+1,j]) and (V[i,j+1], V[i+1,j], V[i+1,j+1]).
                tris[t++] = a;
                tris[t++] = b;
                tris[t++] = c;
                tris[t++] = b;
                tris[t++] = c;
                tris[t++] = e;
            }
        }

        if (truncated)
        {
            problem = "A mesh shading has more vertices than the limit allows; the rest was dropped.";
        }
        else if (rows < 2)
        {
            problem = "A lattice mesh shading has fewer than two complete rows; it paints nothing.";
        }

        return new PdfShadingMesh(stride, verts, used, tris, 0, 0, [], [], truncated);
    }

    private static PdfShadingMesh DecodePatches(in Format f, int stride, bool tensor, ReadOnlySpan<byte> data, ref string? problem)
    {
        int np = f.Np;
        int pointsNew = tensor ? 16 : 12;
        int pointsShared = pointsNew - 4;
        long bitsNew = f.Flag + (2L * f.Coord * pointsNew) + (4L * np * f.Comp);
        long bitsShared = f.Flag + (2L * f.Coord * pointsShared) + (2L * np * f.Comp);
        var pts = new List<double>();
        var pars = new List<double>();
        var r = new Bits(data);
        Span<double> cur = stackalloc double[32];
        Span<double> prev = stackalloc double[32];
        Span<double> curC = stackalloc double[4 * np];
        Span<double> prevC = stackalloc double[4 * np];
        Span<double> xy = stackalloc double[2];
        int patches = 0;
        int maxPatches = Math.Min(MaxPatches, MaxVertexDoubles / (32 + (4 * np)));
        bool havePrev = false, badFlag = false, truncated = false;
        while (r.Remaining >= Math.Min(bitsNew, bitsShared))
        {
            int flag = (int)(r.Read(f.Flag) & 3);
            bool shared = flag != 0;
            long need = (shared ? bitsShared : bitsNew) - f.Flag;
            if (r.Remaining < need)
            {
                break;
            }

            if (patches >= maxPatches)
            {
                truncated = true;
                break;
            }

            cur.Clear();
            curC.Clear();
            int first = 0;
            if (shared)
            {
                if (!havePrev)
                {
                    badFlag = true;
                }
                else
                {
                    // Implicit edge from the previous patch (ISO 32000-2 Tables 85 and 86): its edge becomes this patch's p00..p03.
                    // Edge points as (i, j) of the previous patch; colours are indices into [c00 c03 c33 c30].
                    ReadOnlySpan<byte> edge = flag switch
                    {
                        1 => [0, 3, 1, 3, 2, 3, 3, 3],
                        2 => [3, 3, 3, 2, 3, 1, 3, 0],
                        _ => [3, 0, 2, 0, 1, 0, 0, 0],
                    };
                    for (int k = 0; k < 4; k++)
                    {
                        int src = ((edge[2 * k] * 4) + edge[(2 * k) + 1]) * 2;
                        int dst = k * 2; // p0k is (i=0, j=k)
                        cur[dst] = prev[src];
                        cur[dst + 1] = prev[src + 1];
                    }

                    int c0 = flag switch { 1 => 1, 2 => 2, _ => 3 };
                    int c1 = flag switch { 1 => 2, 2 => 3, _ => 0 };
                    prevC.Slice(c0 * np, np).CopyTo(curC[..np]);
                    prevC.Slice(c1 * np, np).CopyTo(curC.Slice(np, np));
                }

                first = 4;
            }

            for (int k = first; k < pointsNew; k++)
            {
                f.ReadPoint(ref r, xy);
                int dst = ((Order[2 * k] * 4) + Order[(2 * k) + 1]) * 2;
                cur[dst] = xy[0];
                cur[dst + 1] = xy[1];
            }

            for (int k = shared ? 2 : 0; k < 4; k++)
            {
                f.ReadColor(ref r, curC.Slice(k * np, np));
            }

            r.Align();
            if (!tensor)
            {
                DeriveInterior(cur);
            }

            if (shared && !havePrev)
            {
                continue;
            }

            foreach (double x in cur)
            {
                pts.Add(x);
            }

            foreach (double x in curC)
            {
                pars.Add(x);
            }

            patches++;
            havePrev = true;
            cur.CopyTo(prev);
            curC.CopyTo(prevC);
        }

        // Choose the grid so that triangle and vertex storage stay bounded (see PdfShadingMesh).
        int div = 8;
        while (div > 1 && !Fits(patches, div, stride))
        {
            div >>= 1;
        }

        while (patches > 0 && !Fits(patches, div, stride))
        {
            patches--;
            truncated = true;
        }

        double[] patchPoints = new double[patches * 32];
        double[] patchPars = new double[patches * 4 * np];
        pts.CopyTo(0, patchPoints, 0, patchPoints.Length);
        pars.CopyTo(0, patchPars, 0, patchPars.Length);
        int side = div + 1;
        int perPatchV = side * side;
        var verts = new double[patches * perPatchV * stride];
        var tris = new int[patches * 2 * div * div * 3];
        Span<double> bu = stackalloc double[4];
        Span<double> bv = stackalloc double[4];
        int vi = 0, ti = 0;
        for (int p = 0; p < patches; p++)
        {
            ReadOnlySpan<double> pp = patchPoints.AsSpan(p * 32, 32);
            int baseVertex = vi / stride;
            for (int iv = 0; iv <= div; iv++)
            {
                Bernstein((double)iv / div, bv);
                double v = (double)iv / div;
                for (int iu = 0; iu <= div; iu++)
                {
                    double u = (double)iu / div;
                    Bernstein(u, bu);
                    double x = 0, y = 0;
                    for (int i = 0; i < 4; i++)
                    {
                        for (int j = 0; j < 4; j++)
                        {
                            double w = bu[i] * bv[j];
                            x += pp[((i * 4) + j) * 2] * w;
                            y += pp[(((i * 4) + j) * 2) + 1] * w;
                        }
                    }

                    verts[vi++] = x;
                    verts[vi++] = y;
                    for (int k = 0; k < np; k++)
                    {
                        // Corner order c00 c03 c33 c30 sits at (u,v) = (0,0) (0,1) (1,1) (1,0).
                        double c00 = patchPars[(p * 4 * np) + k], c03 = patchPars[(p * 4 * np) + np + k];
                        double c33 = patchPars[(p * 4 * np) + (2 * np) + k], c30 = patchPars[(p * 4 * np) + (3 * np) + k];
                        verts[vi++] = (((1 - u) * (1 - v) * c00) + ((1 - u) * v * c03)) + ((u * v * c33) + (u * (1 - v) * c30));
                    }
                }
            }

            for (int iv = 0; iv < div; iv++)
            {
                for (int iu = 0; iu < div; iu++)
                {
                    int a = baseVertex + (iv * side) + iu;
                    int b = a + 1, c = a + side, e = c + 1;
                    tris[ti++] = a;
                    tris[ti++] = b;
                    tris[ti++] = c;
                    tris[ti++] = b;
                    tris[ti++] = e;
                    tris[ti++] = c;
                }
            }
        }

        if (badFlag)
        {
            problem = "A patch mesh shading continues a patch that does not exist; those patches were skipped.";
        }

        if (truncated)
        {
            problem = "A patch mesh shading has more patches than the limits allow; the rest was dropped.";
        }

        return new PdfShadingMesh(stride, verts, vi / stride, tris, patches, div, patchPoints, patchPars, truncated);
    }

    private static bool Fits(int patches, int div, int stride)
    {
        long tri = (long)patches * 2 * div * div;
        long vert = (long)patches * (div + 1) * (div + 1) * stride;
        return tri <= MaxTriangles && vert <= MaxVertexDoubles;
    }

    private static void Bernstein(double t, Span<double> b)
    {
        double m = 1 - t;
        b[0] = m * m * m;
        b[1] = 3 * t * (m * m);
        b[2] = 3 * (t * t) * m;
        b[3] = t * t * t;
    }

    /// <summary>The four interior control points of a Coons patch, from the boundary (ISO 32000-2 8.7.4.5.8).</summary>
    private static void DeriveInterior(Span<double> p)
    {
        for (int c = 0; c < 2; c++)
        {
            double p11 = ((-4 * At(p, 0, 0, c)) + (6 * (At(p, 0, 1, c) + At(p, 1, 0, c))) - (2 * (At(p, 0, 3, c) + At(p, 3, 0, c))) + (3 * (At(p, 3, 1, c) + At(p, 1, 3, c))) - At(p, 3, 3, c)) / 9;
            double p12 = ((-4 * At(p, 0, 3, c)) + (6 * (At(p, 0, 2, c) + At(p, 1, 3, c))) - (2 * (At(p, 0, 0, c) + At(p, 3, 3, c))) + (3 * (At(p, 3, 2, c) + At(p, 1, 0, c))) - At(p, 3, 0, c)) / 9;
            double p21 = ((-4 * At(p, 3, 0, c)) + (6 * (At(p, 3, 1, c) + At(p, 2, 0, c))) - (2 * (At(p, 3, 3, c) + At(p, 0, 0, c))) + (3 * (At(p, 0, 1, c) + At(p, 2, 3, c))) - At(p, 0, 3, c)) / 9;
            double p22 = ((-4 * At(p, 3, 3, c)) + (6 * (At(p, 3, 2, c) + At(p, 2, 3, c))) - (2 * (At(p, 3, 0, c) + At(p, 0, 3, c))) + (3 * (At(p, 0, 2, c) + At(p, 2, 0, c))) - At(p, 0, 0, c)) / 9;
            p[(((1 * 4) + 1) * 2) + c] = p11;
            p[(((1 * 4) + 2) * 2) + c] = p12;
            p[(((2 * 4) + 1) * 2) + c] = p21;
            p[(((2 * 4) + 2) * 2) + c] = p22;
        }
    }

    private static double At(ReadOnlySpan<double> p, int i, int j, int c) => p[(((i * 4) + j) * 2) + c];
}
