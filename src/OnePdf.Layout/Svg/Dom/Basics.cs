namespace OnePdf.Layout.Svg.Dom;

/// <summary>An sRGB colour with straight alpha, one byte per channel.</summary>
internal readonly record struct Rgba32(byte R, byte G, byte B, byte A);

/// <summary>The CSS compositing blend modes (compositing level 1) that <c>mix-blend-mode</c> names.</summary>
internal enum BlendMode
{
    Normal, Multiply, Screen, Overlay, Darken, Lighten, ColorDodge, ColorBurn, HardLight, SoftLight, Difference, Exclusion, Hue, Saturation, Color, Luminosity,
}
