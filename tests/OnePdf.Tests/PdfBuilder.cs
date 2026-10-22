using System.IO.Compression;
using System.Text;

namespace OnePdf.Tests;

/// <summary>Assembles small PDF files byte by byte, recording object offsets, so tests can also break them in precise ways.</summary>
internal sealed class PdfBuilder
{
    private readonly List<byte> _ms = [];

    public PdfBuilder(string version = "1.7", string junkPrefix = "")
    {
        Write(junkPrefix);
        HeaderOffset = _ms.Count;
        Write($"%PDF-{version}\n%\u00e2\u00e3\u00cf\u00d3\n");
    }

    public Dictionary<int, long> Offsets { get; } = [];

    public long HeaderOffset { get; }

    public long Position => _ms.Count;

    public PdfBuilder Write(string s)
    {
        byte[] b = Encoding.Latin1.GetBytes(s);
        _ms.AddRange(b);
        return this;
    }

    public PdfBuilder Write(byte[] b)
    {
        _ms.AddRange(b);
        return this;
    }

    public PdfBuilder Obj(int n, string body)
    {
        Offsets[n] = _ms.Count;
        return Write($"{n} 0 obj\n{body}\nendobj\n");
    }

    public PdfBuilder Stream(int n, string dictEntries, byte[] data, string? lengthEntry = null)
    {
        Offsets[n] = _ms.Count;
        Write($"{n} 0 obj\n<< {dictEntries} /Length {lengthEntry ?? data.Length.ToString()} >>\nstream\n");
        Write(data);
        return Write("\nendstream\nendobj\n");
    }

    /// <summary>Catalog 1, Pages 2, one Page 3.</summary>
    public PdfBuilder BasicPages()
    {
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        return Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>");
    }

    public long XrefTable(int size, IEnumerable<int>? only = null, long bias = 0)
    {
        long at = _ms.Count;
        var sb = new StringBuilder();
        sb.Append("xref\n");
        HashSet<int>? set = only is null ? null : [.. only];
        sb.Append($"0 {size}\n");
        sb.Append("0000000000 65535 f \n");
        for (int i = 1; i < size; i++)
        {
            if (Offsets.TryGetValue(i, out long o) && (set is null || set.Contains(i)))
            {
                sb.Append($"{o - bias:D10} 00000 n \n");
            }
            else
            {
                sb.Append("0000000000 00000 f \n");
            }
        }

        Write(sb.ToString());
        return at;
    }

    public PdfBuilder Trailer(string entries, long xrefOffset, long bias = 0) =>
        Write($"trailer\n<< {entries} >>\nstartxref\n{xrefOffset - bias}\n%%EOF\n");

    public byte[] ToArray() => [.. _ms];

    public static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(data);
        }

        return ms.ToArray();
    }

    /// <summary>A complete file: basic pages, classic table, trailer.</summary>
    public static byte[] Simple()
    {
        var b = new PdfBuilder();
        b.BasicPages();
        long x = b.XrefTable(4);
        b.Trailer("/Size 4 /Root 1 0 R", x);
        return b.ToArray();
    }

    public static byte[] Replace(byte[] data, string find, string replace)
    {
        string s = Encoding.Latin1.GetString(data);
        int i = s.IndexOf(find, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{find}' not found");
        return Encoding.Latin1.GetBytes(s.Remove(i, find.Length).Insert(i, replace));
    }
}
