namespace OnePdf.Fonts;

/// <summary>
/// Entry point of the substitute-font package. Call <see cref="Register"/> once at startup and documents that do not embed
/// the standard 14 fonts (or their Arial, Times New Roman and Courier New aliases) get outlines from the bundled
/// Liberation, Noto Sans Math and Noto Sans Symbols fonts.
/// </summary>
public static class FontsPackage
{
    /// <summary>The name of the package's primary assembly.</summary>
    public static string Name => "OnePdf.Fonts";

    /// <summary>
    /// The package's provider. It answers only for the standard 14 names and their common aliases and returns null for every
    /// other request, including CID-keyed fonts. Font files are read from the embedded resources on first use and shared by
    /// all documents and threads. Safe to use from any thread.
    /// </summary>
    public static IPdfFontProvider Provider { get; } = new SubstituteFontProvider();

    /// <summary>
    /// Adds <see cref="Provider"/> to <see cref="PdfFontProviders"/>. It sits behind close matches among installed fonts, so
    /// a machine's own Helvetica or Arial wins unless <see cref="PdfFontProviders.UseSystemFonts"/> is off. Calling it more
    /// than once has no further effect. Thread-safe.
    /// </summary>
    public static void Register() => PdfFontProviders.Register(Provider);
}

internal sealed class SubstituteFontProvider : IPdfFontProvider
{
    public PdfSubstituteFont? FindFont(PdfFontRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CidOrdering is not null)
        {
            return null;
        }

        // The core's own alias resolution first (it also reads descriptor flags), then the package's name table.
        IReadOnlyList<SubstituteFace>? chain = SubstituteFonts.GetChain(request.StandardName) ?? SubstituteFonts.GetChain(request.FontName);
        if (chain is null)
        {
            return null;
        }

        // Symbol and ZapfDingbats draw from several fonts: the first is primary, the rest fill the characters it lacks.
        PdfSubstituteFont[] rest = new PdfSubstituteFont[chain.Count - 1];
        for (int i = 0; i < rest.Length; i++)
        {
            rest[i] = Wrap(chain[i + 1]);
        }

        bool symbol = chain[0].Family == "Noto Sans Math";
        return new PdfSubstituteFont(() => chain[0].GetData(), chain[0].Name, "OnePdf.Fonts/" + chain[0].Name)
        {
            Fallbacks = rest,
            MapCodePoint = symbol ? SubstituteFonts.RemapSymbolCodePoint : null,
        };
    }

    private static PdfSubstituteFont Wrap(SubstituteFace f) => new(() => f.GetData(), f.Name, "OnePdf.Fonts/" + f.Name);
}
