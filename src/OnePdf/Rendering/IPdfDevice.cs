namespace OnePdf;

/// <summary>
/// What an interpretation drives. OnePDF resolves everything specific to PDF (operators, resources, inheritance, optional
/// content, form nesting); the device sees paths, glyph runs, images and brackets. Calls come from one thread in content
/// order. Memory in the arguments (paths, glyph arrays, code bytes) is only valid during the call.
/// </summary>
/// <remarks>
/// Brackets (<c>PushClip</c>/<c>PopClip</c>, groups, soft masks, marked content, annotations) are always balanced, also
/// when the content is damaged or interpretation is cancelled. Content hidden by optional content is never delivered.
/// Derive from <see cref="PdfDevice"/> to override only the calls you need.
/// </remarks>
public interface IPdfDevice
{
    /// <summary>The page starts. Everything until <see cref="EndPage"/> lies inside it.</summary>
    void BeginPage(in PdfPageInfo page);

    /// <summary>The page is complete (also called after a limit stopped the page or the token was cancelled).</summary>
    void EndPage();

    /// <summary>Fills a path (open subpaths are closed implicitly) with <see cref="PdfGraphicsState.FillColor"/>.</summary>
    void FillPath(in PdfPath path, bool evenOdd, in PdfGraphicsState state);

    /// <summary>Strokes a path with the line parameters and <see cref="PdfGraphicsState.StrokeColor"/>.</summary>
    void StrokePath(in PdfPath path, in PdfGraphicsState state);

    /// <summary>Intersects the clip with a path. Undone by the matching <see cref="PopClip"/>.</summary>
    void PushClip(in PdfPath path, bool evenOdd, in PdfGraphicsState state);

    /// <summary>
    /// Intersects the clip with the union of the glyphs shown with a clipping render mode (4 to 7) since the last
    /// <c>BT</c>. Undone by the matching <see cref="PopClip"/>.
    /// </summary>
    void PushTextClip(in PdfGraphicsState state);

    /// <summary>Removes the most recent clip.</summary>
    void PopClip();

    /// <summary>Shows a string of text.</summary>
    void ShowGlyphRun(in PdfGlyphRun run, in PdfGraphicsState state);

    /// <summary>
    /// A Type 3 glyph procedure follows, up to the matching <see cref="EndType3Glyph"/>: the glyph's content as ordinary drawing
    /// calls. <see cref="PdfGraphicsState.Ctm"/> of <paramref name="state"/> already includes the font matrix, so the glyph
    /// coordinates in the content map straight to the output. Glyphs of a font in render mode 3 are not delivered, and nothing is
    /// delivered for a code the font has no procedure for. Glyphs whose procedure starts with <c>d1</c> declare that they carry
    /// shape only: colour operators in them are already skipped, so the content paints in the state's fill colour.
    /// The text itself (codes, Unicode, advances) was delivered by the <see cref="ShowGlyphRun"/> call before.
    /// </summary>
    void BeginType3Glyph(in PdfType3GlyphInfo glyph, in PdfGraphicsState state);

    /// <summary>Ends the Type 3 glyph.</summary>
    void EndType3Glyph();

    /// <summary>Draws an image into the unit square of <see cref="PdfGraphicsState.Ctm"/>. The image is passed with its dictionary and encoded data; <see cref="PdfImage.GetData"/> builds the finished image.</summary>
    void DrawImage(in PdfImage image, in PdfGraphicsState state);

    /// <summary>
    /// The <c>sh</c> operator: paints a shading over the whole current clip. The shading's target space is the user space of
    /// <see cref="PdfGraphicsState.Ctm"/>. Its <see cref="PdfShading.BBox"/> still limits the painted area; its background is
    /// ignored (ISO 32000-2 8.7.4.3). Fill alpha, blend mode and soft mask in the state apply.
    /// </summary>
    void PaintShading(PdfShading shading, in PdfGraphicsState state);

    /// <summary>Starts a transparency group; its content is composited as a unit with the state's opacity, blend mode and soft mask.</summary>
    void BeginGroup(in PdfGroupInfo group, in PdfGraphicsState state);

    /// <summary>Ends the group.</summary>
    void EndGroup();

    /// <summary>Starts the content of a soft mask definition; later states name it by id.</summary>
    void BeginSoftMask(in PdfSoftMaskInfo mask, in PdfGraphicsState state);

    /// <summary>Ends the soft mask definition.</summary>
    void EndSoftMask();

    /// <summary>A marked-content sequence starts (<c>BMC</c>, <c>BDC</c>). Properties are null for <c>BMC</c> and unresolved names.</summary>
    void BeginMarkedContent(string tag, PdfDictionary? properties);

    /// <summary>The marked-content sequence ends.</summary>
    void EndMarkedContent();

    /// <summary>The appearance of an annotation follows.</summary>
    void BeginAnnotation(in PdfAnnotationInfo annotation);

