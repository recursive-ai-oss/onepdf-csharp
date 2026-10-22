namespace OnePdf;

/// <summary>
/// An immutable snapshot of the graphics state handed to a device with each call. Colours are in final spaces;
/// <see cref="Ctm"/> maps the call's user-space coordinates all the way to the output space chosen by
/// <see cref="PdfInterpretOptions"/> (y grows downward in the output).
/// </summary>
/// <param name="Ctm">User space to output space.</param>
/// <param name="FillColor">The non-stroking colour.</param>
/// <param name="StrokeColor">The stroking colour.</param>
/// <param name="LineWidth">Line width in user-space units (0 means the thinnest line the device can draw).</param>
/// <param name="LineCap">0 butt, 1 round, 2 projecting square.</param>
/// <param name="LineJoin">0 miter, 1 round, 2 bevel.</param>
/// <param name="MiterLimit">Miter limit.</param>
/// <param name="Dash">Dash lengths in user-space units; empty for a solid line.</param>
/// <param name="DashPhase">Dash phase.</param>
/// <param name="Flatness">Flatness tolerance (advisory).</param>
/// <param name="RenderingIntent">The rendering intent name, for example <c>RelativeColorimetric</c>.</param>
/// <param name="FillAlpha">Constant opacity for non-stroking operations, 0 to 1.</param>
/// <param name="StrokeAlpha">Constant opacity for stroking operations, 0 to 1.</param>
/// <param name="BlendMode">The first blend mode name the file gave that is a standard one, or <c>Normal</c>.</param>
/// <param name="SoftMaskId">0 for none, otherwise the id of a soft mask announced earlier by <see cref="IPdfDevice.BeginSoftMask"/>.</param>
/// <param name="AlphaIsShape">The ExtGState <c>/AIS</c> flag.</param>
/// <param name="FillOverprint">The <c>/op</c> (non-stroking overprint) flag.</param>
/// <param name="StrokeOverprint">The <c>/OP</c> (stroking overprint) flag.</param>
/// <param name="OverprintMode">The <c>/OPM</c> value, 0 or 1.</param>
public readonly record struct PdfGraphicsState(
    PdfMatrix Ctm,
    PdfColor FillColor,
    PdfColor StrokeColor,
    double LineWidth,
    int LineCap,
    int LineJoin,
    double MiterLimit,
    ReadOnlyMemory<double> Dash,
    double DashPhase,
    double Flatness,
    string RenderingIntent,
    double FillAlpha,
    double StrokeAlpha,
    string BlendMode,
    int SoftMaskId,
    bool AlphaIsShape,
    bool FillOverprint,
    bool StrokeOverprint,
    int OverprintMode);

/// <summary>Which page box selects the output area.</summary>
public enum PdfPageBox
{
    /// <summary>The crop box, or the media box when there is none. The default.</summary>
    CropBox,

    /// <summary>The media box.</summary>
    MediaBox,

    /// <summary>The bleed box, falling back to the crop box.</summary>
    BleedBox,

    /// <summary>The trim box, falling back to the crop box.</summary>
    TrimBox,

    /// <summary>The art box, falling back to the crop box.</summary>
    ArtBox,
}

/// <summary>Which annotations are drawn.</summary>
public enum PdfAnnotationMode
{
    /// <summary>Annotations visible on screen: not hidden, not NoView. The default.</summary>
    View,

    /// <summary>Annotations that print: the Print flag set and not hidden.</summary>
    Print,

    /// <summary>No annotations.</summary>
    None,
}

/// <summary>Describes the page and the output space at the start of an interpretation.</summary>
/// <param name="Box">The selected box in default user space, normalized, clipped to the media box.</param>
/// <param name="BoxName">Which box was used (after fallback).</param>
/// <param name="Rotation">The applied clockwise rotation: 0, 90, 180 or 270.</param>
/// <param name="UserUnit">The applied user unit (1 when not applied).</param>
/// <param name="Scale">The total scale from default user space units to output units (the requested scale times the user unit).</param>
/// <param name="Width">Output width.</param>
/// <param name="Height">Output height.</param>
/// <param name="BaseMatrix">Default user space to output space. Content starts with this as its CTM.</param>
/// <param name="Page">The page dictionary.</param>
public readonly record struct PdfPageInfo(
    PdfBounds Box,
    PdfPageBox BoxName,
    int Rotation,
    double UserUnit,
    double Scale,
    double Width,
    double Height,
    PdfMatrix BaseMatrix,
    PdfDictionary Page);

