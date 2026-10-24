namespace OnePdf;

/// <summary>What <see cref="PdfSaveOptions.PdfUA"/>, <see cref="PdfDocument.PreparePdfUA"/> and <see cref="PdfDocument.CheckPdfUA"/> aim for: ISO 14289-1 (PDF/UA-1).</summary>
public sealed class PdfUAOptions
{
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

    /// <summary>The clause of ISO 14289-1 the requirement comes from, such as <c>7.3</c>. Findings that PDF/A level A also requires are numbered by ISO 19005 clause when raised for PDF/A.</summary>
    public string Clause { get; }

    /// <summary>What is wrong, in plain words.</summary>
    public string Message { get; }

    /// <summary>The indirect object the problem was found in, or -1 when it concerns the file or the catalog as a whole.</summary>
    public int ObjectNumber { get; }

    /// <summary>The zero-based page the problem was found on, or -1 when not tied to a page.</summary>
    public int PageIndex { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"[{Clause}, checkpoint {Checkpoint}] {Message}" + (ObjectNumber >= 0 ? $" (object {ObjectNumber} 0 R)" : string.Empty) + (PageIndex >= 0 ? $" (page {PageIndex + 1})" : string.Empty);
}

/// <summary>
/// The document cannot be written as PDF/UA-1 because it breaks requirements OnePDF cannot repair. Nothing is written to the output
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
        sb.Append("The document cannot be written as PDF/UA-1: ").Append(violations.Count).Append(violations.Count == 1 ? " violation." : " violations.");
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