    /// <summary>The annotation's appearance is complete.</summary>
    void EndAnnotation();
}

/// <summary>A device whose calls all do nothing; override what you need.</summary>
public abstract class PdfDevice : IPdfDevice
{
    /// <inheritdoc/>
    public virtual void BeginPage(in PdfPageInfo page)
    {
    }

    /// <inheritdoc/>
    public virtual void EndPage()
    {
    }

    /// <inheritdoc/>
    public virtual void FillPath(in PdfPath path, bool evenOdd, in PdfGraphicsState state)
    {
    }

    /// <inheritdoc/>
    public virtual void StrokePath(in PdfPath path, in PdfGraphicsState state)
    {
    }

    /// <inheritdoc/>
    public virtual void PushClip(in PdfPath path, bool evenOdd, in PdfGraphicsState state)
    {
    }

    /// <inheritdoc/>
    public virtual void PushTextClip(in PdfGraphicsState state)
    {
    }

    /// <inheritdoc/>
    public virtual void PopClip()
    {
    }

    /// <inheritdoc/>
    public virtual void ShowGlyphRun(in PdfGlyphRun run, in PdfGraphicsState state)
    {
    }

    /// <inheritdoc/>
    public virtual void BeginType3Glyph(in PdfType3GlyphInfo glyph, in PdfGraphicsState state)
    {
    }

    /// <inheritdoc/>
    public virtual void EndType3Glyph()
    {
    }

    /// <inheritdoc/>
    public virtual void DrawImage(in PdfImage image, in PdfGraphicsState state)
    {
    }

    /// <inheritdoc/>
    public virtual void PaintShading(PdfShading shading, in PdfGraphicsState state)
    {
    }

    /// <inheritdoc/>
    public virtual void BeginGroup(in PdfGroupInfo group, in PdfGraphicsState state)
    {
    }

    /// <inheritdoc/>
    public virtual void EndGroup()
    {
    }

    /// <inheritdoc/>
    public virtual void BeginSoftMask(in PdfSoftMaskInfo mask, in PdfGraphicsState state)
    {
    }

    /// <inheritdoc/>
    public virtual void EndSoftMask()
    {
    }

    /// <inheritdoc/>
    public virtual void BeginMarkedContent(string tag, PdfDictionary? properties)
    {
    }

    /// <inheritdoc/>
    public virtual void EndMarkedContent()
    {
    }

    /// <inheritdoc/>
    public virtual void BeginAnnotation(in PdfAnnotationInfo annotation)
    {
    }

    /// <inheritdoc/>
    public virtual void EndAnnotation()
    {
    }
}

/// <summary>Options for <c>Interpret</c>. All members have defaults.</summary>
public sealed record PdfInterpretOptions
{
    /// <summary>The page box that defines the output area. Default <see cref="PdfPageBox.CropBox"/>.</summary>
    public PdfPageBox Box { get; init; } = PdfPageBox.CropBox;

    /// <summary>Output units per default user space unit (1/72 inch); ignored when <see cref="Dpi"/> is set. Default 1.</summary>
    public double Scale { get; init; } = 1;

    /// <summary>When positive, the output resolution in dots per inch (scale = dpi / 72). Default 0 (use <see cref="Scale"/>).</summary>
    public double Dpi { get; init; }

    /// <summary>Apply the page's <c>/Rotate</c>. Default true.</summary>
    public bool ApplyRotation { get; init; } = true;

    /// <summary>Apply the page's <c>/UserUnit</c>. Default true.</summary>
    public bool ApplyUserUnit { get; init; } = true;

    /// <summary>Which annotations to draw. Default <see cref="PdfAnnotationMode.View"/>.</summary>
    public PdfAnnotationMode Annotations { get; init; } = PdfAnnotationMode.View;

    /// <summary>
    /// Draw a synthesised appearance (see <see cref="PdfAnnotation.GenerateAppearance"/>) for an annotation that has no usable normal
    /// appearance stream, and for form widgets when the form sets <c>/NeedAppearances true</c>. Each use is recorded as an Info
    /// diagnostic. Set false to get the behaviour of version 1.0: such annotations draw nothing. Default true.
    /// </summary>
    public bool GenerateAppearances { get; init; } = true;

    /// <summary>
    /// Overrides the visibility of an optional content group: given the group dictionary, return true or false to force it,
    /// or null to use the document's default configuration. Default null.
    /// </summary>
    public Func<PdfDictionary, bool?>? OptionalContentOverride { get; init; }

    /// <summary>
    /// Whether glyph runs carry outlines and Type 3 glyph procedures are executed (<see cref="IPdfDevice.BeginType3Glyph"/>).
    /// Set false for a device that only needs the text, positions and advances: it skips outline conversion and glyph content.
    /// Default true.
    /// </summary>
    public bool GlyphOutlines { get; init; } = true;
}
