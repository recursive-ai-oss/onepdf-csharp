using OnePdf.Forms;
using System.Buffers.Binary;
using OnePdf.Content;

namespace OnePdf.Redaction;

/// <summary>
/// Blanks the samples of an image that lie under redacted areas and stores the result as a new Flate-compressed image, so the
/// original bytes (JPEG, JPEG 2000, JBIG2 or anything else) do not survive. The samples are blanked in the stored sample
/// domain, so the colour space, <c>/Decode</c> and bit depth stay exactly as the file says them.
/// </summary>
internal static class ImageRedactor
{
    private const double Eps = 1e-9;
    private const long MaxGeneralCells = 1L << 22;

    /// <summary>Keys copied from the source image dictionary; everything else (alternates, OPI, metadata, thumbnails) is dropped.</summary>
    private static readonly string[] KeptKeys =
    [
        "Type", "Subtype", "Intent", "Interpolate", "OC", "StructParent", "Name",
    ];

    internal enum Outcome
    {
        Unchanged,
        Replaced,
        Removed,
    }

    internal readonly record struct Result(Outcome Outcome, PdfReference? Reference, string? Note, int Blanked);

    private sealed class Plane
    {
        public byte[] Data = [];
        public int W;
        public int H;
        public int Comps;
        public int Bpc;
        public bool Stencil;
        public bool Codec;
        public double[]? Decode;
        public PdfObject? ColorSpace;
        public int Stride => (int)((((long)W * Comps * Bpc) + 7) / 8);
    }

    /// <summary>
    /// Redacts one image drawn with <paramref name="ctm"/> (unit square to device space) against device-space rectangles.
    /// </summary>
    public static Result Redact(PdfDocument doc, PdfStream image, PdfDictionary? resources, PdfMatrix ctm, IReadOnlyList<PdfBounds> rects, PdfImageDecoderRegistry decoders, CancellationToken ct)
    {
        PdfMatrix? inv = Invert(ctm);
        if (inv is null)
        {
            return new Result(Outcome.Unchanged, null, null, 0);
        }

        PdfMatrix m = inv.Value;
        bool aligned = (Math.Abs(ctm.B) < 1e-9 && Math.Abs(ctm.C) < 1e-9) || (Math.Abs(ctm.A) < 1e-9 && Math.Abs(ctm.D) < 1e-9);
        var polys = new List<double[]>();
        foreach (PdfBounds r in rects)
        {
            double[] p = new double[8];
            (p[0], p[1]) = m.Transform(r.Left, r.Bottom);
            (p[2], p[3]) = m.Transform(r.Right, r.Bottom);
            (p[4], p[5]) = m.Transform(r.Right, r.Top);
            (p[6], p[7]) = m.Transform(r.Left, r.Top);
            polys.Add(p);
        }

        PdfDictionary d = image.Dictionary;
        Plane? main = Load(doc, image, resources, decoders, ct);
        if (main is null)
        {
            return new Result(Outcome.Removed, null, "the image could not be decoded", 0);
        }

        int blanked = Blank(main, polys, aligned, BlankValue(main, forMask: false), ct);

        // The coverage planes: a soft mask, or an explicit stencil mask (a colour-key mask is an array and needs no change).
        PdfStream? smask = d.GetStream("SMask");
        PdfStream? hard = d.Get("Mask") as PdfStream;
        Plane? smaskPlane = null, hardPlane = null;
        int blankedMask = 0;
        if (smask is not null)
        {
            smaskPlane = Load(doc, smask, resources, decoders, ct);
            if (smaskPlane is null)
            {
                return new Result(Outcome.Removed, null, "its soft mask could not be decoded", 0);
            }

            blankedMask += Blank(smaskPlane, polys, aligned, BlankValue(smaskPlane, forMask: true), ct);
        }

        if (hard is not null)
        {
            hardPlane = Load(doc, hard, resources, decoders, ct);
            if (hardPlane is null)
            {
                return new Result(Outcome.Removed, null, "its mask could not be decoded", 0);
            }

            blankedMask += Blank(hardPlane, polys, aligned, BlankValue(hardPlane, forMask: true), ct);
        }

        if (blanked == 0 && blankedMask == 0)
        {
            // The rectangles only brush the image's bounding box; nothing is under them. Stored bytes stay as they are.
            return new Result(Outcome.Unchanged, null, null, 0);
        }

        PdfStream result = Build(doc, image, main);
        PdfDictionary rd = result.Dictionary;
        if (smaskPlane is not null)
        {
            rd.SetEntry("SMask", doc.AddObject(Build(doc, smask!, smaskPlane)));
        }

        if (hardPlane is not null)
        {
            rd.SetEntry("Mask", doc.AddObject(Build(doc, hard!, hardPlane)));
        }
        else if (d.Get("Mask") is PdfArray key && !main.Stencil)
        {
            rd.SetEntry("Mask", FormFiller.Clone(doc, key));
        }

        if (main.Codec ? false : d.ContainsKey("SMaskInData"))
        {
            rd.SetEntry("SMaskInData", new PdfInteger(d.GetInt("SMaskInData")));
        }

        return new Result(Outcome.Replaced, doc.AddObject(result), null, blanked + blankedMask);
    }

