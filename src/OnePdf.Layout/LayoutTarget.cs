namespace OnePdf.Layout;

/// <summary>An open page: draw into <see cref="Builder"/>, then <see cref="Commit"/>. Disposing without committing drops the page.</summary>
internal sealed class TargetPage(PdfContentBuilder builder, Action commit, PdfStreamPageBuilder? stream = null) : IDisposable
{
    public PdfContentBuilder Builder { get; } = builder;

    /// <summary>The streaming page, which can carry link annotations; null for a page of an editable document.</summary>
    public PdfStreamPageBuilder? Stream { get; } = stream;

    public void Commit() => commit();

    public void Dispose() => Builder.Dispose();
}

/// <summary>Where laid-out pages go: a document being built in memory, or a streaming writer that emits each page as it is committed.</summary>
internal interface ILayoutTarget
{
    int PageCount { get; }

    PdfCreatedFont AddStandardFont(string name);

    PdfCreatedFont AddFont(PdfFontSource font, PdfEmbedFontOptions? options);

    PdfCreatedImage AddJpegImage(ReadOnlyMemory<byte> jpeg);

    PdfCreatedImage AddPngImage(ReadOnlyMemory<byte> png);

    /// <summary>A builder used only to measure text; it is never committed.</summary>
    PdfContentBuilder BeginMeasurer();

    TargetPage BeginPage(double width, double height);

    /// <summary>True when the target can record links, bookmarks, named destinations and page labels.</summary>
    bool SupportsNavigation { get; }

    /// <summary>True when the target can write a structure tree and tagged content.</summary>
    bool SupportsStructure { get; }

    PdfStructureElement BeginStructure(PdfStructureElement? parent, string type, PdfStructureElementOptions? options);

    void EndStructure(PdfStructureElement element);

    void AddBookmark(string title, int level, int pageIndex, double top);

    void AddNamedDestination(string name, int pageIndex, double top);

    void AddPageLabelRange(int firstPageIndex, PdfPageLabelStyle style, string prefix, int start);

    /// <summary>
    /// A form for a number that is known only once the pages it counts are done. <paramref name="section"/> is a section index, or -1 for the
    /// whole document; <paramref name="paint"/> draws the number given the count.
    /// </summary>
    PdfFormXObject TotalForm(PdfRectangle box, int section, Action<PdfFormContentBuilder, int> paint);

    /// <summary>Tells the target how many pages <paramref name="section"/> (-1: the document) has, so the forms waiting for it can be filled.</summary>
    void SetTotal(int section, int count);
}

internal sealed class DocumentTarget(PdfDocument document, int[]? sectionTotals = null) : ILayoutTarget
{
    // Counts from an earlier run of the same layout: a document built in memory cannot fill a form after the pages that draw it are done.
    private readonly int[] _sectionTotals = sectionTotals ?? [];

    public bool SupportsNavigation => false;

    public bool SupportsStructure => false;

    public PdfStructureElement BeginStructure(PdfStructureElement? parent, string type, PdfStructureElementOptions? options) => throw new NotSupportedException();

    public void EndStructure(PdfStructureElement element) => throw new NotSupportedException();

    public void AddBookmark(string title, int level, int pageIndex, double top) { }

    public void AddNamedDestination(string name, int pageIndex, double top) { }

    public void AddPageLabelRange(int firstPageIndex, PdfPageLabelStyle style, string prefix, int start) { }

    public PdfFormXObject TotalForm(PdfRectangle box, int section, Action<PdfFormContentBuilder, int> paint)
    {
        int count = 0;
        if (section < 0)
        {
            foreach (int n in _sectionTotals)
            {
                count += n;
            }
        }
        else if (section < _sectionTotals.Length)
        {
            count = _sectionTotals[section];
        }

        PdfFormContentBuilder fb = document.BeginForm(box);
        paint(fb, count);
        return fb.Commit();
    }

    public void SetTotal(int section, int count) { }

    public int PageCount => document.Pages.Count;

    public PdfCreatedFont AddStandardFont(string name) => document.AddStandardFont(name);

    public PdfCreatedFont AddFont(PdfFontSource font, PdfEmbedFontOptions? options) => document.AddFont(font, options);

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
    private readonly List<(PdfFormXObject Form, int Section, Action<PdfFormContentBuilder, int> Paint)> _pending = [];

    public bool SupportsNavigation => true;

    public bool SupportsStructure => true;

    public PdfStructureElement BeginStructure(PdfStructureElement? parent, string type, PdfStructureElementOptions? options) => writer.BeginStructureElement(parent, type, options);

    public void EndStructure(PdfStructureElement element) => writer.EndStructureElement(element);

    public void AddBookmark(string title, int level, int pageIndex, double top) => writer.AddBookmark(title, level, pageIndex, top);

    public void AddNamedDestination(string name, int pageIndex, double top) => writer.AddNamedDestination(name, pageIndex, top);

    public void AddPageLabelRange(int firstPageIndex, PdfPageLabelStyle style, string prefix, int start) => writer.AddPageLabelRange(firstPageIndex, style, prefix, start);

    public PdfFormXObject TotalForm(PdfRectangle box, int section, Action<PdfFormContentBuilder, int> paint)
    {
        PdfFormXObject form = writer.ReserveForm(box);
        _pending.Add((form, section, paint));
        return form;
    }

    public void SetTotal(int section, int count)
    {
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            if (_pending[i].Section != section)
            {
                continue;
            }

            PdfFormContentBuilder fb = writer.BeginForm(_pending[i].Form);
            _pending[i].Paint(fb, count);
            fb.Commit();
            _pending.RemoveAt(i);
        }
    }

    public int PageCount => writer.PageCount;

    public PdfCreatedFont AddStandardFont(string name) => writer.AddStandardFont(name);

    public PdfCreatedFont AddFont(PdfFontSource font, PdfEmbedFontOptions? options) => writer.AddFont(font, options);

    public PdfCreatedImage AddJpegImage(ReadOnlyMemory<byte> jpeg) => writer.AddJpegImage(jpeg);

    public PdfCreatedImage AddPngImage(ReadOnlyMemory<byte> png) => writer.AddPngImage(png);

    public PdfContentBuilder BeginMeasurer() => writer.BeginForm(new PdfRectangle(0, 0, 1, 1));

    public TargetPage BeginPage(double width, double height)
    {
        PdfStreamPageBuilder b = writer.BeginPage(width, height);
        return new TargetPage(b, b.Commit, b);
    }
}
