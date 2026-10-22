using System;
using System.Threading;

namespace OnePdf.Filters;

/// <summary>ASCIIHexDecode, ASCII85Decode and RunLengthDecode (ISO 32000-2 7.4.2, 7.4.3, 7.4.5).</summary>
internal static class AsciiFilters
{
    private const int CancelEvery = 1 << 16;

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        _ => -1,
    };

    /// <summary>Two hex digits per byte; white space ignored; <c>&gt;</c> ends the data; an odd last digit counts as followed by 0.</summary>
    public static void Hex(ReadOnlySpan<byte> input, OutputBuffer output, ref FilterIssues issues, ref string? message, CancellationToken ct)
    {
        int hi = -1;
        bool sawEod = false;
        bool badChar = false;
        int i = 0;
        for (; i < input.Length; i++)
        {
            if ((i & (CancelEvery - 1)) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            byte b = input[i];
            if (b == (byte)'>')
            {
                sawEod = true;
                i++;
                break;
            }

            int v = HexValue(b);
            if (v < 0)
            {
                if (!CompressedFilters.IsWhite(b))
                {
                    badChar = true;
                }

                continue;
            }

            if (hi < 0)
            {
                hi = v;
            }
            else
            {
                if (!output.Put((byte)((hi << 4) | v)))
                {
                    Exceeded(ref issues, ref message, "ASCIIHex");
                    return;
                }

                hi = -1;
            }
        }

        if (hi >= 0 && !output.Put((byte)(hi << 4)))
        {
            Exceeded(ref issues, ref message, "ASCIIHex");
            return;
        }

        if (badChar)
        {
            issues |= FilterIssues.DataError;
            CompressedFilters.Note(ref message, "ASCIIHex: characters that are not hex digits were skipped");
        }

        if (!sawEod)
        {
            issues |= FilterIssues.MissingEndOfData;
            CompressedFilters.Note(ref message, "ASCIIHex: '>' end marker is missing");
        }
        else
        {
            CompressedFilters.NoteTrailing(input.Slice(i), ref issues, ref message, "ASCIIHex");
        }
    }

    /// <summary>
    /// Base-85: five characters '!'..'u' give four bytes, <c>z</c> is four zero bytes, <c>~&gt;</c> ends the data and a
    /// final group of n characters (2-4) gives n-1 bytes. A leading <c>&lt;~</c> is tolerated.
    /// </summary>
    public static void Ascii85(ReadOnlySpan<byte> input, OutputBuffer output, ref FilterIssues issues, ref string? message, CancellationToken ct)
    {
        int i = 0;
        int start = 0;
        while (start < input.Length && CompressedFilters.IsWhite(input[start]))
        {
            start++;
        }

        if (start + 1 < input.Length && input[start] == (byte)'<' && input[start + 1] == (byte)'~')
        {
            i = start + 2;
        }

        ulong acc = 0;
        int n = 0;
        bool sawEod = false;
        bool bad = false;
        bool overflow = false;
        for (; i < input.Length; i++)
        {
            if ((i & (CancelEvery - 1)) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            byte b = input[i];
            if (CompressedFilters.IsWhite(b))
            {
                continue;
            }

            if (b == (byte)'~')
            {
                // The marker is "~>"; a lone '~' at the very end counts too.
                sawEod = true;
                i++;
                while (i < input.Length && CompressedFilters.IsWhite(input[i]))
                {
                    i++;
                }

                if (i < input.Length && input[i] == (byte)'>')
                {
                    i++;
                }

                break;
            }

            if (b == (byte)'z' && n == 0)
            {
                if (!output.PutRun(0, 4))
                {
                    Exceeded(ref issues, ref message, "ASCII85");
                    return;
                }

                continue;
            }

            if (b < (byte)'!' || b > (byte)'u')
            {
                bad = true;
                continue;
            }

            acc = (acc * 85) + (uint)(b - '!');
            if (++n == 5)
            {
                if (acc > uint.MaxValue)
                {
                    overflow = true;
                }

                uint v = (uint)acc;
                if (!output.Put((byte)(v >> 24)) || !output.Put((byte)(v >> 16)) || !output.Put((byte)(v >> 8)) || !output.Put((byte)v))
                {
                    Exceeded(ref issues, ref message, "ASCII85");
                    return;
                }

                acc = 0;
                n = 0;
            }
        }

        if (n == 1)
        {
            bad = true;
        }
        else if (n > 1)
        {
            // Pad with 'u' (84), the highest digit, so truncation rounds the same way the encoder did.
            for (int k = n; k < 5; k++)
            {
                acc = (acc * 85) + 84;
            }

            if (acc > uint.MaxValue)
            {
                overflow = true;
            }

            uint v = (uint)acc;
            for (int k = 0; k < n - 1; k++)
            {
                if (!output.Put((byte)(v >> (24 - (8 * k)))))
                {
                    Exceeded(ref issues, ref message, "ASCII85");
                    return;
                }
            }
        }

        if (bad || overflow)
        {
            issues |= FilterIssues.DataError;
            CompressedFilters.Note(ref message, "ASCII85: invalid characters or groups were skipped or truncated");
        }

        if (!sawEod)
        {
            issues |= FilterIssues.MissingEndOfData;
            CompressedFilters.Note(ref message, "ASCII85: '~>' end marker is missing");
        }
        else
        {
            CompressedFilters.NoteTrailing(input.Slice(i), ref issues, ref message, "ASCII85");
        }
    }

    /// <summary>
    /// Length byte 0-127: copy the next length+1 bytes; 129-255: repeat the next byte 257-length times; 128 ends the data.
    /// </summary>
    public static void RunLength(ReadOnlySpan<byte> input, OutputBuffer output, ref FilterIssues issues, ref string? message, CancellationToken ct)
    {
        int i = 0;
        bool sawEod = false;
        while (i < input.Length)
        {
            ct.ThrowIfCancellationRequested();

            int len = input[i++];
            if (len == 128)
            {
                sawEod = true;
                break;
            }

            if (len < 128)
            {
                int count = len + 1;
                int avail = Math.Min(count, input.Length - i);
                if (!output.Put(input.Slice(i, avail)))
                {
                    Exceeded(ref issues, ref message, "RunLength");
                    return;
                }

                i += avail;
                if (avail < count)
                {
                    issues |= FilterIssues.Truncated;
                    CompressedFilters.Note(ref message, "RunLength: literal run is cut short");
                    return;
                }
            }
            else
            {
                if (i >= input.Length)
                {
                    issues |= FilterIssues.Truncated;
                    CompressedFilters.Note(ref message, "RunLength: repeat run has no byte to repeat");
                    return;
                }

                if (!output.PutRun(input[i++], 257 - len))
                {
                    Exceeded(ref issues, ref message, "RunLength");
                    return;
                }
            }
        }

        if (!sawEod)
        {
            issues |= FilterIssues.MissingEndOfData;
            CompressedFilters.Note(ref message, "RunLength: end-of-data marker (128) is missing");
        }
        else
        {
            CompressedFilters.NoteTrailing(input.Slice(i), ref issues, ref message, "RunLength");
        }
    }

    private static void Exceeded(ref FilterIssues issues, ref string? message, string name)
    {
        issues |= FilterIssues.LimitExceeded;
        CompressedFilters.Note(ref message, name + ": decoded data exceeds the size limit");
    }
}
