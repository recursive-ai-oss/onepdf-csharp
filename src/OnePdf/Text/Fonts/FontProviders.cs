namespace OnePdf;

/// <summary>
/// What a PDF asks of a font provider when the file does not embed the font program (or the embedded one is damaged):
/// the font's name, its descriptor flags and, for CID-keyed fonts, the character collection. All members are plain data.
/// </summary>
public sealed class PdfFontRequest
{
    /// <summary>The <c>/BaseFont</c> name with any subset tag (<c>ABCDEF+</c>) removed.</summary>
    public string FontName { get; init; } = string.Empty;

    /// <summary>The family part of <see cref="FontName"/>: the text before the first comma or hyphen, with Windows suffixes (<c>MT</c>, <c>PS</c>) removed.</summary>
    public string FamilyName { get; init; } = string.Empty;

    /// <summary>One of the 14 standard font names (<c>Helvetica-Bold</c>, <c>Times-Roman</c>...) when <see cref="FontName"/> is one of them or a known alias such as <c>Arial,Bold</c>; otherwise null.</summary>
    public string? StandardName { get; init; }

    /// <summary>True for a bold face, from the name or the descriptor's ForceBold flag or weight.</summary>
    public bool IsBold { get; init; }

    /// <summary>True for an italic or oblique face, from the name, the descriptor's Italic flag or a non-zero italic angle.</summary>
    public bool IsItalic { get; init; }

    /// <summary>True when the descriptor's Serif flag is set or the name says so.</summary>
    public bool IsSerif { get; init; }

    /// <summary>True when the descriptor's FixedPitch flag is set or the family is a Courier-like name.</summary>
    public bool IsFixedPitch { get; init; }

    /// <summary>True for the Symbol and ZapfDingbats families and other fonts the descriptor marks symbolic with a symbol-like name.</summary>
    public bool IsSymbolic { get; init; }

    /// <summary>The CIDSystemInfo ordering of a CID-keyed font (<c>Japan1</c>, <c>GB1</c>, <c>CNS1</c>, <c>Korea1</c>, <c>KR</c>, <c>Identity</c>), or null for a simple font.</summary>
    public string? CidOrdering { get; init; }

    /// <summary>True when the font is used in vertical writing mode.</summary>
    public bool IsVertical { get; init; }
}

/// <summary>
/// A substitute font returned by an <see cref="IPdfFontProvider"/>: the bytes of a TrueType, OpenType, bare CFF or Type 1
/// program. The caller scales the substitute's glyphs horizontally so that their advances match the PDF's widths.
/// </summary>
public sealed class PdfSubstituteFont
{
    /// <summary>Creates a substitute.</summary>
    /// <param name="data">The font program bytes; the library never modifies them.</param>
    /// <param name="name">The face's PostScript or full name, for diagnostics.</param>
    /// <param name="cacheKey">An identifier that is the same whenever <paramref name="data"/> is the same font; lets the library parse the program once and share it. Null disables sharing.</param>
    /// <param name="faceIndex">Which face of a TrueType collection to use (0 for other formats).</param>
    public PdfSubstituteFont(ReadOnlyMemory<byte> data, string name, string? cacheKey = null, int faceIndex = 0)
        : this(() => data, name, cacheKey, faceIndex)
    {
    }

    /// <summary>Creates a substitute whose bytes are produced on demand, so a provider can answer a lookup without reading the font file when the library already holds the parsed program for <paramref name="cacheKey"/>.</summary>
    /// <param name="loader">Returns the font program bytes; called at most once per use, possibly from several threads.</param>
    /// <param name="name">The face's PostScript or full name, for diagnostics.</param>
    /// <param name="cacheKey">An identifier that is the same whenever the loader would return the same font. Without one the bytes are always loaded.</param>
    /// <param name="faceIndex">Which face of a TrueType collection to use (0 for other formats).</param>
    public PdfSubstituteFont(Func<ReadOnlyMemory<byte>> loader, string name, string? cacheKey = null, int faceIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(loader);
        _loader = loader;
        Name = name;
        CacheKey = cacheKey;
        FaceIndex = faceIndex;
    }

