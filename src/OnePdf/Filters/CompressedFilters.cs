using System;
using System.Threading;

namespace OnePdf.Filters;

/// <summary>FlateDecode and LZWDecode drivers (ISO 32000-2 7.4.4): lenient decoding into an <see cref="OutputBuffer"/>.</summary>
internal static class CompressedFilters
{
    // Output is requested from the codecs in slices so cancellation is observed and the cap is enforced as it runs.
    private const int Slice = 1 << 20;

    [ThreadStatic]
    private static Inflater? t_inflater;

    [ThreadStatic]
    private static LzwDecoder? t_lzw;

    public static void Flate(ReadOnlySpan<byte> input, in DecodeLimits limits, OutputBuffer output, ref FilterIssues issues, ref string? message, CancellationToken ct)
    {
        if (input.IsEmpty)
        {
            return;
        }

        bool zlib = SniffZlibHeader(input, out int skip, out bool badHeader);
        if (badHeader)
        {
            issues |= FilterIssues.BadHeader;
            Note(ref message, "Flate: missing or invalid zlib header");
        }

        ReadOnlySpan<byte> rest = input.Slice(skip);
        Inflater inf = t_inflater ?? new Inflater(zlib, limits.MaxDecodedBytes, limits.MaxExpansionRatio, limits.RatioAppliesAfterBytes);
        t_inflater = null;
        try
        {
            inf.Reset(zlib, limits.MaxDecodedBytes, limits.MaxExpansionRatio, limits.RatioAppliesAfterBytes);
            int pos = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Span<byte> free = output.Free(Slice);
                if (free.Length > Slice)
                {
                    free = free.Slice(0, Slice);
                }

                InflateStatus st;
                int consumed = 0;
                int written = 0;
                try
                {
                    st = inf.Inflate(rest.Slice(pos), free, out consumed, out written);
                }
                catch (FilterDataException ex)
                {
                    // Keep every byte decoded before the bad spot.
                    output.Advance(written);
                    Salvage(inf, output);
                    issues |= FilterIssues.DataError;
                    Note(ref message, "Flate: " + ex.Message + " (after " + output.Length + " bytes)");
                    return;
                }
                catch (FilterLimitException ex)
                {
                    output.Advance(written);
                    issues |= FilterIssues.LimitExceeded;
                    Note(ref message, "Flate: " + ex.Message);
                    return;
                }

                output.Advance(written);
                pos += consumed;
                if (st == InflateStatus.NeedOutput)
                {
                    if (free.Length == 0)
                    {
                        issues |= FilterIssues.LimitExceeded;
                        Note(ref message, "Flate: decoded data exceeds the size limit");
                        return;
                    }

                    continue;
                }

                if (st == InflateStatus.Done)
                {
                    if (inf.ChecksumMismatch)
                    {
                        issues |= FilterIssues.BadChecksum;
                        Note(ref message, "Flate: Adler-32 checksum mismatch");
                    }

                    NoteTrailing(rest.Slice(pos), ref issues, ref message, "Flate");
                    return;
                }

                // Input exhausted.
                if (inf.AtTrailer)
                {
                    issues |= FilterIssues.MissingChecksum;
                    Note(ref message, "Flate: zlib checksum is missing");
                }
                else
                {
                    issues |= FilterIssues.Truncated;
                    Note(ref message, "Flate: stream is truncated after " + output.Length + " bytes of output");
                }

                return;
            }
        }
        finally
        {
            t_inflater = inf;
        }
    }

    /// <summary>
    /// Inflates <paramref name="input"/> once, keeping nothing, and returns the size of the whole result, so a large stream can
    /// be decoded straight into one array of exactly that size instead of growing segments that are joined and thrown away.
    /// Returns 0 when the stream does not decode cleanly to its end within the limits (the normal path then decodes and
    /// reports it as before).
    /// </summary>
    public static long MeasureFlate(ReadOnlySpan<byte> input, in DecodeLimits limits, CancellationToken ct)
    {
        if (input.IsEmpty)
        {
            return 0;
        }

        bool zlib = SniffZlibHeader(input, out int skip, out bool badHeader);
        if (badHeader)
        {
            return 0;
        }

        ReadOnlySpan<byte> rest = input.Slice(skip);
        Inflater inf = t_inflater ?? new Inflater(zlib, limits.MaxDecodedBytes, limits.MaxExpansionRatio, limits.RatioAppliesAfterBytes);
        t_inflater = null;
        byte[] scratch = System.Buffers.ArrayPool<byte>.Shared.Rent(Slice);
        try
        {
            inf.Reset(zlib, limits.MaxDecodedBytes, limits.MaxExpansionRatio, limits.RatioAppliesAfterBytes);
            long total = 0;
            int pos = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                InflateStatus st;
                int consumed;
                int written;
                try
                {
                    st = inf.Inflate(rest.Slice(pos), scratch.AsSpan(0, Slice), out consumed, out written);
                }
                catch (FilterDataException)
                {
                    return 0;
                }
                catch (FilterLimitException)
                {
                    return 0;
                }

                total += written;
                pos += consumed;
                if (total > limits.MaxDecodedBytes)
                {
                    return 0;
                }

                if (st == InflateStatus.Done)
                {
                    return total;
                }

                if (st != InflateStatus.NeedOutput)
                {
                    return 0;
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(scratch);
            t_inflater = inf;
        }
    }

    private static void Salvage(Inflater inf, OutputBuffer output)
    {
        while (true)
        {
            Span<byte> room = output.Free(Slice);
            if (room.Length == 0)
            {
                output.MarkExceeded();
                return;
            }

            int n = inf.TakePending(room);
            if (n == 0)
            {
                return;
            }

            output.Advance(n);
        }
    }

    // zlib header (RFC 1950 2.2): CM = 8, CINFO <= 7, (CMF*256 + FLG) divisible by 31, no preset dictionary.
    private static bool ValidHeader(byte cmf, byte flg) =>
        (cmf & 0x0F) == 8 && (cmf >> 4) <= 7 && (((cmf << 8) | flg) % 31) == 0 && (flg & 0x20) == 0;

    /// <summary>
    /// Decides how to read the start of a Flate stream. Writers in the wild emit a stray end-of-line before the header,
    /// a header with a bad check value, or no header at all (raw deflate).
    /// </summary>
    private static bool SniffZlibHeader(ReadOnlySpan<byte> d, out int skip, out bool bad)
    {
        skip = 0;
        bad = false;
        if (d.Length >= 2 && ValidHeader(d[0], d[1]))
        {
            return true;
        }

        bad = true;
        int k = 0;
        while (k < 2 && k < d.Length && (d[k] is 0x0A or 0x0D or 0x20))
        {
            k++;
        }

        if (k > 0 && d.Length >= k + 2 && ValidHeader(d[k], d[k + 1]))
        {
            skip = k;
            return true;
        }

        if (d.Length >= 2 && (d[0] & 0x0F) == 8 && (d[0] >> 4) <= 7)
        {
            // Looks like a zlib header with a damaged check value or an unsupported dictionary flag: skip it,
            // decode the rest as raw deflate and ignore the checksum.
            skip = 2;
            return false;
        }

        return false;
    }

    public static void Lzw(ReadOnlySpan<byte> input, FilterParameters p, in DecodeLimits limits, OutputBuffer output, ref FilterIssues issues, ref string? message, CancellationToken ct)
    {
        if (input.IsEmpty)
        {
            return;
        }

        LzwDecoder dec = t_lzw ?? new LzwDecoder();
        t_lzw = null;
        try
        {
            dec.Reset(p.EarlyChange, limits.MaxDecodedBytes, limits.MaxExpansionRatio, limits.RatioAppliesAfterBytes);
            int pos = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Span<byte> free = output.Free(Slice);
                if (free.Length > Slice)
                {
                    free = free.Slice(0, Slice);
                }

                InflateStatus st;
                int consumed = 0;
                int written = 0;
                try
                {
                    st = dec.Decode(input.Slice(pos), free, out consumed, out written);
                }
                catch (FilterDataException ex)
                {
                    output.Advance(written);
                    issues |= FilterIssues.DataError;
                    Note(ref message, "LZW: " + ex.Message + " (after " + output.Length + " bytes)");
                    return;
                }
                catch (FilterLimitException ex)
                {
                    output.Advance(written);
                    issues |= FilterIssues.LimitExceeded;
                    Note(ref message, "LZW: " + ex.Message);
                    return;
                }

                output.Advance(written);
                pos += consumed;
                if (st == InflateStatus.NeedOutput)
                {
                    if (free.Length == 0)
                    {
                        issues |= FilterIssues.LimitExceeded;
                        Note(ref message, "LZW: decoded data exceeds the size limit");
                        return;
                    }

                    continue;
                }

                if (st == InflateStatus.Done)
                {
                    NoteTrailing(input.Slice(pos), ref issues, ref message, "LZW");
                }
                else
                {
                    issues |= FilterIssues.MissingEndOfData;
                    Note(ref message, "LZW: end-of-data code is missing");
                }

                return;
            }
        }
        finally
        {
            t_lzw = dec;
        }
    }

    internal static void NoteTrailing(ReadOnlySpan<byte> rest, ref FilterIssues issues, ref string? message, string name)
    {
        foreach (byte b in rest)
        {
            if (!IsWhite(b))
            {
                issues |= FilterIssues.TrailingData;
                Note(ref message, name + ": " + rest.Length + " bytes of data follow the end of the encoded stream");
                return;
            }
        }
    }

    internal static bool IsWhite(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    internal static void Note(ref string? message, string text) => message = message is null ? text : message + "; " + text;
}
