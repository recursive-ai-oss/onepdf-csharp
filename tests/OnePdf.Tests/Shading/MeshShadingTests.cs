using static OnePdf.Tests.Shading.ShadingKit;

namespace OnePdf.Tests.Shading;

public class MeshShadingTests
{
    private static BitWriter Vertex(BitWriter w, int flag, int x, int y, params int[] c)
    {
        w.Put(flag, 8).Put(x, 8).Put(y, 8);
        foreach (int v in c)
        {
            w.Put(v, 8);
        }

        return w;
    }

    private static double V(PdfShadingMesh m, int vertex, int field) => m.Vertices.Span[(vertex * m.VertexStride) + field];

    [Fact]
    public void FreeFormMeshSharesEdgesByFlag()
    {
        BitWriter w = new();
        Vertex(w, 0, 0, 0, 255, 0, 0);
        Vertex(w, 0, 255, 0, 0, 255, 0);
        Vertex(w, 0, 0, 255, 0, 0, 255);
        Vertex(w, 1, 255, 255, 255, 255, 255); // (b, c, new) = (v1, v2, v3)
        Vertex(w, 2, 128, 128, 0, 0, 0);       // (a, c, new) of the last triangle = (v1, v3, v4)
        byte[] data = [.. w.ToArray(), 7, 9]; // two stray bytes: not a whole vertex

        using ShadingKit k = Sh(Mesh(4), data);
        PdfShading sh = k.Shading!;
        PdfShadingMesh m = sh.Mesh!;
        Assert.Equal(4, sh.ShadingType);
        Assert.Equal(5, m.VertexStride);
        Assert.Equal(5, m.VertexCount);
        Assert.Equal([0, 1, 2, 1, 2, 3, 1, 3, 4], m.Triangles.ToArray());
        Assert.Equal(100, V(m, 1, 0), 9);
        Assert.Equal(0, V(m, 1, 1), 9);
        Assert.Equal(128 / 255.0 * 100, V(m, 4, 0), 9);
        Assert.Equal(1, V(m, 3, 2), 9);
        Assert.Equal(3, sh.ParameterCount);
        Near([0, 1, 0], At(sh, V(m, 1, 2), V(m, 1, 3), V(m, 1, 4)));
        Assert.False(m.IsTruncated);
        Assert.Equal(0, m.PatchCount);
    }

    [Fact]
    public void AContinuationWithoutAFirstTriangleIsSkipped()
    {
        BitWriter w = new();
        Vertex(w, 1, 10, 10, 0, 0, 0);
        Vertex(w, 0, 0, 0, 255, 0, 0);
        Vertex(w, 0, 255, 0, 0, 255, 0);
        Vertex(w, 0, 0, 255, 0, 0, 255);
        using ShadingKit k = Sh(Mesh(4), w.ToArray());
        Assert.Equal([1, 2, 3], k.Shading!.Mesh!.Triangles.ToArray());
        Assert.Contains(k.Doc.Diagnostics, d => d.Message.Contains("invalid edge flag"));
    }

    [Fact]
    public void FlagsAndCoordinatesThatAreNotByteAligned()
    {
        // 2-bit flag, 12-bit coordinates, 4-bit components: 30 bits per vertex, padded to 4 bytes.
        BitWriter w = new();
        w.Put(0, 2).Put(0, 12).Put(0, 12).Put(15, 4).Pad();
        w.Put(0, 2).Put(4095, 12).Put(0, 12).Put(0, 4).Pad();
        w.Put(0, 2).Put(0, 12).Put(4095, 12).Put(8, 4).Pad();
        using ShadingKit k = Sh(Mesh(4, bpc: 12, bpk: 4, bpf: 2, cs: "/DeviceGray", comps: 1), w.ToArray());
        PdfShadingMesh m = k.Shading!.Mesh!;
        Assert.Equal(3, m.VertexCount);
        Assert.Equal(100, V(m, 1, 0), 9);
        Assert.Equal(100, V(m, 2, 1), 9);
        Assert.Equal(1, V(m, 0, 2), 9);
        Assert.Equal(8 / 15.0, V(m, 2, 2), 9);
    }

