using System.Globalization;
using System.Numerics;

namespace OnePdf.Asn1;

/// <summary>
/// A small writer for DER (ITU-T X.690 clause 10): definite lengths in the shortest form, SET OF members in encoded order.
/// Every function returns a complete encoding, so structures are built from the inside out. Sizes are computed with checked
/// arithmetic; nothing here reads input from a file.
/// </summary>
internal static class DerWriter
{
    /// <summary>Tag, definite length and the concatenated <paramref name="parts"/>.</summary>
    public static byte[] Tlv(byte tag, params ReadOnlySpan<byte[]> parts)
    {
        int n = 0;
        foreach (byte[] p in parts)
        {
            n = checked(n + p.Length);
        }

        int headerLength = 2 + (n < 0x80 ? 0 : n <= 0xFF ? 1 : n <= 0xFFFF ? 2 : n <= 0xFFFFFF ? 3 : 4);
        var result = new byte[checked(headerLength + n)];
        result[0] = tag;
        int at = 1;
        if (n < 0x80)
        {
            result[at++] = (byte)n;
        }
        else
        {
            int count = n <= 0xFF ? 1 : n <= 0xFFFF ? 2 : n <= 0xFFFFFF ? 3 : 4;
            result[at++] = (byte)(0x80 | count);
            for (int i = count - 1; i >= 0; i--)
            {
                result[at++] = (byte)(n >> (8 * i));
            }
        }

        foreach (byte[] p in parts)
        {
            p.CopyTo(result, at);
            at += p.Length;
        }

        return result;
    }

    public static byte[] Sequence(params ReadOnlySpan<byte[]> parts) => Tlv(0x30, parts);

    public static byte[] Set(params ReadOnlySpan<byte[]> parts) => Tlv(0x31, parts);

    /// <summary>SET OF: the members sorted by their encodings, as DER requires (X.690 11.6).</summary>
    public static byte[] SetOf(IEnumerable<byte[]> members)
    {
        var list = new List<byte[]>(members);
        list.Sort(static (a, b) => a.AsSpan().SequenceCompareTo(b));
        return Tlv(0x31, list.ToArray());
    }

    public static byte[] OctetString(ReadOnlySpan<byte> data) => Tlv(0x04, data.ToArray());

    public static byte[] Null() => [0x05, 0x00];

    public static byte[] Boolean(bool value) => [0x01, 0x01, value ? (byte)0xFF : (byte)0x00];

    public static byte[] Integer(long value) => Tlv(0x02, new BigInteger(value).ToByteArray(isUnsigned: false, isBigEndian: true));

    /// <summary>A non-negative INTEGER from big-endian magnitude bytes (a leading zero octet is added when the top bit is set).</summary>
    public static byte[] UnsignedInteger(ReadOnlySpan<byte> magnitude)
    {
        while (magnitude.Length > 1 && magnitude[0] == 0)
        {
            magnitude = magnitude[1..];
        }

        if (magnitude.IsEmpty)
        {
            return Tlv(0x02, new byte[] { 0 });
        }

        return (magnitude[0] & 0x80) != 0 ? Tlv(0x02, new byte[] { 0 }, magnitude.ToArray()) : Tlv(0x02, magnitude.ToArray());
    }

    public static byte[] Oid(string dotted)
    {
        string[] parts = dotted.Split('.');
        if (parts.Length < 2)
        {
            throw new ArgumentException("An object identifier needs at least two arcs.", nameof(dotted));
        }

        var bytes = new List<byte>();
        ulong first = ulong.Parse(parts[0], CultureInfo.InvariantCulture);
        ulong second = ulong.Parse(parts[1], CultureInfo.InvariantCulture);
        if (first > 2 || (first < 2 && second > 39))
        {
            throw new ArgumentException("The first arcs of the object identifier are out of range.", nameof(dotted));
        }

        Arc(bytes, checked((first * 40) + second));
        for (int i = 2; i < parts.Length; i++)
        {
            Arc(bytes, ulong.Parse(parts[i], CultureInfo.InvariantCulture));
        }

        return Tlv(0x06, bytes.ToArray());
    }

    private static void Arc(List<byte> into, ulong value)
    {
        int start = into.Count;
        into.Add((byte)(value & 0x7F));
        value >>= 7;
        while (value != 0)
        {
            into.Insert(start, (byte)(0x80 | (value & 0x7F)));
            value >>= 7;
        }
    }

    /// <summary>[n] EXPLICIT: a constructed context-specific wrapper around <paramref name="parts"/>.</summary>
    public static byte[] Context(int n, params ReadOnlySpan<byte[]> parts) => Tlv(checked((byte)(0xA0 | n)), parts);

    /// <summary>[n] IMPLICIT primitive.</summary>
    public static byte[] ContextPrimitive(int n, ReadOnlySpan<byte> data) => Tlv(checked((byte)(0x80 | n)), data.ToArray());

    public static byte[] Utf8String(string s) => Tlv(0x0C, System.Text.Encoding.UTF8.GetBytes(s));

    /// <summary>GeneralizedTime in UTC, whole seconds (RFC 5280 4.1.2.5.2 form).</summary>
    public static byte[] GeneralizedTime(DateTimeOffset t) =>
        Tlv(0x18, System.Text.Encoding.ASCII.GetBytes(t.UtcDateTime.ToString("yyyyMMddHHmmss'Z'", CultureInfo.InvariantCulture)));

    /// <summary>UTCTime in UTC (years 1950 to 2049).</summary>
    public static byte[] UtcTime(DateTimeOffset t) =>
        Tlv(0x17, System.Text.Encoding.ASCII.GetBytes(t.UtcDateTime.ToString("yyMMddHHmmss'Z'", CultureInfo.InvariantCulture)));

    /// <summary>Changes the outer tag of an encoding, keeping length and content (a SET OF becomes an IMPLICIT [0], for example).</summary>
    public static byte[] Retag(byte[] encoding, byte tag)
    {
        byte[] copy = (byte[])encoding.Clone();
        copy[0] = tag;
        return copy;
    }
}
