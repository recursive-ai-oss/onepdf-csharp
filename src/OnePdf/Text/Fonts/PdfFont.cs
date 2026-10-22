using OnePdf.FontPrograms;

namespace OnePdf.PdfFonts;

/// <summary>Font descriptor flag bits (ISO 32000-2 Table 121).</summary>
internal static class FontFlags
{
    public const int FixedPitch = 1;
    public const int Serif = 1 << 1;
    public const int Symbolic = 1 << 2;
    public const int Script = 1 << 3;
    public const int Nonsymbolic = 1 << 5;
    public const int Italic = 1 << 6;
    public const int ForceBold = 1 << 18;
}

/// <summary>
/// One character of a shown string, resolved: its code, the glyph it selects, its Unicode text and its metrics. Instances are
/// immutable and shared by every use of the same code in a font, so decoding a string allocates nothing once a font is warm.
/// </summary>
internal sealed class FontGlyph
{
    private readonly PdfFont _font;

    internal FontGlyph(PdfFont font) => _font = font;

    /// <summary>The character code as read from the string (big endian for multi-byte codes).</summary>
    public uint Code { get; init; }

    /// <summary>Bytes the code occupies in the string: 1 for simple fonts; 1 to 4 for composite fonts.</summary>
    public int CodeLength { get; init; }

    /// <summary>The CID of a composite font's glyph, or the code itself for a simple font.</summary>
    public int Cid { get; init; }

    /// <summary>The glyph index in the font program, or -1 when the font has no usable program (then there are metrics and Unicode but no outline).</summary>
    public int Gid { get; init; } = -1;

    /// <summary>The Unicode text of the glyph (a ligature glyph may give several characters); U+FFFD when nothing in the chain knew it (<see cref="HasUnicode"/> is then false).</summary>
    public string Unicode { get; init; } = "\uFFFD";

    /// <summary>False when <see cref="Unicode"/> is the replacement character because no source of text applied.</summary>
    public bool HasUnicode { get; init; }

    /// <summary>True when the font program has a glyph for the code (not .notdef). False for a missing glyph or a font without a program.</summary>
    public bool Exists { get; init; }

    /// <summary>The glyph name from the encoding or program, when there is one.</summary>
    public string? Name { get; init; }

    /// <summary>Horizontal advance in text space units for a font size of 1 (the PDF width divided by 1000; Type 3 widths are mapped through the font matrix).</summary>
    public double Advance { get; init; }

    /// <summary>Vertical advance for a font size of 1 in vertical writing mode (the W2/DW2 w1 value divided by 1000; negative moves down); 0 in horizontal mode.</summary>
    public double VerticalAdvance { get; init; }

    /// <summary>X of the vertical origin vector v (position vector from origin 0 to origin 1), for a font size of 1. 0 in horizontal mode.</summary>
    public double OriginX { get; init; }

    /// <summary>Y of the vertical origin vector v, for a font size of 1. 0 in horizontal mode.</summary>
    public double OriginY { get; init; }

    /// <summary>Matrix (a b c d e f) from the font program's glyph space to text space for a font size of 1, including the horizontal scale applied to a substituted font. Null for Type 3.</summary>
    public double[]? OutlineMatrix { get; init; }

    /// <summary>For Type 3 fonts, the glyph's content stream (<c>/CharProcs</c>); null when the glyph has none and for other fonts.</summary>
    public PdfStream? CharProc { get; init; }

    /// <summary>The font this glyph belongs to.</summary>
    public PdfFont Font => _font;

    /// <summary>The unhinted outline in glyph space (apply <see cref="OutlineMatrix"/>), empty when the glyph is blank, missing or damaged.</summary>
    public GlyphOutline GetOutline() => _font.GetOutline(this);
}

/// <summary>What font loading needs from the document: diagnostics and the provider.</summary>
internal sealed class FontContext
{
    public FontContext(PdfDocument document, IPdfFontProvider provider, int objectNumber)
    {
        Document = document;
        Provider = provider;
        ObjectNumber = objectNumber;
    }

    public PdfDocument Document { get; }

    public IPdfFontProvider Provider { get; }

    public int ObjectNumber { get; }

    public void Warn(string message) => Document.ReportWarning(message, ObjectNumber, -1);
}

/// <summary>
/// A PDF font resolved from a font dictionary: decodes the bytes of a shown string into glyphs. Created through
/// <c>PdfDocument.GetFont</c>, cached per document and safe for concurrent use.
/// </summary>
internal abstract class PdfFont
{
    private const int MaxOutlineCache = 4096;
    private static long s_nextId;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, GlyphOutline> _outlines = new();
    private int _outlineWarned;

