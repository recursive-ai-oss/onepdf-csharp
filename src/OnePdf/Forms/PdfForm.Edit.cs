namespace OnePdf;

public sealed partial class PdfFormField
{
    /// <summary>
    /// Changes the partial name <c>/T</c> of this field (ISO 32000-2 12.7.4.2). The field stays under the same parent, its widgets, <c>/Kids</c>
    /// and <c>/Parent</c> links are untouched, and the full names of every descendant change with it. Works for a terminal field, a field
    /// merged with its widget, and a parent. Renaming to the current name does nothing. Nothing is changed when it throws.
    /// </summary>
    /// <param name="newName">The new partial name: not empty, no period.</param>
    /// <exception cref="InvalidOperationException">The document is not editable (<see cref="PdfOpenOptions.AllowEditing"/>).</exception>
    /// <exception cref="PdfFormValueException"><see cref="PdfFormValueError.InvalidName"/> for an empty name or one with a period; <see cref="PdfFormValueError.NameInUse"/> when this field or a descendant would get the full name of a field outside the renamed subtree.</exception>
    public void Rename(string newName) => OnePdf.Forms.FormEditor.Rename(this, newName);

    /// <summary>
    /// Changes background colour, border colour, font size and text colour, and rebuilds the stored normal appearance of every widget so a
    /// viewer shows the change without <c>/NeedAppearances</c>.
    /// <list type="bullet">
    /// <item>Scope: this field if it is terminal, otherwise every terminal field below it. Signature and unknown-type fields below a parent are skipped.</item>
    /// <item>Background and border go into each widget's own <c>/MK</c> (a shared indirect <c>/MK</c> is copied first). Transparent removes the entry.</item>
    /// <item>Size and text colour change the <c>/DA</c> string (12.7.4.3). Each terminal field gets its own <c>/DA</c>, built from the one it inherits from its parents or the AcroForm, keeping the font and other operators; siblings that inherited the same string are not touched. A widget with its own <c>/DA</c> gets the change too; intermediate parents keep theirs.</item>
    /// <item>Text, combo, list and push button fields are redrawn as filling does. For check boxes and radio buttons every state in <c>/AP /N</c> is redrawn by the generator (frame in the new colours, mark from <c>/MK /CA</c>), replacing authored art, because the caller asked for the change.</item>
    /// </list>
    /// </summary>
    /// <param name="appearance">The settings to change; null properties stay as they are. All null does nothing.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The number of widgets whose appearance was rebuilt.</returns>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    /// <exception cref="ArgumentException">A transparent text colour, or a font size outside 0..1000.</exception>
    /// <exception cref="PdfFormValueException"><see cref="PdfFormValueError.WrongType"/>: this terminal field is a signature or has no known type.</exception>
    public int SetAppearance(PdfFieldAppearance appearance, CancellationToken cancellationToken = default) =>
        OnePdf.Forms.FormEditor.SetAppearance(this, appearance, cancellationToken);
}

public sealed partial class PdfAcroForm
{
    internal void InvalidateNames() => _byName = null;

    /// <summary>Renames the field with this full name; see <see cref="PdfFormField.Rename"/>. <c>RenameField("person.surname", "family")</c> makes the field <c>person.family</c>.</summary>
    /// <param name="oldFullName">The current fully qualified name.</param>
    /// <param name="newName">The new partial name (the last part only): not empty, no period.</param>
    /// <exception cref="PdfFormValueException"><see cref="PdfFormValueError.UnknownField"/>, <see cref="PdfFormValueError.InvalidName"/> or <see cref="PdfFormValueError.NameInUse"/>.</exception>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    public void RenameField(string oldFullName, string newName) => Named(oldFullName).Rename(newName);

    /// <summary>Changes the appearance of the named field and rebuilds its widgets; see <see cref="PdfFormField.SetAppearance"/>.</summary>
    /// <param name="fullName">The fully qualified name of a terminal or parent field.</param>
    /// <param name="appearance">The settings to change.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The number of widgets rebuilt.</returns>
    /// <exception cref="PdfFormValueException">No such field, or it has no appearance to rebuild.</exception>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    public int SetFieldAppearance(string fullName, PdfFieldAppearance appearance, CancellationToken cancellationToken = default) =>
        Named(fullName).SetAppearance(appearance, cancellationToken);
}
