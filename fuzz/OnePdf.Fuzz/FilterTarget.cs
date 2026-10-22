using System;
using OnePdf.Filters;

namespace OnePdf.Fuzz;

/// <summary>
/// Stream filter target. The first bytes pick the filter and predictor parameters; the rest is the encoded stream.
/// Filters must never throw, never exceed the cap and never read or write out of bounds.
/// </summary>
internal static class FilterTarget
{
    private static readonly string[] Names = ["FlateDecode", "LZWDecode", "ASCIIHexDecode", "ASCII85Decode", "RunLengthDecode", "Fl", "LZW", "A85"];

    // Small bounds keep each execution fast and exercise the limit paths.
    private static readonly DecodeLimits Limits = new(1 << 20, 64, 1 << 12);

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 6)
        {
            return;
        }

        string name = Names[data[0] % Names.Length];
        int predictor = data[1] % 17;
        var p = new FilterParameters(
            Predictor: predictor,
            Colors: 1 + (data[2] & 7),
            BitsPerComponent: (data[3] & 7) switch { 0 => 1, 1 => 2, 2 => 4, 3 => 16, 4 => 3, _ => 8 },
            Columns: 1 + (data[4] * 3),
            EarlyChange: data[5] & 1);
        ReadOnlySpan<byte> input = data.Slice(6);
        FilterResult r = StreamFilters.Decode(name, input, p, Limits);
        if (r.Data.Length > Limits.MaxDecodedBytes)
        {
            throw new InvalidOperationException("output exceeds the cap");
        }

        // Chain the output through a second, cheaper filter to cover stage hand-off.
        _ = StreamFilters.DecodeChain([name, "RunLengthDecode"], [p, null], input.ToArray(), Limits);
    }
}
