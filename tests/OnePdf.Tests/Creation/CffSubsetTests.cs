using OnePdf.FontData;
using OnePdf.FontPrograms;
using OnePdf.FontWriting;

namespace OnePdf.Tests.Creation;

/// <summary>The CFF subsetter on a small font built here: an accented glyph written as endchar with four arguments, and the String INDEX.</summary>
public class CffSubsetTests
{
    private static byte[] Num(int v) => [28, (byte)(v >> 8), (byte)v];

    private static byte[] Cat(params byte[][] parts) => [.. parts.SelectMany(static p => p)];

    private static byte[] Index(params byte[][] items)
    {
        if (items.Length == 0)
        {
            return [0, 0];
        }

        var b = new List<byte> { (byte)(items.Length >> 8), (byte)items.Length, 2 };
        int off = 1;
        b.Add((byte)(off >> 8));
        b.Add((byte)off);
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

    private static int Sid(string name)
    {
        for (int i = 0; i < 391; i++)
        {
            if (CffStandardData.StandardString(i) == name)
            {
                return i;
            }
        }

        throw new InvalidOperationException(name);
    }

    private static byte[] Int5(int v) => [29, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    /// <summary>
    /// A name-keyed CFF with glyphs .notdef, A, acute, Aacute (endchar: width adx ady bchar achar), B and a String INDEX holding two strings
    /// that the font refers to in its Top DICT (FullName and Notice), default width 500, nominal width 600.
    /// </summary>
    private static byte[] Build(bool composedBaseIsComposed = false)
    {
        byte[] notdef = [14];
        byte[] a = Cat(Num(100), Num(50), Num(0), [21], Num(400), Num(0), [5], Num(0), Num(600), [5], Num(-400), Num(0), [5], [14]);
        byte[] acute = Cat(Num(-300), Num(50), Num(600), [21], Num(100), Num(100), [5], Num(50), Num(-100), [5], [14]);
        byte[] aacute = Cat(Num(100), Num(120), Num(200), Num(65), Num(194), [14]);
        byte[] b = Cat(Num(0), Num(10), Num(0), [21], Num(300), Num(0), [5], Num(10), Num(20), Num(30), Num(40), Num(50), Num(60), [8], [14]);
        byte[][] glyphs = composedBaseIsComposed ? [notdef, aacute, acute, aacute, b] : [notdef, a, acute, aacute, b];
        int[] sids = [0, Sid("A"), Sid("acute"), Sid("Aacute"), Sid("B")];

        byte[] strings = Index("Full Name"u8.ToArray(), "A notice"u8.ToArray());
        byte[] charset = new byte[1 + (2 * (glyphs.Length - 1))];
        for (int i = 1; i < glyphs.Length; i++)
        {
            charset[(2 * i) - 1] = (byte)(sids[i] >> 8);
            charset[2 * i] = (byte)sids[i];
        }

        byte[] charStrings = Index(glyphs);
        byte[] priv = Cat(Num(500), [20], Num(600), [21]);
        byte[] name = Index("T"u8.ToArray());

        // Top DICT: FullName (SID 391), Notice (392), charset, CharStrings, Private. Fixed-width operands make its size known.
        byte[] Top(int charsetOff, int csOff, int privOff) =>
            Cat(Int5(391), [2], Int5(392), [1], Int5(charsetOff), [15], Int5(csOff), [17], Int5(priv.Length), Int5(privOff), [18]);

        int topLength = Top(0, 0, 0).Length;
        int topIndexLength = 2 + 1 + 4 + topLength;
        int charsetOffset = 4 + name.Length + topIndexLength + strings.Length + 2;
        int csOffset = charsetOffset + charset.Length;
        int privOffset = csOffset + charStrings.Length;
        return Cat([1, 0, 4, 2], name, Index(Top(charsetOffset, csOffset, privOffset)), strings, Index(), charset, charStrings, priv);
    }

    private static int StringCount(byte[] cff)
    {
        ReadOnlySpan<byte> d = cff;
        int pos = d[2];
        _ = CffIndex.Read(d, pos, out pos);
        _ = CffIndex.Read(d, pos, out pos);
        return CffIndex.Read(d, pos, out _).Count;
    }

    [Fact]
    public void TheFixtureHasAnAccentedGlyphThatComposesTwoOthers()
    {
        var font = new CffFont(Build());
        Assert.Equal(5, font.GlyphCount);
        Assert.True(font.IsAccented(3));
        Assert.False(font.IsAccented(1));
        GlyphOutline composed = font.GetOutline(3);
        GlyphOutline baseGlyph = font.GetOutline(1);
        Assert.Equal(baseGlyph.Verbs.Length + font.GetOutline(2).Verbs.Length, composed.Verbs.Length);
        Assert.Equal(700, Advance(font, 3));
    }

    private static float Advance(CffFont f, int gid)
    {
        Assert.True(f.TryGetAdvance(gid, out float w));
        return w;
    }

    [Fact]
    public void AnAccentedGlyphIsKeptAsItsComposedOutlineAndWidth()
    {
        byte[] cff = Build();
        var original = new CffFont(cff);
        byte[] subset = CffSubsetter.Subset(cff, [3, 4], "Sub");

        var parsed = new CffFont(subset);
        Assert.True(parsed.IsCidKeyed);
        Assert.Equal(3, parsed.GlyphCount); // .notdef and the two requested glyphs; the base and the accent are not needed
        Assert.False(parsed.IsAccented(1));

        GlyphOutline want = original.GetOutline(3);
        GlyphOutline got = parsed.GetOutline(1);
        Assert.Equal(want.Verbs, got.Verbs);
        Assert.Equal(want.Coords, got.Coords);
        Assert.Equal(Advance(original, 3), Advance(parsed, 1));

        // The plain glyph is copied byte for byte, so its outline and width are unchanged too.
        GlyphOutline plain = parsed.GetOutline(2);
        Assert.Equal(original.GetOutline(4).Verbs, plain.Verbs);
        Assert.Equal(original.GetOutline(4).Coords, plain.Coords);
        Assert.Equal(Advance(original, 4), Advance(parsed, 2));
    }

    [Fact]
    public void ABaseThatIsItselfComposedIsRefusedWithATypedError()
    {
        Assert.Throws<FontProgramException>(() => CffSubsetter.Subset(Build(composedBaseIsComposed: true), [3], "Sub"));
    }

    [Fact]
    public void OnlyTheStringsTheSubsetUsesAreWritten()
    {
        byte[] cff = Build();
        Assert.Equal(2, StringCount(cff)); // the source holds two strings (a full name and a notice)
        byte[] subset = CffSubsetter.Subset(cff, [1, 4], "Sub");

        // The output's String INDEX holds the two ROS strings and nothing from the source.
        Assert.Equal(2, StringCount(subset));
        Assert.False(subset.AsSpan().IndexOf("Full Name"u8) >= 0);
        Assert.False(subset.AsSpan().IndexOf("A notice"u8) >= 0);
        var parsed = new CffFont(subset);
        Assert.True(parsed.IsCidKeyed);
        Assert.Equal(CffOutlineVerbs(new CffFont(cff), 1), CffOutlineVerbs(parsed, 1));
    }

    private static string CffOutlineVerbs(CffFont f, int gid) => string.Join(",", f.GetOutline(gid).Verbs) + "|" + string.Join(",", f.GetOutline(gid).Coords);
}
