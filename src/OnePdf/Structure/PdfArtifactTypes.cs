namespace OnePdf;

/// <summary>Per page figures of <see cref="PdfDocument.ArtifactUntaggedGraphics"/>. Only pages where something was wrapped or left are listed.</summary>
public sealed class PdfArtifactPageResult
{
    /// <summary>Zero-based page index.</summary>
    public int PageIndex { get; internal set; }

    /// <summary>Painting operations (a path, an image, a shading, an inline image, a form drawn outside marked content) now marked as artifacts.</summary>
    public int Wrapped { get; internal set; }

    /// <summary>Text-showing operators outside marked content, which are left alone.</summary>
    public int UntaggedTextOperators { get; internal set; }

    /// <summary>Form XObjects drawn outside marked content that were left alone because they show text, tag content of their own or cannot be read.</summary>
    public int FormsLeft { get; internal set; }
}

/// <summary>What <see cref="PdfDocument.ArtifactUntaggedGraphics"/> did.</summary>
public sealed class PdfArtifactResult
{
    internal List<PdfArtifactPageResult> PageList { get; } = [];

    internal List<string> WarningList { get; } = [];

    /// <summary>Painting operations now marked as artifacts, over all pages.</summary>
    public int Wrapped { get; internal set; }

    /// <summary>Text-showing operators outside marked content, which are left alone: whether text is decoration is the caller's decision.</summary>
    public int UntaggedTextOperators { get; internal set; }

    /// <summary>Form XObjects drawn outside marked content that were left alone (they show text, tag content of their own, or cannot be read).</summary>
    public int FormsLeft { get; internal set; }

    /// <summary>How many pages got a new content stream.</summary>
    public int StreamsChanged { get; internal set; }

    /// <summary>The pages where something was wrapped or left alone.</summary>
    public IReadOnlyList<PdfArtifactPageResult> Pages => PageList;

    /// <summary>Pages whose content could not be read and were left as they are.</summary>
    public IReadOnlyList<string> Warnings => WarningList;
}
