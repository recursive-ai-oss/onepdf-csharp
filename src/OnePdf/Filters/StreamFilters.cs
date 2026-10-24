using System;
using System.Collections.Generic;
using System.Threading;

namespace OnePdf.Filters;

/// <summary>
/// Decodes PDF stream filters that need no image codec: FlateDecode, LZWDecode, ASCIIHexDecode, ASCII85Decode,
/// RunLengthDecode and CCITTFaxDecode, with the PNG/TIFF predictors, singly or chained (ISO 32000-2 7.4).
/// </summary>
/// <remarks>
/// <para>Damaged data never throws. The decoded prefix comes back with <see cref="FilterIssues"/> flags and a message the
/// caller turns into a diagnostic. A <see cref="DecodeLimits"/> breach also comes back as a flag
/// (<see cref="FilterIssues.LimitExceeded"/>) with the output produced within the bound; the caller raises its limit error.</para>
/// <para>Filter names are the full names and the inline-image abbreviations (AHx, A85, LZW, Fl, RL, CCF). Anything else
/// (DCT, JPX, JBIG2, Crypt, unknown) stops a chain with <see cref="FilterIssues.UnsupportedFilter"/>.</para>
/// <para>Only <see cref="OperationCanceledException"/> and argument exceptions escape.</para>
/// </remarks>
internal static class StreamFilters
{
    private enum Kind
    {
        Unsupported,
        Flate,
        Lzw,
        AsciiHex,
        Ascii85,
        RunLength,
        CcittFax,
    }

    /// <summary>True when <paramref name="name"/> is a filter this class decodes.</summary>
    public static bool IsSupported(ReadOnlySpan<char> name) => Classify(name) != Kind.Unsupported;

    /// <summary>Decodes one filter stage. <paramref name="parameters"/> may be null for defaults.</summary>
    public static FilterResult Decode(string name, ReadOnlySpan<byte> input, FilterParameters? parameters, in DecodeLimits limits, CancellationToken cancellationToken = default)
        => Decode(name, input, parameters, limits, 0, cancellationToken);

    /// <summary>
    /// As above, with the expected size of the stage's final output (after any predictor), or 0 when unknown. For Flate and
    /// LZW a plausible hint (within the limits, and no more than the input could expand to) sizes the output buffer once, so
    /// a large result is allocated a single time. A wrong hint costs nothing but the up-front allocation: the output is
    /// whatever the data decodes to, and the limits apply as before.
    /// </summary>
    public static FilterResult Decode(string name, ReadOnlySpan<byte> input, FilterParameters? parameters, in DecodeLimits limits, long expectedSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Run(Classify(name), name, input, parameters ?? FilterParameters.Default, limits, expectedSize, cancellationToken);
    }

    /// <summary>As <see cref="Decode(string, ReadOnlySpan{byte}, FilterParameters?, in DecodeLimits, CancellationToken)"/> with the name as raw (Latin-1/ASCII) bytes.</summary>
    public static FilterResult Decode(ReadOnlySpan<byte> name, ReadOnlySpan<byte> input, FilterParameters? parameters, in DecodeLimits limits, CancellationToken cancellationToken = default)
    {
        Span<char> chars = name.Length <= 32 ? stackalloc char[name.Length] : new char[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            chars[i] = (char)name[i];
        }

        return Run(Classify(chars), new string(chars), input, parameters ?? FilterParameters.Default, limits, 0, cancellationToken);
    }

    /// <summary>
    /// Runs a /Filter array in order. <paramref name="parameters"/> is parallel to <paramref name="names"/> (entries or the
    /// list itself may be null). Stops at the first unsupported filter, returning the data produced so far and its index in
    /// <see cref="FilterResult.StoppedAtFilter"/>, so image codecs can take over; also stops after a limit breach.
    /// </summary>
    public static FilterResult DecodeChain(IReadOnlyList<string> names, IReadOnlyList<FilterParameters?>? parameters, ReadOnlyMemory<byte> input, in DecodeLimits limits, CancellationToken cancellationToken = default)
        => DecodeChain(names, parameters, input, limits, 0, cancellationToken);

    /// <summary>
    /// As above, with the expected size of the chain's final output (0 when unknown), applied to the last stage; see
    /// <see cref="Decode(string, ReadOnlySpan{byte}, FilterParameters?, in DecodeLimits, long, CancellationToken)"/>.
    /// </summary>
    public static FilterResult DecodeChain(IReadOnlyList<string> names, IReadOnlyList<FilterParameters?>? parameters, ReadOnlyMemory<byte> input, in DecodeLimits limits, long expectedSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        ReadOnlyMemory<byte> data = input;
        FilterIssues issues = FilterIssues.None;
        string? message = null;
        for (int i = 0; i < names.Count; i++)
        {
            FilterParameters? fp = parameters is not null && i < parameters.Count ? parameters[i] : null;
            FilterResult r = Decode(names[i], data.Span, fp, limits, i == names.Count - 1 ? expectedSize : 0, cancellationToken);
            issues |= r.Issues;
            if (r.Message is not null)
            {
                CompressedFilters.Note(ref message, r.Message);
            }

            if ((r.Issues & FilterIssues.UnsupportedFilter) != 0)
            {
                return new FilterResult(data, issues, message, i);
            }

            data = r.Data;
            if ((r.Issues & FilterIssues.LimitExceeded) != 0)
            {
                return new FilterResult(data, issues, message, i);
            }
        }

        return new FilterResult(data, issues, message, -1);
    }

