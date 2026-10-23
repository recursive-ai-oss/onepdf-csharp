using OnePdf.Forms;

namespace OnePdf;

public sealed partial class PdfFormField
{
    internal PdfDocument Doc => _doc;

    internal PdfObject? InheritedEntry(string key) => Inherited(key);

    internal IReadOnlyList<string> DefaultValues => Texts(Inherited("DV"));

    /// <summary>
    /// Stores a value in this field of a document opened with <see cref="PdfOpenOptions.AllowEditing"/>, and makes the widgets show it.
    /// Nothing is calculated, formatted or validated by script: the value is stored as given (field actions are never run).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>Text field: <paramref name="value"/> becomes <c>/V</c>; longer than <c>/MaxLen</c> is refused or cut per <see cref="PdfFillOptions.TruncateToMaxLength"/>. Null removes the value.</description></item>
    /// <item><description>Combo box: an export value from <c>/Opt</c>, or its display text (resolved to the export value); any text when the field is editable.</description></item>
    /// <item><description>List box: one option, as for a combo box. Use <see cref="SetValues"/> for several.</description></item>
    /// <item><description>Check box and radio button: the on-state name of a widget (a key of its <c>/AP /N</c> other than <c>Off</c>), the matching <c>/Opt</c> export value, or <c>Off</c> (null also means off).</description></item>
    /// </list>
    /// <c>/V</c> and each widget's <c>/AS</c> are updated and normal appearances are stored in <c>/AP</c> (see <see cref="PdfFillOptions.GenerateAppearances"/>).
    /// When <c>/NeedAppearances</c> is set it is cleared once every widget of the form has an appearance.
    /// </remarks>
    /// <param name="value">The value; null clears the field.</param>
    /// <param name="options">Options; null for the defaults.</param>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    /// <exception cref="PdfFormValueException">The value or the field does not fit (see <see cref="PdfFormValueError"/>); nothing was changed.</exception>
    /// <exception cref="PdfLimitException">The value is larger than <see cref="PdfLimits.MaxMetadataBytes"/>.</exception>
    public void SetValue(string? value, PdfFillOptions? options = null) =>
        FormFiller.SetValues(this, value is null ? [] : [value], options ?? PdfFillOptions.Default, lenient: false);

    /// <summary>
    /// Selects several options of a multi-select list box (their export values, or display texts). Also accepts zero values (clears)
    /// or one value for any choice field. The array <c>/V</c> and the index array <c>/I</c> are written in option order.
    /// </summary>
    /// <param name="values">The values to select.</param>
    /// <param name="options">Options; null for the defaults.</param>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    /// <exception cref="PdfFormValueException">The field is not a choice field, several values were given for a field that is not multi-select, or a value is not an option.</exception>
    public void SetValues(IReadOnlyList<string> values, PdfFillOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        FormFiller.SetValues(this, values, options ?? PdfFillOptions.Default, lenient: false);
    }

    /// <summary>
    /// Turns a check box on (its first on-state) or off. For a radio button group only <c>false</c> is meaningful; select a button with
    /// <see cref="SetValue"/> and its state name.
    /// </summary>
    /// <param name="isChecked">True for on.</param>
    /// <param name="options">Options; null for the defaults.</param>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    /// <exception cref="PdfFormValueException">The field is not a check box (or radio group, for false).</exception>
    public void SetChecked(bool isChecked, PdfFillOptions? options = null) =>
        FormFiller.SetChecked(this, isChecked, options ?? PdfFillOptions.Default);

    /// <summary>
    /// Sets the field back to its default value <c>/DV</c> (no <c>/DV</c> means empty, or off for buttons) and refreshes the widgets.
    /// A default that is not among the options is stored as it is.
    /// </summary>
    /// <param name="options">Options; null for the defaults.</param>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    /// <exception cref="PdfFormValueException">The field is read-only, a signature or push button, or has child fields.</exception>
    public void Reset(PdfFillOptions? options = null) => FormFiller.Reset(this, options ?? PdfFillOptions.Default);
}

public sealed partial class PdfAcroForm
{
    private Dictionary<string, PdfFormField>? _byName;
    private FormSession? _session;

    internal PdfDocument Doc => _doc;

    internal FormSession Session => _session ??= new FormSession();

    internal IReadOnlyList<PdfFormField> AllFieldList => _all;

    private Dictionary<string, PdfFormField> ByName()
    {
        if (_byName is null)
        {
            var map = new Dictionary<string, PdfFormField>(_all.Count, StringComparer.Ordinal);
            foreach (PdfFormField f in _all)
            {
                map.TryAdd(f.FullName, f);
            }

            _byName = map;
        }

        return _byName;
    }

    private PdfFormField Named(string fullName)
    {
        ArgumentNullException.ThrowIfNull(fullName);
        return FindField(fullName) ?? throw new PdfFormValueException(PdfFormValueError.UnknownField, fullName, "The form has no field named '" + fullName + "'.");
    }

    /// <summary>Stores a value in the field with this fully qualified name; see <see cref="PdfFormField.SetValue"/>.</summary>
    /// <param name="fullName">The fully qualified field name.</param>
    /// <param name="value">The value; null clears the field.</param>
    /// <param name="options">Options; null for the defaults.</param>
    /// <exception cref="PdfFormValueException">No such field, or the value does not fit.</exception>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    public void SetValue(string fullName, string? value, PdfFillOptions? options = null) => Named(fullName).SetValue(value, options);

    /// <summary>Selects options of the list box with this name; see <see cref="PdfFormField.SetValues"/>.</summary>
    /// <param name="fullName">The fully qualified field name.</param>
    /// <param name="values">The values to select.</param>
    /// <param name="options">Options; null for the defaults.</param>
    /// <exception cref="PdfFormValueException">No such field, or a value does not fit.</exception>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    public void SetValues(string fullName, IReadOnlyList<string> values, PdfFillOptions? options = null) => Named(fullName).SetValues(values, options);

    /// <summary>Turns the named check box on or off; see <see cref="PdfFormField.SetChecked"/>.</summary>
    /// <param name="fullName">The fully qualified field name.</param>
    /// <param name="isChecked">True for on.</param>
    /// <param name="options">Options; null for the defaults.</param>
    /// <exception cref="PdfFormValueException">No such field, or it is not a check box.</exception>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    public void SetChecked(string fullName, bool isChecked, PdfFillOptions? options = null) => Named(fullName).SetChecked(isChecked, options);

    /// <summary>
    /// Resets every text, choice, check box and radio button field to its default value (<see cref="PdfFormField.Reset"/>). Read-only
    /// fields are skipped unless <see cref="PdfFillOptions.OverrideReadOnly"/> is set; push buttons and signatures are never touched.
    /// </summary>
    /// <param name="options">Options; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The number of fields reset.</returns>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    public int ResetAll(PdfFillOptions? options = null, CancellationToken cancellationToken = default) =>
        FormFiller.ResetAll(this, options ?? PdfFillOptions.Default, cancellationToken);

    /// <summary>
    /// Builds and stores the normal appearance of every widget that has none (check boxes and radio buttons: for their current
    /// state). When <c>/NeedAppearances</c> is set, the text and choice fields are rebuilt as well, because the flag is then cleared
    /// once every widget has an appearance (the same rule filling uses).
    /// </summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The number of appearances stored.</returns>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    public int GenerateMissingAppearances(CancellationToken cancellationToken = default) =>
        FormFiller.GenerateMissing(this, cancellationToken);
}
