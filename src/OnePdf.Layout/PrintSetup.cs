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
    /// <summary>Bleed in points on every side of the trim box; finite and zero or more. Default 0. Ignored when <see cref="BleedSides"/> is set.</summary>
    public double Bleed { get; set; }

    /// <summary>Slug in points on every side outside the bleed, room for printer's marks and job information; finite and zero or more. Default 0. Ignored when <see cref="SlugSides"/> is set.</summary>
    public double Slug { get; set; }

    /// <summary>Bleed per side (left, bottom, right, top), in points. When set it replaces <see cref="Bleed"/>. Default null.</summary>
    public PdfPrintMargins? BleedSides { get; set; }

    /// <summary>Slug per side (left, bottom, right, top), in points. When set it replaces <see cref="Slug"/>. Default null.</summary>
    public PdfPrintMargins? SlugSides { get; set; }

    /// <summary>
    /// Printer's marks (crop and registration marks) drawn in the slug of every page, as page artifacts, so tagged output stays
    /// conformant. A slug smaller than <see cref="PdfPrinterMarks.MinimumSlug"/> on any side is refused with an <see cref="ArgumentException"/> when the
    /// document is laid out. Default null: no marks.
    /// </summary>
    public PdfPrinterMarks? Marks { get; set; }

    /// <summary>The <c>/BoxColorInfo</c> entry for the crop box (ISO 32000-2 14.11.2.2), or null for none. Written as the core page API writes it.</summary>
    public PdfBoxColorInfo? CropBoxColor { get; set; }

    /// <summary>The <c>/BoxColorInfo</c> entry for the bleed box, or null for none.</summary>
    public PdfBoxColorInfo? BleedBoxColor { get; set; }

    /// <summary>The <c>/BoxColorInfo</c> entry for the trim box, or null for none.</summary>
    public PdfBoxColorInfo? TrimBoxColor { get; set; }

    /// <summary>The <c>/BoxColorInfo</c> entry for the art box, or null for none.</summary>
    public PdfBoxColorInfo? ArtBoxColor { get; set; }

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

    internal PdfPrintMargins BleedMargins => BleedSides ?? new PdfPrintMargins(Bleed);

    internal PdfPrintMargins SlugMargins => SlugSides ?? new PdfPrintMargins(Slug);

    /// <summary>The distance from the media box's left edge to the trim box's.</summary>
    internal double OffsetLeft => BleedMargins.Left + SlugMargins.Left;

    /// <summary>The distance from the media box's bottom edge to the trim box's.</summary>
    internal double OffsetBottom => BleedMargins.Bottom + SlugMargins.Bottom;

    internal void Validate()
    {
        CheckSides(BleedMargins, BleedSides is null ? nameof(Bleed) : nameof(BleedSides), "bleed");
        PdfPrintMargins slug = SlugMargins;
        CheckSides(slug, SlugSides is null ? nameof(Slug) : nameof(SlugSides), "slug");
        if (Marks is { } marks)
        {
            double need = marks.MinimumSlug;
            if (!(slug.Left >= need && slug.Bottom >= need && slug.Right >= need && slug.Top >= need))
            {
                throw new ArgumentException($"The printer's marks need a slug of at least {need.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} points on every side.", nameof(Marks));
            }
        }
    }

    private static void CheckSides(PdfPrintMargins m, string name, string what)
    {
        foreach (double v in (ReadOnlySpan<double>)[m.Left, m.Bottom, m.Right, m.Top])
        {
            if (!double.IsFinite(v) || v < 0)
            {
                throw new ArgumentException($"The {what} must be finite and not negative.", name);
            }
        }
    }

    internal PdfRectangle ToPage(PdfRectangle r) => new(r.Left + OffsetLeft, r.Bottom + OffsetBottom, r.Right + OffsetLeft, r.Top + OffsetBottom);

    // Both layout targets write the guide colours the same way.
    internal void ApplyColors(Action<PdfPageBox, PdfBoxColorInfo> set)
    {
        if (CropBoxColor is not null) { set(PdfPageBox.CropBox, CropBoxColor); }
        if (BleedBoxColor is not null) { set(PdfPageBox.BleedBox, BleedBoxColor); }
        if (TrimBoxColor is not null) { set(PdfPageBox.TrimBox, TrimBoxColor); }
        if (ArtBoxColor is not null) { set(PdfPageBox.ArtBox, ArtBoxColor); }
    }
}
