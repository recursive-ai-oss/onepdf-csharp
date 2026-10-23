using OnePdf.FontPrograms;
using OnePdf.PdfFonts;

namespace OnePdf.Content;

/// <summary>The interpreter's mutable graphics state. Saved copies are made with <see cref="Clone"/>.</summary>
internal sealed class GState
{
    public PdfMatrix Ctm = PdfMatrix.Identity;
    public PdfColorSpace FillCs = PdfColorSpace.Gray;
    public PdfColorSpace StrokeCs = PdfColorSpace.Gray;
    public PdfColor Fill = PdfColor.Black;
    public PdfColor Stroke = PdfColor.Black;
    public double LineWidth = 1;
    public int LineCap;
    public int LineJoin;
    public double MiterLimit = 10;
    public ReadOnlyMemory<double> Dash;
    public double DashPhase;
    public double Flatness = 1;
    public string Intent = "RelativeColorimetric";
    public double FillAlpha = 1;
    public double StrokeAlpha = 1;
    public string Blend = "Normal";
    public int SoftMask;
    public bool Ais;
    public bool TextKnockout = true;
    public bool FillOverprint;
    public bool StrokeOverprint;
    public int OverprintMode;

    // Text state (ISO 32000-2 9.3), saved and restored with q and Q.
    public double CharSpace;
    public double WordSpace;
    public double HorizScale = 1;
    public double Leading;
    public int RenderMode;
    public double Rise;
    public double FontSize;
    public FontInfo? Font;

    /// <summary>Clips pushed to the device since this state was created by <c>q</c>; popped when it is restored.</summary>
    public int ClipCount;

    public GState Clone()
    {
        var g = (GState)MemberwiseClone();
        g.ClipCount = 0;
        return g;
    }

    /// <summary>Makes this object a copy of <paramref name="o"/> as <see cref="Clone"/> would (clip count zero); lets the interpreter reuse state objects across q/Q pairs.</summary>
    public void CopyFrom(GState o)
    {
        Ctm = o.Ctm;
        FillCs = o.FillCs;
        StrokeCs = o.StrokeCs;
        Fill = o.Fill;
        Stroke = o.Stroke;
        LineWidth = o.LineWidth;
        LineCap = o.LineCap;
        LineJoin = o.LineJoin;
        MiterLimit = o.MiterLimit;
        Dash = o.Dash;
        DashPhase = o.DashPhase;
        Flatness = o.Flatness;
        Intent = o.Intent;
        FillAlpha = o.FillAlpha;
        StrokeAlpha = o.StrokeAlpha;
        Blend = o.Blend;
        SoftMask = o.SoftMask;
        Ais = o.Ais;
        TextKnockout = o.TextKnockout;
        FillOverprint = o.FillOverprint;
        StrokeOverprint = o.StrokeOverprint;
        OverprintMode = o.OverprintMode;
        CharSpace = o.CharSpace;
        WordSpace = o.WordSpace;
        HorizScale = o.HorizScale;
        Leading = o.Leading;
        RenderMode = o.RenderMode;
        Rise = o.Rise;
        FontSize = o.FontSize;
        Font = o.Font;
        ClipCount = 0;
    }
}

internal sealed class FontInfo(string name, PdfDictionary? dictionary, PdfFont font)
{
    public string Name { get; } = name;

    /// <summary>The font dictionary, or null for the default font used when the name did not resolve.</summary>
    public PdfDictionary? Dictionary { get; } = dictionary;

    public PdfFont Font { get; } = font;
}
