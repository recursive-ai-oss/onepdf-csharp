namespace OnePdf.Text;

/// <summary>
/// The glyph producer: interprets a page into text glyphs. A device receives the page's glyph runs (no outlines, no Type 3
/// procedures: only text, positions and advances are needed) and turns each shown character into a <see cref="PdfTextGlyph"/> in
/// upright page space with its colour, render mode, visibility, clip state, marked-content id and <c>/ActualText</c> span.
/// </summary>
internal sealed class PdfGlyphSource(PdfDocument document) : IGlyphSource
{
    private static readonly PdfInterpretOptions InterpretOptions = new() { GlyphOutlines = false, Annotations = PdfAnnotationMode.None };

    public int PageCount => document.Pages.Count;

    public PdfPageGlyphs GetPage(int pageIndex, CancellationToken cancellationToken)
    {
        PdfPage page = document.Pages[pageIndex];
        var device = new TextGlyphDevice(pageIndex);
        page.Interpret(device, InterpretOptions, cancellationToken);
        return device.Result();
    }
}

/// <summary>Collects the glyphs of one page. Calls come from one thread.</summary>
internal sealed class TextGlyphDevice(int pageIndex) : PdfDevice
{
    /// <summary>Glyphs smaller than this (page units) are not text anybody can see or read.</summary>
    private const double MinSize = 1e-6;

    private readonly List<PdfTextGlyph> _glyphs = [];
    private readonly List<PdfBounds> _clips = [];
    private readonly List<(int Mcid, bool Span)> _marked = [];
    private readonly Dictionary<PdfDictionary, int> _fontIds = new(ReferenceEqualityComparer.Instance);
    private double _width, _height;
    private PdfBounds _clip = PdfBounds.Unbounded;
    private int _skip;
    private int _spanCounter;
    private int _span;
    private string? _spanText;
    private bool _spanFirst;
    private int _mcid = -1;

    public PdfPageGlyphs Result() => new(pageIndex, _width, _height, _glyphs);

    public override void BeginPage(in PdfPageInfo page)
    {
        _width = page.Width;
        _height = page.Height;
    }

    // Text inside soft masks, annotations (not requested) and Type 3 procedures is not page text.
    public override void BeginSoftMask(in PdfSoftMaskInfo mask, in PdfGraphicsState state) => _skip++;

    public override void EndSoftMask() => _skip--;

    public override void BeginType3Glyph(in PdfType3GlyphInfo glyph, in PdfGraphicsState state) => _skip++;

    public override void EndType3Glyph() => _skip--;

    public override void BeginAnnotation(in PdfAnnotationInfo annotation) => _skip++;

    public override void EndAnnotation() => _skip--;

    // ---- clipping: a conservative box in upright page space ----

    public override void PushClip(in PdfPath path, bool evenOdd, in PdfGraphicsState state)
    {
        _clips.Add(_clip);
        if (path.IsEmpty)
        {
            _clip = new PdfBounds(0, 0, 0, 0);
            return;
        }

        PdfBounds b = state.Ctm.TransformBounds(path.ControlBounds());
        if (!b.IsFinite)
        {
            return;
        }

        // Output space has y down; the glyphs are in y-up page space.
        var up = new PdfBounds(b.Left, _height - b.Top, b.Right, _height - b.Bottom);
        _clip = new PdfBounds(Math.Max(_clip.Left, up.Left), Math.Max(_clip.Bottom, up.Bottom), Math.Min(_clip.Right, up.Right), Math.Min(_clip.Top, up.Top));
    }

    public override void PushTextClip(in PdfGraphicsState state) => _clips.Add(_clip);

    public override void PopClip()
    {
        if (_clips.Count > 0)
        {
            _clip = _clips[^1];
            _clips.RemoveAt(_clips.Count - 1);
        }
    }

    // ---- marked content ----

    public override void BeginMarkedContent(string tag, PdfDictionary? properties)
    {
        int mcid = -1;
        bool span = false;
        if (properties is not null)
        {
            if (properties.Get("MCID") is PdfInteger { Value: >= 0 and <= int.MaxValue } id)
            {
                mcid = (int)id.Value;
            }

            // Nested spans: the outermost /ActualText wins; its text replaces everything inside it.
            if (_span == 0 && properties.Get("ActualText") is PdfString at)
            {
                _span = ++_spanCounter;
                _spanText = at.GetText();
                _spanFirst = true;
                span = true;
            }
        }

        _marked.Add((mcid, span));
        if (mcid >= 0)
        {
            _mcid = mcid;
        }
    }

    public override void EndMarkedContent()
    {
        if (_marked.Count == 0)
        {
            return;
        }

        (int _, bool span) = _marked[^1];
        _marked.RemoveAt(_marked.Count - 1);
        if (span)
        {
            _span = 0;
            _spanText = null;
        }

        _mcid = -1;
        for (int i = _marked.Count - 1; i >= 0; i--)
        {
            if (_marked[i].Mcid >= 0)
            {
                _mcid = _marked[i].Mcid;
                break;
            }
        }
    }

