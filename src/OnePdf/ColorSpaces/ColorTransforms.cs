using System;
using OnePdf.Functions;

namespace OnePdf;

/// <summary>A tint transform of a Separation or DeviceN colour space: input tints to alternate-space components.</summary>
internal interface IColorTransform
{
    /// <summary>Number of input tints.</summary>
    int InputCount { get; }

    /// <summary>Number of components produced.</summary>
    int OutputCount { get; }

    /// <summary>Evaluates the transform. <paramref name="input"/> is clipped to 0..1 by the caller.</summary>
    void Evaluate(ReadOnlySpan<double> input, Span<double> output);
}

/// <summary>Tint transforms backed by PDF functions (ISO 32000-2 7.10).</summary>
internal sealed class FunctionTint : IColorTransform
{
    private readonly PdfFunction _function;

    private FunctionTint(PdfFunction function) => _function = function;

    public int InputCount => _function.InputCount;

    public int OutputCount => _function.OutputCount;

    public void Evaluate(ReadOnlySpan<double> input, Span<double> output) => _function.Evaluate(input, output);

    /// <summary>
    /// Builds the transform from the colour space's function object. Returns null with a diagnostic when the function is
    /// unusable, too large for the limits, or does not match the colorant and alternate-space component counts
    /// (a function with extra outputs is accepted; the surplus is ignored).
    /// </summary>
    public static FunctionTint? Create(PdfDocument doc, PdfObject function, int inputs, int alternateComponents, int objNum)
    {
        PdfFunction? f;
        string? problem;
        try
        {
            f = PdfFunction.Create(function, doc.Limits, out problem);
        }
        catch (PdfLimitException ex)
        {
            doc.ReportWarning("A tint transform exceeds the stream limits: " + ex.Message, objNum, -1);
            return null;
        }

        if (f is null)
        {
            doc.ReportWarning("A tint transform is unusable: " + problem, objNum, -1);
            return null;
        }

        if (problem is not null)
        {
            doc.ReportWarning("A tint transform was repaired: " + problem, objNum, -1);
        }

        if (f.InputCount != inputs || f.OutputCount < alternateComponents)
        {
            doc.ReportWarning($"A tint transform maps {f.InputCount} inputs to {f.OutputCount} outputs; {inputs} to {alternateComponents} are needed.", objNum, -1);
            return null;
        }

        return new FunctionTint(f);
    }
}
