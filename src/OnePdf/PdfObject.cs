using System.Text;
using OnePdf.Internal;
using OnePdf.Security;

namespace OnePdf;

/// <summary>
/// Base of the COS (Carousel Object System) object model: the eight basic PDF object types, plus streams.
/// Objects read from a file are immutable. Indirect objects remember their identity and where they were read from, so a
/// writer can later reuse the original bytes.
/// </summary>
public abstract class PdfObject
{
    internal PdfObject()
    {
    }

    private PdfReference? _reference;
    private long _sourceOffset = -1;

    /// <summary>
    /// The object number and generation this object was read under, or null for direct objects and for indirect nulls.
    /// A stream's dictionary reports the same reference as the stream itself: in a file they are one indirect object
    /// (ISO 32000-2 7.3.8), so code that identifies objects through <see cref="PdfDictionary"/> sees it as the same object.
    /// </summary>
    public PdfReference? Reference
    {
        get => _reference;
        internal set
        {
            _reference = value;
            if (this is PdfStream s)
            {
                s.Dictionary.Reference = value;
            }
        }
    }

    /// <summary>The byte offset of the object's <c>n g obj</c> header in the file, or -1 for direct objects and for objects read from an object stream. A stream's dictionary reports the stream's offset.</summary>
    public long SourceOffset
    {
        get => _sourceOffset;
        internal set
        {
            _sourceOffset = value;
            if (this is PdfStream s)
            {
                s.Dictionary.SourceOffset = value;
            }
        }
    }

    /// <summary>The object stream's object number when this object was stored in one, otherwise -1.</summary>
    public int ContainerObjectNumber { get; internal set; } = -1;

    // The number of the indirect object this object is stored inside, plus one (zero: none, or not tracked). Only filled in
    // for documents opened for editing, where a change to a nested object must mark its owner for saving.
    internal int OwnerPlusOne;

    internal static PdfObject? Unwrap(PdfObject? o, PdfResolver? resolver)
    {
        if (o is PdfReference r)
        {
            o = resolver?.Resolve(r);
        }

        return o is null or PdfNull ? null : o;
    }
}

/// <summary>The null object. Dictionary entries whose value is null are treated as absent.</summary>
public sealed class PdfNull : PdfObject
{
    private PdfNull()
    {
    }

    /// <summary>The only instance.</summary>
    public static PdfNull Instance { get; } = new();
}

/// <summary>A boolean.</summary>
public sealed class PdfBoolean : PdfObject
{
    /// <summary>Creates the object.</summary>
    public PdfBoolean(bool value) => Value = value;

    /// <summary>The value.</summary>
    public bool Value { get; }
}

/// <summary>An integer. Values beyond the 64-bit range are read as <see cref="PdfReal"/>.</summary>
public sealed class PdfInteger : PdfObject
{
    /// <summary>Creates the object.</summary>
    public PdfInteger(long value) => Value = value;

    /// <summary>The value.</summary>
    public long Value { get; }
}

/// <summary>A real number.</summary>
public sealed class PdfReal : PdfObject
{
    /// <summary>Creates the object.</summary>
    public PdfReal(double value) => Value = value;

    /// <summary>The value.</summary>
    public double Value { get; }
}