    [Fact]
    public void LatticeMeshMakesTwoTrianglesPerCell()
    {
        BitWriter w = new();
        for (int i = 0; i < 7; i++)
        {
            w.Put(i * 30, 8).Put(i * 10, 8).Put(i * 30, 8).Put(0, 8).Put(0, 8);
        }

        using ShadingKit k = Sh(Mesh(5, "/VerticesPerRow 3 "), w.ToArray());
        PdfShadingMesh m = k.Shading!.Mesh!;
        Assert.Equal(6, m.VertexCount); // the seventh vertex does not complete a row
        Assert.Equal([0, 1, 3, 1, 3, 4, 1, 2, 4, 2, 4, 5], m.Triangles.ToArray());
    }

    [Fact]
    public void LatticeNeedsVerticesPerRow()
    {
        using ShadingKit k = Sh(Mesh(5, "/VerticesPerRow 1 "), new byte[20]);
        Assert.Null(k.Shading);
        Assert.Contains(k.Doc.Diagnostics, d => d.Message.Contains("VerticesPerRow"));
    }

    private static BitWriter CoonsPatch(BitWriter w, int flag, IEnumerable<(int I, int J)> order, Func<int, int, (int X, int Y)> point, params int[] colors)
    {
        w.Put(flag, 8);
        foreach ((int i, int j) in order)
        {
            (int x, int y) = point(i, j);
            w.Put(x, 8).Put(y, 8);
        }

        foreach (int c in colors)
        {
            w.Put(c, 8);
        }

        return w;
    }

    private static readonly (int, int)[] StreamOrder =
    [
        (0, 0), (0, 1), (0, 2), (0, 3), (1, 3), (2, 3), (3, 3), (3, 2), (3, 1), (3, 0), (2, 0), (1, 0), (1, 1), (1, 2), (2, 2), (2, 1),
    ];

    [Fact]
    public void CoonsPatchIsTessellatedIntoTheGeometryTheDeviceReceives()
    {
        BitWriter w = new();
        CoonsPatch(w, 0, StreamOrder.Take(12), (i, j) => (85 * i, 85 * j), 0, 85, 255, 170);
        using ShadingKit k = Sh(Mesh(6, cs: "/DeviceGray", comps: 1), w.ToArray());
        PdfShading sh = k.Shading!;
        PdfShadingMesh m = sh.Mesh!;
        Assert.Equal(1, m.PatchCount);
        Assert.Equal(8, m.PatchDivisions);
        Assert.Equal(81, m.VertexCount);
        Assert.Equal(128, m.TriangleCount);
        Assert.Equal(3, m.VertexStride);
        Assert.Equal([0, 1, 9], m.Triangles.ToArray()[..3]);

        // Corners: (u, v) = (1, 0) is c30, (0, 1) is c03.
        Assert.Equal(100, V(m, 8, 0), 9);
        Assert.Equal(0, V(m, 8, 1), 9);
        Assert.Equal(170 / 255.0, V(m, 8, 2), 9);
        Assert.Equal(100, V(m, 72, 1), 9);
        Assert.Equal(85 / 255.0, V(m, 72, 2), 9);

        // The straight boundary gives interior points on the grid, so the centre of the surface is the centre of the square.
        Assert.Equal(50, V(m, 40, 0), 9);
        Assert.Equal(50, V(m, 40, 1), 9);
        Assert.Equal((0 + 85 + 255 + 170) / 4.0 / 255.0, V(m, 40, 2), 9);
        ReadOnlySpan<double> pp = m.PatchPoints.Span;
        Assert.Equal(85 / 255.0 * 100, pp[(((1 * 4) + 1) * 2)], 9);
        Assert.Equal(170 / 255.0 * 100, pp[(((2 * 4) + 2) * 2) + 1], 9);

        // Triangles are in painting order: v ascends, then u.
        int[] t = m.Triangles.ToArray();
        for (int i = 3; i < t.Length; i += 3)
        {
            Assert.True(t[i] >= t[i - 3]);
        }
    }

