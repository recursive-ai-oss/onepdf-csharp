using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using OnePdf.PdfFonts;

namespace OnePdf.Appearance;

/// <summary>A font a generated appearance can write with: its dictionary and a table from Unicode scalar to single-byte code.</summary>
internal sealed class AppearanceFont
{
    private readonly Dictionary<int, (byte Code, double Advance)> _map = [];

    public AppearanceFont(PdfDictionary dictionary, PdfFont font)
    {
        Dictionary = dictionary;
        // Only simple (single-byte) fonts can be written to: a composite font needs CIDs a Unicode string cannot name.
        Usable = !font.IsComposite && !font.IsType3;
        if (!Usable)
        {
            return;
        }

        Span<byte> one = stackalloc byte[1];
        for (int code = 0; code < 256; code++)
        {
            one[0] = (byte)code;
            int next = font.Next(one, 0, out FontGlyph? g);
            if (g is null || next <= 0 || !g.HasUnicode || g.Unicode.Length == 0)
            {
                continue;
            }

            Rune r;
            if (!Rune.TryGetRuneAt(g.Unicode, 0, out r) || r.Utf16SequenceLength != g.Unicode.Length)
            {
                continue;
            }

            if (g.Advance > 0 || r.Value == ' ')
            {
                _map.TryAdd(r.Value, ((byte)code, g.Advance));
            }
        }
    }

    public PdfDictionary Dictionary { get; }

    public bool Usable { get; }

    public bool TryEncode(int scalar, out byte code, out double advance)
    {
        if (_map.TryGetValue(scalar, out (byte Code, double Advance) e))
        {
            code = e.Code;
            advance = e.Advance;
            return true;
        }

        code = 0;
        advance = 0;
        return false;
    }
}

/// <summary>Per-document state shared by all generations: loaded appearance fonts and the standard 14 font dictionaries.</summary>
internal sealed class AppearanceCache
{
    private const int MaxFonts = 256;
    private static readonly ConditionalWeakTable<PdfDocument, AppearanceCache> s_caches = new();

    private readonly ConcurrentDictionary<PdfDictionary, AppearanceFont> _fonts = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<string, PdfDictionary> _standard = new(StringComparer.Ordinal);

    public static AppearanceCache For(PdfDocument doc) => s_caches.GetValue(doc, static _ => new AppearanceCache());

    public AppearanceFont Get(PdfDocument doc, PdfDictionary fontDictionary)
    {
        if (_fonts.TryGetValue(fontDictionary, out AppearanceFont? hit))
        {
            return hit;
        }

        var f = new AppearanceFont(fontDictionary, doc.GetFont(fontDictionary));
        if (_fonts.Count < MaxFonts)
        {
            _fonts.TryAdd(fontDictionary, f);
        }

        return f;
    }

    /// <summary>A direct standard 14 font dictionary (WinAnsi for the text faces; Symbol and ZapfDingbats keep their own encodings).</summary>
    public PdfDictionary Standard(string baseFont) => _standard.GetOrAdd(baseFont, static name =>
    {
        PdfDictionary d = Cos.Dict()
            .With("Type", Cos.Name("Font"))
            .With("Subtype", Cos.Name("Type1"))
            .With("BaseFont", Cos.Name(name));
        if (name is not ("Symbol" or "ZapfDingbats"))
        {
            d.Set("Encoding", Cos.Name("WinAnsiEncoding"));
        }

        return d;
    });

    /// <summary>The standard 14 font a conventional <c>/DA</c> font name stands for (Helv, TiRo, Cour, ZaDb, Symb and their bold/italic forms), else Helvetica.</summary>
    public static string StandardNameFor(string? alias)
    {
        string a = alias ?? string.Empty;
        string lower = a.ToLowerInvariant();
        if (lower.StartsWith("zadb", StringComparison.Ordinal) || lower.Contains("dingbat", StringComparison.Ordinal))
        {
            return "ZapfDingbats";
        }

        if (lower.StartsWith("symb", StringComparison.Ordinal) || lower == "symbol")
        {
            return "Symbol";
        }

        bool bold = lower.Contains("bold", StringComparison.Ordinal) || lower.EndsWith("bo", StringComparison.Ordinal) || lower.EndsWith("bd", StringComparison.Ordinal);
        bool italic = lower.Contains("ital", StringComparison.Ordinal) || lower.Contains("oblique", StringComparison.Ordinal) || lower.EndsWith("it", StringComparison.Ordinal) || lower.EndsWith("ob", StringComparison.Ordinal);
        if (lower.StartsWith("ti", StringComparison.Ordinal) || lower.Contains("times", StringComparison.Ordinal))
        {
            return bold ? (italic ? "Times-BoldItalic" : "Times-Bold") : (italic ? "Times-Italic" : "Times-Roman");
        }

        if (lower.StartsWith("co", StringComparison.Ordinal) || lower.Contains("courier", StringComparison.Ordinal))
        {
            return bold ? (italic ? "Courier-BoldOblique" : "Courier-Bold") : (italic ? "Courier-Oblique" : "Courier");
        }

        return bold ? (italic ? "Helvetica-BoldOblique" : "Helvetica-Bold") : (italic ? "Helvetica-Oblique" : "Helvetica");
    }
}

