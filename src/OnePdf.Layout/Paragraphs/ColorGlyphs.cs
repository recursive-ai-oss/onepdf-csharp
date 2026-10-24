using System.Buffers.Binary;

namespace OnePdf.Layout;

/// <summary>
/// Which of a colour font's palettes colour glyphs are drawn with. A colour font (OpenType <c>COLR</c> and <c>CPAL</c> tables) carries
/// one or more palettes of colours; the first is the default. Version 1 of <c>CPAL</c> can tag a palette as meant for light or for dark
/// backgrounds. Fonts without colour glyphs are not affected.
/// </summary>
/// <remarks>
/// A choice that the font cannot satisfy (no palette tagged for the background, or an index past the last palette) falls back to palette 0.
/// </remarks>
public readonly record struct ColorPalette
{
    private ColorPalette(int kind, int index)
    {
        Kind = kind;
        PaletteIndex = index;
    }

    internal int Kind { get; }

    internal int PaletteIndex { get; }

    /// <summary>Palette 0. This is the default.</summary>
    public static ColorPalette Default => default;

    /// <summary>The first palette the font tags as usable on a light background (OpenType <c>CPAL</c> 1, flag 0x0001); palette 0 when none is tagged.</summary>
    public static ColorPalette Light => new(1, 0);

    /// <summary>The first palette the font tags as usable on a dark background (OpenType <c>CPAL</c> 1, flag 0x0002); palette 0 when none is tagged.</summary>
    public static ColorPalette Dark => new(2, 0);

    /// <summary>The palette with this index.</summary>
    /// <param name="index">Zero-based palette index. A font with fewer palettes uses palette 0.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative or above 65535.</exception>
    public static ColorPalette FromIndex(int index)
    {
        if (index is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "A palette index is between 0 and 65535.");
        }

        return new ColorPalette(3, index);
    }
}

/// <summary>
/// The colour layers of one font: OpenType <c>COLR</c> version 0 base glyphs and their layers, and the <c>CPAL</c> palettes they use. Read from
/// the font file with bounds-checked code. A table that does not hold together (offsets outside the table, unordered base glyph records, layer
/// glyphs outside the font, palette entries that do not exist, too many layers) is rejected as a whole and <see cref="Problem"/> says why;
/// the font's glyphs are then drawn as plain outlines.
/// </summary>
internal sealed class ColorGlyphs
{
    /// <summary>Most layers one base glyph may have. Real fonts use a handful.</summary>
    internal const int MaxLayers = 256;

    private const ushort Foreground = 0xFFFF;

    private readonly byte[] _data;
    private readonly int _baseOffset;
    private readonly int _baseCount;
    private readonly int _layerOffset;
    private readonly int _paletteCount;
    private readonly int _entryCount;
    private readonly int _colorsOffset;
    private readonly int _indicesOffset;
    private readonly int _typesOffset;
    private readonly HashSet<int>? _v1Glyphs;

    private ColorGlyphs(byte[] data, string? problem)
    {
        _data = data;
        Problem = problem;
    }

    private ColorGlyphs(byte[] data, int baseOffset, int baseCount, int layerOffset, int paletteCount, int entryCount, int colorsOffset, int indicesOffset, int typesOffset, HashSet<int>? v1)
    {
        _data = data;
        _baseOffset = baseOffset;
        _baseCount = baseCount;
        _layerOffset = layerOffset;
        _paletteCount = paletteCount;
        _entryCount = entryCount;
        _colorsOffset = colorsOffset;
        _indicesOffset = indicesOffset;
        _typesOffset = typesOffset;
        _v1Glyphs = v1;
    }

    /// <summary>Why the tables were rejected, or null when they are usable.</summary>
    public string? Problem { get; }

    /// <summary>True when glyphs can be looked up (the tables were read without problems).</summary>
    public bool IsUsable => Problem is null;

    /// <summary>True when there is at least one base glyph with layers.</summary>
    public bool HasBaseGlyphs => IsUsable && _baseCount > 0;

    /// <summary>Reads the colour tables of a font file (or of face <paramref name="faceIndex"/> of a collection). Null when the font has no <c>COLR</c> table.</summary>
    public static ColorGlyphs? Load(byte[] data, int faceIndex, int glyphCount)
    {
        if (!FindTable(data, faceIndex, 0x434F4C52, out int colr, out int colrLength))
        {
            return null;
        }

        return Parse(data, faceIndex, glyphCount, colr, colrLength);
    }