/// <summary>Text rendering modes (ISO 32000-2 9.3.6).</summary>
public enum PdfTextRenderMode
{
    /// <summary>Fill.</summary>
    Fill = 0,

    /// <summary>Stroke.</summary>
    Stroke = 1,

    /// <summary>Fill then stroke.</summary>
    FillStroke = 2,

    /// <summary>Neither: invisible.</summary>
    Invisible = 3,

    /// <summary>Fill and add to the text clip.</summary>
    FillClip = 4,

    /// <summary>Stroke and add to the text clip.</summary>
    StrokeClip = 5,

    /// <summary>Fill, stroke and add to the text clip.</summary>
    FillStrokeClip = 6,

    /// <summary>Add to the text clip only.</summary>
    Clip = 7,
}

/// <summary>
/// One glyph of a run. Positions come from the font's width information; outline, glyph id and Unicode are filled in
/// once a font program is attached and are empty or negative until then.
/// </summary>
/// <param name="Code">The character code (one to four bytes, big-endian).</param>
/// <param name="ByteLength">How many bytes of the string the code took.</param>
/// <param name="OffsetX">Distance of the glyph origin from the run start along the writing direction, in text-space units (character and word spacing and horizontal scaling included).</param>
/// <param name="OffsetY">Vertical distance of the glyph origin from the run start, in text-space units (non-zero in vertical writing mode).</param>
/// <param name="Width">The glyph's advance in em units (1 = the font size), as the font declares it.</param>
/// <param name="GlyphId">The glyph id in the font program, or -1 when unknown.</param>
/// <param name="Unicode">The Unicode text the glyph stands for, or null when unknown.</param>
/// <param name="Outline">The glyph outline in glyph space scaled to one em, or an empty path.</param>
public readonly record struct PdfGlyph(int Code, int ByteLength, double OffsetX, double OffsetY, double Width, int GlyphId = -1, string? Unicode = null, PdfPath Outline = default);

/// <summary>
/// One shown string (<c>Tj</c>, <c>'</c>, <c>"</c> or one string element of <c>TJ</c>). The glyph at index <c>i</c> has the
/// rendering matrix <c>[Tfs·Th 0 0 Tfs 0 Trise] × T(OffsetX, OffsetY) × TextMatrix × Ctm</c> (ISO 32000-2 9.4.4).
/// Memory is valid only for the duration of the call.
/// </summary>
/// <param name="FontName">The key in the Font resource dictionary, without the slash (may be empty).</param>
/// <param name="Font">The font dictionary, or null when the name did not resolve.</param>
/// <param name="FontSize">The font size operand of <c>Tf</c>.</param>
/// <param name="RenderMode">The text rendering mode.</param>
/// <param name="CharSpacing">Character spacing (<c>Tc</c>), unscaled text-space units.</param>
/// <param name="WordSpacing">Word spacing (<c>Tw</c>).</param>
/// <param name="HorizontalScale">Horizontal scaling as a factor (<c>Tz</c> / 100).</param>
/// <param name="Rise">Text rise (<c>Ts</c>).</param>
/// <param name="TextMatrix">The text matrix at the start of the string.</param>
/// <param name="IsVertical">True for vertical writing mode.</param>
/// <param name="Codes">The string's raw bytes.</param>
/// <param name="Glyphs">One entry per character code.</param>
/// <param name="AdvanceX">The total displacement of the text matrix by the string, x.</param>
/// <param name="AdvanceY">The total displacement of the text matrix by the string, y.</param>
public readonly record struct PdfGlyphRun(
    string FontName,
    PdfDictionary? Font,
    double FontSize,
    PdfTextRenderMode RenderMode,
    double CharSpacing,
    double WordSpacing,
    double HorizontalScale,
    double Rise,
    PdfMatrix TextMatrix,
    bool IsVertical,
    ReadOnlyMemory<byte> Codes,
    ReadOnlyMemory<PdfGlyph> Glyphs,
    double AdvanceX,
    double AdvanceY)
{
    /// <summary>
    /// The matrix mapping glyph space (one em = 1 unit, origin at the glyph origin) of glyph <paramref name="index"/> to
    /// user space; multiply by the call's CTM to reach the output. Does not include the font's own glyph-space scale.
    /// </summary>
    public PdfMatrix GlyphMatrix(int index)
    {
        PdfGlyph g = Glyphs.Span[index];
        var scale = new PdfMatrix(FontSize * HorizontalScale, 0, 0, FontSize, 0, Rise);
        return scale.Then(PdfMatrix.Translation(g.OffsetX, g.OffsetY)).Then(TextMatrix);
    }
}