    // Compressed input at least this long is measured before it is decoded when nothing says how big the result is.
    private const int MeasureThreshold = 128 << 10;

    private static Kind Classify(ReadOnlySpan<char> name) => name switch
    {
        "FlateDecode" or "Fl" => Kind.Flate,
        "LZWDecode" or "LZW" => Kind.Lzw,
        "ASCIIHexDecode" or "AHx" => Kind.AsciiHex,
        "ASCII85Decode" or "A85" => Kind.Ascii85,
        "RunLengthDecode" or "RL" => Kind.RunLength,
        "CCITTFaxDecode" or "CCF" => Kind.CcittFax,
        _ => Kind.Unsupported,
    };

    private static FilterResult Run(Kind kind, string name, ReadOnlySpan<byte> input, FilterParameters p, in DecodeLimits limits, long expectedSize, CancellationToken ct)
    {
        if (kind == Kind.Unsupported)
        {
            return new FilterResult(ReadOnlyMemory<byte>.Empty, FilterIssues.UnsupportedFilter, "Filter " + name + " is not handled by the stream filter layer", 0);
        }

        ct.ThrowIfCancellationRequested();
        FilterIssues issues = FilterIssues.None;
        string? message = null;
        long cap = limits.MaxDecodedBytes;
        long estimate = kind switch
        {
            Kind.Flate or Kind.Lzw => (long)input.Length * 4,
            Kind.RunLength => (long)input.Length * 2,
            Kind.CcittFax => Math.Min(cap, Math.Max(4096, (long)input.Length * 8)),
            _ => input.Length,
        };
        long exact = kind is Kind.Flate or Kind.Lzw ? ExactOutputSize(expectedSize, input.Length, p, limits) : 0;
        if (exact == 0 && kind == Kind.Flate && input.Length >= MeasureThreshold)
        {
            // No size is known and the result is probably large: one cheap pass to learn it saves the segment growth and the join.
            long measured = CompressedFilters.MeasureFlate(input, limits, ct);
            exact = measured > OutputBuffer.PooledLimit && measured <= cap ? measured : 0;
        }

        var output = exact > 0 ? new OutputBuffer(cap, estimate, exact) : new OutputBuffer(cap, estimate);
        try
        {
            switch (kind)
            {
                case Kind.Flate:
                    CompressedFilters.Flate(input, limits, output, ref issues, ref message, ct);
                    break;
                case Kind.Lzw:
                    CompressedFilters.Lzw(input, p, limits, output, ref issues, ref message, ct);
                    break;
                case Kind.AsciiHex:
                    AsciiFilters.Hex(input, output, ref issues, ref message, ct);
                    break;
                case Kind.Ascii85:
                    AsciiFilters.Ascii85(input, output, ref issues, ref message, ct);
                    break;
                case Kind.CcittFax:
                    CcittFax.Decode(input, p, output, ref issues, ref message, ct);
                    break;
                default:
                    AsciiFilters.RunLength(input, output, ref issues, ref message, ct);
                    break;
            }

            if (output.Exceeded)
            {
                issues |= FilterIssues.LimitExceeded;
            }

            if ((issues & FilterIssues.LimitExceeded) != 0 && limits.DiscardOnLimit)
            {
                return new FilterResult(ReadOnlyMemory<byte>.Empty, issues, message, 0);
            }

            // The predictor works in place on the exact-size result; PNG rows lose their tag byte, so the result may be a slice.
            byte[] decoded = output.DetachArray();
            ReadOnlyMemory<byte> data = decoded;
            if (kind is Kind.Flate or Kind.Lzw && p.Predictor > 1)
            {
                int n = Predictors.Undo(decoded, decoded.Length, p, ref issues, ref message);
                if (n >= 0)
                {
                    data = decoded.AsMemory(0, n);
                }
            }

            return new FilterResult(data, issues, message, (issues & FilterIssues.LimitExceeded) != 0 ? 0 : -1);
        }
        finally
        {
            output.Dispose();
        }
    }

    // The size of the buffer before the predictor runs, when the caller's expectation is believable: within the cap and no more
    // than the input could expand to (the ratio bound, which is above deflate's own maximum), so a tiny stream whose
    // dictionary declares a huge image does not get a huge buffer.
    private static long ExactOutputSize(long expected, int inputLength, FilterParameters p, in DecodeLimits limits)
    {
        if (expected <= 0 || expected > limits.MaxDecodedBytes)
        {
            return 0;
        }

        long size = expected;
        if (p.Predictor >= 10)
        {
            // PNG predictors add a tag byte to every row (ISO 32000-2 7.4.4.4).
            if (p.Columns is < 1 or > (1 << 24) || p.Colors is < 1 or > (1 << 12) || p.BitsPerComponent is < 1 or > 16)
            {
                return 0;
            }

            long rowBytes = ((long)p.Columns * p.Colors * p.BitsPerComponent + 7) / 8;
            if (expected % rowBytes != 0)
            {
                return 0;
            }

            size = expected / rowBytes * (rowBytes + 1);
        }

        double ratio = limits.MaxExpansionRatio;
        if (ratio > 0 && size > Math.Max(1.0, ratio * inputLength))
        {
            return 0;
        }

        return size;
    }
}
