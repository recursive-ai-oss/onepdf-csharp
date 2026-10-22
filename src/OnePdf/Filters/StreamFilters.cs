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
    {
        ArgumentNullException.ThrowIfNull(name);
        return Run(Classify(name), name, input, parameters ?? FilterParameters.Default, limits, cancellationToken);
    }

    /// <summary>As <see cref="Decode(string, ReadOnlySpan{byte}, FilterParameters?, in DecodeLimits, CancellationToken)"/> with the name as raw (Latin-1/ASCII) bytes.</summary>
    public static FilterResult Decode(ReadOnlySpan<byte> name, ReadOnlySpan<byte> input, FilterParameters? parameters, in DecodeLimits limits, CancellationToken cancellationToken = default)
    {
        Span<char> chars = name.Length <= 32 ? stackalloc char[name.Length] : new char[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            chars[i] = (char)name[i];
        }

        return Run(Classify(chars), new string(chars), input, parameters ?? FilterParameters.Default, limits, cancellationToken);
    }

    /// <summary>
    /// Runs a /Filter array in order. <paramref name="parameters"/> is parallel to <paramref name="names"/> (entries or the
    /// list itself may be null). Stops at the first unsupported filter, returning the data produced so far and its index in
    /// <see cref="FilterResult.StoppedAtFilter"/>, so image codecs can take over; also stops after a limit breach.
    /// </summary>
    public static FilterResult DecodeChain(IReadOnlyList<string> names, IReadOnlyList<FilterParameters?>? parameters, ReadOnlyMemory<byte> input, in DecodeLimits limits, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        ReadOnlyMemory<byte> data = input;
        FilterIssues issues = FilterIssues.None;
        string? message = null;
        for (int i = 0; i < names.Count; i++)
        {
            FilterParameters? fp = parameters is not null && i < parameters.Count ? parameters[i] : null;
            FilterResult r = Decode(names[i], data.Span, fp, limits, cancellationToken);
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

    private static FilterResult Run(Kind kind, string name, ReadOnlySpan<byte> input, FilterParameters p, in DecodeLimits limits, CancellationToken ct)
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
        var output = new OutputBuffer(cap, estimate);
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

        if (kind is Kind.Flate or Kind.Lzw && p.Predictor > 1)
        {
            int n = Predictors.Undo(output.Buffer, output.Length, p, ref issues, ref message);
            if (n >= 0)
            {
                output.SetLength(n);
            }
        }

        if (output.Exceeded)
        {
            issues |= FilterIssues.LimitExceeded;
        }

        return new FilterResult(output.Detach(), issues, message, (issues & FilterIssues.LimitExceeded) != 0 ? 0 : -1);
    }
}
