namespace OnePdf;

/// <summary>
/// A margin that can differ per side, in points: used for bleed (the margin outside the trim box) and slug (the margin outside the
/// bleed box). <c>new PdfPrintMargins(9)</c> is the same on all four sides. Values must be finite and not negative; that is checked
/// where the margins are used.
/// </summary>
/// <param name="Left">The margin on the left side.</param>
/// <param name="Bottom">The margin at the bottom.</param>
/// <param name="Right">The margin on the right side.</param>
/// <param name="Top">The margin at the top.</param>
public readonly record struct PdfPrintMargins(double Left, double Bottom, double Right, double Top)
{
    /// <summary>The same margin on all four sides.</summary>
    /// <param name="all">The margin in points.</param>
    public PdfPrintMargins(double all)
        : this(all, all, all, all)
    {
    }

    /// <summary>True when all four sides are equal.</summary>
    public bool IsUniform => Left == Bottom && Left == Right && Left == Top;

    internal void Check(string paramName, string what)
    {
        foreach (double v in (ReadOnlySpan<double>)[Left, Bottom, Right, Top])
        {
            if (!double.IsFinite(v) || v < 0)
            {
                throw new ArgumentException($"The {what} must be finite and not negative on every side.", paramName);
            }
        }
    }
}

/// <summary>
/// Printer's marks drawn in the slug area of a print page: crop (trim) marks at the four trim corners and registration marks at the
/// middle of each side. The marks are stroked in registration colour (<c>/Separation /All</c>, ISO 32000-2 8.6.6.4), so they print on
/// every plate, and they are marked as page artifacts (ISO 32000-2 14.8.2.2), so tagged output ignores them.
/// </summary>
/// <remarks>
/// Marks lie entirely outside the bleed box, a gap of <see cref="Offset"/> away from it, and inside the media box. They need slug: the
/// call that is given the marks and a slug smaller than <see cref="MinimumSlug"/> on any side throws <see cref="ArgumentException"/>
/// and changes nothing; the marks are never shrunk to fit. Colour bars are not drawn.
/// </remarks>
public sealed class PdfPrinterMarks
{
    /// <summary>Draw crop marks: a horizontal and a vertical line at each trim corner, reaching away from the page. Default true.</summary>
    public bool CropMarks { get; init; } = true;

    /// <summary>Draw registration marks (a circle with a cross hair) centred on each side's trim edge. Default true.</summary>
    public bool RegistrationMarks { get; init; } = true;

    /// <summary>Line width in points; finite, above zero, and at most half of <see cref="RegistrationSize"/>. Default 0.25.</summary>
    public double LineWidth { get; init; } = 0.25;

    /// <summary>The gap in points between the bleed box and the nearest end of any mark, so marks never touch the bleed; finite and at least half of <see cref="LineWidth"/>. Default 3.</summary>
    public double Offset { get; init; } = 3;

    /// <summary>Length in points of each crop mark line; finite and above zero. Default 14.</summary>
    public double Length { get; init; } = 14;

    /// <summary>Diameter in points of the registration circle; the cross hair is one and a half times as long. Finite and above zero. Default 10.</summary>
    public double RegistrationSize { get; init; } = 10;

    /// <summary>
    /// The slug the chosen marks need on every side: the gap, the longest mark and half the line width. Zero when neither kind is on.
    /// </summary>
    public double MinimumSlug
    {
        get
        {
            double reach = Math.Max(CropMarks ? Length : 0, RegistrationMarks ? 1.5 * RegistrationSize : 0);
            return reach > 0 ? Offset + reach + LineWidth / 2 : 0;
        }
    }

    internal void Check(PdfPrintMargins slug)
    {
        if (!double.IsFinite(LineWidth) || LineWidth <= 0 || !double.IsFinite(Offset) || !double.IsFinite(Length) || Length <= 0
            || !double.IsFinite(RegistrationSize) || RegistrationSize <= 0 || LineWidth > RegistrationSize / 2 || Offset < LineWidth / 2)
        {
            throw new ArgumentException("The marks need a line width above zero (at most half the registration size), an offset of at least half the line width, and a length and registration size above zero.", nameof(slug));
        }

        double need = MinimumSlug;
        if (!(slug.Left >= need && slug.Bottom >= need && slug.Right >= need && slug.Top >= need))
        {
            throw new ArgumentException($"The marks need a slug of at least {need.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} points on every side.", nameof(slug));
        }
    }

    // trim / bleedBox are in the coordinates of the builder (the page's default user space).
    internal void Draw(PdfContentBuilder b, PdfRectangle trim, PdfRectangle bleedBox)
    {
        if (!CropMarks && !RegistrationMarks)
        {
            return;
        }

        b.Save();
        b.BeginArtifact(PdfArtifactType.Page);
        b.SetStrokeRegistration();
        b.SetLineWidth(LineWidth);
        if (CropMarks)
        {
            double xl = bleedBox.Left - Offset, xr = bleedBox.Right + Offset, yb = bleedBox.Bottom - Offset, yt = bleedBox.Top + Offset;
            foreach (double y in (ReadOnlySpan<double>)[trim.Bottom, trim.Top])
            {
                Line(b, xl, y, xl - Length, y);
                Line(b, xr, y, xr + Length, y);
            }

            foreach (double x in (ReadOnlySpan<double>)[trim.Left, trim.Right])
            {
                Line(b, x, yb, x, yb - Length);
                Line(b, x, yt, x, yt + Length);
            }
        }

        if (RegistrationMarks)
        {
            double r = RegistrationSize / 2, arm = 0.75 * RegistrationSize, d = Offset + arm;
            double mx = (trim.Left + trim.Right) / 2, my = (trim.Bottom + trim.Top) / 2;
            Target(b, bleedBox.Left - d, my, r, arm);
            Target(b, bleedBox.Right + d, my, r, arm);
            Target(b, mx, bleedBox.Bottom - d, r, arm);
            Target(b, mx, bleedBox.Top + d, r, arm);
        }

        b.EndMarkedContent();
        b.Restore();
    }

    private static void Line(PdfContentBuilder b, double x1, double y1, double x2, double y2)
    {
        b.MoveTo(x1, y1);
        b.LineTo(x2, y2);
        b.Stroke();
    }

    private static void Target(PdfContentBuilder b, double cx, double cy, double r, double arm)
    {
        b.Ellipse(cx - r, cy - r, 2 * r, 2 * r);
        b.Stroke();
        Line(b, cx - arm, cy, cx + arm, cy);
        Line(b, cx, cy - arm, cx, cy + arm);
    }
}
