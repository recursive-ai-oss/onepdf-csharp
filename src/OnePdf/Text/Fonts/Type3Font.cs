using OnePdf.FontData;

namespace OnePdf.PdfFonts;

/// <summary>
/// A Type 3 font (ISO 32000-2 9.6.5): each glyph is a content stream in <c>/CharProcs</c>, selected by the name the encoding gives
/// the code. The font exposes the streams, the font matrix and the resources; running them is the interpreter's job.
/// </summary>
internal sealed class Type3Font : PdfFont
{
    private readonly string?[] _names = new string?[256];
    private readonly CMap? _toUnicode;
    private readonly double[]? _widths;
    private readonly int _firstChar;
    private readonly double _missingWidth;
    private readonly PdfDictionary? _charProcs;
    private readonly FontGlyph?[] _cache = new FontGlyph?[256];

    public Type3Font(PdfDictionary dictionary, FontContext context)
        : base(dictionary, context)
    {
        FontMatrix = [0.001, 0, 0, 0.001, 0, 0];
        if (dictionary.GetArray("FontMatrix") is { Count: 6 } fm)
        {
            double[] m = new double[6];
            for (int i = 0; i < 6; i++)
            {
                m[i] = fm.GetDouble(i);
            }

            if (double.IsFinite(m[0] + m[1] + m[2] + m[3] + m[4] + m[5]))
            {
                FontMatrix = m;
            }
        }

        if (dictionary.GetArray("FontBBox") is { Count: 4 } bb)
        {
            FontBBox = [bb.GetDouble(0), bb.GetDouble(1), bb.GetDouble(2), bb.GetDouble(3)];
        }

        _charProcs = dictionary.GetDictionary("CharProcs");
        Resources = dictionary.GetDictionary("Resources");
        DescriptorInfo desc = DescriptorInfo.Read(dictionary.GetDictionary("FontDescriptor"));
        Flags = desc.Flags;
        _missingWidth = desc.MissingWidth;
        _toUnicode = FontSupport.LoadToUnicode(context, dictionary);

        SimpleEncoding.Read(dictionary, out _, out PredefinedEncoding? baseEnc, out PdfArray? diffs);
        if (baseEnc is { } be)
        {
            Array.Copy(Encodings.Get(be), _names, 256);
        }

        SimpleEncoding.ApplyDifferences(_names, diffs);

        _firstChar = (int)Math.Clamp(dictionary.GetInt("FirstChar"), 0, 255);
        if (dictionary.GetArray("Widths") is { } w)
        {
            int n = Math.Min(w.Count, 256);
            _widths = new double[n];
            for (int i = 0; i < n; i++)
            {
                _widths[i] = w.GetDouble(i);
            }
        }
    }

    /// <inheritdoc />
    public override bool IsType3 => true;

    /// <summary>The <c>/FontMatrix</c>: glyph space to text space (default 0.001 scale).</summary>
    public double[] FontMatrix { get; }

    /// <summary>The font's own <c>/Resources</c> for its glyph procedures, or null (the page's resources then apply).</summary>
    public PdfDictionary? Resources { get; }

    /// <inheritdoc />
    public override int Next(ReadOnlySpan<byte> text, int position, out FontGlyph? glyph)
    {
        if ((uint)position >= (uint)text.Length)
        {
            glyph = null;
            return position;
        }

        int code = text[position];
        // Racing threads may both build a glyph; exactly one instance is published and shared.
        FontGlyph? g = Volatile.Read(ref _cache[code]);
        if (g is null)
        {
            FontGlyph built = Build(code);
            g = Interlocked.CompareExchange(ref _cache[code], built, null) ?? built;
        }

        glyph = g;
        return position + 1;
    }

    private FontGlyph Build(int code)
    {
        string? name = _names[code];
        PdfStream? proc = name is not null ? _charProcs?.Get(name) as PdfStream : null;
        bool hasUnicode = FontSupport.TryToUnicode(_toUnicode, (uint)code, 1, out string text) || FontSupport.NameToUnicode(name, false, out text);
        int idx = code - _firstChar;
        double w = _widths is not null && (uint)idx < (uint)_widths.Length ? _widths[idx] : _missingWidth;
        return new FontGlyph(this)
        {
            Code = (uint)code,
            CodeLength = 1,
            Cid = code,
            Gid = -1,
            Unicode = hasUnicode ? text : "\uFFFD",
            HasUnicode = hasUnicode,
            Exists = proc is not null,
            Name = name,
            Advance = w * FontMatrix[0],
            CharProc = proc,
        };
    }
}
