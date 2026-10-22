namespace OnePdf;

/// <summary>The final colour spaces a device sees. Indexed, Separation and DeviceN are resolved to one of these.</summary>
public enum PdfColorSpaceKind
{
    /// <summary>One component, 0 = black, 1 = white.</summary>
    DeviceGray,

    /// <summary>Three components, red, green, blue.</summary>
    DeviceRgb,

    /// <summary>Four components, cyan, magenta, yellow, black.</summary>
    DeviceCmyk,

    /// <summary>CIE L*a*b*: L in 0..100, a and b in the space's range.</summary>
    Lab,

    /// <summary>An ICC profile; the component count is the profile's.</summary>
    IccBased,

    /// <summary>The pattern space: the colour names a pattern, with a tint for uncoloured patterns.</summary>
    Pattern,
}

/// <summary>
/// A final colour space. Instances are shared: the same document colour space yields the same instance within one
/// interpretation, so reference equality is meaningful.
/// </summary>
public sealed class PdfColorSpace
{
    internal PdfColorSpace(PdfColorSpaceKind kind, int components)
    {
        Kind = kind;
        ComponentCount = components;
    }

    /// <summary>DeviceGray.</summary>
    public static PdfColorSpace Gray { get; } = new(PdfColorSpaceKind.DeviceGray, 1);

    /// <summary>DeviceRGB.</summary>
    public static PdfColorSpace Rgb { get; } = new(PdfColorSpaceKind.DeviceRgb, 3);

    /// <summary>DeviceCMYK.</summary>
    public static PdfColorSpace Cmyk { get; } = new(PdfColorSpaceKind.DeviceCmyk, 4);

    /// <summary>The kind.</summary>
    public PdfColorSpaceKind Kind { get; }

    /// <summary>Number of components in a colour of this space (for Pattern, those of the base space of an uncoloured pattern, else 0).</summary>
    public int ComponentCount { get; }

    /// <summary>For <see cref="PdfColorSpaceKind.IccBased"/>: the profile bytes as stored, decoded from their filters. Empty otherwise.</summary>
    public ReadOnlyMemory<byte> IccProfile { get; internal set; }

    /// <summary>For <see cref="PdfColorSpaceKind.IccBased"/>: the device space to use when the profile is unusable, chosen by component count.</summary>
    public PdfColorSpace? IccFallback { get; internal set; }

    /// <summary>For <see cref="PdfColorSpaceKind.Lab"/>: the white point X, Y, Z.</summary>
    public ReadOnlyMemory<double> WhitePoint { get; internal set; }

    /// <summary>For <see cref="PdfColorSpaceKind.Lab"/>: the range amin, amax, bmin, bmax. For ICC: the per-component ranges.</summary>
    public ReadOnlyMemory<double> Range { get; internal set; }

    /// <summary>For <see cref="PdfColorSpaceKind.Pattern"/>: the space of the tint of an uncoloured pattern, or null.</summary>
    public PdfColorSpace? PatternBase { get; internal set; }

    /// <summary>The name used in text snapshots.</summary>
    public string Name => Kind switch
    {
        PdfColorSpaceKind.DeviceGray => "gray",
        PdfColorSpaceKind.DeviceRgb => "rgb",
        PdfColorSpaceKind.DeviceCmyk => "cmyk",
        PdfColorSpaceKind.Lab => "lab",
        PdfColorSpaceKind.IccBased => "icc" + ComponentCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => "pattern",
    };
}

/// <summary>
/// A colour in a final space: up to four components, stored inline. A pattern colour carries the pattern object and its
/// resource name, and the tint components for an uncoloured pattern.
/// </summary>
/// <param name="Space">The colour space.</param>
/// <param name="C0">First component.</param>
/// <param name="C1">Second component.</param>
/// <param name="C2">Third component.</param>
/// <param name="C3">Fourth component.</param>
/// <param name="Pattern">For pattern colours: the pattern dictionary or stream, unevaluated.</param>
/// <param name="PatternName">For pattern colours: the name in the resource dictionary.</param>
public readonly record struct PdfColor(PdfColorSpace Space, double C0, double C1, double C2, double C3, PdfObject? Pattern = null, string? PatternName = null)
{
    /// <summary>Opaque black in DeviceGray, the initial colour.</summary>
    public static PdfColor Black => new(PdfColorSpace.Gray, 0, 0, 0, 0);

    /// <summary>Component <paramref name="index"/> (0 to 3).</summary>
    public double this[int index] => index switch { 0 => C0, 1 => C1, 2 => C2, 3 => C3, _ => 0 };

    /// <summary>Number of meaningful components.</summary>
    public int ComponentCount => Space.Kind == PdfColorSpaceKind.Pattern ? Space.ComponentCount : Space.ComponentCount;
}
