namespace OnePdf;

/// <summary>
/// The size of a page box as a viewer or printer shows it: after <c>/Rotate</c> (width and height swap for 90 and 270) and
/// scaled by <c>/UserUnit</c> (ISO 32000-2 7.7.3.3, Table 31). Values are in points of 1/72 inch.
/// </summary>
/// <param name="Width">Width in points, in display orientation, with the user unit applied.</param>
/// <param name="Height">Height in points, in display orientation, with the user unit applied.</param>
public readonly record struct PdfPageSize(double Width, double Height)
{
    /// <summary>Width in inches.</summary>
    public double WidthInches => Width / 72.0;

    /// <summary>Height in inches.</summary>
    public double HeightInches => Height / 72.0;

    /// <summary>Width in millimetres.</summary>
    public double WidthMillimeters => Width * 25.4 / 72.0;

    /// <summary>Height in millimetres.</summary>
    public double HeightMillimeters => Height * 25.4 / 72.0;
}

/// <summary>Line style of a guide a viewer draws for a page box (ISO 32000-2 14.11.2.2, Table 394).</summary>
public enum PdfBoxLineStyle
{
    /// <summary>A solid line (<c>/S</c>). The default.</summary>
    Solid,

    /// <summary>A dashed line (<c>/D</c>) using <see cref="PdfBoxColorInfo.Dash"/>.</summary>
    Dashed,
}

/// <summary>
/// How a viewer should draw the guides for one page box: the entry for one box in the page's <c>/BoxColorInfo</c> dictionary
/// (ISO 32000-2 14.11.2.2, Tables 393 and 394). Every property has the specification's default.
/// </summary>
public sealed record PdfBoxColorInfo
{
    /// <summary>Red component, 0 to 1. Default 0.</summary>
    public double Red { get; init; }

    /// <summary>Green component, 0 to 1. Default 0.</summary>
    public double Green { get; init; }

    /// <summary>Blue component, 0 to 1. Default 0.</summary>
    public double Blue { get; init; }

    /// <summary>Line width in points; zero or more. Default 1.</summary>
    public double LineWidth { get; init; } = 1;

    /// <summary>Solid or dashed. Default solid.</summary>
    public PdfBoxLineStyle Style { get; init; } = PdfBoxLineStyle.Solid;

    /// <summary>The dash pattern in points, used when <see cref="Style"/> is dashed. Default <c>[3]</c>.</summary>
    public IReadOnlyList<double> Dash { get; init; } = [3];
}
