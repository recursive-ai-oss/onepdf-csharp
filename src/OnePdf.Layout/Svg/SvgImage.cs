using System.Globalization;
using OnePdf.Layout.Svg;
using OnePdf.Layout.Svg.Dom;
using OnePdf.Layout.Svg.Xml;

namespace OnePdf.Layout;

/// <summary>
/// A parsed SVG document, ready to be placed in a layout (<c>Slot.Svg</c>) or drawn with a content builder
/// (<see cref="Draw"/>). Parsing is strict about the XML and lenient about SVG: a document that is not well-formed XML, whose root is not an
/// <c>svg</c> element or that exceeds a limit raises <see cref="PdfFormatException"/> or <see cref="PdfLimitException"/>; features that cannot
/// be drawn (scripts, animation, <c>foreignObject</c>, references to other files or addresses, most filters) are left out when drawing, each
/// with a note. Nothing is ever fetched, run or read from disk. An instance is immutable and may be drawn any number of times, from any thread.
/// </summary>
/// <remarks>
/// Natural size: the root's <c>width</c> and <c>height</c> in CSS pixels (96 per inch, so 0.75 point each), or the <c>viewBox</c>
/// size when they are missing or relative; a document with neither is 100 by 100 pixels.
/// </remarks>
public sealed class SvgImage
{
    private const double PointsPerPixel = 0.75;

    internal SvgImage(SvgDocument document, SvgLimits limits, double widthPx, double heightPx)
    {
        Document = document;
        Limits = limits;
        WidthPx = widthPx;
        HeightPx = heightPx;
        Title = TextOfChild(document.Root, "title");
        Description = TextOfChild(document.Root, "desc");
    }

    internal SvgDocument Document { get; }

    internal SvgLimits Limits { get; }

    internal double WidthPx { get; }

    internal double HeightPx { get; }

    /// <summary>Natural width in points (0.75 per CSS pixel).</summary>
    public double Width => WidthPx * PointsPerPixel;

    /// <summary>Natural height in points (0.75 per CSS pixel).</summary>
    public double Height => HeightPx * PointsPerPixel;

    /// <summary>The text of the root's first <c>title</c> child, trimmed, or null.</summary>
    public string? Title { get; }

    /// <summary>The text of the root's first <c>desc</c> child, trimmed, or null.</summary>
    public string? Description { get; }

    /// <summary>Parses an SVG file given as bytes (UTF-8, UTF-16 with a byte order mark, or the encoding the XML declaration names for Latin-1).</summary>
    /// <param name="data">The file.</param>
    /// <param name="limits">Bounds on the document; null takes <see cref="SvgLimits.Default"/>.</param>
    /// <exception cref="PdfFormatException">The document is not well-formed or not an SVG document.</exception>
    /// <exception cref="PdfLimitException">The document exceeds a limit.</exception>
    public static SvgImage Parse(ReadOnlySpan<byte> data, SvgLimits? limits = null)
    {
        limits ??= SvgLimits.Default;
        if (data.Length > limits.MaxDocumentBytes)
        {
            throw new PdfLimitException($"The SVG is larger than {limits.MaxDocumentBytes} bytes.");
        }

        return Build(XmlParser.Parse(data, limits), limits);
    }

    /// <summary>Parses an SVG document given as text.</summary>
    /// <param name="text">The document.</param>
    /// <param name="limits">Bounds on the document; null takes <see cref="SvgLimits.Default"/>.</param>
    /// <exception cref="PdfFormatException">The document is not well-formed or not an SVG document.</exception>
    /// <exception cref="PdfLimitException">The document exceeds a limit.</exception>
    public static SvgImage Parse(string text, SvgLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        limits ??= SvgLimits.Default;
        if (text.Length > limits.MaxDocumentBytes)
        {
            throw new PdfLimitException($"The SVG is larger than {limits.MaxDocumentBytes} characters.");
        }

        return Build(XmlParser.ParseText(text, limits), limits);
    }

    private static SvgImage Build(XmlDocument xml, SvgLimits limits)
    {
        SvgDocument doc = SvgDocument.Build(xml, limits);
        SvgElement root = doc.Root;
        (double? w, double? h) = SvgSize.Partial(root.Attr("width"), root.Attr("height"), root.Attr("viewBox"));
        double wp = w ?? 100;
        double hp = h ?? 100;
        if (!(wp > 0 && wp <= 1e6 && hp > 0 && hp <= 1e6))
        {
            throw new PdfLimitException("The SVG's natural size is outside 0 to 1,000,000 pixels.");
        }

        return new SvgImage(doc, limits, wp, hp);
    }

