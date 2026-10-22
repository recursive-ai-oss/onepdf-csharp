using System.Text;
using OnePdf.Internal;

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

    /// <summary>The object number and generation this object was read under, or null for direct objects and for indirect nulls.</summary>
    public PdfReference? Reference { get; internal set; }

    /// <summary>The byte offset of the object's <c>n g obj</c> header in the file, or -1 for direct objects and for objects read from an object stream.</summary>
    public long SourceOffset { get; internal set; } = -1;

    /// <summary>The object stream's object number when this object was stored in one, otherwise -1.</summary>
    public int ContainerObjectNumber { get; internal set; } = -1;

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
    internal PdfBoolean(bool value) => Value = value;

    /// <summary>The value.</summary>
    public bool Value { get; }
}

/// <summary>An integer. Values beyond the 64-bit range are read as <see cref="PdfReal"/>.</summary>
public sealed class PdfInteger : PdfObject
{
    internal PdfInteger(long value) => Value = value;

    /// <summary>The value.</summary>
    public long Value { get; }
}

/// <summary>A real number.</summary>
public sealed class PdfReal : PdfObject
{
    internal PdfReal(double value) => Value = value;

    /// <summary>The value.</summary>
    public double Value { get; }
}

/// <summary>A string: literal or hexadecimal in the file, always raw bytes here.</summary>
public sealed class PdfString : PdfObject
{
    internal PdfString(byte[] bytes) => Bytes = bytes;

    /// <summary>The string's bytes after escape and hexadecimal decoding. Do not modify.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>
    /// Decodes the bytes as a text string (ISO 32000-2 7.9.2.2): UTF-16BE or UTF-16LE with a byte-order mark, UTF-8 with a
    /// byte-order mark, otherwise PDFDocEncoding approximated by Latin-1.
    /// </summary>
    public string GetText()
    {
        ReadOnlySpan<byte> b = Bytes.Span;
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(b[2..]);
        }

        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(b[2..]);
        }

        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(b[3..]);
        }

        return Encoding.Latin1.GetString(b);
    }

    /// <inheritdoc/>
    public override string ToString() => GetText();
}

/// <summary>A name object, with <c>#xx</c> escapes decoded. Bytes that are not valid UTF-8 are read as Latin-1.</summary>
public sealed class PdfName : PdfObject
{
    internal PdfName(string value) => Value = value;

    /// <summary>The name without the leading slash.</summary>
    public string Value { get; }

    /// <inheritdoc/>
    public override string ToString() => "/" + Value;
}

/// <summary>An indirect reference (<c>n g R</c>). Use <see cref="PdfDocument.Resolve(PdfObject?)"/> or the resolving getters of containers to follow it.</summary>
public sealed class PdfReference : PdfObject
{
    internal PdfReference(int number, int generation)
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