    [Fact]
    public void CoonsPatchesShareAnEdgeThroughTheFlag()
    {
        BitWriter w = new();
        CoonsPatch(w, 0, StreamOrder.Take(12), (i, j) => (85 * i, 85 * j), 0, 85, 255, 170);

        // f = 1: the new patch starts on the previous patch's p03..p33 edge; only eight new points and two colours follow.
        CoonsPatch(w, 1, StreamOrder.Skip(4).Take(8), (i, j) => (85 * j, 85 * (3 - i)), 100, 200);
        using ShadingKit k = Sh(Mesh(6, cs: "/DeviceGray", comps: 1), w.ToArray());
        PdfShadingMesh m = k.Shading!.Mesh!;
        Assert.Equal(2, m.PatchCount);
        Assert.Equal(162, m.VertexCount);
        ReadOnlySpan<double> pp = m.PatchPoints.Span;

        // p00..p03 of patch 2 = p03, p13, p23, p33 of patch 1.
        for (int j = 0; j < 4; j++)
        {
            Assert.Equal(pp[(((j * 4) + 3) * 2)], pp[32 + (j * 2)], 12);
            Assert.Equal(pp[(((j * 4) + 3) * 2) + 1], pp[32 + (j * 2) + 1], 12);
        }

        ReadOnlySpan<double> pc = m.PatchParameters.Span;
        Assert.Equal(85 / 255.0, pc[4], 9);   // c00 of patch 2 = c03 of patch 1
        Assert.Equal(1.0, pc[5], 9);          // c03 of patch 2 = c33 of patch 1
        Assert.Equal(100 / 255.0, pc[6], 9);
        Assert.Equal(200 / 255.0, pc[7], 9);

        // Patch 2's (u, v) = (0, 0) and (0, 1) vertices sit on the shared edge's ends.
        Assert.Equal(0, V(m, 81, 0), 9);
        Assert.Equal(100, V(m, 81, 1), 9);
        Assert.Equal(100, V(m, 81 + 72, 0), 9);
        Assert.Equal(100, V(m, 81 + 72, 1), 9);
    }

    [Fact]
    public void CoonsPatchWithoutAPreviousPatchIsSkipped()
    {
        BitWriter w = new();
        CoonsPatch(w, 2, StreamOrder.Skip(4).Take(8), (i, j) => (85 * i, 85 * j), 1, 2);
        CoonsPatch(w, 0, StreamOrder.Take(12), (i, j) => (85 * i, 85 * j), 0, 85, 255, 170);
        using ShadingKit k = Sh(Mesh(6, cs: "/DeviceGray", comps: 1), w.ToArray());
        Assert.Equal(1, k.Shading!.Mesh!.PatchCount);
        Assert.Contains(k.Doc.Diagnostics, d => d.Message.Contains("does not exist"));
    }

    [Fact]
    public void TensorPatchInteriorPointsMoveTheSurface()
    {
        BitWriter w = new();
        w.Put(0, 8);
        foreach ((int i, int j) in StreamOrder)
        {
            // p11 is pulled to the corner; the other points stay on the grid.
            (int x, int y) = i == 1 && j == 1 ? (0, 0) : (85 * i, 85 * j);
            w.Put(x, 8).Put(y, 8);
        }

        foreach (int c in new[] { 0, 85, 255, 170 })
        {
            w.Put(c, 8);
        }

        using ShadingKit k = Sh(Mesh(7, cs: "/DeviceGray", comps: 1), w.ToArray());
        PdfShadingMesh m = k.Shading!.Mesh!;
        Assert.Equal(1, m.PatchCount);

        // B1(1/2)^2 = 9/64 of the displacement (-100/3, -100/3) applies at the centre.
        Assert.Equal(50 - (100.0 / 3 * 9 / 64), V(m, 40, 0), 9);
        Assert.Equal(50 - (100.0 / 3 * 9 / 64), V(m, 40, 1), 9);
    }

    [Fact]
    public void ManyPatchesCoarsenTheTessellation()
    {
        BitWriter w = new();
        for (int p = 0; p < 10_000; p++)
        {
            CoonsPatch(w, 0, StreamOrder.Take(12), (i, j) => (85 * i, 85 * j), 0, 85, 255, 170);
        }

        using ShadingKit k = Sh(Mesh(6, cs: "/DeviceGray", comps: 1), w.ToArray());
        PdfShadingMesh m = k.Shading!.Mesh!;
        Assert.Equal(10_000, m.PatchCount);
        Assert.Equal(4, m.PatchDivisions);
        Assert.Equal(320_000, m.TriangleCount);
        Assert.Equal(250_000, m.VertexCount);
        Assert.False(m.IsTruncated);
    }

    [Fact]
    public void MeshesBeyondTheLimitAreTruncatedWithADiagnostic()
    {
        byte[] data = new byte[1_200_000];
        using ShadingKit k = Sh(Mesh(4, bpc: 1, bpk: 1, bpf: 2, cs: "/DeviceGray", comps: 1), data);
        PdfShadingMesh m = k.Shading!.Mesh!;
        Assert.True(m.IsTruncated);
        Assert.Equal(1 << 20, m.VertexCount);
        Assert.True(m.TriangleCount <= 1 << 20);
        Assert.Contains(k.Doc.Diagnostics, d => d.Message.Contains("limit"));
    }

