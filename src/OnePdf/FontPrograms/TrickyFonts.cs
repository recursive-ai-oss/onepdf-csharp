namespace OnePdf.FontPrograms;

/// <summary>
/// Recognises TrueType fonts whose composite glyphs are only correct after the font's own instructions have run, and says how to run
/// them. These are a few CJK fonts (the DynaLab-designed MingLiU, PMingLiU and DFKai-SB families that ship with Windows; DFKai-SB's
/// PostScript name is <c>DFKaiShu-SB-Estd-BF</c>): the
/// component offsets in their composite glyphs are provisional, and the composite's program moves the components into place.
/// Every other font keeps the plain outline path, which never executes font code.
/// </summary>
/// <remarks>
/// <para>
/// Detection uses only the font's own data, so it survives subsetting:
/// </para>
/// <list type="number">
/// <item>The family, full or PostScript name in the <c>name</c> table (name IDs 1, 4, 6 and 16), after removing a subset tag
/// (<c>ABCDEF+</c>) and every character that is not a letter or digit and folding case, starts with <c>mingliu</c>, <c>pmingliu</c>,
/// <c>dfkaisb</c> or <c>dfkaishu</c>. Subsets from PDF producers usually keep the names of the original, with the tag added.</item>
/// <item>Or the font's <c>fpgm</c>, <c>prep</c> and <c>cvt </c> tables (copied unchanged into subsets) have the checksums, computed
/// here from the table bytes and not read from the directory, of one of two releases seen in the wild: the 2001 DynaLab
/// MingLiU/PMingLiU (<c>fpgm</c> 0x28233BF1, <c>prep</c> 0xA344A1EB, <c>cvt </c> 0x05BCF058) and DFKai-SB (<c>fpgm</c> 0x5A30CA3B,
/// <c>prep</c> 0x13A42602, <c>cvt </c> 0x11E5EAD4). This covers subsets whose <c>name</c> table was removed or renamed.</item>
/// </list>
/// <para>
/// The programs only run when the font has an <c>fpgm</c> or <c>prep</c> table, and failures fall back to the plain outline
/// (<see cref="SfntFont.GlyphProgramFailure"/>).
/// </para>
/// </remarks>
internal static class TrickyFonts
{

    /// <summary>True: the programs run with version 35 semantics (movement along both axes), the era these fonts were hinted for.</summary>
    public const bool NativeMode = true;

    private static readonly string[] s_namePrefixes = ["mingliu", "pmingliu", "dfkaisb", "dfkaishu"];

    // fpgm, prep and cvt checksums of the known releases.
    private static readonly (uint Fpgm, uint Prep, uint Cvt)[] s_signatures =
    [
        (0x28233BF1, 0xA344A1EB, 0x05BCF058),
        (0x5A30CA3B, 0x13A42602, 0x11E5EAD4),
    ];

    /// <summary>True when the tables identify one of the fonts described above.</summary>
    public static bool IsTricky(Dictionary<uint, ReadOnlyMemory<byte>> tables)
    {
        if (!tables.ContainsKey(Be.Tag("fpgm")) && !tables.ContainsKey(Be.Tag("prep")))
        {
            return false;
        }

        if (tables.TryGetValue(Be.Tag("name"), out ReadOnlyMemory<byte> name) && NameMatches(name))
        {
            return true;
        }

        if (!tables.TryGetValue(Be.Tag("fpgm"), out ReadOnlyMemory<byte> fpgm)
            || !tables.TryGetValue(Be.Tag("prep"), out ReadOnlyMemory<byte> prep)
            || !tables.TryGetValue(Be.Tag("cvt "), out ReadOnlyMemory<byte> cvt))
        {
            return false;
        }

        uint f = Checksum(fpgm.Span);
        foreach ((uint Fpgm, uint Prep, uint Cvt) sig in s_signatures)
        {
            if (f == sig.Fpgm && Checksum(prep.Span) == sig.Prep && Checksum(cvt.Span) == sig.Cvt)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The table checksum of the OpenType specification: the sum of the table as big-endian 32-bit words, zero padded, modulo 2^32.</summary>
    internal static uint Checksum(ReadOnlySpan<byte> table)
    {
        uint sum = 0;
        int i = 0;
        for (; i + 4 <= table.Length; i += 4)
        {
            sum = unchecked(sum + Be.U32(table, i));
        }

        if (i < table.Length)
        {
            uint last = 0;
            for (int k = 0; k < 4; k++)
            {
                last <<= 8;
                if (i + k < table.Length)
                {
                    last |= table[i + k];
                }
            }

            sum = unchecked(sum + last);
        }

        return sum;
    }

    private static bool NameMatches(ReadOnlyMemory<byte> table)
    {
        ReadOnlySpan<byte> d = table.Span;
        if (d.Length < 6)
        {
            return false;
        }

        int count = Math.Min(Be.U16(d, 2), (d.Length - 6) / 12);
        int strings = Be.U16(d, 4);
        Span<char> buf = stackalloc char[256];
        for (int i = 0; i < count; i++)
        {
            int r = 6 + (12 * i);
            int id = Be.U16(d, r + 6);
            if (id is not (1 or 4 or 6 or 16))
            {
                continue;
            }

            int platform = Be.U16(d, r);
            int len = Be.U16(d, r + 8);
            int off = strings + Be.U16(d, r + 10);
            if (len > 256 || (long)off + len > d.Length)
            {
                continue;
            }

            // Windows and Unicode records are UTF-16BE; Macintosh records are single bytes. Only ASCII letters and digits matter.
            int n = 0;
            ReadOnlySpan<byte> s = d.Slice(off, len);
            int step = platform == 1 ? 1 : 2;
            for (int k = 0; k + step <= s.Length; k += step)
            {
                int c = step == 1 ? s[k] : (s[k] << 8) | s[k + 1];
                if (c < 128 && char.IsAsciiLetterOrDigit((char)c))
                {
                    buf[n++] = char.ToLowerInvariant((char)c);
                }
            }

            ReadOnlySpan<char> name = buf[..n];
            if (name.Length > 6 && IsSubsetTagged(s, step))
            {
                name = name[6..];
            }

            foreach (string prefix in s_namePrefixes)
            {
                if (name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // "ABCDEF+Name": six capitals and a plus sign (ISO 32000-2 9.6.4).
    private static bool IsSubsetTagged(ReadOnlySpan<byte> s, int step)
    {
        if (s.Length < 7 * step)
        {
            return false;
        }

        for (int k = 0; k < 6; k++)
        {
            int c = step == 1 ? s[k] : (s[2 * k] << 8) | s[(2 * k) + 1];
            if (c is < 'A' or > 'Z')
            {
                return false;
            }
        }

        int plus = step == 1 ? s[6] : (s[12] << 8) | s[13];
        return plus == '+';
    }
}

/// <summary>Whether a TrueType font's own glyph programs run.</summary>
internal enum GlyphPrograms
{
    /// <summary>Only for fonts recognised by <see cref="TrickyFonts"/>.</summary>
    Detect,

    /// <summary>Never: plain outlines.</summary>
    Never,

    /// <summary>For every TrueType font that has an <c>fpgm</c> or <c>prep</c> table, recognised or not (tests and fuzzing).</summary>
    Always,
}
