namespace OnePdf.Internal;

/// <summary>
/// The result of checking a signature's <c>/ByteRange</c> against the file (ISO 32000-2 12.8.1, "Signature dictionary"):
/// it must be <c>[0 b c d]</c>, the bytes from <c>b</c> to <c>c</c> must be exactly the hex string that is the value of
/// <c>/Contents</c>, and <c>c + d</c> must be where a revision ends, so that nothing of that revision is left unsigned.
/// All arithmetic is on validated, non-negative <see cref="long"/> values, and the file is only read once the bounds are known.
/// </summary>
internal sealed class ByteRangeInfo
{
    public PdfByteRangeStatus Status { get; private set; }

    public string? Detail { get; private set; }

    public long HoleStart { get; private set; }

    public long HoleEnd { get; private set; }

    public long SecondStart { get; private set; }

    public long SecondLength { get; private set; }

    /// <summary>Index into <see cref="PdfDocument.Revisions"/> of the covered revision.</summary>
    public int Revision { get; private set; } = -1;

    public bool DelimitersExcluded { get; private set; }

    public static ByteRangeInfo Analyze(PdfSignature sig)
    {
        var info = new ByteRangeInfo();
        info.Run(sig);
        return info;
    }

    private void Run(PdfSignature sig)
    {
        PdfDocument doc = sig.Document;
        if (sig.ByteRange is null || sig.RawContents.IsEmpty)
        {
            Fail(PdfByteRangeStatus.Missing, sig.ByteRange is null ? "The signature has no usable /ByteRange." : "The signature has no /Contents.");
            return;
        }

        if (sig.IndirectValues)
        {
            Fail(PdfByteRangeStatus.Malformed, "/Contents or /ByteRange is an indirect object; ISO 32000-2 12.8.1 requires direct values, so the signature is not trusted.");
            return;
        }

        IReadOnlyList<long> r = sig.ByteRange;
        if (r.Count != 4)
        {
            Fail(PdfByteRangeStatus.Malformed, $"/ByteRange has {r.Count} numbers; exactly four are supported.");
            return;
        }

        long a = r[0], b = r[1], c = r[2], d = r[3];
        if (a < 0 || b < 0 || c < 0 || d < 0)
        {
            Fail(PdfByteRangeStatus.Malformed, "/ByteRange has a negative number.");
            return;
        }

        if (a != 0)
        {
            Fail(PdfByteRangeStatus.DoesNotStartAtZero, "The first range does not start at offset 0.");
            return;
        }

        long length = doc.FileLength;
        if (b > length || c > length || d > length || c > length - d)
        {
            Fail(PdfByteRangeStatus.OutsideFile, "A range lies outside the file.");
            return;
        }

        // The hole holds at least "<>" and cannot start before the first range ends.
        if (c < b + 2)
        {
            Fail(PdfByteRangeStatus.OverlapOrGap, "The ranges overlap or leave no room for /Contents.");
            return;
        }

        HoleStart = b;
        HoleEnd = c;
        SecondStart = c;
        SecondLength = d;
        long hole = c - b;
        if (hole > doc.Limits.MaxMetadataBytes || hole > int.MaxValue - 2)
        {
            Fail(PdfByteRangeStatus.Malformed, "The excluded /Contents area is larger than the metadata limit.");
            return;
        }

        // Read one extra byte on each side so a hole that omits the angle brackets can be recognised.
        ReadOnlyMemory<byte> bytes = doc.ReadRange(b - 1 >= 0 ? b - 1 : b, (int)hole + (b >= 1 ? 1 : 0) + (c < length ? 1 : 0));
        int lead = b >= 1 ? 1 : 0;
        ReadOnlySpan<byte> h = bytes.Span.Slice(lead, (int)hole);
        bool bracketed = h.Length >= 2 && h[0] == (byte)'<' && h[^1] == (byte)'>';
        ReadOnlySpan<byte> hex;
        if (bracketed)
        {
            hex = h[1..^1];
        }
        else if (lead == 1 && c < length && bytes.Span[0] == (byte)'<' && bytes.Span[^1] == (byte)'>')
        {
            hex = h;
            DelimitersExcluded = true;
        }
        else
        {
            Fail(PdfByteRangeStatus.ContentsMismatch, "The bytes left out of the signature are not a hex string.");
            return;
        }

        byte[]? decoded = DecodeHex(hex);
        if (decoded is null)
        {
            Fail(PdfByteRangeStatus.ContentsMismatch, "The bytes left out of the signature are not a hex string.");
            return;
        }

        if (!sig.RawContents.Span.SequenceEqual(decoded))
        {
            Fail(PdfByteRangeStatus.ContentsMismatch, "The bytes left out of the signature are not the value of /Contents.");
            return;
        }

        // The signature dictionary has to be inside what is signed (an object in an object stream has no offset of its own).
        if (sig.Dictionary.SourceOffset >= b && sig.Dictionary.ContainerObjectNumber < 0)
        {
            Fail(PdfByteRangeStatus.ContentsMismatch, "The signature dictionary lies outside the signed bytes.");
            return;
        }

        long end = c + d;
        IReadOnlyList<PdfRevision> revs = doc.Revisions;
        for (int i = 0; i < revs.Count; i++)
        {
            long revEnd = revs[i].EndOffset;
            if (revEnd < end)
            {
                continue;
            }

            if (revEnd > end && !IsWhitespace(doc, end, revEnd))
            {
                break;
            }

            // A linearized file chains its first-page cross-reference section after the main one although it lies earlier in the
            // file; such a section belongs to the same saved state, not to a later update.
            while (i + 1 < revs.Count && revs[i + 1].EndOffset <= revs[i].EndOffset)
            {
                i++;
            }

            Revision = i;
            Status = PdfByteRangeStatus.Valid;
            return;
        }

        if (end == length && revs.Count > 0)
        {
            // Trailing bytes after the last %%EOF that the range includes.
            Revision = revs.Count - 1;
            Status = PdfByteRangeStatus.Valid;
            return;
        }

        Fail(PdfByteRangeStatus.DoesNotCoverRevision, "The byte ranges stop before the end of a revision, so part of it is not signed.");
    }

    private void Fail(PdfByteRangeStatus status, string detail)
    {
        Status = status;
        Detail = detail;
    }

    private static bool IsWhitespace(PdfDocument doc, long from, long to)
    {
        if (to - from > 64)
        {
            return false;
        }

        foreach (byte b in doc.ReadRange(from, (int)(to - from)).Span)
        {
            if (b is not (0 or 9 or 10 or 12 or 13 or 32))
            {
                return false;
            }
        }

        return true;
    }

    private static byte[]? DecodeHex(ReadOnlySpan<byte> hex)
    {
        var result = new byte[(hex.Length + 1) / 2];
        int n = 0;
        int high = -1;
        foreach (byte ch in hex)
        {
            int v = ch switch
            {
                >= (byte)'0' and <= (byte)'9' => ch - '0',
                >= (byte)'a' and <= (byte)'f' => ch - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => ch - 'A' + 10,
                0 or 9 or 10 or 12 or 13 or 32 => -2,
                _ => -1,
            };
            if (v == -2)
            {
                continue;
            }

            if (v < 0)
            {
                return null;
            }

            if (high < 0)
            {
                high = v;
            }
            else
            {
                result[n++] = (byte)((high << 4) | v);
                high = -1;
            }
        }

        if (high >= 0)
        {
            result[n++] = (byte)(high << 4);
        }

        return n == result.Length ? result : result.AsSpan(0, n).ToArray();
    }
}
