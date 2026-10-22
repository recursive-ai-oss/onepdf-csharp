using System;
using System.Threading;

namespace OnePdf.Filters;

/// <summary>
/// CCITTFaxDecode (ISO 32000-2 7.4.6): ITU-T T.4 Group 3 one-dimensional (K = 0) and mixed one/two-dimensional (K &gt; 0)
/// coding and T.6 Group 4 (K &lt; 0), producing packed 1-bit rows.
/// </summary>
/// <remarks>
/// <para>Rows come out MSB first, each padded to a byte boundary. With <c>BlackIs1</c> false (the default) black pixels are 0
/// bits and white are 1 bits; with it true they are swapped. The bits past the last column are written as white.</para>
/// <para>Leniency, in the order the repairs are tried:
/// a code that is not valid, a run that overshoots the row, or a vertical code pointing outside the row ends the row at the
/// last good pixel (the rest of the row is white). Group 3 data then resumes at the next EOL code, so the later rows survive;
/// the damaged row is replaced by a copy of the row above, or white if there is none. Group 4 has no markers to resynchronise on,
/// so decoding stops there. <c>/DamagedRowsBeforeError</c> is read but never makes decoding stop: damage is only reported through
/// <see cref="FilterIssues.DataError"/>. A missing end-of-block code is not an error. Input that ends inside a row keeps the
/// partial row and sets <see cref="FilterIssues.Truncated"/>. When <c>/Rows</c> is given and fewer rows come out, the output is
/// not padded; the image layer sizes the picture from the image dictionary.</para>
/// <para>Group 3 rows may be preceded by fill bits and an EOL (000000000001), which is always accepted; in mixed mode a tag
/// bit after the EOL selects 1-D (1) or 2-D (0) coding. If no EOL precedes a row of a mixed stream, one row in every K is taken as 1-D. Six EOLs in a row
/// (RTC) or two (EOFB, Group 4) end the data. With <c>/EncodedByteAlign</c> each row starts on a byte boundary.</para>
/// <para>Bounds: <c>/Columns</c> up to 2^24; rows × bytes-per-row is checked against the decoded-size cap before any buffer is
/// sized for it, and decoding stops at the cap with <see cref="FilterIssues.LimitExceeded"/>.</para>
/// </remarks>
internal static class CcittFax
{
    internal const int MaxColumns = 1 << 24;

    // T.4 Tables 2 and 3 (terminating codes for runs 0..63, make-up codes for 64..1728) and the extended make-up codes
    // 1792..2560 shared by both colours.
    private static readonly string[] s_whiteTerm =
    [
        "00110101", "000111", "0111", "1000", "1011", "1100", "1110", "1111", "10011", "10100", "00111", "01000", "001000", "000011", "110100", "110101",
        "101010", "101011", "0100111", "0001100", "0001000", "0010111", "0000011", "0000100", "0101000", "0101011", "0010011", "0100100", "0011000", "00000010", "00000011", "00011010",
        "00011011", "00010010", "00010011", "00010100", "00010101", "00010110", "00010111", "00101000", "00101001", "00101010", "00101011", "00101100", "00101101", "00000100", "00000101", "00001010",
        "00001011", "01010010", "01010011", "01010100", "01010101", "00100100", "00100101", "01011000", "01011001", "01011010", "01011011", "01001010", "01001011", "00110010", "00110011", "00110100",
    ];

    private static readonly string[] s_whiteMakeup =
    [
        "11011", "10010", "010111", "0110111", "00110110", "00110111", "01100100", "01100101", "01101000", "01100111", "011001100", "011001101", "011010010", "011010011", "011010100", "011010101",
        "011010110", "011010111", "011011000", "011011001", "011011010", "011011011", "010011000", "010011001", "010011010", "011000", "010011011",
    ];

    private static readonly string[] s_blackTerm =
    [
        "0000110111", "010", "11", "10", "011", "0011", "0010", "00011", "000101", "000100", "0000100", "0000101", "0000111", "00000100", "00000111", "000011000",
        "0000010111", "0000011000", "0000001000", "00001100111", "00001101000", "00001101100", "00000110111", "00000101000", "00000010111", "00000011000", "000011001010", "000011001011", "000011001100", "000011001101", "000001101000", "000001101001",
        "000001101010", "000001101011", "000011010010", "000011010011", "000011010100", "000011010101", "000011010110", "000011010111", "000001101100", "000001101101", "000011011010", "000011011011", "000001010100", "000001010101", "000001010110", "000001010111",
        "000001100100", "000001100101", "000001010010", "000001010011", "000000100100", "000000110111", "000000111000", "000000100111", "000000101000", "000001011000", "000001011001", "000000101011", "000000101100", "000001011010", "000001100110", "000001100111",
    ];

