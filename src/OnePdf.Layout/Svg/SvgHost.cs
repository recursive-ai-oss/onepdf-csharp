using System.Runtime.CompilerServices;
using OnePdf.Layout.Svg.Dom;

namespace OnePdf.Layout.Svg;

/// <summary>What the SVG painter needs from the document it draws into: fonts, images, forms and a place for notes.</summary>
internal interface ISvgHost
{
    TextFontRegistry Fonts { get; }

    TextRenderer Renderer { get; }

    /// <summary>Adds a JPEG or PNG once per source element; the same element drawn again reuses the picture.</summary>
    PdfCreatedImage AddImage(SvgElement source, ReadOnlyMemory<byte> data, bool png);

    PdfFormContentBuilder BeginForm(PdfRectangle box);

    /// <param name="message">What was dropped or approximated.</param>
    /// <param name="perDocument">True for a note that is the same everywhere, so a layout reports it once for the whole document.</param>
    void Note(string message, bool perDocument = false);
}

/// <summary>The host for a plain content builder: its document takes the fonts, images and forms.</summary>
internal sealed class BuilderSvgHost : ISvgHost
{
    private static readonly ConditionalWeakTable<PdfDocument, State> s_states = new();
    private readonly PdfDocument _document;

    private sealed class State
    {
        public TextRenderer? Renderer;
        public readonly Dictionary<SvgElement, PdfCreatedImage> Images = [];
    }

    public BuilderSvgHost(PdfContentBuilder builder, TextFontRegistry? fonts)
    {
        _document = builder.Document;
        Fonts = fonts ?? TextFontRegistry.Shared;
        State s = s_states.GetOrCreateValue(_document);
        Renderer = s.Renderer ??= new TextRenderer(_document);
    }

    public TextFontRegistry Fonts { get; }

    public TextRenderer Renderer { get; }

    public PdfCreatedImage AddImage(SvgElement source, ReadOnlyMemory<byte> data, bool png)
    {
        State s = s_states.GetOrCreateValue(_document);
        if (!s.Images.TryGetValue(source, out PdfCreatedImage? image))
        {
            image = png ? _document.AddPngImage(data) : _document.AddJpegImage(data);
            s.Images[source] = image;
        }

        return image;
    }

    public PdfFormContentBuilder BeginForm(PdfRectangle box) => _document.BeginForm(box);

    // A plain builder has no diagnostics list; the notes come back from Draw.
    public void Note(string message, bool perDocument = false)
    {
    }
}

/// <summary>The host while a layout is drawn: pictures and fonts go to the layout's target and notes to its diagnostics.</summary>
internal sealed class LayoutSvgHost : ISvgHost
{
    private static readonly ConditionalWeakTable<object, Dictionary<SvgElement, PdfCreatedImage>> s_images = new();
    private readonly LayoutContext _ctx;
    private readonly ParagraphTextEngine _engine;

    public LayoutSvgHost(LayoutContext ctx)
    {
        _ctx = ctx;
        _engine = ctx.Text as ParagraphTextEngine ?? throw new InvalidOperationException("The layout's text engine cannot draw SVG text.");
    }

    public TextFontRegistry Fonts => _engine.Fonts;

    public TextRenderer Renderer => _engine.Renderer;

    public PdfCreatedImage AddImage(SvgElement source, ReadOnlyMemory<byte> data, bool png)
    {
        Dictionary<SvgElement, PdfCreatedImage> images = s_images.GetOrCreateValue(_ctx.Target);
        if (!images.TryGetValue(source, out PdfCreatedImage? image))
        {
            image = png ? _ctx.Target.AddPngImage(data) : _ctx.Target.AddJpegImage(data);
            images[source] = image;
        }

        return image;
    }

    public PdfFormContentBuilder BeginForm(PdfRectangle box) => _ctx.Target.BeginForm(box);

    public void Note(string message, bool perDocument = false) => _ctx.NoteOnce(perDocument ? message : (_ctx.PathText, message), message);
}

/// <summary>Gives a canvas the SVG host of the layout it is drawn in.</summary>
internal interface ISvgHostFactory
{
    ISvgHost Create();
}

internal sealed class LayoutHostFactory(LayoutContext ctx) : ISvgHostFactory
{
    public ISvgHost Create() => new LayoutSvgHost(ctx);
}
