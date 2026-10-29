using OnePdf.FontPrograms;

namespace OnePdf;

/// <summary>The outline flavour of a font file opened with <see cref="PdfFontFile"/>.</summary>
public enum PdfFontFileKind
{
    /// <summary>An sfnt (TrueType or OpenType) file with <c>glyf</c> outlines.</summary>
    TrueType,

    /// <summary>An sfnt (OpenType) file with a <c>CFF </c> table.</summary>
    OpenTypeCff,

    /// <summary>A bare Compact Font Format program, as stored in a PDF <c>FontFile3</c> stream.</summary>
    Cff,
}

/// <summary>What the font's own <c>OS/2</c> <c>fsType</c> field allows for embedding in a document.</summary>
public enum PdfFontEmbeddingRights
{
    /// <summary>No restriction (<c>fsType</c> 0, or no <c>OS/2</c> table): installable embedding.</summary>
    Installable,

    /// <summary>Editable embedding (bit 3).</summary>
    Editable,

    /// <summary>Preview and print embedding (bit 2): the font may be embedded in documents that are only viewed and printed.</summary>
    PreviewAndPrint,

    /// <summary>Restricted license embedding (bit 1): the font must not be embedded.</summary>
    Restricted,

    /// <summary>Bitmap embedding only (bit 9): outlines must not be embedded.</summary>
    BitmapOnly,
}

/// <summary>
/// A font program opened for reading: TrueType, OpenType with TrueType or CFF outlines, or a bare CFF program. It exposes what is needed
/// to decide how a program fits a PDF font: the design grid, the glyph count, the character maps, advance widths in PDF glyph space
/// (1/1000 of an em) and the embedding permission. It reads; it never changes the file or any document.
/// </summary>
/// <remarks>
/// <para>
/// Glyph ids are those of the file. Glyph 0 is <c>.notdef</c>; lookups that find nothing return 0, so "no glyph" and ".notdef" are the same
/// answer, as they are for a PDF viewer. The object is immutable and safe to share between threads.
/// </para>
/// <para>
/// The input is untrusted: table offsets, glyph counts and nesting are bounded, and damage raises <see cref="PdfFormatException"/>.
/// Hinting programs in the font are never run.
/// </para>
/// </remarks>
public sealed class PdfFontFile
{
    private readonly FontProgram _program;
    private readonly SfntFont? _sfnt;
    private readonly CmapSubtable? _unicode;
    private readonly CmapSubtable? _cmap30;
    private readonly CmapSubtable? _cmap10;

    private PdfFontFile(FontProgram program, ReadOnlyMemory<byte> data)
    {
        _program = program;
        Data = data;
        _sfnt = program as SfntFont;
        if (_sfnt is not null)
        {
            _unicode = _sfnt.FindUnicodeCmap();
            _cmap30 = _sfnt.FindCmap(3, 0);
            _cmap10 = _sfnt.FindCmap(1, 0);
        }
    }

    /// <summary>The bytes the file was opened from.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>The outline flavour.</summary>
    public PdfFontFileKind Kind => _program.Kind switch
    {
        FontProgramKind.TrueType => PdfFontFileKind.TrueType,
        FontProgramKind.OpenTypeCff => PdfFontFileKind.OpenTypeCff,
        _ => PdfFontFileKind.Cff,
    };

    /// <summary>The number of glyphs. Valid glyph ids are 0 to <c>GlyphCount - 1</c>.</summary>
    public int GlyphCount => _program.GlyphCount;

    /// <summary>
    /// The design grid: font units per em for an sfnt file (<c>head.unitsPerEm</c>), and the reciprocal of the font matrix's x scale
    /// (normally 1000) for a bare CFF program.
    /// </summary>
    public int UnitsPerEm => _sfnt is not null ? _sfnt.UnitsPerEm : (int)Math.Clamp(Math.Round(1.0 / Math.Abs(_program.FontMatrix[0] == 0 ? 0.001 : _program.FontMatrix[0])), 1, 65535);

    /// <summary>The PostScript name of the font (<c>name</c> ID 6 or the CFF name), or null when the file has none.</summary>
    public string? PostScriptName => _program.FontName;

    /// <summary>The raw <c>OS/2</c> <c>fsType</c> flags; 0 when the file has no <c>OS/2</c> table or is a bare CFF program.</summary>
    public int FsType => _sfnt is not null ? EmbeddingFlags(_sfnt) : 0;

    /// <summary>The embedding permission <see cref="FsType"/> expresses (the most restrictive bit wins).</summary>
    public PdfFontEmbeddingRights EmbeddingRights
    {
        get
        {
            int f = FsType;
            if ((f & 0x0002) != 0 && (f & 0x000C) == 0)
            {
                return PdfFontEmbeddingRights.Restricted;
            }

            if ((f & 0x0200) != 0)
            {
                return PdfFontEmbeddingRights.BitmapOnly;
            }

            if ((f & 0x0004) != 0)
            {
                return PdfFontEmbeddingRights.PreviewAndPrint;
            }

            return (f & 0x0008) != 0 ? PdfFontEmbeddingRights.Editable : PdfFontEmbeddingRights.Installable;
        }
    }

    /// <summary>True when the font's <c>fsType</c> allows embedding its outlines in a document at all.</summary>
    public bool AllowsEmbedding => EmbeddingRights is not (PdfFontEmbeddingRights.Restricted or PdfFontEmbeddingRights.BitmapOnly);

    /// <summary>False when <c>fsType</c> bit 8 forbids subsetting: such a font may only be embedded whole.</summary>
    public bool AllowsSubsetting => (FsType & 0x0100) == 0;