    private static readonly string[] s_blackMakeup =
    [
        "0000001111", "000011001000", "000011001001", "000001011011", "000000110011", "000000110100", "000000110101", "0000001101100", "0000001101101", "0000001001010", "0000001001011", "0000001001100", "0000001001101", "0000001110010", "0000001110011", "0000001110100",
        "0000001110101", "0000001110110", "0000001110111", "0000001010010", "0000001010011", "0000001010100", "0000001010101", "0000001011010", "0000001011011", "0000001100100", "0000001100101",
    ];

    private static readonly string[] s_extMakeup =
    [
        "00000001000", "00000001100", "00000001101", "000000010010", "000000010011", "000000010100", "000000010101", "000000010110", "000000010111", "000000011100", "000000011101", "000000011110", "000000011111",
    ];

    // Longest code is 13 bits. Table entries: run length and code length (0 = no code starts with these bits).
    private const int PeekBits = 13;
    private static readonly short[] s_whiteRun = new short[1 << PeekBits];
    private static readonly byte[] s_whiteLen = new byte[1 << PeekBits];
    private static readonly short[] s_blackRun = new short[1 << PeekBits];
    private static readonly byte[] s_blackLen = new byte[1 << PeekBits];

    static CcittFax()
    {
        for (int i = 0; i < 64; i++)
        {
            Add(s_whiteRun, s_whiteLen, s_whiteTerm[i], i);
            Add(s_blackRun, s_blackLen, s_blackTerm[i], i);
        }

        for (int i = 0; i < s_whiteMakeup.Length; i++)
        {
            Add(s_whiteRun, s_whiteLen, s_whiteMakeup[i], 64 * (i + 1));
            Add(s_blackRun, s_blackLen, s_blackMakeup[i], 64 * (i + 1));
        }

        for (int i = 0; i < s_extMakeup.Length; i++)
        {
            Add(s_whiteRun, s_whiteLen, s_extMakeup[i], 1792 + (64 * i));
            Add(s_blackRun, s_blackLen, s_extMakeup[i], 1792 + (64 * i));
        }
    }

    /// <summary>The T.4 code word, as '0'/'1' characters, for a run of 0 to 2560 pixels (make-up code, if any, then terminating code); null beyond that.</summary>
    internal static string? CodeFor(bool black, int run)
    {
        if (run < 0 || run > 2560)
        {
            return null;
        }

        string prefix = "";
        if (run >= 1792)
        {
            int step = (run / 64) * 64;
            prefix = s_extMakeup[(step - 1792) / 64];
            run -= step;
        }
        else if (run >= 64)
        {
            int step = (run / 64) * 64;
            prefix = (black ? s_blackMakeup : s_whiteMakeup)[(step / 64) - 1];
            run -= step;
        }

        return prefix + (black ? s_blackTerm[run] : s_whiteTerm[run]);
    }

    private static void Add(short[] runs, byte[] lens, string code, int run)
    {
        int value = 0;
        foreach (char c in code)
        {
            value = (value << 1) | (c - '0');
        }

        int first = value << (PeekBits - code.Length);
        int count = 1 << (PeekBits - code.Length);
        for (int k = 0; k < count; k++)
        {
            runs[first + k] = (short)run;
            lens[first + k] = (byte)code.Length;
        }
    }

    private ref struct Bits
    {
        private readonly ReadOnlySpan<byte> _data;

        public Bits(ReadOnlySpan<byte> data)
        {
            _data = data;
            Position = 0;
        }

        /// <summary>Bit offset of the next unread bit; may run past the end after reading padding zeros.</summary>
        public long Position;

        public readonly long Left => ((long)_data.Length * 8) - Position;

        /// <summary>The next <paramref name="n"/> (at most 24) bits, zeros past the end, not consumed.</summary>
        public readonly int Peek(int n)
        {
            long at = Position >> 3;
            uint w = 0;
            if (at + 4 <= _data.Length)
            {
                int i = (int)at;
                w = ((uint)_data[i] << 24) | ((uint)_data[i + 1] << 16) | ((uint)_data[i + 2] << 8) | _data[i + 3];
            }
            else if (at < _data.Length)
            {
                for (int k = 0; k < 4; k++)
                {
                    w <<= 8;
                    if (at + k < _data.Length)
                    {
                        w |= _data[(int)at + k];
                    }
                }
            }

            return (int)((w << (int)(Position & 7)) >> (32 - n));
        }

        public void Skip(int n) => Position += n;

        public void Align() => Position = (Position + 7) & ~7L;
    }

