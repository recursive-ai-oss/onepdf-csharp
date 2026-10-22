namespace OnePdf.PdfFonts;

/// <summary>
/// Decomposition of ligature characters into the letters they stand for, for text extraction (the Unicode compatibility
/// decompositions of the Latin ligature blocks). Fonts report ligature glyphs as one character such as U+FB01; searchable text
/// wants "fi".
/// </summary>
internal static class Ligatures
{
    /// <summary>Returns the decomposition of a single ligature character, or false when the character is not a ligature.</summary>
    public static bool TryDecompose(char c, out string letters)
    {
        letters = c switch
        {
            '\uFB00' => "ff",
            '\uFB01' => "fi",
            '\uFB02' => "fl",
            '\uFB03' => "ffi",
            '\uFB04' => "ffl",
            '\uFB05' => "st",
            '\uFB06' => "st",
            '\u0132' => "IJ",
            '\u0133' => "ij",
            '\u0149' => "\u02BCn",
            '\u01C7' => "LJ",
            '\u01C8' => "Lj",
            '\u01C9' => "lj",
            '\u01CA' => "NJ",
            '\u01CB' => "Nj",
            '\u01CC' => "nj",
            '\u01C4' => "D\u017D",
            '\u01C5' => "D\u017E",
            '\u01C6' => "d\u017E",
            _ => string.Empty,
        };
        return letters.Length != 0;
    }

    /// <summary>Replaces every ligature character in <paramref name="text"/>; returns the same instance when there is none.</summary>
    public static string Decompose(string text)
    {
        int i = 0;
        for (; i < text.Length; i++)
        {
            if (TryDecompose(text[i], out _))
            {
                break;
            }
        }

        if (i == text.Length)
        {
            return text;
        }

        var sb = new System.Text.StringBuilder(text.Length + 4);
        sb.Append(text, 0, i);
        for (; i < text.Length; i++)
        {
            if (TryDecompose(text[i], out string letters))
            {
                sb.Append(letters);
            }
            else
            {
                sb.Append(text[i]);
            }
        }

        return sb.ToString();
    }
}
