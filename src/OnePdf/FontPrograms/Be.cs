using System.Buffers.Binary;

namespace OnePdf.FontPrograms;

/// <summary>A font program, CMap or font-data table is malformed or exceeds a parsing bound. Internal: the font layer maps it to a PDF diagnostic or format error.</summary>
internal sealed class FontProgramException : Exception
{
    public FontProgramException(string message)
        : base(message)
    {
    }

    public FontProgramException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Bounds-checked big-endian reads. Every offset that comes from a font file goes through here.</summary>
internal static class Be
{
    public static byte U8(ReadOnlySpan<byte> d, int o)
    {
        if ((uint)o >= (uint)d.Length)
        {
            throw Truncated();
        }

        return d[o];
    }

    public static sbyte I8(ReadOnlySpan<byte> d, int o) => (sbyte)U8(d, o);

    public static ushort U16(ReadOnlySpan<byte> d, int o)
    {
        if (o < 0 || o > d.Length - 2)
        {
            throw Truncated();
        }

        return BinaryPrimitives.ReadUInt16BigEndian(d.Slice(o, 2));
    }

    public static short I16(ReadOnlySpan<byte> d, int o) => (short)U16(d, o);

    public static uint U24(ReadOnlySpan<byte> d, int o)
    {
        if (o < 0 || o > d.Length - 3)
        {
            throw Truncated();
        }

        return (uint)((d[o] << 16) | (d[o + 1] << 8) | d[o + 2]);
    }

    public static uint U32(ReadOnlySpan<byte> d, int o)
    {
        if (o < 0 || o > d.Length - 4)
        {
            throw Truncated();
        }

        return BinaryPrimitives.ReadUInt32BigEndian(d.Slice(o, 4));
    }

    public static int I32(ReadOnlySpan<byte> d, int o) => (int)U32(d, o);

    /// <summary>Returns <c>d[offset..offset+length]</c> or throws; both numbers may be hostile.</summary>
    public static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> d, long offset, long length)
    {
        if (offset < 0 || length < 0 || offset > d.Length || length > d.Length - offset)
        {
            throw Truncated();
        }

        return d.Slice((int)offset, (int)length);
    }

    /// <summary>Reads an unsigned big-endian integer of 1 to 4 bytes.</summary>
    public static uint UN(ReadOnlySpan<byte> d, int o, int size) => size switch
    {
        1 => U8(d, o),
        2 => U16(d, o),
        3 => U24(d, o),
        4 => U32(d, o),
        _ => throw new FontProgramException("Invalid integer size."),
    };

    public static FontProgramException Truncated() => new("The font data is truncated or an offset points outside its table.");

    public static uint Tag(string s) => ((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | s[3];
}

/// <summary>Parsing bounds shared by the font program parsers. Fixed values; a hostile font hits these before it can cost real time or memory.</summary>
internal static class FontLimits
{
    /// <summary>Operators executed by one charstring, subroutines included.</summary>
    public const int MaxCharstringOperators = 400_000;

    /// <summary>Subroutine nesting in a charstring (Type 1 allows 10, Type 2 allows 10).</summary>
    public const int MaxSubrDepth = 16;

    /// <summary>Operand stack of a charstring (Type 2 specifies 48 and later 96).</summary>
    public const int MaxCharstringStack = 96;

    /// <summary>Nesting of composite TrueType glyphs.</summary>
    public const int MaxCompositeDepth = 8;

    /// <summary>Components of one composite TrueType glyph, nested ones included.</summary>
    public const int MaxCompositeComponents = 512;

    /// <summary>Verbs in one outline.</summary>
    public const int MaxOutlineVerbs = 1 << 18;

    /// <summary>Points of one TrueType glyph, composites included.</summary>
    public const int MaxGlyphPoints = 1 << 18;

    /// <summary>Glyphs (charstrings) in one Type 1 or CFF font.</summary>
    public const int MaxGlyphs = 1 << 17;

    /// <summary>Font dictionaries of a CID-keyed CFF font.</summary>
    public const int MaxFontDicts = 256;

    /// <summary>Tables in an sfnt directory.</summary>
    public const int MaxTables = 256;

    /// <summary>Fonts in a TrueType collection.</summary>
    public const int MaxCollectionFaces = 256;

    /// <summary>Bytes of a Type 1 font after eexec decryption.</summary>
    public const int MaxType1DecryptedBytes = 64 << 20;
}
