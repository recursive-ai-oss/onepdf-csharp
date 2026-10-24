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

    /// <summary>A grey: 0 is black, 1 is white. Values outside 0..1 are clamped.</summary>
    public static LayoutColor Grey(double level) => new(Math.Clamp(level, 0, 1), Math.Clamp(level, 0, 1), Math.Clamp(level, 0, 1));

    /// <summary>A colour from a web-style hex string: <c>#RRGGBB</c>, <c>RRGGBB</c>, <c>#RGB</c> or <c>RGB</c>.</summary>
    /// <exception cref="FormatException">The text is not one of those forms.</exception>
    public static LayoutColor FromHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        ReadOnlySpan<char> h = hex.AsSpan().Trim();
        if (h.Length > 0 && h[0] == '#')
        {
            h = h[1..];
        }

        if (h.Length == 3)
        {
            Span<char> full = stackalloc char[6] { h[0], h[0], h[1], h[1], h[2], h[2] };
            return FromHexDigits(full, hex);
        }

        return FromHexDigits(h, hex);
    }

    private static LayoutColor FromHexDigits(ReadOnlySpan<char> h, string original)
    {
        if (h.Length != 6 || !int.TryParse(h, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out int v))
        {
            throw new FormatException($"'{original}' is not a colour of the form #RRGGBB or #RGB.");
        }

        return FromBytes((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
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
    /// <summary>ISO A0, 841 x 1189 mm.</summary>
    public static PageSize A0 => new(2383.94, 3370.39);

    /// <summary>ISO A1, 594 x 841 mm.</summary>
    public static PageSize A1 => new(1683.78, 2383.94);

    /// <summary>ISO A2, 420 x 594 mm.</summary>
    public static PageSize A2 => new(1190.55, 1683.78);

    /// <summary>ISO A3, 297 x 420 mm.</summary>
    public static PageSize A3 => new(841.89, 1190.55);

    /// <summary>ISO A4, 210 x 297 mm.</summary>
    public static PageSize A4 => new(595.28, 841.89);

    /// <summary>ISO A5, 148 x 210 mm.</summary>
    public static PageSize A5 => new(419.53, 595.28);

    /// <summary>ISO A6, 105 x 148 mm.</summary>
    public static PageSize A6 => new(297.64, 419.53);

    /// <summary>ISO B4, 250 x 353 mm.</summary>
    public static PageSize B4 => new(708.66, 1000.63);

    /// <summary>ISO B5, 176 x 250 mm.</summary>
    public static PageSize B5 => new(498.90, 708.66);

    /// <summary>US Executive, 7.25 x 10.5 in.</summary>
    public static PageSize Executive => new(522, 756);

    /// <summary>US Tabloid, 11 x 17 in.</summary>
    public static PageSize Tabloid => new(792, 1224);

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

/// <summary>Reads like a unit: <c>10.Mm()</c>, <c>2.5.Cm()</c>, <c>1.Inch()</c>. Everything in the layout is measured in points; these convert.</summary>
public static class UnitExtensions
{
    /// <summary>Millimetres to points.</summary>
    public static double Mm(this double value) => Units.Mm(value);

    /// <summary>Millimetres to points.</summary>
    public static double Mm(this int value) => Units.Mm(value);

    /// <summary>Centimetres to points.</summary>
    public static double Cm(this double value) => Units.Cm(value);

    /// <summary>Centimetres to points.</summary>
    public static double Cm(this int value) => Units.Cm(value);

    /// <summary>Inches to points.</summary>
    public static double Inch(this double value) => Units.Inch(value);

    /// <summary>Inches to points.</summary>
    public static double Inch(this int value) => Units.Inch(value);
}

/// <summary>A few named colours. For others use <see cref="LayoutColor.FromHex(string)"/>, <see cref="LayoutColor.FromBytes(byte, byte, byte)"/> or <see cref="LayoutColor.Grey(double)"/>.</summary>
public static class Colors
{
    /// <summary>Black.</summary>
    public static LayoutColor Black => LayoutColor.Black;

    /// <summary>White.</summary>
    public static LayoutColor White => LayoutColor.White;

    /// <summary>Pure red, <c>#FF0000</c>.</summary>
    public static LayoutColor Red => new(1, 0, 0);

    /// <summary>Pure green, <c>#00A000</c>.</summary>
    public static LayoutColor Green => LayoutColor.FromBytes(0, 160, 0);

    /// <summary>Pure blue, <c>#0000FF</c>.</summary>
    public static LayoutColor Blue => new(0, 0, 1);

    /// <summary>Yellow, <c>#FFE000</c>.</summary>
    public static LayoutColor Yellow => LayoutColor.FromBytes(255, 224, 0);

    /// <summary>Orange, <c>#FF8000</c>.</summary>
    public static LayoutColor Orange => LayoutColor.FromBytes(255, 128, 0);

    /// <summary>Mid grey, <c>#808080</c>.</summary>
    public static LayoutColor Grey => LayoutColor.Grey(0.5);

    /// <summary>Light grey, <c>#D9D9D9</c>.</summary>
    public static LayoutColor LightGrey => LayoutColor.Grey(0.85);

    /// <summary>Dark grey, <c>#404040</c>.</summary>
    public static LayoutColor DarkGrey => LayoutColor.Grey(0.25);
}
