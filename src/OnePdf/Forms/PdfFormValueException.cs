namespace OnePdf;

/// <summary>Why a form value was refused.</summary>
public enum PdfFormValueError
{
    /// <summary>No field has the given fully qualified name.</summary>
    UnknownField = 0,

    /// <summary>The operation does not apply to the field's type (a text value for a check box, a check for a text field, ...), or the field is a signature, push button or of unknown type.</summary>
    WrongType,

    /// <summary>The field has the read-only flag and <see cref="PdfFillOptions.OverrideReadOnly"/> is false.</summary>
    ReadOnly,

    /// <summary>The value is not one of the field's options (<c>/Opt</c>), or not one of a button's appearance states.</summary>
    NotInOptions,

    /// <summary>A text value is longer than <c>/MaxLen</c> and <see cref="PdfFillOptions.TruncateToMaxLength"/> is false.</summary>
    TooLong,

    /// <summary>Several values were given for a field that is not a multi-select list box.</summary>
    NotMultiSelect,

    /// <summary>The field has child fields; values belong to terminal fields.</summary>
    NotTerminal,
}

/// <summary>
/// A value could not be stored in a form field. <see cref="Reason"/> says why; nothing was changed when this is thrown.
/// It is an <see cref="ArgumentException"/>: the field name or the value is what the caller got wrong.
/// </summary>
public sealed class PdfFormValueException : ArgumentException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="reason">Why the value was refused.</param>
    /// <param name="fieldName">The fully qualified field name.</param>
    /// <param name="message">What went wrong, in words.</param>
    public PdfFormValueException(PdfFormValueError reason, string fieldName, string message)
        : base(message)
    {
        Reason = reason;
        FieldName = fieldName;
    }

    /// <summary>Why the value was refused.</summary>
    public PdfFormValueError Reason { get; }

    /// <summary>The fully qualified name of the field.</summary>
    public string FieldName { get; }
}

/// <summary>Options for filling form fields.</summary>
public sealed class PdfFillOptions
{
    /// <summary>Fill fields that have the read-only flag. Default false: such a field throws <see cref="PdfFormValueError.ReadOnly"/>.</summary>
    public bool OverrideReadOnly { get; init; }

    /// <summary>
    /// Cut a text value to the field's <c>/MaxLen</c> characters (Unicode code points, counted from the start) instead of
    /// refusing it with <see cref="PdfFormValueError.TooLong"/>. Default false.
    /// </summary>
    public bool TruncateToMaxLength { get; init; }

    /// <summary>Build and store the normal appearance of the widgets (ISO 32000-2 12.7.4). Default true. When false only <c>/V</c> and <c>/AS</c> change and <c>/NeedAppearances</c> is left as it is.</summary>
    public bool GenerateAppearances { get; init; } = true;

    internal static PdfFillOptions Default { get; } = new();
}

/// <summary>Options for <see cref="PdfDocument.Flatten"/>.</summary>
public sealed class PdfFlattenOptions
{
    /// <summary>Flatten form widgets. Default true.</summary>
    public bool Fields { get; init; } = true;

    /// <summary>
    /// Fully qualified names of the fields to flatten (each with all fields under it); null flattens every field, including
    /// widgets that are not part of the form's field tree. An unknown name throws <see cref="PdfFormValueException"/>.
    /// </summary>
    public IReadOnlyCollection<string>? FieldNames { get; init; }

    /// <summary>Also flatten annotations other than widgets, links and pop-ups (markup, stamps, ...). Default false.</summary>
    public bool IncludeMarkup { get; init; }

    /// <summary>
    /// Which annotations are drawn: <see cref="PdfAnnotationMode.View"/> (the default: not hidden, not NoView) or
    /// <see cref="PdfAnnotationMode.Print"/> (Print flag set, not hidden). Annotations that are not drawn are still removed.
    /// <see cref="PdfAnnotationMode.None"/> is not allowed.
    /// </summary>
    public PdfAnnotationMode Visibility { get; init; } = PdfAnnotationMode.View;

    /// <summary>Build missing appearances in memory (as <c>page.Interpret</c> does) and draw them. Default true.</summary>
    public bool GenerateAppearances { get; init; } = true;
}

/// <summary>What <see cref="PdfDocument.Flatten"/> did.</summary>
public sealed class PdfFlattenResult
{
    internal PdfFlattenResult()
    {
    }

    /// <summary>Annotations whose appearance was drawn into page content.</summary>
    public int Drawn { get; internal set; }

    /// <summary>Annotations removed from pages (drawn or not).</summary>
    public int Removed { get; internal set; }

    /// <summary>Fields removed from the form.</summary>
    public int FieldsRemoved { get; internal set; }

    /// <summary>Signed signature fields that were kept (flattening them would delete the signature).</summary>
    public int SignedKept { get; internal set; }

    /// <summary>True when the document has no <c>/AcroForm</c> after flattening.</summary>
    public bool FormRemoved { get; internal set; }
}
