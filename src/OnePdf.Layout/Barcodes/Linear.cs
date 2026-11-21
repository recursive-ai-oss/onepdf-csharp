using System.Globalization;
using System.Text;

namespace OnePdf.Layout.Barcodes;

/// <summary>Where a piece of human-readable text goes under a linear symbol: centred between two module positions, counted from the first bar (the quiet zone is negative).</summary>
internal readonly record struct TextGroup(string Text, double FromModule, double ToModule);

/// <summary>A linear symbol as modules (true is a bar), plus the bars that run lower than the rest and the text that belongs under it.</summary>
internal sealed class LinearSymbol
{
    public required bool[] Modules { get; init; }

    /// <summary>Per module: the bar runs below the others (the guard bars of EAN and UPC). Null when none do.</summary>
    public bool[]? Extended { get; init; }

    public required double LeftQuiet { get; init; }

    public required double RightQuiet { get; init; }

    /// <summary>Text groups for the digit layout of the standard; when null the whole <see cref="Text"/> is centred under the bars.</summary>
    public TextGroup[]? Groups { get; init; }

    /// <summary>The characters shown under the symbol when it has no <see cref="Groups"/>.</summary>
    public required string Text { get; init; }

    /// <summary>What a reader reports: the data, with check digits added.</summary>
    public required string Data { get; init; }
}

