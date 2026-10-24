using System.Buffers.Binary;
using OnePdf.Filters;

namespace OnePdf.Images;

/// <summary>The colour model of a PNG image as PDF sees it.</summary>
internal enum PngKind
{
    Gray,
    Rgb,
    Indexed,
}

/// <summary>A PNG file turned into what a PDF image XObject needs. Not a general decoder: see <see cref="PngReader"/>.</summary>
internal sealed class PngImageData
{
    public int Width { get; init; }

    public int Height { get; init; }

    public PngKind Kind { get; init; }

    /// <summary>Bits per colour sample (1, 2, 4, 8 or 16).</summary>
    public int BitsPerComponent { get; init; }

    /// <summary>Colour components per pixel (1 or 3).</summary>
    public int Components { get; init; }

    /// <summary>For indexed images: RGB triples, padded to 2^bits entries (at most 256).</summary>
    public byte[]? Palette { get; init; }

    /// <summary>The ICC profile as a zlib stream, and its component count, when the file carries a usable one.</summary>
    public byte[]? IccProfileZlib { get; init; }

    public int IccComponents { get; init; }

    /// <summary>
    /// Image data. When <see cref="IsPassThrough"/> it is the concatenated IDAT payload (a zlib stream of PNG-filtered rows) for a
    /// <c>FlateDecode</c> stream with <c>/Predictor 15</c>; otherwise packed, unfiltered rows (each padded to a whole byte).
    /// </summary>
    public required byte[] Data { get; init; }

    public bool IsPassThrough { get; init; }

    /// <summary>A colour-key mask: min and max per component, in sample units (for the PDF <c>/Mask</c> entry).</summary>
    public int[]? ColorKey { get; init; }

    /// <summary>8-bit or 16-bit alpha samples for a soft mask, one per pixel, or null.</summary>
    public byte[]? Alpha { get; init; }

    public int AlphaBitsPerComponent { get; init; }

    public List<string> Warnings { get; } = [];
}

