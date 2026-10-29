using System.Globalization;
using System.Text;
using OnePdf.FontPrograms;
using OnePdf.FontWriting;

namespace OnePdf.Tests.Fonts;

/// <summary>Builds small CFF programs for tests: name-keyed, Type 2 charstrings, real-valued width defaults like a font instanced from a variable font.</summary>
internal static class SyntheticCff
{
    public static byte[] Num(int v) => [28, (byte)(v >> 8), (byte)v];

    /// <summary>A charstring operand in 16.16 fixed point (TN #5177 section 3.2).</summary>
    public static byte[] Fx(double v)
    {
        int f = (int)Math.Round(v * 65536.0);
        return [255, (byte)(f >> 24), (byte)(f >> 16), (byte)(f >> 8), (byte)f];
    }

    public static byte[] Cat(params byte[][] parts) => [.. parts.SelectMany(static p => p)];

    public static byte[] Int5(int v) => [29, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    public static byte[] Real(string text)
    {
        var nibbles = new List<int>();
        foreach (char c in text)
        {
            nibbles.Add(c == '.' ? 0xA : c == '-' ? 0xE : c - '0');
        }

        nibbles.Add(0xF);
        if (nibbles.Count % 2 == 1)
        {
            nibbles.Add(0xF);
        }

        var b = new List<byte> { 30 };
        for (int i = 0; i < nibbles.Count; i += 2)
        {
            b.Add((byte)((nibbles[i] << 4) | nibbles[i + 1]));
        }

        return [.. b];
    }

    public static byte[] Index(params byte[][] items)
    {
        if (items.Length == 0)
        {
            return [0, 0];
        }

        var b = new List<byte> { (byte)(items.Length >> 8), (byte)items.Length, 2, 0, 1 };
        int off = 1;
        foreach (byte[] i in items)
        {
            off += i.Length;
            b.Add((byte)(off >> 8));
            b.Add((byte)off);
        }

        foreach (byte[] i in items)
        {
            b.AddRange(i);
        }

        return [.. b];
    }

    private static int StandardSid(string name)
    {
        for (int i = 0; i < 391; i++)
        {
            if (OnePdf.FontData.CffStandardData.StandardString(i) == name)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// A name-keyed CFF font. <paramref name="glyphs"/> start with .notdef (its name is ignored). The Private DICT has the given
    /// <c>defaultWidthX</c> and <c>nominalWidthX</c> (text, so they can be fractional) and optional local subroutines.
    /// </summary>
    public static byte[] Build(IReadOnlyList<(string Name, byte[] Charstring)> glyphs, string defaultWidth = "234.4962", string nominalWidth = "655.66064", byte[][]? localSubrs = null)
    {
        var custom = new List<string>();
        var sids = new int[glyphs.Count];
        for (int i = 1; i < glyphs.Count; i++)
        {
            int sid = StandardSid(glyphs[i].Name);
            if (sid < 0)
            {
                int at = custom.IndexOf(glyphs[i].Name);
                if (at < 0)
                {
                    custom.Add(glyphs[i].Name);
                    at = custom.Count - 1;
                }

                sid = 391 + at;
            }

            sids[i] = sid;
        }

        byte[] strings = Index([.. custom.Select(static c => Encoding.ASCII.GetBytes(c))]);
        byte[] charset = new byte[1 + (2 * (glyphs.Count - 1))];
        for (int i = 1; i < glyphs.Count; i++)
        {
            charset[(2 * i) - 1] = (byte)(sids[i] >> 8);
            charset[2 * i] = (byte)sids[i];
        }

        byte[] charStrings = Index([.. glyphs.Select(static g => g.Charstring)]);
        byte[] privHead = Cat(Real(defaultWidth), [20], Real(nominalWidth), [21]);
        byte[] priv = localSubrs is null ? privHead : Cat(privHead, Int5(privHead.Length + 6), [19]);
        byte[] subrs = localSubrs is null ? [] : Index(localSubrs);
        byte[] name = Index("Test"u8.ToArray());
        byte[] Top(int charsetOff, int csOff, int privOff) =>
            Cat(Int5(charsetOff), [15], Int5(csOff), [17], Int5(priv.Length), Int5(privOff), [18]);

        int topIndexLength = 2 + 1 + 4 + Top(0, 0, 0).Length;
        int charsetOffset = 4 + name.Length + topIndexLength + strings.Length + 2;
        int csOffset = charsetOffset + charset.Length;
        int privOffset = csOffset + charStrings.Length;
        return Cat([1, 0, 4, 2], name, Index(Top(charsetOffset, csOffset, privOffset)), strings, Index(), charset, charStrings, priv, subrs);
    }

    /// <summary>
    /// Five glyphs whose widths sit in the charstring in each of the ways a width can: after a move, none at all (the default), before a
    /// stem, before a hint mask, and before a lone endchar. Program widths: 751.42, 234.4962 (default), 331, 500.5, 700.5.
    /// </summary>
    public static byte[] Standard(byte[][]? localSubrs = null, byte[]? firstGlyphOverride = null)
    {
        const double Nominal = 655.66064;
        byte[] g1 = firstGlyphOverride ?? Cat(Fx(751.42 - Nominal), Num(10), Num(20), [21], Num(100), Num(0), [5], Num(0), Num(100), [5], [14]);
        byte[] g2 = Cat(Num(10), [22], Num(0), Num(120), [5], Num(80), Num(0), [5], [14]);
        byte[] g3 = Cat(Fx(331 - Nominal), Num(0), Num(20), [1], Num(5), Num(5), [21], Num(60), Num(0), [5], Num(0), Num(40), [5], [14]);
        byte[] g4 = Cat(Fx(500.5 - Nominal), Num(0), Num(10), [18], [19, 0x80], Num(1), Num(2), [21], Num(30), Num(30), [5], [14]);
        byte[] g5 = Cat(Fx(700.5 - Nominal), [14]);
        return Build([("", [14]), ("gid00132", g1), ("gid00131", g2), ("C", g3), ("D", g4), ("E", g5)], localSubrs: localSubrs);
    }

    /// <summary>The Private DICT's defaultWidthX and nominalWidthX of a name-keyed font.</summary>
    public static (double Default, double Nominal) PrivateWidths(byte[] cff)
    {
        ReadOnlySpan<byte> d = cff;
        _ = CffIndex.Read(d, d[2], out int pos);
        CffIndex top = CffIndex.Read(d, pos, out _);
        var entries = CffSubsetter.ParseDict(top.Get(d, 0));
        var p = entries[CffSubsetter.Find(entries, 18)];
        var priv = CffSubsetter.ParseDict(d.Slice((int)p.Second, (int)p.First));
        return (priv[CffSubsetter.Find(priv, 20)].First, priv[CffSubsetter.Find(priv, 21)].First);
    }

    public static string Describe(CffFont f, int gid) =>
        string.Join(",", f.GetOutline(gid).Verbs) + "|" + string.Join(",", f.GetOutline(gid).Coords.Select(static c => c.ToString("R", CultureInfo.InvariantCulture))) + "|" + f.GetGlyphName(gid);
}