    /// <summary>True when the file has a Unicode character map ((3,1), (3,10) or a platform 0 table).</summary>
    public bool HasUnicodeCmap => _unicode is not null;

    /// <summary>True when the file has a symbol character map ((3,0)) or a Macintosh Roman one ((1,0)) that selects glyphs by character code.</summary>
    public bool HasSymbolicCmap => _cmap30 is not null || _cmap10 is not null;

    /// <summary>True when glyphs have names (a bare CFF program, an OpenType-CFF file, or a TrueType <c>post</c> table of format 2).</summary>
    public bool HasGlyphNames => _program.FindGlyph(".notdef") >= 0;

    /// <summary>
    /// Opens a font file. A TrueType collection yields the face at <paramref name="faceIndex"/>.
    /// </summary>
    /// <param name="data">The font bytes: a TrueType or OpenType file, or a bare CFF program.</param>
    /// <param name="faceIndex">The face of a collection; 0 for single fonts.</param>
    /// <returns>The opened font. The bytes are referenced, not copied; do not change them while the object is in use.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="faceIndex"/> is negative.</exception>
    /// <exception cref="PdfFormatException">The data is not a TrueType, OpenType or CFF font, is damaged, or exceeds a parsing bound.</exception>
    public static PdfFontFile Open(ReadOnlyMemory<byte> data, int faceIndex = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(faceIndex);
        try
        {
            FontProgramKind kind = FontProgramLoader.Sniff(data.Span);
            switch (kind)
            {
                case FontProgramKind.TrueType:
                case FontProgramKind.OpenTypeCff:
                    return new PdfFontFile(SfntFont.Parse(data, faceIndex, GlyphPrograms.Never), data);
                case FontProgramKind.Cff:
                    if (faceIndex != 0)
                    {
                        throw new PdfFormatException("A bare CFF program has a single face.");
                    }

                    return new PdfFontFile(new CffFont(data), data);
                default:
                    throw new PdfFormatException("The data is not a TrueType, OpenType or CFF font.");
            }
        }
        catch (FontProgramException ex)
        {
            throw new PdfFormatException("The font file is damaged: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// The glyph for a Unicode scalar value through the Unicode character map, or 0 when the font has none or has no such character.
    /// A bare CFF program has no character map; use <see cref="GetGlyphForName"/> there.
    /// </summary>
    public int GetGlyphForUnicode(int codePoint)
    {
        if (_unicode is null || codePoint < 0)
        {
            return 0;
        }

        int g = _unicode.Lookup(codePoint);
        return (uint)g < (uint)GlyphCount ? g : 0;
    }

    /// <summary>
    /// The glyph a symbolic simple font uses for a one-byte character code (ISO 32000-2 9.6.5.4): the (3,0) table at the code itself and
    /// at U+F000, U+F100 and U+F200 plus the code, then the (1,0) table. 0 when neither table has the code.
    /// </summary>
    public int GetGlyphForSymbolicCode(int code)
    {
        if (code is < 0 or > 255)
        {
            return 0;
        }

        int g = _cmap30?.LookupSymbolic(code) ?? 0;
        if (g == 0 && _cmap10 is not null)
        {
            g = _cmap10.Lookup(code);
        }

        return (uint)g < (uint)GlyphCount ? g : 0;
    }

    /// <summary>The glyph with a PostScript name (<c>A</c>, <c>space</c>), or 0 when the font has no glyph names or none by that name.</summary>
    public int GetGlyphForName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        int g = _program.FindGlyph(name);
        return g > 0 ? g : 0;
    }

    /// <summary>The PostScript name of a glyph, or null when the font has no names or the glyph is out of range.</summary>
    public string? GetGlyphName(int glyph) => (uint)glyph < (uint)GlyphCount ? SafeName(glyph) : null;

    /// <summary>
    /// The advance width of a glyph in PDF glyph space, 1/1000 of an em, as the font's metrics give it. The value is not rounded.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="glyph"/> is outside the font.</exception>
    /// <exception cref="PdfFormatException">The glyph's charstring is damaged (CFF widths are read from the charstring).</exception>
    public double GetAdvanceWidth(int glyph)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(glyph);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(glyph, GlyphCount);
        try
        {
            return _program.TryGetAdvance(glyph, out float advance) ? advance * Math.Abs(_program.FontMatrix[0]) * 1000.0 : 0;
        }
        catch (FontProgramException ex)
        {
            throw new PdfFormatException("The font file is damaged: " + ex.Message, ex);
        }
    }

    /// <summary>Like <see cref="GetAdvanceWidth"/>, but returns false, and never throws, when the glyph is outside the font, its data is damaged, or the font defines no width for it.</summary>
    public bool TryGetAdvanceWidth(int glyph, out double width)
    {
        width = 0;
        if ((uint)glyph >= (uint)GlyphCount)
        {
            return false;
        }

        try
        {
            if (!_program.TryGetAdvance(glyph, out float advance))
            {
                return false;
            }

            width = advance * Math.Abs(_program.FontMatrix[0]) * 1000.0;
            return true;
        }
        catch (FontProgramException)
        {
            return false;
        }
    }

    internal SfntFont? Sfnt => _sfnt;

    internal FontProgram Program => _program;

    internal CmapSubtable? UnicodeCmap => _unicode;

    private string? SafeName(int glyph)
    {
        try
        {
            return _program.GetGlyphName(glyph);
        }
        catch (FontProgramException)
        {
            return null;
        }
    }

    private static int EmbeddingFlags(SfntFont f)
    {
        ReadOnlyMemory<byte> os2 = f.GetTable("OS/2");
        return os2.Length >= 10 ? Be.U16(os2.Span, 8) : 0;
    }
}
