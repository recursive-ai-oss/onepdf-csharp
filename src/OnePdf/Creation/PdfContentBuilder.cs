using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OnePdf;

/// <summary>Where content added to an existing page goes relative to what the page already draws.</summary>
public enum PdfContentPlacement
{
    /// <summary>After the page's content, so the new content covers it. The existing content runs in its own saved graphics state, so nothing it leaves behind (an unbalanced <c>q</c>, a changed matrix, colour or clip) can affect the new content.</summary>
    Overlay = 0,

    /// <summary>Before the page's content, so the page's own content covers the new content.</summary>
    Underlay = 1,
}

/// <summary>Line end shapes (ISO 32000-2 8.4.3.3).</summary>
public enum PdfLineCap
{
    /// <summary>The stroke ends square at the end point.</summary>
    Butt = 0,

    /// <summary>A semicircle around the end point.</summary>
    Round = 1,

    /// <summary>The stroke extends half a line width past the end point.</summary>
    ProjectingSquare = 2,
}

/// <summary>Line join shapes (ISO 32000-2 8.4.3.4).</summary>
public enum PdfLineJoin
{
    /// <summary>Mitered, up to the miter limit.</summary>
    Miter = 0,

    /// <summary>Rounded.</summary>
    Round = 1,

    /// <summary>Bevelled.</summary>
    Bevel = 2,
}

/// <summary>Separable and non-separable blend modes (ISO 32000-2 11.3.5).</summary>
public enum PdfBlendMode
{
    /// <summary>Normal (the default).</summary>
    Normal = 0,

    /// <summary>Multiply.</summary>
    Multiply,

    /// <summary>Screen.</summary>
    Screen,

    /// <summary>Overlay.</summary>
    Overlay,

    /// <summary>Darken.</summary>
    Darken,

    /// <summary>Lighten.</summary>
    Lighten,

    /// <summary>ColorDodge.</summary>
    ColorDodge,

    /// <summary>ColorBurn.</summary>
    ColorBurn,

    /// <summary>HardLight.</summary>
    HardLight,

    /// <summary>SoftLight.</summary>
    SoftLight,

    /// <summary>Difference.</summary>
    Difference,

    /// <summary>Exclusion.</summary>
    Exclusion,
}

/// <summary>Horizontal alignment of text in a box.</summary>
public enum PdfTextAlignment
{
    /// <summary>Lines start at the left edge.</summary>
    Left = 0,

    /// <summary>Lines are centred.</summary>
    Center,

    /// <summary>Lines end at the right edge.</summary>
    Right,

    /// <summary>Lines are stretched to both edges by widening word gaps; the last line of a paragraph stays left aligned.</summary>
    Justify,
}

/// <summary>
/// Writes the content stream of a page or a form XObject: paths, colours, text, images and nested forms, in the coordinates of
/// the target's default user space (for a page, origin at the lower left of the MediaBox as stored, before <c>/Rotate</c> and
/// <c>/UserUnit</c>; y grows upward). Numbers are written to four decimals in the invariant culture, so identical calls always give
/// identical bytes.
/// </summary>
/// <remarks>
/// A builder collects content until <c>Commit</c> is called on the page or form builder; disposing it without committing discards
/// everything. A builder is for one thread. Open graphics states (<see cref="Save"/> without <see cref="Restore"/>) are closed by
/// commit. Colours are DeviceGray, DeviceRGB and DeviceCMYK; components are clamped to 0..1. Non-finite numbers are refused with
/// <see cref="ArgumentOutOfRangeException"/>.
/// </remarks>
public abstract partial class PdfContentBuilder : IDisposable
{
    private const int MaxContentBytes = 1 << 28;
    private const int MaxDepth = 28;

    private readonly StringBuilder _sb = new();
    private readonly NameAllocator _names;
    private readonly List<(string Name, PdfObject Value)> _fonts = [];
    private readonly List<(string Name, PdfObject Value)> _xobjects = [];
    private readonly List<(string Name, PdfObject Value)> _states = [];
    private readonly Dictionary<string, string> _stateByKey = [];
    private readonly Dictionary<int, string> _nameByObject = [];
    private int _depth;
    // What is open, innermost last: 'q' for a saved graphics state, 'M' for a marked-content sequence. They must nest (ISO 32000-2 14.6.1).
    private readonly List<char> _open = [];
    private PdfCreatedFont? _font;
    private double _fontSize;
    private double _charSpacing;
    private double _wordSpacing;
    private PdfTextRenderMode _renderMode = PdfTextRenderMode.Fill;

