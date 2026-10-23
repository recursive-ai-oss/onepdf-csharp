namespace OnePdf.Appearance;

/// <summary>Chooses between widget and markup generation for an annotation and decides when generation applies.</summary>
internal static class AppearanceGenerator
{
    /// <summary>Generates a normal appearance for the annotation, or returns null (with a diagnostic where that is news) when its subtype is not supported or its geometry is unusable.</summary>
    public static PdfGeneratedAppearance? Generate(PdfDocument doc, PdfAnnotation annotation, Action<PdfSeverity, string> report)
    {
        var c = new GenContext(doc, annotation.Dictionary, report);
        string subtype = annotation.Subtype;
        PdfRectangle rect = annotation.Rect;
        if (subtype == "Widget")
        {
            return WidgetAppearance.Generate(c, rect);
        }

        if (MarkupAppearance.Supports(subtype))
        {
            return MarkupAppearance.Generate(c, subtype, rect);
        }

        return null;
    }

    /// <summary>
    /// True when the interpreter should replace or supply the appearance of a widget because the form asks for it
    /// (<c>/NeedAppearances true</c>, ISO 32000-2 12.7.3).
    /// </summary>
    public static bool FormNeedsAppearances(PdfDocument doc) =>
        doc.Catalog.GetDictionary("AcroForm")?.GetBool("NeedAppearances") ?? false;
}
