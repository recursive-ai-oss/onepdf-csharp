using System;
using OnePdf.Filters;

namespace OnePdf.Fuzz;

/// <summary>
/// CCITTFaxDecode target. The first six bytes pick K, the flags, /Columns and /Rows; the rest is the encoded stream.
/// Decoding must never throw, never exceed the cap and always return whole rows.
/// </summary>
internal static class CcittTarget
{
    private static readonly DecodeLimits Limits = new(1 << 20, 0, 0);

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 7)
        {
            return;
        }

        int columns = 1 + (((data[3] << 8) | data[4]) % 5000);
        var p = new FilterParameters(
            K: (data[0] % 4) - 1,
            EndOfLine: (data[1] & 1) != 0,
            EncodedByteAlign: (data[1] & 2) != 0,
            BlackIs1: (data[1] & 4) != 0,
            EndOfBlock: (data[1] & 8) == 0,
            DamagedRowsBeforeError: data[2] & 3,
            CcittColumns: columns,
            Rows: (data[1] & 16) != 0 ? data[5] : 0);
        FilterResult r = StreamFilters.Decode("CCITTFaxDecode", data.Slice(6), p, Limits);
        int rowBytes = (columns + 7) / 8;
        if (r.Data.Length > Limits.MaxDecodedBytes || r.Data.Length % rowBytes != 0)
        {
            throw new InvalidOperationException("output is not whole rows within the cap");
        }
    }
}
