namespace OnePdf;

/// <summary>
/// A rectangle in default user space (points, y up). Rectangles read from a file are normalized so that
/// <see cref="Left"/> &lt;= <see cref="Right"/> and <see cref="Bottom"/> &lt;= <see cref="Top"/> (ISO 32000-2 7.9.5 allows any two
/// opposite corners).
/// </summary>
/// <param name="Left">The smaller x.</param>
/// <param name="Bottom">The smaller y.</param>
/// <param name="Right">The larger x.</param>
/// <param name="Top">The larger y.</param>
public readonly record struct PdfRectangle(double Left, double Bottom, double Right, double Top)
{
    /// <summary><see cref="Right"/> minus <see cref="Left"/>.</summary>
    public double Width => Right - Left;

    /// <summary><see cref="Top"/> minus <see cref="Bottom"/>.</summary>
    public double Height => Top - Bottom;

    /// <summary>True when the rectangle has no area.</summary>
    public bool IsEmpty => !(Width > 0 && Height > 0);

    /// <summary>The overlap of two rectangles, or null when they share no area.</summary>
    public PdfRectangle? Intersect(PdfRectangle other)
    {
        var r = new PdfRectangle(Math.Max(Left, other.Left), Math.Max(Bottom, other.Bottom), Math.Min(Right, other.Right), Math.Min(Top, other.Top));
        return r.IsEmpty ? null : r;
    }

    /// <summary>Reads four numbers from an array and normalizes them; null when the array is missing, short or not numeric.</summary>
    internal static PdfRectangle? From(PdfObject? o)
    {
        if (o is not PdfArray a || a.Count < 4)
        {
            return null;
        }

        Span<double> v = stackalloc double[4];
        for (int i = 0; i < 4; i++)
        {
            if (a[i] is not (PdfInteger or PdfReal))
            {
                return null;
            }

            v[i] = a.GetDouble(i);
            if (!double.IsFinite(v[i]))
            {
                return null;
            }
        }

        return new PdfRectangle(Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3]));
    }
}
