using System;
using System.Collections.Generic;

namespace OnePdf;

/// <summary>The families of PDF colour space (ISO 32000-2 8.6).</summary>
public enum PdfColorSpaceKind
{
    /// <summary>DeviceGray: one component, 0 = black, 1 = white.</summary>
    DeviceGray,

    /// <summary>DeviceRGB: three components.</summary>
    DeviceRgb,

    /// <summary>DeviceCMYK: four components, 0 = no ink.</summary>
    DeviceCmyk,

    /// <summary>CalGray: one component with a white point, optional black point and gamma.</summary>
    CalGray,

    /// <summary>CalRGB: three components with white point, optional black point, gamma and matrix.</summary>
    CalRgb,

    /// <summary>CIE L*a*b*: L in 0..100, a and b in <see cref="PdfColorSpace.Range"/>.</summary>
    Lab,

    /// <summary>ICCBased: 1, 3 or 4 components described by an ICC profile.</summary>
    IccBased,

    /// <summary>Indexed: one component, an index into a table of colours in a base space.</summary>
    Indexed,

    /// <summary>Separation: one tint component mapped to an alternate space by a tint transform.</summary>
    Separation,

    /// <summary>DeviceN: several tint components mapped to an alternate space by a tint transform.</summary>
    DeviceN,

    /// <summary>Pattern: colour comes from a pattern; an uncoloured pattern has an underlying space.</summary>
    Pattern,
}

/// <summary>
/// A resolved PDF colour space (ISO 32000-2 8.6). Spaces are immutable and shared; get them from
/// <see cref="Resolve"/> or the device singletons.
/// </summary>
/// <remarks>
/// <para><b>Final spaces</b> are what a device receives: DeviceGray, DeviceRGB, DeviceCMYK, CalGray, CalRGB, Lab and
/// ICCBased (profile bytes in <see cref="IccProfile"/>; an unusable profile is replaced by the device space with the same
/// component count). Indexed, Separation and DeviceN are resolved by OnePDF to <see cref="FinalSpace"/> (the base or
/// alternate space) with <see cref="ToFinal"/>; colour management is the device's job.</para>
/// <para>Component values of a final space lie in the ranges given by <see cref="GetComponentRange"/>: 0..1 except Lab
/// (L 0..100, a and b in <see cref="Range"/>) and ICCBased with its own <c>/Range</c>.</para>
/// </remarks>
public sealed class PdfColorSpace
{
    private static readonly double[] D50 = [0.9642, 1.0, 0.8249];

    internal PdfColorSpace(PdfColorSpaceKind kind, int components)
    {
        Kind = kind;
        ComponentCount = components;
    }

    /// <summary>DeviceGray.</summary>
    public static PdfColorSpace Gray { get; } = new(PdfColorSpaceKind.DeviceGray, 1);

    /// <summary>DeviceRGB.</summary>
    public static PdfColorSpace Rgb { get; } = new(PdfColorSpaceKind.DeviceRgb, 3);

    /// <summary>DeviceCMYK.</summary>
    public static PdfColorSpace Cmyk { get; } = new(PdfColorSpaceKind.DeviceCmyk, 4);

    /// <summary>
    /// For <see cref="PdfColorSpaceKind.Pattern"/>: the final space of the tint of an uncoloured pattern, or null. Colours
    /// painted with an uncoloured pattern carry their tint converted to this space.
    /// </summary>
    public PdfColorSpace? PatternBase => Kind == PdfColorSpaceKind.Pattern ? BaseSpace?.FinalSpace : null;

    /// <summary>The short name used in text snapshots: <c>gray</c>, <c>rgb</c>, <c>cmyk</c>, <c>lab</c>, <c>icc3</c>, <c>calrgb</c>, <c>pattern</c> and so on.</summary>
    public string Name => Kind switch
    {
        PdfColorSpaceKind.DeviceGray => "gray",
        PdfColorSpaceKind.DeviceRgb => "rgb",
        PdfColorSpaceKind.DeviceCmyk => "cmyk",
        PdfColorSpaceKind.CalGray => "calgray",
        PdfColorSpaceKind.CalRgb => "calrgb",
        PdfColorSpaceKind.Lab => "lab",
        PdfColorSpaceKind.IccBased => "icc" + ComponentCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PdfColorSpaceKind.Indexed => "indexed",
        PdfColorSpaceKind.Separation => "separation",
        PdfColorSpaceKind.DeviceN => "devicen",
        _ => "pattern",
    };

