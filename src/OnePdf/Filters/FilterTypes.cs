using System;

namespace OnePdf.Filters;

/// <summary>
/// The /DecodeParms values a filter can use, as plain numbers (ISO 32000-2 7.4.4.4 and 7.4.4.3 Table 8, Table 9).
/// Absent entries keep their defaults.
/// </summary>
/// <param name="Predictor">1 = none, 2 = TIFF predictor 2, 10-15 = PNG predictors (the row's own tag byte decides the method).</param>
/// <param name="Colors">Components per sample, at least 1.</param>
/// <param name="BitsPerComponent">1, 2, 4, 8 or 16.</param>
/// <param name="Columns">Samples per row, at least 1.</param>
/// <param name="EarlyChange">LZW only: 1 (default) widens codes one table entry early, 0 does not.</param>
/// <param name="K">CCITTFaxDecode: negative = Group 4, 0 = Group 3 one-dimensional (default), positive = Group 3 mixed.</param>
/// <param name="EndOfLine">CCITTFaxDecode: EOL codes are present (they are accepted either way).</param>
/// <param name="EncodedByteAlign">CCITTFaxDecode: each encoded row starts on a byte boundary.</param>
/// <param name="CcittColumns">CCITTFaxDecode: pixels per row (<c>/Columns</c>; 1728 when absent).</param>
/// <param name="Rows">CCITTFaxDecode: height in rows, 0 when unknown (decode until the data ends).</param>
/// <param name="EndOfBlock">CCITTFaxDecode: the data ends with an end-of-block code (decoding stops at one regardless).</param>
/// <param name="BlackIs1">CCITTFaxDecode: black pixels are 1 bits; default false (black is 0).</param>
/// <param name="DamagedRowsBeforeError">CCITTFaxDecode: read for completeness; damaged rows never stop decoding.</param>
internal sealed record FilterParameters(
    int Predictor = 1,
    int Colors = 1,
    int BitsPerComponent = 8,
    int Columns = 1,
    int EarlyChange = 1,
    int K = 0,
    bool EndOfLine = false,
    bool EncodedByteAlign = false,
    int CcittColumns = 1728,
    int Rows = 0,
    bool EndOfBlock = true,
    bool BlackIs1 = false,
    int DamagedRowsBeforeError = 0)
{
    /// <summary>All defaults: no predictor, early change.</summary>
    public static FilterParameters Default { get; } = new();
}

/// <summary>Bounds applied while a filter runs; they are checked before output buffers grow.</summary>
/// <param name="MaxDecodedBytes">The most bytes one filter stage may produce. Use <see cref="long.MaxValue"/> for no cap
/// (array size still bounds it). <c>default(DecodeLimits)</c> allows no output; start from <see cref="Default"/>.</param>
/// <param name="MaxExpansionRatio">The most output bytes per input byte for Flate and LZW once the output is past
/// <paramref name="RatioAppliesAfterBytes"/>; 0 disables the ratio.</param>
/// <param name="RatioAppliesAfterBytes">Output up to this size is never rejected by the ratio, so small streams of repetitive data pass.</param>
/// <param name="DiscardOnLimit">When a stage breaches the limit, return no data instead of the part produced within the bound.
/// For callers that raise an error and never look at it: it saves copying up to the whole cap just to throw it away.</param>
internal readonly record struct DecodeLimits(long MaxDecodedBytes, double MaxExpansionRatio, long RatioAppliesAfterBytes, bool DiscardOnLimit = false)
{
    /// <summary>256 MiB per stage, ratio 1100 once past 16 MiB of output.</summary>
    public static DecodeLimits Default { get; } = new(256L << 20, 1100, 16L << 20);

    /// <summary>No limits other than the maximum array size.</summary>
    public static DecodeLimits Unrestricted { get; } = new(long.MaxValue, 0, 0);
}

/// <summary>Things that went wrong, or were repaired, while decoding. Several can be set at once.</summary>
[Flags]
internal enum FilterIssues
{
    /// <summary>The data decoded cleanly.</summary>
    None = 0,

    /// <summary>The input ended before the compressed data was complete. The output holds what decoded.</summary>
    Truncated = 1,

    /// <summary>The zlib Adler-32 did not match. The output is complete.</summary>
    BadChecksum = 2,

    /// <summary>The zlib checksum is absent but the deflate data is complete.</summary>
    MissingChecksum = 4,

    /// <summary>The end-of-data marker (<c>&gt;</c>, <c>~&gt;</c>, LZW code 257, RunLength 128) is absent.</summary>
    MissingEndOfData = 8,

    /// <summary>Non-whitespace bytes follow the end of the encoded data; they were ignored.</summary>
    TrailingData = 16,

    /// <summary>The zlib header is missing or wrong; the data was decoded as raw deflate or after skipping the header.</summary>
    BadHeader = 32,

    /// <summary>Invalid data was met. Decoding stopped (compressed formats) or skipped the byte (ASCII formats).</summary>
    DataError = 64,

    /// <summary>A <see cref="DecodeLimits"/> bound was hit. The output holds what was produced within the bound.</summary>
    LimitExceeded = 128,

    /// <summary>The filter is not handled here (image codecs, Crypt, unknown names). See <see cref="FilterResult.StoppedAtFilter"/>.</summary>
    UnsupportedFilter = 256,

    /// <summary>Predictor parameters are invalid; the predictor was not applied.</summary>
    BadParameters = 512,
}

/// <summary>The outcome of decoding one stream.</summary>
/// <param name="Data">The decoded bytes. For a chain that stopped early, the bytes produced by the last stage that ran.</param>
/// <param name="Issues">What was repaired or went wrong; <see cref="FilterIssues.None"/> when clean.</param>
/// <param name="Message">Human-readable description of every issue, or null when clean.</param>
/// <param name="StoppedAtFilter">Index in the chain of the first filter not run (unsupported or after a limit), or -1.</param>
internal readonly record struct FilterResult(ReadOnlyMemory<byte> Data, FilterIssues Issues, string? Message, int StoppedAtFilter)
{
    /// <summary>True when the data decoded with no repair.</summary>
    public bool IsClean => Issues == FilterIssues.None;
}
