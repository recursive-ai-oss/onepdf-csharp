namespace OnePdf.FontPrograms;

/// <summary>The kind of font program found in a byte buffer.</summary>
internal enum FontProgramKind
{
    /// <summary>Not recognized.</summary>
    Unknown,

    /// <summary>PostScript Type 1 (PFA, PFB, or a raw PDF <c>FontFile</c>).</summary>
    Type1,

    /// <summary>Bare Compact Font Format data (PDF <c>FontFile3</c> with <c>Type1C</c> or <c>CIDFontType0C</c>).</summary>
    Cff,

    /// <summary>An sfnt with TrueType (<c>glyf</c>) outlines.</summary>
    TrueType,

    /// <summary>An sfnt with a <c>CFF </c> table.</summary>
    OpenTypeCff,
}

/// <summary>
/// A parsed font program, reduced to what a PDF font needs: outlines (unhinted, in glyph space), advance widths where the
/// program has them, names, the built-in encoding and CID lookup. Glyphs are addressed by a dense glyph index (GID) from 0.
/// Instances are immutable after construction and safe to share between threads; outline calls allocate their own state.
/// </summary>
/// <remarks>
/// Parsing failures throw <see cref="FontProgramException"/> from the factory (<see cref="FontProgramLoader.Open"/>) and from
/// <see cref="GetOutline"/> when one glyph's data is damaged; out-of-range glyph indexes return an empty outline.
/// A PDF font layer maps the code in a string to a GID through the encoding, <see cref="FindGlyph"/>, the cmap
/// (<see cref="SfntFont.FindCmap"/>) or <see cref="GlyphForCid"/>, then asks for the outline and width.
/// </remarks>
internal abstract class FontProgram
{
    /// <summary>The kind of program.</summary>
    public abstract FontProgramKind Kind { get; }

    /// <summary>Number of glyphs; valid GIDs are 0 to <c>GlyphCount - 1</c>.</summary>
    public abstract int GlyphCount { get; }

    /// <summary>
    /// The matrix (a b c d e f) from glyph space to text space: 0.001 scale for Type 1 and CFF unless the font says otherwise,
    /// 1/unitsPerEm for sfnt fonts. Never null; callers must not modify it.
    /// </summary>
    public abstract double[] FontMatrix { get; }

    /// <summary>The font bounding box in glyph space (llx, lly, urx, ury) as the program declares it, or null.</summary>
    public virtual double[]? FontBBox => null;

    /// <summary>The unhinted outline of a glyph in glyph space. Empty for blank glyphs and out-of-range indexes.</summary>
    /// <exception cref="FontProgramException">The glyph's data is damaged or exceeds a parsing bound.</exception>
    public abstract GlyphOutline GetOutline(int gid);

    /// <summary>The advance width of a glyph in glyph space, when the program defines one (hmtx, charstring width, hsbw).</summary>
    /// <exception cref="FontProgramException">A charstring is damaged (Type 1 and CFF run the charstring to find the width).</exception>
    public abstract bool TryGetAdvance(int gid, out float advance);

    /// <summary>The GID of a named glyph (<c>/A</c>, <c>.notdef</c>), or -1 when the font has no glyph names or none by that name.</summary>
    public virtual int FindGlyph(string name) => -1;

    /// <summary>The PostScript name of a glyph, or null when the font has no names.</summary>
    public virtual string? GetGlyphName(int gid) => null;

    /// <summary>
    /// The program's built-in encoding as 256 glyph names (null = unassigned), or null when the format has none (sfnt fonts use
    /// cmap tables instead).
    /// </summary>
    public virtual string?[]? BuiltInEncoding => null;

    /// <summary>True for CID-keyed CFF fonts, whose charset maps GIDs to CIDs.</summary>
    public virtual bool IsCidKeyed => false;

    /// <summary>The GID for a CID: through the charset for CID-keyed CFF, the identity otherwise. -1 when the font has no such glyph.</summary>
    public virtual int GlyphForCid(int cid) => (uint)cid < (uint)GlyphCount ? cid : -1;

    /// <summary>The PostScript name of the font (<c>/FontName</c>, the CFF name, or the sfnt <c>name</c> table), or null.</summary>
    public virtual string? FontName => null;
}

/// <summary>Recognizes and opens font programs.</summary>
internal static class FontProgramLoader
{
    /// <summary>Identifies the program by its first bytes, as PDF producers write them in <c>FontFile</c>, <c>FontFile2</c> and <c>FontFile3</c> streams.</summary>
    public static FontProgramKind Sniff(ReadOnlySpan<byte> d)
    {
        if (d.Length < 4)
        {
            return FontProgramKind.Unknown;
        }

        uint tag = Be.U32(d, 0);
        switch (tag)
        {
            case 0x00010000:
            case 0x74727565: // 'true'
            case 0x74746366: // 'ttcf'
                return FontProgramKind.TrueType;
            case 0x4F54544F: // 'OTTO'
                return FontProgramKind.OpenTypeCff;
            case 0x74797031: // 'typ1'
                return FontProgramKind.Unknown;
        }

        if (d[0] == 0x80 || (d[0] == (byte)'%' && d[1] == (byte)'!'))
        {
            return FontProgramKind.Type1;
        }

        // CFF header: major 1, minor, hdrSize >= 4, offSize 1 to 4 (TN 5176 section 6).
        if (d[0] == 1 && d[2] >= 4 && d[3] is >= 1 and <= 4)
        {
            return FontProgramKind.Cff;
        }

        // Some Type 1 files start with whitespace or comments before the %!.
        int limit = Math.Min(d.Length, 1024);
        return d[..limit].IndexOf("%!"u8) >= 0 || d[..limit].IndexOf("/FontName"u8) >= 0 ? FontProgramKind.Type1 : FontProgramKind.Unknown;
    }

    /// <summary>Opens a font program of any supported kind; for a TrueType collection the first face is used.</summary>
    /// <exception cref="FontProgramException">The data is not a supported or intact font program.</exception>
    public static FontProgram Open(ReadOnlyMemory<byte> data)
    {
        switch (Sniff(data.Span))
        {
            case FontProgramKind.Type1:
                return Type1Font.Parse(data.Span);
            case FontProgramKind.Cff:
                return new CffFont(data);
            case FontProgramKind.TrueType:
            case FontProgramKind.OpenTypeCff:
                return SfntFont.Parse(data, 0);
            default:
                throw new FontProgramException("The data is not a recognized font program.");
        }
    }
}
