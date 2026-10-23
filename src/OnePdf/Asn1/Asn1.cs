using System.Globalization;
using System.Text;

namespace OnePdf.Asn1;

/// <summary>
/// One decoded BER/DER element: its tag, its content and its complete encoding. Slices point into the buffer that was read;
/// nothing is copied.
/// </summary>
internal readonly struct Asn1Element
{
    public Asn1Element(int tagClass, bool constructed, int tagNumber, ReadOnlyMemory<byte> content, ReadOnlyMemory<byte> encoded, bool indefinite)
    {
        TagClass = tagClass;
        Constructed = constructed;
        TagNumber = tagNumber;
        Content = content;
        Encoded = encoded;
        Indefinite = indefinite;
    }

    /// <summary>0 universal, 1 application, 2 context-specific, 3 private.</summary>
    public int TagClass { get; }

    public bool Constructed { get; }

    public int TagNumber { get; }

    /// <summary>The content octets; for an indefinite length, without the end-of-contents marker.</summary>
    public ReadOnlyMemory<byte> Content { get; }

    /// <summary>Tag, length, content (and end-of-contents for an indefinite length).</summary>
    public ReadOnlyMemory<byte> Encoded { get; }

    public bool Indefinite { get; }

    public bool IsUniversal(int tagNumber) => TagClass == 0 && TagNumber == tagNumber;

    public bool IsContext(int tagNumber) => TagClass == 2 && TagNumber == tagNumber;

    public bool IsSequence => TagClass == 0 && Constructed && TagNumber == Asn1Tags.Sequence;

    public bool IsSet => TagClass == 0 && Constructed && TagNumber == Asn1Tags.Set;
}

internal static class Asn1Tags
{
    public const int Boolean = 1;
    public const int Integer = 2;
    public const int BitString = 3;
    public const int OctetString = 4;
    public const int Null = 5;
    public const int ObjectIdentifier = 6;
    public const int Enumerated = 10;
    public const int Utf8String = 12;
    public const int Sequence = 16;
    public const int Set = 17;
    public const int UtcTime = 23;
    public const int GeneralizedTime = 24;
}

/// <summary>
/// A bounded reader for BER (and so DER) as used in CMS and X.509 (ITU-T X.690). Everything is length-checked before a slice
/// is taken, nesting is limited, and indefinite lengths are resolved with an explicit counter rather than recursion, so
/// hostile input costs at most a linear scan per nesting level.
/// </summary>
internal static class Asn1Reader
{
    /// <summary>Deepest constructed nesting accepted while looking for the end of an indefinite-length element.</summary>
    public const int MaxDepth = 32;

    /// <summary>Most elements examined while looking for the end of one indefinite-length element.</summary>
    public const int MaxScanElements = 1 << 20;

    /// <summary>Reads one element at the start of <paramref name="data"/>; <paramref name="consumed"/> is its total encoded length.</summary>
    public static bool TryRead(ReadOnlyMemory<byte> data, out Asn1Element element, out int consumed, out string? error)
    {
        element = default;
        consumed = 0;
        ReadOnlySpan<byte> span = data.Span;
        if (!TryHeader(span, 0, out Header h, out error))
        {
            return false;
        }

        if (h.Length >= 0)
        {
            if (h.Length > span.Length - h.HeaderLength)
            {
                error = "The element is longer than the data that holds it.";
                return false;
            }

            int total = h.HeaderLength + h.Length;
            element = new Asn1Element(h.Class, h.Constructed, h.Tag, data.Slice(h.HeaderLength, h.Length), data[..total], false);
            consumed = total;
            return true;
        }

        if (!h.Constructed)
        {
            error = "A primitive element has an indefinite length.";
            return false;
        }

        if (!FindEndOfContents(span, h.HeaderLength, out int contentLength, out error))
        {
            return false;
        }

        int all = h.HeaderLength + contentLength + 2;
        element = new Asn1Element(h.Class, true, h.Tag, data.Slice(h.HeaderLength, contentLength), data[..all], true);
        consumed = all;
        return true;
    }

    /// <summary>Reads exactly one element that fills <paramref name="data"/> apart from trailing zero padding.</summary>
    public static bool TryReadPadded(ReadOnlyMemory<byte> data, out Asn1Element element, out string? error)
    {
        if (!TryRead(data, out element, out int used, out error))
        {
            return false;
        }

        foreach (byte b in data.Span[used..])
        {
            if (b != 0)
            {
                error = "Unexpected data follows the element.";
                return false;
            }
        }

        return true;
    }

    /// <summary>Splits a constructed element's content into its children, at most <paramref name="max"/> of them.</summary>
    public static bool TryChildren(in Asn1Element parent, int max, List<Asn1Element> into, out string? error)
    {
        error = null;
        ReadOnlyMemory<byte> rest = parent.Content;
        while (!rest.IsEmpty)
        {
            if (into.Count >= max)
            {
                error = "Too many elements.";
                return false;
            }

            if (!TryRead(rest, out Asn1Element child, out int used, out error))
            {
                return false;
            }

            into.Add(child);
            rest = rest[used..];
        }

        return true;
    }

