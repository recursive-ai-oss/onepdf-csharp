using OnePdf.FontEmbedding;

namespace OnePdf;

public sealed partial class PdfDocument
{
    /// <summary>
    /// Embeds a font program into a font dictionary the document already has, so a font the file only names (Arial, Times New Roman, a
    /// symbolic TrueType subset the producer left out) becomes self-contained. The document must be opened for editing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Supported fonts are simple TrueType and Type1 fonts (they become <c>/Subtype /TrueType</c>, as ISO 32000-2 9.6.3 requires for a
    /// <c>FontFile2</c> program) and Type 0 fonts with a CIDFontType2 descendant. The program must have TrueType outlines; OpenType-CFF and CFF
    /// programs can be read with <see cref="PdfFontFile"/> but are refused here.
    /// </para>
    /// <para>
    /// Glyphs are chosen the way a viewer would choose them for an embedded font (ISO 32000-2 9.6.5.4): through glyph names and the program's
    /// Unicode map for non-symbolic fonts, through the (3,0) map for symbolic ones, and through <c>/ToUnicode</c> or the caller's
    /// <see cref="PdfFontEmbedOptions.CodeToUnicode"/> for composite fonts. The written subset has one character map that gives each shown code
    /// its glyph (ISO 32000-2 9.6.6.4), so the result works for symbolic and non-symbolic fonts alike. The font's name keeps its identity: the
    /// <c>/BaseFont</c> is the old name behind a six-letter subset tag, not the program's own name.
    /// </para>
    /// <para>
    /// The PDF's <c>/Widths</c> (or <c>/W</c>) are never changed, so text keeps its positions. By default the subset's advance widths are set to
    /// the PDF's (<see cref="PdfFontWidthPolicy.AdjustProgram"/>), so program and PDF agree for ISO 14289-1 7.21.5; outlines are not touched.
    /// An <c>/Encoding</c> that cannot be kept is rewritten to name the same characters, and a <c>/ToUnicode</c> is written or extended where the
    /// written encoding no longer says what the codes are. The font cache is refreshed, so later extraction and interpretation see the embedded font.
    /// </para>
    /// <para>
    /// On refusal the document is unchanged. Licensing of the program is the caller's: the font's own embedding flags are checked, nothing else.
    /// </para>
    /// </remarks>
    /// <param name="font">The font dictionary: a value of a page's <c>/Font</c> resource (an indirect object, so its use can be found).</param>
    /// <param name="program">The program to embed.</param>
    /// <param name="options">Options; null uses the defaults.</param>
    /// <param name="cancellationToken">Checked while the document's content is read to find the shown codes.</param>
    /// <returns>What was written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="font"/> or <paramref name="program"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The document was not opened for editing.</exception>
    /// <exception cref="PdfFontEmbedException">The embedding was refused; <see cref="PdfFontEmbedException.Reason"/> says why.</exception>
    /// <exception cref="PdfFormatException">Content that must be read to find the shown codes is damaged beyond reading.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public PdfFontEmbedResult EmbedFontProgram(PdfDictionary font, PdfFontFile program, PdfFontEmbedOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(program);
        return FontEmbedder.Embed(this, font, program, options ?? new PdfFontEmbedOptions(), cancellationToken);
    }

    /// <summary>
    /// Adds code-to-Unicode entries to a font's <c>/ToUnicode</c> map, or creates the map. Entries already in the map and its code space are
    /// kept; with <paramref name="overrideExisting"/> false (the default) the given entries only add codes the map doesn't cover. Text
    /// extraction and PDF/UA checks see the new values at once. The document must be opened for editing.
    /// </summary>
    /// <param name="font">A simple or Type 0 font dictionary.</param>
    /// <param name="entries">Unicode text by character code. Empty text is ignored. Codes are one byte for a simple font and two bytes for a Type 0 font unless <paramref name="codeLength"/> says otherwise.</param>
    /// <param name="overrideExisting">True replaces existing entries for the same codes.</param>
    /// <param name="codeLength">The byte length of the codes in <paramref name="entries"/>, 1 to 4; 0 (the default) chooses 1 for simple fonts and 2 for Type 0 fonts.</param>
    /// <returns>True when the map changed.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="codeLength"/> is outside 0 to 4.</exception>
    /// <exception cref="InvalidOperationException">The document was not opened for editing.</exception>
    public bool SetToUnicode(PdfDictionary font, IReadOnlyDictionary<int, string> entries, bool overrideExisting = false, int codeLength = 0)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfNegative(codeLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(codeLength, 4);
        EnsureEditable(0);
        int length = codeLength != 0 ? codeLength : (font.GetName("Subtype") == "Type0" || font.ContainsKey("DescendantFonts") ? 2 : 1);
        var typed = new Dictionary<int, (int, string)>(entries.Count);
        foreach ((int code, string text) in entries)
        {
            typed[code] = (length, text);
        }

        bool changed = ToUnicodeWriter.Merge(this, font, typed, overrideExisting);
        if (changed)
        {
            ForgetFont(font);
        }

        return changed;
    }

    /// <summary>
    /// Embeds metric-compatible substitutes into every font the pages use that names a standard face without embedding it (Helvetica, Arial,
    /// Times New Roman, Courier New and their bold and italic forms), using the substitutes the registered font provider supplies. With the
    /// <c>RecursiveAI.OnePdf.Fonts</c> package registered these are Liberation Sans, Serif and Mono. Fonts with no substitute, fonts that are
    /// already embedded, and fonts the embedding refuses are left as they were and listed in the report. The document must be opened for editing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The package is not found by itself: call <c>OnePdf.Fonts.FontsPackage.Register()</c> once at startup, or set <see cref="FontProvider"/> (or
    /// <see cref="PdfSubstituteEmbedOptions.Provider"/>). With no provider at all the fonts that name a standard face get
    /// <see cref="PdfSubstituteOutcome.NoFontProvider"/> and <see cref="PdfSubstituteEmbedReport.FontProviderMissing"/> is set.
    /// </para>
    /// <para>
    /// This is the happy path over <see cref="EmbedFontProgram"/>: widths follow the PDF, a <c>/ToUnicode</c> is written where the encoding stops
    /// naming characters, and a shown code the substitute lacks is left to draw <c>.notdef</c>. It changes how the text looks in viewers that had
    /// the real font; the metrics are compatible, the letter shapes are Liberation's.
    /// </para>
    /// </remarks>
    /// <param name="options">Options; null uses the defaults.</param>
    /// <param name="cancellationToken">Checked between fonts and while content is read.</param>
    /// <returns>One entry per font examined.</returns>
    /// <exception cref="InvalidOperationException">The document was not opened for editing.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public PdfSubstituteEmbedReport EmbedSubstituteFonts(PdfSubstituteEmbedOptions? options = null, CancellationToken cancellationToken = default)
    {
        EnsureEditable(0);
        return SubstituteEmbedder.Run(this, options ?? new PdfSubstituteEmbedOptions(), cancellationToken);
    }

    /// <summary>
    /// Writes <c>/ToUnicode</c> entries for the codes the document shows with fonts that lack them, using only what OnePDF's own Unicode chain
    /// already knows for each code: the encoding and <c>/Differences</c> glyph names (through the Adobe Glyph List), the embedded font's character
    /// map, and an existing map's other entries. Codes whose Unicode is unknown are never guessed: they get no entry and are listed in the report.
    /// Text extraction is unchanged, because it reads the same chain; PDF/UA checks (ISO 14289-1 7.21.7) see a map where there was none.
    /// The document must be opened for editing.
    /// </summary>
    /// <remarks>
    /// Fonts are those of the pages, forms and annotation appearances. A font is looked at when it has no <c>/ToUnicode</c> and its encoding does
    /// not already determine Unicode (a standard Latin base encoding without <c>/Differences</c>, or an Adobe CJK collection), or when it has a map
    /// that misses codes the document shows. Entries already in a map are kept. Type 3 fonts are left alone. A simple font whose use cannot be
    /// traced (a direct dictionary, unreadable content) is given every code it can name; a Type 0 font in that case is skipped. The font cache is
    /// refreshed.
    /// </remarks>
    /// <param name="cancellationToken">Checked between fonts and while content is read.</param>
    /// <returns>One entry per font that changed or has shown codes with no known Unicode.</returns>
    /// <exception cref="InvalidOperationException">The document was not opened for editing.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public PdfToUnicodeReport AddMissingToUnicode(CancellationToken cancellationToken = default) =>
        AddMissingToUnicode(new PdfToUnicodeOptions(), cancellationToken);

    /// <summary>
    /// Like <see cref="AddMissingToUnicode(CancellationToken)"/>, and also asks <see cref="PdfToUnicodeOptions.CodeToText"/> for the text of every
    /// shown code the chain cannot map, so ISO 14289-1 7.21.7 can be met for fonts whose codes carry no derivable Unicode: embedded CFF programs
    /// with glyph names such as <c>gid00132</c>, subsets without a character map, composite fonts whose map lacks codes. This works for every
    /// font kind, because it only writes the font dictionary's <c>/ToUnicode</c>; the font program is not read or changed.
    /// </summary>
    /// <remarks>
    /// A <c>/ToUnicode</c> that has a <c>bfrange</c> across a boundary of its last code byte (such as <c>&lt;0000&gt; &lt;FFFF&gt; &lt;0000&gt;</c>, which
    /// readers that follow ISO 32000-2 9.10.3 read wrongly beyond the first 256 codes) is written again with the same meaning in valid ranges;
    /// <see cref="PdfToUnicodeEntry.MapRewritten"/> says so. Two fonts of one file that draw the same glyph are mapped separately: nothing is
    /// derived from another subset, because a glyph's shape does not say which character it is.
    /// </remarks>
    /// <param name="options">Options, including the caller's code-to-text callback.</param>
    /// <param name="cancellationToken">Checked between fonts and while content is read.</param>
    /// <returns>One entry per font that changed or has shown codes with no known Unicode; <see cref="PdfToUnicodeEntry.Unmapped"/> describes what is left.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The document was not opened for editing.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public PdfToUnicodeReport AddMissingToUnicode(PdfToUnicodeOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        EnsureEditable(0);
        return ToUnicodeAdder.Run(this, options, cancellationToken);
    }

    /// <summary>
    /// Rewrites embedded CFF font programs so the advance width of each glyph the document shows equals the width the PDF gives it in
    /// <c>/Widths</c> or <c>/W</c>, as ISO 14289-1 7.21.5 requires. Text positions come from the PDF's widths and stay as they are; outlines,
    /// glyph names and encodings are not touched. The document must be opened for editing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Producers that subset a font instanced from a variable font often leave the instance's fractional advances in the program while the PDF
    /// lists rounded or differently instanced widths. A CFF program keeps each advance as the first operand of the glyph's charstring, relative to
    /// the Private DICT's <c>nominalWidthX</c>; this changes that operand for the shown glyphs and makes both width defaults whole numbers.
    /// Every glyph of the program is written again (unchanged except for that operand) and the result is read back and compared with the
    /// original before it replaces it: glyph count, names, outlines and widths. A glyph that reaches its width through a subroutine, or any
    /// mismatch, leaves the font as it was (outcome <see cref="PdfWidthMatchOutcome.Refused"/>).
    /// </para>
    /// <para>
    /// Handles <c>FontFile3</c> programs of subtype <c>Type1C</c> and <c>CIDFontType0C</c>, simple and composite. TrueType, OpenType and Type 1
    /// programs are only examined (<see cref="PdfWidthMatchOutcome.UnsupportedProgram"/>); for TrueType fonts embedded with
    /// <see cref="EmbedFontProgram"/> or <see cref="EmbedSubstituteFonts"/> the widths already agree. Fonts shown nowhere are left alone. The font
    /// descriptor is replaced by a copy that names the new program, so other fonts that shared the old descriptor keep the old program.
    /// </para>
    /// <para>
    /// A glyph shared by codes with different PDF widths has one advance: the first code's width is used and the others are listed in
    /// <see cref="PdfWidthMatchEntry.WidthConflicts"/>.
    /// </para>
    /// </remarks>
    /// <param name="options">Options; null uses the defaults.</param>
    /// <param name="cancellationToken">Checked between fonts and while content is read.</param>
    /// <returns>One entry per font that has an embedded program.</returns>
    /// <exception cref="InvalidOperationException">The document was not opened for editing.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public PdfWidthMatchReport MatchEmbeddedFontWidths(PdfWidthMatchOptions? options = null, CancellationToken cancellationToken = default)
    {
        EnsureEditable(0);
        return WidthMatcher.Run(this, options ?? new PdfWidthMatchOptions(), cancellationToken);
    }

    /// <summary>Drops the cached decoded font for a dictionary, after its entries changed.</summary>
    internal void ForgetFont(PdfDictionary font) => _fontCache?.Forget(font);
}
