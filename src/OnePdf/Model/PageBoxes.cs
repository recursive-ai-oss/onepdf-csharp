namespace OnePdf;

// Shared rules for writing page boxes, used by the editing API on PdfPage and by the page writer.
internal static class PageBoxes
{
    // A box that is written is finite, has an area and has its corners in order (ISO 32000-2 7.9.5 allows any two opposite corners
    // on read; we always write the normal form).
    public static PdfRectangle Normalize(PdfRectangle r, string paramName)
    {
        if (!double.IsFinite(r.Left) || !double.IsFinite(r.Bottom) || !double.IsFinite(r.Right) || !double.IsFinite(r.Top))
        {
            throw new ArgumentException("A page box must have finite coordinates.", paramName);
        }

        var n = new PdfRectangle(Math.Min(r.Left, r.Right), Math.Min(r.Bottom, r.Top), Math.Max(r.Left, r.Right), Math.Max(r.Bottom, r.Top));
        if (n.IsEmpty)
        {
            throw new ArgumentException("A page box must have an area.", paramName);
        }

        return n;
    }

    public static PdfRectangle WithBleed(PdfRectangle trim, double bleed)
    {
        if (!double.IsFinite(bleed) || bleed < 0)
        {
            throw new ArgumentException("The bleed must be finite and not negative.", nameof(bleed));
        }

        var r = new PdfRectangle(trim.Left - bleed, trim.Bottom - bleed, trim.Right + bleed, trim.Top + bleed);
        return Normalize(r, nameof(bleed));
    }

    public static PdfArray ToArray(PdfRectangle r)
    {
        var a = new PdfArray();
        a.Append(Number(r.Left));
        a.Append(Number(r.Bottom));
        a.Append(Number(r.Right));
        a.Append(Number(r.Top));
        return a;
    }

    public static PdfObject Number(double v) => v == Math.Floor(v) && Math.Abs(v) < 1e9 ? new PdfInteger((long)v) : new PdfReal(v);

    // The slug is the margin outside the bleed box that holds printer's marks (ISO 32000-2 14.11.2.1: the media box is the full
    // physical medium, so it is the box that grows).
    public static PdfRectangle WithSlug(PdfRectangle bleedBox, double slug)
    {
        if (!double.IsFinite(slug) || slug < 0)
        {
            throw new ArgumentException("The slug must be finite and not negative.", nameof(slug));
        }

        return Normalize(new PdfRectangle(bleedBox.Left - slug, bleedBox.Bottom - slug, bleedBox.Right + slug, bleedBox.Top + slug), nameof(slug));
    }

    public static string ColorInfoKey(PdfPageBox box) => box switch
    {
        PdfPageBox.CropBox => "CropBox",
        PdfPageBox.BleedBox => "BleedBox",
        PdfPageBox.TrimBox => "TrimBox",
        PdfPageBox.ArtBox => "ArtBox",
        _ => throw new ArgumentException("/BoxColorInfo has entries for the crop, bleed, trim and art boxes only.", nameof(box)),
    };

    // The dictionary written for one box in /BoxColorInfo; shared by the editing API and the page writer so both write the same bytes.
    public static PdfDictionary BuildColorInfo(PdfBoxColorInfo info)
    {
        var d = new PdfDictionary();
        var c = new PdfArray();
        c.Append(new PdfReal(info.Red));
        c.Append(new PdfReal(info.Green));
        c.Append(new PdfReal(info.Blue));
        d.SetEntry("C", c);
        d.SetEntry("W", new PdfReal(info.LineWidth));
        d.SetEntry("S", new PdfName(info.Style == PdfBoxLineStyle.Dashed ? "D" : "S"));
        if (info.Style == PdfBoxLineStyle.Dashed)
        {
            var da = new PdfArray();
            foreach (double v in info.Dash)
            {
                da.Append(new PdfReal(v));
            }

            d.SetEntry("D", da);
        }

        return d;
    }

    public static void CheckColorInfo(PdfBoxColorInfo info)
    {
        if (!(info.Red >= 0 && info.Red <= 1 && info.Green >= 0 && info.Green <= 1 && info.Blue >= 0 && info.Blue <= 1))
        {
            throw new ArgumentException("Colour components are between 0 and 1.", nameof(info));
        }

        if (!double.IsFinite(info.LineWidth) || info.LineWidth < 0)
        {
            throw new ArgumentException("The line width must be finite and not negative.", nameof(info));
        }

        if (info.Style == PdfBoxLineStyle.Dashed)
        {
            bool any = false;
            if (info.Dash is null || info.Dash.Count == 0 || info.Dash.Count > 32)
            {
                throw new ArgumentException("A dash pattern has 1 to 32 entries.", nameof(info));
            }

            foreach (double v in info.Dash)
            {
                if (!double.IsFinite(v) || v < 0)
                {
                    throw new ArgumentException("Dash entries must be finite and not negative.", nameof(info));
                }

                any |= v > 0;
            }

            if (!any)
            {
                throw new ArgumentException("A dash pattern cannot be all zero.", nameof(info));
            }
        }
    }
}
