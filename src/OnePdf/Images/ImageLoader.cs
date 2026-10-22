using System;
using System.Collections.Generic;
using System.Threading;
using OnePdf.Filters;

namespace OnePdf;

/// <summary>
/// Turns image XObjects and inline images into <see cref="PdfImage"/> (ISO 32000-2 8.9 and 11.6.5): sample unpacking,
/// <c>/Decode</c>, colour spaces, stencil masks, colour-key, explicit and soft masks, codec decoders.
/// </summary>
internal static class ImageLoader
{
    private sealed class Src
    {
        public PdfDocument Doc = null!;
        public PdfDictionary Dict = null!;
        public PdfDictionary? Resources;
        public bool Inline;
        public int ObjNum = -1;
        public ReadOnlyMemory<byte> Data;
        public string? Codec;
        public PdfDictionary? CodecParms;
        public PdfImageOptions Options = PdfImageOptions.Default;
        public bool AllowMasks = true;
    }

    /// <summary>The samples of an image before PDF semantics: what is in the stream or what a decoder returned.</summary>
    private readonly record struct Raw(ReadOnlyMemory<byte> Data, int Width, int Height, int Components, bool HasAlpha, int Bpc, bool InvertBits)
    {
        public long Stride => ((long)Width * (Components + (HasAlpha ? 1 : 0)) * Bpc + 7) / 8;
    }

    public static PdfImage? LoadXObject(PdfDocument doc, PdfStream stream, PdfDictionary? resources, PdfImageOptions options, bool allowMasks, CancellationToken ct)
    {
        PdfDictionary d = stream.Dictionary;
        var s = new Src
        {
            Doc = doc,
            Dict = d,
            Resources = resources,
            ObjNum = stream.Reference?.Number ?? -1,
            Options = options,
            AllowMasks = allowMasks,
            Data = stream.GetDecodedBytes(ct),
            Codec = stream.UndecodedFilter,
        };
        if (s.Codec is not null)
        {
            IReadOnlyList<string> names = stream.FilterNames;
            int idx = -1;
            for (int i = 0; i < names.Count; i++)
            {
                if (names[i] == s.Codec)
                {
                    idx = i;
                    break;
                }
            }

            s.CodecParms = ParmsAt(d, idx);
            if (idx >= 0 && idx < names.Count - 1)
            {
                doc.ReportWarning("Filters after the image codec " + s.Codec + " are ignored.", s.ObjNum, -1);
            }
        }

        return Build(s, ct);
    }

    public static PdfImage? LoadInline(PdfDocument doc, PdfDictionary dict, ReadOnlyMemory<byte> data, PdfDictionary? resources, PdfImageOptions options, CancellationToken ct)
    {
        var s = new Src { Doc = doc, Dict = dict, Resources = resources, Inline = true, Options = options, Data = data };
        var names = new List<string>();
        PdfObject? f = Pick(dict, s, "Filter", "F");
        if (f is PdfName fn)
        {
            names.Add(Canonical(fn.Value));
        }
        else if (f is PdfArray fa)
        {
            for (int i = 0; i < fa.Count; i++)
            {
                names.Add(Canonical(fa.GetName(i) ?? string.Empty));
            }
        }

        if (names.Count == 0)
        {
            return Build(s, ct);
        }

        PdfObject? parms = Pick(dict, s, "DecodeParms", "DP");
        var fp = new FilterParameters?[names.Count];
        for (int i = 0; i < fp.Length; i++)
        {
            PdfDictionary? p = parms switch
            {
                PdfDictionary pd => i == 0 ? pd : null,
                PdfArray pa when i < pa.Count => pa[i] as PdfDictionary,
                _ => null,
            };
            if (p is not null)
            {
                fp[i] = new FilterParameters(
                    Clamp(p.GetInt("Predictor", 1)), Clamp(p.GetInt("Colors", 1)), Clamp(p.GetInt("BitsPerComponent", 8)),
                    Clamp(p.GetInt("Columns", 1)), Clamp(p.GetInt("EarlyChange", 1)));
            }
        }

        PdfLimits lim = doc.Limits;
        var limits = new DecodeLimits(Math.Min(lim.MaxDecodedStreamBytes, Array.MaxLength), lim.MaxExpansionRatio, lim.ExpansionCheckThreshold);
        FilterResult r = StreamFilters.DecodeChain(names, fp, data, limits, ct);
        if ((r.Issues & FilterIssues.LimitExceeded) != 0)
        {
            throw new PdfLimitException(r.Message ?? "An inline image decodes to more than the configured limits allow.");
        }

        if ((r.Issues & ~FilterIssues.UnsupportedFilter) != 0)
        {
            doc.ReportWarning(r.Message ?? "Inline image data is damaged.", -1, -1);
        }

        s.Data = r.Data;
        if (r.StoppedAtFilter >= 0)
        {
            s.Codec = names[r.StoppedAtFilter];
            s.CodecParms = parms switch
            {
                PdfDictionary pd => r.StoppedAtFilter == 0 ? pd : null,
                PdfArray pa when r.StoppedAtFilter < pa.Count => pa[r.StoppedAtFilter] as PdfDictionary,
                _ => null,
            };
        }

        return Build(s, ct);
    }