internal static class Code128
{
    // ISO/IEC 15417 Table 2: bar and space widths of each symbol character, bar first. Values 0..105, then the stop character.
    private static readonly string[] Patterns =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112",
    ];

    private const int Fnc1 = -1;

    internal static string Pattern(int value) => Patterns[value];

    public static LinearSymbol Create(string value, bool gs1, double? quiet)
    {
        int[] tokens;
        string text;
        if (gs1)
        {
            tokens = ParseGs1(value, out text);
        }
        else
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new PdfBarcodeException(BarcodeSymbology.Code128, "A Code 128 barcode needs a value to encode.");
            }

            tokens = new int[value.Length];
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] > 127)
                {
                    throw new PdfBarcodeException(BarcodeSymbology.Code128, $"Code 128 encodes characters 0 to 127; U+{(int)value[i]:X4} at position {i} is outside that range.");
                }

                tokens[i] = value[i];
            }

            text = value;
        }

        int[] values = Encode(tokens);
        var modules = new List<bool>(values.Length * 11 + 2);
        foreach (int v in values)
        {
            AppendPattern(modules, Patterns[v]);
        }

        AppendPattern(modules, Patterns[106]);
        double q = quiet ?? 10;
        return new LinearSymbol { Modules = [.. modules], LeftQuiet = q, RightQuiet = q, Text = text, Data = text };
    }

    private static void AppendPattern(List<bool> modules, string pattern)
    {
        bool bar = true;
        foreach (char c in pattern)
        {
            for (int i = 0; i < c - '0'; i++)
            {
                modules.Add(bar);
            }

            bar = !bar;
        }
    }

    internal static bool AllDigits(string s)
    {
        foreach (char c in s)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool InA(int c) => c is >= 0 and <= 95;

    private static bool InB(int c) => c is >= 32 and <= 127;

    private static bool Digit(int c) => c is >= '0' and <= '9';

    /// <summary>
    /// The symbol character values for the tokens (characters, or FNC1 as -1): start character, data, check character, with the code sets
    /// chosen to give the fewest symbol characters (ISO/IEC 15417 clause 4.4 allows any switching; the shortest is what readers expect).
    /// </summary>
    internal static int[] Encode(int[] t)
    {
        int n = t.Length;
        const int Inf = int.MaxValue / 2;
        var best = new int[n + 2, 3];
        var parI = new int[n + 2, 3];
        var parS = new int[n + 2, 3];
        var kind = new byte[n + 2, 3];
        for (int i = 0; i <= n + 1; i++)
        {
            for (int s = 0; s < 3; s++)
            {
                best[i, s] = Inf;
            }
        }

        for (int s = 0; s < 3; s++)
        {
            best[0, s] = 1;
            kind[0, s] = 0;
        }

        for (int i = 0; i <= n; i++)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                for (int s = 0; s < 3; s++)
                {
                    for (int u = 0; u < 3; u++)
                    {
                        if (u != s && best[i, s] + 1 < best[i, u])
                        {
                            best[i, u] = best[i, s] + 1;
                            parI[i, u] = i;
                            parS[i, u] = s;
                            kind[i, u] = 1;
                        }
                    }
                }
            }

            if (i == n)
            {
                break;
            }

            int c = t[i];
            for (int s = 0; s < 3; s++)
            {
                int b = best[i, s];
                if (b >= Inf)
                {
                    continue;
                }

                if (s == 2)
                {
                    if (c == Fnc1)
                    {
                        Relax(i + 1, 2, b + 1, i, s, 2);
                    }
                    else if (Digit(c) && i + 1 < n && Digit(t[i + 1]))
                    {
                        Relax(i + 2, 2, b + 1, i, s, 3);
                    }

                    continue;
                }

                bool here = c == Fnc1 || (s == 0 ? InA(c) : InB(c));
                bool other = c != Fnc1 && (s == 0 ? InB(c) : InA(c));
                if (here)
                {
                    Relax(i + 1, s, b + 1, i, s, 2);
                }
                else if (other)
                {
                    Relax(i + 1, s, b + 2, i, s, 4);
                }
            }
        }

        void Relax(int ni, int ns, int cost, int fromI, int fromS, byte k)
        {
            if (cost < best[ni, ns])
            {
                best[ni, ns] = cost;
                parI[ni, ns] = fromI;
                parS[ni, ns] = fromS;
                kind[ni, ns] = k;
            }
        }

        int endSet = 0;
        for (int s = 1; s < 3; s++)
        {
            if (best[n, s] < best[n, endSet])
            {
                endSet = s;
            }
        }

        if (best[n, endSet] >= Inf)
        {
            throw new PdfBarcodeException(BarcodeSymbology.Code128, "The value cannot be encoded in Code 128.");
        }

        // Walk back, collecting symbol characters in reverse.
        var rev = new List<int>();
        int ci = n;
        int cs = endSet;
        while (true)
        {
            byte k = kind[ci, cs];
            if (k == 0 && ci == 0)
            {
                rev.Add(103 + cs);
                break;
            }

            int pi = parI[ci, cs];
            int ps = parS[ci, cs];
            switch (k)
            {
                case 1:
                    rev.Add(cs == 2 ? 99 : cs == 1 ? 100 : 101);
                    break;
                case 2:
                    rev.Add(CharValue(t[pi], cs));
                    break;
                case 3:
                    rev.Add(((t[pi] - '0') * 10) + (t[pi + 1] - '0'));
                    break;
                default:
                    rev.Add(CharValue(t[pi], ps == 0 ? 1 : 0));
                    rev.Add(98);
                    break;
            }

            ci = pi;
            cs = ps;
        }

        rev.Reverse();
        long sum = rev[0];
        for (int i = 1; i < rev.Count; i++)
        {
            sum += (long)i * rev[i];
        }

        rev.Add((int)(sum % 103));
        return [.. rev];
    }

    private static int CharValue(int c, int set)
    {
        if (c == Fnc1)
        {
            return 102;
        }

        return set == 0 && c < 32 ? c + 64 : c - 32;
    }

    // GS1 General Specifications 5.4.7: application identifiers whose data has a fixed length need no FNC1 after them.
    private static int FixedLength(string ai)
    {
        if (ai.Length < 2)
        {
            return -1;
        }

        int two = ((ai[0] - '0') * 10) + (ai[1] - '0');
        return two switch
        {
            0 => 18,
            1 or 2 => 14,
            11 or 12 or 13 or 15 or 16 or 17 => 6,
            20 => 2,
            >= 31 and <= 36 => 6,
            41 => 13,
            _ => -1,
        };
    }

    internal static int[] ParseGs1(string value, out string text)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new PdfBarcodeException(BarcodeSymbology.Gs1128, "A GS1-128 barcode needs a value to encode.");
        }

        var tokens = new List<int> { Fnc1 };
        var shown = new StringBuilder();
        int pos = 0;
        bool needFnc1 = false;
        while (pos < value.Length)
        {
            if (value[pos] != '(')
            {
                throw new PdfBarcodeException(BarcodeSymbology.Gs1128, $"Expected an application identifier in parentheses at position {pos}; write the value as (01)12345678901231(10)ABC.");
            }

            int close = value.IndexOf(')', pos);
            if (close < 0)
            {
                throw new PdfBarcodeException(BarcodeSymbology.Gs1128, "An application identifier is missing its closing parenthesis.");
            }

            string ai = value.Substring(pos + 1, close - pos - 1);
            if (ai.Length is < 2 or > 4 || !AllDigits(ai))
            {
                throw new PdfBarcodeException(BarcodeSymbology.Gs1128, $"'{ai}' is not an application identifier (2 to 4 digits).");
            }

            int next = value.IndexOf('(', close + 1);
            string data = next < 0 ? value[(close + 1)..] : value[(close + 1)..next];
            if (data.Length == 0)
            {
                throw new PdfBarcodeException(BarcodeSymbology.Gs1128, $"Application identifier ({ai}) has no data.");
            }

            foreach (char ch in data)
            {
                if (ch is < ' ' or > '~')
                {
                    throw new PdfBarcodeException(BarcodeSymbology.Gs1128, $"U+{(int)ch:X4} in the data of ({ai}) cannot be encoded.");
                }
            }

            int fixedLen = FixedLength(ai);
            if (fixedLen >= 0)
            {
                // The length counts the data after the identifier; 31xx to 36xx carry their decimal point in the identifier.
                if (data.Length != fixedLen || !AllDigits(data))
                {
                    throw new PdfBarcodeException(BarcodeSymbology.Gs1128, $"The data of ({ai}) must be {fixedLen} digits.");
                }
            }

            if (needFnc1)
            {
                tokens.Add(Fnc1);
            }

            foreach (char ch in ai)
            {
                tokens.Add(ch);
            }

            foreach (char ch in data)
            {
                tokens.Add(ch);
            }

            shown.Append('(').Append(ai).Append(')').Append(data);
            needFnc1 = fixedLen < 0;
            pos = next < 0 ? value.Length : next;
        }

        text = shown.ToString();
        return [.. tokens];
    }
}

