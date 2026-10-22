using System.Buffers;
using System.IO.Compression;

namespace OnePdf.Internal;

/// <summary>
/// The single place where stream filters are looked up. Today it provides what reading the document structure itself
/// needs (Flate with PNG/TIFF predictors); the full filter set replaces the bodies here and nothing else changes.
/// </summary>
internal static class FilterSeam
{
    /// <summary>Maps abbreviations used in inline images to the full filter name.</summary>
    public static string Canonical(string name) => name switch
    {
        "Fl" => "FlateDecode",
        "AHx" => "ASCIIHexDecode",
        "A85" => "ASCII85Decode",
        "LZW" => "LZWDecode",
        "RL" => "RunLengthDecode",
        "CCF" => "CCITTFaxDecode",
        "DCT" => "DCTDecode",
        _ => name,
    };

    /// <summary>True for codecs that produce image samples and are handled by the image layer rather than here.</summary>
    public static bool IsImageFilter(string canonical) => canonical is "DCTDecode" or "JPXDecode" or "JBIG2Decode" or "CCITTFaxDecode";

    /// <summary>True when <see cref="Decode"/> handles the filter.</summary>
    public static bool CanDecode(string canonical) => canonical is "FlateDecode" or "Crypt";

    /// <summary>
    /// Applies one filter. Damaged data returns what could be decoded and sets <paramref name="problem"/>.
    /// Throws <see cref="PdfLimitException"/> when the output would exceed the limits.
    /// </summary>
    public static byte[] Decode(string canonical, ReadOnlyMemory<byte> input, PdfDictionary? parms, PdfLimits limits, out string? problem)
    {
        problem = null;
        switch (canonical)
        {
            case "FlateDecode":
                byte[] raw = Inflate(input, limits, out problem);
                return ApplyPredictor(raw, parms, ref problem);
            case "Crypt":
                return input.ToArray();
            default:
                throw new PdfFormatException($"The stream filter /{canonical} is not supported.");
        }
    }

    private static byte[] Inflate(ReadOnlyMemory<byte> input, PdfLimits limits, out string? problem)
    {
        problem = null;
        long max = Math.Min(limits.MaxDecodedStreamBytes, Array.MaxLength);
        byte[] buf = ArrayPool<byte>.Shared.Rent(Math.Max(256, (int)Math.Min(max, Math.Min((long)input.Length * 4, 1 << 20))));
        int n = 0;
        try
        {
            using var src = new MemoryStream(input.ToArray(), writable: false);
            using var z = new ZLibStream(src, CompressionMode.Decompress);
            while (true)
            {
                if (n == buf.Length)
                {
                    if (n >= max)
                    {
                        throw new PdfLimitException($"A stream decodes to more than {max} bytes.");
                    }

                    long want = Math.Min(max, Math.Min((long)n * 2, Array.MaxLength));
                    byte[] bigger = ArrayPool<byte>.Shared.Rent((int)want);
                    buf.AsSpan(0, n).CopyTo(bigger);
                    ArrayPool<byte>.Shared.Return(buf);
                    buf = bigger;
                }

                int room = (int)Math.Min(buf.Length - n, max - n + 1);
                int got;
                try
                {
                    got = z.Read(buf, n, room);
                }
                catch (Exception e) when (e is InvalidDataException or IOException)
                {
                    problem = "The Flate data is damaged; the part before the damage was kept.";
                    break;
                }

                if (got == 0)
                {
                    break;
                }

                n += got;
                if (n > max)
                {
                    throw new PdfLimitException($"A stream decodes to more than {max} bytes.");
                }

                if (n > limits.ExpansionCheckThreshold && n / Math.Max(1, input.Length) > limits.MaxExpansionRatio)
                {
                    throw new PdfLimitException($"A stream expands by more than a factor of {limits.MaxExpansionRatio}.");
                }
            }

            return buf.AsSpan(0, n).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    // ISO 32000-2 7.4.4.4: predictor 2 is TIFF, 10..15 are PNG filters chosen per row.
    private static byte[] ApplyPredictor(byte[] data, PdfDictionary? parms, ref string? problem)
    {
        if (parms is null)
        {
            return data;
        }

        long predictor = parms.GetInt("Predictor", 1);
        if (predictor <= 1)
        {
            return data;
        }

        long colors = Math.Clamp(parms.GetInt("Colors", 1), 1, 64);
        long bpc = parms.GetInt("BitsPerComponent", 8);
        long columns = Math.Clamp(parms.GetInt("Columns", 1), 1, 1 << 24);
        long rowBitsL = columns * colors * bpc;
        if (bpc is not (1 or 2 or 4 or 8 or 16) || rowBitsL > int.MaxValue)
        {
            problem = "Unsupported predictor parameters.";
            return data;
        }

        int bpp = (int)Math.Max(1, (colors * bpc) / 8);
        int rowBytes = (int)((rowBitsL + 7) / 8);
        if (predictor == 2)
        {
            if (bpc == 8)
            {
                for (int row = 0; row + rowBytes <= data.Length; row += rowBytes)
                {
                    for (int i = bpp; i < rowBytes; i++)
                    {
                        data[row + i] = (byte)(data[row + i] + data[row + i - bpp]);
                    }
                }
            }
            else
            {
                problem = "TIFF predictor with other than 8 bits per component is not supported.";
            }

            return data;
        }

        int stride = rowBytes + 1;
        int rows = (data.Length + stride - 1) / stride;
        byte[] outBuf = new byte[(long)rows * rowBytes > Array.MaxLength ? throw new PdfLimitException("Predictor output is too large.") : rows * rowBytes];
        int outLen = 0;
        for (int r = 0; r < rows; r++)
        {
            int src = r * stride;
            int avail = Math.Min(stride, data.Length - src) - 1;
            if (avail <= 0)
            {
                break;
            }

            int ft = data[src];
            int dst = r * rowBytes;
            int prev = dst - rowBytes;
            for (int i = 0; i < avail; i++)
            {
                int x = data[src + 1 + i];
                int a = i >= bpp ? outBuf[dst + i - bpp] : 0;
                int b = r > 0 ? outBuf[prev + i] : 0;
                int c = r > 0 && i >= bpp ? outBuf[prev + i - bpp] : 0;
                int v = ft switch
                {
                    1 => x + a,
                    2 => x + b,
                    3 => x + ((a + b) >> 1),
                    4 => x + Paeth(a, b, c),
                    _ => x,
                };
                outBuf[dst + i] = (byte)v;
            }

            outLen = dst + avail;
        }

        return outLen == outBuf.Length ? outBuf : outBuf.AsSpan(0, outLen).ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