    private static int Clamp(long v) => (int)Math.Clamp(v, int.MinValue, int.MaxValue);

    private static string Canonical(string name) => name switch
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

    private static PdfDictionary? ParmsAt(PdfDictionary d, int index)
    {
        if (index < 0)
        {
            return null;
        }

        PdfObject? p = d.Get("DecodeParms") ?? d.Get("DP");
        return p switch
        {
            PdfDictionary pd => index == 0 ? pd : null,
            PdfArray pa when index < pa.Count => pa[index] as PdfDictionary,
            _ => null,
        };
    }

    /// <summary>Looks up a key by its full name, or by the inline-image abbreviation in inline dictionaries.</summary>
    private static PdfObject? Pick(PdfDictionary d, Src s, string full, string abbr) => d.Get(full) ?? (s.Inline ? d.Get(abbr) : null);

    private static PdfImage? Build(Src s, CancellationToken ct)
    {
        PdfDocument doc = s.Doc;
        PdfDictionary d = s.Dict;
        PdfLimits lim = doc.Limits;
        long w = AsInt(Pick(d, s, "Width", "W"));
        long h = AsInt(Pick(d, s, "Height", "H"));
        bool mask = Pick(d, s, "ImageMask", "IM") is PdfBoolean { Value: true };
        if (w < 1 || h < 1 || w > int.MaxValue || h > int.MaxValue)
        {
            doc.ReportWarning("An image has a missing or invalid /Width or /Height; it is skipped.", s.ObjNum, -1);
            return null;
        }

        // Limits come before any buffer is sized from the declared dimensions (DESIGN: limits before allocation).
        if (w * h > lim.MaxImagePixels)
        {
            throw new PdfLimitException($"An image of {w}x{h} pixels exceeds the limit of {lim.MaxImagePixels} pixels.") { ObjectNumber = s.ObjNum };
        }

        var img = new PdfImage
        {
            Width = (int)w,
            Height = (int)h,
            IsStencil = mask,
            Interpolate = Pick(d, s, "Interpolate", "I") is PdfBoolean { Value: true },
            RenderingIntent = d.GetName("Intent"),
            EncodedData = s.Data,
            EncodedFilter = s.Codec,
        };
        PdfImageSemantics sem = img.Semantics;
        sem.BitsPerComponent = (int)Math.Clamp(AsInt(Pick(d, s, "BitsPerComponent", "BPC")), 0, 64);
        double[]? decode = Numbers(Pick(d, s, "Decode", "D"));
        sem.Decode = decode;
        PdfObject? maskObj = d.Get("Mask");
        int[]? key = null;
        if (maskObj is PdfArray ka && ka.Count >= 2 && !mask)
        {
            key = new int[ka.Count];
            for (int i = 0; i < key.Length; i++)
            {
                key[i] = (int)Math.Clamp(ka.GetInt(i), 0, 65535);
            }

            sem.ColorKeyMask = key;
        }

        sem.SMaskInData = (int)Math.Clamp(d.GetInt("SMaskInData", 0), 0, 2);
        PdfObject? csObj = Pick(d, s, "ColorSpace", "CS");
        PdfColorSpace? declared = null;
        if (!mask && csObj is not null)
        {
            declared = ColorSpaceResolver.Resolve(doc, csObj, s.Resources, s.ObjNum);
            if (declared is { Kind: PdfColorSpaceKind.Pattern })
            {
                doc.ReportWarning("An image cannot use a Pattern colour space.", s.ObjNum, -1);
                declared = null;
            }
        }

        sem.ColorSpace = declared;
        if (s.Codec == "DCTDecode" && s.CodecParms is { } dp && dp.GetInt("ColorTransform", -1) is 0 or 1)
        {
            sem.ColorTransform = (int)dp.GetInt("ColorTransform");
        }

        // The mask planes do not depend on the main samples' decoding, so they are built first.
        PdfImageAlpha? maskAlpha = null;
        double[]? matte = null;
        if (s.AllowMasks && !mask)
        {
            maskAlpha = BuildMaskPlane(s, d, maskObj, out matte, ct);
            sem.Matte = matte;
        }

        // Plain stream or codec output?
        Raw raw;
        PdfImageDecoderRegistry reg = s.Options.Decoders;
        if (s.Codec is not null)
        {
            if (s.Codec == "JBIG2Decode" && s.CodecParms?.Get("JBIG2Globals") is PdfStream gs)
            {
                img.EncodedGlobals = gs.GetDecodedBytes(ct);
            }

            bool lossy = s.Codec is "DCTDecode" or "JPXDecode";
            IPdfImageDecoder? dec = null;
            bool known = s.Codec is "DCTDecode" or "JPXDecode" or "JBIG2Decode";
            if (!known)
            {
                doc.ReportWarning("Image filter " + s.Codec + " is not supported; the encoded data is returned.", s.ObjNum, -1);
            }
            else if (lossy && s.Options.PreferEncoded)
            {
                // The device asked for the original bytes; semantics travel as data.
            }
            else if (!reg.TryGet(s.Codec, out dec) || dec is null)
            {
                doc.ReportWarning("No decoder is registered for " + s.Codec + "; the encoded data is returned.", s.ObjNum, -1);
            }

            if (dec is null)
            {
                img.Alpha = mask ? null : maskAlpha;
                return img;
            }

            var req = new PdfImageDecodeRequest(s.Codec, s.Data, (int)w, (int)h)
            {
                Globals = img.EncodedGlobals,
                ColorTransform = sem.ColorTransform,
                DeclaredBitsPerComponent = sem.BitsPerComponent,
            };
            PdfDecodedImage? res;
            try
            {
                res = dec.Decode(req, ct);
            }
            catch (PdfFormatException ex)
            {
                doc.ReportWarning(s.Codec + " decoding failed: " + ex.Message, s.ObjNum, -1);
                res = null;
            }

            if (res is null)
            {
                doc.ReportWarning(s.Codec + " data could not be decoded; the encoded data is returned.", s.ObjNum, -1);
                img.Alpha = mask ? null : maskAlpha;
                return img;
            }

            if ((long)res.Width * res.Height > lim.MaxImagePixels)
            {
                throw new PdfLimitException($"A decoded image of {res.Width}x{res.Height} pixels exceeds the limit of {lim.MaxImagePixels} pixels.") { ObjectNumber = s.ObjNum };
            }

            if (res.Width != w || res.Height != h)
            {
                doc.ReportWarning($"The {s.Codec} data is {res.Width}x{res.Height}, the dictionary says {w}x{h}; the data size is used.", s.ObjNum, -1);
                img.Width = res.Width;
                img.Height = res.Height;
            }

            raw = new Raw(res.Samples, res.Width, res.Height, res.Components, res.HasAlpha, res.BitsPerComponent, s.Codec == "JBIG2Decode");
            if (!mask)
            {
                PdfColorSpace? cs = declared;
                if (cs is not null && cs.ComponentCount != res.Components)
                {
                    doc.ReportWarning($"The image colour space has {cs.ComponentCount} components, the {s.Codec} data {res.Components}; the data's are used.", s.ObjNum, -1);
                    cs = null;
                }

                if (cs is null && s.Codec == "JPXDecode" && res.ColorSpace is { } jcs && jcs.ComponentCount == res.Components)
                {
                    cs = jcs;
                }

                cs ??= res.Components switch
                {
                    1 => PdfColorSpace.DeviceGray,
                    3 => PdfColorSpace.DeviceRgb,
                    4 => PdfColorSpace.DeviceCmyk,
                    _ => null,
                };
                if (cs is null)
                {
                    doc.ReportWarning($"A {s.Codec} image with {res.Components} components has no colour space; the encoded data is returned.", s.ObjNum, -1);
                    img.Alpha = maskAlpha;
                    return img;
                }

                declared = cs;
                if (s.Codec == "JPXDecode")
                {
                    decode = null; // ISO 32000-2 7.4.9: /Decode is ignored for JPX unless the image is a mask.
                }
            }
        }
        else
        {
            int bpc = sem.BitsPerComponent;
            if (mask)
            {
                bpc = 1;
            }
            else if (bpc is not (1 or 2 or 4 or 8 or 16))
            {
                doc.ReportWarning("Image /BitsPerComponent " + bpc + " is invalid; 8 is assumed.", s.ObjNum, -1);
                bpc = 8;
            }

            if (!mask && declared is null)
            {
                // Required entry missing: guess from the amount of data.
                declared = s.Data.Length >= w * h * 3 * bpc / 8 && w * h * 3 * bpc / 8 > 0 ? PdfColorSpace.DeviceRgb : PdfColorSpace.DeviceGray;
                doc.ReportWarning("An image has no usable /ColorSpace; " + declared + " is assumed.", s.ObjNum, -1);
            }

            raw = new Raw(s.Data, (int)w, (int)h, mask ? 1 : declared!.ComponentCount, false, bpc, false);
        }

        long need = raw.Stride * raw.Height;
        if (raw.Data.Length < need)
        {
            doc.ReportWarning($"Image data holds {raw.Data.Length} bytes, {need} needed; missing samples read as 0.", s.ObjNum, -1);
        }

        if (mask)
        {
            img.Alpha = Stencil(raw, decode);
            img.IsDecoded = true;
            return img;
        }

        PdfColorSpace space = declared!;
        long outBytes = (long)raw.Width * raw.Height * space.FinalComponentCount;
        if (outBytes > lim.MaxDecodedStreamBytes || outBytes > Array.MaxLength)
        {
            throw new PdfLimitException($"A finished image of {outBytes} bytes exceeds the decoded size limit of {lim.MaxDecodedStreamBytes}.") { ObjectNumber = s.ObjNum };
        }

        ct.ThrowIfCancellationRequested();
        byte[] samples = ConvertColor(raw, space, decode, key, sem.SMaskInData, out byte[]? chanAlpha, ct);
        img.Samples = samples;
        img.ColorSpace = space.FinalSpace;
        img.IsDecoded = true;
        if (maskAlpha is not null)
        {
            img.Alpha = maskAlpha;
            if (matte is not null)
            {
                Unmatte(samples, raw.Width, raw.Height, space, matte, maskAlpha);
            }
        }
        else if (chanAlpha is not null)
        {
            img.Alpha = new PdfImageAlpha(raw.Width, raw.Height, chanAlpha);
            if (sem.SMaskInData == 2)
            {
                UnPremultiply(samples, chanAlpha, space.FinalComponentCount);
            }
        }

        return img;
    }

