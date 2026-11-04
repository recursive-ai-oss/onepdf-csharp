using System.Linq;

namespace OnePdf;

/// <summary>Builds and stores text markup annotations (ISO 32000-2 12.5.6.10, 12.5.6.11, 12.5.6.14) on pages of an editable document.</summary>
internal static class MarkupAnnotationWriter
{
    private const double MaxExtent = 1e7;

    // A pop-up carries these flags in files written by common viewers: Print, NoZoom and NoRotate (ISO 32000-2 Table 167).
    private const int PopupFlags = 4 | 8 | 16;

    public static PdfAnnotation Add(PdfPage page, PdfMarkupAnnotationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        PdfDocument doc = page.Document;
        doc.EnsureEditable(0);
        Validate(doc, options);

        string subtype = options.Kind.ToString();
        bool caret = options.Kind == PdfMarkupKind.Caret;

        PdfRectangle rect = options.Rectangle is { } given ? Normalize(given) : Bounds(options.Quads);
        var annot = new PdfDictionary();
        annot.SetEntry("Type", new PdfName("Annot"));
        annot.SetEntry("Subtype", new PdfName(subtype));
        annot.SetEntry("Rect", RectArray(rect));
        annot.SetEntry("F", new PdfInteger((int)options.Flags));
        if (options.Color is { } color)
        {
            var c = new PdfArray();
            foreach (double v in color)
            {
                c.Append(new PdfReal(v));
            }

            annot.SetEntry("C", c);
        }

        if (options.Opacity is { } opacity)
        {
            annot.SetEntry("CA", new PdfReal(opacity));
        }

        if (options.Contents is not null)
        {
            annot.SetEntry("Contents", PdfString.FromText(options.Contents));
        }

        if (options.Title is not null)
        {
            annot.SetEntry("T", PdfString.FromText(options.Title));
        }

        if (options.Name is not null)
        {
            annot.SetEntry("NM", PdfString.FromText(options.Name));
        }

        if (options.ModifiedDate is { } modified)
        {
            annot.SetEntry("M", PdfString.FromText(PdfDocument.FormatDate(modified)));
        }

        if (caret)
        {
            if (options.RectangleDifferences is { } rd)
            {
                // /RD is four distances (left, bottom, right, top), not a rectangle: no normalizing.
                annot.SetEntry("RD", RectArray(rd.Left, rd.Bottom, rd.Right, rd.Top));
            }

            if (options.CaretSymbol == PdfCaretSymbol.Paragraph)
            {
                annot.SetEntry("Sy", new PdfName("P"));
            }
        }
        else
        {
            var quads = new PdfArray();
            foreach (PdfMarkupQuad q in options.Quads)
            {
                foreach (double v in (double[])[q.UpperLeftX, q.UpperLeftY, q.UpperRightX, q.UpperRightY, q.LowerLeftX, q.LowerLeftY, q.LowerRightX, q.LowerRightY])
                {
                    quads.Append(new PdfReal(v));
                }
            }

            annot.SetEntry("QuadPoints", quads);
        }

        // The appearance is generated from the annotation's own data (the same generator the interpreter uses for annotations
        // that lack one) before anything is added to the document, so a failure leaves the document unchanged.
        PdfGeneratedAppearance gen = new PdfAnnotation(doc, page, annot).GenerateAppearance()
            ?? throw new ArgumentException("No appearance could be generated from the given geometry (see the document diagnostics).", nameof(options));

        // ISO 32000-2 12.5.5 maps the transformed /BBox onto /Rect; making the two equal keeps the drawing at its true size.
        PdfRectangle box = new(Round(gen.Rect.Left), Round(gen.Rect.Bottom), Round(gen.Rect.Right), Round(gen.Rect.Top));
        annot.SetEntry("Rect", RectArray(box));

        // Object numbers are taken in a fixed order (annotation, appearance, pop-up), so the same calls number the same way.
        PdfReference annotRef = doc.AddObject(annot);
        if (page.Dictionary.Reference is { } pageRef)
        {
            annot.SetEntry("P", pageRef);
        }

        PdfStream form = doc.CreateStream(gen.Content.Span, compress: false);
        foreach (string key in gen.Dictionary.Keys.ToArray())
        {
            if (key is "Length" or "Filter" or "DecodeParms" || gen.Dictionary.GetRaw(key) is not { } value)
            {
                continue;
            }

            if (key == "BBox")
            {
                value = RectArray(box);
            }

            form.Dictionary.SetEntry(key, value);
        }

        var ap = new PdfDictionary();
        ap.SetEntry("N", form);
        annot.SetEntry("AP", ap);

        PdfReference? popupRef = null;
        if (options.Popup is { } popup)
        {
            var p = new PdfDictionary();
            p.SetEntry("Type", new PdfName("Annot"));
            p.SetEntry("Subtype", new PdfName("Popup"));
            p.SetEntry("Rect", RectArray(Normalize(popup.Rectangle)));
            p.SetEntry("F", new PdfInteger(PopupFlags));
            p.SetEntry("Open", new PdfBoolean(popup.Open));
            p.SetEntry("Parent", annotRef);
            if (page.Dictionary.Reference is { } pr)
            {
                p.SetEntry("P", pr);
            }

            popupRef = doc.AddObject(p);
            annot.SetEntry("Popup", popupRef);
        }

        AppendToAnnots(page.Dictionary, annotRef);
        if (popupRef is not null)
        {
            AppendToAnnots(page.Dictionary, popupRef);
        }

        page.ResetAnnotations();
        return new PdfAnnotation(doc, page, annot);
    }