    protected PdfFont(PdfDictionary dictionary, FontContext context)
    {
        Id = Interlocked.Increment(ref s_nextId);
        Dictionary = dictionary;
        Context = context;
        string baseFont = dictionary.GetName("BaseFont") ?? string.Empty;
        Name = baseFont;
        Subtype = dictionary.GetName("Subtype") ?? string.Empty;
    }

    /// <summary>A process-unique identifier, usable as a cache key together with a glyph index.</summary>
    public long Id { get; }

    /// <summary>The font dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The <c>/BaseFont</c> name as written (with any subset tag), or empty.</summary>
    public string Name { get; protected set; }

    /// <summary>The <c>/Subtype</c> of the font dictionary (<c>Type1</c>, <c>TrueType</c>, <c>Type0</c>, <c>Type3</c>...).</summary>
    public string Subtype { get; }

    /// <summary>The descriptor <c>/Flags</c> (see <see cref="FontFlags"/>); 0 when there is no descriptor.</summary>
    public int Flags { get; protected set; }

    /// <summary>True when a font program from the file is in use.</summary>
    public bool IsEmbedded { get; protected set; }

    /// <summary>True when the descriptor names an embedded font program that could not be read (a diagnostic says why).</summary>
    public bool EmbeddedFailed { get; protected set; }

    /// <summary>True when the file's font is not usable and a provider supplied the outlines.</summary>
    public bool IsSubstituted { get; protected set; }

    /// <summary>1 for vertical writing, else 0.</summary>
    public int WMode { get; protected set; }

    /// <summary>True for Type 0 fonts.</summary>
    public virtual bool IsComposite => false;

    /// <summary>True for Type 3 fonts, whose glyphs are content streams.</summary>
    public virtual bool IsType3 => false;

    internal FontContext Context { get; }

    /// <summary>The font program in use, if any (embedded or substitute).</summary>
    internal FontProgram? Program { get; set; }

    /// <summary>
    /// Reads the glyph starting at <paramref name="position"/> in <paramref name="text"/> and returns the position after it.
    /// An empty remainder returns <paramref name="position"/> and a null glyph.
    /// </summary>
    public abstract int Next(ReadOnlySpan<byte> text, int position, out FontGlyph? glyph);

    /// <summary>Drops the decoded-glyph cache (glyphs are rebuilt on demand). For tools that decode a font's whole code range.</summary>
    internal virtual void ClearGlyphCache()
    {
    }

    /// <summary>Decodes a whole string, appending its glyphs.</summary>
    public void Decode(ReadOnlySpan<byte> text, List<FontGlyph> into)
    {
        int pos = 0;
        while (pos < text.Length)
        {
            int next = Next(text, pos, out FontGlyph? g);
            if (g is null || next <= pos)
            {
                break;
            }

            into.Add(g);
            pos = next;
        }
    }

    /// <summary>The glyph outline for a glyph of this font (never null; empty when there is none).</summary>
    internal GlyphOutline GetOutline(FontGlyph glyph)
    {
        FontProgram? p = Program;
        int gid = glyph.Gid;
        if (p is null || gid < 0 || gid >= p.GlyphCount)
        {
            return GlyphOutline.Empty;
        }

        if (_outlines.TryGetValue(gid, out GlyphOutline? hit))
        {
            return hit;
        }

        GlyphOutline o;
        try
        {
            o = p.GetOutline(gid);
        }
        catch (FontProgramException ex)
        {
            if (Interlocked.Exchange(ref _outlineWarned, 1) == 0)
            {
                Context.Warn($"Font '{Name}': a glyph outline could not be read ({ex.Message}); damaged glyphs are drawn empty.");
            }

            o = GlyphOutline.Empty;
        }

        if (_outlines.Count < MaxOutlineCache)
        {
            _outlines.TryAdd(gid, o);
        }

        return o;
    }

    /// <summary>The bounding box of the font in glyph space from the descriptor, or null.</summary>
    public double[]? FontBBox { get; protected set; }

    protected static string StripSubsetTag(string name) =>
        name.Length > 7 && name[6] == '+' && IsTag(name) ? name[7..] : name;

    private static bool IsTag(string n)
    {
        for (int i = 0; i < 6; i++)
        {
            if (n[i] is < 'A' or > 'Z')
            {
                return false;
            }
        }

        return true;
    }
}
