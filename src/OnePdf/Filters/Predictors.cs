using System;
using System.Numerics;

namespace OnePdf.Filters;

/// <summary>PNG and TIFF predictors, undone in place (ISO 32000-2 7.4.4.4).</summary>
internal static class Predictors
{
    // Rows longer than this are rejected as corrupt parameters; it also keeps every bit offset inside an int.
    private const long MaxRowBytes = 1 << 26;

    /// <summary>
    /// Reverses the predictor on <paramref name="data"/>[0..length) in place and returns the new length (PNG rows lose their tag byte).
    /// Returns -1 when the parameters are unusable; the data is then untouched.
    /// </summary>
    public static int Undo(byte[] data, int length, FilterParameters p, ref FilterIssues issues, ref string? message)
    {
        if (p.Predictor <= 1)
        {
            return length;
        }

        bool png = p.Predictor >= 10 && p.Predictor <= 15;
        if (!png && p.Predictor != 2)
        {
            return Bad(ref issues, ref message, "unknown predictor " + p.Predictor);
        }

        if (p.Colors < 1 || p.Colors > 255 || p.Columns < 1 || p.BitsPerComponent is not (1 or 2 or 4 or 8 or 16))
        {
            return Bad(ref issues, ref message, "invalid Colors, Columns or BitsPerComponent");
        }

        long rowBits = (long)p.Colors * p.BitsPerComponent * p.Columns;
        long rowBytesL = (rowBits + 7) / 8;
        if (rowBytesL > MaxRowBytes)
        {
            return Bad(ref issues, ref message, "row is too long");
        }

        int rowBytes = (int)rowBytesL;
        if (png)
        {
            int bpp = Math.Max(1, ((p.Colors * p.BitsPerComponent) + 7) / 8);
            return Png(data, length, rowBytes, bpp, ref issues, ref message);
        }

        Tiff(data, length, rowBytes, p.Colors, p.BitsPerComponent, p.Columns);
        return length;
    }

    private static int Bad(ref FilterIssues issues, ref string? message, string why)
    {
        issues |= FilterIssues.BadParameters;
        CompressedFilters.Note(ref message, "Predictor: " + why + "; predictor not applied");
        return -1;
    }

    private static int Png(byte[] buf, int length, int rowBytes, int bpp, ref FilterIssues issues, ref string? message)
    {
        int rowIn = rowBytes + 1;
        int outPos = 0;
        bool badTag = false;
        bool partial = false;
        for (int inPos = 0; inPos < length; inPos += rowIn)
        {
            int avail = Math.Min(rowIn, length - inPos);
            if (avail < 2)
            {
                // A lone tag byte carries no data.
                partial = true;
                break;
            }

            if (avail < rowIn)
            {
                partial = true;
            }

            int tag = buf[inPos];
            int n = avail - 1;
            int src = inPos + 1;
            int dst = outPos;
            int prev = outPos - rowBytes; // start of the previous decoded row; negative on the first row

            // The write position never passes the read position, so decoding in place is safe. The spans overlap (the output
            // row sits a little before the input row); every loop below reads a byte or a vector before it writes the
            // bytes that could alias it, going forward only.
            Span<byte> d = buf.AsSpan(dst, n);
            ReadOnlySpan<byte> s = buf.AsSpan(src, n);
            switch (tag)
            {
                case 0:
                    s.CopyTo(d);
                    break;
                case 1:
                    Sub(d, s, bpp);
                    break;
                case 2:
                    if (prev < 0)
                    {
                        s.CopyTo(d);
                    }
                    else
                    {
                        Up(d, s, buf.AsSpan(prev, n));
                    }

                    break;
                case 3:
                    Average(d, s, prev < 0 ? default : buf.AsSpan(prev, n), bpp);
                    break;
                case 4:
                    Paeth(d, s, prev < 0 ? default : buf.AsSpan(prev, n), bpp);
                    break;
                default:
                    // Unknown tag: keep the row as it is rather than lose it.
                    badTag = true;
                    s.CopyTo(d);
                    break;
            }

            outPos += n;
        }

        if (badTag)
        {
            issues |= FilterIssues.DataError;
            CompressedFilters.Note(ref message, "Predictor: unknown PNG row filter tag; row kept unfiltered");
        }

        if (partial)
        {
            issues |= FilterIssues.Truncated;
            CompressedFilters.Note(ref message, "Predictor: last row is incomplete");
        }

        return outPos;
    }

    // ISO 32000-2 7.4.4.4 / PNG filter types. Sums wrap modulo 256.
    private static void Sub(Span<byte> d, ReadOnlySpan<byte> s, int bpp)
    {
        int n = d.Length;
        s = s.Slice(0, n);
        int lead = Math.Min(bpp, n);
        s.Slice(0, lead).CopyTo(d);
        if (bpp == 1)
        {
            byte a = n > 0 ? d[0] : (byte)0;
            for (int i = 1; i < d.Length; i++)
            {
                a = (byte)(s[i] + a);
                d[i] = a;
            }

            return;
        }

        for (int i = bpp; i < d.Length; i++)
        {
            d[i] = (byte)(s[i] + d[i - bpp]);
        }
    }

