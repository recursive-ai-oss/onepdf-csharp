using System.Buffers.Binary;
using System.Text;

namespace OnePdf.PdfA;

/// <summary>
/// Builds a small ICC version 2 display profile for sRGB from the published parameters of IEC 61966-2-1: the Rec. 709 primaries, the
/// D65 white point and the sRGB tone curve. The profile is made here rather than copied from a vendor, so it carries no third-party
/// license. The bytes are the same on every run.
/// </summary>
/// <remarks>
/// Layout (ICC.1:2001-12, sections 6 and 10): a 128-byte header, a tag table, then the tags <c>desc</c>, <c>cprt</c>, <c>wtpt</c>,
/// <c>rXYZ</c>, <c>gXYZ</c>, <c>bXYZ</c> and one <c>curv</c> table shared by <c>rTRC</c>, <c>gTRC</c> and <c>bTRC</c>. The colorants are
/// adapted from D65 to the D50 profile connection space with the Bradford matrix, as the ICC specification requires.
/// </remarks>
internal static class SrgbProfile
{
    private const int CurvePoints = 1024;

    private static readonly Lazy<byte[]> s_bytes = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The profile bytes. The array is shared: do not modify it.</summary>
    public static byte[] Bytes => s_bytes.Value;

    /// <summary>Text recorded in the output intent's <c>/OutputConditionIdentifier</c>.</summary>
    public const string Identifier = "sRGB IEC61966-2.1";

    /// <summary>The profile description.</summary>
    public const string Description = "sRGB IEC61966-2.1 (generated from the published parameters)";