    private static PdfMatrix? Invert(PdfMatrix m)
    {
        double det = m.Determinant;
        if (!(Math.Abs(det) > 1e-12) || !double.IsFinite(det))
        {
            return null;
        }

        double a = m.D / det, b = -m.B / det, c = -m.C / det, dd = m.A / det;
        double e = ((m.C * m.F) - (m.D * m.E)) / det;
        double f = ((m.B * m.E) - (m.A * m.F)) / det;
        var r = new PdfMatrix(a, b, c, dd, e, f);
        return r.IsFinite ? r : null;
    }

    // ---- loading ----

    private static Plane? Load(PdfDocument doc, PdfStream s, PdfDictionary? resources, PdfImageDecoderRegistry decoders, CancellationToken ct)
    {
        PdfDictionary d = s.Dictionary;
        long w = d.GetInt("Width"), h = d.GetInt("Height");
        if (w < 1 || h < 1 || w > int.MaxValue || h > int.MaxValue)
        {
            return null;
        }

        if (w * h > doc.Limits.MaxImagePixels)
        {
            throw new PdfLimitException($"An image of {w}x{h} pixels exceeds the limit of {doc.Limits.MaxImagePixels} pixels.");
        }

        bool stencil = d.Get("ImageMask") is PdfBoolean { Value: true };
        double[]? decode = Numbers(d.Get("Decode"));
        string? codec = s.UndecodedFilter;
        ReadOnlyMemory<byte> data = s.GetDecodedBytes(ct);
        if (codec is null)
        {
            int bpc = stencil ? 1 : (int)Math.Clamp(d.GetInt("BitsPerComponent"), 0, 64);
            if (bpc is not (1 or 2 or 4 or 8 or 16))
            {
                return null;
            }

            int comps = 1;
            if (!stencil)
            {
                PdfColorSpace? cs = ColorSpaceResolver.Resolve(doc, d.Get("ColorSpace"), resources);
                if (cs is null || cs.Kind == PdfColorSpaceKind.Pattern)
                {
                    return null;
                }

                comps = cs.ComponentCount;
            }

            var p = new Plane { W = (int)w, H = (int)h, Comps = comps, Bpc = bpc, Stencil = stencil, Decode = decode };
            long need = (long)p.Stride * p.H;
            if (need > doc.Limits.MaxDecodedStreamBytes || need > Array.MaxLength)
            {
                throw new PdfLimitException($"An image of {need} bytes exceeds the decoded size limit.");
            }

            p.Data = new byte[need];
            data.Span[..(int)Math.Min(need, data.Length)].CopyTo(p.Data);
            return p;
        }

        if (!decoders.TryGet(codec, out IPdfImageDecoder? dec) || dec is null)
        {
            return null;
        }

        IReadOnlyList<string> names = s.FilterNames;
        int idx = -1;
        for (int i = 0; i < names.Count; i++)
        {
            if (names[i] == codec)
            {
                idx = i;
                break;
            }
        }

        PdfDictionary? parms = ParmsAt(d, idx);
        int? transform = codec == "DCTDecode" && parms is not null && parms.GetInt("ColorTransform", -1) is 0 or 1 ? (int)parms.GetInt("ColorTransform") : null;
        ReadOnlyMemory<byte> globals = codec == "JBIG2Decode" && parms?.Get("JBIG2Globals") is PdfStream gs ? gs.GetDecodedBytes(ct) : default;
        var req = new PdfImageDecodeRequest(codec, data, (int)w, (int)h)
        {
            Globals = globals,
            ColorTransform = transform,
            DeclaredBitsPerComponent = (int)Math.Clamp(d.GetInt("BitsPerComponent"), 0, 64),
        };
        PdfDecodedImage? res;
        try
        {
            res = dec.Decode(req, ct);
        }
        catch (PdfFormatException)
        {
            return null;
        }

        if (res is null || (long)res.Width * res.Height > doc.Limits.MaxImagePixels)
        {
            return null;
        }

        int rc = res.Components - (res.HasAlpha ? 1 : 0);
        int rb = res.BitsPerComponent;
        if (rc < 1 || rb is not (1 or 2 or 4 or 8 or 16) || (res.HasAlpha && rb < 8))
        {
            return null;
        }

        var plane = new Plane { W = res.Width, H = res.Height, Comps = rc, Bpc = rb, Stencil = stencil, Codec = true, Decode = decode };
        long total = (long)plane.Stride * plane.H;
        if (total > doc.Limits.MaxDecodedStreamBytes || total > Array.MaxLength)
        {
            throw new PdfLimitException($"An image of {total} bytes exceeds the decoded size limit.");
        }

        plane.Data = new byte[total];
        ReadOnlySpan<byte> src = res.Samples.Span;
        if (res.HasAlpha)
        {
            // Drop the opacity channel (last): the redacted copy is opaque; the dictionary's own masks carry any transparency.
            int bytesPer = rb / 8;
            int srcPixel = res.Components * bytesPer, dstPixel = rc * bytesPer;
            long srcStride = (long)res.Width * srcPixel;
            for (int y = 0; y < res.Height; y++)
            {
                for (int x = 0; x < res.Width; x++)
                {
                    long so = (y * srcStride) + ((long)x * srcPixel);
                    if (so + dstPixel <= src.Length)
                    {
                        src.Slice((int)so, dstPixel).CopyTo(plane.Data.AsSpan((y * plane.Stride) + (x * dstPixel), dstPixel));
                    }
                }
            }
        }
        else
        {
            src[..(int)Math.Min(total, src.Length)].CopyTo(plane.Data);
        }

        if (codec == "JBIG2Decode")
        {
            // JBIG2 reports 1 as black; PDF stores 0 as black (ISO 32000-2 7.4.7).
            for (int i = 0; i < plane.Data.Length; i++)
            {
                plane.Data[i] ^= 0xFF;
            }
        }

        if (!stencil)
        {
            PdfColorSpace? declared = ColorSpaceResolver.Resolve(doc, d.Get("ColorSpace"), resources);
            if (declared is not null && declared.Kind != PdfColorSpaceKind.Pattern && declared.ComponentCount == rc)
            {
                plane.ColorSpace = d.Get("ColorSpace");
            }
            else
            {
                plane.ColorSpace = rc switch
                {
                    1 => new PdfName("DeviceGray"),
                    3 => new PdfName("DeviceRGB"),
                    4 => new PdfName("DeviceCMYK"),
                    _ => null,
                };
                if (plane.ColorSpace is null)
                {
                    return null;
                }
            }
        }

        if (codec == "JPXDecode")
        {
            plane.Decode = null;
        }

        return plane;
    }

