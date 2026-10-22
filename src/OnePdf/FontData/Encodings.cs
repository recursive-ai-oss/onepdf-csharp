using System.Globalization;
using OnePdf.FontPrograms;

namespace OnePdf.FontData;

/// <summary>The predefined single-byte encodings of ISO 32000-1 Annex D (and the built-in encodings of Symbol and ZapfDingbats).</summary>
internal enum PredefinedEncoding
{
    /// <summary>StandardEncoding (Annex D.2, STD column).</summary>
    Standard,

    /// <summary>WinAnsiEncoding (D.2, WIN column), with the footnoted bullet, space and hyphen fills.</summary>
    WinAnsi,

    /// <summary>MacRomanEncoding (D.2, MAC column), with the footnoted second space at 0xCA.</summary>
    MacRoman,

    /// <summary>MacExpertEncoding (D.4).</summary>
    MacExpert,

    /// <summary>The built-in encoding of the Symbol font (D.5).</summary>
    Symbol,

    /// <summary>The built-in encoding of ZapfDingbats (D.6).</summary>
    ZapfDingbats,

    /// <summary>PDFDocEncoding (D.2, PDF column), used for text strings in document metadata.</summary>
    PdfDoc,
}

/// <summary>Code to glyph-name tables for <see cref="PredefinedEncoding"/>. Loaded on first use.</summary>
internal static class Encodings
{
    private static readonly Lazy<string?[][]> Tables = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly string[] Names =
    [
        "StandardEncoding", "WinAnsiEncoding", "MacRomanEncoding", "MacExpertEncoding", "SymbolEncoding", "ZapfDingbatsEncoding", "PDFDocEncoding",
    ];

    /// <summary>The 256 glyph names of an encoding; unassigned codes are null. The array is shared: do not modify it.</summary>
    public static string?[] Get(PredefinedEncoding encoding) => Tables.Value[(int)encoding];

    /// <summary>The glyph name at a code, or null.</summary>
    public static string? GetName(PredefinedEncoding encoding, int code) => (uint)code < 256 ? Tables.Value[(int)encoding][code] : null;

    private static string?[][] Load()
    {
        var tables = new string?[Names.Length][];
        for (int i = 0; i < tables.Length; i++)
        {
            tables[i] = new string?[256];
        }

        foreach (string line in FontResources.ReadLines("encodings.bin"))
        {
            string[] f = line.Split('\t');
            int t = Array.IndexOf(Names, f[0]);
            int code = int.Parse(f[1], CultureInfo.InvariantCulture);
            if (t < 0 || (uint)code >= 256)
            {
                throw new FontProgramException("Corrupt encodings resource.");
            }

            tables[t][code] = f[2];
        }

        return tables;
    }
}
