namespace OnePdf.Asn1;

/// <summary>
/// Rewrites the BER framing of an encoding into the form DER requires (ITU-T X.690 clauses 8.1.3 and 10.1-10.2): indefinite
/// lengths become definite, and a constructed OCTET STRING becomes one primitive string with the same octets. Everything else
/// is copied byte for byte: values are never normalised, SET OF members are never re-ordered, and a definite-length subtree
/// with nothing to change is returned as it was, so the bytes a signature or certificate covers are untouched.
/// </summary>
/// <remarks>
/// A time-stamp token returned by an authority (RFC 3161 2.4.2) is a CMS SignedData that PAdES wants DER-encoded (ETSI EN 319 142-1:
/// the SignedData in <c>/Contents</c> is DER). Authorities nearly always answer in DER already; some write the outer ContentInfo with
/// indefinite lengths. Constructed forms of other string types and non-minimal definite lengths are left as they are.
/// </remarks>
internal static class BerToDer
{
    /// <summary>Most nesting levels rewritten; deeper input fails.</summary>
    private const int MaxDepth = 64;

    /// <summary>Largest result accepted.</summary>
    private const int MaxBytes = 1 << 22;

    /// <summary>
    /// Re-encodes the single element in <paramref name="data"/>. Returns false when it is malformed, nested deeper than 64 levels or
    /// larger than 4 MiB after the change. Input that needs no change comes back as a copy.
    /// </summary>
    public static bool TryConvert(ReadOnlyMemory<byte> data, out byte[] der)
    {
        der = [];
        if (!Asn1Reader.TryRead(data, out Asn1Element e, out int used, out _) || used != data.Length)
        {
            return false;
        }

        if (!TryRewrite(e, 0, out byte[]? result, out bool changed) || result is null)
        {
            return false;
        }

        der = changed ? result : data.ToArray();
        return true;
    }

    // Recursion follows nesting, which is capped by MaxDepth (a counter, as the reader does for indefinite lengths).
    private static bool TryRewrite(in Asn1Element e, int depth, out byte[]? result, out bool changed)
    {
        result = null;
        changed = false;
        if (depth > MaxDepth)
        {
            return false;
        }

        if (!e.Constructed)
        {
            result = e.Encoded.ToArray();
            return true;
        }

        if (e.TagClass == 0 && e.TagNumber == Asn1Tags.OctetString)
        {
            if (!Asn1Reader.TryOctetString(e, MaxBytes, out byte[] joined))
            {
                return false;
            }

            result = DerWriter.OctetString(joined);
            changed = true;
            return true;
        }

        var kids = new List<Asn1Element>();
        if (!Asn1Reader.TryChildren(e, 1 << 16, kids, out _))
        {
            return false;
        }

        var parts = new List<byte[]>(kids.Count);
        bool kidChanged = false;
        int total = 0;
        foreach (Asn1Element k in kids)
        {
            if (!TryRewrite(k, depth + 1, out byte[]? part, out bool c) || part is null)
            {
                return false;
            }

            kidChanged |= c;
            parts.Add(part);
            total += part.Length;
            if (total > MaxBytes)
            {
                return false;
            }
        }

        if (!kidChanged && !e.Indefinite)
        {
            result = e.Encoded.ToArray();
            return true;
        }

        // Keep the identifier octets (high tag numbers take several); only the length form changes.
        ReadOnlySpan<byte> enc = e.Encoded.Span;
        int idLength = 1;
        if ((enc[0] & 0x1F) == 0x1F)
        {
            while (idLength < enc.Length && (enc[idLength++] & 0x80) != 0)
            {
            }
        }

        int lengthBytes = total < 0x80 ? 1 : total <= 0xFF ? 2 : total <= 0xFFFF ? 3 : total <= 0xFFFFFF ? 4 : 5;
        result = new byte[idLength + lengthBytes + total];
        enc[..idLength].CopyTo(result);
        int at = idLength;
        if (lengthBytes == 1)
        {
            result[at++] = (byte)total;
        }
        else
        {
            result[at++] = (byte)(0x80 | (lengthBytes - 1));
            for (int i = lengthBytes - 2; i >= 0; i--)
            {
                result[at++] = (byte)(total >> (8 * i));
            }
        }

        foreach (byte[] p in parts)
        {
            p.CopyTo(result, at);
            at += p.Length;
        }

        changed = true;
        return true;
    }
}