    internal PdfContentBuilder(PdfDocument document, PdfDictionary? existingResources)
    {
        Document = document;
        _names = new NameAllocator(existingResources);
    }

    /// <summary>The document the content is for.</summary>
    public PdfDocument Document { get; }

    /// <summary>Called by the form and streaming page builders after their content has been stored, so a streaming writer can write it out.</summary>
    internal Action? AfterCommit { get; set; }

    /// <summary>True once <c>Commit</c> has run or the builder was disposed; further calls throw <see cref="InvalidOperationException"/>.</summary>
    public bool IsClosed { get; private protected set; }

    internal IReadOnlyList<(string Name, PdfObject Value)> FontResources => _fonts;

    internal IReadOnlyList<(string Name, PdfObject Value)> XObjectResources => _xobjects;

    internal IReadOnlyList<(string Name, PdfObject Value)> StateResources => _states;

    /// <summary>Discards the content if it was not committed.</summary>
    public void Dispose()
    {
        IsClosed = true;
        GC.SuppressFinalize(this);
    }

    // ---- plumbing ----

    private protected void CheckOpen()
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        Document.EnsureEditable(0);
    }

    private protected byte[] Finish()
    {
        for (int i = _open.Count - 1; i >= 0; i--)
        {
            _sb.Append(_open[i] == 'q' ? "Q\n" : "EMC\n");
        }

        _open.Clear();
        _depth = 0;
        MarkedContentDepth = 0;

        return Encoding.Latin1.GetBytes(_sb.ToString());
    }

    private void Emit(string s)
    {
        if (_sb.Length > MaxContentBytes)
        {
            throw new PdfLimitException("The content stream would be larger than 256 MiB.");
        }

        _sb.Append(s);
    }

    internal static string Num(double v)
    {
        Span<char> buffer = stackalloc char[48];
        int n = FormatNum(v, buffer);
        return n == 1 && buffer[0] == '0' ? "0" : new string(buffer[..n]);
    }

    // The number as the stream writes it: rounded to four decimals (halves away from zero), no trailing zeros, "0" for zero (never "-0"),
    // invariant culture. Values below 1e9 are formatted from the rounded integer count of ten-thousandths, which is exactly what the
    // general double formatting gives for them (at most 13 significant digits) and is several times faster; larger ones take the
    // general path.
    internal static int FormatNum(double v, Span<char> dest)
    {
        if (!double.IsFinite(v))
        {
            throw new ArgumentOutOfRangeException(nameof(v), "Numbers must be finite.");
        }

        double clamped = Math.Clamp(v, -1e12, 1e12);
        if (Math.Abs(clamped) < 1e9)
        {
            double scaled = Math.Round(clamped * 10000.0, MidpointRounding.AwayFromZero);
            long count = (long)Math.Abs(scaled);
            if (count == 0)
            {
                dest[0] = '0';
                return 1;
            }

            int pos = 0;
            if (scaled < 0)
            {
                dest[pos++] = '-';
            }

            long whole = count / 10000;
            int fraction = (int)(count % 10000);
            whole.TryFormat(dest[pos..], out int written, default, CultureInfo.InvariantCulture);
            pos += written;
            if (fraction != 0)
            {
                dest[pos++] = '.';
                int digits = 4;
                while (fraction % 10 == 0)
                {
                    fraction /= 10;
                    digits--;
                }

                for (int i = digits - 1; i >= 0; i--)
                {
                    dest[pos + i] = (char)('0' + (fraction % 10));
                    fraction /= 10;
                }

                pos += digits;
            }

            return pos;
        }

        double r = Math.Round(clamped, 4, MidpointRounding.AwayFromZero);
        if (r == 0)
        {
            dest[0] = '0';
            return 1;
        }

        return r.TryFormat(dest, out int w, "0.####", CultureInfo.InvariantCulture) ? w : throw new InvalidOperationException("A number does not fit its buffer.");
    }

    private static string Unit(double v) => Num(Math.Clamp(double.IsFinite(v) ? v : throw new ArgumentOutOfRangeException(nameof(v), "Numbers must be finite."), 0, 1));

    // Appends Num(v) without making the string. The text is the same, so output bytes do not change.
    internal static void AppendNum(StringBuilder sb, double v)
    {
        Span<char> buffer = stackalloc char[48];
        int n = FormatNum(v, buffer);
        sb.Append(buffer[..n]);
    }

    // True when Num(v) would be "0".
    internal static bool IsZeroNum(double v) => Math.Round(Math.Clamp(v, -1e12, 1e12), 4, MidpointRounding.AwayFromZero) == 0;

    private void CheckContentSize()
    {
        if (_sb.Length > MaxContentBytes)
        {
            throw new PdfLimitException("The content stream would be larger than 256 MiB.");
        }
    }

    private void Op(string op, params ReadOnlySpan<double> operands)
    {
        CheckOpen();
        foreach (double d in operands)
        {
            if (!double.IsFinite(d))
            {
                throw new ArgumentOutOfRangeException(nameof(operands), "Numbers must be finite.");
            }
        }

        CheckContentSize();
        foreach (double d in operands)
        {
            AppendNum(_sb, d);
            _sb.Append(' ');
        }

        _sb.Append(op).Append('\n');
    }

    // ---- graphics state ----

    /// <summary>Saves the graphics state (<c>q</c>): matrix, clip, colours, line style, opacity and text settings. At most 28 levels.</summary>
    /// <exception cref="InvalidOperationException">Too many levels, or the builder is closed.</exception>
    public void Save()
    {
        CheckOpen();
        if (_depth >= MaxDepth)
        {
            throw new InvalidOperationException("Graphics states are nested too deeply (28 levels).");
        }

        _depth++;
        _open.Add('q');
        Emit("q\n");
    }

    /// <summary>Restores the graphics state saved by the matching <see cref="Save"/> (<c>Q</c>).</summary>
    /// <exception cref="InvalidOperationException">There is no saved state to restore, or the builder is closed.</exception>
    public void Restore()
    {
        CheckOpen();
        if (_depth == 0)
        {
            throw new InvalidOperationException("Restore without a matching Save.");
        }

        if (_open[^1] != 'q')
        {
            throw new InvalidOperationException("Restore while a marked-content sequence begun after the matching Save is still open; end it first.");
        }

        _open.RemoveAt(_open.Count - 1);
        _depth--;
        Emit("Q\n");
    }

    /// <summary>Multiplies the current matrix by the given one (<c>cm</c>): the new coordinate system is <c>[a b c d e f]</c> applied inside the old.</summary>
    public void Transform(double a, double b, double c, double d, double e, double f) => Op("cm", a, b, c, d, e, f);

    /// <summary>Moves the origin by (<paramref name="x"/>, <paramref name="y"/>) in the current coordinates.</summary>
    public void Translate(double x, double y) => Transform(1, 0, 0, 1, x, y);

    /// <summary>Scales the current coordinates.</summary>
    public void Scale(double sx, double sy) => Transform(sx, 0, 0, sy, 0, 0);

    /// <summary>Rotates the current coordinates counter-clockwise by <paramref name="degrees"/> about the origin. Multiples of 90 are exact.</summary>
    public void Rotate(double degrees)
    {
        if (!double.IsFinite(degrees))
        {
            throw new ArgumentOutOfRangeException(nameof(degrees));
        }

        double normalized = degrees % 360;
        if (normalized < 0)
        {
            normalized += 360;
        }

        double cos;
        double sin;
        if (normalized == 0)
        {
            (cos, sin) = (1, 0);
        }
        else if (normalized == 90)
        {
            (cos, sin) = (0, 1);
        }
        else if (normalized == 180)
        {
            (cos, sin) = (-1, 0);
        }
        else if (normalized == 270)
        {
            (cos, sin) = (0, -1);
        }
        else
        {
            double rad = normalized * Math.PI / 180.0;
            (cos, sin) = (Math.Cos(rad), Math.Sin(rad));
        }

        Transform(cos, sin, -sin, cos, 0, 0);
    }

    /// <summary>Sets the fill colour to a gray level (0 black .. 1 white).</summary>
    public void SetFillGray(double gray) => Op("g", Math.Clamp(Check(gray), 0, 1));

    /// <summary>Sets the fill colour in DeviceRGB.</summary>
    public void SetFillRgb(double red, double green, double blue) => Op("rg", Clamp01(red), Clamp01(green), Clamp01(blue));

    /// <summary>Sets the fill colour in DeviceCMYK.</summary>
    public void SetFillCmyk(double cyan, double magenta, double yellow, double black) => Op("k", Clamp01(cyan), Clamp01(magenta), Clamp01(yellow), Clamp01(black));

    /// <summary>Sets the stroke colour to a gray level.</summary>
    public void SetStrokeGray(double gray) => Op("G", Clamp01(gray));

    /// <summary>Sets the stroke colour in DeviceRGB.</summary>
    public void SetStrokeRgb(double red, double green, double blue) => Op("RG", Clamp01(red), Clamp01(green), Clamp01(blue));

    /// <summary>Sets the stroke colour in DeviceCMYK.</summary>
    public void SetStrokeCmyk(double cyan, double magenta, double yellow, double black) => Op("K", Clamp01(cyan), Clamp01(magenta), Clamp01(yellow), Clamp01(black));

    private static double Check(double v) => double.IsFinite(v) ? v : throw new ArgumentOutOfRangeException(nameof(v), "Numbers must be finite.");

    private static double Clamp01(double v) => Math.Clamp(Check(v), 0, 1);

    /// <summary>Sets the stroke width in the current units (<c>w</c>). 0 is the thinnest line the device draws.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Negative or not finite.</exception>
    public void SetLineWidth(double width)
    {
        if (!double.IsFinite(width) || width < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        Op("w", width);
    }

    /// <summary>Sets the line cap.</summary>
    public void SetLineCap(PdfLineCap cap)
    {
        CheckOpen();
        Emit(((int)cap).ToString(CultureInfo.InvariantCulture) + " J\n");
    }

    /// <summary>Sets the line join.</summary>
    public void SetLineJoin(PdfLineJoin join)
    {
        CheckOpen();
        Emit(((int)join).ToString(CultureInfo.InvariantCulture) + " j\n");
    }

    /// <summary>Sets the miter limit (<c>M</c>); at least 1.</summary>
    public void SetMiterLimit(double limit)
    {
        if (!double.IsFinite(limit) || limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        Op("M", limit);
    }

    /// <summary>Sets the dash pattern (<c>d</c>): alternating dash and gap lengths starting <paramref name="phase"/> units in. An empty array makes lines solid.</summary>
    /// <exception cref="ArgumentException">A length is negative or not finite, or all are zero.</exception>
    public void SetDash(ReadOnlySpan<double> lengths, double phase = 0)
    {
        CheckOpen();
        bool any = false;
        var sb = new StringBuilder("[");
        for (int i = 0; i < lengths.Length; i++)
        {
            if (!double.IsFinite(lengths[i]) || lengths[i] < 0)
            {
                throw new ArgumentException("Dash lengths must be finite and not negative.", nameof(lengths));
            }

            any |= lengths[i] > 0;
            sb.Append(i > 0 ? " " : string.Empty).Append(Num(lengths[i]));
        }

        if (lengths.Length > 0 && !any)
        {
            throw new ArgumentException("A dash pattern needs at least one length above zero.", nameof(lengths));
        }

        sb.Append("] ").Append(Num(Check(phase))).Append(" d\n");
        Emit(sb.ToString());
    }

    /// <summary>Sets the opacity of fills, images and text (<c>/ca</c>), 0 transparent .. 1 opaque.</summary>
    public void SetFillOpacity(double opacity) => SetState("ca", Num(Clamp01(opacity)));

    /// <summary>Sets the opacity of strokes (<c>/CA</c>), 0 transparent .. 1 opaque.</summary>
    public void SetStrokeOpacity(double opacity) => SetState("CA", Num(Clamp01(opacity)));

    /// <summary>Sets both opacities.</summary>
    public void SetOpacity(double opacity)
    {
        SetFillOpacity(opacity);
        SetStrokeOpacity(opacity);
    }

    /// <summary>Sets the blend mode (<c>/BM</c>).</summary>
    public void SetBlendMode(PdfBlendMode mode) => SetState("BM", mode.ToString());

    private void SetState(string key, string value)
    {
        CheckOpen();
        string k = key + "=" + value;
        if (!_stateByKey.TryGetValue(k, out string? name))
        {
            name = _names.Next("OpGs");
            var d = new PdfDictionary();
            d.SetEntry("Type", new PdfName("ExtGState"));
            d.SetEntry(key, key == "BM" ? new PdfName(value) : new PdfReal(double.Parse(value, CultureInfo.InvariantCulture)));
            _states.Add((name, d));
            _stateByKey[k] = name;
        }

        Emit("/" + name + " gs\n");
    }

    // ---- paths ----

    /// <summary>Begins a subpath at the point (<c>m</c>).</summary>
    public void MoveTo(double x, double y) => Op("m", x, y);

    /// <summary>Adds a line to the point (<c>l</c>).</summary>
    public void LineTo(double x, double y) => Op("l", x, y);

    /// <summary>Adds a cubic Bezier curve with two control points (<c>c</c>).</summary>
    public void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3) => Op("c", x1, y1, x2, y2, x3, y3);

    /// <summary>Adds a closed rectangle subpath (<c>re</c>); (<paramref name="x"/>, <paramref name="y"/>) is a corner and the size may be negative.</summary>
    public void Rectangle(double x, double y, double width, double height) => Op("re", x, y, width, height);

    /// <summary>Adds an ellipse inscribed in the box as four Bezier curves (control distance 0.5522847498 of the radius), as a closed subpath.</summary>
    public void Ellipse(double x, double y, double width, double height)
    {
        const double k = 0.5522847498;
        double rx = width / 2;
        double ry = height / 2;
        double cx = x + rx;
        double cy = y + ry;
        MoveTo(cx + rx, cy);
        CurveTo(cx + rx, cy + (ry * k), cx + (rx * k), cy + ry, cx, cy + ry);
        CurveTo(cx - (rx * k), cy + ry, cx - rx, cy + (ry * k), cx - rx, cy);
        CurveTo(cx - rx, cy - (ry * k), cx - (rx * k), cy - ry, cx, cy - ry);
        CurveTo(cx + (rx * k), cy - ry, cx + rx, cy - (ry * k), cx + rx, cy);
        ClosePath();
    }

    /// <summary>Closes the current subpath with a line to its start (<c>h</c>).</summary>
    public void ClosePath() => Op("h");

    /// <summary>Fills the path with the fill colour (<c>f</c> nonzero winding, <c>f*</c> even-odd) and ends it.</summary>
    public void Fill(bool evenOdd = false) => Op(evenOdd ? "f*" : "f");

    /// <summary>Strokes the path with the stroke colour and line style (<c>S</c>) and ends it.</summary>
    public void Stroke() => Op("S");

    /// <summary>Fills then strokes the path (<c>B</c>, <c>B*</c>) and ends it.</summary>
    public void FillAndStroke(bool evenOdd = false) => Op(evenOdd ? "B*" : "B");

    /// <summary>Intersects the clip with the path (<c>W n</c>, <c>W* n</c>) and ends it. Wrap in <see cref="Save"/> and <see cref="Restore"/> to undo.</summary>
    public void Clip(bool evenOdd = false)
    {
        CheckOpen();
        Emit(evenOdd ? "W* n\n" : "W n\n");
    }

    /// <summary>Ends the path without painting it (<c>n</c>).</summary>
    public void EndPath() => Op("n");

    // ---- text ----

    /// <summary>The font set by <see cref="SetFont"/>, or null.</summary>
    public PdfCreatedFont? Font => _font;

    /// <summary>The font size set by <see cref="SetFont"/>.</summary>
    public double FontSize => _fontSize;

    /// <summary>Selects the font and size for the text that follows.</summary>
    /// <exception cref="ArgumentException">The font belongs to another document, or the size is not finite and above zero.</exception>
    public void SetFont(PdfCreatedFont font, double size)
    {
        ArgumentNullException.ThrowIfNull(font);
        if (font.Document != Document)
        {
            throw new ArgumentException("The font belongs to another document.", nameof(font));
        }

        if (!double.IsFinite(size) || size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        _font = font;
        _fontSize = size;
    }

    /// <summary>Extra space added after every glyph, in text units scaled by nothing but the font size (<c>Tc</c>, in points). Default 0.</summary>
    public void SetCharacterSpacing(double spacing) => _charSpacing = Check(spacing);

    /// <summary>
    /// Extra space added after every space character (U+0020), in points. Default 0. For embedded fonts this is written as a position
    /// adjustment because the PDF <c>Tw</c> operator ignores two-byte codes (ISO 32000-2 9.3.3).
    /// </summary>
    public void SetWordSpacing(double spacing) => _wordSpacing = Check(spacing);

    /// <summary>Sets the text rendering mode: fill (default), stroke, both, invisible, or with a clip.</summary>
    public void SetTextRenderMode(PdfTextRenderMode mode) => _renderMode = mode;

    /// <summary>The width of <paramref name="text"/> in points with the current font, size, character and word spacing.</summary>
    /// <exception cref="InvalidOperationException">No font has been set.</exception>
    public double MeasureText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        EncodedText e = RequireFont().Encode(text, record: false);
        return (e.Width1000 * _fontSize / 1000.0) + (e.GlyphCount * _charSpacing) + (e.Spaces.Length * _wordSpacing);
    }

    private PdfCreatedFont RequireFont() => _font ?? throw new InvalidOperationException("Call SetFont before drawing or measuring text.");

    /// <summary>Draws one line of text with its baseline start at (<paramref name="x"/>, <paramref name="y"/>) in the current coordinates, using the fill colour (and stroke colour in stroke modes). Control characters are skipped; '\n' does not break lines (use <see cref="DrawTextBox"/>).</summary>
    public void DrawText(string text, double x, double y) => DrawTextCore(text, 1, 0, 0, 1, x, y);

    /// <summary>Draws one line of text with an explicit text matrix <c>[a b c d e f]</c> (<c>Tm</c>), applied inside the current coordinates.</summary>
    public void DrawText(string text, double a, double b, double c, double d, double e, double f) => DrawTextCore(text, a, b, c, d, e, f);

    private void DrawTextCore(string text, double a, double b, double c, double d, double e, double f)
    {
        ArgumentNullException.ThrowIfNull(text);
        CheckOpen();
        PdfCreatedFont font = RequireFont();
        EncodedText enc = font.Encode(text, record: true);
        if (enc.GlyphCount == 0)
        {
            return;
        }

        string fontName = ResourceName(_fonts, "OpF", font.Reference);
        var sb = new StringBuilder();
        sb.Append("BT\n/").Append(fontName).Append(' ').Append(Num(_fontSize)).Append(" Tf\n");
        sb.Append(Num(_charSpacing)).Append(" Tc\n");
        sb.Append(enc.TwoByte ? "0" : Num(_wordSpacing)).Append(" Tw\n");
        sb.Append((int)_renderMode).Append(" Tr\n");
        sb.Append(Num(a)).Append(' ').Append(Num(b)).Append(' ').Append(Num(c)).Append(' ').Append(Num(d)).Append(' ').Append(Num(e)).Append(' ').Append(Num(f)).Append(" Tm\n");
        if (enc.TwoByte && _wordSpacing != 0 && enc.Spaces.Length > 0)
        {
            // Word spacing as a TJ adjustment after each space glyph (thousandths of the font size, positive moves left).
            string adjust = Num(-_wordSpacing * 1000.0 / _fontSize);
            sb.Append('[');
            int start = 0;
            foreach (int space in enc.Spaces)
            {
                AppendHex(sb, enc.Codes, start, space + 1);
                sb.Append(adjust);
                start = space + 1;
            }

            if (start < enc.GlyphCount)
            {
                AppendHex(sb, enc.Codes, start, enc.GlyphCount);
            }

            sb.Append("] TJ\n");
        }
        else if (enc.TwoByte)
        {
            AppendHex(sb, enc.Codes, 0, enc.GlyphCount);
            sb.Append(" Tj\n");
        }
        else
        {
            AppendLiteral(sb, enc.Codes);
            sb.Append(" Tj\n");
        }

        sb.Append("ET\n");
        Emit(sb.ToString());
    }

    private static void AppendHex(StringBuilder sb, byte[] codes, int firstGlyph, int endGlyph)
    {
        sb.Append('<');
        for (int i = firstGlyph * 2; i < endGlyph * 2; i++)
        {
            sb.Append(codes[i].ToString("X2", CultureInfo.InvariantCulture));
        }

        sb.Append('>');
    }

    private static void AppendLiteral(StringBuilder sb, byte[] codes)
    {
        sb.Append('(');
        foreach (byte b in codes)
        {
            if (b is (byte)'(' or (byte)')' or (byte)'\\')
            {
                sb.Append('\\').Append((char)b);
            }
            else if (b is < 32 or > 126)
            {
                sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
            }
            else
            {
                sb.Append((char)b);
            }
        }

        sb.Append(')');
    }

    /// <summary>
    /// Draws one line inside the horizontal span [<paramref name="x"/>, <paramref name="x"/> + <paramref name="width"/>] with its
    /// baseline at <paramref name="y"/>, aligned as asked. <see cref="PdfTextAlignment.Justify"/> acts as Left for a single line.
    /// A line wider than the span starts at the left edge (right edge for Right) and overflows.
    /// </summary>
    public void DrawTextAligned(string text, double x, double y, double width, PdfTextAlignment alignment = PdfTextAlignment.Left)
    {
        double w = MeasureText(text);
        double start = alignment switch
        {
            PdfTextAlignment.Center => x + ((width - w) / 2),
            PdfTextAlignment.Right => x + width - w,
            _ => x,
        };
        DrawText(text, start, y);
    }

    /// <summary>
    /// Splits <paramref name="text"/> into lines no wider than <paramref name="width"/> points with the current font, size and spacing.
    /// Breaks fall at spaces; '\n' ends a paragraph; a word wider than the width is broken between characters. No hyphenation.
    /// </summary>
    public IReadOnlyList<string> WrapText(string text, double width)
    {
        List<(string Text, bool EndsParagraph)> lines = WrapLines(text, width);
        var result = new List<string>(lines.Count);
        foreach ((string line, bool _) in lines)
        {
            result.Add(line);
        }

        return result;
    }

    private List<(string Text, bool EndsParagraph)> WrapLines(string text, double width)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!double.IsFinite(width) || width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        var lines = new List<(string, bool)>();
        string[] paragraphs = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        foreach (string paragraph in paragraphs)
        {
            var line = new StringBuilder();
            foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (MeasureText(candidate) <= width)
                {
                    line.Clear().Append(candidate);
                    continue;
                }

                if (line.Length > 0)
                {
                    lines.Add((line.ToString(), false));
                    line.Clear();
                }

                string rest = word;
                while (rest.Length > 0 && MeasureText(rest) > width)
                {
                    int take = 1;
                    int step = 1;
                    // Longest prefix that fits (at least one character), by growing the prefix one text element at a time.
                    while (take < rest.Length)
                    {
                        step = char.IsHighSurrogate(rest[take - 1]) ? 2 : 1;
                        int next = Math.Min(rest.Length, take + step);
                        if (MeasureText(rest[..next]) > width)
                        {
                            break;
                        }

                        take = next;
                    }

                    lines.Add((rest[..take], false));
                    rest = rest[take..];
                }

                line.Append(rest);
            }

            lines.Add((line.ToString(), true));
        }

        return lines;
    }

    /// <summary>
    /// Wraps and draws <paramref name="text"/> in a box whose lower left corner is (<paramref name="x"/>, <paramref name="y"/>).
    /// The first baseline is the font's ascent below the top edge and lines are <c>size * lineSpacing</c> apart. Lines are
    /// drawn while their descent stays inside the box.
    /// </summary>
    /// <returns>The text that did not fit (lines rejoined with single spaces, paragraphs with '\n'), or an empty string.</returns>
    public string DrawTextBox(string text, double x, double y, double width, double height, PdfTextAlignment alignment = PdfTextAlignment.Left, double lineSpacing = 1.2)
    {
        PdfCreatedFont font = RequireFont();
        if (!double.IsFinite(height) || height < 0 || !double.IsFinite(lineSpacing) || lineSpacing <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        List<(string Text, bool EndsParagraph)> lines = WrapLines(text, width);
        double baseline = y + height - (font.Ascent * _fontSize);
        int drawn = 0;
        double savedWord = _wordSpacing;
        try
        {
            foreach ((string line, bool ends) in lines)
            {
                if (baseline + (font.Descent * _fontSize) < y - 1e-9)
                {
                    break;
                }

                if (alignment == PdfTextAlignment.Justify && !ends && line.Contains(' ', StringComparison.Ordinal))
                {
                    _wordSpacing = savedWord;
                    double natural = MeasureText(line);
                    int gaps = 0;
                    foreach (char ch in line)
                    {
                        if (ch == ' ')
                        {
                            gaps++;
                        }
                    }

                    _wordSpacing = savedWord + ((width - natural) / gaps);
                    DrawText(line, x, baseline);
                    _wordSpacing = savedWord;
                }
                else
                {
                    DrawTextAligned(line, x, baseline, width, alignment == PdfTextAlignment.Justify ? PdfTextAlignment.Left : alignment);
                }

                baseline -= _fontSize * lineSpacing;
                drawn++;
            }
        }
        finally
        {
            _wordSpacing = savedWord;
        }

        if (drawn == lines.Count)
        {
            return string.Empty;
        }

        var rest = new StringBuilder();
        for (int i = drawn; i < lines.Count; i++)
        {
            rest.Append(lines[i].Text);
            if (i < lines.Count - 1)
            {
                rest.Append(lines[i].EndsParagraph ? '\n' : ' ');
            }
        }

        return rest.ToString();
    }

    // ---- images and forms ----

    /// <summary>Draws an image from <see cref="PdfDocument.AddJpegImage"/> or <see cref="PdfDocument.AddRawImage"/> into the box with lower left (<paramref name="x"/>, <paramref name="y"/>) and the given size, in the current coordinates. A negative size mirrors it.</summary>
    public void DrawImage(PdfCreatedImage image, double x, double y, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Document != Document)
        {
            throw new ArgumentException("The image belongs to another document.", nameof(image));
        }

        PlaceXObject(image.Reference, x, y, width, height);
    }

    /// <summary>Draws an image XObject of this document that was read from a page (<see cref="PdfImage.Stream"/>) again.</summary>
    /// <exception cref="ArgumentException">The image is inline, or is not an indirect object of this document.</exception>
    public void DrawImage(PdfImage image, double x, double y, double width, double height)
    {
        if (image.Stream is not { Reference: { } reference } stream || stream.Resolver.Doc != Document)
        {
            throw new ArgumentException("Only an image XObject of this document can be drawn again (not an inline image or another document's).", nameof(image));
        }

        PlaceXObject(reference, x, y, width, height);
    }

    private void PlaceXObject(PdfReference reference, double x, double y, double width, double height)
    {
        CheckOpen();
        string name = ResourceName(_xobjects, "OpIm", reference);
        Emit("q\n" + Num(width) + " 0 0 " + Num(height) + " " + Num(x) + " " + Num(y) + " cm\n/" + name + " Do\nQ\n");
    }

    /// <summary>Draws a form made by <see cref="PdfDocument.BeginForm"/>, with its coordinate origin at the current origin (use <see cref="Translate"/>, <see cref="Scale"/> and <see cref="Rotate"/> to place it).</summary>
    public void DrawForm(PdfFormXObject form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (form.Document != Document)
        {
            throw new ArgumentException("The form belongs to another document.", nameof(form));
        }

        DrawFormReference(form.Reference);
    }

    /// <summary>Draws a Form XObject of this document (for example one read from a page's resources) again, at the current origin.</summary>
    /// <exception cref="ArgumentException">The stream is not a Form XObject that is an indirect object of this document.</exception>
    public void DrawForm(PdfStream form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (form.Reference is not { } reference || form.Resolver.Doc != Document || form.Dictionary.GetName("Subtype") != "Form")
        {
            throw new ArgumentException("The stream must be a Form XObject that is an indirect object of this document.", nameof(form));
        }

        DrawFormReference(reference);
    }

    private void DrawFormReference(PdfReference reference)
    {
        CheckOpen();
        string name = ResourceName(_xobjects, "OpFm", reference);
        Emit("/" + name + " Do\n");
    }

    private string ResourceName(List<(string Name, PdfObject Value)> list, string prefix, PdfReference reference)
    {
        if (_nameByObject.TryGetValue(reference.Number, out string? known))
        {
            return known;
        }

        string name = _names.Next(prefix);
        list.Add((name, reference));
        _nameByObject[reference.Number] = name;
        return name;
    }

    /// <summary>Hands out resource names that are not used by the target's existing resources.</summary>
    private sealed class NameAllocator
    {
        private readonly HashSet<string> _taken = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _next = new(StringComparer.Ordinal);

        public NameAllocator(PdfDictionary? existing)
        {
            if (existing is null)
            {
                return;
            }

            foreach (string category in (string[])["Font", "XObject", "ExtGState", "Shading"])
            {
                if (existing.GetDictionary(category) is { } d)
                {
                    foreach (string k in d.Keys)
                    {
                        _taken.Add(k);
                    }
                }
            }
        }

        public string Next(string prefix)
        {
            _next.TryGetValue(prefix, out int n);
            string name;
            do
            {
                n++;
                name = prefix + n.ToString(CultureInfo.InvariantCulture);
            }
            while (_taken.Contains(name));
            _next[prefix] = n;
            _taken.Add(name);
            return name;
        }
    }
}
