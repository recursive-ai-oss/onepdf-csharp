namespace OnePdf;

/// <summary>
/// A colour for a form field's background, border or text: DeviceGray, DeviceRGB or DeviceCMYK components in 0..1
/// (ISO 32000-2 12.5.6.19 Table 192), or <see cref="Transparent"/> (no paint; valid for background and border only).
/// </summary>
public readonly struct PdfFieldColor : IEquatable<PdfFieldColor>
{
    private readonly double[]? _c;

    private PdfFieldColor(double[] components) => _c = components;

    /// <summary>No paint: the background or border is left out (an empty colour array, Table 192).</summary>
    public static PdfFieldColor Transparent => new([]);

    /// <summary>A gray level.</summary>
    /// <param name="gray">0 (black) to 1 (white).</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a number between 0 and 1.</exception>
    public static PdfFieldColor Gray(double gray) => new([Check(gray, nameof(gray))]);

    /// <summary>A DeviceRGB colour.</summary>
    /// <param name="r">Red, 0 to 1.</param>
    /// <param name="g">Green, 0 to 1.</param>
    /// <param name="b">Blue, 0 to 1.</param>
    /// <exception cref="ArgumentOutOfRangeException">A component is not a number between 0 and 1.</exception>
    public static PdfFieldColor Rgb(double r, double g, double b) => new([Check(r, nameof(r)), Check(g, nameof(g)), Check(b, nameof(b))]);

    /// <summary>A DeviceCMYK colour.</summary>
    /// <param name="c">Cyan, 0 to 1.</param>
    /// <param name="m">Magenta, 0 to 1.</param>
    /// <param name="y">Yellow, 0 to 1.</param>
    /// <param name="k">Black, 0 to 1.</param>
    /// <exception cref="ArgumentOutOfRangeException">A component is not a number between 0 and 1.</exception>
    public static PdfFieldColor Cmyk(double c, double m, double y, double k) =>
        new([Check(c, nameof(c)), Check(m, nameof(m)), Check(y, nameof(y)), Check(k, nameof(k))]);

    /// <summary>True for <see cref="Transparent"/> (and for a default value, which carries no colour).</summary>
    public bool IsTransparent => _c is null || _c.Length == 0;

    /// <summary>A copy of the components: none (transparent), 1 (gray), 3 (RGB) or 4 (CMYK).</summary>
    public double[] Components => _c is null ? [] : (double[])_c.Clone();

    private static double Check(double v, string name) =>
        double.IsFinite(v) && v is >= 0 and <= 1 ? v : throw new ArgumentOutOfRangeException(name, v, "A colour component must be a number between 0 and 1.");

    /// <inheritdoc/>
    public bool Equals(PdfFieldColor other) => Components.AsSpan().SequenceEqual(other.Components);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PdfFieldColor o && Equals(o);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (double d in Components)
        {
            h.Add(d);
        }

        return h.ToHashCode();
    }

    /// <summary>Equality of the components.</summary>
    public static bool operator ==(PdfFieldColor a, PdfFieldColor b) => a.Equals(b);

    /// <summary>Inequality of the components.</summary>
    public static bool operator !=(PdfFieldColor a, PdfFieldColor b) => !a.Equals(b);
}

/// <summary>
/// What <see cref="PdfFormField.SetAppearance"/> changes. A null property leaves that setting as it is; at least one must be set.
/// </summary>
public sealed class PdfFieldAppearance
{
    /// <summary>The background colour (<c>/MK /BG</c>). <see cref="PdfFieldColor.Transparent"/> removes the background.</summary>
    public PdfFieldColor? Background { get; init; }

    /// <summary>The border colour (<c>/MK /BC</c>). <see cref="PdfFieldColor.Transparent"/> removes the border (no border is drawn without a border colour).</summary>
    public PdfFieldColor? BorderColor { get; init; }

    /// <summary>The font size in points written into <c>/DA</c>; 0 means automatic size. Must be between 0 and 1000.</summary>
    public double? FontSize { get; init; }

    /// <summary>The text colour written into <c>/DA</c> (<c>g</c>, <c>rg</c> or <c>k</c>). A transparent text colour is not allowed.</summary>
    public PdfFieldColor? TextColor { get; init; }
}