internal static class Retail
{
    private static readonly string[] L = ["0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011", "0110111", "0001011"];
    private static readonly string[] G = ["0100111", "0110011", "0011011", "0100001", "0011101", "0111001", "0000101", "0010001", "0001001", "0010111"];

    // ISO/IEC 15420 Table 4: the first digit of an EAN-13 selects which of the six left digits use set G.
    private static readonly string[] Parity = ["LLLLLL", "LLGLGG", "LLGGLG", "LLGGGL", "LGLLGG", "LGGLLG", "LGGGLL", "LGLGLG", "LGLGGL", "LGGLGL"];

    /// <summary>The check digit for digits of an EAN/UPC number without it: weights 3 and 1 alternating from the right (ISO/IEC 15420 7.2.1).</summary>
    internal static int CheckDigit(ReadOnlySpan<char> digits)
    {
        int sum = 0;
        for (int i = 0; i < digits.Length; i++)
        {
            int d = digits[i] - '0';
            int fromRight = digits.Length - i;
            sum += fromRight % 2 == 1 ? d * 3 : d;
        }

        return (10 - (sum % 10)) % 10;
    }

    private static string Digits(BarcodeSymbology sym, string value, int without)
    {
        if (value is null || (value.Length != without && value.Length != without + 1) || !Code128.AllDigits(value))
        {
            throw new PdfBarcodeException(sym, $"{sym} takes {without} digits (the check digit is added) or {without + 1} (it is verified); got '{value}'.");
        }

        int l = value.Length;
        int check = CheckDigit(value.AsSpan(0, without));
        if (l == without + 1 && value[without] - '0' != check)
        {
            throw new PdfBarcodeException(sym, $"The check digit of '{value}' is {value[without]}, but the digits before it give {check}.");
        }

        return string.Concat(value.AsSpan(0, without), check.ToString(CultureInfo.InvariantCulture));
    }

    private static void Add(List<bool> m, string bits)
    {
        foreach (char c in bits)
        {
            m.Add(c == '1');
        }
    }

    private static string Complement(string s)
    {
        var c = new char[s.Length];
        for (int i = 0; i < c.Length; i++)
        {
            c[i] = s[i] == '1' ? '0' : '1';
        }

        return new string(c);
    }

    private static void Right(List<bool> m, int d) => Add(m, Complement(L[d]));

    public static LinearSymbol Ean13(string value, double? quiet, bool withText, bool upc)
    {
        BarcodeSymbology sym = upc ? BarcodeSymbology.UpcA : BarcodeSymbology.Ean13;
        string full = upc ? Digits(sym, value, 11) : Digits(sym, value, 12);
        string ean = upc ? "0" + full : full;
        int first = ean[0] - '0';
        var m = new List<bool>(95);
        Add(m, "101");
        string parity = Parity[first];
        for (int i = 0; i < 6; i++)
        {
            int d = ean[i + 1] - '0';
            Add(m, parity[i] == 'L' ? L[d] : G[d]);
        }

        Add(m, "01010");
        for (int i = 7; i < 13; i++)
        {
            Right(m, ean[i] - '0');
        }

        Add(m, "101");

        // Guard bars run lower than the digit bars; in UPC-A so do the bars of the number system and check digits at either end.
        var extended = new bool[m.Count];
        void Tall(int from, int to)
        {
            for (int i = from; i < to; i++)
            {
                extended[i] = true;
            }
        }

        Tall(0, 3);
        Tall(45, 50);
        Tall(92, 95);
        if (upc)
        {
            Tall(3, 10);
            Tall(85, 92);
        }

        double left = quiet ?? (upc ? 9 : 11);
        double right = quiet ?? (upc ? 9 : 7);
        TextGroup[]? groups = null;
        if (withText)
        {
            // The digit outside the bars sits in the quiet zone; it needs room, so a narrower zone is widened (documented on Barcode.QuietZone).
            left = Math.Max(left, 7);
            if (upc)
            {
                right = Math.Max(right, 7);
                groups =
                [
                    new TextGroup(full[..1], -left, -1),
                    new TextGroup(full.Substring(1, 5), 10, 45),
                    new TextGroup(full.Substring(6, 5), 50, 85),
                    new TextGroup(full.Substring(11, 1), 96, 95 + right),
                ];
            }
            else
            {
                groups =
                [
                    new TextGroup(ean[..1], -left, -1),
                    new TextGroup(ean.Substring(1, 6), 3, 45),
                    new TextGroup(ean.Substring(7, 6), 50, 92),
                ];
            }
        }

        return new LinearSymbol { Modules = [.. m], Extended = extended, LeftQuiet = left, RightQuiet = right, Groups = groups, Text = full, Data = upc ? full : ean };
    }

