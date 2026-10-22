using System.Text;
using OnePdf.Tests.Content;

namespace OnePdf.Tests.Shading;

/// <summary>Builds a one-page file whose resources hold shadings (and patterns) and collects what the device receives.</summary>
internal sealed class ShadingKit : IDisposable
{
    public PdfDocument Doc { get; }

    public PdfDisplayList List { get; }

    public Collector Dev { get; } = new();

    private ShadingKit(PdfDocument doc, PdfDisplayList list)
    {
        Doc = doc;
        List = list;
        list.Replay(Dev);
    }

    public PdfShading? Shading => Dev.Shadings.Count > 0 ? Dev.Shadings[0] : null;

    public void Dispose() => Doc.Dispose();

    /// <summary>Object 6 is the shading (a stream when <paramref name="streamData"/> is given); the page paints it with <c>sh</c>.</summary>
    public static ShadingKit Sh(string dict, byte[]? streamData = null, Action<PdfBuilder>? more = null, string content = "/S sh", int objects = 12)
    {
        byte[] file = TestPdf.Build(
            content,
            "/Resources << /Shading << /S 6 0 R >> /Pattern << /PS 7 0 R >> >>",
            b =>
            {
                if (streamData is null)
                {
                    b.Obj(6, "<< " + dict + " >>");
                }
                else
                {
                    b.Stream(6, dict, streamData);
                }

                b.Obj(7, "<< /PatternType 2 /Shading 6 0 R >>");
                more?.Invoke(b);
            },
            objects: objects);
        PdfDocument doc = TestPdf.Open(file);
        return new ShadingKit(doc, TestPdf.Record(doc));
    }

    internal sealed class Collector : PdfDevice
    {
        public List<PdfShading> Shadings { get; } = [];

        public List<PdfGraphicsState> States { get; } = [];

        public override void PaintShading(PdfShading shading, in PdfGraphicsState state)
        {
            Shadings.Add(shading);
            States.Add(state);
        }

        public override void FillPath(in PdfPath path, bool evenOdd, in PdfGraphicsState state) => States.Add(state);

        public override void StrokePath(in PdfPath path, in PdfGraphicsState state) => States.Add(state);
    }

    public const string Rgb2 = "/ShadingType 2 /ColorSpace /DeviceRGB ";

    public const string RedToBlue = "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> ";

    /// <summary>Evaluates a shading at a parameter and returns the colour.</summary>
    public static double[] At(PdfShading sh, params double[] p)
    {
        var c = new double[sh.ColorComponentCount];
        sh.Evaluate(p, c);
        return c;
    }

    public static double[]? AtPoint(PdfShading sh, double x, double y)
    {
        var c = new double[sh.ColorComponentCount];
        return sh.EvaluateAt(x, y, c) ? c : null;
    }

    public static void Near(double[] expected, double[]? actual, double eps = 1e-9)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Length, actual!.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(Math.Abs(expected[i] - actual[i]) <= eps, $"component {i}: expected {expected[i]}, got {actual[i]}");
        }
    }

    /// <summary>Packs values into a bit stream, most significant bit first, padding to a byte boundary after <see cref="Pad"/>.</summary>
    internal sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _bit;

        public BitWriter Put(long value, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                if (_bit == 0)
                {
                    _bytes.Add(0);
                }

                if (((value >> i) & 1) != 0)
                {
                    _bytes[^1] |= (byte)(0x80 >> _bit);
                }

                _bit = (_bit + 1) & 7;
            }

            return this;
        }

        public BitWriter Pad()
        {
            _bit = 0;
            return this;
        }

        public byte[] ToArray() => [.. _bytes];
    }

    public static string Mesh(int type, string extra = "", string cs = "/DeviceRGB", int bpc = 8, int bpk = 8, int bpf = 8, string? decode = null, int comps = 3) =>
        $"/ShadingType {type} /ColorSpace {cs} /BitsPerCoordinate {bpc} /BitsPerComponent {bpk} /BitsPerFlag {bpf} "
        + $"/Decode [0 100 0 100 {string.Join(' ', Enumerable.Repeat("0 1", comps))}] {extra}" + (decode ?? string.Empty);

    public static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);
}