    /// <summary>
    /// Draws the picture into a box of a content builder, as vector content. The box is placed by <paramref name="options"/> (fit and
    /// alignment) and the picture is clipped to its own viewport. Text is shaped with the fonts of <paramref name="options"/> (the
    /// Liberation faces by default) and added to the builder's document, so it extracts as text.
    /// </summary>
    /// <param name="builder">The target page or form builder; the drawing is wrapped in a saved graphics state.</param>
    /// <param name="x">Left edge of the box in the builder's coordinates.</param>
    /// <param name="y">Bottom edge of the box in the builder's coordinates (y up).</param>
    /// <param name="width">Box width in points.</param>
    /// <param name="height">Box height in points.</param>
    /// <param name="options">Fit, alignment, fonts and cancellation; null for the defaults.</param>
    /// <returns>Notes on what was left out or approximated, each once.</returns>
    /// <exception cref="ArgumentException">The box is empty or not finite.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="PdfLimitException">The picture instantiates more elements than <see cref="SvgLimits.MaxInstances"/>.</exception>
    public IReadOnlyList<string> Draw(PdfContentBuilder builder, double x, double y, double width, double height, SvgDrawOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var host = new BuilderSvgHost(builder, options?.Fonts);
        return DrawWith(builder, host, x, y, width, height, options ?? new SvgDrawOptions());
    }

    /// <summary>Draws the picture so that its natural box fills the given box exactly (the caller has chosen the fit), optionally clipped.</summary>
    internal IReadOnlyList<string> PaintDirect(PdfContentBuilder builder, ISvgHost host, double x, double y, double width, double height, (double X, double Y, double W, double H)? clipBox, CancellationToken ct)
    {
        var painter = new SvgPainter(this, host, ct);
        painter.Render(builder, x, y, width, height, clipBox);
        return painter.Notes;
    }

    internal IReadOnlyList<string> DrawWith(PdfContentBuilder builder, ISvgHost host, double x, double y, double width, double height, SvgDrawOptions options)
    {
        if (!(width > 0 && height > 0) || !double.IsFinite(x + y + width + height))
        {
            throw new ArgumentException("The box must have a positive, finite size.");
        }

        double nw = Width;
        double nh = Height;
        double dw;
        double dh;
        double dx = 0;
        double dy = 0;
        bool clip = false;
        switch (options.Fit)
        {
            case ImageFit.Stretch:
                dw = width;
                dh = height;
                break;
            case ImageFit.FitWidth:
                dw = width;
                dh = width * nh / nw;
                break;
            case ImageFit.FitHeight:
                dh = height;
                dw = height * nw / nh;
                break;
            default:
            {
                double s = options.Fit == ImageFit.Cover ? Math.Max(width / nw, height / nh) : Math.Min(width / nw, height / nh);
                dw = nw * s;
                dh = nh * s;
                clip = options.Fit == ImageFit.Cover;
                dx = clip ? (width - dw) / 2 : Align(options.HorizontalAlignment, width - dw);
                dy = clip ? (height - dh) / 2 : AlignV(options.VerticalAlignment, height - dh);
                break;
            }
        }

        var painter = new SvgPainter(this, host, options.CancellationToken);
        painter.Render(builder, x + dx, y + (height - dy - dh), dw, dh, clip ? (x, y, width, height) : null);
        return painter.Notes;
    }

    private static double Align(HorizontalAlignment a, double free) => a switch
    {
        HorizontalAlignment.Left or HorizontalAlignment.Start => 0,
        HorizontalAlignment.Right or HorizontalAlignment.End => free,
        _ => free / 2,
    };

    // Offset from the top of the box.
    private static double AlignV(VerticalAlignment a, double free) => a switch
    {
        VerticalAlignment.Top => 0,
        VerticalAlignment.Bottom => free,
        _ => free / 2,
    };

    private static string? TextOfChild(SvgElement root, string name)
    {
        foreach (SvgElement c in root.Children)
        {
            if (c.Name != name)
            {
                continue;
            }

            var sb = new System.Text.StringBuilder();
            if (c.TextContent is { } parts)
            {
                foreach (TextNode n in parts)
                {
                    if (n.Text is { } t && sb.Length < 4096)
                    {
                        sb.Append(t);
                    }
                }
            }

            string s = string.Join(' ', sb.ToString().Split((char[])[' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));
            return s.Length > 0 ? s : null;
        }

        return null;
    }
}

/// <summary>How <see cref="SvgImage.Draw"/> places a picture in its box and which fonts it uses.</summary>
public sealed class SvgDrawOptions
{
    /// <summary>How the picture fills its box. Default <see cref="ImageFit.Contain"/>.</summary>
    public ImageFit Fit { get; set; } = ImageFit.Contain;

    /// <summary>For Contain: where the picture sits horizontally. Default centred.</summary>
    public HorizontalAlignment HorizontalAlignment { get; set; } = HorizontalAlignment.Center;

    /// <summary>For Contain: where the picture sits vertically. Default centred.</summary>
    public VerticalAlignment VerticalAlignment { get; set; } = VerticalAlignment.Middle;

    /// <summary>The fonts SVG text can use. Null, the default, is the shared registry of the Liberation faces and the standard fonts.</summary>
    public TextFontRegistry? Fonts { get; set; }

    /// <summary>Checked while drawing; cancelling raises <see cref="OperationCanceledException"/>.</summary>
    public CancellationToken CancellationToken { get; set; }
}
