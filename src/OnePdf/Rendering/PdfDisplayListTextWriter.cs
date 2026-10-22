using System.Globalization;
using System.Text;

namespace OnePdf;

/// <summary>
/// Writes the text form of a display list while receiving device calls (see <c>docs/display-list-format.md</c>). Used as
/// a device directly to snapshot an interpretation without recording it.
/// </summary>
internal sealed class PdfDisplayListTextWriter(TextWriter output) : IPdfDevice
{
    /// <summary>The version of the text format. Bump it with any change to the output.</summary>
    public const int FormatVersion = 3;

    /// <summary>First token of the header line.</summary>
    public const string Magic = "onepdf-display-list";

    /// <summary>Every record keyword the writer can emit, for the format document's consistency check.</summary>
    public static readonly string[] Records =
    [
        "page", "end-page", "gs", "fill", "stroke", "clip", "text-clip", "pop-clip", "text", "image", "shading", "pattern-def", "end-pattern-def",
        "begin-group", "end-group", "begin-softmask", "end-softmask", "begin-mc", "end-mc", "begin-annot", "end-annot",
    ];

    private const int MaxDictDepth = 8;
    private readonly StringBuilder _sb = new();
    private PdfGraphicsState _lastState;
    private bool _haveState;
    private int _indent;
    private Dictionary<PdfPattern, int> _patternIds = new(ReferenceEqualityComparer.Instance);

    public void WriteHeader() => output.Write(Magic + " " + FormatVersion.ToString(CultureInfo.InvariantCulture) + "\n");

    // ---- numbers ----

    /// <summary>
    /// Formats a number rounded to four decimals (ties to even), without exponent, trailing zeros or negative zero. The
    /// digits are produced by integer arithmetic so every platform prints the same text. Non-finite values print as
    /// <c>nan</c>, <c>inf</c>, <c>-inf</c>; magnitudes of 1e14 or more print as <c>huge</c> or <c>-huge</c>.
    /// </summary>
    public static string Num(double v)
    {
        var sb = new StringBuilder(16);
        AppendNum(sb, v);
        return sb.ToString();
    }

    public static void AppendNum(StringBuilder sb, double v)
    {
        if (double.IsNaN(v))
        {
            sb.Append("nan");
            return;
        }

        if (double.IsPositiveInfinity(v))
        {
            sb.Append("inf");
            return;
        }

        if (double.IsNegativeInfinity(v))
        {
            sb.Append("-inf");
            return;
        }

        if (Math.Abs(v) >= 1e14)
        {
            sb.Append(v < 0 ? "-huge" : "huge");
            return;
        }

        long scaled = (long)Math.Round(v * 10000.0, MidpointRounding.ToEven);
        if (scaled == 0)
        {
            sb.Append('0');
            return;
        }

        if (scaled < 0)
        {
            sb.Append('-');
            scaled = -scaled;
        }

        long whole = scaled / 10000;
        int frac = (int)(scaled % 10000);
        sb.Append(whole.ToString(CultureInfo.InvariantCulture));
        if (frac != 0)
        {
            sb.Append('.');
            int div = 1000;
            while (frac != 0)
            {
                sb.Append((char)('0' + (frac / div)));
                frac %= div;
                div /= 10;
            }
        }
    }

    private static void Matrix(StringBuilder sb, in PdfMatrix m)
    {
        sb.Append('[');
        AppendNum(sb, m.A);
        sb.Append(' ');
        AppendNum(sb, m.B);
        sb.Append(' ');
        AppendNum(sb, m.C);
        sb.Append(' ');
        AppendNum(sb, m.D);
        sb.Append(' ');
        AppendNum(sb, m.E);
        sb.Append(' ');
        AppendNum(sb, m.F);
        sb.Append(']');
    }

    private static void Box(StringBuilder sb, in PdfBounds b)
    {
        AppendNum(sb, b.Left);
        sb.Append(' ');
        AppendNum(sb, b.Bottom);
        sb.Append(' ');
        AppendNum(sb, b.Right);
        sb.Append(' ');
        AppendNum(sb, b.Top);
    }

    private static void Hex(StringBuilder sb, ReadOnlySpan<byte> bytes)
    {
        const string digits = "0123456789abcdef";
        foreach (byte b in bytes)
        {
            sb.Append(digits[b >> 4]);
            sb.Append(digits[b & 15]);
        }
    }