    /// <summary>The change list of the row above; element 2i is a white-to-black change, 2i+1 black-to-white.</summary>
    private sealed class Line
    {
        public int[] Pos = new int[64];
        public int Count;

        public bool Add(int p, int width)
        {
            if (Count + 4 >= Pos.Length)
            {
                int bigger = (int)Math.Min((long)Pos.Length * 2, (long)width + 8);
                if (bigger > Pos.Length)
                {
                    Array.Resize(ref Pos, bigger);
                }
            }

            if (Count + 4 < Pos.Length)
            {
                Pos[Count++] = p;
                return true;
            }

            return false;
        }

        // Past the last change the row is a single run to the edge; b1/b2 searches rely on these.
        public void Seal(int width)
        {
            for (int i = 0; i < 4; i++)
            {
                if (Count + i >= Pos.Length)
                {
                    Array.Resize(ref Pos, Pos.Length + 4);
                }

                Pos[Count + i] = width;
            }
        }
    }

    private enum RowResult
    {
        Ok,
        Damaged,
        Truncated,
    }

    /// <summary>Decodes one CCITTFaxDecode stage into <paramref name="output"/>.</summary>
    public static void Decode(ReadOnlySpan<byte> input, FilterParameters p, OutputBuffer output, ref FilterIssues issues, ref string? message, CancellationToken ct)
    {
        int width = p.CcittColumns;
        if (width < 1 || width > MaxColumns)
        {
            issues |= FilterIssues.BadParameters;
            CompressedFilters.Note(ref message, $"CCITTFaxDecode /Columns {width} is outside 1 to {MaxColumns}.");
            return;
        }

        int rowBytes = (width + 7) >> 3;
        long rowsWanted = p.Rows > 0 ? p.Rows : long.MaxValue;
        int k = p.K;
        bool byteAlign = p.EncodedByteAlign;
        bool eolRequired = p.EndOfLine;
        byte white = p.BlackIs1 ? (byte)0x00 : (byte)0xFF;

        // Check the declared size before anything is sized from it.
        long cap = output.Cap;
        long maxRows = cap / rowBytes;
        if (maxRows == 0)
        {
            output.MarkExceeded();
            issues |= FilterIssues.LimitExceeded;
            CompressedFilters.Note(ref message, "A CCITT row is larger than the decoded-size limit.");
            return;
        }

        var bits = new Bits(input);
        var reference = new Line();
        var current = new Line();
        reference.Seal(width);
        byte[] row = new byte[rowBytes];
        long rows = 0;
        int sinceOneD = 0;
        bool anyDamage = false;

        while (rows < rowsWanted)
        {
            if ((rows & 63) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            if (rows >= maxRows)
            {
                output.MarkExceeded();
                issues |= FilterIssues.LimitExceeded;
                CompressedFilters.Note(ref message, "CCITT output reached the decoded-size limit.");
                return;
            }

            // --- Row start: fill bits, EOL, tag bit, byte alignment, end-of-block.
            bool oneD;
            if (k < 0)
            {
                if (byteAlign)
                {
                    bits.Align();
                }

                if (bits.Peek(12) == 1)
                {
                    break; // EOFB (two EOLs); one is enough to stop.
                }

                oneD = false;
            }
            else
            {
                int eols = 0;
                bool tagOneD = true;
                long save = bits.Position;
                bool endOfData = false;
                while (true)
                {
                    if (bits.Peek(12) == 1)
                    {
                        bits.Skip(12);
                        eols++;
                        if (k > 0)
                        {
                            // The tag bit follows every EOL, RTC's included.
                            tagOneD = bits.Peek(1) == 1;
                            bits.Skip(1);
                        }

                        if (eols >= 2)
                        {
                            endOfData = true; // RTC is six EOLs; two in a row never occur inside image data.
                            break;
                        }

                        continue;
                    }

                    if (eols == 0 && (eolRequired || k > 0) && bits.Peek(11) == 0 && bits.Left > 12)
                    {
                        // Fill zeros before an EOL.
                        bits.Skip(1);
                        continue;
                    }

                    break;
                }

                if (endOfData)
                {
                    break;
                }

                if (eols == 0)
                {
                    bits.Position = save;
                    if (byteAlign)
                    {
                        bits.Align();
                    }

                    oneD = k == 0 || sinceOneD % k == 0;
                }
                else
                {
                    oneD = k == 0 || tagOneD;
                }
            }

            if (bits.Left <= 0 || (bits.Left < 16 && bits.Peek((int)bits.Left) == 0))
            {
                break; // Only padding is left.
            }

            // --- The row itself.
            RowResult rr = oneD ? DecodeRow1D(ref bits, current, width) : DecodeRow2D(ref bits, reference, current, width);
            if (k > 0)
            {
                sinceOneD = oneD ? 1 : sinceOneD + 1;
            }

            if (rr == RowResult.Damaged)
            {
                anyDamage = true;
                if (k >= 0)
                {
                    // Skip to the next EOL; the damaged row repeats the row above (white when there is none).
                    bool found = false;
                    while (bits.Left > 12)
                    {
                        if (bits.Peek(12) == 1)
                        {
                            found = true;
                            break;
                        }

                        bits.Skip(1);
                    }

                    if (!found)
                    {
                        bits.Position = (long)input.Length * 8;
                    }

                    current.Count = 0;
                    for (int i = 0; i < reference.Count; i++)
                    {
                        current.Add(reference.Pos[i], width);
                    }
                }
            }

            Render(current, row, width, white, p.BlackIs1);
            if (!output.Put(row))
            {
                issues |= FilterIssues.LimitExceeded;
                CompressedFilters.Note(ref message, "CCITT output reached the decoded-size limit.");
                return;
            }

            rows++;
            if (rr == RowResult.Ok && bits.Left < 0)
            {
                rr = RowResult.Truncated; // the last code ran past the end of the data
            }

            if (rr == RowResult.Truncated)
            {
                issues |= FilterIssues.Truncated;
                CompressedFilters.Note(ref message, "CCITT data ends inside a row.");
                return;
            }

            if (rr == RowResult.Damaged && k < 0)
            {
                issues |= FilterIssues.DataError;
                CompressedFilters.Note(ref message, $"CCITT Group 4 data is invalid in row {rows}; decoding stopped.");
                return;
            }

            current.Seal(width);
            (reference, current) = (current, reference);
            current.Count = 0;
        }

        if (anyDamage)
        {
            issues |= FilterIssues.DataError;
            CompressedFilters.Note(ref message, "CCITT data had damaged rows, which were repaired or repeated.");
        }

        if (p.Rows > 0 && rows < p.Rows)
        {
            issues |= FilterIssues.Truncated;
            CompressedFilters.Note(ref message, $"CCITT data holds {rows} of the {p.Rows} declared rows.");
        }
    }

    // One run of the given colour: make-up codes followed by a terminating code. -1 for no valid code.
    private static int ReadRun(ref Bits bits, bool black)
    {
        short[] runs = black ? s_blackRun : s_whiteRun;
        byte[] lens = black ? s_blackLen : s_whiteLen;
        int total = 0;
        while (true)
        {
            int code = bits.Peek(PeekBits);
            int len = lens[code];
            if (len == 0 || bits.Left < len)
            {
                return -1;
            }

            bits.Skip(len);
            int run = runs[code];
            total += run;
            if (run < 64)
            {
                return total;
            }

            if (total > MaxColumns + 2560)
            {
                return -1;
            }
        }
    }

    private static RowResult DecodeRow1D(ref Bits bits, Line cur, int width)
    {
        cur.Count = 0;
        int a0 = 0;
        bool black = false;
        while (a0 < width)
        {
            int run = ReadRun(ref bits, black);
            if (run < 0)
            {
                return Classify(ref bits);
            }

            if (a0 + (long)run > width)
            {
                cur.Add(width, width);
                return RowResult.Damaged;
            }

            a0 += run;
            if (!cur.Add(a0, width)) { return RowResult.Damaged; }
            black = !black;
        }

        return RowResult.Ok;
    }

    // A code that fails within the last few bits of the data is a cut-off code, not damage: no code is longer than 13 bits.
    private static RowResult Classify(ref Bits bits) => bits.Left < PeekBits ? RowResult.Truncated : RowResult.Damaged;

    private static RowResult DecodeRow2D(ref Bits bits, Line reference, Line cur, int width)
    {
        cur.Count = 0;
        int a0 = -1;
        bool black = false;
        int bi = 0;
        int[] refPos = reference.Pos;
        int refCount = reference.Count;
        while (a0 < width)
        {
            // b1: first change on the reference row right of a0 whose colour is opposite to the current one.
            while (bi > 0 && refPos[bi - 1] > a0)
            {
                bi--;
            }

            while (bi < refCount && refPos[bi] <= a0)
            {
                bi++;
            }

            if ((bi & 1) != (black ? 1 : 0))
            {
                bi++;
            }

            int b1 = bi < refCount ? refPos[bi] : width;
            int code = bits.Peek(7);
            int a1;
            if (bits.Left <= 0)
            {
                return RowResult.Truncated;
            }

            if ((code & 0x40) != 0)
            {
                bits.Skip(1);
                a1 = b1;
            }
            else if ((code >> 4) == 3)
            {
                bits.Skip(3);
                a1 = b1 + 1;
            }
            else if ((code >> 4) == 2)
            {
                bits.Skip(3);
                a1 = b1 - 1;
            }
            else if ((code >> 4) == 1)
            {
                // Horizontal mode: two runs, the current colour first.
                bits.Skip(3);
                int start = Math.Max(a0, 0);
                int r1 = ReadRun(ref bits, black);
                int r2 = r1 < 0 ? -1 : ReadRun(ref bits, !black);
                if (r1 < 0 || r2 < 0)
                {
                    return Classify(ref bits);
                }

                long e1 = (long)start + r1;
                long e2 = e1 + r2;
                if (e2 > width)
                {
                    cur.Add((int)Math.Min(e1, width), width);
                    cur.Add(width, width);
                    return RowResult.Damaged;
                }

                if (!cur.Add((int)e1, width)) { return RowResult.Damaged; }
                if (!cur.Add((int)e2, width)) { return RowResult.Damaged; }
                a0 = (int)e2;
                if (a0 >= width)
                {
                    break;
                }

                continue;
            }
            else if ((code >> 3) == 1)
            {
                // Pass mode: a0 jumps to b2 and the colour stays.
                bits.Skip(4);
                int b2 = bi + 1 < refCount ? refPos[bi + 1] : width;
                a0 = b2;
                if (a0 >= width)
                {
                    break;
                }

                continue;
            }
            else if ((code >> 1) == 3)
            {
                bits.Skip(6);
                a1 = b1 + 2;
            }
            else if ((code >> 1) == 2)
            {
                bits.Skip(6);
                a1 = b1 - 2;
            }
            else if (code == 3)
            {
                bits.Skip(7);
                a1 = b1 + 3;
            }
            else if (code == 2)
            {
                bits.Skip(7);
                a1 = b1 - 3;
            }
            else
            {
                return Classify(ref bits);
            }

            if (a1 < Math.Max(a0, 0) || a1 > width)
            {
                return RowResult.Damaged;
            }

            if (!cur.Add(a1, width)) { return RowResult.Damaged; }
            a0 = a1;
            black = !black;
            if (a0 >= width)
            {
                break;
            }
        }

        return RowResult.Ok;
    }

    // Writes the row from its change list: runs alternate white, black, white, ...
    private static void Render(Line line, byte[] row, int width, byte white, bool blackIs1)
    {
        row.AsSpan().Fill(white);
        int n = line.Count;
        for (int i = 0; i < n; i += 2)
        {
            int s = line.Pos[i];
            int e = i + 1 < n ? line.Pos[i + 1] : width;
            if (s >= width)
            {
                break;
            }

            if (e > width)
            {
                e = width;
            }

            if (e > s)
            {
                FillBits(row, s, e, blackIs1);
            }
        }
    }

    private static void FillBits(byte[] row, int s, int e, bool set)
    {
        int sb = s >> 3;
        int eb = (e - 1) >> 3;
        int sm = 0xFF >> (s & 7);
        int em = (0xFF << (7 - ((e - 1) & 7))) & 0xFF;
        if (sb == eb)
        {
            int m = sm & em;
            row[sb] = (byte)(set ? row[sb] | m : row[sb] & ~m);
            return;
        }

        row[sb] = (byte)(set ? row[sb] | sm : row[sb] & ~sm);
        if (eb - sb > 1)
        {
            row.AsSpan(sb + 1, eb - sb - 1).Fill(set ? (byte)0xFF : (byte)0x00);
        }

        row[eb] = (byte)(set ? row[eb] | em : row[eb] & ~em);
    }
}