    /// <summary>The family.</summary>
    public PdfColorSpaceKind Kind { get; }

    /// <summary>Number of components a colour in this space has (1 for Indexed, Separation and Pattern without an underlying space).</summary>
    public int ComponentCount { get; }

    /// <summary>CalGray, CalRGB and Lab: the diffuse white point (XYZ). Null for other kinds.</summary>
    public IReadOnlyList<double>? WhitePoint { get; internal set; }

    /// <summary>CalGray, CalRGB and Lab: the diffuse black point (XYZ), default 0 0 0. Null for other kinds.</summary>
    public IReadOnlyList<double>? BlackPoint { get; internal set; }

    /// <summary>CalGray (one value) and CalRGB (three): the gamma exponents, default 1. Null for other kinds.</summary>
    public IReadOnlyList<double>? Gamma { get; internal set; }

    /// <summary>CalRGB: the nine matrix entries (X<sub>A</sub> Y<sub>A</sub> Z<sub>A</sub> X<sub>B</sub> …), default identity. Null for other kinds.</summary>
    public IReadOnlyList<double>? Matrix { get; internal set; }

    /// <summary>Lab: amin amax bmin bmax (default −100 100 −100 100). ICCBased: the stream's <c>/Range</c> as min/max pairs, or null for 0..1.</summary>
    public IReadOnlyList<double>? Range { get; internal set; }

    /// <summary>ICCBased: the profile bytes (decoded). Empty when the profile was unusable and the space became a device space.</summary>
    public ReadOnlyMemory<byte> IccProfile { get; internal set; }

    /// <summary>ICCBased: the <c>/Alternate</c> space if given (informational; used when the profile is unusable).</summary>
    public PdfColorSpace? Alternate { get; internal set; }

    /// <summary>Indexed: the base space. Separation and DeviceN: the alternate space. Pattern: the underlying space of an uncoloured pattern, or null.</summary>
    public PdfColorSpace? BaseSpace { get; internal set; }

    /// <summary>Indexed: the highest valid index (0..255).</summary>
    public int HiVal { get; internal set; }

    /// <summary>Indexed: the table, <c>(HiVal + 1) × BaseSpace.ComponentCount</c> bytes, each 0..255 mapped onto the base space's component range. May be shorter than that when the file is damaged; missing entries read as 0.</summary>
    public ReadOnlyMemory<byte> Lookup { get; internal set; }

    /// <summary>Separation and DeviceN: the colorant names.</summary>
    public IReadOnlyList<string> Colorants { get; internal set; } = [];

    internal IColorTransform? Tint { get; set; }

    /// <summary>True for the spaces a device receives as they are (everything except Indexed, Separation, DeviceN and Pattern).</summary>
    public bool IsFinal => Kind is not (PdfColorSpaceKind.Indexed or PdfColorSpaceKind.Separation or PdfColorSpaceKind.DeviceN or PdfColorSpaceKind.Pattern);

    /// <summary>
    /// The final space colours of this space convert to: itself for final spaces, the base of an Indexed space, the alternate of a
    /// Separation or DeviceN space (recursively). For Pattern, the underlying space, or the space itself when there is none.
    /// </summary>
    public PdfColorSpace FinalSpace => Kind switch
    {
        PdfColorSpaceKind.Indexed or PdfColorSpaceKind.Separation or PdfColorSpaceKind.DeviceN => BaseSpace!.FinalSpace,
        PdfColorSpaceKind.Pattern => BaseSpace?.FinalSpace ?? this,
        _ => this,
    };

    /// <summary>Component count of <see cref="FinalSpace"/>.</summary>
    public int FinalComponentCount => FinalSpace.ComponentCount;

