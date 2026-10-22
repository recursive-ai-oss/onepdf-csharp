using System.IO.Compression;
using System.Reflection;
using System.Text;
using OnePdf.FontPrograms;

namespace OnePdf.FontData;

/// <summary>
/// Reads the generated data tables embedded in the assembly (see <c>tools/fontdata</c>). Each resource is a raw deflate stream.
/// Resources are read on first use; nothing is loaded at startup. No reflection beyond the manifest stream lookup, which trimming
/// and NativeAOT keep.
/// </summary>
internal static class FontResources
{
    private const string Prefix = "OnePdf.FontData.";

    /// <summary>Returns the stored (compressed) bytes of a resource.</summary>
    public static byte[] ReadStored(string name)
    {
        Assembly asm = typeof(FontResources).Assembly;
        using Stream? s = asm.GetManifestResourceStream(Prefix + name);
        if (s is null)
        {
            throw new FontProgramException("Missing font data resource " + name + ".");
        }

        var buf = new byte[checked((int)s.Length)];
        s.ReadExactly(buf);
        return buf;
    }

    /// <summary>Inflates a raw deflate stream. <paramref name="maxBytes"/> bounds the output.</summary>
    public static byte[] Inflate(ReadOnlySpan<byte> stored, int maxBytes)
    {
        using var input = new MemoryStream(stored.ToArray(), writable: false);
        using var z = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buf = new byte[16 * 1024];
        while (true)
        {
            int n = z.Read(buf, 0, buf.Length);
            if (n == 0)
            {
                break;
            }

            if (output.Length + n > maxBytes)
            {
                throw new FontProgramException("Font data resource is larger than its bound.");
            }

            output.Write(buf, 0, n);
        }

        return output.ToArray();
    }

    /// <summary>Reads and inflates a resource.</summary>
    public static byte[] Read(string name, int maxBytes = 64 << 20) => Inflate(ReadStored(name), maxBytes);

    /// <summary>Reads a resource that holds ASCII or Latin-1 text and splits it into lines.</summary>
    public static string[] ReadLines(string name)
    {
        string text = Encoding.Latin1.GetString(Read(name));
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
}
