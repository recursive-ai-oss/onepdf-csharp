namespace OnePdf.Layout;

/// <summary>
/// Print geometry for a run of pages: bleed and slug around the trim size, and optional explicit crop and art boxes. The page size
/// (<see cref="LayoutPage.Size"/>) is the trim size, and margins, headers, footers and content are laid out inside it exactly as
/// without print geometry, so adding bleed or slug never moves content on the trimmed page (ISO 32000-2 14.11.2).
/// </summary>
/// <remarks>
/// The pages get a trim box of the page size, a bleed box that is the trim box grown by <see cref="Bleed"/>, and a media box that is the
/// bleed box grown by <see cref="Slug"/>. No crop box is written unless <see cref="CropBox"/> is set, so the slug shows and prints.
/// </remarks>
public sealed class PrintSetup
{
    /// <summary>Bleed in points on every side of the trim box; finite and zero or more. Default 0.</summary>
    public double Bleed { get; set; }

    /// <summary>Slug in points on every side outside the bleed, room for printer's marks and job information; finite and zero or more. Default 0.</summary>
    public double Slug { get; set; }

    /// <summary>
    /// When true (default) and <see cref="Bleed"/> is above zero, the section's <see cref="LayoutPage.Background"/> is laid out over the
    /// bleed box (the trim size grown by the bleed) and drawn from the bleed box's corner, so backgrounds and colour fills run into the
    /// bleed. Its content is then laid out against the larger area; set false to lay it out over the trim size only.
    /// </summary>
    public bool BleedBackground { get; set; } = true;

    /// <summary>An explicit crop box, in points from the trim box's lower-left corner with y pointing up (so a negative origin reaches into the bleed). Null writes none.</summary>
    public PdfRectangle? CropBox { get; set; }

    /// <summary>An explicit art box, in the same coordinates as <see cref="CropBox"/>. Null writes none.</summary>
    public PdfRectangle? ArtBox { get; set; }

    /// <summary>The distance from the media box's edge to the trim box's edge.</summary>
    internal double Margin => Bleed + Slug;

    internal void Validate()
    {
        if (!double.IsFinite(Bleed) || Bleed < 0)
        {
            throw new ArgumentException("The bleed must be finite and not negative.", nameof(Bleed));
        }

        if (!double.IsFinite(Slug) || Slug < 0)
        {
            throw new ArgumentException("The slug must be finite and not negative.", nameof(Slug));
        }
    }

    internal PdfRectangle ToPage(PdfRectangle r) => new(r.Left + Margin, r.Bottom + Margin, r.Right + Margin, r.Top + Margin);
}
