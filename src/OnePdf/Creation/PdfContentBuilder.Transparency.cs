using System.Globalization;

namespace OnePdf;

/// <summary>How a soft mask turns the group that defines it into an opacity (ISO 32000-2 11.6.5.2).</summary>
public enum PdfSoftMaskKind
{
    /// <summary>The mask is the group's alpha.</summary>
    Alpha = 0,

    /// <summary>The mask is the luminosity of the group composited on a black backdrop.</summary>
    Luminosity,
}

public abstract partial class PdfContentBuilder
{
    /// <summary>
    /// Makes a soft mask current (<c>/SMask</c> in an <c>ExtGState</c>): everything painted afterwards is multiplied by the mask until
    /// <see cref="ClearSoftMask"/> or the enclosing <see cref="Restore"/>. The mask form is drawn in the coordinate system that is current
    /// <i>now</i>, not at the time of painting.
    /// </summary>
    /// <param name="mask">A form made with <see cref="PdfFormContentBuilder.SetTransparencyGroup"/> in this builder's document.</param>
    /// <param name="kind">Alpha or luminosity. Default luminosity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="mask"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The builder is closed.</exception>
    public void SetSoftMask(PdfFormXObject mask, PdfSoftMaskKind kind = PdfSoftMaskKind.Luminosity)
    {
        ArgumentNullException.ThrowIfNull(mask);
        CheckOpen();
        string key = "SMask=" + mask.Reference.Number.ToString(CultureInfo.InvariantCulture) + ":" + kind;
        if (!_stateByKey.TryGetValue(key, out string? name))
        {
            name = _names.Next("OpGs");
            var m = new PdfDictionary();
            m.SetEntry("Type", new PdfName("Mask"));
            m.SetEntry("S", new PdfName(kind == PdfSoftMaskKind.Alpha ? "Alpha" : "Luminosity"));
            m.SetEntry("G", mask.Reference);
            var d = new PdfDictionary();
            d.SetEntry("Type", new PdfName("ExtGState"));
            d.SetEntry("SMask", m);
            _states.Add((name, d));
            _stateByKey[key] = name;
        }

        Emit("/" + name + " gs\n");
    }

    /// <summary>Removes the current soft mask (<c>/SMask /None</c>).</summary>
    /// <exception cref="InvalidOperationException">The builder is closed.</exception>
    public void ClearSoftMask()
    {
        CheckOpen();
        const string key = "SMask=None";
        if (!_stateByKey.TryGetValue(key, out string? name))
        {
            name = _names.Next("OpGs");
            var d = new PdfDictionary();
            d.SetEntry("Type", new PdfName("ExtGState"));
            d.SetEntry("SMask", new PdfName("None"));
            _states.Add((name, d));
            _stateByKey[key] = name;
        }

        Emit("/" + name + " gs\n");
    }
}
