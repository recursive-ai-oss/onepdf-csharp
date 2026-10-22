namespace OnePdf;

/// <summary>
/// A colour in a final space: up to four components, stored inline. A pattern colour carries the resolved pattern and, for an
/// uncoloured pattern, the tint components.
/// </summary>
/// <param name="Space">The colour space.</param>
/// <param name="C0">First component.</param>
/// <param name="C1">Second component.</param>
/// <param name="C2">Third component.</param>
/// <param name="C3">Fourth component.</param>
/// <param name="Pattern">For pattern colours: the resolved pattern (tiling or shading). Null for other colours.</param>
public readonly record struct PdfColor(PdfColorSpace Space, double C0, double C1, double C2, double C3, PdfPattern? Pattern = null)
{
    /// <summary>Opaque black in DeviceGray, the initial colour.</summary>
    public static PdfColor Black => new(PdfColorSpace.Gray, 0, 0, 0, 0);

    /// <summary>Component <paramref name="index"/> (0 to 3).</summary>
    public double this[int index] => index switch { 0 => C0, 1 => C1, 2 => C2, 3 => C3, _ => 0 };

    /// <summary>Number of meaningful components.</summary>
    public int ComponentCount => Space.ComponentCount;
}
