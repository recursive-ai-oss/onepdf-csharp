namespace OnePdf.Layout;

/// <summary>A colour at a position along a <see cref="LayoutGradient"/>.</summary>
/// <param name="Position">Where the colour is reached, 0 (start) to 1 (end).</param>
/// <param name="Color">The colour, in DeviceGray, DeviceRGB or DeviceCMYK.</param>
public readonly record struct LayoutGradientStop(double Position, PdfGradientColor Color)
{
    /// <summary>A stop with a DeviceRGB colour.</summary>
    public LayoutGradientStop(double position, LayoutColor color)
        : this(position, PdfGradientColor.Rgb(color.R, color.G, color.B))
    {
    }
}

/// <summary>Which edge or corner of a box a radial gradient's end circle reaches.</summary>
public enum RadialExtent
{
    /// <summary>The nearest side: the circle touches the closest edge.</summary>
    ClosestSide = 0,

    /// <summary>The farthest side.</summary>
    FarthestSide,

    /// <summary>The nearest corner.</summary>
    ClosestCorner,

    /// <summary>The farthest corner: the end colour is reached exactly in the corner farthest from the centre.</summary>
    FarthestCorner,
}

/// <summary>
/// A colour transition for a box background (<see cref="Slot.Background(LayoutGradient)"/>), written as an axial or radial PDF shading
/// (ISO 32000-2 8.7.4.5). It is described relative to the box, so one gradient suits boxes of any size. Instances are immutable.
/// </summary>
/// <remarks>
/// The gradient fills the box's own area (inside the margin, under the border and rounded corners). A box that splits over pages paints
/// the whole gradient again in each piece, scaled to that piece, so every piece shows the full transition. Stops must be in one colour
/// space, positions 0..1 and not decreasing; the colour before the first and after the last stop is held.
/// </remarks>
public sealed class LayoutGradient
{
    private readonly PdfGradientStop[] _stops;

    private LayoutGradient(bool radial, double angle, double cx, double cy, RadialExtent extent, PdfGradientStop[] stops)
    {
        IsRadial = radial;
        AngleDegrees = angle;
        CenterX = cx;
        CenterY = cy;
        Extent = extent;
        _stops = stops;
    }

    /// <summary>True for a radial gradient.</summary>
    public bool IsRadial { get; }

    /// <summary>Direction of a linear gradient in degrees, clockwise from "towards the top": 0 up, 90 right, 180 down, 270 left.</summary>
    public double AngleDegrees { get; }

    /// <summary>Centre of a radial gradient as a fraction of the box width (0 left, 1 right).</summary>
    public double CenterX { get; }

    /// <summary>Centre of a radial gradient as a fraction of the box height (0 top, 1 bottom).</summary>
    public double CenterY { get; }

    /// <summary>Where a radial gradient's end circle reaches.</summary>
    public RadialExtent Extent { get; }

    /// <summary>The colour stops.</summary>
    public IReadOnlyList<PdfGradientStop> Stops => _stops;

    /// <summary>
    /// A linear gradient. The gradient line runs through the box centre at the angle and is as long as needed for the first stop to be
    /// reached exactly in one corner and the last in the opposite corner.
    /// </summary>
    /// <param name="angleDegrees">Direction in degrees, clockwise from up: 0 goes bottom to top, 90 left to right, 180 top to bottom.</param>
    /// <param name="stops">At least two stops, one colour space, positions 0..1 and not decreasing.</param>
    /// <exception cref="ArgumentException">Invalid stops, or a non-finite angle.</exception>
    public static LayoutGradient Linear(double angleDegrees, params ReadOnlySpan<LayoutGradientStop> stops)
    {
        if (!double.IsFinite(angleDegrees))
        {
            throw new ArgumentException("The angle must be a finite number.", nameof(angleDegrees));
        }

        return new LayoutGradient(false, angleDegrees, 0.5, 0.5, RadialExtent.FarthestCorner, Check(stops));
    }

    /// <summary>A linear gradient from top to bottom (angle 180).</summary>
    public static LayoutGradient Vertical(params ReadOnlySpan<LayoutGradientStop> stops) => Linear(180, stops);

