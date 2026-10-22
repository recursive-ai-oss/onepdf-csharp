namespace OnePdf.Fonts;

/// <summary>
/// Finds the substitute face for a standard PDF font name. Liberation Sans, Serif and Mono (SIL OFL 1.1, metric-compatible
/// with Helvetica/Arial, Times and Courier) stand in for the twelve Helvetica, Times and Courier faces; Symbol is drawn from
/// Noto Sans Math with Liberation Serif for the few Latin-range glyphs; ZapfDingbats from Noto Sans Symbols 2 with
/// Noto Sans Math and Noto Sans Symbols filling the rest.
/// </summary>
public static class SubstituteFonts
{
    private const string LibSans = "Liberation Sans";
    private const string LibSerif = "Liberation Serif";
    private const string LibMono = "Liberation Mono";

    private static readonly SubstituteFace[] Sans = Family("LiberationSans", LibSans);
    private static readonly SubstituteFace[] Serif = Family("LiberationSerif", LibSerif);
    private static readonly SubstituteFace[] Mono = Family("LiberationMono", LibMono);
    private static readonly SubstituteFace Math = Single("NotoSansMath-Regular", "Noto Sans Math");
    private static readonly SubstituteFace Symbols2 = Single("NotoSansSymbols2-Regular", "Noto Sans Symbols 2");
    private static readonly SubstituteFace Symbols = Single("NotoSansSymbols-Regular", "Noto Sans Symbols");

    private static readonly IReadOnlyList<SubstituteFace> SymbolChain = [Math, Serif[0]];
    private static readonly IReadOnlyList<SubstituteFace> ZapfChain = [Symbols2, Math, Symbols];

    /// <summary>The standard font names the package covers, as the PDF specification spells them.</summary>
    public static IReadOnlyList<string> StandardNames { get; } =
    [
        "Courier", "Courier-Bold", "Courier-Oblique", "Courier-BoldOblique",
        "Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique",
        "Times-Roman", "Times-Bold", "Times-Italic", "Times-BoldItalic",
        "Symbol", "ZapfDingbats",
    ];

    /// <summary>Every font file in the package: the twelve Liberation faces, then Noto Sans Math, Symbols 2 and Symbols.</summary>
    public static IReadOnlyList<SubstituteFace> AllFaces { get; } =
        [.. Sans, .. Serif, .. Mono, Math, Symbols2, Symbols];

    /// <summary>
    /// Finds the primary substitute for a <c>/BaseFont</c> name. Accepts the 14 standard names and their common aliases:
    /// Arial, ArialMT, Arial-BoldMT, Arial,Bold, TimesNewRoman, TimesNewRomanPSMT, TimesNewRomanPS-BoldItalicMT, CourierNew,
    /// CourierNewPS-BoldMT, SymbolMT, and so on, with or without a six-letter subset prefix (<c>ABCDEF+</c>) and with spaces
    /// ignored. Matching is case-insensitive. Returns false for any other name; the package never guesses at unknown fonts.
    /// </summary>
    public static bool TryFind(string? baseFont, out SubstituteFace face)
    {
        IReadOnlyList<SubstituteFace>? chain = GetChain(baseFont);
        if (chain is null)
        {
            face = null!;
            return false;
        }

        face = chain[0];
        return true;
    }

    /// <summary>
    /// Like <see cref="TryFind"/> but returns the whole fallback chain, or null for a name that is not a standard font. The
    /// first face is the primary. Symbol and ZapfDingbats have further faces to consult, in order, for code points the
    /// primary lacks; the Helvetica, Times and Courier families have a single face.
    /// </summary>
    public static IReadOnlyList<SubstituteFace>? GetChain(string? baseFont)
    {
        if (!Classify(baseFont, out Kind kind, out bool bold, out bool italic))
        {
            return null;
        }

        int style = (bold ? 1 : 0) | (italic ? 2 : 0);
        switch (kind)
        {
            case Kind.Sans: return [Sans[style]];
            case Kind.Serif: return [Serif[style]];
            case Kind.Mono: return [Mono[style]];
            case Kind.Symbol: return SymbolChain;
            default: return ZapfChain;
        }
    }

    /// <summary>
    /// Maps a Unicode value that the Adobe Glyph List assigns to a Symbol glyph onto the code point where the substitute fonts
    /// have the matching shape. Symbol's glyph names resolve through the glyph list to a few values the substitutes do not carry
    /// at that address: the private-use bracket, brace and integral pieces (U+F8E5..U+F8FE) sit at U+239B.. and U+23D0 in
    /// Noto Sans Math, <c>Omega</c> and <c>mu</c> are the Greek letters, the angle brackets are the mathematical ones, and the
    /// serif/sans copyright, registered and trademark signs are the plain signs. Other values come back unchanged.
    /// </summary>
    public static int RemapSymbolCodePoint(int codePoint) => codePoint switch
    {
        0x2126 => 0x03A9, // Omega: OHM SIGN -> GREEK CAPITAL LETTER OMEGA
        0x00B5 => 0x03BC, // mu: MICRO SIGN -> GREEK SMALL LETTER MU
        0x2329 => 0x27E8, // angleleft
        0x232A => 0x27E9, // angleright
        0xF8E5 => 0x23AF, // radicalex
        0xF8E6 => 0x23D0, // arrowvertex
        0xF8E7 => 0x23AF, // arrowhorizex
        0xF8E8 or 0xF6DA => 0x00AE, // registersans, registerserif
        0xF8E9 or 0xF6D9 => 0x00A9, // copyrightsans, copyrightserif
        0xF8EA or 0xF6DB => 0x2122, // trademarksans, trademarkserif
        0xF8EB => 0x239B, // parenlefttp
        0xF8EC => 0x239C, // parenleftex
        0xF8ED => 0x239D, // parenleftbt
        0xF8EE => 0x23A1, // bracketlefttp
        0xF8EF => 0x23A2, // bracketleftex
        0xF8F0 => 0x23A3, // bracketleftbt
        0xF8F1 => 0x23A7, // bracelefttp
        0xF8F2 => 0x23A8, // braceleftmid
        0xF8F3 => 0x23A9, // braceleftbt
        0xF8F4 => 0x23AA, // braceex
        0xF8F5 => 0x23AE, // integralex
        0xF8F6 => 0x239E, // parenrighttp
        0xF8F7 => 0x239F, // parenrightex
        0xF8F8 => 0x23A0, // parenrightbt
        0xF8F9 => 0x23A4, // bracketrighttp
        0xF8FA => 0x23A5, // bracketrightex
        0xF8FB => 0x23A6, // bracketrightbt
        0xF8FC => 0x23AB, // bracerighttp
        0xF8FD => 0x23AC, // bracerightmid
        0xF8FE => 0x23AD, // bracerightbt
        _ => codePoint,
    };

