namespace OnePdf;

/// <summary>What <see cref="PdfDocument.RemoveTags"/> does with a marked-content sequence that carries an <c>/MCID</c>.</summary>
public enum PdfMarkedContentRemoval
{
    /// <summary>The <c>BDC</c> and its matching <c>EMC</c> are dropped; the content between them stays where it is. This is the default.</summary>
    Drop,

    /// <summary>The <c>BDC</c> becomes a <c>BMC</c> with the same tag (<c>/P &lt;&lt;/MCID 3&gt;&gt; BDC</c> becomes <c>/P BMC</c>); the <c>EMC</c> stays.</summary>
    ConvertToBmc,
}

/// <summary>Options for <see cref="PdfDocument.RemoveTags"/>.</summary>
public sealed class PdfRemoveTagsOptions
{
    /// <summary>What happens to marked-content sequences with an <c>/MCID</c>. The default is <see cref="PdfMarkedContentRemoval.Drop"/>.</summary>
    public PdfMarkedContentRemoval MarkedContent { get; init; } = PdfMarkedContentRemoval.Drop;

    /// <summary>
    /// True also drops <c>/Artifact</c> sequences (they have no <c>/MCID</c>, so they are not part of a structure tree). The default is
    /// false: artifact marks say which content is decoration, and a later tagging pass reads them.
    /// </summary>
    public bool RemoveArtifactMarks { get; init; }

    /// <summary>
    /// True reads everything and reports what a real call would remove, without changing the document: the result's counts are the ones a
    /// call without this option would return (<see cref="PdfRemoveTagsResult.SequencesRemoved"/> counts marked-content sequences with an
    /// <c>/MCID</c>, <see cref="PdfRemoveTagsResult.StructureTreeRemoved"/> a <c>/StructTreeRoot</c>, and so on), so it answers "is this file
    /// tagged in any way" even for marked content with no tree and no <c>/MarkInfo</c>. A dry run does not need
    /// <see cref="PdfOpenOptions.AllowEditing"/> and leaves <see cref="PdfDocument.StructureTree"/> as it was. Default false.
    /// </summary>
    public bool DryRun { get; init; }
}

/// <summary>What <see cref="PdfDocument.RemoveTags"/> removed.</summary>
public sealed class PdfRemoveTagsResult
{
    internal List<string> WarningList { get; } = [];

    /// <summary>True when the catalog had a <c>/StructTreeRoot</c>, which is gone now.</summary>
    public bool StructureTreeRemoved { get; internal set; }

    /// <summary>True when the catalog had a <c>/MarkInfo</c> dictionary, which is gone now.</summary>
    public bool MarkInfoRemoved { get; internal set; }

    /// <summary>How many <c>/StructParents</c> and <c>/StructParent</c> entries were removed (pages, forms, images, annotations).</summary>
    public int StructParentsRemoved { get; internal set; }

    /// <summary>How many marked-content sequences with an <c>/MCID</c> were found and removed or converted.</summary>
    public int SequencesRemoved { get; internal set; }

    /// <summary>How many artifact sequences were dropped (only with <see cref="PdfRemoveTagsOptions.RemoveArtifactMarks"/>).</summary>
    public int ArtifactSequencesRemoved { get; internal set; }

    /// <summary>
    /// How many removed sequences carried an <c>/ActualText</c>. Those keep a <c>BDC</c> with the <c>/ActualText</c> alone, because
    /// text extraction reads it from the content stream and the page must read the same afterwards.
    /// </summary>
    public int ActualTextKept { get; internal set; }

    /// <summary>How many content streams were rewritten (a page counts once, however many streams its <c>/Contents</c> holds).</summary>
    public int StreamsChanged { get; internal set; }

    /// <summary>Content that could not be rewritten (an undecodable stream, an inline image without an end) and was left as it is, and other notes.</summary>
    public IReadOnlyList<string> Warnings => WarningList;
}
