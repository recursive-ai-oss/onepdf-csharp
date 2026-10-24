using System.Globalization;

namespace OnePdf;

/// <summary>The colour space of a <see cref="PdfGradientColor"/>.</summary>
public enum PdfGradientColorSpace
{
    /// <summary>One component, 0 black to 1 white.</summary>
    DeviceGray = 0,

    /// <summary>Three components: red, green, blue.</summary>
    DeviceRgb,

    /// <summary>Four components: cyan, magenta, yellow, black.</summary>
    DeviceCmyk,
}

/// <summary>A colour in DeviceGray, DeviceRGB or DeviceCMYK, components clamped to 0..1.</summary>
public readonly struct PdfGradientColor : IEquatable<PdfGradientColor>
{
    private PdfGradientColor(PdfGradientColorSpace space, double c0, double c1, double c2, double c3)
    {
        Space = space;
        C0 = Clamp(c0);
        C1 = Clamp(c1);
        C2 = Clamp(c2);
        C3 = Clamp(c3);
    }

    /// <summary>The colour space; the default value is DeviceGray black.</summary>
    public PdfGradientColorSpace Space { get; }

    /// <summary>First component (gray, red or cyan).</summary>
    public double C0 { get; }

    /// <summary>Second component (green or magenta); 0 for gray.</summary>
    public double C1 { get; }

    /// <summary>Third component (blue or yellow); 0 for gray.</summary>
    public double C2 { get; }

    /// <summary>Fourth component (black); 0 unless CMYK.</summary>
    public double C3 { get; }

    /// <summary>Number of components of <see cref="Space"/>: 1, 3 or 4.</summary>
    public int ComponentCount => Space switch { PdfGradientColorSpace.DeviceGray => 1, PdfGradientColorSpace.DeviceRgb => 3, _ => 4 };

    /// <summary>A gray level, 0 black .. 1 white. NaN is treated as 0.</summary>
    public static PdfGradientColor Gray(double gray) => new(PdfGradientColorSpace.DeviceGray, gray, 0, 0, 0);

    /// <summary>A DeviceRGB colour.</summary>
    public static PdfGradientColor Rgb(double red, double green, double blue) => new(PdfGradientColorSpace.DeviceRgb, red, green, blue, 0);

    /// <summary>A DeviceCMYK colour.</summary>
    public static PdfGradientColor Cmyk(double cyan, double magenta, double yellow, double black) => new(PdfGradientColorSpace.DeviceCmyk, cyan, magenta, yellow, black);

    /// <inheritdoc/>
    public bool Equals(PdfGradientColor other) => Space == other.Space && C0 == other.C0 && C1 == other.C1 && C2 == other.C2 && C3 == other.C3;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PdfGradientColor c && Equals(c);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Space, C0, C1, C2, C3);

    /// <summary>Equality.</summary>
    public static bool operator ==(PdfGradientColor left, PdfGradientColor right) => left.Equals(right);

    /// <summary>Inequality.</summary>
    public static bool operator !=(PdfGradientColor left, PdfGradientColor right) => !left.Equals(right);

    private static double Clamp(double v) => double.IsNaN(v) ? 0 : Math.Clamp(v, 0, 1);

    internal PdfArray ToArray()
    {
        var a = new PdfArray();
        a.Append(new PdfReal(C0));
        if (Space != PdfGradientColorSpace.DeviceGray)
        {
            a.Append(new PdfReal(C1));
            a.Append(new PdfReal(C2));
        }

        if (Space == PdfGradientColorSpace.DeviceCmyk)
        {
            a.Append(new PdfReal(C3));
        }

        return a;
    }
}

/// <summary>A colour at a position along a gradient.</summary>
/// <param name="Position">Where the colour is reached, 0 (start) to 1 (end).</param>
/// <param name="Color">The colour.</param>
public readonly record struct PdfGradientStop(double Position, PdfGradientColor Color);

/// <summary>
/// A smooth colour transition that <see cref="PdfContentBuilder.PaintGradient(PdfGradient)"/> paints, written as a PDF shading
/// (ISO 32000-2 8.7.4.5.3 axial, 8.7.4.5.4 radial) with an exponential-interpolation function, or a stitching function when there are
/// more than two distinct segments (7.10.3, 7.10.4). Create one with <see cref="Axial"/> or <see cref="Radial"/>.
/// </summary>
/// <remarks>
/// Stops must all be in one colour space and their positions must not decrease. If the first stop is after 0, or the last before 1, the
/// colour is held to the end of the range; two stops at the same position make a hard edge. Coordinates are in the user space current
/// where the gradient is painted. Instances are immutable and may be used on several builders and documents.
/// </remarks>
public sealed class PdfGradient
{
    private readonly PdfGradientStop[] _stops;

