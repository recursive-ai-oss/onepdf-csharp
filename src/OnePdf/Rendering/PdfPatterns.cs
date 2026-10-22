namespace OnePdf;

/// <summary>
/// A pattern used as a colour (ISO 32000-2 8.7): a tiling pattern or a shading pattern. Delivered inside
/// <see cref="PdfColor.Pattern"/> of the fill or stroke colour of a path, text or other paint call. The same instance comes
/// back for every use of one pattern in one interpretation.
/// </summary>
public abstract class PdfPattern
{
    private protected PdfPattern(PdfObject source, string name, PdfMatrix matrix)
    {
        Source = source;
        Name = name;
        Matrix = matrix;
    }

    /// <summary>The pattern dictionary (a stream's dictionary for tiling patterns).</summary>
    public PdfDictionary Dictionary => Source is PdfStream s ? s.Dictionary : (PdfDictionary)Source;

    /// <summary>The pattern object as found in the resources: a stream (tiling) or dictionary (shading).</summary>
    public PdfObject Source { get; }

    /// <summary>The name in the <c>/Pattern</c> resource dictionary, without the slash.</summary>
    public string Name { get; }

    /// <summary>
    /// Maps pattern space to the output space. This is the pattern's <c>/Matrix</c> followed by the base transform of the
    /// content stream that selected it (page, form or annotation appearance), not the current CTM at the time of painting
    /// (ISO 32000-2 8.7.3.1): a pattern stays fixed to its parent stream while the painted shape moves.
    /// </summary>
    public PdfMatrix Matrix { get; }
}

/// <summary>A shading pattern (<c>PatternType 2</c>): a shading that paints the shape it fills or strokes.</summary>
public sealed class PdfShadingPattern : PdfPattern
{
    internal PdfShadingPattern(PdfObject source, string name, PdfMatrix matrix, PdfShading shading)
        : base(source, name, matrix)
    {
        Shading = shading;
    }

    /// <summary>
    /// The shading, in pattern space (apply <see cref="PdfPattern.Matrix"/>). Its <see cref="PdfShading.BBox"/> and
    /// <see cref="PdfShading.TryGetBackground"/> apply: outside the shading's own area the background shows, and a pattern with no
    /// background leaves the shape unpainted there. The pattern's <c>/ExtGState</c> entry is not applied.
    /// </summary>
    public PdfShading Shading { get; }
}

/// <summary>A tiling pattern (<c>PatternType 1</c>): a cell repeated on a grid.</summary>
/// <remarks>
/// The cell is interpreted once into <see cref="Cell"/>, in pattern space, clipped to <see cref="BBox"/>. The cell sits at
/// the origin of pattern space and repeats every <see cref="XStep"/> along x and <see cref="YStep"/> along y (either may
/// be negative; neither is zero). Tile positions are <c>(i · XStep, j · YStep)</c> for all integers i and j; the device
/// decides which ones intersect what it paints, so a huge step or box costs nothing here.
/// </remarks>
public sealed class PdfTilingPattern : PdfPattern
{
    internal PdfTilingPattern(PdfObject source, string name, PdfMatrix matrix, bool colored, int tilingType, PdfBounds bbox, double xStep, double yStep)
        : base(source, name, matrix)
    {
        IsColored = colored;
        TilingType = tilingType;
        BBox = bbox;
        XStep = xStep;
        YStep = yStep;
    }

    /// <summary>
    /// True for <c>PaintType 1</c>: the cell specifies its own colours. False for <c>PaintType 2</c>: the cell is a stencil
    /// painted in the tint carried by the colour that selected the pattern (its components in the pattern colour space's <see cref="PdfColorSpace.PatternBase"/>), and every
    /// colour in <see cref="Cell"/> is a placeholder to be replaced by it.
    /// </summary>
    public bool IsColored { get; }

    /// <summary><c>TilingType</c>: 1 constant spacing, 2 no distortion, 3 constant spacing and faster tiling. A device may treat all three alike.</summary>
    public int TilingType { get; }

    /// <summary>The cell's bounding box in pattern space.</summary>
    public PdfBounds BBox { get; }

    /// <summary>Horizontal distance between tiles in pattern space.</summary>
    public double XStep { get; }

    /// <summary>Vertical distance between tiles in pattern space.</summary>
    public double YStep { get; }

    /// <summary>
    /// The cell's content, recorded with pattern space as its user space (<see cref="PdfGraphicsState.Ctm"/> starts as the
    /// identity). It holds no page brackets. Null only when the pattern's content could not be run (a diagnostic says why)
    /// or when the pattern is nested too deeply or refers to itself; then nothing is painted for it.
    /// </summary>
    public PdfDisplayList? Cell { get; internal set; }

    internal bool CellBuilt { get; set; }
}
