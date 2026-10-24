namespace OnePdf.Layout;

/// <summary>Whether content runs from left to right or right to left. Start and End alignments, insets and borders resolve against it.</summary>
public enum LayoutDirection
{
    /// <summary>Start is the left edge.</summary>
    LeftToRight = 0,

    /// <summary>Start is the right edge.</summary>
    RightToLeft,
}

/// <summary>Horizontal placement of a narrower element inside the space it is given.</summary>
public enum HorizontalAlignment
{
    /// <summary>The start edge (left in left-to-right content).</summary>
    Start = 0,

    /// <summary>Centred.</summary>
    Center,

    /// <summary>The end edge (right in left-to-right content).</summary>
    End,

    /// <summary>The left edge whatever the direction.</summary>
    Left,

    /// <summary>The right edge whatever the direction.</summary>
    Right,
}

/// <summary>Vertical placement of a shorter element inside the space it is given.</summary>
public enum VerticalAlignment
{
    /// <summary>The top edge.</summary>
    Top = 0,

    /// <summary>Centred.</summary>
    Middle,

    /// <summary>The bottom edge.</summary>
    Bottom,
}

/// <summary>How a border line is drawn.</summary>
public enum BorderStyle
{
    /// <summary>A continuous line.</summary>
    Solid = 0,

    /// <summary>Dashes three line widths long.</summary>
    Dashed,

    /// <summary>Round dots two line widths apart.</summary>
    Dotted,
}

/// <summary>How an image is fitted to the space it is given.</summary>
public enum ImageFit
{
    /// <summary>The width fills the offered width and the height follows the image's proportions. This is the default.</summary>
    FitWidth = 0,

    /// <summary>The height fills the offered height (the natural height when none is bounded) and the width follows; the width is reduced if it exceeds the offered width.</summary>
    FitHeight,

    /// <summary>The whole image is shown, proportions kept, inside the offered box, placed by the image's alignment.</summary>
    Contain,

    /// <summary>The image covers the offered box, proportions kept, and is clipped to it.</summary>
    Cover,

    /// <summary>The image is stretched to the offered box, proportions ignored.</summary>
    Stretch,
}

/// <summary>Page orientation applied to a <see cref="PageSize"/>.</summary>
public enum PageOrientation
{
    /// <summary>Height at least the width.</summary>
    Portrait = 0,

    /// <summary>Width at least the height.</summary>
    Landscape,
}

/// <summary>A colour in DeviceRGB, components 0..1.</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
public readonly record struct LayoutColor(double R, double G, double B)
{
    /// <summary>Black.</summary>
    public static LayoutColor Black => new(0, 0, 0);

    /// <summary>White.</summary>
    public static LayoutColor White => new(1, 1, 1);

    /// <summary>A colour from 8-bit components.</summary>
    public static LayoutColor FromBytes(byte r, byte g, byte b) => new(r / 255.0, g / 255.0, b / 255.0);
}

/// <summary>A border line on one side of a box. A width of zero (the default) draws nothing.</summary>
/// <param name="Width">Line width in points.</param>
/// <param name="Color">Line colour.</param>
/// <param name="Style">Solid, dashed or dotted.</param>
public readonly record struct Border(double Width, LayoutColor Color, BorderStyle Style = BorderStyle.Solid);

/// <summary>Four physical edge sizes in points.</summary>
/// <param name="Top">Top edge.</param>
/// <param name="Right">Right edge.</param>
/// <param name="Bottom">Bottom edge.</param>
/// <param name="Left">Left edge.</param>
public readonly record struct Insets(double Top, double Right, double Bottom, double Left)
{
    /// <summary>The same size on all four edges.</summary>
    public static Insets All(double value) => new(value, value, value, value);

    /// <summary><paramref name="horizontal"/> on left and right, <paramref name="vertical"/> on top and bottom.</summary>
    public static Insets Symmetric(double horizontal, double vertical) => new(vertical, horizontal, vertical, horizontal);
}

/// <summary>A page size in points.</summary>
/// <param name="Width">Width in points.</param>
/// <param name="Height">Height in points.</param>
public readonly record struct PageSize(double Width, double Height)
{
    /// <summary>ISO A3, 297 x 420 mm.</summary>
    public static PageSize A3 => new(841.89, 1190.55);

    /// <summary>ISO A4, 210 x 297 mm.</summary>
    public static PageSize A4 => new(595.28, 841.89);

    /// <summary>ISO A5, 148 x 210 mm.</summary>
    public static PageSize A5 => new(419.53, 595.28);

    /// <summary>US Letter, 8.5 x 11 in.</summary>
    public static PageSize Letter => new(612, 792);

    /// <summary>US Legal, 8.5 x 14 in.</summary>
    public static PageSize Legal => new(612, 1008);

    /// <summary>The size turned to <paramref name="orientation"/>.</summary>
    public PageSize Oriented(PageOrientation orientation) =>
        (orientation == PageOrientation.Landscape) == (Width >= Height) ? this : new PageSize(Height, Width);
}

/// <summary>Conversions to points.</summary>
public static class Units
{
    /// <summary>Millimetres to points.</summary>
    public static double Mm(double value) => value * 72.0 / 25.4;

    /// <summary>Centimetres to points.</summary>
    public static double Cm(double value) => value * 72.0 / 2.54;

    /// <summary>Inches to points.</summary>
    public static double Inch(double value) => value * 72.0;
}

/// <summary>A note recorded while laying out: something was adjusted or could not be honoured.</summary>
/// <param name="Path">The element path where it happened.</param>
/// <param name="Message">What happened.</param>
public readonly record struct LayoutDiagnostic(string Path, string Message);
