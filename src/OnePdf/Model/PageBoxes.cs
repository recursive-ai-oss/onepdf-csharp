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
}