    [Fact]
    public void MeshWithAFunctionCarriesOneParameterPerVertex()
    {
        BitWriter w = new();
        Vertex(w, 0, 0, 0, 0);
        Vertex(w, 0, 255, 0, 128);
        Vertex(w, 0, 0, 255, 255);
        using ShadingKit k = Sh(Mesh(4, RedToBlue, comps: 1), w.ToArray());
        PdfShading sh = k.Shading!;
        Assert.True(sh.HasFunction);
        Assert.Equal(1, sh.ParameterCount);
        Assert.Equal(3, sh.Mesh!.VertexStride);
        Near([0.75, 0, 0.25], At(sh, 0.25));
        Assert.Equal(128 / 255.0, V(sh.Mesh, 1, 2), 9);
    }

    [Fact]
    public void IndexedMeshColoursConvertThroughTheTable()
    {
        BitWriter w = new();
        Vertex(w, 0, 0, 0, 0);
        Vertex(w, 0, 255, 0, 255);
        Vertex(w, 0, 0, 255, 255);
        using ShadingKit k = Sh(Mesh(4, cs: "[/Indexed /DeviceRGB 1 <ff00000000ff>]", comps: 1), w.ToArray());
        PdfShading sh = k.Shading!;
        Near([1, 0, 0], At(sh, 0));
        Near([0, 0, 1], At(sh, 1));
    }

    [Fact]
    public void BadMeshDictionariesAreRejected()
    {
        byte[] data = new byte[64];
        (string Dict, string Expect)[] cases =
        [
            ("/ShadingType 4 /ColorSpace /DeviceRGB /BitsPerCoordinate 99 /BitsPerComponent 8 /BitsPerFlag 8 /Decode [0 1 0 1 0 1 0 1 0 1]", "BitsPer"),
            ("/ShadingType 4 /ColorSpace /DeviceRGB /BitsPerCoordinate 8 /BitsPerComponent 8 /BitsPerFlag 8", "Decode"),
            ("/ShadingType 4 /ColorSpace /DeviceRGB /BitsPerCoordinate 8 /BitsPerComponent 8 /BitsPerFlag 8 /Decode [0 1 0 1 0 1]", "Decode"),
            ("/ShadingType 6 /ColorSpace /DeviceRGB /BitsPerCoordinate 8 /BitsPerComponent 8 /BitsPerFlag 0 /Decode [0 1 0 1 0 1 0 1 0 1]", "BitsPer"),
        ];
        foreach ((string dict, string expect) in cases)
        {
            using ShadingKit k = Sh(dict, data);
            Assert.Null(k.Shading);
            Assert.Contains(k.Doc.Diagnostics, d => d.Message.Contains(expect));
        }
    }

    [Fact]
    public void ShadingsDecodeIdenticallyEveryTime()
    {
        BitWriter w = new();
        CoonsPatch(w, 0, StreamOrder.Take(12), (i, j) => ((i * 70) + j, 85 * j), 0, 85, 255, 170);
        byte[] data = w.ToArray();
        string a, b;
        using (ShadingKit k = Sh(Mesh(6, cs: "/DeviceGray", comps: 1), data))
        {
            a = k.List.ToText();
        }

        using (ShadingKit k = Sh(Mesh(6, cs: "/DeviceGray", comps: 1), data))
        {
            b = k.List.ToText();
        }

        Assert.Equal(a, b);
        Assert.Contains("shading type=6 cs=gray fn=0 bbox=none bg=none aa=0 vertices=81 triangles=128 patches=1 div=8 fnv=", a);
    }

    [Fact]
    public void TheShadingFuzzTargetSurvivesMutatedInputs()
    {
        var rnd = new Random(20261022);
        var seeds = OnePdf.Fuzz.ShadingTarget.Seeds().ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int runs = 0;
        while (sw.ElapsedMilliseconds < 4000)
        {
            byte[] m = (byte[])seeds[rnd.Next(seeds.Count)].Clone();
            for (int e = rnd.Next(1, 6); e > 0; e--)
            {
                if (rnd.Next(3) == 0)
                {
                    m[rnd.Next(m.Length)] ^= (byte)(1 << rnd.Next(8));
                }
                else
                {
                    m[rnd.Next(m.Length)] = (byte)rnd.Next(256);
                }
            }

            OnePdf.Fuzz.ShadingTarget.Run(m);
            runs++;
        }

        Assert.True(runs > 100);
    }
}
