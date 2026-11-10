namespace OnePdf;

// Print geometry for every page at once.
public sealed partial class PdfDocument
{
    /// <summary>
    /// Gives every page print geometry: each page keeps its current trim box (the crop box when none is stored), and its media box and
    /// bleed box become that grown by <paramref name="bleed"/> on every side. See <see cref="PdfPage.SetPrintGeometry(PdfRectangle, double)"/> for what is
    /// written on each page.
    /// </summary>
    /// <param name="bleed">The bleed margin in points; finite and zero or more.</param>
    /// <exception cref="ArgumentException">The bleed is negative or not finite. No page is changed.</exception>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    public void SetPrintGeometry(double bleed)
    {
        EnsureEditable(0);
        PageBoxes.WithBleed(new PdfRectangle(0, 0, 1, 1), bleed);
        PdfPageCollection pages = Pages;
        for (int i = 0; i < pages.Count; i++)
        {
            PdfPage p = pages[i];
            p.SetPrintGeometry(p.TrimBox, bleed);
        }
    }

    /// <summary>
    /// Gives every page print geometry with a slug: each page keeps its current trim box, its bleed box is that grown by
    /// <paramref name="bleed"/>, and its media box is the bleed box grown by <paramref name="slug"/>. See
    /// <see cref="PdfPage.SetPrintGeometry(PdfRectangle, double, double)"/>.
    /// </summary>
    /// <param name="bleed">The bleed margin in points; finite and zero or more.</param>
    /// <param name="slug">The slug margin outside the bleed in points; finite and zero or more.</param>
    /// <exception cref="ArgumentException">The bleed or slug is negative or not finite. No page is changed.</exception>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    public void SetPrintGeometry(double bleed, double slug)
    {
        EnsureEditable(0);
        PageBoxes.WithSlug(PageBoxes.WithBleed(new PdfRectangle(0, 0, 1, 1), bleed), slug);
        PdfPageCollection pages = Pages;
        for (int i = 0; i < pages.Count; i++)
        {
            PdfPage p = pages[i];
            p.SetPrintGeometry(p.TrimBox, bleed, slug);
        }
    }

    /// <summary>
    /// Gives every page print geometry with a slug and printer's marks: as <see cref="SetPrintGeometry(double, double)"/>, then each
    /// page gets <paramref name="marks"/> in its slug.
    /// </summary>
    /// <param name="bleed">The bleed margin in points; finite and zero or more.</param>
    /// <param name="slug">The slug margin outside the bleed in points; at least <see cref="PdfPrinterMarks.MinimumSlug"/>.</param>
    /// <param name="marks">The marks to draw on every page.</param>
    /// <exception cref="ArgumentException">The bleed or slug is not valid or too small for the marks. No page is changed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="marks"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    public void SetPrintGeometry(double bleed, double slug, PdfPrinterMarks marks)
    {
        ArgumentNullException.ThrowIfNull(marks);
        SetPrintGeometry(new PdfPrintMargins(bleed), new PdfPrintMargins(slug), marks);
    }

    /// <summary>
    /// Gives every page print geometry with a different bleed and slug on each side: each page keeps its current trim box, its bleed box
    /// is that grown by <paramref name="bleed"/>, and its media box is the bleed box grown by <paramref name="slug"/>. See
    /// <see cref="PdfPage.SetPrintGeometry(PdfRectangle, PdfPrintMargins, PdfPrintMargins, PdfPrinterMarks?)"/>.
    /// </summary>
    /// <param name="bleed">The bleed on each side, in points; finite and zero or more.</param>
    /// <param name="slug">The slug on each side outside the bleed, in points; finite and zero or more.</param>
    /// <param name="marks">Printer's marks to draw on every page, or null for none.</param>
    /// <exception cref="ArgumentException">A margin is not valid or too small for the marks. No page is changed.</exception>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    public void SetPrintGeometry(PdfPrintMargins bleed, PdfPrintMargins slug, PdfPrinterMarks? marks = null)
    {
        EnsureEditable(0);
        PageBoxes.WithSlug(PageBoxes.WithBleed(new PdfRectangle(0, 0, 1, 1), bleed), slug);
        marks?.Check(slug);
        PdfPageCollection pages = Pages;
        for (int i = 0; i < pages.Count; i++)
        {
            PdfPage p = pages[i];
            p.SetPrintGeometry(p.TrimBox, bleed, slug, marks);
        }
    }
}