    private static void Validate(PdfDocument doc, PdfMarkupAnnotationOptions o)
    {
        if (!Enum.IsDefined(o.Kind))
        {
            throw new ArgumentException("Unknown markup kind.", nameof(o));
        }

        if (o.Kind == PdfMarkupKind.Caret)
        {
            if (o.Quads.Count != 0)
            {
                throw new ArgumentException("A Caret annotation has no quadrilaterals (ISO 32000-2 12.5.6.11).", nameof(o));
            }

            if (o.Rectangle is not { } r || !Finite(r.Left, r.Bottom, r.Right, r.Top) || Normalize(r).IsEmpty)
            {
                throw new ArgumentException("A Caret annotation needs a non-empty finite Rectangle.", nameof(o));
            }

            if (o.RectangleDifferences is { } d && !Finite(d.Left, d.Bottom, d.Right, d.Top))
            {
                throw new ArgumentException("RectangleDifferences must be finite.", nameof(o));
            }
        }
        else
        {
            if (o.Quads.Count == 0)
            {
                throw new ArgumentException("A text markup annotation needs at least one quadrilateral.", nameof(o));
            }

            if (o.Quads.Count > doc.Limits.MaxAppearanceItems)
            {
                throw new PdfLimitException($"A markup annotation may have at most {doc.Limits.MaxAppearanceItems} quadrilaterals.");
            }

            foreach (PdfMarkupQuad q in o.Quads)
            {
                if (!Finite(q.UpperLeftX, q.UpperLeftY, q.UpperRightX, q.UpperRightY, q.LowerLeftX, q.LowerLeftY, q.LowerRightX, q.LowerRightY))
                {
                    throw new ArgumentException("Quadrilateral coordinates must be finite and below 10^7 in magnitude.", nameof(o));
                }
            }

            if (o.Rectangle is { } r && !Finite(r.Left, r.Bottom, r.Right, r.Top))
            {
                throw new ArgumentException("Rectangle must be finite.", nameof(o));
            }
        }

        if (o.Color is { } c)
        {
            if (c.Count is not (1 or 3 or 4))
            {
                throw new ArgumentException("Color needs one, three or four components.", nameof(o));
            }

            foreach (double v in c)
            {
                if (!(v >= 0 && v <= 1))
                {
                    throw new ArgumentException("Color components must be between 0 and 1.", nameof(o));
                }
            }
        }

        if (o.Opacity is { } op && !(op > 0 && op <= 1))
        {
            throw new ArgumentException("Opacity must be above 0 and at most 1.", nameof(o));
        }

        if (o.Popup is { } p && (!Finite(p.Rectangle.Left, p.Rectangle.Bottom, p.Rectangle.Right, p.Rectangle.Top) || Normalize(p.Rectangle).IsEmpty))
        {
            throw new ArgumentException("The pop-up needs a non-empty finite rectangle.", nameof(o));
        }

        if (o.CaretSymbol is not (PdfCaretSymbol.None or PdfCaretSymbol.Paragraph))
        {
            throw new ArgumentException("Unknown caret symbol.", nameof(o));
        }
    }

    private static bool Finite(params ReadOnlySpan<double> values)
    {
        foreach (double v in values)
        {
            if (!double.IsFinite(v) || Math.Abs(v) > MaxExtent)
            {
                return false;
            }
        }

        return true;
    }

    private static double Round(double v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

    private static PdfRectangle Normalize(PdfRectangle r) =>
        new(Math.Min(r.Left, r.Right), Math.Min(r.Bottom, r.Top), Math.Max(r.Left, r.Right), Math.Max(r.Bottom, r.Top));

    private static PdfRectangle Bounds(IReadOnlyList<PdfMarkupQuad> quads)
    {
        double l = double.PositiveInfinity, b = double.PositiveInfinity, r = double.NegativeInfinity, t = double.NegativeInfinity;
        foreach (PdfMarkupQuad q in quads)
        {
            foreach ((double x, double y) in ((double, double)[])[(q.UpperLeftX, q.UpperLeftY), (q.UpperRightX, q.UpperRightY), (q.LowerLeftX, q.LowerLeftY), (q.LowerRightX, q.LowerRightY)])
            {
                l = Math.Min(l, x);
                r = Math.Max(r, x);
                b = Math.Min(b, y);
                t = Math.Max(t, y);
            }
        }

        return new PdfRectangle(l, b, r, t);
    }

    private static PdfArray RectArray(PdfRectangle r) => RectArray(r.Left, r.Bottom, r.Right, r.Top);

    private static PdfArray RectArray(double a, double b, double c, double d)
    {
        var arr = new PdfArray();
        foreach (double v in (double[])[a, b, c, d])
        {
            arr.Append(new PdfReal(Math.Round(v, 4, MidpointRounding.AwayFromZero)));
        }

        return arr;
    }

    private static void AppendToAnnots(PdfDictionary page, PdfReference item)
    {
        if (page.Get("Annots") is PdfArray existing)
        {
            existing.Append(item);
        }
        else
        {
            var fresh = new PdfArray();
            fresh.Append(item);
            page.SetEntry("Annots", fresh);
        }
    }
}
