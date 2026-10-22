using System;

namespace OnePdf;

/// <summary>A tint transform of a Separation or DeviceN colour space: input tints to alternate-space components.</summary>
internal interface IColorTransform
{
    /// <summary>Number of input tints.</summary>
    int InputCount { get; }

    /// <summary>Number of components produced (the alternate space's).</summary>
    int OutputCount { get; }

    /// <summary>Evaluates the transform. <paramref name="input"/> is clipped to 0..1 by the caller.</summary>
    void Evaluate(ReadOnlySpan<double> input, Span<double> output);
}

/// <summary>Where the colour-space resolver gets tint transforms from (the function evaluators of ISO 32000-2 7.10).</summary>
internal static class ColorTransforms
{
    /// <summary>
    /// Builds a transform from a function object (dictionary or stream) with the given input and output counts, or returns null when
    /// the function is unusable. Unset until functions are wired: Separation and DeviceN then use an ink approximation.
    /// </summary>
    internal static Func<PdfDocument, PdfObject, int, int, IColorTransform?>? Factory { get; set; }
}