/// <summary>
/// A minimal reader for the PNG file format (W3C PNG 3rd edition / ISO 15948), only as far as it is needed to put a PNG into a
/// PDF image XObject. It is not a general codec: no APNG, no writing, and no colour management beyond an embedded ICC profile
/// (<c>gAMA</c>, <c>cHRM</c>, <c>sRGB</c>, <c>sBIT</c> and the other ancillary chunks are ignored; samples are used as stored).
/// </summary>
/// <remarks>
/// <para>A non-interlaced image without an alpha channel is passed through: the IDAT data is not decoded. PDF's predictor 15
/// (ISO 32000-2 7.4.4.4) uses the same filtered-row layout as PNG, so the bytes go straight into a <c>FlateDecode</c> stream.
/// Interlaced images and images with alpha are decoded (with the library's inflater) to unfiltered rows, the alpha channel
/// becoming a soft mask.</para>
/// <para>Limits: pixels against <see cref="PdfLimits.MaxImagePixels"/> and decoded bytes against
/// <see cref="PdfLimits.MaxDecodedStreamBytes"/>, both checked from the header before anything is allocated. A bad CRC in a critical
/// chunk (IHDR, PLTE, IDAT) is an error; in an ancillary chunk that is used (tRNS, iCCP) the chunk is dropped with a warning.</para>
/// </remarks>
internal static class PngReader
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private const int MaxIccBytes = 8 << 20;
    private const int MaxChunks = 1 << 20;

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            t[n] = c;
        }

        return t;
    }

    public static PngImageData Read(ReadOnlySpan<byte> file, PdfLimits limits)
    {
        if (file.Length < 8 + 25 || !file[..8].SequenceEqual(Signature))
        {
            throw new PdfFormatException("The data is not a PNG file (bad signature).");
        }

        var warnings = new List<string>();
        int pos = 8;
        int width = 0, height = 0, depth = 0, colorType = 0, interlace = 0;
        bool haveHeader = false;
        byte[]? plte = null;
        byte[]? trns = null;
        byte[]? iccp = null;
        int idatTotal = 0;
        var idatChunks = new List<(int Start, int Length)>();
        bool sawEnd = false;
        int chunks = 0;

        while (pos + 12 <= file.Length)
        {
            if (++chunks > MaxChunks)
            {
                throw new PdfLimitException("The PNG has too many chunks.");
            }

            uint rawLen = BinaryPrimitives.ReadUInt32BigEndian(file[pos..]);
            if (rawLen > int.MaxValue)
            {
                throw new PdfFormatException("A PNG chunk has an invalid length.");
            }

            int len = (int)rawLen;
            if (len > file.Length - pos - 12)
            {
                throw new PdfFormatException("The PNG is truncated inside a chunk.");
            }

            ReadOnlySpan<byte> type = file.Slice(pos + 4, 4);
            ReadOnlySpan<byte> body = file.Slice(pos + 8, len);
            uint stored = BinaryPrimitives.ReadUInt32BigEndian(file[(pos + 8 + len)..]);
            string name = System.Text.Encoding.Latin1.GetString(type);
            bool critical = (type[0] & 0x20) == 0;
            bool crcOk = true;
            if (critical || name is "tRNS" or "iCCP")
            {
                // The CRC covers the type and the data (PNG 5.3).
                uint c = 0xFFFFFFFFu;
                foreach (byte b in type)
                {
                    c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
                }

                foreach (byte b in body)
                {
                    c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
                }

                crcOk = (c ^ 0xFFFFFFFFu) == stored;
            }

            if (!crcOk && critical)
            {
                throw new PdfFormatException($"The PNG chunk {name} has a bad CRC.");
            }

            if (!crcOk)
            {
                warnings.Add($"The PNG chunk {name} has a bad CRC and was ignored.");
            }
            else if (!haveHeader)
            {
                if (name != "IHDR" || len != 13)
                {
                    throw new PdfFormatException("The PNG does not start with an IHDR chunk.");
                }

                uint w = BinaryPrimitives.ReadUInt32BigEndian(body);
                uint h = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue)
                {
                    throw new PdfFormatException("The PNG has an invalid size.");
                }

                width = (int)w;
                height = (int)h;
                depth = body[8];
                colorType = body[9];
                interlace = body[12];
                if (body[10] != 0 || body[11] != 0 || interlace > 1)
                {
                    throw new PdfFormatException("The PNG uses an unknown compression, filter or interlace method.");
                }

                bool validDepth = colorType switch
                {
                    0 => depth is 1 or 2 or 4 or 8 or 16,
                    2 or 4 or 6 => depth is 8 or 16,
                    3 => depth is 1 or 2 or 4 or 8,
                    _ => false,
                };
                if (!validDepth)
                {
                    throw new PdfFormatException($"The PNG has an invalid colour type {colorType} / bit depth {depth} combination.");
                }

                if ((long)width * height > limits.MaxImagePixels)
                {
                    throw new PdfLimitException($"The image has more than {limits.MaxImagePixels} pixels.");
                }

                haveHeader = true;
            }
            else
            {
                switch (name)
                {
                    case "IHDR":
                        throw new PdfFormatException("The PNG has two IHDR chunks.");
                    case "PLTE":
                        if (len == 0 || len % 3 != 0 || len > 768)
                        {
                            throw new PdfFormatException("The PNG palette has an invalid length.");
                        }

                        plte ??= body.ToArray();
                        break;
                    case "tRNS":
                        trns ??= body.ToArray();
                        break;
                    case "iCCP":
                        iccp ??= body.ToArray();
                        break;
                    case "IDAT":
                        idatTotal = checked(idatTotal + len);
                        if (idatTotal > limits.MaxDecodedStreamBytes)
                        {
                            throw new PdfLimitException("The PNG image data is larger than the stream size limit.");
                        }

                        idatChunks.Add((pos + 8, len));
                        break;
                }
            }

            pos += 12 + len;
            if (name == "IEND")
            {
                sawEnd = true;
                break;
            }
        }

        if (!haveHeader)
        {
            throw new PdfFormatException("The PNG has no IHDR chunk.");
        }

        if (idatChunks.Count == 0 || idatTotal < 2 + 4)
        {
            throw new PdfFormatException("The PNG has no image data.");
        }

        if (!sawEnd)
        {
            warnings.Add("The PNG has no IEND chunk (truncated after the image data).");
        }

        PngKind kind = colorType switch { 0 or 4 => PngKind.Gray, 3 => PngKind.Indexed, _ => PngKind.Rgb };
        int components = kind == PngKind.Rgb ? 3 : 1;
        bool hasAlphaChannel = colorType is 4 or 6;
        int channels = colorType switch { 0 or 3 => 1, 4 => 2, 2 => 3, _ => 4 };

        byte[]? palette = null;
        if (kind == PngKind.Indexed)
        {
            if (plte is null)
            {
                throw new PdfFormatException("The palette PNG has no PLTE chunk.");
            }

            int entries = 1 << depth;
            if (plte.Length / 3 > entries)
            {
                throw new PdfFormatException("The PNG palette has more entries than the bit depth allows.");
            }

            palette = new byte[entries * 3]; // entries past the palette's end are black, so a stray index cannot read outside
            plte.CopyTo(palette, 0);
        }

        // The data of all IDAT chunks as one zlib stream.
        var idat = new byte[idatTotal];
        int at = 0;
        foreach ((int start, int length) in idatChunks)
        {
            file.Slice(start, length).CopyTo(idat.AsSpan(at));
            at += length;
        }

        // A zlib header (RFC 1950 2.2): deflate method with a window of at most 32 KiB, and a header checksum that divides by 31.
        if ((idat[0] & 0x0F) != 8 || (idat[0] >> 4) > 7 || (((idat[0] << 8) | idat[1]) % 31) != 0 || (idat[1] & 0x20) != 0)
        {
            throw new PdfFormatException("The PNG image data is not a zlib stream.");
        }

        // ---- transparency ----
        int[]? colorKey = null;
        byte[]? paletteAlpha = null; // per palette index, only when it cannot be a colour key
        if (trns is not null && !hasAlphaChannel)
        {
            switch (kind)
            {
                case PngKind.Gray when trns.Length >= 2:
                    {
                        int v = BinaryPrimitives.ReadUInt16BigEndian(trns) & ((1 << depth) - 1);
                        colorKey = [v, v];
                        break;
                    }

                case PngKind.Rgb when trns.Length >= 6:
                    {
                        int mask = (1 << depth) - 1;
                        int r = BinaryPrimitives.ReadUInt16BigEndian(trns) & mask;
                        int g = BinaryPrimitives.ReadUInt16BigEndian(trns.AsSpan(2)) & mask;
                        int b = BinaryPrimitives.ReadUInt16BigEndian(trns.AsSpan(4)) & mask;
                        colorKey = [r, r, g, g, b, b];
                        break;
                    }

                case PngKind.Indexed:
                    {
                        int n = Math.Min(trns.Length, palette!.Length / 3);
                        int first = -1, last = -1;
                        bool keyable = true;
                        for (int i = 0; i < n; i++)
                        {
                            if (trns[i] == 255)
                            {
                                continue;
                            }

                            if (trns[i] != 0 || (first >= 0 && i != last + 1))
                            {
                                keyable = false;
                                break;
                            }

                            first = first < 0 ? i : first;
                            last = i;
                        }

                        if (keyable && first >= 0)
                        {
                            colorKey = [first, last]; // a single run of fully transparent entries is a colour-key range
                        }
                        else if (!keyable)
                        {
                            paletteAlpha = new byte[palette.Length / 3];
                            Array.Fill(paletteAlpha, (byte)255);
                            trns.AsSpan(0, n).CopyTo(paletteAlpha);
                        }

                        break;
                    }

                default:
                    warnings.Add("The PNG tRNS chunk is too short for its colour type and was ignored.");
                    break;
            }
        }

        // ---- ICC profile ----
        byte[]? iccZlib = null;
        int iccN = 0;
        if (iccp is not null)
        {
            (iccZlib, iccN) = ReadIcc(iccp, kind, warnings);
        }

        bool needsDecode = interlace == 1 || hasAlphaChannel || paletteAlpha is not null;
        if (!needsDecode)
        {
            return new PngImageData
            {
                Width = width,
                Height = height,
                Kind = kind,
                BitsPerComponent = depth,
                Components = components,
                Palette = palette,
                IccProfileZlib = iccZlib,
                IccComponents = iccN,
                Data = idat,
                IsPassThrough = true,
                ColorKey = colorKey,
            }.WithWarnings(warnings);
        }

        // ---- decode path ----
        int bitsPerPixel = channels * depth;
        long rawSize = RawSize(width, height, bitsPerPixel, interlace == 1);
        long outSize = (long)height * (((long)width * bitsPerPixel + 7) / 8);
        if (rawSize > limits.MaxDecodedStreamBytes || outSize > limits.MaxDecodedStreamBytes)
        {
            throw new PdfLimitException("The decoded PNG would be larger than the stream size limit.");
        }

        byte[] raw = InflateExact(idat, (int)rawSize);
        byte[] pixels = Unfilter(raw, width, height, bitsPerPixel, interlace == 1);

        int stride = (int)(((long)width * bitsPerPixel + 7) / 8);
        byte[] colorData;
        byte[]? alpha = null;
        int alphaBpc = 8;
        if (hasAlphaChannel)
        {
            int bytesPer = depth / 8;
            int colorCh = channels - 1;
            colorData = new byte[checked(height * ((int)(((long)width * colorCh * depth + 7) / 8)))];
            alpha = new byte[checked(width * height * bytesPer)];
            alphaBpc = depth;
            int px = channels * bytesPer;
            int ci = 0;
            int ai = 0;
            for (int y = 0; y < height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < width; x++)
                {
                    int p = row + (x * px);
                    for (int k = 0; k < colorCh * bytesPer; k++)
                    {
                        colorData[ci++] = pixels[p + k];
                    }

                    for (int k = 0; k < bytesPer; k++)
                    {
                        alpha[ai++] = pixels[p + (colorCh * bytesPer) + k];
                    }
                }
            }
        }
        else
        {
            colorData = pixels;
            if (paletteAlpha is not null)
            {
                alpha = new byte[checked(width * height)];
                int ai = 0;
                int mask = (1 << depth) - 1;
                for (int y = 0; y < height; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < width; x++)
                    {
                        int bit = x * depth;
                        int idx = (pixels[row + (bit >> 3)] >> (8 - depth - (bit & 7))) & mask;
                        alpha[ai++] = paletteAlpha[idx];
                    }
                }
            }
        }

        return new PngImageData
        {
            Width = width,
            Height = height,
            Kind = kind,
            BitsPerComponent = depth,
            Components = components,
            Palette = palette,
            IccProfileZlib = iccZlib,
            IccComponents = iccN,
            Data = colorData,
            ColorKey = colorKey,
            Alpha = alpha,
            AlphaBitsPerComponent = alphaBpc,
        }.WithWarnings(warnings);
    }

    private static PngImageData WithWarnings(this PngImageData d, List<string> warnings)
    {
        d.Warnings.AddRange(warnings);
        return d;
    }

    private static (byte[]? Zlib, int N) ReadIcc(byte[] chunk, PngKind kind, List<string> warnings)
    {
        // iCCP: profile name (1-79 bytes), a zero byte, the compression method (0), then the zlib-compressed profile (PNG 11.3.3.3).
        int z = Array.IndexOf(chunk, (byte)0);
        if (z < 1 || z > 79 || z + 2 >= chunk.Length || chunk[z + 1] != 0)
        {
            warnings.Add("The PNG iCCP chunk is malformed and was ignored.");
            return (null, 0);
        }

        byte[] zlib = chunk[(z + 2)..];
        byte[] profile;
        try
        {
            profile = InflateAtMost(zlib, MaxIccBytes);
        }
        catch (PdfFormatException)
        {
            warnings.Add("The PNG iCCP profile could not be decompressed and was ignored.");
            return (null, 0);
        }

        // ICC.1 7.2: 128-byte header; 'acsp' at offset 36; colour space signature at offset 16.
        if (profile.Length < 132 || profile[36] != (byte)'a' || profile[37] != (byte)'c' || profile[38] != (byte)'s' || profile[39] != (byte)'p')
        {
            warnings.Add("The PNG iCCP profile is not an ICC profile and was ignored.");
            return (null, 0);
        }

        string space = System.Text.Encoding.Latin1.GetString(profile, 16, 4);
        int n = space switch { "RGB " => 3, "GRAY" => 1, _ => 0 };
        int need = kind == PngKind.Gray ? 1 : 3;
        if (n != need)
        {
            warnings.Add($"The PNG iCCP profile colour space '{space.Trim()}' does not match the image and was ignored.");
            return (null, 0);
        }

        return (zlib, n);
    }

    private static long RawSize(int width, int height, int bitsPerPixel, bool interlaced)
    {
        if (!interlaced)
        {
            return height * (1 + (((long)width * bitsPerPixel + 7) / 8));
        }

        long total = 0;
        for (int p = 0; p < 7; p++)
        {
            (int pw, int ph) = PassSize(p, width, height);
            if (pw > 0 && ph > 0)
            {
                total += ph * (1 + (((long)pw * bitsPerPixel + 7) / 8));
            }
        }

        return total;
    }

    private static readonly int[] XStart = [0, 4, 0, 2, 0, 1, 0];
    private static readonly int[] YStart = [0, 0, 4, 0, 2, 0, 1];
    private static readonly int[] XStep = [8, 8, 4, 4, 2, 2, 1];
    private static readonly int[] YStep = [8, 8, 8, 4, 4, 2, 2];

    private static (int W, int H) PassSize(int pass, int width, int height) =>
        ((width - XStart[pass] + XStep[pass] - 1) / XStep[pass], (height - YStart[pass] + YStep[pass] - 1) / YStep[pass]);

    private static byte[] InflateExact(byte[] zlib, int expected)
    {
        // One byte of slack so a stream that is longer than the image is noticed rather than cut short.
        byte[] output = new byte[expected + 1];
        int n = InflateInto(zlib, output);
        if (n < expected)
        {
            throw new PdfFormatException($"The PNG image data is truncated ({n} of {expected} bytes).");
        }

        return output;
    }

    private static byte[] InflateAtMost(byte[] zlib, int max)
    {
        byte[] output = new byte[max + 1];
        int n = InflateInto(zlib, output);
        if (n > max)
        {
            throw new PdfFormatException("The compressed data is larger than allowed.");
        }

        return output.AsSpan(0, n).ToArray();
    }

    private static int InflateInto(byte[] zlib, byte[] output)
    {
        using var inf = new Inflater(true);
        int inPos = 0;
        int outPos = 0;
        try
        {
            while (true)
            {
                InflateStatus st = inf.Inflate(zlib.AsSpan(inPos), output.AsSpan(outPos), out int consumed, out int written);
                inPos += consumed;
                outPos += written;
                if (st == InflateStatus.Done)
                {
                    return outPos;
                }

                if (st == InflateStatus.NeedInput)
                {
                    throw new PdfFormatException("The compressed PNG image data ends early.");
                }

                if (outPos >= output.Length)
                {
                    return outPos;
                }
            }
        }
        catch (FilterDataException ex)
        {
            throw new PdfFormatException("The PNG image data is damaged: " + ex.Message, ex);
        }
    }

    // Reverses the five row filters (PNG 9.2) and, for interlaced images, places the seven passes (PNG 8.2) into one packed image.
    private static byte[] Unfilter(byte[] raw, int width, int height, int bitsPerPixel, bool interlaced)
    {
        int bpp = Math.Max(1, bitsPerPixel / 8);
        int stride = (int)(((long)width * bitsPerPixel + 7) / 8);
        byte[] image = new byte[checked(height * stride)];
        int pos = 0;
        if (!interlaced)
        {
            for (int y = 0; y < height; y++)
            {
                UnfilterRow(raw, pos, image, y * stride, y == 0 ? -1 : (y - 1) * stride, stride, bpp);
                pos += stride + 1;
            }

            return image;
        }

        for (int pass = 0; pass < 7; pass++)
        {
            (int pw, int ph) = PassSize(pass, width, height);
            if (pw == 0 || ph == 0)
            {
                continue;
            }

            int pstride = (int)(((long)pw * bitsPerPixel + 7) / 8);
            byte[] cur = new byte[pstride];
            byte[] last = new byte[pstride];
            for (int y = 0; y < ph; y++)
            {
                UnfilterRow(raw, pos, cur, 0, y == 0 ? -2 : -3, pstride, bpp, last);
                pos += pstride + 1;
                int dy = YStart[pass] + (y * YStep[pass]);
                for (int x = 0; x < pw; x++)
                {
                    int dx = XStart[pass] + (x * XStep[pass]);
                    CopyPixel(cur, x, image, (dy * stride), dx, bitsPerPixel);
                }

                (cur, last) = (last, cur);
            }
        }

        return image;
    }

    private static void CopyPixel(byte[] src, int sx, byte[] dst, int dstRow, int dx, int bitsPerPixel)
    {
        if (bitsPerPixel >= 8)
        {
            int n = bitsPerPixel / 8;
            Array.Copy(src, sx * n, dst, dstRow + (dx * n), n);
            return;
        }

        int sbit = sx * bitsPerPixel;
        int dbit = dx * bitsPerPixel;
        int mask = (1 << bitsPerPixel) - 1;
        int v = (src[sbit >> 3] >> (8 - bitsPerPixel - (sbit & 7))) & mask;
        int shift = 8 - bitsPerPixel - (dbit & 7);
        dst[dstRow + (dbit >> 3)] = (byte)((dst[dstRow + (dbit >> 3)] & ~(mask << shift)) | (v << shift));
    }

    // prevRow: offset of the previous row inside 'image' (-1: none); for the interlaced path 'previous' holds it instead.
    private static void UnfilterRow(byte[] raw, int rawPos, byte[] image, int dst, int prevRow, int stride, int bpp, byte[]? previous = null)
    {
        byte filter = raw[rawPos];
        int src = rawPos + 1;
        for (int i = 0; i < stride; i++)
        {
            int x = raw[src + i];
            int a = i >= bpp ? image[dst + i - bpp] : 0;
            int b = previous is not null ? previous[i] : (prevRow >= 0 ? image[prevRow + i] : 0);
            int c = i >= bpp ? (previous is not null ? previous[i - bpp] : (prevRow >= 0 ? image[prevRow + i - bpp] : 0)) : 0;
            int v;
            switch (filter)
            {
                case 0: v = x; break;
                case 1: v = x + a; break;
                case 2: v = x + b; break;
                case 3: v = x + ((a + b) >> 1); break;
                case 4:
                    {
                        int p = a + b - c;
                        int pa = Math.Abs(p - a);
                        int pb = Math.Abs(p - b);
                        int pc = Math.Abs(p - c);
                        v = x + (pa <= pb && pa <= pc ? a : (pb <= pc ? b : c));
                        break;
                    }

                default:
                    throw new PdfFormatException($"The PNG has an unknown row filter type {filter}.");
            }

            image[dst + i] = (byte)v;
        }
    }
}
