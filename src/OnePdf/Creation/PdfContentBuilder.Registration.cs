namespace OnePdf;

public abstract partial class PdfContentBuilder
{
    private readonly List<(string Name, PdfObject Value)> _colorSpaces = [];
    private string? _registrationSpace;

    internal IReadOnlyList<(string Name, PdfObject Value)> ColorSpaceResources => _colorSpaces;

    /// <summary>
    /// Sets the fill colour to registration colour at full tint: a <c>/Separation /All</c> colour space (ISO 32000-2 8.6.6.4), so
    /// the paint lands on every plate of a separated job. Viewers without separations show it black (the alternate space is
    /// DeviceGray).
    /// </summary>
    /// <exception cref="InvalidOperationException">The builder is closed.</exception>
    public void SetFillRegistration()
    {
        CheckOpen();
        Emit("/" + RegistrationSpace() + " cs 1 scn\n");
    }

    /// <summary>Sets the stroke colour to registration colour at full tint. See <see cref="SetFillRegistration"/>.</summary>
    /// <exception cref="InvalidOperationException">The builder is closed.</exception>
    public void SetStrokeRegistration()
    {
        CheckOpen();
        Emit("/" + RegistrationSpace() + " CS 1 SCN\n");
    }

    private string RegistrationSpace()
    {
        if (_registrationSpace is null)
        {
            string name = _names.Next("CsAll");

            // Tint 1 maps to gray 0 (black), tint 0 to white.
            var tint = new PdfDictionary();
            tint.SetEntry("FunctionType", new PdfInteger(2));
            var domain = new PdfArray();
            domain.Append(new PdfInteger(0));
            domain.Append(new PdfInteger(1));
            tint.SetEntry("Domain", domain);
            var c0 = new PdfArray();
            c0.Append(new PdfInteger(1));
            tint.SetEntry("C0", c0);
            var c1 = new PdfArray();
            c1.Append(new PdfInteger(0));
            tint.SetEntry("C1", c1);
            tint.SetEntry("N", new PdfInteger(1));

            var cs = new PdfArray();
            cs.Append(new PdfName("Separation"));
            cs.Append(new PdfName("All"));
            cs.Append(new PdfName("DeviceGray"));
            cs.Append(tint);
            _colorSpaces.Add((name, cs));
            _registrationSpace = name;
        }

        return _registrationSpace;
    }
}