/// <summary>A string: literal or hexadecimal in the file, always raw bytes here.</summary>
public sealed class PdfString : PdfObject
{
    /// <summary>Creates the object.</summary>
    public PdfString(byte[] bytes) => Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));

    // What was wrong with the ciphertext this string was decrypted from (bad padding, a partial last block); None for every other string.
    // The bytes are then only what decrypted. A conformance save must not write them as if they were the original text.
    internal DecryptIssues DecryptDamage { get; init; }

    /// <summary>
    /// Encodes <paramref name="text"/> as a PDF text string (ISO 32000-2 7.9.2.2): PDFDocEncoding when every character has a code
    /// there that equals Latin-1 (printable ASCII and U+00A1 to U+00FF except U+00AD), otherwise UTF-16BE with a byte-order mark.
    /// </summary>
    public static PdfString FromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        bool simple = true;
        foreach (char c in text)
        {
            if (!(c is >= ' ' and <= '~' or '\n' or '\r' or '\t' || (c >= '\u00A1' && c <= '\u00FF' && c != '\u00AD')))
            {
                simple = false;
                break;
            }
        }

        if (simple)
        {
            return new PdfString(Encoding.Latin1.GetBytes(text));
        }

        byte[] utf16 = new byte[2 + (text.Length * 2)];
        utf16[0] = 0xFE;
        utf16[1] = 0xFF;
        Encoding.BigEndianUnicode.GetBytes(text, 0, text.Length, utf16, 2);
        return new PdfString(utf16);
    }

    /// <summary>The string's bytes after escape and hexadecimal decoding. Do not modify.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>
    /// Decodes the bytes as a text string (ISO 32000-2 7.9.2.2): UTF-16BE or UTF-16LE with a byte-order mark, UTF-8 with a
    /// byte-order mark (PDF 2.0), otherwise PDFDocEncoding (Annex D.2). Bytes PDFDocEncoding leaves undefined decode as U+FFFD.
    /// Language escape sequences (U+001B, a language code, U+001B) in UTF-16 text are removed. Never throws.
    /// </summary>
    public string GetText()
    {
        ReadOnlySpan<byte> b = Bytes.Span;
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
        {
            return StripLanguageEscapes(Encoding.BigEndianUnicode.GetString(b[2..]));
        }

        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
        {
            return StripLanguageEscapes(Encoding.Unicode.GetString(b[2..]));
        }

        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(b[3..]);
        }

        return string.Create(b.Length, Bytes, static (dest, bytes) =>
        {
            ReadOnlySpan<byte> src = bytes.Span;
            for (int i = 0; i < src.Length; i++)
            {
                dest[i] = PdfDocChar(src[i]);
            }
        });
    }

    private static string StripLanguageEscapes(string s)
    {
        int e = s.IndexOf('\u001b');
        if (e < 0)
        {
            return s;
        }

        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] == '\u001b')
            {
                int close = s.IndexOf('\u001b', i + 1);
                i = close < 0 ? s.Length : close + 1;
                continue;
            }

            sb.Append(s[i++]);
        }

        return sb.ToString();
    }

    // ISO 32000-2 Annex D.2: PDFDocEncoding is Latin-1 except for these ranges.
    private const string PdfDocHigh = "\u2022\u2020\u2021\u2026\u2014\u2013\u0192\u2044\u2039\u203A\u2212\u2030\u201E\u201C\u201D\u2018\u2019\u201A\u2122\uFB01\uFB02\u0141\u0152\u0160\u0178\u017D\u0131\u0142\u0153\u0161\u017E\uFFFD\u20AC";
    private const string PdfDocLow = "\u02D8\u02C7\u02C6\u02D9\u02DD\u02DB\u02DA\u02DC";

    internal static char PdfDocChar(byte c) => c switch
    {
        >= 0x18 and <= 0x1F => PdfDocLow[c - 0x18],
        >= 0x80 and <= 0xA0 => PdfDocHigh[c - 0x80],
        0xAD => '\uFFFD',
        _ => (char)c,
    };

    /// <inheritdoc/>
    public override string ToString() => GetText();
}

/// <summary>A name object, with <c>#xx</c> escapes decoded. Bytes that are not valid UTF-8 are read as Latin-1.</summary>
public sealed class PdfName : PdfObject
{
    /// <summary>Creates the object.</summary>
    public PdfName(string value) => Value = value;

    /// <summary>The name without the leading slash.</summary>
    public string Value { get; }

    /// <inheritdoc/>
    public override string ToString() => "/" + Value;
}

/// <summary>An indirect reference (<c>n g R</c>). Use <see cref="PdfDocument.Resolve(PdfObject?)"/> or the resolving getters of containers to follow it.</summary>
public sealed class PdfReference : PdfObject
{
    /// <summary>Creates a reference to object <paramref name="number"/> of <paramref name="generation"/>.</summary>
    public PdfReference(int number, int generation)
    {
        Number = number;
        Generation = generation;
    }

    /// <summary>The object number.</summary>
    public int Number { get; }

    /// <summary>The generation number.</summary>
    public int Generation { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{Number} {Generation} R";
}