    /// <summary>
    /// The octets of an OCTET STRING, joining the pieces of a constructed (BER) one. Fails above <paramref name="maxBytes"/>.
    /// </summary>
    public static bool TryOctetString(in Asn1Element e, int maxBytes, out byte[] bytes)
    {
        bytes = [];
        if (e.TagClass != 0 || e.TagNumber != Asn1Tags.OctetString)
        {
            return false;
        }

        if (!e.Constructed)
        {
            if (e.Content.Length > maxBytes)
            {
                return false;
            }

            bytes = e.Content.ToArray();
            return true;
        }

        // Constructed form: walk the nested pieces with an explicit stack.
        using var ms = new MemoryStream();
        var stack = new Stack<(ReadOnlyMemory<byte> Data, int Depth)>();
        stack.Push((e.Content, 1));
        int elements = 0;
        while (stack.Count > 0)
        {
            (ReadOnlyMemory<byte> rest, int depth) = stack.Pop();
            if (rest.IsEmpty)
            {
                continue;
            }

            if (++elements > 4096 || !TryRead(rest, out Asn1Element piece, out int used, out _) || piece.TagClass != 0 || piece.TagNumber != Asn1Tags.OctetString)
            {
                return false;
            }

            stack.Push((rest[used..], depth));
            if (piece.Constructed)
            {
                if (depth >= 4)
                {
                    return false;
                }

                stack.Push((piece.Content, depth + 1));
            }
            else
            {
                if (ms.Length + piece.Content.Length > maxBytes)
                {
                    return false;
                }

                ms.Write(piece.Content.Span);
            }
        }

        bytes = ms.ToArray();
        return true;
    }

    /// <summary>The dotted form of an OBJECT IDENTIFIER, or null when it is malformed or an arc overflows 64 bits.</summary>
    public static string? TryOid(in Asn1Element e)
    {
        if (e.TagClass != 0 || e.TagNumber != Asn1Tags.ObjectIdentifier || e.Constructed)
        {
            return null;
        }

        ReadOnlySpan<byte> s = e.Content.Span;
        if (s.Length is 0 or > 128)
        {
            return null;
        }

        var sb = new StringBuilder();
        ulong acc = 0;
        bool first = true;
        for (int i = 0; i < s.Length; i++)
        {
            byte b = s[i];
            if (acc > (ulong.MaxValue >> 7))
            {
                return null;
            }

            acc = (acc << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) != 0)
            {
                if (i == s.Length - 1 || (acc == 0 && b == 0x80))
                {
                    return null;
                }

                continue;
            }

            if (first)
            {
                ulong a = acc < 80 ? acc / 40 : 2;
                sb.Append(a.ToString(CultureInfo.InvariantCulture)).Append('.').Append((acc - (a * 40)).ToString(CultureInfo.InvariantCulture));
                first = false;
            }
            else
            {
                sb.Append('.').Append(acc.ToString(CultureInfo.InvariantCulture));
            }

            acc = 0;
        }