    /// <summary>A linear gradient from left to right (angle 90).</summary>
    public static LayoutGradient Horizontal(params ReadOnlySpan<LayoutGradientStop> stops) => Linear(90, stops);

    /// <summary>A linear gradient between two colours.</summary>
    public static LayoutGradient Linear(double angleDegrees, LayoutColor from, LayoutColor to) =>
        Linear(angleDegrees, [new LayoutGradientStop(0, from), new LayoutGradientStop(1, to)]);

    /// <summary>A radial gradient: circles around a centre, the first stop at the centre and the last at the radius <paramref name="extent"/> names.</summary>
    /// <param name="stops">At least two stops, one colour space, positions 0..1 and not decreasing.</param>
    /// <param name="centerX">Centre across the box, 0 (left) to 1 (right). Default 0.5.</param>
    /// <param name="centerY">Centre down the box, 0 (top) to 1 (bottom). Default 0.5.</param>
    /// <param name="extent">Where the last stop is reached. Default <see cref="RadialExtent.FarthestCorner"/>.</param>
    /// <exception cref="ArgumentException">Invalid stops, or a centre that is not finite.</exception>
    public static LayoutGradient Circle(ReadOnlySpan<LayoutGradientStop> stops, double centerX = 0.5, double centerY = 0.5, RadialExtent extent = RadialExtent.FarthestCorner)
    {
        if (!double.IsFinite(centerX) || !double.IsFinite(centerY))
        {
            throw new ArgumentException("The centre must be finite numbers.");
        }

        return new LayoutGradient(true, 0, centerX, centerY, extent, Check(stops));
    }

    private static PdfGradientStop[] Check(ReadOnlySpan<LayoutGradientStop> stops)
    {
        var converted = new PdfGradientStop[stops.Length];
        for (int i = 0; i < stops.Length; i++)
        {
            converted[i] = new PdfGradientStop(stops[i].Position, stops[i].Color);
        }

        // Validate now, so a bad gradient fails where it is written rather than at layout. The probe is discarded.
        _ = PdfGradient.Axial(0, 0, 1, 0, converted);
        return converted;
    }

    /// <summary>The PDF gradient for a box whose top-left corner is (<paramref name="x"/>, <paramref name="top"/>) in PDF space and whose size is given.</summary>
    internal PdfGradient ToPdf(double x, double top, double width, double height)
    {
        double cxBox = x + (width / 2);
        double cyBox = top - (height / 2);
        if (!IsRadial)
        {
            double rad = AngleDegrees * Math.PI / 180.0;
            double dx = Math.Sin(rad);
            double dy = Math.Cos(rad); // PDF y points up, so "up" (angle 0) is +y.
            double half = ((Math.Abs(width * dx) + Math.Abs(height * dy)) / 2);
            if (half < 1e-6)
            {
                half = 1e-6;
            }

            return PdfGradient.Axial(cxBox - (dx * half), cyBox - (dy * half), cxBox + (dx * half), cyBox + (dy * half), _stops);
        }

        double cx = x + (CenterX * width);
        double cy = top - (CenterY * height);
        double left = cx - x;
        double right = x + width - cx;
        double up = top - cy;
        double down = cy - (top - height);
        double nearX = Math.Min(Math.Abs(left), Math.Abs(right));
        double nearY = Math.Min(Math.Abs(up), Math.Abs(down));
        double farX = Math.Max(Math.Abs(left), Math.Abs(right));
        double farY = Math.Max(Math.Abs(up), Math.Abs(down));
        double r = Extent switch
        {
            RadialExtent.ClosestSide => Math.Min(nearX, nearY),
            RadialExtent.FarthestSide => Math.Max(farX, farY),
            RadialExtent.ClosestCorner => Math.Sqrt((nearX * nearX) + (nearY * nearY)),
            _ => Math.Sqrt((farX * farX) + (farY * farY)),
        };
        return PdfGradient.Radial(cx, cy, 0, cx, cy, Math.Max(r, 1e-6), _stops);
    }

    /// <inheritdoc/>
    public override string ToString() => IsRadial ? $"Radial gradient, {_stops.Length} stops" : $"Linear gradient at {AngleDegrees} degrees, {_stops.Length} stops";
}
