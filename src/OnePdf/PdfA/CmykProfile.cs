using System.Buffers.Binary;

namespace OnePdf.PdfA;

/// <summary>
/// Builds an ICC version 2 output profile for CMYK, used as the output intent of a converted file whose colour is DeviceCMYK. It encodes
/// the conversion that ISO 32000-1 10.3.3 gives for DeviceCMYK on an RGB display (<c>r = 1 - min(1, c + k)</c>, and likewise for g and b),
/// so a reader that colour-manages through the profile shows the same colours as one that converts DeviceCMYK directly. It describes no
/// real press or ink set and says so in its description. It is made here from the published formulas, so it carries no third-party
/// license, and the bytes are the same on every run.
/// </summary>
/// <remarks>
/// Layout (ICC.1:2001-12): a 128-byte header (class <c>prtr</c>, space <c>CMYK</c>, connection space <c>Lab </c>), a tag table and the tags
/// <c>desc</c>, <c>cprt</c>, <c>wtpt</c> and <c>A2B0</c>, which is a <c>mft2</c> lookup table with a 9-point grid in each of the four inputs
/// holding L*a*b* in the 16-bit legacy encoding of ICC.1:2001-12 6.3.4.2. The same table serves the other two rendering intents (<c>A2B1</c>, <c>A2B2</c>).
/// </remarks>
internal static class CmykProfile
{
    private const int Grid = 9;

    private static readonly Lazy<byte[]> s_bytes = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The profile bytes. The array is shared: do not modify it.</summary>
    public static byte[] Bytes => s_bytes.Value;

    /// <summary>Text recorded in the output intent's <c>/OutputConditionIdentifier</c>.</summary>
    public const string Identifier = "Device CMYK (additive formula)";

    private const string Description = "Device CMYK as ISO 32000-1 converts it to RGB (generated; not a press profile)";

