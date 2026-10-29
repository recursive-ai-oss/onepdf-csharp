using System.Linq;

namespace OnePdf;

/// <summary>Why a font program could not be embedded into a font dictionary. See <see cref="PdfFontEmbedException.Reason"/>.</summary>
public enum PdfFontEmbedFailure
{
    /// <summary>A code the document shows maps to no glyph of the program (it would draw <c>.notdef</c>). <see cref="PdfFontEmbedException.Codes"/> lists the codes.</summary>
    NoGlyphForCode,

    /// <summary>The program's <c>fsType</c> forbids embedding, or forbids subsetting while a subset was requested.</summary>
    ProgramForbidsEmbedding,

    /// <summary>The font already has an embedded program and <see cref="PdfFontEmbedOptions.Replace"/> is false.</summary>
    AlreadyEmbedded,

    /// <summary>The font is a Type 3 font: its glyphs are content streams, not a font program.</summary>
    Type3Font,

    /// <summary>The dictionary is not a font this API can embed into (not a font, or a CIDFontType0 descendant, or a composite font without descendants).</summary>
    UnsupportedFont,

    /// <summary>The program is not a TrueType-outline font (OpenType-CFF and bare CFF programs are readable but are not embedded by this API yet).</summary>
    UnsupportedProgram,

    /// <summary>The PDF's widths differ from the program's by more than <see cref="PdfFontEmbedOptions.MaxWidthDifference"/> under <see cref="PdfFontWidthPolicy.Refuse"/>.</summary>
    WidthMismatch,

    /// <summary>The embedding would exceed a bound (glyph count, character id range).</summary>
    TooLarge,
}

/// <summary>
/// An embedding request was refused. The document is left as it was. <see cref="Reason"/> says why; <see cref="Codes"/> lists the character
/// codes involved when the reason is about codes.
/// </summary>
public sealed class PdfFontEmbedException : PdfException
{
    /// <summary>Creates the exception.</summary>
    public PdfFontEmbedException(PdfFontEmbedFailure reason, string message, IReadOnlyList<int>? codes = null)
        : base(message)
    {
        Reason = reason;
        Codes = codes ?? [];
    }

    /// <summary>The typed reason.</summary>
    public PdfFontEmbedFailure Reason { get; }

    /// <summary>The character codes the reason is about; empty when it is not about codes.</summary>
    public IReadOnlyList<int> Codes { get; }
}

/// <summary>What to do with the advance widths in the embedded program when the PDF's <c>/Widths</c> (or <c>/W</c>) differ from them.</summary>
public enum PdfFontWidthPolicy
{
    /// <summary>
    /// Write the PDF's widths into the embedded subset's <c>hmtx</c> for the glyphs it shows. Text positions come from the PDF's widths and
    /// stay as they are; the program then agrees with them, as ISO 14289-1 7.21.5 requires. Only advances change, never outlines.
    /// This is the default.
    /// </summary>
    AdjustProgram,

    /// <summary>Embed the program's own advances and report the differences in <see cref="PdfFontEmbedResult.WidthChanges"/>.</summary>
    KeepProgram,

    /// <summary>Refuse with <see cref="PdfFontEmbedFailure.WidthMismatch"/> when a width differs by more than <see cref="PdfFontEmbedOptions.MaxWidthDifference"/>.</summary>
    Refuse,
}

/// <summary>What to do with a shown code that has no glyph in the program.</summary>
public enum PdfMissingGlyphPolicy
{
    /// <summary>Refuse with <see cref="PdfFontEmbedFailure.NoGlyphForCode"/>. This is the default.</summary>
    Refuse,

    /// <summary>Leave the code unmapped, so it draws <c>.notdef</c>, and list it in <see cref="PdfFontEmbedResult.MissingCodes"/>.</summary>
    UseNotdef,
}

/// <summary>Options for <see cref="PdfDocument.EmbedFontProgram"/>.</summary>
public sealed class PdfFontEmbedOptions
{
    /// <summary>
    /// True (the default) embeds only the glyphs of the codes the document shows; false embeds every glyph the program has (glyph ids
    /// are kept), with codes mapped wherever the program can map them.
    /// </summary>
    public bool Subset { get; init; } = true;