    private PdfGradient(bool radial, double x0, double y0, double r0, double x1, double y1, double r1, bool extendStart, bool extendEnd, PdfGradientStop[] stops)
    {
        IsRadial = radial;
        X0 = x0;
        Y0 = y0;
        R0 = r0;
        X1 = x1;
        Y1 = y1;
        R1 = r1;
        ExtendStart = extendStart;
        ExtendEnd = extendEnd;
        _stops = stops;
    }

    /// <summary>True for a radial gradient, false for an axial (linear) one.</summary>
    public bool IsRadial { get; }

    /// <summary>X of the start point (axial) or the start circle's centre (radial).</summary>
    public double X0 { get; }

    /// <summary>Y of the start point or the start circle's centre.</summary>
    public double Y0 { get; }

    /// <summary>Radius of the start circle; 0 for an axial gradient.</summary>
    public double R0 { get; }

    /// <summary>X of the end point or the end circle's centre.</summary>
    public double X1 { get; }

    /// <summary>Y of the end point or the end circle's centre.</summary>
    public double Y1 { get; }

    /// <summary>Radius of the end circle; 0 for an axial gradient.</summary>
    public double R1 { get; }

    /// <summary>Whether the start colour continues beyond the start point or circle.</summary>
    public bool ExtendStart { get; }

    /// <summary>Whether the end colour continues beyond the end point or circle.</summary>
    public bool ExtendEnd { get; }

    /// <summary>The colour stops, in order.</summary>
    public IReadOnlyList<PdfGradientStop> Stops => _stops;

    /// <summary>The colour space of the stops.</summary>
    public PdfGradientColorSpace ColorSpace => _stops[0].Color.Space;

    /// <summary>A gradient along the line from (<paramref name="x0"/>, <paramref name="y0"/>) to (<paramref name="x1"/>, <paramref name="y1"/>); colour is constant across it.</summary>
    /// <param name="x0">Start x.</param>
    /// <param name="y0">Start y.</param>
    /// <param name="x1">End x.</param>
    /// <param name="y1">End y.</param>
    /// <param name="stops">At least two stops, in one colour space, positions 0..1 and not decreasing.</param>
    /// <param name="extendStart">Continue the start colour before the start point. Default true.</param>
    /// <param name="extendEnd">Continue the end colour after the end point. Default true.</param>
    /// <exception cref="ArgumentException">Fewer than two stops, mixed colour spaces, positions outside 0..1 or decreasing, a non-finite number, or both points equal.</exception>
    public static PdfGradient Axial(double x0, double y0, double x1, double y1, ReadOnlySpan<PdfGradientStop> stops, bool extendStart = true, bool extendEnd = true)
    {
        CheckFinite(x0, y0, x1, y1);
        if (x0 == x1 && y0 == y1)
        {
            throw new ArgumentException("The start and end points of an axial gradient must differ.");
        }

        return new PdfGradient(false, x0, y0, 0, x1, y1, 0, extendStart, extendEnd, CheckStops(stops));
    }

    /// <summary>A gradient between two circles: from the circle at (<paramref name="x0"/>, <paramref name="y0"/>) of radius <paramref name="r0"/> to the one at (<paramref name="x1"/>, <paramref name="y1"/>) of radius <paramref name="r1"/>.</summary>
    /// <param name="x0">Start circle centre x.</param>
    /// <param name="y0">Start circle centre y.</param>
    /// <param name="r0">Start radius, 0 or more.</param>
    /// <param name="x1">End circle centre x.</param>
    /// <param name="y1">End circle centre y.</param>
    /// <param name="r1">End radius, 0 or more.</param>
    /// <param name="stops">At least two stops, in one colour space, positions 0..1 and not decreasing.</param>
    /// <param name="extendStart">Continue the start colour inside the start circle. Default true.</param>
    /// <param name="extendEnd">Continue the end colour outside the end circle. Default true.</param>
    /// <exception cref="ArgumentException">As for <see cref="Axial"/>, a negative radius, or both radii zero.</exception>
    public static PdfGradient Radial(double x0, double y0, double r0, double x1, double y1, double r1, ReadOnlySpan<PdfGradientStop> stops, bool extendStart = true, bool extendEnd = true)
    {
        CheckFinite(x0, y0, r0, x1, y1, r1);
        if (r0 < 0 || r1 < 0)
        {
            throw new ArgumentException("Radii must not be negative.");
        }

        if (r0 == 0 && r1 == 0)
        {
            throw new ArgumentException("A radial gradient needs a radius above zero at one end.");
        }

        return new PdfGradient(true, x0, y0, r0, x1, y1, r1, extendStart, extendEnd, CheckStops(stops));
    }

    private static void CheckFinite(params ReadOnlySpan<double> values)
    {
        foreach (double v in values)
        {
            if (!double.IsFinite(v))
            {
                throw new ArgumentException("Gradient coordinates must be finite numbers.");
            }
        }
    }

