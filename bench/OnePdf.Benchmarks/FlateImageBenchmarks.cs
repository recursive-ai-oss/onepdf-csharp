using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using BenchmarkDotNet.Attributes;

namespace OnePdf.Benchmarks;

/// <summary>
/// Loading a multi-megabyte Flate image XObject (open, decode, finish the samples): the allocation per load shows whether
/// the decoded data is allocated once. Uses only the public API, so the same file runs against an older build.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class FlateImageBenchmarks
{
    private byte[] _pdf = [];

    [Params("gray 2048x2048", "rgb 1024x1024 png-predictor")]
    public string Image { get; set; } = "gray 2048x2048";

    [GlobalSetup]
    public void Setup()
    {
        bool rgb = Image.StartsWith("rgb", StringComparison.Ordinal);
        int w = rgb ? 1024 : 2048, h = rgb ? 1024 : 2048, comps = rgb ? 3 : 1;
        var rnd = new Random(11);
        string[] words = ["the", "quick", "brown", "fox", "jumps", "over", "lazy", "dog", "stream", "object"];
        int row = w * comps;
        int stride = rgb ? row + 1 : row;
        var data = new byte[stride * h];
        int p = 0;
        while (p < data.Length)
        {
            string t = words[rnd.Next(words.Length)] + " ";
            for (int i = 0; i < t.Length && p < data.Length; i++)
            {
                data[p++] = (byte)t[i];
            }
        }

        if (rgb)
        {
            for (int r = 0; r < h; r++)
            {
                data[r * stride] = 0; // PNG row tag: None
            }
        }

        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true))
        {
            zs.Write(data);
        }

        byte[] packed = z.ToArray();
        string parms = rgb ? $" /DecodeParms << /Predictor 15 /Colors 3 /Columns {w} >>" : string.Empty;
        string dict = $"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace {(rgb ? "/DeviceRGB" : "/DeviceGray")} /BitsPerComponent 8 /Filter /FlateDecode{parms} /Length {packed.Length} >>";
        _pdf = Assemble(dict, packed);
    }

    private static byte[] Assemble(string imageDict, byte[] stream)
    {
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        var offsets = new long[5];
        W("%PDF-1.7\n");
        offsets[1] = ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[3] = ms.Position;
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>\nendobj\n");
        offsets[4] = ms.Position;
        W("4 0 obj\n" + imageDict + "\nstream\n");
        ms.Write(stream);
        W("\nendstream\nendobj\n");
        long xref = ms.Position;
        W("xref\n0 5\n0000000000 65535 f \n");
        for (int i = 1; i <= 4; i++)
        {
            W($"{offsets[i]:D10} 00000 n \n");
        }

        W($"trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    [Benchmark]
    public int LoadImage()
    {
        using PdfDocument doc = PdfDocument.Open(_pdf);
        var stream = (PdfStream)doc.GetObject(4)!;
        PdfImageData img = PdfImageData.FromXObject(doc, stream, null, PdfImageOptions.Default)!;
        return img.Samples.Length;
    }
}
