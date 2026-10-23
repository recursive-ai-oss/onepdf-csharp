using System.Text;
using OnePdf.FontData;

namespace OnePdf.FontPrograms;

/// <summary>
/// An sfnt font (TrueType or OpenType) for use as a PDF font program: outlines from <c>glyf</c>/<c>loca</c> or a <c>CFF </c> table,
/// advance widths from <c>hmtx</c>, every <c>cmap</c> subtable, and glyph names from <c>post</c> (or the CFF charset).
/// </summary>
/// <remarks>
/// PDF embeds subsets, so the reader tolerates what subsetters drop or break: no <c>cmap</c>, <c>name</c>, <c>post</c>, <c>OS/2</c>
/// or <c>hhea</c>/<c>hmtx</c> (those queries answer "none"); a missing <c>head</c> or <c>maxp</c> (units per em 1000, glyph count
/// from <c>loca</c>); a <c>loca</c> that is short or non-monotonic (the glyph is empty); a table that runs past the end of the file
/// (clipped); and table checksums are not checked. <c>loca</c> and <c>glyf</c> (or <c>CFF </c>) are required for outlines.
/// </remarks>
internal sealed class SfntFont : FontProgram
{
    private readonly Dictionary<uint, ReadOnlyMemory<byte>> _tables;
    private readonly ReadOnlyMemory<byte> _hmtx;
    private readonly int _numHMetrics;
    private readonly TrueTypeGlyphs? _glyf;
    private readonly string? _setupFailure;
    private readonly CffFont? _cff;
    private readonly List<CmapSubtable> _cmaps;
    private readonly double[] _matrix;
    private readonly Lazy<PostNames?> _post;

