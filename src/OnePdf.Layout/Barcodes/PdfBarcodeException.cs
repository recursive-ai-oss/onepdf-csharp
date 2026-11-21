namespace OnePdf.Layout;

/// <summary>The symbologies <see cref="Barcode"/> can draw.</summary>
public enum BarcodeSymbology
{
    /// <summary>QR Code (ISO/IEC 18004), versions 1 to 40, error correction levels L, M, Q and H.</summary>
    QrCode = 0,

    /// <summary>Code 128 (ISO/IEC 15417) with code sets A, B and C switched automatically for the shortest symbol. Characters 0 to 127.</summary>
    Code128,

    /// <summary>
    /// GS1-128: Code 128 with a leading FNC1. The value is written with application identifiers in parentheses,
    /// <c>(01)09501101530003(17)150119(10)AB-123</c>; the parentheses are not encoded, and an FNC1 follows the data of every
    /// application identifier of variable length that is not the last.
    /// </summary>
    Gs1128,

    /// <summary>EAN-13 (ISO/IEC 15420). 12 digits (the check digit is added) or 13 (the check digit is verified).</summary>
    Ean13,

    /// <summary>EAN-8 (ISO/IEC 15420). 7 digits (the check digit is added) or 8 (verified).</summary>
    Ean8,

    /// <summary>UPC-A (ISO/IEC 15420). 11 digits (the check digit is added) or 12 (verified).</summary>
    UpcA,

    /// <summary>Code 39 (ISO/IEC 16388): digits, capital letters and <c>- . space $ / + %</c>. The start and stop character are added.</summary>
    Code39,
}

/// <summary>How much of the symbol is spent on error correction. Higher levels survive more damage and need a larger symbol.</summary>
public enum QrErrorCorrection
{
    /// <summary>Level L: about 7% of the codewords can be restored.</summary>
    Low = 0,

    /// <summary>Level M: about 15%. The default.</summary>
    Medium,

    /// <summary>Level Q: about 25%.</summary>
    Quartile,

    /// <summary>Level H: about 30%.</summary>
    High,
}

/// <summary>How the text of a QR Code is turned into bytes when it needs byte mode.</summary>
public enum QrCharacterSet
{
    /// <summary>UTF-8 without an ECI header. Readers detect it; this is what most generators write. The default.</summary>
    Utf8 = 0,

    /// <summary>UTF-8 announced by an ECI header (ECI 26, ISO/IEC 18004 7.4.2), for readers that honour it.</summary>
    Utf8WithEci,

    /// <summary>ISO 8859-1, the default interpretation of the standard. A character above U+00FF cannot be encoded.</summary>
    Latin1,
}

/// <summary>
/// The value given to a <see cref="Barcode"/> cannot be drawn in the symbology asked for: a character the symbology has no code for, a
/// wrong length, a check digit that does not match, or more data than the largest allowed symbol holds. Raised when the barcode is composed or
/// laid out, so no document ever holds a symbol that does not read.
/// </summary>
public sealed class PdfBarcodeException : PdfException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="symbology">The symbology that was asked for.</param>
    /// <param name="message">What is wrong with the value.</param>
    public PdfBarcodeException(BarcodeSymbology symbology, string message)
        : base(message)
    {
        Symbology = symbology;
    }

    /// <summary>The symbology the value was meant for.</summary>
    public BarcodeSymbology Symbology { get; }
}