    private static ColorGlyphs Parse(byte[] data, int faceIndex, int glyphCount, int colr, int colrLength)
    {
        ColorGlyphs Bad(string why) => new(data, why);

        if (colrLength < 14)
        {
            return Bad("The COLR table is too short.");
        }

        ReadOnlySpan<byte> t = data.AsSpan(colr, colrLength);
        int version = BinaryPrimitives.ReadUInt16BigEndian(t);
        if (version > 1)
        {
            return Bad($"COLR version {version} is not supported.");
        }

        int baseCount = BinaryPrimitives.ReadUInt16BigEndian(t[2..]);
        long baseOff = BinaryPrimitives.ReadUInt32BigEndian(t[4..]);
        long layerOff = BinaryPrimitives.ReadUInt32BigEndian(t[8..]);
        int layerCount = BinaryPrimitives.ReadUInt16BigEndian(t[12..]);
        if (baseOff + (6L * baseCount) > colrLength || layerOff + (4L * layerCount) > colrLength)
        {
            return Bad("A COLR record array lies outside the table.");
        }

        if (!FindTable(data, faceIndex, 0x4350414C, out int cpal, out int cpalLength) || cpalLength < 12)
        {
            return Bad("The font has a COLR table but no usable CPAL table.");
        }

        ReadOnlySpan<byte> p = data.AsSpan(cpal, cpalLength);
        int cpalVersion = BinaryPrimitives.ReadUInt16BigEndian(p);
        int entries = BinaryPrimitives.ReadUInt16BigEndian(p[2..]);
        int palettes = BinaryPrimitives.ReadUInt16BigEndian(p[4..]);
        int colors = BinaryPrimitives.ReadUInt16BigEndian(p[6..]);
        long colorsOff = BinaryPrimitives.ReadUInt32BigEndian(p[8..]);
        if (cpalVersion > 1 || palettes < 1 || entries < 1 || 12L + (2L * palettes) > cpalLength || colorsOff + (4L * colors) > cpalLength)
        {
            return Bad("The CPAL table is not valid.");
        }

        for (int i = 0; i < palettes; i++)
        {
            long first = BinaryPrimitives.ReadUInt16BigEndian(p[(12 + (2 * i))..]);
            if (first + entries > colors)
            {
                return Bad("A CPAL palette refers to colours outside the table.");
            }
        }

        int typesOff = -1;
        if (cpalVersion == 1)
        {
            long headerEnd = 12L + (2L * palettes) + 12;
            if (headerEnd > cpalLength)
            {
                return Bad("The CPAL version 1 header is cut short.");
            }

            long types = BinaryPrimitives.ReadUInt32BigEndian(p[(12 + (2 * palettes))..]);
            if (types != 0)
            {
                if (types + (4L * palettes) > cpalLength)
                {
                    return Bad("The CPAL palette types lie outside the table.");
                }

                typesOff = (int)types;
            }
        }

        // Base glyph records must be in increasing glyph order (OpenType COLR), which also rules out two records for one glyph. Every
        // record is checked here so that drawing never meets a bad one.
        int previous = -1;
        for (int i = 0; i < baseCount; i++)
        {
            ReadOnlySpan<byte> r = t[(int)(baseOff + (6L * i))..];
            int gid = BinaryPrimitives.ReadUInt16BigEndian(r);
            int first = BinaryPrimitives.ReadUInt16BigEndian(r[2..]);
            int n = BinaryPrimitives.ReadUInt16BigEndian(r[4..]);
            if (gid <= previous || gid >= glyphCount)
            {
                return Bad("The COLR base glyph records are not in increasing order or name a glyph outside the font.");
            }

            if (n > MaxLayers || first + n > layerCount)
            {
                return Bad("A COLR base glyph has more layers than allowed or layers outside the layer array.");
            }

            previous = gid;
        }

        for (int i = 0; i < layerCount; i++)
        {
            ReadOnlySpan<byte> r = t[(int)(layerOff + (4L * i))..];
            int gid = BinaryPrimitives.ReadUInt16BigEndian(r);
            int entry = BinaryPrimitives.ReadUInt16BigEndian(r[2..]);
            if (gid >= glyphCount || (entry != Foreground && entry >= entries))
            {
                return Bad("A COLR layer names a glyph outside the font or a palette entry that does not exist.");
            }
        }

        // Version 1 glyphs (paint graphs) are not drawn; remember which ones the table holds so they can be reported. The BaseGlyphList is
        // a count and (glyph, offset) records (COLR 1).
        HashSet<int>? v1 = null;
        if (version == 1 && colrLength >= 34)
        {
            long list = BinaryPrimitives.ReadUInt32BigEndian(t[14..]);
            if (list != 0 && list + 4 <= colrLength)
            {
                long count = BinaryPrimitives.ReadUInt32BigEndian(t[(int)list..]);
                count = Math.Min(count, (colrLength - list - 4) / 6);
                v1 = [];
                for (long i = 0; i < count; i++)
                {
                    v1.Add(BinaryPrimitives.ReadUInt16BigEndian(t[(int)(list + 4 + (6 * i))..]));
                }
            }
        }

        return new ColorGlyphs(
            data,
            colr + (int)baseOff,
            baseCount,
            colr + (int)layerOff,
            palettes,
            entries,
            cpal + (int)colorsOff,
            cpal + 12,
            typesOff < 0 ? -1 : cpal + typesOff,
            v1);
    }