/// <summary>
/// An image the content drew, undecoded: its dictionary and encoded data. The image occupies the unit square of the
/// call's CTM. For inline images the dictionary keys and the colour-space and filter names are in their long forms.
/// </summary>
public readonly struct PdfImage
{
    internal PdfImage(PdfDictionary dictionary, PdfStream? stream, ReadOnlyMemory<byte> inlineData, string name)
    {
        Dictionary = dictionary;
        Stream = stream;
        InlineData = inlineData;
        Name = name;
    }

    /// <summary>The image dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The image XObject, or null for an inline image.</summary>
    public PdfStream? Stream { get; }

    /// <summary>The encoded bytes of an inline image (still filtered); empty for an XObject.</summary>
    public ReadOnlyMemory<byte> InlineData { get; }

    /// <summary>The XObject's resource name without the slash, or empty for an inline image.</summary>
    public string Name { get; }

    /// <summary>True for an inline image.</summary>
    public bool IsInline => Stream is null;

    /// <summary>
    /// The data as stored, still encoded (filters not applied): the inline bytes, or the XObject's raw bytes. Empty when
    /// the stream is external or damaged.
    /// </summary>
    public ReadOnlyMemory<byte> GetEncodedData() => Stream is null ? InlineData : Stream.GetRawBytes();
}

/// <summary>A transparency group about to be painted as a unit.</summary>
/// <param name="Bounds">The group's bounding box in the call's user space.</param>
/// <param name="Isolated">The group's <c>/I</c> flag.</param>
/// <param name="Knockout">The group's <c>/K</c> flag.</param>
/// <param name="ColorSpace">The group's colour space object (<c>/CS</c>), or null.</param>
public readonly record struct PdfGroupInfo(PdfBounds Bounds, bool Isolated, bool Knockout, PdfObject? ColorSpace);

/// <summary>A soft mask definition. Its content is delivered between <see cref="IPdfDevice.BeginSoftMask"/> and <see cref="IPdfDevice.EndSoftMask"/>.</summary>
/// <param name="Id">The id later states refer to through <see cref="PdfGraphicsState.SoftMaskId"/>.</param>
/// <param name="Luminosity">True for a luminosity mask, false for an alpha mask.</param>
/// <param name="Bounds">The mask group's bounding box in the call's user space.</param>
/// <param name="Backdrop">The <c>/BC</c> array of a luminosity mask, or null.</param>
/// <param name="Transfer">The <c>/TR</c> function (a function dictionary or stream), or null for identity.</param>
public readonly record struct PdfSoftMaskInfo(int Id, bool Luminosity, PdfBounds Bounds, PdfArray? Backdrop, PdfObject? Transfer);

/// <summary>An annotation whose appearance is about to be drawn.</summary>
/// <param name="Annotation">The annotation dictionary.</param>
/// <param name="Subtype">The <c>/Subtype</c> name, or empty.</param>
/// <param name="Rect">The annotation rectangle in default user space.</param>
/// <param name="Flags">The <c>/F</c> flags.</param>
public readonly record struct PdfAnnotationInfo(PdfDictionary Annotation, string Subtype, PdfBounds Rect, int Flags);