    private static void Name(StringBuilder sb, string name)
    {
        sb.Append('/');
        foreach (char c in name)
        {
            if (c is > ' ' and < (char)127 and not ('#' or '/' or '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '%'))
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('#');
                sb.Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
            }
        }
    }

    internal static ulong Fnv1a(ReadOnlySpan<byte> data)
    {
        ulong h = 14695981039346656037UL;
        foreach (byte b in data)
        {
            h ^= b;
            h *= 1099511628211UL;
        }

        return h;
    }

    private void Color(StringBuilder sb, in PdfColor c)
    {
        PdfColorSpace s = c.Space;
        if (s.Kind == PdfColorSpaceKind.Pattern)
        {
            sb.Append("pattern(");
            if (c.Pattern is { } pat)
            {
                sb.Append('#').Append((_patternIds.TryGetValue(pat, out int pid) ? pid : 0).ToString(CultureInfo.InvariantCulture)).Append(' ');
                Name(sb, pat.Name);
            }
            if (s.PatternBase is { } b)
            {
                sb.Append(' ').Append(b.Name);
                for (int i = 0; i < b.ComponentCount; i++)
                {
                    sb.Append(' ');
                    AppendNum(sb, c[i]);
                }
            }

            sb.Append(')');
            return;
        }

        sb.Append(s.Name);
        if (s.Kind == PdfColorSpaceKind.IccBased)
        {
            sb.Append(':').Append(s.IccProfile.Length.ToString(CultureInfo.InvariantCulture));
            sb.Append(':').Append(Fnv1a(s.IccProfile.Span).ToString("x16", CultureInfo.InvariantCulture));
        }

        sb.Append('(');
        for (int i = 0; i < s.ComponentCount; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            AppendNum(sb, c[i]);
        }

        sb.Append(')');
    }

    private static void Dict(StringBuilder sb, PdfObject? o, int depth)
    {
        switch (o)
        {
            case null or PdfNull:
                sb.Append("null");
                break;
            case PdfBoolean b:
                sb.Append(b.Value ? "true" : "false");
                break;
            case PdfInteger i:
                sb.Append(i.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case PdfReal r:
                AppendNum(sb, r.Value);
                break;
            case PdfString s:
                sb.Append('<');
                Hex(sb, s.Bytes.Span);
                sb.Append('>');
                break;
            case PdfName n:
                Name(sb, n.Value);
                break;
            case PdfReference r:
                sb.Append(r.Number.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(r.Generation.ToString(CultureInfo.InvariantCulture)).Append(" R");
                break;
            case PdfStream st:
                sb.Append("stream");
                Dict(sb, st.Dictionary, depth);
                break;
            case PdfArray a:
                if (depth >= MaxDictDepth)
                {
                    sb.Append("[...]");
                    break;
                }

                sb.Append('[');
                for (int k = 0; k < a.Count && k < 4096; k++)
                {
                    if (k > 0)
                    {
                        sb.Append(' ');
                    }

                    Dict(sb, a.GetRaw(k), depth + 1);
                }

                sb.Append(']');
                break;
            case PdfDictionary d:
                if (depth >= MaxDictDepth)
                {
                    sb.Append("<<...>>");
                    break;
                }

                sb.Append("<<");
                var keys = new List<string>(d.Keys);
                keys.Sort(StringComparer.Ordinal);
                bool first = true;
                foreach (string key in keys)
                {
                    if (!first)
                    {
                        sb.Append(' ');
                    }

                    first = false;
                    Name(sb, key);
                    sb.Append(' ');
                    Dict(sb, d.GetRaw(key), depth + 1);
                }

                sb.Append(">>");
                break;
            default:
                sb.Append('?');
                break;
        }
    }

    private static void Path(StringBuilder sb, in PdfPath p)
    {
        ReadOnlySpan<byte> verbs = p.VerbBytes.Span;
        ReadOnlySpan<double> pts = p.Points.Span;
        int pi = 0;
        for (int i = 0; i < verbs.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            switch ((PdfPathVerb)verbs[i])
            {
                case PdfPathVerb.Move:
                case PdfPathVerb.Line:
                    sb.Append(verbs[i] == 0 ? 'M' : 'L').Append(' ');
                    Pts(sb, pts, ref pi, 1);
                    break;
                case PdfPathVerb.Curve:
                    sb.Append("C ");
                    Pts(sb, pts, ref pi, 3);
                    break;
                default:
                    sb.Append('Z');
                    break;
            }
        }
    }

    private static void Pts(StringBuilder sb, ReadOnlySpan<double> pts, ref int pi, int n)
    {
        for (int k = 0; k < n * 2; k++)
        {
            if (k > 0)
            {
                sb.Append(' ');
            }

            AppendNum(sb, pi < pts.Length ? pts[pi] : double.NaN);
            pi++;
        }
    }

    // ---- lines ----

    private void Line(string text)
    {
        for (int i = 0; i < _indent; i++)
        {
            output.Write("  ");
        }

        output.Write(text);
        output.Write('\n');
    }

    private void EmitState(in PdfGraphicsState s)
    {
        if (_haveState && _lastState.Equals(s))
        {
            return;
        }

        DefinePattern(s.FillColor.Pattern);
        DefinePattern(s.StrokeColor.Pattern);
        _lastState = s;
        _haveState = true;
        StringBuilder sb = _sb;
        sb.Clear();
        sb.Append("gs ctm=");
        Matrix(sb, s.Ctm);
        sb.Append(" fill=");
        Color(sb, s.FillColor);
        sb.Append(" stroke=");
        Color(sb, s.StrokeColor);
        sb.Append(" lw=");
        AppendNum(sb, s.LineWidth);
        sb.Append(" cap=").Append(s.LineCap.ToString(CultureInfo.InvariantCulture));
        sb.Append(" join=").Append(s.LineJoin.ToString(CultureInfo.InvariantCulture));
        sb.Append(" miter=");
        AppendNum(sb, s.MiterLimit);
        sb.Append(" dash=[");
        ReadOnlySpan<double> d = s.Dash.Span;
        for (int i = 0; i < d.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            AppendNum(sb, d[i]);
        }

        sb.Append("] phase=");
        AppendNum(sb, s.DashPhase);
        sb.Append(" flat=");
        AppendNum(sb, s.Flatness);
        sb.Append(" ri=").Append(s.RenderingIntent);
        sb.Append(" fa=");
        AppendNum(sb, s.FillAlpha);
        sb.Append(" sa=");
        AppendNum(sb, s.StrokeAlpha);
        sb.Append(" bm=").Append(s.BlendMode);
        sb.Append(" sm=").Append(s.SoftMaskId.ToString(CultureInfo.InvariantCulture));
        sb.Append(" ais=").Append(s.AlphaIsShape ? '1' : '0');
        sb.Append(" op=").Append(s.FillOverprint ? '1' : '0').Append(s.StrokeOverprint ? '1' : '0').Append(s.OverprintMode.ToString(CultureInfo.InvariantCulture));
        Line(sb.ToString());
    }

    public void BeginPage(in PdfPageInfo page)
    {
        _haveState = false;
        StringBuilder sb = _sb;
        sb.Clear();
        sb.Append("page box=");
        Box(sb, page.Box);
        sb.Append(" name=").Append(page.BoxName.ToString());
        sb.Append(" rotate=").Append(page.Rotation.ToString(CultureInfo.InvariantCulture));
        sb.Append(" unit=");
        AppendNum(sb, page.UserUnit);
        sb.Append(" scale=");
        AppendNum(sb, page.Scale);
        sb.Append(" size=");
        AppendNum(sb, page.Width);
        sb.Append(' ');
        AppendNum(sb, page.Height);
        sb.Append(" base=");
        Matrix(sb, page.BaseMatrix);
        Line(sb.ToString());
        _indent++;
    }

    public void EndPage()
    {
        _indent = Math.Max(0, _indent - 1);
        Line("end-page");
    }

    public void FillPath(in PdfPath path, bool evenOdd, in PdfGraphicsState state)
    {
        EmitState(state);
        _sb.Clear();
        _sb.Append("fill ").Append(evenOdd ? "evenodd " : "nonzero ");
        Path(_sb, path);
        Line(_sb.ToString());
    }

    public void StrokePath(in PdfPath path, in PdfGraphicsState state)
    {
        EmitState(state);
        _sb.Clear();
        _sb.Append("stroke ");
        Path(_sb, path);
        Line(_sb.ToString());
    }

    public void PushClip(in PdfPath path, bool evenOdd, in PdfGraphicsState state)
    {
        EmitState(state);
        _sb.Clear();
        _sb.Append("clip ").Append(evenOdd ? "evenodd " : "nonzero ");
        Path(_sb, path);
        Line(_sb.ToString());
        _indent++;
    }

    public void PushTextClip(in PdfGraphicsState state)
    {
        EmitState(state);
        Line("text-clip");
        _indent++;
    }

    public void PopClip()
    {
        _indent = Math.Max(0, _indent - 1);
        Line("pop-clip");
    }

    public void ShowGlyphRun(in PdfGlyphRun run, in PdfGraphicsState state)
    {
        EmitState(state);
        StringBuilder sb = _sb;
        sb.Clear();
        sb.Append("text font=");
        Name(sb, run.FontName);
        sb.Append(" size=");
        AppendNum(sb, run.FontSize);
        sb.Append(" mode=").Append(((int)run.RenderMode).ToString(CultureInfo.InvariantCulture));
        sb.Append(" tc=");
        AppendNum(sb, run.CharSpacing);
        sb.Append(" tw=");
        AppendNum(sb, run.WordSpacing);
        sb.Append(" th=");
        AppendNum(sb, run.HorizontalScale);
        sb.Append(" rise=");
        AppendNum(sb, run.Rise);
        sb.Append(" tm=");
        Matrix(sb, run.TextMatrix);
        sb.Append(" vert=").Append(run.IsVertical ? '1' : '0');
        sb.Append(" adv=");
        AppendNum(sb, run.AdvanceX);
        sb.Append(' ');
        AppendNum(sb, run.AdvanceY);
        sb.Append(" codes=");
        Hex(sb, run.Codes.Span);
        sb.Append(" glyphs=[");
        ReadOnlySpan<PdfGlyph> g = run.Glyphs.Span;
        for (int i = 0; i < g.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append(g[i].Code.ToString(CultureInfo.InvariantCulture)).Append('@');
            AppendNum(sb, g[i].OffsetX);
            sb.Append(',');
            AppendNum(sb, g[i].OffsetY);
            sb.Append(',');
            AppendNum(sb, g[i].Width);
            if (g[i].GlyphId >= 0)
            {
                sb.Append(":g").Append(g[i].GlyphId.ToString(CultureInfo.InvariantCulture));
            }

            if (g[i].Unicode is { } u)
            {
                sb.Append(":u");
                Hex(sb, Encoding.UTF8.GetBytes(u));
            }
        }

        sb.Append(']');
        Line(sb.ToString());
    }

    public void DrawImage(in PdfImage image, in PdfGraphicsState state)
    {
        EmitState(state);
        StringBuilder sb = _sb;
        sb.Clear();
        sb.Append("image ");
        if (image.IsInline)
        {
            sb.Append("inline");
        }
        else
        {
            Name(sb, image.Name);
        }

        long length = -1;
        ulong hash = 0;
        try
        {
            ReadOnlyMemory<byte> data = image.GetEncodedData();
            length = data.Length;
            hash = Fnv1a(data.Span);
        }
        catch (PdfException)
        {
            // Unreadable image data is reported as length -1; the interpreter has already recorded why.
        }

        sb.Append(" bytes=").Append(length.ToString(CultureInfo.InvariantCulture));
        sb.Append(" fnv=").Append(hash.ToString("x16", CultureInfo.InvariantCulture));
        sb.Append(" data=");
        FinishedImage(sb, image);
        sb.Append(" dict=");
        Dict(sb, image.Dictionary, 0);
        Line(sb.ToString());
    }

    /// <summary>Describes the finished image: <c>none</c>, <c>encoded</c>, <c>stencil</c> or <c>decoded</c>, with sizes and sample hashes.</summary>
    private static void FinishedImage(StringBuilder sb, in PdfImage image)
    {
        PdfImageData? d;
        try
        {
            d = image.GetData();
        }
        catch (PdfException)
        {
            sb.Append("limit");
            return;
        }

        if (d is null)
        {
            sb.Append("none");
            return;
        }

        CultureInfo ci = CultureInfo.InvariantCulture;
        if (!d.IsDecoded)
        {
            sb.Append("encoded:").Append(d.Width.ToString(ci)).Append('x').Append(d.Height.ToString(ci));
            if (d.Alpha is { } ea)
            {
                sb.Append(":alpha=").Append(ea.Width.ToString(ci)).Append('x').Append(ea.Height.ToString(ci)).Append(':').Append(Fnv1a(ea.Values.Span).ToString("x16", ci));
            }

            return;
        }

        if (d.IsStencil)
        {
            sb.Append("stencil:").Append(d.Width.ToString(ci)).Append('x').Append(d.Height.ToString(ci)).Append(':').Append(Fnv1a(d.Alpha!.Values.Span).ToString("x16", ci));
            return;
        }

        sb.Append("decoded:").Append(d.Width.ToString(ci)).Append('x').Append(d.Height.ToString(ci)).Append(':').Append(d.ColorSpace!.Name);
        sb.Append(':').Append(Fnv1a(d.Samples.Span).ToString("x16", ci));
        if (d.Alpha is { } a)
        {
            sb.Append(":alpha=").Append(a.Width.ToString(ci)).Append('x').Append(a.Height.ToString(ci)).Append(':').Append(Fnv1a(a.Values.Span).ToString("x16", ci));
        }
    }

    public void PaintShading(PdfShading shading, in PdfGraphicsState state)
    {
        EmitState(state);
        StringBuilder sb = _sb;
        sb.Clear();
        ShadingFields(sb, shading);
        Line(sb.ToString());
    }

    /// <summary>Writes the definition of a pattern the first time a colour uses it (the cell's records are nested inside).</summary>
    private void DefinePattern(PdfPattern? pattern)
    {
        if (pattern is null || _patternIds.ContainsKey(pattern))
        {
            return;
        }

        int id = _patternIds.Count + 1;
        _patternIds[pattern] = id;
        StringBuilder sb = _sb;
        sb.Clear();
        sb.Append("pattern-def #").Append(id.ToString(CultureInfo.InvariantCulture)).Append(' ');
        Name(sb, pattern.Name);
        sb.Append(' ');
        if (pattern is PdfTilingPattern t)
        {
            sb.Append("tiling paint=").Append(t.IsColored ? '1' : '2');
            sb.Append(" tile=").Append(t.TilingType.ToString(CultureInfo.InvariantCulture));
            sb.Append(" bbox=");
            Box(sb, t.BBox);
            sb.Append(" step=");
            AppendNum(sb, t.XStep);
            sb.Append(' ');
            AppendNum(sb, t.YStep);
        }
        else
        {
            sb.Append("shading");
        }

        sb.Append(" matrix=");
        Matrix(sb, pattern.Matrix);
        Line(sb.ToString());
        _indent++;
        if (pattern is PdfTilingPattern { Cell: { } cell })
        {
            var nested = new PdfDisplayListTextWriter(output) { _patternIds = _patternIds, _indent = _indent };
            cell.Replay(nested);
        }
        else if (pattern is PdfShadingPattern sp)
        {
            sb.Clear();
            ShadingFields(sb, sp.Shading);
            Line(sb.ToString());
        }

        _indent--;
        Line("end-pattern-def");
    }

    private static void ShadingFields(StringBuilder sb, PdfShading sh)
    {
        CultureInfo ci = CultureInfo.InvariantCulture;
        sb.Append("shading type=").Append(sh.ShadingType.ToString(ci));
        sb.Append(" cs=").Append(sh.FinalSpace.Name);
        sb.Append(" fn=").Append(sh.HasFunction ? '1' : '0');
        sb.Append(" bbox=");
        if (sh.BBox is { } bb)
        {
            Box(sb, bb);
        }
        else
        {
            sb.Append("none");
        }

        sb.Append(" bg=");
        Span<double> c = stackalloc double[32];
        if (sh.TryGetBackground(c))
        {
            Comps(sb, c[..sh.ColorComponentCount]);
        }
        else
        {
            sb.Append("none");
        }

        sb.Append(" aa=").Append(sh.AntiAlias ? '1' : '0');
        Span<double> p = stackalloc double[32];
        switch (sh.ShadingType)
        {
            case 1:
                sb.Append(" domain=");
                Comps(sb, [.. sh.Domain]);
                sb.Append(" matrix=");
                Matrix(sb, sh.Matrix);
                p[0] = (sh.Domain[0] + sh.Domain[1]) / 2;
                p[1] = (sh.Domain[2] + sh.Domain[3]) / 2;
                sb.Append(" at-centre=");
                sh.Evaluate(p[..2], c);
                Comps(sb, c[..sh.ColorComponentCount]);
                break;
            case 2 or 3:
                sb.Append(" coords=");
                Comps(sb, [.. sh.Coords]);
                sb.Append(" domain=");
                Comps(sb, [.. sh.Domain]);
                sb.Append(" extend=").Append(sh.ExtendStart ? '1' : '0').Append(sh.ExtendEnd ? '1' : '0');
                for (int k = 0; k < 3; k++)
                {
                    p[0] = sh.Domain[0] + (k * 0.5 * (sh.Domain[1] - sh.Domain[0]));
                    sh.Evaluate(p[..1], c);
                    sb.Append(k == 0 ? " at-start=" : k == 1 ? " at-mid=" : " at-end=");
                    Comps(sb, c[..sh.ColorComponentCount]);
                }

                break;
            default:
            {
                PdfShadingMesh m = sh.Mesh!;
                sb.Append(" vertices=").Append(m.VertexCount.ToString(ci));
                sb.Append(" triangles=").Append(m.TriangleCount.ToString(ci));
                if (m.PatchCount > 0)
                {
                    sb.Append(" patches=").Append(m.PatchCount.ToString(ci));
                    sb.Append(" div=").Append(m.PatchDivisions.ToString(ci));
                }

                // A hash of the rounded vertex data and the triangle indices stands for the mesh.
                ulong h = 14695981039346656037UL;
                var tmp = new StringBuilder(24);
                foreach (double v in m.Vertices.Span)
                {
                    tmp.Clear();
                    AppendNum(tmp, v);
                    tmp.Append(' ');
                    for (int k = 0; k < tmp.Length; k++)
                    {
                        h = (h ^ tmp[k]) * 1099511628211UL;
                    }
                }

                foreach (int t in m.Triangles.Span)
                {
                    h = (h ^ (uint)t) * 1099511628211UL;
                }

                sb.Append(" fnv=").Append(h.ToString("x16", ci));
                if (m.IsTruncated)
                {
                    sb.Append(" truncated");
                }

                break;
            }
        }
    }

    private static void Comps(StringBuilder sb, ReadOnlySpan<double> v)
    {
        sb.Append('(');
        for (int i = 0; i < v.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            AppendNum(sb, v[i]);
        }

        sb.Append(')');
    }

    public void BeginGroup(in PdfGroupInfo group, in PdfGraphicsState state)
    {
        EmitState(state);
        StringBuilder sb = _sb;
        sb.Clear();
        sb.Append("begin-group bbox=");
        Box(sb, group.Bounds);
        sb.Append(" isolated=").Append(group.Isolated ? '1' : '0');
        sb.Append(" knockout=").Append(group.Knockout ? '1' : '0');
        sb.Append(" cs=");
        Dict(sb, group.ColorSpace, 0);
        Line(sb.ToString());
        _indent++;
    }

    public void EndGroup()
    {
        _indent = Math.Max(0, _indent - 1);
        Line("end-group");
    }

    public void BeginSoftMask(in PdfSoftMaskInfo mask, in PdfGraphicsState state)
    {
        EmitState(state);
        StringBuilder sb = _sb;
        sb.Clear();
        sb.Append("begin-softmask id=").Append(mask.Id.ToString(CultureInfo.InvariantCulture));
        sb.Append(" type=").Append(mask.Luminosity ? "luminosity" : "alpha");
        sb.Append(" bbox=");
        Box(sb, mask.Bounds);
        sb.Append(" bc=");
        Dict(sb, mask.Backdrop, 0);
        sb.Append(" tr=");
        Dict(sb, mask.Transfer, 0);
        Line(sb.ToString());
        _indent++;
    }

    public void EndSoftMask()
    {
        _indent = Math.Max(0, _indent - 1);
        Line("end-softmask");
    }

    public void BeginMarkedContent(string tag, PdfDictionary? properties)
    {
        StringBuilder sb = _sb;
        sb.Clear();
        sb.Append("begin-mc ");
        Name(sb, tag);
        sb.Append(" props=");
        Dict(sb, properties, 0);
        Line(sb.ToString());
        _indent++;
    }

    public void EndMarkedContent()
    {
        _indent = Math.Max(0, _indent - 1);
        Line("end-mc");
    }

    public void BeginAnnotation(in PdfAnnotationInfo annotation)
    {
        StringBuilder sb = _sb;
        sb.Clear();
        sb.Append("begin-annot ");
        Name(sb, annotation.Subtype);
        sb.Append(" rect=");
        Box(sb, annotation.Rect);
        sb.Append(" flags=").Append(annotation.Flags.ToString(CultureInfo.InvariantCulture));
        Line(sb.ToString());
        _indent++;
    }

    public void EndAnnotation()
    {
        _indent = Math.Max(0, _indent - 1);
        Line("end-annot");
    }
}