    /// <summary>
    /// The codes to embed glyphs for. Null (the default) finds the codes by reading every page, form, pattern and annotation appearance that
    /// shows the font; a font that cannot be tracked (a direct dictionary, an unreadable stream) keeps every code its encoding names.
    /// </summary>
    public IReadOnlyCollection<int>? Codes { get; init; }

    /// <summary>True replaces an already embedded program; false (the default) refuses with <see cref="PdfFontEmbedFailure.AlreadyEmbedded"/>.</summary>
    public bool Replace { get; init; }

    /// <summary>True embeds a program whose <c>fsType</c> forbids it. The caller takes responsibility for the license. Default false.</summary>
    public bool IgnoreEmbeddingRestrictions { get; init; }

    /// <summary>What to do with the PDF's widths; see <see cref="PdfFontWidthPolicy"/>. Default <see cref="PdfFontWidthPolicy.AdjustProgram"/>. The PDF's <c>/Widths</c> are never rewritten.</summary>
    public PdfFontWidthPolicy Widths { get; init; } = PdfFontWidthPolicy.AdjustProgram;

    /// <summary>The largest accepted difference, in 1/1000 em, under <see cref="PdfFontWidthPolicy.Refuse"/>. Default 1.</summary>
    public double MaxWidthDifference { get; init; } = 1;

    /// <summary>What to do with shown codes that have no glyph. Default <see cref="PdfMissingGlyphPolicy.Refuse"/>.</summary>
    public PdfMissingGlyphPolicy MissingGlyphs { get; init; } = PdfMissingGlyphPolicy.Refuse;

    /// <summary>
    /// Unicode text by character code, for fonts whose encoding does not say what a code means (symbolic subsets, composite fonts without
    /// a usable <c>/ToUnicode</c>). It selects glyphs through the program's Unicode map and is also written into <c>/ToUnicode</c>
    /// (see <see cref="OverrideToUnicode"/>).
    /// </summary>
    public IReadOnlyDictionary<int, string>? CodeToUnicode { get; init; }

    /// <summary>
    /// Chooses the glyph of a code directly; return a glyph id above 0, or 0 or less to leave the choice to the default rules
    /// (ISO 32000-2 9.6.5.4 for simple fonts, Unicode through <c>/ToUnicode</c> for composite fonts). Called once per shown code, on the calling thread.
    /// </summary>
    public Func<int, int>? GlyphForCode { get; init; }

    /// <summary>
    /// For a composite font: the program is the font the document was made with, so a character id is the glyph id in the program. Default false:
    /// glyphs are found through Unicode.
    /// </summary>
    public bool CidsAreGlyphIds { get; init; }

    /// <summary>
    /// True writes <see cref="CodeToUnicode"/> over existing <c>/ToUnicode</c> entries for the same codes; false (the default) adds only codes the
    /// map does not cover.
    /// </summary>
    public bool OverrideToUnicode { get; init; }

    /// <summary>Adjusts a Unicode value before it is looked up in the program, for substitutes that keep a shape at another address (see <see cref="PdfSubstituteFont.MapCodePoint"/>). The mapped value is tried first, the original second.</summary>
    internal Func<int, int>? CodePointMap { get; init; }

    /// <summary>True (the default) writes or extends <c>/ToUnicode</c> where the embedding needs it: when the font already has one, when
    /// <see cref="CodeToUnicode"/> is set, or when the encoding written no longer names the characters.</summary>
    public bool WriteToUnicode { get; init; } = true;
}

/// <summary>A code whose width in the PDF differs from the program's advance.</summary>
/// <param name="Code">The character code (the character id for a composite font).</param>
/// <param name="PdfWidth">The width the PDF gives, 1/1000 em.</param>
/// <param name="ProgramWidth">The advance the program had for the glyph, 1/1000 em.</param>
public readonly record struct PdfFontWidthChange(int Code, double PdfWidth, double ProgramWidth);

/// <summary>What <see cref="PdfDocument.EmbedFontProgram"/> did.</summary>
public sealed class PdfFontEmbedResult
{
    internal PdfFontEmbedResult()
    {
    }

    /// <summary>The stream object holding the embedded program.</summary>
    public PdfReference FontFile { get; internal set; } = null!;

    /// <summary>The <c>/BaseFont</c> now written, with its six-letter subset tag when the program was subset.</summary>
    public string BaseFont { get; internal set; } = string.Empty;

