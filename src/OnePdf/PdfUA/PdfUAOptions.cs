namespace OnePdf;

/// <summary>The part of ISO 14289 (PDF/UA) a document is prepared and checked for.</summary>
public enum PdfUAPart
{
    /// <summary>ISO 14289-1:2014, built on PDF 1.7. The default.</summary>
    Part1 = 1,

    /// <summary>
    /// ISO 14289-2, built on PDF 2.0 (ISO 32000-2). A save writes PDF 2.0, puts every structure element in the PDF 2.0 structure
    /// namespace (ISO 32000-2 14.7.4.2) and identifies the file with <c>pdfuaid:part</c> 2 and <c>pdfuaid:rev</c> 2024. It cannot be
    /// combined with PDF/A-2 or PDF/A-3, which are PDF 1.7 formats.
    /// </summary>
    Part2 = 2,
}

/// <summary>What <see cref="PdfSaveOptions.PdfUA"/>, <see cref="PdfDocument.PreparePdfUA"/> and <see cref="PdfDocument.CheckPdfUA(CancellationToken)"/> aim for: ISO 14289-1 (PDF/UA-1) by default, ISO 14289-2 (PDF/UA-2) with <see cref="Part"/>.</summary>
public sealed class PdfUAOptions
{
    /// <summary>Which part of ISO 14289 to prepare for and check against. Default <see cref="PdfUAPart.Part1"/>.</summary>
    public PdfUAPart Part { get; init; } = PdfUAPart.Part1;

    /// <summary>
    /// The language to write as the catalog's <c>/Lang</c> (a BCP 47 tag such as <c>en-US</c>) when the document has none. Default null:
    /// a document without <c>/Lang</c> is reported, because OnePDF cannot know the language of the text. An existing <c>/Lang</c> is kept.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// When true (the default) and the <c>RecursiveAI.OnePdf.Fonts</c> package is registered, the standard 14 fonts that OnePDF itself put
    /// into the document are replaced by embedded Liberation faces, because PDF/UA needs every font embedded (ISO 14289-1 7.21.4.1).
    /// Fonts that came with a file are never replaced.
    /// </summary>
    public bool EmbedLibraryFonts { get; init; } = true;

    /// <summary>
    /// When true, fonts the file names without embedding (Arial, Times New Roman, Courier New and the other standard faces and their common
    /// aliases) get metric-compatible substitutes embedded by <see cref="PdfDocument.EmbedSubstituteFonts"/>, so ISO 14289-1 7.21.4.1 is met
    /// for them. Default false, because it changes the letter shapes in viewers that had the real font; without it such fonts are reported.
    /// Needs a font provider (reference <c>RecursiveAI.OnePdf.Fonts</c> and call <c>FontsPackage.Register()</c>, or set <see cref="PdfDocument.FontProvider"/>);
    /// the save throws <see cref="InvalidOperationException"/> naming the fix when a font needs a substitute and there is none.
    /// </summary>
    public bool EmbedSubstituteFonts { get; init; }
}

/// <summary>One requirement of ISO 14289-1 (PDF/UA-1) that a document does not meet.</summary>
public sealed class PdfUAViolation
{
    internal PdfUAViolation(string checkpoint, string clause, string message, int objectNumber, int pageIndex)
    {
        Checkpoint = checkpoint;
        Clause = clause;
        Message = message;
        ObjectNumber = objectNumber;
        PageIndex = pageIndex;
    }

    /// <summary>The checkpoint of the Matterhorn Protocol (PDF Association) the finding falls under, such as <c>13</c> for graphics.</summary>
    public string Checkpoint { get; }

    /// <summary>The clause of ISO 14289-1 (or of ISO 14289-2 when <see cref="Part"/> is 2) the requirement comes from, such as <c>7.3</c>. Findings that PDF/A level A also requires are numbered by ISO 19005 clause when raised for PDF/A.</summary>
    public string Clause { get; }

    /// <summary>The part of ISO 14289 the finding was raised against: 1 or 2.</summary>
    public int Part { get; internal init; } = 1;

    /// <summary>What is wrong, in plain words.</summary>
    public string Message { get; }

    /// <summary>The indirect object the problem was found in, or -1 when it concerns the file or the catalog as a whole.</summary>
    public int ObjectNumber { get; }

    /// <summary>The zero-based page the problem was found on, or -1 when not tied to a page.</summary>
    public int PageIndex { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"[{(Part == 2 ? "PDF/UA-2 " : string.Empty)}{(Clause == "-" ? "general" : Clause)}{(Checkpoint == "-" ? string.Empty : ", checkpoint " + Checkpoint)}] {Message}" + (ObjectNumber >= 0 ? $" (object {ObjectNumber} 0 R)" : string.Empty) + (PageIndex >= 0 ? $" (page {PageIndex + 1})" : string.Empty);
}

/// <summary>
/// The document cannot be written as PDF/UA (part 1 or part 2) because it breaks requirements OnePDF cannot repair. Nothing is written to the output
/// stream. <see cref="Violations"/> lists every problem found.
/// </summary>
public sealed class PdfUAException : PdfException
{
    internal PdfUAException(IReadOnlyList<PdfUAViolation> violations)
        : base(Describe(violations))
    {
        Violations = violations;
    }

    /// <summary>Every requirement that is not met, in the order found (at most 2000; large numbers of one kind are cut after twenty).</summary>
    public IReadOnlyList<PdfUAViolation> Violations { get; }

    private static string Describe(IReadOnlyList<PdfUAViolation> violations)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("The document cannot be written as PDF/UA-" + (violations.Count > 0 ? violations[0].Part : 1) + ": ").Append(violations.Count).Append(violations.Count == 1 ? " violation." : " violations.");
        int shown = Math.Min(violations.Count, 12);
        for (int i = 0; i < shown; i++)
        {
            sb.Append("\n  ").Append(violations[i]);
        }

        if (violations.Count > shown)
        {
            sb.Append("\n  ... and ").Append(violations.Count - shown).Append(" more.");
        }

        return sb.ToString();
    }
}