    /// <summary>
    /// Resolves a colour space object: a name (device names, the inline abbreviations G/RGB/CMYK/I, or a key of the
    /// resources' <c>/ColorSpace</c> dictionary) or an array. Returns null and records a diagnostic when the object does not
    /// describe a usable space. Recursion is bounded by <see cref="PdfLimits.MaxRecursionDepth"/>.
    /// </summary>
    /// <param name="document">The document that owns the object (diagnostics and limits).</param>
    /// <param name="colorSpace">The object, for example the value of a <c>/ColorSpace</c> entry or an operand of <c>cs</c>.</param>
    /// <param name="resources">The resource dictionary used to look up named spaces, or null.</param>
    public static PdfColorSpace? Resolve(PdfDocument document, PdfObject? colorSpace, PdfDictionary? resources = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        return ColorSpaceResolver.Resolve(document, colorSpace, resources);
    }

    /// <summary>The colour a space starts with (<c>cs</c>/<c>CS</c>): zeros clipped to range, CMYK 0 0 0 1, tints 1.0 (ISO 32000-2 8.6.5 to 8.6.6).</summary>
    /// <param name="initial">Receives <see cref="ComponentCount"/> values.</param>
    public void GetInitialColor(Span<double> initial)
    {
        if (initial.Length < ComponentCount)
        {
            throw new ArgumentException("The span is shorter than ComponentCount.", nameof(initial));
        }

        initial = initial[..ComponentCount];
        initial.Clear();
        switch (Kind)
        {
            case PdfColorSpaceKind.DeviceCmyk:
                initial[3] = 1;
                break;
            case PdfColorSpaceKind.Separation or PdfColorSpaceKind.DeviceN:
                initial.Fill(1);
                break;
            case PdfColorSpaceKind.IccBased when ComponentCount == 4 && Range is null:
                // Zeros would be "no ink" (white); a four-component profile starts as black like DeviceCMYK.
                initial[3] = 1;
                break;
            case PdfColorSpaceKind.Lab or PdfColorSpaceKind.IccBased:
                for (int i = 0; i < initial.Length; i++)
                {
                    (double min, double max) = GetComponentRange(i);
                    initial[i] = Math.Clamp(0, min, max);
                }

                break;
            case PdfColorSpaceKind.Pattern when BaseSpace is { } u:
                u.GetInitialColor(initial);
                break;
        }
    }