    /// <summary>The codes (character codes for a simple font, codes as shown for a composite font) that now have a glyph.</summary>
    public IReadOnlyList<int> EmbeddedCodes { get; internal set; } = [];

    /// <summary>Shown codes that have no glyph in the program and now draw <c>.notdef</c> (only with <see cref="PdfMissingGlyphPolicy.UseNotdef"/>).</summary>
    public IReadOnlyList<int> MissingCodes { get; internal set; } = [];

    /// <summary>Codes whose PDF width differs from the program's advance. With the default policy the subset carries the PDF's width for them.</summary>
    public IReadOnlyList<PdfFontWidthChange> WidthChanges { get; internal set; } = [];

    /// <summary>Codes that share a glyph with another code but have a different PDF width. A glyph has one advance, so these keep the first code's width and still disagree.</summary>
    public IReadOnlyList<int> WidthConflicts { get; internal set; } = [];

    /// <summary>True when a <c>/ToUnicode</c> map was written or extended.</summary>
    public bool ToUnicodeWritten { get; internal set; }

    /// <summary>Shown codes for which no Unicode value is known, so <c>/ToUnicode</c> has no entry for them.</summary>
    public IReadOnlyList<int> CodesWithoutUnicode { get; internal set; } = [];

    /// <summary>True when the font was written as symbolic (codes reach glyphs through a (3,0) character map, and <c>/Encoding</c> was removed).</summary>
    public bool Symbolic { get; internal set; }
}

/// <summary>Options for <see cref="PdfDocument.EmbedSubstituteFonts"/>.</summary>
public sealed class PdfSubstituteEmbedOptions
{
    /// <summary>Embed only the glyphs the document shows (default true).</summary>
    public bool Subset { get; init; } = true;

    /// <summary>What to do with the PDF's widths (default <see cref="PdfFontWidthPolicy.AdjustProgram"/>).</summary>
    public PdfFontWidthPolicy Widths { get; init; } = PdfFontWidthPolicy.AdjustProgram;

    /// <summary>
    /// The provider that supplies substitutes. Null (the default) uses <see cref="PdfDocument.FontProvider"/>, then the registered providers
    /// (the <c>RecursiveAI.OnePdf.Fonts</c> package registers Liberation Sans, Serif and Mono).
    /// </summary>
    public IPdfFontProvider? Provider { get; init; }
}

/// <summary>What happened to one font in <see cref="PdfDocument.EmbedSubstituteFonts"/>.</summary>
public enum PdfSubstituteOutcome
{
    /// <summary>A substitute was embedded.</summary>
    Embedded,

    /// <summary>The font already has a program; nothing to do.</summary>
    AlreadyEmbedded,

    /// <summary>The font's name maps to no substitute (or the provider has none). It is left as it was.</summary>
    NoSubstitute,

    /// <summary>The embedding was refused; <see cref="PdfSubstituteEntry.Failure"/> says why.</summary>
    Refused,
}

/// <summary>One font examined by <see cref="PdfDocument.EmbedSubstituteFonts"/>.</summary>
/// <param name="ObjectNumber">The font dictionary's object number, or -1 for a direct dictionary.</param>
/// <param name="BaseFont">The <c>/BaseFont</c> before embedding.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Substitute">The substitute's name when one was used or chosen.</param>
/// <param name="Failure">The reason, for <see cref="PdfSubstituteOutcome.Refused"/>.</param>
/// <param name="Message">A sentence about the outcome.</param>
public sealed record PdfSubstituteEntry(int ObjectNumber, string BaseFont, PdfSubstituteOutcome Outcome, string? Substitute, PdfFontEmbedFailure? Failure, string Message);

/// <summary>The result of <see cref="PdfDocument.EmbedSubstituteFonts"/>.</summary>
public sealed class PdfSubstituteEmbedReport
{
    internal PdfSubstituteEmbedReport(IReadOnlyList<PdfSubstituteEntry> entries) => Entries = entries;

    /// <summary>Every font examined, in the order found.</summary>
    public IReadOnlyList<PdfSubstituteEntry> Entries { get; }

    /// <summary>The number of fonts that now carry an embedded substitute.</summary>
    public int EmbeddedCount => Entries.Count(e => e.Outcome == PdfSubstituteOutcome.Embedded);
}
