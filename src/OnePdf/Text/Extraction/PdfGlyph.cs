using System.Globalization;

namespace OnePdf.Text;

/// <summary>An affine matrix (a b c d e f) in PDF order: x' = a·x + c·y + e, y' = b·x + d·y + f.</summary>
/// <param name="A">Scale x.</param>
/// <param name="B">Shear y.</param>
/// <param name="C">Shear x.</param>
/// <param name="D">Scale y.</param>
/// <param name="E">Translation x.</param>
/// <param name="F">Translation y.</param>
public readonly record struct PdfTextMatrix(double A, double B, double C, double D, double E, double F)
{
    /// <summary>The identity matrix.</summary>
    public static PdfTextMatrix Identity { get; } = new(1, 0, 0, 1, 0, 0);

    /// <summary>Maps a point.</summary>
    public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);
}

/// <summary>A quadrilateral given by its four corners in order: bottom-left, bottom-right, top-right, top-left of the glyph box before the matrix is applied.</summary>
public readonly record struct PdfQuad(double X0, double Y0, double X1, double Y1, double X2, double Y2, double X3, double Y3);

/// <summary>
/// One shown character as the text extractor sees it. Coordinates are in <b>upright page space</b>: points, origin at the
/// bottom-left of the page as a viewer shows it (page <c>/Rotate</c> and the chosen box already applied), y up.
/// </summary>
/// <remarks>
/// <para>The glyph box in glyph space is x∈[0, <see cref="Advance"/>], y∈[<see cref="Descent"/>, <see cref="Ascent"/>] for horizontal writing and
/// x∈[-0.5, 0.5], y∈[-<see cref="Advance"/>, 0] for vertical writing (origin at the top centre), both in em units; <see cref="Matrix"/> maps
/// glyph space (one em = one unit) to page space, so the font size is folded into it.</para>
/// <para>Content order is the order of the list the glyph arrives in.</para>
/// </remarks>
public readonly struct PdfGlyph
{
    /// <summary>The Unicode text of the glyph (usually one code point; a ligature glyph may carry several). Never null; may be empty for glyphs whose text was replaced by <see cref="ActualText"/>.</summary>
    public string Text { get; init; }

    /// <summary>An identifier of the font within the document (stable for the run), or -1.</summary>
    public int FontId { get; init; }

    /// <summary>The font's name (<c>BaseFont</c> without a subset tag) or null.</summary>
    public string? FontName { get; init; }

    /// <summary>Maps glyph space (em units) to page space; see the type remarks.</summary>
    public PdfTextMatrix Matrix { get; init; }

    /// <summary>The advance in em units along the writing direction (for vertical writing, the downward advance as a positive number).</summary>
    public double Advance { get; init; }

    /// <summary>The ascent in em units above the baseline (horizontal writing). Default 0.9.</summary>
    public double Ascent { get; init; }

    /// <summary>The descent in em units, zero or negative. Default -0.2.</summary>
    public double Descent { get; init; }

    /// <summary>True when the glyph was shown in vertical writing mode.</summary>
    public bool IsVertical { get; init; }

    /// <summary>The fill colour as 0xRRGGBB, or -1 when unknown.</summary>
    public int FillColor { get; init; }

    /// <summary>The text rendering mode, 0 to 7.</summary>
    public int RenderMode { get; init; }

    /// <summary>False when the glyph does not paint anything (render mode 3 or 7, a fully transparent fill). Such glyphs are kept and flagged unless clipped.</summary>
    public bool IsVisible { get; init; }

    /// <summary>True when the current clip removes the glyph entirely. Excluded from extraction unless the options keep them.</summary>
    public bool IsClipped { get; init; }

    /// <summary>The marked-content identifier (<c>/MCID</c>) of the innermost marked-content sequence that has one, or -1.</summary>
    public int MarkedContentId { get; init; }

    /// <summary>Non-zero for every glyph inside one marked-content sequence that has an <c>/ActualText</c>; the same number for the whole span, 0 otherwise.</summary>
    public int ActualTextSpan { get; init; }

    /// <summary>The <c>/ActualText</c> string, set on the first glyph (in content order) of the span only; null elsewhere.</summary>
    public string? ActualText { get; init; }

    /// <summary>Creates a glyph with default metrics (ascent 0.9, descent -0.2, visible, no marked content).</summary>
    public PdfGlyph()
    {
        Text = "";
        FontId = -1;
        Matrix = PdfTextMatrix.Identity;
        Ascent = 0.9;
        Descent = -0.2;
        FillColor = -1;
        IsVisible = true;
        MarkedContentId = -1;
    }

    /// <summary>The origin of the glyph in page space.</summary>
    public (double X, double Y) Origin => (Matrix.E, Matrix.F);

    /// <summary>The em size in page units: the length of the matrix's y axis.</summary>
    public double Size => Math.Sqrt(Matrix.C * Matrix.C + Matrix.D * Matrix.D);

    /// <summary>The corners of the glyph box in page space.</summary>
    public PdfQuad Quad
    {
        get
        {
            double x0, x1, y0, y1;
            if (IsVertical)
            {
                x0 = -0.5;
                x1 = 0.5;
                y0 = -Advance;
                y1 = 0;
            }
            else
            {
                x0 = 0;
                x1 = Advance;
                y0 = Descent;
                y1 = Ascent;
            }

            PdfTextMatrix m = Matrix;
            (double ax, double ay) = m.Apply(x0, y0);
            (double bx, double by) = m.Apply(x1, y0);
            (double cx, double cy) = m.Apply(x1, y1);
            (double dx, double dy) = m.Apply(x0, y1);
            return new PdfQuad(ax, ay, bx, by, cx, cy, dx, dy);
        }
    }

    /// <summary>The axis-aligned bounding box of <see cref="Quad"/>.</summary>
    public PdfRectangle Bounds
    {
        get
        {
            PdfQuad q = Quad;
            return new PdfRectangle(
                Math.Min(Math.Min(q.X0, q.X1), Math.Min(q.X2, q.X3)),
                Math.Min(Math.Min(q.Y0, q.Y1), Math.Min(q.Y2, q.Y3)),
                Math.Max(Math.Max(q.X0, q.X1), Math.Max(q.X2, q.X3)),
                Math.Max(Math.Max(q.Y0, q.Y1), Math.Max(q.Y2, q.Y3)));
        }
    }

    /// <summary>A copy with different text.</summary>
    public PdfGlyph WithText(string text) => this with { Text = text };

    /// <inheritdoc/>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Text} @({Matrix.E:0.##},{Matrix.F:0.##})");
}

/// <summary>The glyphs of one page, in content order, with the page size in upright page space.</summary>
public sealed class PdfPageGlyphs
{
    /// <summary>Creates the input for one page.</summary>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="width">Width of the upright page in points.</param>
    /// <param name="height">Height of the upright page in points.</param>
    /// <param name="glyphs">The glyphs in content order.</param>
    public PdfPageGlyphs(int pageIndex, double width, double height, IReadOnlyList<PdfGlyph> glyphs)
    {
        ArgumentNullException.ThrowIfNull(glyphs);
        PageIndex = pageIndex;
        Width = width;
        Height = height;
        Glyphs = glyphs;
    }

    /// <summary>Zero-based page index.</summary>
    public int PageIndex { get; }

    /// <summary>Width of the upright page in points.</summary>
    public double Width { get; }

    /// <summary>Height of the upright page in points.</summary>
    public double Height { get; }

    /// <summary>The glyphs in content order.</summary>
    public IReadOnlyList<PdfGlyph> Glyphs { get; }
}