    private static PdfDictionary? ParmsAt(PdfDictionary d, int index)
    {
        PdfObject? p = d.Get("DecodeParms") ?? d.Get("DP");
        if (p is PdfDictionary pd && index <= 0)
        {
            return pd;
        }

        return p is PdfArray a && index >= 0 && index < a.Count ? a[index] as PdfDictionary : null;
    }

    private static double[]? Numbers(PdfObject? o)
    {
        if (o is not PdfArray a || a.Count == 0 || a.Count > 64)
        {
            return null;
        }

        double[] r = new double[a.Count];
        for (int i = 0; i < r.Length; i++)
        {
            r[i] = a.GetDouble(i);
        }

        return r;
    }

    // The sample value that shows nothing: an unpainted stencil, a fully transparent mask, and zero for colour (black, or white in CMYK).
    private static int BlankValue(Plane p, bool forMask)
    {
        int max = (1 << p.Bpc) - 1;
        bool inverted = p.Decode is { Length: >= 2 } dc && dc[0] > dc[1];
        if (p.Stencil)
        {
            return inverted ? 0 : max;
        }

        if (forMask)
        {
            return inverted ? max : 0;
        }

        return 0;
    }

    // ---- blanking ----

    private static int Blank(Plane p, List<double[]> polys, bool aligned, int value, CancellationToken ct)
    {
        int count = 0;
        int stride = p.Stride;
        foreach (double[] poly in polys)
        {
            double u0 = Math.Min(Math.Min(poly[0], poly[2]), Math.Min(poly[4], poly[6]));
            double u1 = Math.Max(Math.Max(poly[0], poly[2]), Math.Max(poly[4], poly[6]));
            double v0 = Math.Min(Math.Min(poly[1], poly[3]), Math.Min(poly[5], poly[7]));
            double v1 = Math.Max(Math.Max(poly[1], poly[3]), Math.Max(poly[5], poly[7]));
            if (u1 <= Eps || u0 >= 1 - Eps || v1 <= Eps || v0 >= 1 - Eps)
            {
                continue;
            }

            int i0 = (int)Math.Clamp(Math.Floor((u0 * p.W) + Eps), 0, p.W - 1);
            int i1 = (int)Math.Clamp(Math.Ceiling((u1 * p.W) - Eps) - 1, 0, p.W - 1);
            // Row j covers v from 1 - (j+1)/H to 1 - j/H: rows count from the top.
            int j0 = (int)Math.Clamp(Math.Floor(((1 - v1) * p.H) + Eps), 0, p.H - 1);
            int j1 = (int)Math.Clamp(Math.Ceiling(((1 - v0) * p.H) - Eps) - 1, 0, p.H - 1);
            if (i1 < i0 || j1 < j0)
            {
                continue;
            }

            bool everyCell = aligned || ((long)(i1 - i0 + 1) * (j1 - j0 + 1)) > MaxGeneralCells;
            for (int j = j0; j <= j1; j++)
            {
                if ((j & 255) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                int rowOff = j * stride;
                double cv0 = 1 - ((j + 1) / (double)p.H), cv1 = 1 - (j / (double)p.H);
                for (int i = i0; i <= i1; i++)
                {
                    if (!everyCell && !CellHits(poly, i / (double)p.W, cv0, (i + 1) / (double)p.W, cv1))
                    {
                        continue;
                    }

                    SetPixel(p, rowOff, i, value);
                    count++;
                }
            }
        }

        return count;
    }

    private static void SetPixel(Plane p, int rowOff, int x, int value)
    {
        int bpc = p.Bpc;
        if (bpc >= 8)
        {
            int bytes = bpc / 8;
            for (int k = 0; k < p.Comps; k++)
            {
                int o = rowOff + (((x * p.Comps) + k) * bytes);
                if (bytes == 1)
                {
                    p.Data[o] = (byte)value;
                }
                else
                {
                    BinaryPrimitives.WriteUInt16BigEndian(p.Data.AsSpan(o, 2), (ushort)value);
                }
            }

            return;
        }

        for (int k = 0; k < p.Comps; k++)
        {
            long bit = ((long)((x * p.Comps) + k)) * bpc;
            int o = rowOff + (int)(bit >> 3);
            int shift = 8 - bpc - (int)(bit & 7);
            int mask = ((1 << bpc) - 1) << shift;
            p.Data[o] = (byte)((p.Data[o] & ~mask) | ((value << shift) & mask));
        }
    }

    // Separating-axis test of the convex quadrilateral against an axis-aligned cell; cells that only touch the quadrilateral do not count.
    private static bool CellHits(double[] q, double x0, double y0, double x1, double y1)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            for (int k = 0; k < 4; k++)
            {
                double v = q[(2 * k) + pass];
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }

            double lo = pass == 0 ? x0 : y0, hi = pass == 0 ? x1 : y1;
            if (max <= lo + Eps || min >= hi - Eps)
            {
                return false;
            }
        }