    private static long AsInt(PdfObject? o) => o switch
    {
        PdfInteger i => i.Value,
        PdfReal r when double.IsFinite(r.Value) => (long)Math.Clamp(r.Value, -1e12, 1e12),
        _ => 0,
    };

    private static double[]? Numbers(PdfObject? o)
    {
        if (o is not PdfArray a)
        {
            return null;
        }

        var r = new double[a.Count];
        for (int i = 0; i < r.Length; i++)
        {
            r[i] = a[i] switch
            {
                PdfInteger n => n.Value,
                PdfReal f when double.IsFinite(f.Value) => f.Value,
                _ => 0,
            };
        }

        return r;
    }

    /// <summary>
    /// Soft mask (ISO 32000-2 11.6.5.3) or explicit stencil mask (8.9.6.3) as an alpha plane. SMask wins when both are present.
    /// </summary>
    private static PdfImageAlpha? BuildMaskPlane(Src s, PdfDictionary d, PdfObject? maskObj, out double[]? matte, CancellationToken ct)
    {
        matte = null;
        PdfDocument doc = s.Doc;
        if (d.Get("SMask") is PdfStream sm)
        {
            PdfImage? m = LoadXObject(doc, sm, s.Resources, s.Options with { PreferEncoded = false }, allowMasks: false, ct: ct);
            if (m is null || !m.IsDecoded || m.Samples.IsEmpty)
            {
                doc.ReportWarning("A soft mask could not be decoded; it is ignored.", s.ObjNum, -1);
                return null;
            }

            int comps = m.ColorSpace!.ComponentCount;
            byte[] src = m.Samples.ToArray();
            byte[] values = src;
            if (comps != 1)
            {
                values = new byte[m.Width * m.Height];
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] = src[i * comps];
                }
            }

