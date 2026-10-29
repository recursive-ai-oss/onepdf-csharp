using System.Linq;

namespace OnePdf;

/// <summary>Options for <see cref="PdfDocument.MatchEmbeddedFontWidths"/>.</summary>
public sealed class PdfWidthMatchOptions
{
    /// <summary>
    /// Differences between a glyph's advance in the embedded program and the width the PDF gives, up to this many 1/1000 em, are left alone.
    /// Default 0.01: a program that disagrees by more than rounding noise is rewritten, because validators compare the two with a tolerance
    /// of their own and read the program's fractional widths in their own way. It applies to CFF programs, which are rewritten; for other
    /// programs only differences above 1 are reported, because their advances are whole units of the font's own grid.
    /// </summary>
    public double Tolerance { get; init; } = 0.01;
}

/// <summary>What <see cref="PdfDocument.MatchEmbeddedFontWidths"/> did with one font.</summary>
public enum PdfWidthMatchOutcome
{
    /// <summary>The embedded program was replaced by a copy whose advance widths for the shown glyphs equal the PDF's. Outlines, glyph names and encodings are the same.</summary>
    Matched,

    /// <summary>Every shown glyph already has the PDF's width within the tolerance; the font was left as it was.</summary>
    AlreadyMatching,

    /// <summary>The widths differ but the program is not a CFF program (TrueType, OpenType, Type 1), which this call does not rewrite. <see cref="PdfWidthMatchEntry.WidthChanges"/> still lists the differences.</summary>
    UnsupportedProgram,

    /// <summary>The font was left as it was: the program cannot be read or rewritten safely, or its use cannot be traced. <see cref="PdfWidthMatchEntry.Note"/> says why.</summary>
    Refused,
}

/// <summary>One font examined by <see cref="PdfDocument.MatchEmbeddedFontWidths"/>.</summary>
/// <param name="ObjectNumber">The font dictionary's object number, or -1 for a direct dictionary.</param>
/// <param name="BaseFont">The <c>/BaseFont</c>.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Note">A sentence on the outcome.</param>
public sealed record PdfWidthMatchEntry(int ObjectNumber, string BaseFont, PdfWidthMatchOutcome Outcome, string Note)
{
    /// <summary>Shown codes (character ids for a composite font) whose PDF width differs from the program's advance by more than the tolerance, with both values in 1/1000 em. For <see cref="PdfWidthMatchOutcome.Matched"/> these are the values before the change.</summary>
    public IReadOnlyList<PdfFontWidthChange> WidthChanges { get; init; } = [];

    /// <summary>Codes that share a glyph with another code but have a different PDF width. A glyph has one advance, so these keep the first code's width and still disagree.</summary>
    public IReadOnlyList<int> WidthConflicts { get; init; } = [];
}

/// <summary>The result of <see cref="PdfDocument.MatchEmbeddedFontWidths"/>.</summary>
public sealed class PdfWidthMatchReport
{
    internal PdfWidthMatchReport(IReadOnlyList<PdfWidthMatchEntry> entries) => Entries = entries;

    /// <summary>Every font that has an embedded program, in the order found.</summary>
    public IReadOnlyList<PdfWidthMatchEntry> Entries { get; }

    /// <summary>The number of fonts whose program was replaced.</summary>
    public int FontsRewritten => Entries.Count(static e => e.Outcome == PdfWidthMatchOutcome.Matched);
}