    private static PdfGradientStop[] CheckStops(ReadOnlySpan<PdfGradientStop> stops)
    {
        if (stops.Length < 2)
        {
            throw new ArgumentException("A gradient needs at least two colour stops.", nameof(stops));
        }

        if (stops.Length > 256)
        {
            throw new ArgumentException("A gradient takes at most 256 colour stops.", nameof(stops));
        }

        double prev = 0;
        for (int i = 0; i < stops.Length; i++)
        {
            double p = stops[i].Position;
            if (!(p >= 0 && p <= 1))
            {
                throw new ArgumentException("Stop positions must be between 0 and 1.", nameof(stops));
            }

            if (p < prev)
            {
                throw new ArgumentException("Stop positions must not decrease.", nameof(stops));
            }

            if (stops[i].Color.Space != stops[0].Color.Space)
            {
                throw new ArgumentException("All stops of a gradient must use the same colour space.", nameof(stops));
            }

            prev = p;
        }

        return stops.ToArray();
    }

    /// <summary>The shading dictionary (ShadingType 2 or 3) with its function.</summary>
    internal PdfDictionary ToShading()
    {
        var d = new PdfDictionary();
        d.SetEntry("ShadingType", new PdfInteger(IsRadial ? 3 : 2));
        d.SetEntry("ColorSpace", new PdfName(ColorSpace switch { PdfGradientColorSpace.DeviceGray => "DeviceGray", PdfGradientColorSpace.DeviceRgb => "DeviceRGB", _ => "DeviceCMYK" }));
        var coords = new PdfArray();
        coords.Append(new PdfReal(X0));
        coords.Append(new PdfReal(Y0));
        if (IsRadial)
        {
            coords.Append(new PdfReal(R0));
        }

        coords.Append(new PdfReal(X1));
        coords.Append(new PdfReal(Y1));
        if (IsRadial)
        {
            coords.Append(new PdfReal(R1));
        }

        d.SetEntry("Coords", coords);
        var extend = new PdfArray();
        extend.Append(new PdfBoolean(ExtendStart));
        extend.Append(new PdfBoolean(ExtendEnd));
        d.SetEntry("Extend", extend);
        d.SetEntry("Function", BuildFunction());
        return d;
    }

    // A single exponential function for one segment; a stitching function over the segments otherwise. Stops are padded to cover 0..1,
    // and a segment between stops at the same position is left out, which leaves a hard edge at that position.
    private PdfDictionary BuildFunction()
    {
        var list = new List<PdfGradientStop>(_stops.Length + 2);
        if (_stops[0].Position > 0)
        {
            list.Add(new PdfGradientStop(0, _stops[0].Color));
        }

        list.AddRange(_stops);
        if (_stops[^1].Position < 1)
        {
            list.Add(new PdfGradientStop(1, _stops[^1].Color));
        }

        var segments = new List<(double Lo, double Hi, PdfGradientColor From, PdfGradientColor To)>();
        for (int i = 0; i + 1 < list.Count; i++)
        {
            if (list[i + 1].Position > list[i].Position)
            {
                segments.Add((list[i].Position, list[i + 1].Position, list[i].Color, list[i + 1].Color));
            }
        }

        if (segments.Count == 1)
        {
            return Exponential(segments[0].From, segments[0].To);
        }

        var functions = new PdfArray();
        var bounds = new PdfArray();
        var encode = new PdfArray();
        for (int i = 0; i < segments.Count; i++)
        {
            functions.Append(Exponential(segments[i].From, segments[i].To));
            if (i > 0)
            {
                bounds.Append(new PdfReal(segments[i].Lo));
            }

            encode.Append(new PdfInteger(0));
            encode.Append(new PdfInteger(1));
        }

        var f = new PdfDictionary();
        f.SetEntry("FunctionType", new PdfInteger(3));
        f.SetEntry("Domain", Domain());
        f.SetEntry("Functions", functions);
        f.SetEntry("Bounds", bounds);
        f.SetEntry("Encode", encode);
        return f;
    }

    private static PdfArray Domain()
    {
        var a = new PdfArray();
        a.Append(new PdfInteger(0));
        a.Append(new PdfInteger(1));
        return a;
    }

    private static PdfDictionary Exponential(PdfGradientColor from, PdfGradientColor to)
    {
        var f = new PdfDictionary();
        f.SetEntry("FunctionType", new PdfInteger(2));
        f.SetEntry("Domain", Domain());
        f.SetEntry("C0", from.ToArray());
        f.SetEntry("C1", to.ToArray());
        f.SetEntry("N", new PdfInteger(1));
        return f;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{(IsRadial ? "Radial" : "Axial")} gradient, {_stops.Length} stops, {ColorSpace}");
}
