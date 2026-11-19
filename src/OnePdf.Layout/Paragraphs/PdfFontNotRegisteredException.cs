#if LAYOUT_CORE
namespace OnePdf.Layout;

/// <summary>
/// Text asks for a font family that the document's <see cref="TextFontRegistry"/> does not hold, and the registry's default family is not
/// registered either. <c>RecursiveAI.OnePdf.Layout.Core</c> carries no fonts: register the application's own with
/// <see cref="TextFontRegistry.Add"/>, or use the <c>RecursiveAI.OnePdf.Layout</c> package, which registers Liberation Sans, Serif and Mono.
/// </summary>
public sealed class PdfFontNotRegisteredException : PdfException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="requestedFamilies">The family names the text asked for (the default family when the style names none).</param>
    /// <param name="defaultFamily">The registry's <see cref="TextFontRegistry.DefaultFamily"/>.</param>
    public PdfFontNotRegisteredException(IReadOnlyList<string> requestedFamilies, string defaultFamily)
        : base(Format(requestedFamilies, defaultFamily))
    {
        RequestedFamilies = requestedFamilies;
        DefaultFamily = defaultFamily;
    }

    /// <summary>The family names the text asked for.</summary>
    public IReadOnlyList<string> RequestedFamilies { get; }

    /// <summary>The registry's default family, which was not registered either.</summary>
    public string DefaultFamily { get; }

    private static string Format(IReadOnlyList<string> families, string defaultFamily) =>
        $"No font is registered for the family '{string.Join(", ", families)}', and the default family '{defaultFamily}' is not registered either. " +
            "RecursiveAI.OnePdf.Layout.Core has no built-in fonts: add one with TextFontRegistry.Add(fontBytes, family) and set TextFontRegistry.DefaultFamily " +
            "(or name the family in the text style), or use the RecursiveAI.OnePdf.Layout package, which registers Liberation Sans, Serif and Mono.";
}
#endif