        for (int k = 0; k < 4; k++)
        {
            int n = (k + 1) & 3;
            double ex = q[(2 * n)] - q[2 * k], ey = q[(2 * n) + 1] - q[(2 * k) + 1];
            double nx = -ey, ny = ex;
            double len = Math.Sqrt((nx * nx) + (ny * ny));
            if (len < 1e-18)
            {
                continue;
            }

            nx /= len;
            ny /= len;
            double qmin = double.PositiveInfinity, qmax = double.NegativeInfinity;
            for (int j = 0; j < 4; j++)
            {
                double v = (q[2 * j] * nx) + (q[(2 * j) + 1] * ny);
                qmin = Math.Min(qmin, v);
                qmax = Math.Max(qmax, v);
            }

            double cmin = double.PositiveInfinity, cmax = double.NegativeInfinity;
            for (int c = 0; c < 4; c++)
            {
                double cx = (c & 1) == 0 ? x0 : x1, cy = (c & 2) == 0 ? y0 : y1;
                double v = (cx * nx) + (cy * ny);
                cmin = Math.Min(cmin, v);
                cmax = Math.Max(cmax, v);
            }

            if (qmax <= cmin + Eps || cmax <= qmin + Eps)
            {
                return false;
            }
        }

        return true;
    }

    // ---- building ----

    private static PdfStream Build(PdfDocument doc, PdfStream original, Plane p)
    {
        PdfStream ns = doc.CreateStream(p.Data, compress: true);
        PdfDictionary nd = ns.Dictionary;
        PdfDictionary od = original.Dictionary;
        foreach (string k in KeptKeys)
        {
            if (od.GetRaw(k) is { } raw)
            {
                nd.SetEntry(k, FormFiller.Clone(doc, raw));
            }
        }

        nd.SetEntry("Type", new PdfName("XObject"));
        nd.SetEntry("Subtype", new PdfName("Image"));
        nd.SetEntry("Width", new PdfInteger(p.W));
        nd.SetEntry("Height", new PdfInteger(p.H));
        nd.SetEntry("BitsPerComponent", new PdfInteger(p.Bpc));
        if (p.Stencil)
        {
            nd.SetEntry("ImageMask", new PdfBoolean(true));
        }
        else
        {
            PdfObject? cs = p.Codec ? p.ColorSpace : od.GetRaw("ColorSpace");
            if (cs is not null)
            {
                nd.SetEntry("ColorSpace", FormFiller.Clone(doc, cs));
            }
        }

        if (p.Decode is { } dc && od.GetRaw("Decode") is { } rawDecode)
        {
            _ = dc;
            nd.SetEntry("Decode", FormFiller.Clone(doc, rawDecode));
        }

        if (od.GetRaw("Matte") is { } matte)
        {
            nd.SetEntry("Matte", FormFiller.Clone(doc, matte));
        }

        return ns;
    }
}
