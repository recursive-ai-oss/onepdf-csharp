namespace OnePdf.Layout;

/// <summary>An open page: draw into <see cref="Builder"/>, then <see cref="Commit"/>. Disposing without committing drops the page.</summary>
internal sealed class TargetPage(PdfContentBuilder builder, Action commit) : IDisposable
{
    public PdfContentBuilder Builder { get; } = builder;

    public void Commit() => commit();

    public void Dispose() => Builder.Dispose();
}

/// <summary>Where laid-out pages go: a document being built in memory, or a streaming writer that emits each page as it is committed.</summary>
internal interface ILayoutTarget
{
    int PageCount { get; }

    PdfCreatedFont AddStandardFont(string name);

    PdfCreatedFont AddFont(ReadOnlyMemory<byte> fontData, PdfEmbedFontOptions? options);

    PdfCreatedImage AddJpegImage(ReadOnlyMemory<byte> jpeg);

    PdfCreatedImage AddPngImage(ReadOnlyMemory<byte> png);

    /// <summary>A builder used only to measure text; it is never committed.</summary>
    PdfContentBuilder BeginMeasurer();

    TargetPage BeginPage(double width, double height);
}

internal sealed class DocumentTarget(PdfDocument document) : ILayoutTarget
{
    public int PageCount => document.Pages.Count;

    public PdfCreatedFont AddStandardFont(string name) => document.AddStandardFont(name);

    public PdfCreatedFont AddFont(ReadOnlyMemory<byte> fontData, PdfEmbedFontOptions? options) => document.AddFont(fontData, options);

    public PdfCreatedImage AddJpegImage(ReadOnlyMemory<byte> jpeg) => document.AddJpegImage(jpeg);

    public PdfCreatedImage AddPngImage(ReadOnlyMemory<byte> png) => document.AddPngImage(png);

    public PdfContentBuilder BeginMeasurer() => document.BeginForm(new PdfRectangle(0, 0, 1, 1));

    public TargetPage BeginPage(double width, double height)
    {
        PdfPage page = document.InsertBlankPage(document.Pages.Count, width, height);
        PdfPageContentBuilder b = page.BeginContent();
        return new TargetPage(b, b.Commit);
    }
}

internal sealed class WriterTarget(PdfDocumentWriter writer) : ILayoutTarget
{
    public int PageCount => writer.PageCount;

    public PdfCreatedFont AddStandardFont(string name) => writer.AddStandardFont(name);

    public PdfCreatedFont AddFont(ReadOnlyMemory<byte> fontData, PdfEmbedFontOptions? options) => writer.AddFont(fontData, options);

    public PdfCreatedImage AddJpegImage(ReadOnlyMemory<byte> jpeg) => writer.AddJpegImage(jpeg);

    public PdfCreatedImage AddPngImage(ReadOnlyMemory<byte> png) => writer.AddPngImage(png);

    public PdfContentBuilder BeginMeasurer() => writer.BeginForm(new PdfRectangle(0, 0, 1, 1));

    public TargetPage BeginPage(double width, double height)
    {
        PdfStreamPageBuilder b = writer.BeginPage(width, height);
        return new TargetPage(b, b.Commit);
    }
}