    private static byte[] Build()
    {
        // Chromaticities (IEC 61966-2-1 Table 1) and the D65 white point.
        double[] xy = [0.64, 0.33, 0.30, 0.60, 0.15, 0.06];
        double[] white65 = XyzOfXy(0.3127, 0.3290);
        double[] white50 = [0.9642, 1.0, 0.8249]; // ICC PCS illuminant D50 (ICC.1 6.1.3)

        // Column i of M holds the XYZ of primary i scaled so that R=G=B=1 gives the white point.
        double[][] prim = [XyzOfXy(xy[0], xy[1]), XyzOfXy(xy[2], xy[3]), XyzOfXy(xy[4], xy[5])];
        double[,] p = new double[3, 3];
        for (int c = 0; c < 3; c++)
        {
            for (int r = 0; r < 3; r++)
            {
                p[r, c] = prim[c][r];
            }
        }

        double[] s = Multiply(Invert(p), white65);
        double[,] m = new double[3, 3];
        for (int c = 0; c < 3; c++)
        {
            for (int r = 0; r < 3; r++)
            {
                m[r, c] = p[r, c] * s[c];
            }
        }

        // Bradford chromatic adaptation D65 -> D50.
        double[,] bradford =
        {
            { 0.8951, 0.2664, -0.1614 },
            { -0.7502, 1.7135, 0.0367 },
            { 0.0389, -0.0685, 1.0296 },
        };
        double[] src = Multiply(bradford, white65);
        double[] dst = Multiply(bradford, white50);
        double[,] scale = { { dst[0] / src[0], 0, 0 }, { 0, dst[1] / src[1], 0 }, { 0, 0, dst[2] / src[2] } };
        double[,] adapt = Multiply(Invert(bradford), Multiply(scale, bradford));
        double[,] d50 = Multiply(adapt, m);

        var tags = new List<(string Sig, byte[] Data)>
        {
            ("desc", DescTag(Description)),
            ("cprt", TextTag("Generated from the published sRGB parameters; no copyright is claimed.")),
            ("wtpt", XyzTag(white50[0], white50[1], white50[2])),
            ("rXYZ", XyzTag(d50[0, 0], d50[1, 0], d50[2, 0])),
            ("gXYZ", XyzTag(d50[0, 1], d50[1, 1], d50[2, 1])),
            ("bXYZ", XyzTag(d50[0, 2], d50[1, 2], d50[2, 2])),
        };
        byte[] curve = CurveTag();

        int tagCount = tags.Count + 3;
        int offset = 128 + 4 + (12 * tagCount);
        var table = new List<(string Sig, int Offset, int Size)>();
        var body = new MemoryStream();
        foreach ((string sig, byte[] data) in tags)
        {
            table.Add((sig, offset + (int)body.Length, data.Length));
            body.Write(data);
            Pad(body);
        }

        int curveAt = offset + (int)body.Length;
        body.Write(curve);
        Pad(body);
        table.Add(("rTRC", curveAt, curve.Length));
        table.Add(("gTRC", curveAt, curve.Length));
        table.Add(("bTRC", curveAt, curve.Length));
        table.Sort(static (a, b) => string.CompareOrdinal(a.Sig, b.Sig));

        int total = offset + (int)body.Length;
        byte[] profile = new byte[total];
        Span<byte> h = profile;
        BinaryPrimitives.WriteUInt32BigEndian(h, (uint)total);
        // CMM type 0; version 2.1.0 (ICC.1:2001-12).
        BinaryPrimitives.WriteUInt32BigEndian(h[8..], 0x02100000);
        Ascii(h[12..], "mntr");
        Ascii(h[16..], "RGB ");
        Ascii(h[20..], "XYZ ");
        // Creation date fixed so the bytes never change: 2000-01-01 00:00:00.
        BinaryPrimitives.WriteUInt16BigEndian(h[24..], 2000);
        BinaryPrimitives.WriteUInt16BigEndian(h[26..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(h[28..], 1);
        Ascii(h[36..], "acsp");
        // Platform, flags, manufacturer, model, attributes and rendering intent (perceptual) stay 0.
        BinaryPrimitives.WriteInt32BigEndian(h[68..], Fixed(white50[0]));
        BinaryPrimitives.WriteInt32BigEndian(h[72..], Fixed(white50[1]));
        BinaryPrimitives.WriteInt32BigEndian(h[76..], Fixed(white50[2]));

        BinaryPrimitives.WriteUInt32BigEndian(h[128..], (uint)tagCount);
        int at = 132;
        foreach ((string sig, int off, int size) in table)
        {
            Ascii(h[at..], sig);
            BinaryPrimitives.WriteUInt32BigEndian(h[(at + 4)..], (uint)off);
            BinaryPrimitives.WriteUInt32BigEndian(h[(at + 8)..], (uint)size);
            at += 12;
        }

        body.ToArray().CopyTo(h[offset..]);
        return profile;
    }

    private static void Pad(MemoryStream ms)
    {
        while ((ms.Length & 3) != 0)
        {
            ms.WriteByte(0);
        }
    }

    private static void Ascii(Span<byte> dest, string s) => Encoding.ASCII.GetBytes(s, dest);

    private static int Fixed(double v) => (int)Math.Round(v * 65536.0, MidpointRounding.AwayFromZero);

    // Type 'XYZ ' (ICC.1 10.27): signature, 4 reserved bytes, three s15Fixed16 numbers.
    private static byte[] XyzTag(double x, double y, double z)
    {
        byte[] b = new byte[20];
        Ascii(b, "XYZ ");
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(8), Fixed(x));
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(12), Fixed(y));
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(16), Fixed(z));
        return b;
    }

    // Type 'text' (ICC.1 10.18): signature, 4 reserved bytes, a NUL-terminated ASCII string.
    private static byte[] TextTag(string text)
    {
        byte[] b = new byte[8 + text.Length + 1];
        Ascii(b, "text");
        Ascii(b.AsSpan(8), text);
        return b;
    }

    // Type 'desc' (ICC.1:2001-12 6.5.7, textDescriptionType): ASCII, Unicode and ScriptCode parts; the last two stay empty.
    private static byte[] DescTag(string text)
    {
        int ascii = text.Length + 1;
        byte[] b = new byte[12 + ascii + 4 + 4 + 2 + 1 + 67];
        Ascii(b, "desc");
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), (uint)ascii);
        Ascii(b.AsSpan(12), text);
        return b;
    }

    // Type 'curv' (ICC.1 10.5): count, then uint16 samples of the curve from 0 to 1 (IEC 61966-2-1 section 5.2: the decoding function).
    private static byte[] CurveTag()
    {
        byte[] b = new byte[12 + (2 * CurvePoints)];
        Ascii(b, "curv");
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), CurvePoints);
        for (int i = 0; i < CurvePoints; i++)
        {
            double v = i / (double)(CurvePoints - 1);
            double lin = v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(12 + (2 * i)), (ushort)Math.Round(lin * 65535.0, MidpointRounding.AwayFromZero));
        }

        return b;
    }

    private static double[] XyzOfXy(double x, double y) => [x / y, 1.0, (1.0 - x - y) / y];

    private static double[] Multiply(double[,] a, double[] v)
    {
        double[] r = new double[3];
        for (int i = 0; i < 3; i++)
        {
            r[i] = (a[i, 0] * v[0]) + (a[i, 1] * v[1]) + (a[i, 2] * v[2]);
        }

        return r;
    }

    private static double[,] Multiply(double[,] a, double[,] b)
    {
        double[,] r = new double[3, 3];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                r[i, j] = (a[i, 0] * b[0, j]) + (a[i, 1] * b[1, j]) + (a[i, 2] * b[2, j]);
            }
        }

        return r;
    }

    private static double[,] Invert(double[,] a)
    {
        double det = (a[0, 0] * ((a[1, 1] * a[2, 2]) - (a[1, 2] * a[2, 1])))
            - (a[0, 1] * ((a[1, 0] * a[2, 2]) - (a[1, 2] * a[2, 0])))
            + (a[0, 2] * ((a[1, 0] * a[2, 1]) - (a[1, 1] * a[2, 0])));
        double[,] r = new double[3, 3];
        r[0, 0] = ((a[1, 1] * a[2, 2]) - (a[1, 2] * a[2, 1])) / det;
        r[0, 1] = ((a[0, 2] * a[2, 1]) - (a[0, 1] * a[2, 2])) / det;
        r[0, 2] = ((a[0, 1] * a[1, 2]) - (a[0, 2] * a[1, 1])) / det;
        r[1, 0] = ((a[1, 2] * a[2, 0]) - (a[1, 0] * a[2, 2])) / det;
        r[1, 1] = ((a[0, 0] * a[2, 2]) - (a[0, 2] * a[2, 0])) / det;
        r[1, 2] = ((a[0, 2] * a[1, 0]) - (a[0, 0] * a[1, 2])) / det;
        r[2, 0] = ((a[1, 0] * a[2, 1]) - (a[1, 1] * a[2, 0])) / det;
        r[2, 1] = ((a[0, 1] * a[2, 0]) - (a[0, 0] * a[2, 1])) / det;
        r[2, 2] = ((a[0, 0] * a[1, 1]) - (a[0, 1] * a[1, 0])) / det;
        return r;
    }
}