    private static void Up(Span<byte> d, ReadOnlySpan<byte> s, ReadOnlySpan<byte> up)
    {
        int n = d.Length;
        s = s.Slice(0, n);
        up = up.Slice(0, n);
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int w = Vector<byte>.Count;
            for (; i <= n - w; i += w)
            {
                (new Vector<byte>(s.Slice(i)) + new Vector<byte>(up.Slice(i))).CopyTo(d.Slice(i));
            }
        }

        for (; i < n; i++)
        {
            d[i] = (byte)(s[i] + up[i]);
        }
    }

    private static void Average(Span<byte> d, ReadOnlySpan<byte> s, ReadOnlySpan<byte> up, int bpp)
    {
        int n = d.Length;
        s = s.Slice(0, n);
        if (up.IsEmpty)
        {
            // First row: the row above is all zero.
            for (int i = 0; i < n; i++)
            {
                int a = i >= bpp ? d[i - bpp] : 0;
                d[i] = (byte)(s[i] + (a >> 1));
            }

            return;
        }

        up = up.Slice(0, n);
        int lead = Math.Min(bpp, n);
        for (int i = 0; i < lead; i++)
        {
            d[i] = (byte)(s[i] + (up[i] >> 1));
        }

        if (bpp == 1)
        {
            // The left neighbour stays in a register: no store-to-load round trip on the serial chain.
            int a = n > 0 ? d[0] : 0;
            for (int i = 1; i < n; i++)
            {
                a = (byte)(s[i] + ((a + up[i]) >> 1));
                d[i] = (byte)a;
            }

            return;
        }

        for (int i = bpp; i < n; i++)
        {
            d[i] = (byte)(s[i] + ((d[i - bpp] + up[i]) >> 1));
        }
    }

    private static void Paeth(Span<byte> d, ReadOnlySpan<byte> s, ReadOnlySpan<byte> up, int bpp)
    {
        int n = d.Length;
        s = s.Slice(0, n);
        if (up.IsEmpty)
        {
            // First row: b and c are zero, so the predictor is always a.
            Sub(d, s, bpp);
            return;
        }

        up = up.Slice(0, n);
        int lead = Math.Min(bpp, n);
        for (int i = 0; i < lead; i++)
        {
            // a and c are zero, so the predictor is b.
            d[i] = (byte)(s[i] + up[i]);
        }

        if (bpp == 1)
        {
            int left = n > 0 ? d[0] : 0;
            int upLeft = n > 0 ? up[0] : 0;
            for (int i = 1; i < n; i++)
            {
                int b = up[i];
                left = (byte)(s[i] + PaethPredictor(left, b, upLeft));
                upLeft = b;
                d[i] = (byte)left;
            }

            return;
        }

        for (int i = bpp; i < n; i++)
        {
            d[i] = (byte)(s[i] + PaethPredictor(d[i - bpp], up[i], up[i - bpp]));
        }
    }

    // The Paeth predictor without branches: the three comparisons become masks, because on real image data the branch is
    // unpredictable and mispredictions cost far more than the arithmetic. Same choice as the specification's order:
    // a, then b, then c on ties.
    private static int PaethPredictor(int a, int b, int c)
    {
        int pa = Math.Abs(b - c);
        int pb = Math.Abs(a - c);
        int pc = Math.Abs(a + b - (2 * c));
        int pickA = ~(((pb - pa) | (pc - pa)) >> 31); // all ones when pa <= pb && pa <= pc
        int pickB = ~((pc - pb) >> 31);               // all ones when pb <= pc
        return (a & pickA) | (~pickA & ((b & pickB) | (c & ~pickB)));
    }

    private static void Tiff(byte[] buf, int length, int rowBytes, int colors, int bpc, int columns)
    {
        for (int row = 0; row < length; row += rowBytes)
        {
            int n = Math.Min(rowBytes, length - row);
            Span<byte> r = buf.AsSpan(row, n);
            switch (bpc)
            {
                case 8:
                    for (int i = colors; i < r.Length; i++)
                    {
                        r[i] = (byte)(r[i] + r[i - colors]);
                    }

                    break;
                case 16:
                    {
                        int step = colors * 2;
                        for (int i = step; i + 1 < r.Length; i += 2)
                        {
                            int v = ((r[i] << 8) | r[i + 1]) + ((r[i - step] << 8) | r[i - step + 1]);
                            r[i] = (byte)(v >> 8);
                            r[i + 1] = (byte)v;
                        }

                        break;
                    }

                default:
                    {
                        // 1, 2 or 4 bits per component: samples are packed most significant bit first, rows start on a byte.
                        int mask = (1 << bpc) - 1;
                        int samples = (int)Math.Min((long)columns * colors, ((long)n * 8) / bpc);
                        for (int k = colors; k < samples; k++)
                        {
                            int cur = GetSample(r, k, bpc, mask) + GetSample(r, k - colors, bpc, mask);
                            SetSample(r, k, bpc, mask, cur & mask);
                        }

                        break;
                    }
            }
        }
    }

    private static int GetSample(Span<byte> r, int k, int bpc, int mask)
    {
        int bit = k * bpc;
        return (r[bit >> 3] >> (8 - bpc - (bit & 7))) & mask;
    }

    private static void SetSample(Span<byte> r, int k, int bpc, int mask, int v)
    {
        int bit = k * bpc;
        int shift = 8 - bpc - (bit & 7);
        r[bit >> 3] = (byte)((r[bit >> 3] & ~(mask << shift)) | (v << shift));
    }
}