    // ---- glyphs ----

    public override void ShowGlyphRun(in PdfGlyphRun run, in PdfGraphicsState state)
    {
        if (_skip > 0)
        {
            return;
        }

        int mode = (int)run.RenderMode;
        bool visible = mode != 3 && mode != 7 && ColorVisible(mode, state);
        int fill = RgbOf(state.FillColor);
        int fontId = -1;
        if (run.Font is not null && !_fontIds.TryGetValue(run.Font, out fontId))
        {
            fontId = _fontIds.Count;
            _fontIds[run.Font] = fontId;
        }

        string? fontName = FontName(run.Font);
        double fs = run.FontSize, th = run.HorizontalScale;
        var scale = new PdfMatrix(fs * th, 0, 0, fs, 0, run.Rise);
        var toPage = new PdfMatrix(1, 0, 0, -1, 0, _height);
        ReadOnlySpan<PdfGlyph> glyphs = run.Glyphs.Span;
        for (int i = 0; i < glyphs.Length; i++)
        {
            ref readonly PdfGlyph g = ref glyphs[i];
            if (g.Unicode is null && g.GlyphId < 0 && g.Width == 0)
            {
                continue;
            }

            // The glyph box of a vertical glyph hangs from the text position (origin 1), not from the glyph origin.
            double ox = g.OffsetX, oy = g.OffsetY;
            if (run.IsVertical)
            {
                ox += g.VectorX * fs * th;
                oy += g.VectorY * fs;
            }

            PdfMatrix m = scale.Then(PdfMatrix.Translation(ox, oy)).Then(run.TextMatrix).Then(state.Ctm).Then(toPage);
            if (!m.IsFinite)
            {
                continue;
            }

            double size = Math.Sqrt((m.C * m.C) + (m.D * m.D));
            if (!(size > MinSize))
            {
                continue;
            }

            var glyph = new PdfTextGlyph
            {
                Text = g.Unicode ?? "\uFFFD",
                FontId = fontId,
                FontName = fontName,
                Matrix = new PdfTextMatrix(m.A, m.B, m.C, m.D, m.E, m.F),
                Advance = run.IsVertical ? Math.Abs(g.VerticalAdvance) : g.Width,
                IsVertical = run.IsVertical,
                FillColor = fill,
                RenderMode = mode,
                IsVisible = visible,
                MarkedContentId = _mcid,
            };
            PdfRectangle b = glyph.Bounds;
            bool clipped = b.Right < _clip.Left || b.Left > _clip.Right || b.Top < _clip.Bottom || b.Bottom > _clip.Top;
            glyph = glyph with { IsClipped = clipped };
            if (_span != 0)
            {
                glyph = glyph with { ActualTextSpan = _span, ActualText = _spanFirst ? _spanText : null };
                _spanFirst = false;
            }

            _glyphs.Add(glyph);
        }
    }

    private static bool ColorVisible(int mode, in PdfGraphicsState state)
    {
        bool fills = mode is 0 or 2 or 4 or 6;
        bool strokes = mode is 1 or 2 or 5 or 6;
        return (fills && state.FillAlpha > 0) || (strokes && state.StrokeAlpha > 0);
    }

    private static string? FontName(PdfDictionary? font)
    {
        string? n = font?.GetName("BaseFont");
        if (n is null)
        {
            return null;
        }

        // Subset tag: six capitals and a plus sign.
        if (n.Length > 7 && n[6] == '+')
        {
            bool tag = true;
            for (int i = 0; i < 6; i++)
            {
                if (n[i] is < 'A' or > 'Z')
                {
                    tag = false;
                    break;
                }
            }

            if (tag)
            {
                return n[7..];
            }
        }

        return n;
    }

    /// <summary>The colour as 0xRRGGBB for the device spaces and spaces with their component count; -1 for patterns.</summary>
    private static int RgbOf(in PdfColor c)
    {
        if (c.Pattern is not null)
        {
            return -1;
        }

        double r, g, b;
        switch (c.Space.ComponentCount)
        {
            case 1:
                r = g = b = c.C0;
                break;
            case 3:
                if (c.Space.Kind == PdfColorSpaceKind.Lab)
                {
                    return -1;
                }

                (r, g, b) = (c.C0, c.C1, c.C2);
                break;
            case 4:
                {
                    double k = 1 - Clamp(c.C3);
                    r = (1 - Clamp(c.C0)) * k;
                    g = (1 - Clamp(c.C1)) * k;
                    b = (1 - Clamp(c.C2)) * k;
                    break;
                }

            default:
                return -1;
        }

        return (Byte(r) << 16) | (Byte(g) << 8) | Byte(b);
    }

    private static double Clamp(double v) => double.IsNaN(v) ? 0 : Math.Clamp(v, 0, 1);

    private static int Byte(double v) => (int)Math.Round(Clamp(v) * 255, MidpointRounding.AwayFromZero);
}