    private enum Kind { Sans, Serif, Mono, Symbol, Zapf }

    private static bool Classify(string? name, out Kind kind, out bool bold, out bool italic)
    {
        kind = Kind.Sans;
        bold = italic = false;
        if (string.IsNullOrEmpty(name) || name.Length > 200)
        {
            return false;
        }

        string n = name;
        if (n.Length > 7 && n[6] == '+' && IsTag(n))
        {
            n = n[7..];
        }

        n = n.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

        // Family and style: "Arial,Bold" (comma first), "Arial-BoldMT", "Times-Roman"; "ArialBold" has no separator.
        int sep = n.IndexOfAny([',', '-']);
        string fam = sep >= 0 ? n[..sep] : n;
        string style = sep >= 0 ? n[(sep + 1)..] : string.Empty;

        // Style words glued onto the family ("ArialBold", "TimesNewRomanBoldItalic").
        while (true)
        {
            string? cut = null;
            foreach (string w in StyleWords)
            {
                if (fam.Length > w.Length && fam.EndsWith(w, StringComparison.Ordinal))
                {
                    cut = w;
                    break;
                }
            }

            if (cut is null)
            {
                break;
            }

            style = cut + style;
            fam = fam[..^cut.Length];
        }

        // Foundry suffixes: ArialMT, TimesNewRomanPSMT, CourierNewPS.
        foreach (string suffix in new[] { "psmt", "ps", "mt" })
        {
            if (fam.Length > suffix.Length && fam.EndsWith(suffix, StringComparison.Ordinal))
            {
                fam = fam[..^suffix.Length];
                break;
            }
        }

        switch (fam)
        {
            case "helvetica":
            case "arial": kind = Kind.Sans; break;
            case "times":
            case "timesroman":
            case "timesnewroman": kind = Kind.Serif; break;
            case "courier":
            case "couriernew": kind = Kind.Mono; break;
            case "symbol": kind = Kind.Symbol; return style.Length == 0 || style == "mt";
            case "zapfdingbats":
            case "itczapfdingbats": kind = Kind.Zapf; return style.Length == 0;
            default: return false;
        }

        // The style part must be made only of known words, so "Arial-Black" or "Courier-Narrow" do not match.
        string s = style;
        while (s.Length > 0)
        {
            string? hit = null;
            foreach (string w in StyleWords)
            {
                if (s.StartsWith(w, StringComparison.Ordinal))
                {
                    hit = w;
                    break;
                }
            }

            if (hit is null)
            {
                foreach (string w in NeutralWords)
                {
                    if (s.StartsWith(w, StringComparison.Ordinal))
                    {
                        hit = w;
                        break;
                    }
                }
            }

            if (hit is null)
            {
                return false;
            }

            bold |= hit.Contains("bold", StringComparison.Ordinal);
            italic |= hit.Contains("italic", StringComparison.Ordinal) || hit.Contains("oblique", StringComparison.Ordinal);
            s = s[hit.Length..];
        }

        return true;
    }

    // Longest first so "bolditalic" wins over "bold".
    private static readonly string[] StyleWords = ["bolditalic", "boldoblique", "bold", "italic", "oblique"];

    private static readonly string[] NeutralWords = ["roman", "regular", "normal", "psmt", "ps", "mt"];

    private static bool IsTag(string n)
    {
        for (int i = 0; i < 6; i++)
        {
            if (n[i] is < 'A' or > 'Z')
            {
                return false;
            }
        }

        return true;
    }

    // Order: regular, bold, italic, bold italic - index = (bold ? 1 : 0) | (italic ? 2 : 0).
    private static SubstituteFace[] Family(string file, string family) =>
    [
        new(file + "-Regular", family, "OnePdf.Fonts." + file + "-Regular.ttf", false, false),
        new(file + "-Bold", family, "OnePdf.Fonts." + file + "-Bold.ttf", true, false),
        new(file + "-Italic", family, "OnePdf.Fonts." + file + "-Italic.ttf", false, true),
        new(file + "-BoldItalic", family, "OnePdf.Fonts." + file + "-BoldItalic.ttf", true, true),
    ];

    private static SubstituteFace Single(string file, string family) =>
        new(file, family, "OnePdf.Fonts." + file + ".ttf", false, false);
}
