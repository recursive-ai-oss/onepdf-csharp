using OnePdf.PdfFonts;

namespace OnePdf;

public sealed partial class PdfDocument
{
    private FontCache? _fontCache;

    /// <summary>
    /// The provider that supplies fonts the file does not embed. Null (the default) uses <see cref="PdfFontProviders.Default"/>.
    /// Set it before the first font is used; fonts already loaded keep the provider they were built with.
    /// </summary>
    public IPdfFontProvider? FontProvider { get; set; }

    /// <summary>
    /// The font for a font dictionary (a value of a page's <c>/Font</c> resource), built once per document and shared by every thread.
    /// A null dictionary gives a default sans-serif font so content naming a missing font still decodes.
    /// </summary>
    internal PdfFont GetFont(PdfDictionary? fontDictionary)
    {
        FontCache cache = _fontCache ?? Interlocked.CompareExchange(ref _fontCache, new FontCache(this), null) ?? _fontCache;
        return cache.Get(fontDictionary, FontProvider ?? PdfFontProviders.Default);
    }

    /// <summary>The font named <paramref name="name"/> in a resource dictionary's <c>/Font</c> entry, or a default font when the name is missing.</summary>
    internal PdfFont GetFont(PdfDictionary? resources, string name) => GetFont(resources?.GetDictionary("Font")?.GetDictionary(name));

    internal void ReportInfo(string message, int objectNumber, long offset) => _diag.Info(message, objectNumber, offset);
}