    /// <summary>The range of component <paramref name="index"/>: 0..1 unless this is Lab or an ICCBased space with <see cref="Range"/>. Indexed: 0..HiVal.</summary>
    public (double Min, double Max) GetComponentRange(int index)
    {
        if ((uint)index >= (uint)ComponentCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        switch (Kind)
        {
            case PdfColorSpaceKind.Lab:
                if (index == 0)
                {
                    return (0, 100);
                }

                return Range is { Count: >= 4 } r ? (r[(index - 1) * 2], r[(index - 1) * 2 + 1]) : (-100, 100);
            case PdfColorSpaceKind.IccBased when Range is { } rr && rr.Count >= (index + 1) * 2:
                return (rr[index * 2], rr[index * 2 + 1]);
            case PdfColorSpaceKind.Indexed:
                return (0, HiVal);
            default:
                return (0, 1);
        }
    }

    /// <summary>
    /// The default <c>/Decode</c> array for an image in this space with <paramref name="bitsPerComponent"/> bits
    /// (ISO 32000-2 8.9.5.2 Table 87): min/max pairs, <c>ComponentCount × 2</c> values.
    /// </summary>
    public void GetDefaultDecode(int bitsPerComponent, Span<double> decode)
    {
        if (decode.Length < ComponentCount * 2)
        {
            throw new ArgumentException("The span is shorter than 2 × ComponentCount.", nameof(decode));
        }

        for (int i = 0; i < ComponentCount; i++)
        {
            if (Kind == PdfColorSpaceKind.Indexed)
            {
                decode[0] = 0;
                decode[1] = (1L << Math.Clamp(bitsPerComponent, 1, 16)) - 1;
            }
            else
            {
                (double min, double max) = GetComponentRange(i);
                decode[i * 2] = min;
                decode[i * 2 + 1] = max;
            }
        }
    }

    /// <summary>
    /// Converts one colour of this space to <see cref="FinalSpace"/>: Indexed looks the index up (clipped to 0..HiVal) and
    /// converts through the base; Separation and DeviceN run the tint transform into the alternate space. Final spaces copy.
    /// </summary>
    /// <param name="components"><see cref="ComponentCount"/> values (missing ones read as 0).</param>
    /// <param name="final">Receives <see cref="FinalComponentCount"/> values.</param>
    public void ToFinal(ReadOnlySpan<double> components, Span<double> final)
    {
        int fc = FinalComponentCount;
        if (final.Length < fc)
        {
            throw new ArgumentException("The span is shorter than FinalComponentCount.", nameof(final));
        }

        ToFinalCore(components, final[..fc]);
    }

    internal void ToFinalCore(ReadOnlySpan<double> c, Span<double> final)
    {
        switch (Kind)
        {
            case PdfColorSpaceKind.Indexed:
            {
                PdfColorSpace b = BaseSpace!;
                int n = b.ComponentCount;
                int idx = c.Length > 0 ? (int)Math.Clamp(Math.Round(double.IsNaN(c[0]) ? 0 : c[0]), 0, HiVal) : 0;
                Span<double> comps = n <= 32 ? stackalloc double[n] : new double[n];
                ReadOnlySpan<byte> table = Lookup.Span;
                for (int i = 0; i < n; i++)
                {
                    int p = idx * n + i;
                    double t = p < table.Length ? table[p] / 255.0 : 0;
                    (double min, double max) = b.GetComponentRange(i);
                    comps[i] = min + t * (max - min);
                }

                b.ToFinalCore(comps, final);
                break;
            }

            case PdfColorSpaceKind.Separation or PdfColorSpaceKind.DeviceN:
            {
                PdfColorSpace alt = BaseSpace!;
                int n = alt.ComponentCount;
                Span<double> comps = n <= 32 ? stackalloc double[n] : new double[n];
                Span<double> input = ComponentCount <= 32 ? stackalloc double[ComponentCount] : new double[ComponentCount];
                for (int i = 0; i < input.Length; i++)
                {
                    input[i] = i < c.Length ? Math.Clamp(c[i], 0, 1) : 0;
                }

                if (Tint is { } t && t.OutputCount >= 1)
                {
                    Span<double> outv = t.OutputCount <= 32 ? stackalloc double[t.OutputCount] : new double[t.OutputCount];
                    t.Evaluate(input, outv);
                    for (int i = 0; i < n; i++)
                    {
                        comps[i] = i < outv.Length ? outv[i] : 0;
                    }
                }
                else
                {
                    // Without a tint transform, approximate the ink: darker with more tint.
                    double m = 0;
                    foreach (double v in input)
                    {
                        m = Math.Max(m, v);
                    }

                    if (alt.FinalSpace.Kind == PdfColorSpaceKind.DeviceCmyk)
                    {
                        comps.Clear();
                        comps[3] = m;
                    }
                    else
                    {
                        comps.Fill(1 - m);
                    }
                }

                for (int i = 0; i < n; i++)
                {
                    (double min, double max) = alt.GetComponentRange(i);
                    comps[i] = Math.Clamp(comps[i], min, max);
                }

                alt.ToFinalCore(comps, final);
                break;
            }

            case PdfColorSpaceKind.Pattern when BaseSpace is { } u:
                u.ToFinalCore(c, final);
                break;
            default:
                for (int i = 0; i < final.Length; i++)
                {
                    (double min, double max) = GetComponentRange(Math.Min(i, ComponentCount - 1));
                    double v = i < c.Length && !double.IsNaN(c[i]) ? c[i] : 0;
                    final[i] = Math.Clamp(v, min, max);
                }

                break;
        }
    }

    internal static IReadOnlyList<double> DefaultWhitePoint => D50;

    /// <inheritdoc />
    public override string ToString() => Kind + "(" + ComponentCount + ")";
}
