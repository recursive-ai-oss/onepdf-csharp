using System.Text;
using OnePdf.FontPrograms;

namespace OnePdf.Tests.Fonts;

/// <summary>Builds small, valid Type 1 fonts for tests: charstring and eexec encryption, hex or binary eexec, PFB wrapping.</summary>
internal static class Type1Builder
{
    private static readonly Dictionary<string, int> Ops = new()
    {
        ["hstem"] = 1, ["vstem"] = 3, ["vmoveto"] = 4, ["rlineto"] = 5, ["hlineto"] = 6, ["vlineto"] = 7, ["rrcurveto"] = 8,
        ["closepath"] = 9, ["callsubr"] = 10, ["return"] = 11, ["hsbw"] = 13, ["endchar"] = 14, ["rmoveto"] = 21, ["hmoveto"] = 22,
        ["vhcurveto"] = 30, ["hvcurveto"] = 31,
        ["dotsection"] = 0x0C00, ["seac"] = 0x0C06, ["sbw"] = 0x0C07, ["div"] = 0x0C0C, ["callothersubr"] = 0x0C10, ["pop"] = 0x0C11,
        ["setcurrentpoint"] = 0x0C21,
    };

    public static byte[] Charstring(params object[] items)
    {
        var b = new List<byte>();
        foreach (object item in items)
        {
            if (item is int n)
            {
                if (n >= -107 && n <= 107)
                {
                    b.Add((byte)(n + 139));
                }
                else if (n >= 108 && n <= 1131)
                {
                    int v = n - 108;
                    b.Add((byte)(247 + (v >> 8)));
                    b.Add((byte)(v & 255));
                }
                else if (n >= -1131 && n <= -108)
                {
                    int v = -n - 108;
                    b.Add((byte)(251 + (v >> 8)));
                    b.Add((byte)(v & 255));
                }
                else
                {
                    b.Add(255);
                    b.Add((byte)(n >> 24));
                    b.Add((byte)(n >> 16));
                    b.Add((byte)(n >> 8));
                    b.Add((byte)n);
                }
            }
            else
            {
                int op = Ops[(string)item];
                if (op > 255)
                {
                    b.Add(12);
                    b.Add((byte)(op & 255));
                }
                else
                {
                    b.Add((byte)op);
                }
            }
        }

        return [.. b];
    }

    public static byte[] Encrypt(byte[] plain, ushort key, int lead)
    {
        var data = new byte[lead + plain.Length];
        for (int i = 0; i < lead; i++)
        {
            data[i] = (byte)(65 + i); // arbitrary lead bytes
        }

        plain.CopyTo(data, lead);
        uint r = key;
        var cipher = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            byte c = (byte)(data[i] ^ (r >> 8));
            cipher[i] = c;
            r = (((c + r) * 52845u) + 22719u) & 0xFFFF;
        }

