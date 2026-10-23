using System.IO.Compression;

namespace OnePdf.Writing;

/// <summary>Flate (zlib, RFC 1950) compression for streams the writer produces.</summary>
internal static class FlateWriter
{
    public static byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream(Math.Max(64, data.Length / 2));
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(data);
        }

        return ms.ToArray();
    }
}