    private static byte[] Build()
    {
        double[] white50 = SrgbProfile.White50;
        byte[] lut = Lut();
        var tags = new List<(string Sig, byte[] Data)>
        {
            ("desc", SrgbProfile.DescTag(Description)),
            ("cprt", SrgbProfile.TextTag("Generated from the published DeviceCMYK conversion formula; no copyright is claimed.")),
            ("wtpt", SrgbProfile.XyzTag(white50[0], white50[1], white50[2])),
        };

        const int tagCount = 6;
        int offset = 128 + 4 + (12 * tagCount);
        var table = new List<(string Sig, int Offset, int Size)>();
        var body = new MemoryStream();
        foreach ((string sig, byte[] data) in tags)
        {
            table.Add((sig, offset + (int)body.Length, data.Length));
            body.Write(data);
            SrgbProfile.Pad(body);
        }

        int lutAt = offset + (int)body.Length;
        body.Write(lut);
        SrgbProfile.Pad(body);
        table.Add(("A2B0", lutAt, lut.Length));
        table.Add(("A2B1", lutAt, lut.Length));
        table.Add(("A2B2", lutAt, lut.Length));
        table.Sort(static (a, b) => string.CompareOrdinal(a.Sig, b.Sig));

        int total = offset + (int)body.Length;
        byte[] profile = new byte[total];
        Span<byte> h = profile;
        BinaryPrimitives.WriteUInt32BigEndian(h, (uint)total);
        BinaryPrimitives.WriteUInt32BigEndian(h[8..], 0x02100000);
        SrgbProfile.Ascii(h[12..], "prtr");
        SrgbProfile.Ascii(h[16..], "CMYK");
        SrgbProfile.Ascii(h[20..], "Lab ");
        BinaryPrimitives.WriteUInt16BigEndian(h[24..], 2000);
        BinaryPrimitives.WriteUInt16BigEndian(h[26..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(h[28..], 1);
        SrgbProfile.Ascii(h[36..], "acsp");
        BinaryPrimitives.WriteInt32BigEndian(h[68..], SrgbProfile.Fixed(white50[0]));
        BinaryPrimitives.WriteInt32BigEndian(h[72..], SrgbProfile.Fixed(white50[1]));
        BinaryPrimitives.WriteInt32BigEndian(h[76..], SrgbProfile.Fixed(white50[2]));

        BinaryPrimitives.WriteUInt32BigEndian(h[128..], (uint)tagCount);
        int at = 132;
        foreach ((string sig, int off, int size) in table)
        {
            SrgbProfile.Ascii(h[at..], sig);
            BinaryPrimitives.WriteUInt32BigEndian(h[(at + 4)..], (uint)off);
            BinaryPrimitives.WriteUInt32BigEndian(h[(at + 8)..], (uint)size);
            at += 12;
        }

        body.ToArray().CopyTo(h[offset..]);
        return profile;
    }

    // Type 'mft2' (ICC.1 10.8): header, identity matrix, input tables, CLUT, output tables.
    private static byte[] Lut()
    {
        const int Entries = 2; // identity input and output curves need only their end points
        int clut = Grid * Grid * Grid * Grid * 3;
        byte[] b = new byte[52 + (4 * Entries * 2) + (clut * 2) + (3 * Entries * 2)];
        SrgbProfile.Ascii(b, "mft2");
        b[8] = 4; // input channels
        b[9] = 3; // output channels
        b[10] = Grid;
        // Matrix (used only when the input is XYZ): identity as s15Fixed16.
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(12), 0x10000);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(28), 0x10000);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(44), 0x10000);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(48), Entries);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(50), Entries);
        int p = 52;
        for (int ch = 0; ch < 4; ch++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(p), 0);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(p + 2), 65535);
            p += 4;
        }

        double[,] m = SrgbProfile.D50Colorants();
        double[] white = SrgbProfile.White50;
        // The first input (cyan) varies slowest (ICC.1 10.8: the first channel is the most significant index).
        for (int ic = 0; ic < Grid; ic++)
        {
            for (int im = 0; im < Grid; im++)
            {
                for (int iy = 0; iy < Grid; iy++)
                {
                    for (int ik = 0; ik < Grid; ik++)
                    {
                        double c = ic / (double)(Grid - 1), mm = im / (double)(Grid - 1), y = iy / (double)(Grid - 1), k = ik / (double)(Grid - 1);
                        double r = 1 - Math.Min(1, c + k), g = 1 - Math.Min(1, mm + k), bl = 1 - Math.Min(1, y + k);
                        double[] lab = ToLab(m, white, Linear(r), Linear(g), Linear(bl));
                        // 16-bit legacy Lab (ICC.1:2001-12 6.3.4.2): L 0..100 -> 0..0xFF00; a, b -128..127.996 -> 0..0xFFFF with 0 at 0x8000.
                        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(p), Clamp16(lab[0] / 100.0 * 0xFF00));
                        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(p + 2), Clamp16((lab[1] + 128.0) * 256.0));
                        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(p + 4), Clamp16((lab[2] + 128.0) * 256.0));
                        p += 6;
                    }
                }
            }
        }

        for (int ch = 0; ch < 3; ch++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(p), 0);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(p + 2), 65535);
            p += 4;
        }

        return b;
    }

    private static ushort Clamp16(double v) => (ushort)Math.Clamp((int)Math.Round(v, MidpointRounding.AwayFromZero), 0, 65535);

    // sRGB decoding function (IEC 61966-2-1 5.2).
    private static double Linear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    // CIE 1976 L*a*b* from linear RGB through the D50 colorant matrix.
    private static double[] ToLab(double[,] m, double[] white, double r, double g, double b)
    {
        double x = (m[0, 0] * r) + (m[0, 1] * g) + (m[0, 2] * b);
        double y = (m[1, 0] * r) + (m[1, 1] * g) + (m[1, 2] * b);
        double z = (m[2, 0] * r) + (m[2, 1] * g) + (m[2, 2] * b);
        double fx = F(x / white[0]), fy = F(y / white[1]), fz = F(z / white[2]);
        return [(116 * fy) - 16, 500 * (fx - fy), 200 * (fy - fz)];
    }

    private static double F(double t) => t > 216.0 / 24389.0 ? Math.Cbrt(t) : (((24389.0 / 27.0) * t) + 16) / 116.0;
}
