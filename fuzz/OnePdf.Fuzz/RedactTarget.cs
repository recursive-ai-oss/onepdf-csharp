using System;
using System.Collections.Generic;
using System.IO;
using OnePdf;
using OnePdf.Text;

namespace OnePdf.Fuzz;

/// <summary>
/// Redaction target. The input is 20 parameter bytes (options, four areas) followed by a PDF file or a bare content stream. The pages are
/// redacted, saved in full and read again. Only the library's typed exceptions may escape; the saved file must open; and the reopened
/// pages must not hold a glyph that lies inside an area (checked on upright pages with the page box at the origin).
/// </summary>
internal static class RedactTarget
{
    public const int HeaderLength = 20;

    private static readonly PdfLimits Limits = PdfLimits.Default with { MaxOperatorsPerPage = 100_000, MaxDecodedStreamBytes = 8 << 20, MaxObjects = 200_000, MaxImagePixels = 1 << 22 };

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length <= HeaderLength)
        {
            return;
        }

        ReadOnlySpan<byte> h = data[..HeaderLength];
        ReadOnlySpan<byte> body = data[HeaderLength..];
        byte[] file = body.Length > 4 && body[0] == '%' && body[1] == 'P' && body[2] == 'D' && body[3] == 'F' ? body.ToArray() : ContentTarget.Wrap(body);
        int sel = h[0];
        var options = new PdfRedactionOptions
        {
            FullyContainedOnly = (sel & 1) != 0,
            VectorMode = (sel & 2) != 0 ? PdfRedactionVectorMode.ClipOut : PdfRedactionVectorMode.RemovePaths,
            Annotations = (sel & 4) != 0 ? PdfRedactionAnnotationMode.KeepRedacted : PdfRedactionAnnotationMode.Remove,
            Overlay = (sel & 8) != 0 ? new PdfRedactionOverlay() : null,
            RemoveJavaScript = (sel & 16) != 0,
            RemoveEmbeddedFiles = (sel & 32) != 0,
            RemoveThumbnails = (sel & 64) != 0,
            MetadataTerms = (sel & 128) != 0 ? ["a", "Hello"] : [],
        };
        try
        {
            using PdfDocument doc = PdfDocument.Open(file, new PdfOpenOptions { Limits = Limits, AllowEditing = true });
            int pages = Math.Min(doc.Pages.Count, 2);
            if (pages == 0)
            {
                return;
            }

            var regions = new List<PdfRedactionRegion>();
            for (int i = 0; i < 4; i++)
            {
                double x = h[1 + (i * 4)] * 1.5, y = h[2 + (i * 4)] * 1.5;
                double w = 1 + (h[3 + (i * 4)] % 120), hh = 1 + (h[4 + (i * 4)] % 120);
                regions.Add(new PdfRedactionRegion(i % pages, new PdfRectangle(x, y, x + w, y + hh)));
            }

            if ((sel & 3) == 3)
            {
                regions.Add(new PdfRedactionRegion(0, new PdfRectangle(0, 0, 10000, 10000)));
            }

            doc.Redact(regions, options);
            using var ms = new MemoryStream();
            doc.Save(ms, new PdfSaveOptions { XrefFormat = PdfXrefFormat.Table, UseObjectStreams = false });
            using PdfDocument again = PdfDocument.Open(ms.ToArray(), new PdfOpenOptions { Limits = Limits });
            if (!options.FullyContainedOnly && options.Overlay is null)
            {
                Check(again, regions, pages);
            }
        }
        catch (PdfException)
        {
        }
    }

    private static void Check(PdfDocument again, List<PdfRedactionRegion> regions, int pages)
    {
        for (int p = 0; p < Math.Min(pages, again.Pages.Count); p++)
        {
            PdfPage page = again.Pages[p];
            if (page.Rotate != 0 || page.MediaBox.Left != 0 || page.MediaBox.Bottom != 0 || page.CropBox != page.MediaBox || page.UserUnit != 1)
            {
                continue;
            }

            foreach (PdfTextGlyph g in page.ExtractText(new PdfTextOptions { RetainGlyphs = true, IncludeClippedAndOffPage = true }).Glyphs)
            {
                PdfQuad q = g.Quad;
                foreach (PdfRedactionRegion r in regions)
                {
                    if (r.PageIndex == p && Meets(q, r.Rectangle, 0.5))
                    {
                        throw new InvalidOperationException($"a glyph {g.Text} at {g.Bounds} font {g.FontName} lies inside {r.Rectangle} on page {p} after redaction");
                    }
                }
            }
        }
    }

    // Separating-axis test of the glyph's quadrilateral against a rectangle; they must overlap by more than tol on every axis.
    private static bool Meets(PdfQuad quad, PdfRectangle r, double tol)
    {
        double[] q = [quad.X0, quad.Y0, quad.X1, quad.Y1, quad.X2, quad.Y2, quad.X3, quad.Y3];
        double minX = Math.Min(Math.Min(q[0], q[2]), Math.Min(q[4], q[6])), maxX = Math.Max(Math.Max(q[0], q[2]), Math.Max(q[4], q[6]));
        double minY = Math.Min(Math.Min(q[1], q[3]), Math.Min(q[5], q[7])), maxY = Math.Max(Math.Max(q[1], q[3]), Math.Max(q[5], q[7]));
        if (maxX <= r.Left + tol || minX >= r.Right - tol || maxY <= r.Bottom + tol || minY >= r.Top - tol)
        {
            return false;
        }

        for (int k = 0; k < 2; k++)
        {
            double ex = q[2 * (k + 1)] - q[2 * k], ey = q[(2 * (k + 1)) + 1] - q[(2 * k) + 1];
            double len = Math.Sqrt((ex * ex) + (ey * ey));
            if (len < 1e-9)
            {
                return false;
            }

            double nx = -ey / len, ny = ex / len;
            double qmin = double.PositiveInfinity, qmax = double.NegativeInfinity;
            for (int j = 0; j < 4; j++)
            {
                double v = (q[2 * j] * nx) + (q[(2 * j) + 1] * ny);
                qmin = Math.Min(qmin, v);
                qmax = Math.Max(qmax, v);
            }

            double cmin = double.PositiveInfinity, cmax = double.NegativeInfinity;
            for (int c = 0; c < 4; c++)
            {
                double v = (((c & 1) == 0 ? r.Left : r.Right) * nx) + (((c & 2) == 0 ? r.Bottom : r.Top) * ny);
                cmin = Math.Min(cmin, v);
                cmax = Math.Max(cmax, v);
            }

            if (qmax <= cmin + tol || cmax <= qmin + tol)
            {
                return false;
            }
        }

        return true;
    }
}
