using System;
using System.IO;
using System.Text;
using BenchmarkDotNet.Attributes;

namespace OnePdf.Benchmarks;

/// <summary>
/// One million shading evaluations per type: the point-to-colour evaluators of types 1 to 3 and the parameter-to-colour
/// evaluator behind the mesh types 4 to 7. All must allocate nothing.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ShadingBenchmarks
{
    private const int N = 1_000_000;
    private readonly PdfShading?[] _sh = new PdfShading?[8];
    private PdfDocument? _doc;

    [GlobalSetup]
    public void Setup()
    {
        const string fn = "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> ";
        const string mesh = "/BitsPerCoordinate 8 /BitsPerComponent 8 /BitsPerFlag 8 /Decode [0 100 0 100 0 1] ";
        byte[] tri = [0, 0, 0, 0, 0, 255, 0, 0, 255, 0, 0, 0, 0, 255, 255, 255, 128, 64];
        byte[] coons = new byte[1 + 24 + 4];
        for (int i = 0; i < coons.Length; i++)
        {
            coons[i] = (byte)(i * 9);
        }

        coons[0] = 0;
        byte[] tensor = new byte[1 + 32 + 4];
        for (int i = 0; i < tensor.Length; i++)
        {
            tensor[i] = (byte)(i * 7);
        }

        tensor[0] = 0;
        byte[] lattice = new byte[20];
        for (int i = 0; i < lattice.Length; i++)
        {
            lattice[i] = (byte)(i * 13);
        }
        (string Dict, byte[]? Data)[] objects =
        [
            ("<< /Type /Catalog /Pages 2 0 R >>", null),
            ("<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            ("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /Shading << /S1 5 0 R /S2 6 0 R /S3 7 0 R /S4 8 0 R /S5 9 0 R /S6 10 0 R /S7 11 0 R >> >> >>", null),
            ("", Encoding.ASCII.GetBytes("/S1 sh /S2 sh /S3 sh /S4 sh /S5 sh /S6 sh /S7 sh")),
            ("<< /ShadingType 1 /ColorSpace /DeviceRGB /Domain [0 1 0 1] /Matrix [100 0 0 100 0 0] /Function 12 0 R >>", null),
            ("<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 100 0] /Extend [true true] " + fn + ">>", null),
            ("<< /ShadingType 3 /ColorSpace /DeviceRGB /Coords [50 50 5 60 50 40] /Extend [true true] " + fn + ">>", null),
            ("<< /ShadingType 4 /ColorSpace /DeviceRGB " + mesh + "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >>", tri),
            ("<< /ShadingType 5 /ColorSpace /DeviceRGB /VerticesPerRow 2 " + mesh.Replace("0 1]", "0 1 0 1 0 1]", StringComparison.Ordinal) + ">>", lattice),
            ("<< /ShadingType 6 /ColorSpace /DeviceGray " + mesh + ">>", coons),
            ("<< /ShadingType 7 /ColorSpace /DeviceGray " + mesh + ">>", tensor),
            ("<< /FunctionType 4 /Domain [0 1 0 1] /Range [0 1 0 1 0 1] >>", Encoding.ASCII.GetBytes("{ 2 copy mul }")),
        ];
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.7\n");
        var offsets = new long[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = ms.Position;
            (string dict, byte[]? data) = objects[i];
            if (data is null)
            {
                W($"{i + 1} 0 obj\n{dict}\nendobj\n");
            }
            else
            {
                string d = dict.Length == 0 ? "<< " : dict[..^2];
                W($"{i + 1} 0 obj\n{d} /Length {data.Length} >>\nstream\n");
                ms.Write(data);
                W("\nendstream\nendobj\n");
            }
        }

        long xref = ms.Position;
        W($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (long o in offsets)
        {
            W($"{o:D10} 00000 n \n");
        }

        W($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        _doc = PdfDocument.Open(ms.ToArray());
        var collector = new Collector(_sh);
        _doc.Pages[0].Interpret(collector);
    }

    [GlobalCleanup]
    public void Cleanup() => _doc?.Dispose();

    private sealed class Collector(PdfShading?[] into) : PdfDevice
    {
        private int _n;

        public override void PaintShading(PdfShading shading, in PdfGraphicsState state) => into[++_n] = shading;
    }

    private double Points(int type)
    {
        PdfShading sh = _sh[type]!;
        Span<double> c = stackalloc double[4];
        double sum = 0;
        for (int i = 0; i < N; i++)
        {
            double x = (i & 1023) * 0.1, y = ((i >> 10) & 1023) * 0.1;
            if (sh.EvaluateAt(x, y, c))
            {
                sum += c[0];
            }
        }

        return sum;
    }

    private double Parameters(int type)
    {
        PdfShading sh = _sh[type]!;
        PdfShadingMesh m = sh.Mesh!;
        ReadOnlySpan<double> v = m.Vertices.Span;
        Span<double> c = stackalloc double[4];
        double sum = 0;
        int np = sh.ParameterCount;
        for (int i = 0; i < N; i++)
        {
            int vertex = i % m.VertexCount;
            sh.Evaluate(v.Slice((vertex * m.VertexStride) + 2, np), c);
            sum += c[0];
        }

        return sum;
    }

    /// <summary>1M points through a type 4 (PostScript) function.</summary>
    [Benchmark]
    public double Type1FunctionBased() => Points(1);

    /// <summary>1M points on an axial shading.</summary>
    [Benchmark]
    public double Type2Axial() => Points(2);

    /// <summary>1M points on a radial shading (quadratic solve per point).</summary>
    [Benchmark]
    public double Type3Radial() => Points(3);

    /// <summary>1M vertex parameters through the function of a free-form mesh.</summary>
    [Benchmark]
    public double Type4FreeForm() => Parameters(4);

    /// <summary>1M vertex colours of a lattice mesh without a function.</summary>
    [Benchmark]
    public double Type5Lattice() => Parameters(5);

    /// <summary>1M interpolated colours of a Coons patch mesh.</summary>
    [Benchmark]
    public double Type6Coons() => Parameters(6);

    /// <summary>1M interpolated colours of a tensor-product patch mesh.</summary>
    [Benchmark]
    public double Type7Tensor() => Parameters(7);
}
