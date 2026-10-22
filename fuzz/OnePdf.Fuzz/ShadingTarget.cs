using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OnePdf;

namespace OnePdf.Fuzz;

/// <summary>
/// Fuzz target for shadings. The first twelve bytes choose the shading type (1 to 7), the bit widths, the colour space, whether a
/// function is used and the geometry of the axial and radial types; the rest is the data stream of a mesh. The page paints the
/// shading with <c>sh</c>; the target then checks the decoded mesh (indices in range, finite values, storage bounded) and runs the
/// evaluators on it. Only the library's documented exceptions may escape.
/// </summary>
internal static class ShadingTarget
{
    private static readonly PdfLimits Limits = PdfLimits.Default with { MaxOperatorsPerPage = 10_000, MaxDecodedStreamBytes = 8 << 20 };
    private static readonly int[] CoordBits = [1, 2, 4, 8, 12, 16, 24, 32];
    private static readonly int[] ComponentBits = [1, 2, 4, 8, 12, 16, 8, 8];
    private static readonly int[] FlagBits = [2, 4, 8, 8];
    private static readonly (string Name, int Components)[] Spaces = [("/DeviceGray", 1), ("/DeviceRGB", 3), ("/DeviceCMYK", 4), ("[/Indexed /DeviceRGB 2 <ff0000 00ff00 0000ff>]", 1)];

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12)
        {
            return;
        }

        try
        {
            byte[] file = Wrap(data);
            using PdfDocument doc = PdfDocument.Open(file, new PdfOpenOptions { Limits = Limits });
            if (doc.Pages.Count == 0)
            {
                return;
            }

            var list = new PdfDisplayList();
            doc.Pages[0].Interpret(list);
            list.Replay(new Checker());
            _ = list.ToText().Length;
        }
        catch (PdfException)
        {
        }
    }

    private sealed class Checker : PdfDevice
    {
        public override void PaintShading(PdfShading shading, in PdfGraphicsState state)
        {
            var color = new double[Math.Max(shading.ColorComponentCount, 4)];
            if (shading.Mesh is { } m)
            {
                if (m.Vertices.Length != m.VertexCount * m.VertexStride || m.Triangles.Length != m.TriangleCount * 3)
                {
                    throw new InvalidOperationException("mesh sizes disagree");
                }

                foreach (int idx in m.Triangles.Span)
                {
                    if ((uint)idx >= (uint)m.VertexCount)
                    {
                        throw new InvalidOperationException("triangle index out of range");
                    }
                }

                ReadOnlySpan<double> v = m.Vertices.Span;
                for (int i = 0; i < v.Length; i++)
                {
                    if (!double.IsFinite(v[i]))
                    {
                        throw new InvalidOperationException("non-finite mesh value");
                    }
                }

                for (int i = 0; i < Math.Min(m.VertexCount, 64); i++)
                {
                    shading.Evaluate(v.Slice((i * m.VertexStride) + 2, shading.ParameterCount), color);
                    Finite(color);
                }

                return;
            }

            for (int i = 0; i < 49; i++)
            {
                if (shading.EvaluateAt(((i % 7) - 3) * 40.0, ((i / 7) - 3) * 40.0, color))
                {
                    Finite(color);
                }
            }

            if (shading.TryGetParameter(1, 2, out double t) && !double.IsFinite(t))
            {
                throw new InvalidOperationException("non-finite parameter");
            }
        }

        private static void Finite(double[] c)
        {
            foreach (double d in c)
            {
                if (!double.IsFinite(d))
                {
                    throw new InvalidOperationException("non-finite colour");
                }
            }
        }
    }

    private static string C(ReadOnlySpan<byte> input, int i) => ((input[6 + i] - 128) / 2.0).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The one-page file a fuzz input describes. Also used to build seeds.</summary>
    public static byte[] Wrap(ReadOnlySpan<byte> input)
    {
        int type = (input[0] % 7) + 1;
        int bpc = CoordBits[input[1] & 7];
        int bpk = ComponentBits[input[2] & 7];
        int bpf = FlagBits[input[3] & 3];
        int vpr = (input[4] % 16) + 1;
        (string cs, int nc) = Spaces[input[5] & 3];
        bool fn = (input[5] & 4) != 0 || type <= 3;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        int outputs = fn ? 3 : nc;
        string function = type == 1
            ? "/Function 6 0 R"
            : "/Function << /FunctionType 2 /Domain [0 1] /C0 [" + string.Join(' ', System.Linq.Enumerable.Repeat("0", outputs)) + "] /C1 [" + string.Join(' ', System.Linq.Enumerable.Repeat("1", outputs)) + "] /N 1 >>";
        string space = fn && type >= 4 ? "/DeviceRGB" : type <= 3 ? "/DeviceRGB" : cs;
        int np = fn ? 1 : nc;
        if (fn && type >= 4)
        {
            nc = 3;
        }

        var sb = new StringBuilder();
        sb.Append("/ShadingType ").Append(type).Append(" /ColorSpace ").Append(space).Append(' ');
        byte[]? stream = null;
        switch (type)
        {
            case 1:
                sb.Append("/Domain [0 1 0 1] /Matrix [").Append(C(input, 0)).Append(' ').Append(C(input, 1)).Append(' ').Append(C(input, 2)).Append(' ').Append(C(input, 3)).Append(' ').Append(C(input, 4)).Append(' ').Append(C(input, 5)).Append("] ").Append(function);
                break;
            case 2:
                sb.Append("/Coords [").Append(C(input, 0)).Append(' ').Append(C(input, 1)).Append(' ').Append(C(input, 2)).Append(' ').Append(C(input, 3)).Append("] /Extend [").Append((input[4] & 16) != 0 ? "true" : "false").Append(' ').Append((input[4] & 32) != 0 ? "true" : "false").Append("] ").Append(function);
                break;
            case 3:
                sb.Append("/Coords [").Append(C(input, 0)).Append(' ').Append(C(input, 1)).Append(' ').Append(Math.Abs(double.Parse(C(input, 2), ci))).Append(' ').Append(C(input, 3)).Append(' ').Append(C(input, 4)).Append(' ').Append(Math.Abs(double.Parse(C(input, 5), ci))).Append("] /Extend [").Append((input[4] & 16) != 0 ? "true" : "false").Append(' ').Append((input[4] & 32) != 0 ? "true" : "false").Append("] ").Append(function);
                break;
            default:
                sb.Append("/BitsPerCoordinate ").Append(bpc).Append(" /BitsPerComponent ").Append(bpk).Append(" /BitsPerFlag ").Append(bpf);
                sb.Append(" /VerticesPerRow ").Append(vpr).Append(" /Decode [-100 100 -100 100");
                for (int i = 0; i < np; i++)
                {
                    sb.Append(" 0 1");
                }

                sb.Append("] ");
                if (fn)
                {
                    sb.Append(function);
                }

                stream = input[12..].ToArray();
                break;
        }

        var bodies = new List<(string Dict, byte[]? Data)>
        {
            ("<< /Type /Catalog /Pages 2 0 R >>", null),
            ("<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            ("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /Shading << /S 5 0 R >> >> >>", null),
            ("", Encoding.Latin1.GetBytes("/S sh")),
            ("<< " + sb + " >>", stream),
            ("<< /FunctionType 4 /Domain [0 1 0 1] /Range [0 1 0 1 0 1] >>", Encoding.Latin1.GetBytes("{ 2 copy add 2 div }")),
        };

        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.7\n");
        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = (int)ms.Length;
            int n = i + 1;
            if (bodies[i].Data is { } d)
            {
                string dict = bodies[i].Dict.Length == 0 ? "<< " : bodies[i].Dict[..^2];
                W($"{n} 0 obj\n{dict} /Length {d.Length} >>\nstream\n");
                ms.Write(d);
                W("\nendstream\nendobj\n");
            }
            else
            {
                W($"{n} 0 obj\n{bodies[i].Dict}\nendobj\n");
            }
        }

        int xref = (int)ms.Length;
        W($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        foreach (int o in offsets)
        {
            W($"{o:D10} 00000 n \n");
        }

        W($"trailer\n<< /Size {bodies.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>Starting inputs: each shading type, with a small valid mesh for the stream types.</summary>
    public static IEnumerable<byte[]> Seeds()
    {
        byte[] geometry = [100, 90, 60, 200, 150, 90];
        for (int t = 0; t < 7; t++)
        {
            byte[] mesh = [0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 1, 255, 255, 0, 0, 0, 2, 128, 128, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
            yield return [(byte)t, 3, 3, 2, 3, 1, .. geometry, .. mesh];
            yield return [(byte)t, 3, 3, 2, 3, 5, .. geometry, .. mesh];
        }
    }
}