    private readonly Func<ReadOnlyMemory<byte>> _loader;

    /// <summary>The font program bytes (loaded on each call for substitutes created with a loader).</summary>
    public ReadOnlyMemory<byte> Data => _loader();

    /// <summary>The face name, for diagnostics.</summary>
    public string Name { get; }

    /// <summary>
    /// Further fonts to consult, in order, for characters this font lacks (for example a Latin font behind a math font). They are
    /// used by simple fonts only; each is loaded like the primary font. Empty by default.
    /// </summary>
    public IReadOnlyList<PdfSubstituteFont> Fallbacks { get; init; } = [];

    /// <summary>
    /// Optional adjustment of a character's code point before it is looked up by Unicode in this font and its fallbacks, for
    /// substitutes that carry a shape at a different address than the Adobe Glyph List gives. Null (the default) means no change.
    /// The lookup tries the mapped value first and the original second. Must be a pure function.
    /// </summary>
    public Func<int, int>? MapCodePoint { get; init; }

    /// <summary>Identifier for sharing parsed programs, or null.</summary>
    public string? CacheKey { get; }

    /// <summary>The face inside a TrueType collection.</summary>
    public int FaceIndex { get; }
}

/// <summary>
/// Finds a substitute for a font a PDF does not embed. Implementations must be thread-safe and must not fetch anything over a
/// network; they may read local font files. Return null when nothing suitable exists.
/// </summary>
public interface IPdfFontProvider
{
    /// <summary>Finds a font for the request, or returns null.</summary>
    PdfSubstituteFont? FindFont(PdfFontRequest request);
}

/// <summary>
/// The process-wide font provider chain: close matches among local system fonts first, then providers registered with
/// <see cref="Register"/> (for example the substitute fonts of the <c>RecursiveAI.OnePdf.Fonts</c> package, which registers itself
/// with one call), then any installed family of the right kind (sans, serif, monospace, symbols).
/// A document can use its own provider through <see cref="PdfDocument.FontProvider"/>.
/// </summary>
public static class PdfFontProviders
{
    private static readonly object Gate = new();
    private static IPdfFontProvider[] s_registered = [];

    /// <summary>
    /// Whether the default chain looks at fonts installed on this machine. Defaults to true. Turn it off for output that must not
    /// depend on the machine's fonts.
    /// </summary>
    public static bool UseSystemFonts { get; set; } = true;

    /// <summary>The default chain: system fonts (if <see cref="UseSystemFonts"/>), registered providers in registration order, generic system fonts.</summary>
    public static IPdfFontProvider Default { get; } = new ChainProvider();

    /// <summary>Adds a provider behind the system fonts. Registering the same instance twice has no effect. Thread-safe; takes no reflection or scanning.</summary>
    public static void Register(IPdfFontProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (Gate)
        {
            if (Array.IndexOf(s_registered, provider) < 0)
            {
                s_registered = [.. s_registered, provider];
            }
        }
    }

    private sealed class ChainProvider : IPdfFontProvider
    {
        public PdfSubstituteFont? FindFont(PdfFontRequest request)
        {
            if (UseSystemFonts)
            {
                PdfSubstituteFont? f = SystemFontProvider.Instance.FindFont(request);
                if (f is not null)
                {
                    return f;
                }
            }

            foreach (IPdfFontProvider p in Volatile.Read(ref s_registered))
            {
                PdfSubstituteFont? f = p.FindFont(request);
                if (f is not null)
                {
                    return f;
                }
            }

            // Last resort: any installed family of the right kind, so text keeps its shape even without a close match.
            return UseSystemFonts ? SystemFontProvider.Instance.FindGeneric(request) : null;
        }
    }
}