            double[]? mt = Numbers(sm.Dictionary.Get("Matte"));
            matte = mt;
            return new PdfImageAlpha(m.Width, m.Height, values);
        }

        if (maskObj is PdfStream ms)
        {
            PdfImage? m = LoadXObject(doc, ms, s.Resources, s.Options with { PreferEncoded = false }, allowMasks: false, ct: ct);
            if (m is null || !m.IsStencil || m.Alpha is null)
            {
                doc.ReportWarning("An explicit /Mask is not a decodable stencil mask; it is ignored.", s.ObjNum, -1);
                return null;
            }

            return m.Alpha;
        }

        return null;
    }

    /// <summary>
    /// Stencil coverage (8.9.6.2): with the default /Decode [0 1] a 0 sample is painted; [1 0] paints the 1 samples.
    /// </summary>
    private static PdfImageAlpha Stencil(Raw r, double[]? decode)
    {
        bool paintOne = decode is { Length: >= 2 } && decode[0] > decode[1];
        int maxv = (1 << Math.Min(r.Bpc, 16)) - 1;
        var alpha = new byte[checked(r.Width * r.Height)];
        ReadOnlySpan<byte> data = r.Data.Span;
        long stride = r.Stride;
        int perPixel = r.Components + (r.HasAlpha ? 1 : 0);
        for (int y = 0; y < r.Height; y++)
        {
            ReadOnlySpan<byte> row = Row(data, y, stride);
            int o = y * r.Width;
            for (int x = 0; x < r.Width; x++)
            {
                int v = ReadSample(row, (long)x * perPixel, r.Bpc);
                if (r.InvertBits)
                {
                    v = maxv - v;
                }

                bool one = r.Bpc == 1 ? v != 0 : v * 2 > maxv;
                alpha[o + x] = one == paintOne ? (byte)255 : (byte)0;
            }
        }

        return new PdfImageAlpha(r.Width, r.Height, alpha);
    }

    private static ReadOnlySpan<byte> Row(ReadOnlySpan<byte> data, int y, long stride)
    {
        long start = y * stride;
        if (start >= data.Length)
        {
            return default;
        }

        return data.Slice((int)start, (int)Math.Min(stride, data.Length - start));
    }

    /// <summary>Reads sample number <paramref name="index"/> of a row; samples past the end of the data read as 0.</summary>
    private static int ReadSample(ReadOnlySpan<byte> row, long index, int bpc)
    {
        switch (bpc)
        {
            case 8:
                return index < row.Length ? row[(int)index] : 0;
            case 16:
            {
                long p = index * 2;
                if (p + 1 < row.Length)
                {
                    return (row[(int)p] << 8) | row[(int)p + 1];
                }

                return p < row.Length ? row[(int)p] << 8 : 0;
            }

            default:
            {
                long bit = index * bpc;
                long b = bit >> 3;
                if (b >= row.Length)
                {
                    return 0;
                }

                int shift = 8 - bpc - (int)(bit & 7);
                return (row[(int)b] >> shift) & ((1 << bpc) - 1);
            }
        }
    }

    private static byte Encode(double v, double min, double max)
    {
        double span = max - min;
        if (!(span > 0) || double.IsNaN(v))
        {
            return 0;
        }

        double t = (v - min) / span * 255.0;
        return (byte)Math.Clamp((int)Math.Round(t, MidpointRounding.AwayFromZero), 0, 255);
    }

    /// <summary>
    /// Unpacks samples, applies /Decode and the colour space's conversion to its final space, and encodes every
    /// component as one byte spanning its range. Colour-key masking (8.9.6.4) and a JPX opacity channel are returned as
    /// alpha when present.
    /// </summary>
    private static byte[] ConvertColor(Raw r, PdfColorSpace cs, double[]? decodeIn, int[]? key, int smaskInData, out byte[]? alpha, CancellationToken ct)
    {
        int n = cs.ComponentCount;
        PdfColorSpace fin = cs.FinalSpace;
        int fn = fin.ComponentCount;
        int bpc = r.Bpc;
        int maxv = (1 << Math.Min(bpc, 16)) - 1;
        // Samples of 16 bits index the lookup tables by their high byte.
        int lutBits = Math.Min(bpc, 8);
        int levels = 1 << lutBits;
        int shift = bpc > 8 ? bpc - 8 : 0;

        double[] decode = new double[n * 2];
        if (decodeIn is not null && decodeIn.Length >= n * 2)
        {
            Array.Copy(decodeIn, decode, n * 2);
        }
        else
        {
            cs.GetDefaultDecode(bpc, decode);
        }

        double lutMax = levels - 1;
        bool indexed = cs.Kind == PdfColorSpaceKind.Indexed;
        byte[]? alphaOut = null;
        bool useChannelAlpha = r.HasAlpha && smaskInData != 0;
        if (key is not null && key.Length >= n * 2)
        {
            alphaOut = new byte[r.Width * r.Height];
        }
        else
        {
            key = null;
            if (useChannelAlpha)
            {
                alphaOut = new byte[r.Width * r.Height];
            }
        }

        var output = new byte[r.Width * r.Height * fn];
        ReadOnlySpan<byte> data = r.Data.Span;
        long stride = r.Stride;
        int perPixel = n + (r.HasAlpha ? 1 : 0);
        int W = r.Width;

        // Lookup tables from raw sample (or its high byte) to final bytes.
        byte[]? perComp = null;   // direct spaces: [component * levels + sample]
        byte[]? palette = null;   // one-component spaces: [sample * fn + k]
        if (cs.IsFinal && !indexed)
        {
            perComp = new byte[n * levels];
            for (int c = 0; c < n; c++)
            {
                (double min, double max) = cs.GetComponentRange(c);
                double d0 = decode[c * 2], d1 = decode[c * 2 + 1];
                for (int v = 0; v < levels; v++)
                {
                    perComp[c * levels + v] = Encode(d0 + v * (d1 - d0) / lutMax, min, max);
                }
            }
        }
        else if (n == 1)
        {
            palette = new byte[levels * fn];
            Span<double> comp = stackalloc double[1];
            Span<double> f = stackalloc double[Math.Max(fn, 1)];
            for (int v = 0; v < levels; v++)
            {
                double x = decode[0] + v * (decode[1] - decode[0]) / lutMax;
                comp[0] = indexed ? Math.Round(x) : x;
                cs.ToFinalCore(comp, f[..fn]);
                for (int k = 0; k < fn; k++)
                {
                    (double min, double max) = fin.GetComponentRange(k);
                    palette[v * fn + k] = Encode(f[k], min, max);
                }
            }
        }

        double[] comps = new double[n];
        double[] fbuf = new double[fn];
        int[] last = new int[n];
        bool haveLast = false;
        byte[] lastOut = new byte[fn];
        int[] rawv = new int[n];
        for (int y = 0; y < r.Height; y++)
        {
            if ((y & 255) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            ReadOnlySpan<byte> row = Row(data, y, stride);
            for (int x = 0; x < W; x++)
            {
                long baseIdx = (long)x * perPixel;
                int o = (y * W + x) * fn;
                bool masked = key is not null;
                for (int c = 0; c < n; c++)
                {
                    int v = ReadSample(row, baseIdx + c, bpc);
                    if (r.InvertBits)
                    {
                        v = maxv - v;
                    }

                    rawv[c] = v;
                    if (key is not null && (v < key[c * 2] || v > key[c * 2 + 1]))
                    {
                        masked = false;
                    }
                }

                if (perComp is not null)
                {
                    for (int c = 0; c < n; c++)
                    {
                        output[o + c] = perComp[c * levels + (rawv[c] >> shift)];
                    }
                }
                else if (palette is not null)
                {
                    Array.Copy(palette, (rawv[0] >> shift) * fn, output, o, fn);
                }
                else
                {
                    // DeviceN with several inputs: tint transforms are costly, so repeated pixels reuse the last result.
                    bool same = haveLast;
                    for (int c = 0; c < n && same; c++)
                    {
                        same = last[c] == rawv[c];
                    }

                    if (!same)
                    {
                        for (int c = 0; c < n; c++)
                        {
                            last[c] = rawv[c];
                            comps[c] = decode[c * 2] + (rawv[c] >> shift) * (decode[c * 2 + 1] - decode[c * 2]) / lutMax;
                        }

                        cs.ToFinalCore(comps, fbuf);
                        for (int k = 0; k < fn; k++)
                        {
                            (double min, double max) = fin.GetComponentRange(k);
                            lastOut[k] = Encode(fbuf[k], min, max);
                        }

                        haveLast = true;
                    }

                    Array.Copy(lastOut, 0, output, o, fn);
                }

                if (alphaOut is not null)
                {
                    if (key is not null)
                    {
                        alphaOut[y * W + x] = masked ? (byte)0 : (byte)255;
                    }
                    else
                    {
                        int a = ReadSample(row, baseIdx + n, bpc);
                        alphaOut[y * W + x] = (byte)(bpc > 8 ? a >> shift : a * 255 / maxv);
                    }
                }
            }
        }

        alpha = alphaOut;
        return output;
    }

    /// <summary>
    /// Undoes premultiplication by the soft mask's matte colour (11.6.5.3): c = m + (c' − m) / α, with the matte given in the
    /// image's colour space and converted here to the final space. Only spaces without a lookup can be un-matted.
    /// </summary>
    private static void Unmatte(byte[] samples, int w, int h, PdfColorSpace cs, double[] matte, PdfImageAlpha a)
    {
        if (cs.Kind is PdfColorSpaceKind.Indexed || matte.Length < cs.ComponentCount)
        {
            return;
        }

        PdfColorSpace fin = cs.FinalSpace;
        int fn = fin.ComponentCount;
        var mf = new double[fn];
        cs.ToFinalCore(matte, mf);
        var m = new int[fn];
        for (int k = 0; k < fn; k++)
        {
            (double min, double max) = fin.GetComponentRange(k);
            m[k] = Encode(mf[k], min, max);
        }

        ReadOnlySpan<byte> av = a.Values.Span;
        for (int y = 0; y < h; y++)
        {
            int ay = (int)((long)y * a.Height / h);
            for (int x = 0; x < w; x++)
            {
                int ax = (int)((long)x * a.Width / w);
                int al = av[ay * a.Width + ax];
                int o = (y * w + x) * fn;
                for (int k = 0; k < fn; k++)
                {
                    int c = al == 0 ? m[k] : m[k] + (samples[o + k] - m[k]) * 255 / al;
                    samples[o + k] = (byte)Math.Clamp(c, 0, 255);
                }
            }
        }
    }

    private static void UnPremultiply(byte[] samples, byte[] alpha, int fn)
    {
        for (int i = 0; i < alpha.Length; i++)
        {
            int al = alpha[i];
            if (al == 0 || al == 255)
            {
                continue;
            }

            for (int k = 0; k < fn; k++)
            {
                samples[i * fn + k] = (byte)Math.Min(255, samples[i * fn + k] * 255 / al);
            }
        }
    }
}