/// <summary>One shaped character: which font of the chain, the single-byte code, its advance at size 1, or a line break.</summary>
internal readonly record struct ShapedGlyph(int Font, byte Code, double Advance, bool IsSpace, bool IsBreak);

/// <summary>A primary font plus fallbacks for characters it cannot encode.</summary>
internal sealed class FontChain
{
    private readonly List<AppearanceFont> _fonts = [];
    private readonly List<string> _aliases = [];

    public FontChain(AppearanceFont primary, AppearanceFont[] fallbacks)
    {
        _fonts.Add(primary);
        foreach (AppearanceFont f in fallbacks)
        {
            if (!_fonts.Contains(f))
            {
                _fonts.Add(f);
            }
        }

        for (int i = 0; i < _fonts.Count; i++)
        {
            _aliases.Add("F" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    public int Count => _fonts.Count;

    public AppearanceFont this[int i] => _fonts[i];

    public string Alias(int i) => _aliases[i];

    /// <summary>The characters that no font of the chain could encode in the last <see cref="Shape"/> call.</summary>
    public int Dropped { get; private set; }

    /// <summary>
    /// Turns text into glyphs. Line breaks (CR, LF, CRLF) become break glyphs when <paramref name="allowBreaks"/>, otherwise
    /// spaces; tabs become spaces; other control characters are dropped silently; characters no font can encode count in
    /// <see cref="Dropped"/>. At most <paramref name="maxChars"/> characters are read.
    /// </summary>
    public List<ShapedGlyph> Shape(string text, int maxChars, bool allowBreaks)
    {
        Dropped = 0;
        var list = new List<ShapedGlyph>(Math.Min(text.Length, maxChars));
        int read = 0;
        for (int i = 0; i < text.Length && read < maxChars; read++)
        {
            Rune.TryGetRuneAt(text, i, out Rune r);
            if (r.Value is '\r' or '\n')
            {
                if (r.Value == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                i++;
                list.Add(allowBreaks ? new ShapedGlyph(0, 0, 0, false, true) : SpaceGlyph());
                continue;
            }

            i += r.Utf16SequenceLength;
            int v = r.Value == '\t' ? ' ' : r.Value;
            if (v < 32 || v == 127 || (v >= 0x80 && v < 0xA0) || r.Value is 0xFFFD)
            {
                if (v >= 0xA0 || v == 0xFFFD)
                {
                    Dropped++;
                }

                continue;
            }

            bool found = false;
            for (int f = 0; f < _fonts.Count; f++)
            {
                if (_fonts[f].Usable && (_fonts[f].TryEncode(v, out byte code, out double adv) || _fonts[f].TryEncode(Equivalent(v), out code, out adv)))
                {
                    list.Add(new ShapedGlyph(f, code, adv, v == ' ', false));
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                Dropped++;
            }
        }

        return list;
    }

    /// <summary>Characters the Adobe glyph list names with a different code point than Unicode's letter (Omega as Ohm, Delta as Increment, micro as mu).</summary>
    private static int Equivalent(int v) => v switch
    {
        0x3A9 => 0x2126,
        0x2126 => 0x3A9,
        0x394 => 0x2206,
        0x2206 => 0x394,
        0xB5 => 0x3BC,
        0x3BC => 0xB5,
        _ => v,
    };

    private ShapedGlyph SpaceGlyph()
    {
        for (int f = 0; f < _fonts.Count; f++)
        {
            if (_fonts[f].Usable && _fonts[f].TryEncode(' ', out byte code, out double adv))
            {
                return new ShapedGlyph(f, code, adv, true, false);
            }
        }

        return new ShapedGlyph(0, 32, 0.278, true, false);
    }
}