    private SfntFont(Dictionary<uint, ReadOnlyMemory<byte>> tables, GlyphPrograms programs)
    {
        _tables = tables;
        int upem = 1000;
        int locaFormat = -1;
        double[]? bbox = null;
        ReadOnlyMemory<byte> glyfTable = default;
        ReadOnlyMemory<byte> locaTable = default;
        bool longLoca = false;
        if (tables.TryGetValue(Be.Tag("head"), out ReadOnlyMemory<byte> headM) && headM.Length >= 54)
        {
            ReadOnlySpan<byte> head = headM.Span;
            int u = Be.U16(head, 18);
            if (u >= 16 && u <= 16384)
            {
                upem = u;
            }

            bbox = [Be.I16(head, 36), Be.I16(head, 38), Be.I16(head, 40), Be.I16(head, 42)];
            locaFormat = Be.I16(head, 50);
        }

        UnitsPerEm = upem;
        _matrix = [1.0 / upem, 0, 0, 1.0 / upem, 0, 0];
        FontBBox = bbox;

        int maxpGlyphs = -1;
        if (tables.TryGetValue(Be.Tag("maxp"), out ReadOnlyMemory<byte> maxp) && maxp.Length >= 6)
        {
            maxpGlyphs = Be.U16(maxp.Span, 4);
        }

        if (tables.TryGetValue(Be.Tag("CFF "), out ReadOnlyMemory<byte> cff))
        {
            _cff = new CffFont(cff);
            GlyphCount = _cff.GlyphCount;
        }
        else
        {
            if (!tables.TryGetValue(Be.Tag("glyf"), out ReadOnlyMemory<byte> glyf) || !tables.TryGetValue(Be.Tag("loca"), out ReadOnlyMemory<byte> loca))
            {
                throw new FontProgramException("The font has no outlines ('glyf' and 'loca', or 'CFF ').");
            }

            if (locaFormat is not (0 or 1))
            {
                // No usable head: long offsets fit exactly (n + 1) * 4 bytes, otherwise assume short ones.
                locaFormat = maxpGlyphs >= 0 && loca.Length >= (maxpGlyphs + 1L) * 4 ? 1 : 0;
            }

            int fromLoca = Math.Max(0, (loca.Length / (locaFormat != 0 ? 4 : 2)) - 1);
            GlyphCount = maxpGlyphs >= 0 ? Math.Min(maxpGlyphs, Math.Max(fromLoca, 0)) : fromLoca;
            GlyphCount = Math.Min(GlyphCount, FontLimits.MaxGlyphs);
            glyfTable = glyf;
            locaTable = loca;
            longLoca = locaFormat != 0;
        }

        if (tables.TryGetValue(Be.Tag("hmtx"), out _hmtx))
        {
            int n = _hmtx.Length / 4;
            if (tables.TryGetValue(Be.Tag("hhea"), out ReadOnlyMemory<byte> hhea) && hhea.Length >= 36)
            {
                n = Math.Min(n, Be.U16(hhea.Span, 34));
            }
            else
            {
                n = Math.Min(n, GlyphCount);
            }

            _numHMetrics = n;
        }

        if (_cff is null)
        {
            TrueTypeHinter? hinter = null;
            if (programs != GlyphPrograms.Never && (programs == GlyphPrograms.Always || TrickyFonts.IsTricky(tables)) && tables.TryGetValue(Be.Tag("maxp"), out ReadOnlyMemory<byte> maxpTable))
            {
                tables.TryGetValue(Be.Tag("fpgm"), out ReadOnlyMemory<byte> fpgm);
                tables.TryGetValue(Be.Tag("prep"), out ReadOnlyMemory<byte> prep);
                tables.TryGetValue(Be.Tag("cvt "), out ReadOnlyMemory<byte> cvt);

                // One pixel per font unit: the programs place components exactly and rounding to the grid moves nothing by more than a unit.
                hinter = TrueTypeHinter.Create(upem, upem, TrickyFonts.NativeMode, fpgm, prep, cvt, maxpTable, out _setupFailure);
            }

            _glyf = new TrueTypeGlyphs(glyfTable, locaTable, longLoca, GlyphCount, hinter, _hmtx, _numHMetrics);
            RunsGlyphPrograms = hinter is not null;
        }

        _cmaps = tables.TryGetValue(Be.Tag("cmap"), out ReadOnlyMemory<byte> cmap) ? CmapSubtable.ReadAll(cmap) : [];
        _post = new Lazy<PostNames?>(() => tables.TryGetValue(Be.Tag("post"), out ReadOnlyMemory<byte> p) ? PostNames.Read(p, GlyphCount) : null, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The bytes of the table with the 4-character <paramref name="tag"/> (clipped to the file), for the font writer. Empty when the font lacks it.</summary>
    internal ReadOnlyMemory<byte> GetTable(string tag) => _tables.TryGetValue(Be.Tag(tag), out ReadOnlyMemory<byte> t) ? t : default;

    /// <summary>True for a TrueType font recognised as one whose composite glyphs need their instructions (see <see cref="TrickyFonts"/>) and whose programs ran at load.</summary>
    public bool RunsGlyphPrograms { get; }

    /// <summary>Why the glyph programs of a recognised font could not run (at load, or for the first glyph where they failed), or null. Glyphs affected use their plain outline.</summary>
    public string? GlyphProgramFailure => _glyf?.HintFailure ?? _setupFailure;

    /// <summary>Units per em from <c>head</c> (1000 when <c>head</c> is missing or out of range).</summary>
    public int UnitsPerEm { get; }

    /// <inheritdoc />
    public override FontProgramKind Kind => _cff is null ? FontProgramKind.TrueType : FontProgramKind.OpenTypeCff;

    /// <inheritdoc />
    public override int GlyphCount { get; }

    /// <inheritdoc />
    public override double[] FontMatrix => _matrix;

    /// <inheritdoc />
    public override double[]? FontBBox { get; }

    /// <summary>The embedded CFF font of an OpenType-CFF program, or null for TrueType outlines.</summary>
    public CffFont? Cff => _cff;

    /// <summary>All usable <c>cmap</c> subtables in file order.</summary>
    public IReadOnlyList<CmapSubtable> CmapSubtables => _cmaps;

    /// <summary>The first subtable with this platform and encoding ID, such as (3,1) Windows Unicode BMP, (3,10), (1,0) Macintosh Roman or (3,0) symbol; or null.</summary>
    public CmapSubtable? FindCmap(int platform, int encoding)
    {
        foreach (CmapSubtable c in _cmaps)
        {
            if (c.Platform == platform && c.Encoding == encoding)
            {
                return c;
            }
        }

        return null;
    }

    /// <summary>A Unicode subtable if the font has one: (3,10), (0,x) or (3,1), best first; otherwise null.</summary>
    public CmapSubtable? FindUnicodeCmap() => FindCmap(3, 10) ?? FindCmap(0, 4) ?? FindCmap(0, 6) ?? FindCmap(3, 1) ?? FindCmapPlatform(0);

    private CmapSubtable? FindCmapPlatform(int platform)
    {
        foreach (CmapSubtable c in _cmaps)
        {
            if (c.Platform == platform)
            {
                return c;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public override string? FontName
    {
        get
        {
            if (_tables.TryGetValue(Be.Tag("name"), out ReadOnlyMemory<byte> n))
            {
                return SfntName.Get(n, 6);
            }

            return _cff?.FontName;
        }
    }

    /// <inheritdoc />
    public override GlyphOutline GetOutline(int gid)
    {
        if ((uint)gid >= (uint)GlyphCount)
        {
            return GlyphOutline.Empty;
        }

        return _cff is not null ? _cff.GetOutline(gid) : _glyf!.Load(gid);
    }

    /// <inheritdoc />
    public override bool TryGetAdvance(int gid, out float advance)
    {
        advance = 0;
        if (_numHMetrics == 0 || (uint)gid >= (uint)GlyphCount)
        {
            return false;
        }

        // Glyphs past numberOfHMetrics share the last advance (OpenType 'hmtx').
        int i = Math.Min(gid, _numHMetrics - 1);
        advance = Be.U16(_hmtx.Span, 4 * i);
        return true;
    }

    /// <inheritdoc />
    public override int FindGlyph(string name)
    {
        if (_cff is not null && !_cff.IsCidKeyed)
        {
            return _cff.FindGlyph(name);
        }

        return _post.Value?.Find(name) ?? -1;
    }

    /// <inheritdoc />
    public override string? GetGlyphName(int gid)
    {
        if (_cff is not null && !_cff.IsCidKeyed)
        {
            return _cff.GetGlyphName(gid);
        }

        return _post.Value?.Name(gid);
    }

    /// <inheritdoc />
    public override bool IsCidKeyed => _cff is { IsCidKeyed: true };

    /// <inheritdoc />
    public override int GlyphForCid(int cid) => _cff is { IsCidKeyed: true } c ? c.GlyphForCid(cid) : base.GlyphForCid(cid);

    /// <summary>Parses the face <paramref name="faceIndex"/> of a font file: a plain sfnt, or a TrueType collection.</summary>
    /// <exception cref="FontProgramException">The data is not an sfnt, or lacks outlines.</exception>
    public static SfntFont Parse(ReadOnlyMemory<byte> data, int faceIndex) => Parse(data, faceIndex, GlyphPrograms.Detect);

    /// <summary>As <see cref="Parse(ReadOnlyMemory{byte}, int)"/>, with the policy for running the font's own glyph programs (tests and fuzzing force it either way).</summary>
    internal static SfntFont Parse(ReadOnlyMemory<byte> data, int faceIndex, GlyphPrograms programs)
    {
        ReadOnlySpan<byte> d = data.Span;
        if (d.Length < 12)
        {
            throw new FontProgramException("The data is too short to be a font.");
        }

        uint offset = 0;
        if (Be.U32(d, 0) == 0x74746366)
        {
            uint count = Be.U32(d, 8);
            if (count == 0 || count > FontLimits.MaxCollectionFaces || 12L + (4L * count) > d.Length)
            {
                throw new FontProgramException("The font collection header is invalid.");
            }

            if ((uint)faceIndex >= count)
            {
                throw new FontProgramException("The font collection has no such face.");
            }

            offset = Be.U32(d, 12 + (4 * faceIndex));
        }
        else if (faceIndex != 0)
        {
            throw new FontProgramException("The font file holds a single face.");
        }

        return new SfntFont(ReadDirectory(data, offset), programs);
    }

    /// <summary>Number of faces in the file: the collection count, or 1.</summary>
    public static int FaceCount(ReadOnlySpan<byte> d)
    {
        if (d.Length >= 12 && Be.U32(d, 0) == 0x74746366)
        {
            return (int)Math.Min(Be.U32(d, 8), FontLimits.MaxCollectionFaces);
        }

        return 1;
    }

    private static Dictionary<uint, ReadOnlyMemory<byte>> ReadDirectory(ReadOnlyMemory<byte> data, uint offset)
    {
        ReadOnlySpan<byte> d = data.Span;
        if (offset > d.Length - 12L)
        {
            throw Be.Truncated();
        }

        int o = (int)offset;
        int numTables = Be.U16(d, o + 4);
        if (numTables == 0 || numTables > FontLimits.MaxTables)
        {
            throw new FontProgramException("The table directory is invalid.");
        }

        // Fewer records than declared fit in a cut-off file: read those that are there.
        numTables = (int)Math.Min(numTables, (d.Length - o - 12L) / 16);
        var tables = new Dictionary<uint, ReadOnlyMemory<byte>>(numTables);
        for (int i = 0; i < numTables; i++)
        {
            int e = o + 12 + (16 * i);
            uint tag = Be.U32(d, e);
            uint tOff = Be.U32(d, e + 8);
            uint tLen = Be.U32(d, e + 12);
            if (tOff >= d.Length)
            {
                continue;
            }

            // A table running past the end of the file is clipped to it; reads inside stay bounds-checked.
            int len = (int)Math.Min(tLen, (uint)(d.Length - tOff));
            tables.TryAdd(tag, data.Slice((int)tOff, len));
        }

        if (tables.Count == 0)
        {
            throw new FontProgramException("The font has no tables.");
        }

        return tables;
    }

    /// <summary>Glyph names from the <c>post</c> table: formats 1 and 2 (3 and others carry none).</summary>
    private sealed class PostNames
    {
        private readonly string?[] _names;
        private readonly Lazy<Dictionary<string, int>> _index;

        private PostNames(string?[] names)
        {
            _names = names;
            _index = new Lazy<Dictionary<string, int>>(
                () =>
                {
                    var d = new Dictionary<string, int>(names.Length, StringComparer.Ordinal);
                    for (int i = 0; i < names.Length; i++)
                    {
                        if (names[i] is { } n)
                        {
                            d.TryAdd(n, i);
                        }
                    }

                    return d;
                },
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public static PostNames? Read(ReadOnlyMemory<byte> table, int glyphCount)
        {
            ReadOnlySpan<byte> d = table.Span;
            if (d.Length < 32)
            {
                return null;
            }

            uint version = Be.U32(d, 0);
            var names = new string?[glyphCount];
            if (version == 0x00010000)
            {
                for (int g = 0; g < glyphCount && g < 258; g++)
                {
                    names[g] = CffStandardData.MacGlyphName(g);
                }

                return new PostNames(names);
            }

            if (version != 0x00020000 || d.Length < 34)
            {
                return null;
            }

            int n = Math.Min(Be.U16(d, 32), glyphCount);
            if (34L + (2L * n) > d.Length)
            {
                n = (d.Length - 34) / 2;
            }

            // The Pascal strings follow the index array.
            var custom = new List<string>();
            int p = 34 + (2 * Be.U16(d, 32));
            if (p > d.Length)
            {
                p = d.Length;
            }

            while (p < d.Length && custom.Count < 65536)
            {
                int len = d[p++];
                if (p + len > d.Length)
                {
                    break;
                }

                custom.Add(Encoding.Latin1.GetString(d.Slice(p, len)));
                p += len;
            }

            for (int g = 0; g < n; g++)
            {
                int idx = Be.U16(d, 34 + (2 * g));
                if (idx < 258)
                {
                    names[g] = CffStandardData.MacGlyphName(idx);
                }
                else if (idx - 258 < custom.Count)
                {
                    names[g] = custom[idx - 258];
                }
            }

            return new PostNames(names);
        }

        public string? Name(int gid) => (uint)gid < (uint)_names.Length ? _names[gid] : null;

        public int Find(string name) => _index.Value.TryGetValue(name, out int g) ? g : -1;
    }
}

/// <summary>Reads strings from the sfnt <c>name</c> table.</summary>
internal static class SfntName
{
    /// <summary>Finds a name, preferring Windows English, then any Windows or Unicode record, then Macintosh Roman.</summary>
    public static string? Get(ReadOnlyMemory<byte> table, int nameId)
    {
        ReadOnlySpan<byte> d = table.Span;
        if (d.Length < 6)
        {
            return null;
        }

        int count = Be.U16(d, 2);
        int strings = Be.U16(d, 4);
        count = Math.Min(count, (d.Length - 6) / 12);
        int bestScore = -1;
        string? best = null;
        for (int i = 0; i < count; i++)
        {
            int r = 6 + (12 * i);
            if (Be.U16(d, r + 6) != nameId)
            {
                continue;
            }

            int platform = Be.U16(d, r);
            int lang = Be.U16(d, r + 4);
            int score = platform switch
            {
                3 => lang == 0x409 ? 4 : 3,
                0 => 2,
                1 => 1,
                _ => -1,
            };
            if (score <= bestScore)
            {
                continue;
            }

            int len = Be.U16(d, r + 8);
            int off = strings + Be.U16(d, r + 10);
            if (off < 0 || (long)off + len > d.Length)
            {
                continue;
            }

            ReadOnlySpan<byte> s = d.Slice(off, len);
            best = platform == 1 ? Encoding.Latin1.GetString(s) : Encoding.BigEndianUnicode.GetString(s[..(len & ~1)]);
            bestScore = score;
        }

        return best;
    }
}