    public static LinearSymbol Ean8(string value, double? quiet, bool withText)
    {
        string full = Digits(BarcodeSymbology.Ean8, value, 7);
        var m = new List<bool>(67);
        Add(m, "101");
        for (int i = 0; i < 4; i++)
        {
            Add(m, L[full[i] - '0']);
        }

        Add(m, "01010");
        for (int i = 4; i < 8; i++)
        {
            Right(m, full[i] - '0');
        }

        Add(m, "101");
        var extended = new bool[m.Count];
        foreach (int i in new[] { 0, 1, 2, 31, 32, 33, 34, 35, 64, 65, 66 })
        {
            extended[i] = true;
        }

        double q = quiet ?? 7;
        TextGroup[]? groups = withText ? [new TextGroup(full[..4], 3, 31), new TextGroup(full[4..], 36, 64)] : null;
        return new LinearSymbol { Modules = [.. m], Extended = extended, LeftQuiet = q, RightQuiet = q, Groups = groups, Text = full, Data = full };
    }
}

internal static class Code39
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%";

    // ISO/IEC 16388 Table 1: five bars and four spaces per character, in order starting with a bar; W is wide, N narrow.
    private static readonly string[] Patterns =
    [
        "NNNWWNWNN", "WNNWNNNNW", "NNWWNNNNW", "WNWWNNNNN", "NNNWWNNNW", "WNNWWNNNN", "NNWWWNNNN", "NNNWNNWNW", "WNNWNNWNN", "NNWWNNWNN",
        "WNNNNWNNW", "NNWNNWNNW", "WNWNNWNNN", "NNNNWWNNW", "WNNNWWNNN", "NNWNWWNNN", "NNNNNWWNW", "WNNNNWWNN", "NNWNNWWNN", "NNNNWWWNN",
        "WNNNNNNWW", "NNWNNNNWW", "WNWNNNNWN", "NNNNWNNWW", "WNNNWNNWN", "NNWNWNNWN", "NNNNNNWWW", "WNNNNNWWN", "NNWNNNWWN", "NNNNWNWWN",
        "WWNNNNNNW", "NWWNNNNNW", "WWWNNNNNN", "NWNNWNNNW", "WWNNWNNNN", "NWWNWNNNN",
        "NWNNNNWNW", "WWNNNNWNN", "NWWNNNWNN", "NWNWNWNNN", "NWNWNNNWN", "NWNNNWNWN", "NNNWNWNWN",
    ];

    private const string Star = "NWNNWNWNN";

    internal static string PatternOf(char c) => Patterns[Alphabet.IndexOf(c, StringComparison.Ordinal)];

    public static LinearSymbol Create(string value, bool checkDigit, double? quiet)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new PdfBarcodeException(BarcodeSymbology.Code39, "A Code 39 barcode needs a value to encode.");
        }

        int sum = 0;
        foreach (char c in value)
        {
            int idx = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (idx < 0)
            {
                throw new PdfBarcodeException(BarcodeSymbology.Code39, $"'{c}' (U+{(int)c:X4}) is not in the Code 39 character set (0-9, A-Z, - . space $ / + %).");
            }

            sum += idx;
        }

        string data = value;
        if (checkDigit)
        {
            data += Alphabet[sum % 43];
        }

        var m = new List<bool>();
        void Char(string pattern, bool gap)
        {
            if (gap)
            {
                m.Add(false);
            }

            bool bar = true;
            foreach (char w in pattern)
            {
                int width = w == 'W' ? 3 : 1;
                for (int i = 0; i < width; i++)
                {
                    m.Add(bar);
                }

                bar = !bar;
            }
        }

        Char(Star, false);
        foreach (char c in data)
        {
            Char(PatternOf(c), true);
        }

        Char(Star, true);
        double q = quiet ?? 10;
        return new LinearSymbol { Modules = [.. m], LeftQuiet = q, RightQuiet = q, Text = data, Data = data };
    }
}
