using System.Collections.Concurrent;

namespace OnePdf.PdfFonts;

/// <summary>
/// Fonts of one document, keyed by the font dictionary's object number (or, for a direct dictionary, by its identity). Each font
/// is built once, even when threads ask at the same time.
/// </summary>
internal sealed class FontCache
{
    private const int MaxDirect = 4096;

    private readonly PdfDocument _document;
    private readonly ConcurrentDictionary<int, Lazy<PdfFont>> _byObject = new();
    private readonly ConcurrentDictionary<PdfDictionary, Lazy<PdfFont>> _direct = new(ReferenceEqualityComparer.Instance);

    public FontCache(PdfDocument document) => _document = document;

    public PdfFont Get(PdfDictionary? dictionary, IPdfFontProvider provider)
    {
        dictionary ??= new PdfDictionary(null);
        int number = dictionary.Reference?.Number ?? -1;
        if (number >= 0)
        {
            return _byObject.GetOrAdd(number, _ => new Lazy<PdfFont>(() => Create(dictionary, provider, number), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        if (_direct.Count >= MaxDirect)
        {
            return Create(dictionary, provider, -1);
        }

        return _direct.GetOrAdd(dictionary, d => new Lazy<PdfFont>(() => Create(d, provider, -1), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    /// <summary>Drops the font built for a dictionary, after the dictionary's entries changed.</summary>
    public void Forget(PdfDictionary dictionary)
    {
        if (dictionary.Reference is { } r)
        {
            _byObject.TryRemove(r.Number, out _);
        }
        else
        {
            _direct.TryRemove(dictionary, out _);
        }
    }

    private PdfFont Create(PdfDictionary dictionary, IPdfFontProvider provider, int objectNumber)
    {
        var ctx = new FontContext(_document, provider, objectNumber);
        string? subtype = dictionary.GetName("Subtype");
        if (subtype is null && dictionary.ContainsKey("DescendantFonts"))
        {
            subtype = "Type0";
        }

        return subtype switch
        {
            "Type0" => new CompositeFont(dictionary, ctx),
            "Type3" => new Type3Font(dictionary, ctx),
            _ => new SimpleFont(dictionary, ctx),
        };
    }
}