        return sb.ToString();
    }

    /// <summary>A small non-negative INTEGER or ENUMERATED, or -1.</summary>
    public static int TrySmallInt(in Asn1Element e)
    {
        if (e.TagClass != 0 || e.Constructed || (e.TagNumber != Asn1Tags.Integer && e.TagNumber != Asn1Tags.Enumerated))
        {
            return -1;
        }

        ReadOnlySpan<byte> s = e.Content.Span;
        if (s.Length is 0 or > 4 || (s[0] & 0x80) != 0)
        {
            return -1;
        }

        int v = 0;
        foreach (byte b in s)
        {
            v = (v << 8) | b;
        }

        return v < 0 ? -1 : v;
    }

    /// <summary>The magnitude bytes of an INTEGER with leading zero octets removed.</summary>
    public static ReadOnlySpan<byte> IntegerMagnitude(in Asn1Element e)
    {
        ReadOnlySpan<byte> s = e.Content.Span;
        while (s.Length > 1 && s[0] == 0)
        {
            s = s[1..];
        }

        return s;
    }

    /// <summary>Parses UTCTime (two-digit years 50-99 are 19xx, ISO/RFC 5280) or GeneralizedTime in UTC.</summary>
    public static bool TryTime(in Asn1Element e, out DateTimeOffset value)
    {
        value = default;
        if (e.TagClass != 0 || e.Constructed || (e.TagNumber != Asn1Tags.UtcTime && e.TagNumber != Asn1Tags.GeneralizedTime))
        {
            return false;
        }

        ReadOnlySpan<byte> s = e.Content.Span;
        if (s.Length is < 11 or > 40)
        {
            return false;
        }

        int pos = 0;
        int year;
        if (e.TagNumber == Asn1Tags.UtcTime)
        {
            if (!Num(s, ref pos, 2, out year))
            {
                return false;
            }

            year += year >= 50 ? 1900 : 2000;
        }
        else if (!Num(s, ref pos, 4, out year))
        {
            return false;
        }

        if (!Num(s, ref pos, 2, out int month) || !Num(s, ref pos, 2, out int day) || !Num(s, ref pos, 2, out int hour) || !Num(s, ref pos, 2, out int minute))
        {
            return false;
        }

        int second = 0;
        if (pos < s.Length && s[pos] is >= (byte)'0' and <= (byte)'9' && !Num(s, ref pos, 2, out second))
        {
            return false;
        }

        long ticks = 0;
        if (pos < s.Length && s[pos] is (byte)'.' or (byte)',')
        {
            pos++;
            long scale = 1_000_000;
            while (pos < s.Length && s[pos] is >= (byte)'0' and <= (byte)'9')
            {
                if (scale > 0)
                {
                    ticks += (s[pos] - '0') * scale;
                    scale /= 10;
                }

                pos++;
            }
        }

        if (pos != s.Length - 1 || s[pos] != (byte)'Z' || year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 60)
        {
            return false;
        }

        value = new DateTimeOffset(year, month, day, hour, minute, Math.Min(second, 59), TimeSpan.Zero).AddTicks(ticks);
        return true;
    }

    private static bool Num(ReadOnlySpan<byte> s, ref int pos, int digits, out int value)
    {
        value = 0;
        if (pos + digits > s.Length)
        {
            return false;
        }

        for (int i = 0; i < digits; i++)
        {
            int d = s[pos + i] - '0';
            if ((uint)d > 9)
            {
                return false;
            }

            value = (value * 10) + d;
        }

        pos += digits;
        return true;
    }

    private readonly record struct Header(int Class, bool Constructed, int Tag, int HeaderLength, int Length);

    // Length < 0 means indefinite.
    private static bool TryHeader(ReadOnlySpan<byte> s, int at, out Header h, out string? error)
    {
        h = default;
        error = null;
        if (at >= s.Length)
        {
            error = "Truncated element.";
            return false;
        }

        int pos = at;
        byte b0 = s[pos++];
        int cls = b0 >> 6;
        bool constructed = (b0 & 0x20) != 0;
        int tag = b0 & 0x1F;
        if (tag == 0x1F)
        {
            tag = 0;
            int n = 0;
            while (true)
            {
                if (pos >= s.Length || ++n > 4)
                {
                    error = "Bad high tag number.";
                    return false;
                }

                byte t = s[pos++];
                tag = (tag << 7) | (t & 0x7F);
                if ((t & 0x80) == 0)
                {
                    break;
                }
            }
        }

        if (pos >= s.Length)
        {
            error = "Truncated element.";
            return false;
        }

        byte l0 = s[pos++];
        int length;
        if (l0 < 0x80)
        {
            length = l0;
        }
        else if (l0 == 0x80)
        {
            length = -1;
        }
        else
        {
            int count = l0 & 0x7F;
            if (count > 4 || pos + count > s.Length)
            {
                error = count > 4 ? "The declared length is too large." : "Truncated length.";
                return false;
            }

            uint v = 0;
            for (int i = 0; i < count; i++)
            {
                v = (v << 8) | s[pos++];
            }

            if (v > int.MaxValue)
            {
                error = "The declared length is too large.";
                return false;
            }

            length = (int)v;
        }

        h = new Header(cls, constructed, tag, pos - at, length);
        return true;
    }

    // Finds the end of an indefinite-length element whose content starts at 'start'. A counter of open indefinite elements
    // stands in for recursion; definite-length children are skipped whole.
    private static bool FindEndOfContents(ReadOnlySpan<byte> s, int start, out int contentLength, out string? error)
    {
        contentLength = 0;
        int depth = 1;
        int pos = start;
        int elements = 0;
        while (depth > 0)
        {
            if (++elements > MaxScanElements)
            {
                error = "Too many elements.";
                return false;
            }

            if (pos + 2 <= s.Length && s[pos] == 0 && s[pos + 1] == 0)
            {
                depth--;
                pos += 2;
                if (depth == 0)
                {
                    contentLength = pos - 2 - start;
                }

                continue;
            }

            if (!TryHeader(s, pos, out Header h, out error))
            {
                return false;
            }

            if (h.Length < 0)
            {
                if (!h.Constructed || ++depth > MaxDepth)
                {
                    error = "Indefinite lengths are nested too deeply.";
                    return false;
                }

                pos += h.HeaderLength;
            }
            else
            {
                if (h.Length > s.Length - pos - h.HeaderLength)
                {
                    error = "The element is longer than the data that holds it.";
                    return false;
                }

                pos += h.HeaderLength + h.Length;
            }
        }

        error = null;
        return true;
    }
}
