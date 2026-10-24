using System.Globalization;

namespace OnePdf.Layout;

/// <summary>
/// An element cannot be placed: it does not fit even at the top of an empty page. The message and properties name the element by its
/// path (for example <c>Page[0] &gt; Content &gt; Column &gt; Item 3 &gt; Image</c>), the space it was offered and the space it needs.
/// </summary>
public sealed class PdfLayoutException : PdfException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="elementPath">Where the element sits in the tree.</param>
    /// <param name="availableWidth">Width offered, in points.</param>
    /// <param name="availableHeight">Height offered, in points.</param>
    /// <param name="requestedWidth">Width needed, in points, or NaN.</param>
    /// <param name="requestedHeight">Height needed, in points, or NaN.</param>
    /// <param name="reason">A short statement of what went wrong.</param>
    public PdfLayoutException(string elementPath, double availableWidth, double availableHeight, double requestedWidth, double requestedHeight, string reason)
        : base(Format(elementPath, availableWidth, availableHeight, requestedWidth, requestedHeight, reason))
    {
        ElementPath = elementPath;
        AvailableWidth = availableWidth;
        AvailableHeight = availableHeight;
        RequestedWidth = requestedWidth;
        RequestedHeight = requestedHeight;
    }

    /// <summary>Where the element sits in the tree, segments joined with <c> &gt; </c>.</summary>
    public string ElementPath { get; }

    /// <summary>The width offered, in points (infinity when unbounded).</summary>
    public double AvailableWidth { get; }

    /// <summary>The height offered, in points (infinity when unbounded).</summary>
    public double AvailableHeight { get; }

    /// <summary>The width needed, in points; NaN when not known.</summary>
    public double RequestedWidth { get; }

    /// <summary>The height needed, in points; NaN when not known.</summary>
    public double RequestedHeight { get; }

    private static string Format(string path, double aw, double ah, double rw, double rh, string reason) =>
        string.Create(CultureInfo.InvariantCulture, $"{reason} at {path}: available {Num(aw)} x {Num(ah)} pt, requested {Num(rw)} x {Num(rh)} pt.");

    private static string Num(double v) => double.IsNaN(v) ? "?" : double.IsInfinity(v) ? "unbounded" : v.ToString("0.##", CultureInfo.InvariantCulture);
}
