namespace OnePdf;

public abstract partial class PdfContentBuilder
{
    private readonly List<(string Name, PdfObject Value)> _shadings = [];

    internal IReadOnlyList<(string Name, PdfObject Value)> ShadingResources => _shadings;

    /// <summary>
    /// Paints the gradient over the whole current clipping region (the <c>sh</c> operator, ISO 32000-2 8.7.4.2). To fill a shape, build
    /// its path, call <see cref="Clip"/>, then this, all between <see cref="Save"/> and <see cref="Restore"/>. The gradient's coordinates
    /// are in the current user space; the current fill opacity applies (<see cref="SetFillOpacity"/>).
    /// </summary>
    /// <param name="gradient">The gradient.</param>
    /// <exception cref="ArgumentNullException"><paramref name="gradient"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The builder is closed.</exception>
    public void PaintGradient(PdfGradient gradient)
    {
        ArgumentNullException.ThrowIfNull(gradient);
        CheckOpen();
        string name = _names.Next("OpSh");
        _shadings.Add((name, gradient.ToShading()));
        Emit("/" + name + " sh\n");
    }

    /// <summary>
    /// Fills the rectangle with the gradient: a saved state, the rectangle as clip, <see cref="PaintGradient(PdfGradient)"/>, restore.
    /// The gradient's coordinates are in the current user space, not relative to the rectangle.
    /// </summary>
    /// <param name="x">Left.</param>
    /// <param name="y">Bottom.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="gradient">The gradient.</param>
    public void FillRectangle(double x, double y, double width, double height, PdfGradient gradient)
    {
        ArgumentNullException.ThrowIfNull(gradient);
        Save();
        Rectangle(x, y, width, height);
        Clip();
        PaintGradient(gradient);
        Restore();
    }
}
