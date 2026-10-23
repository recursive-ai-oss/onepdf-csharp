namespace OnePdf;

/// <summary>The part of ISO 19005 a file is made to conform to.</summary>
public enum PdfAPart
{
    /// <summary>ISO 19005-2 (PDF/A-2), based on ISO 32000-1 (PDF 1.7). Embedded files are not written or accepted by OnePDF.</summary>
    Part2 = 2,

    /// <summary>ISO 19005-3 (PDF/A-3): like part 2, and embedded files of any type are allowed when each is associated through <c>/AF</c> with an <c>/AFRelationship</c>.</summary>
    Part3 = 3,
}

/// <summary>The conformance level within a part of ISO 19005.</summary>
public enum PdfAConformance
{
    /// <summary>Level B (basic): the visual appearance is reproducible.</summary>
    B = 0,

    /// <summary>Level U (Unicode): level B, and every font maps its codes to Unicode (ISO 19005-2, 6.2.11.7), so text can be extracted reliably. OnePDF offers U only when that holds.</summary>
    U = 1,
}

/// <summary>What <see cref="PdfSaveOptions.PdfA"/>, <see cref="PdfDocument.PreparePdfA"/> and <see cref="PdfDocument.CheckPdfA"/> aim for.</summary>
public sealed class PdfAOptions
{
    /// <summary>The part of ISO 19005. Default <see cref="PdfAPart.Part2"/>.</summary>
    public PdfAPart Part { get; init; } = PdfAPart.Part2;

    /// <summary>The conformance level. Default <see cref="PdfAConformance.B"/>. Level A (tagged structure) is not offered.</summary>
    public PdfAConformance Conformance { get; init; } = PdfAConformance.B;

    /// <summary>
    /// When true (the default) and the <c>RecursiveAI.OnePdf.Fonts</c> package is registered, the standard 14 fonts that OnePDF itself
    /// put into the document (<see cref="PdfDocument.AddStandardFont"/>, and the fonts of appearances it generated) are replaced by
    /// embedded Liberation faces, which have the same metrics. Standard fonts that came with the file are never touched: they are
    /// reported as violations. Symbol and ZapfDingbats have no substitute with matching glyph names and are always reported.
    /// </summary>
    public bool EmbedLibraryFonts { get; init; } = true;

    /// <summary>The identification written to the XMP packet, for example <c>PDF/A-2b</c>.</summary>
    public string Name => $"PDF/A-{(int)Part}{(Conformance == PdfAConformance.B ? 'b' : 'u')}";
}

/// <summary>One requirement of ISO 19005 that a document does not meet.</summary>
public sealed class PdfAViolation
{
    internal PdfAViolation(string clause, string message, int objectNumber, int pageIndex)
    {
        Clause = clause;
        Message = message;
        ObjectNumber = objectNumber;
        PageIndex = pageIndex;
    }

    /// <summary>The clause of ISO 19005-2 (the same numbers apply to part 3) the requirement comes from, such as <c>6.2.11.4.1</c>.</summary>
    public string Clause { get; }

    /// <summary>What is wrong, in plain words.</summary>
    public string Message { get; }

    /// <summary>The indirect object the problem was found in, or -1 when it concerns the file or the catalog as a whole.</summary>
    public int ObjectNumber { get; }

    /// <summary>The zero-based page the problem was found on, or -1 when not tied to a page.</summary>
    public int PageIndex { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"[{Clause}] {Message}" + (ObjectNumber >= 0 ? $" (object {ObjectNumber} 0 R)" : string.Empty) + (PageIndex >= 0 ? $" (page {PageIndex + 1})" : string.Empty);
}

/// <summary>
/// The document cannot be written as PDF/A because it breaks requirements OnePDF cannot repair. Nothing is written to the output
/// stream. <see cref="Violations"/> lists every problem found.
/// </summary>
public sealed class PdfAException : PdfException
{
    internal PdfAException(string profile, IReadOnlyList<PdfAViolation> violations)
        : base(Describe(profile, violations))
    {
        Violations = violations;
    }

    /// <summary>Every requirement that is not met, in the order found.</summary>
    public IReadOnlyList<PdfAViolation> Violations { get; }

    private static string Describe(string profile, IReadOnlyList<PdfAViolation> violations)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("The document cannot be written as ").Append(profile).Append(": ").Append(violations.Count).Append(violations.Count == 1 ? " violation." : " violations.");
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