    /// <summary>Finds the layers of a base glyph. False when the glyph has none (it is drawn as an outline).</summary>
    public bool TryGetLayers(int gid, out int first, out int count)
    {
        first = 0;
        count = 0;
        if (!IsUsable)
        {
            return false;
        }

        int lo = 0;
        int hi = _baseCount - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) / 2);
            ReadOnlySpan<byte> r = _data.AsSpan(_baseOffset + (6 * mid), 6);
            int g = BinaryPrimitives.ReadUInt16BigEndian(r);
            if (g == gid)
            {
                first = BinaryPrimitives.ReadUInt16BigEndian(r[2..]);
                count = BinaryPrimitives.ReadUInt16BigEndian(r[4..]);
                return count > 0;
            }

            if (g < gid)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return false;
    }

    /// <summary>True for a glyph that only has a version 1 (paint graph) description.</summary>
    public bool IsVersion1Only(int gid) => IsUsable && _v1Glyphs is not null && _v1Glyphs.Contains(gid) && !TryGetLayers(gid, out _, out _);

    /// <summary>Layer <paramref name="index"/> of the layer array: the glyph to draw and its palette entry (0xFFFF for the text colour).</summary>
    public void GetLayer(int index, out int gid, out int paletteEntry)
    {
        ReadOnlySpan<byte> r = _data.AsSpan(_layerOffset + (4 * index), 4);
        gid = BinaryPrimitives.ReadUInt16BigEndian(r);
        paletteEntry = BinaryPrimitives.ReadUInt16BigEndian(r[2..]);
    }

    /// <summary>The index of the palette a choice stands for in this font.</summary>
    public int ResolvePalette(ColorPalette choice)
    {
        switch (choice.Kind)
        {
            case 1 or 2 when _typesOffset >= 0:
                uint flag = choice.Kind == 1 ? 1u : 2u;
                for (int i = 0; i < _paletteCount; i++)
                {
                    if ((BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_typesOffset + (4 * i), 4)) & flag) != 0)
                    {
                        return i;
                    }
                }

                return 0;
            case 3:
                return choice.PaletteIndex < _paletteCount ? choice.PaletteIndex : 0;
            default:
                return 0;
        }
    }

    /// <summary>The colour of a palette entry as red, green and blue in 0..1. The entry's alpha is not used.</summary>
    public (double R, double G, double B) GetColor(int palette, int entry)
    {
        int first = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(_indicesOffset + (2 * palette), 2));
        ReadOnlySpan<byte> c = _data.AsSpan(_colorsOffset + (4 * (first + entry)), 4);

        // Records are blue, green, red, alpha (OpenType CPAL ColorRecord).
        return (c[2] / 255.0, c[1] / 255.0, c[0] / 255.0);
    }

    /// <summary>True for the palette entry value that means the text colour.</summary>
    public static bool IsForeground(int paletteEntry) => paletteEntry == Foreground;

    // The table directory of an sfnt file or of one font of a collection (OpenType 'sfnt' header, TrueType Collection header).
    private static bool FindTable(byte[] d, int faceIndex, uint tag, out int offset, out int length)
    {
        offset = 0;
        length = 0;
        if (d.Length < 12)
        {
            return false;
        }

        long dir = 0;
        if (BinaryPrimitives.ReadUInt32BigEndian(d) == 0x74746366)
        {
            uint fonts = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(8));
            if (faceIndex < 0 || faceIndex >= fonts || 16L + (4L * faceIndex) > d.Length)
            {
                return false;
            }

            dir = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(12 + (4 * faceIndex)));
        }

        if (dir + 12 > d.Length)
        {
            return false;
        }

        int tables = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan((int)dir + 4));
        for (int i = 0; i < tables; i++)
        {
            long rec = dir + 12 + (16L * i);
            if (rec + 16 > d.Length)
            {
                return false;
            }

            ReadOnlySpan<byte> r = d.AsSpan((int)rec, 16);
            if (BinaryPrimitives.ReadUInt32BigEndian(r) != tag)
            {
                continue;
            }

            long o = BinaryPrimitives.ReadUInt32BigEndian(r[8..]);
            long l = BinaryPrimitives.ReadUInt32BigEndian(r[12..]);
            if (o + l > d.Length)
            {
                return false;
            }

            offset = (int)o;
            length = (int)l;
            return true;
        }

        return false;
    }
}