        return cipher;
    }

    /// <summary>Glyph names and their charstrings (unencrypted). The test font has: .notdef, A (a square), acute, Aacute (seac), F (flex), H (hint replacement).</summary>
    public static byte[] Font(bool hexEexec = false, bool pfb = false, int lenIV = 4, bool compactCharStringsTag = false)
    {
        byte[][] subrs =
        [
            Charstring(3, 0, "callothersubr", "pop", "pop", "setcurrentpoint", "return"),
            Charstring(0, 1, "callothersubr", "return"),
            Charstring(0, 2, "callothersubr", "return"),
            Charstring("return"),
            Charstring(0, 10, "hstem", "return"),
        ];
        var glyphs = new List<(string Name, byte[] Code)>
        {
            (".notdef", Charstring(0, 500, "hsbw", "endchar")),
            ("A", Charstring(50, 600, "hsbw", 100, 0, "rmoveto", 400, "hlineto", 500, "vlineto", -400, "hlineto", "closepath", "endchar")),
            ("acute", Charstring(30, 300, "hsbw", 0, 600, "rmoveto", 100, "hlineto", 100, "vlineto", -100, "hlineto", "closepath", "endchar")),
            ("Aacute", Charstring(50, 600, "hsbw", 30, 200, 0, 65, 194, "seac")),
            ("F", Charstring(
                0, 700, "hsbw", 0, 0, "rmoveto", 1, "callsubr",
                150, 0, "rmoveto", 2, "callsubr",
                -100, 0, "rmoveto", 2, "callsubr",
                50, 20, "rmoveto", 2, "callsubr",
                50, 10, "rmoveto", 2, "callsubr",
                50, -10, "rmoveto", 2, "callsubr",
                50, -20, "rmoveto", 2, "callsubr",
                50, 0, "rmoveto", 2, "callsubr",
                50, 300, 0, 0, "callsubr",
                0, 100, "rlineto", "closepath", "endchar")),
            ("H", Charstring(0, 800, "hsbw", 4, 1, 3, "callothersubr", "pop", "callsubr", 10, 20, "rmoveto", 1000, 3, "div", "hlineto", 100, "vlineto", "closepath", "endchar")),
        };

        var ms = new MemoryStream();
        void Text(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        Text("dup /Private 8 dict dup begin\n/RD {string currentfile exch readstring pop} executeonly def\n/ND {noaccess def} executeonly def\n/NP {noaccess put} executeonly def\n");
        Text("/BlueValues [] def\n/lenIV " + lenIV.ToString(System.Globalization.CultureInfo.InvariantCulture) + " def\n");
        Text("/Subrs " + subrs.Length + " array\n");
        for (int i = 0; i < subrs.Length; i++)
        {
            byte[] enc = lenIV >= 0 ? Encrypt(subrs[i], 4330, lenIV) : subrs[i];
            Text("dup " + i + " " + enc.Length + " RD ");
            ms.Write(enc);
            Text(" NP\n");
        }

        // Some producers write the dictionary entry as "dup/CharStrings" straight after the last Subrs entry.
        Text(compactCharStringsTag ? "ND\nend noaccess put\ndup/CharStrings " + glyphs.Count + " dict dup begin\n" : "ND\n2 index /CharStrings " + glyphs.Count + " dict dup begin\n");
        foreach ((string name, byte[] code) in glyphs)
        {
            byte[] enc = lenIV >= 0 ? Encrypt(code, 4330, lenIV) : code;
            Text("/" + name + " " + enc.Length + " RD ");
            ms.Write(enc);
            Text(" ND\n");
        }

        Text("end\nend\nreadonly put\nnoaccess put\ndup /FontName get exch definefont pop\nmark currentfile closefile\n");
        byte[] cipher = Encrypt(ms.ToArray(), 55665, 4);

        string head =
            "%!PS-AdobeFont-1.0: TestFont 001.000\n11 dict begin\n/FontInfo 1 dict dup begin /version (001) readonly def end readonly def\n" +
            "/FontName /TestFont def\n/PaintType 0 def\n/FontType 1 def\n/FontMatrix [0.001 0 0 0.001 0 0] readonly def\n" +
            "/Encoding 256 array\n0 1 255 {1 index exch /.notdef put} for\ndup 65 /A put\ndup 194/acute put\nreadonly def\n" +
            "/FontBBox {0 0 800 800} readonly def\ncurrentdict end\ncurrentfile eexec\n";
        string tail = new string('0', 64) + "\ncleartomark\n";

        byte[] middle = hexEexec ? Encoding.ASCII.GetBytes(Convert.ToHexString(cipher) + "\n") : cipher;
        byte[] h = Encoding.Latin1.GetBytes(head);
        byte[] t = Encoding.Latin1.GetBytes(tail);
        if (!pfb)
        {
            return [.. h, .. middle, .. t];
        }

        static byte[] Seg(int type, byte[] d) => [0x80, (byte)type, (byte)d.Length, (byte)(d.Length >> 8), (byte)(d.Length >> 16), (byte)(d.Length >> 24), .. d];
        return [.. Seg(1, h), .. Seg(2, cipher), .. Seg(1, t), 0x80, 3];
    }
}
