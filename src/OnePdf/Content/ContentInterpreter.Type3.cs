using OnePdf.FontPrograms;
using OnePdf.PdfFonts;

namespace OnePdf.Content;

internal sealed partial class ContentInterpreter
{
    private const int MaxCachedOutlines = 1 << 16;

    /// <summary>The outline of a glyph as a path in text-space units for font size 1; empty when the glyph has none.</summary>
    private PdfPath OutlineOf(FontGlyph g)
    {
        if (!g.Exists || g.Gid < 0 || g.OutlineMatrix is not { Length: 6 } m)
        {
            return default;
        }

        if (_outlinePaths.TryGetValue(g, out PdfPath hit))
        {
            return hit;
        }

        PdfPath path = GlyphPaths.Convert(g.GetOutline(), m);
        if (_outlinePaths.Count < MaxCachedOutlines)
        {
            _outlinePaths[g] = path;
        }

        return path;
    }

    /// <summary>
    /// Runs the glyph procedures of the glyphs just delivered in a run (ISO 32000-2 9.6.5): for each glyph with a procedure, the
    /// graphics state is saved, the CTM becomes the font matrix times the text space at the glyph, and the procedure runs as
    /// content between <see cref="IPdfDevice.BeginType3Glyph"/> and <see cref="IPdfDevice.EndType3Glyph"/>. A code with no
    /// procedure paints nothing.
    /// </summary>
    private void ShowType3Glyphs(FontInfo fi, int count)
    {
        var font = (Type3Font)fi.Font;
        double fs = _g.FontSize, th = _g.HorizScale, rise = _g.Rise;
        PdfMatrix tm = _tm;
        PdfMatrix ctm = _g.Ctm;
        double[] fm = font.FontMatrix;
        var fontMatrix = new PdfMatrix(fm[0], fm[1], fm[2], fm[3], fm[4], fm[5]);
        var scale = new PdfMatrix(fs * th, 0, 0, fs, 0, rise);
        PdfMatrix glyphToText = fontMatrix.Then(scale);

        // Text shown inside a glyph procedure needs its own buffers while this run's are still being read.
        PdfGlyph[] glyphs = _glyphs;
        FontGlyph?[] fontGlyphs = _fontGlyphs;
        if (_type3Depth == _spareBuffers.Count)
        {
            _spareBuffers.Add((new PdfGlyph[16], new FontGlyph?[16]));
        }

        (_glyphs, _fontGlyphs) = _spareBuffers[_type3Depth];
        _type3Depth++;
        try
        {
            for (int i = 0; i < count && !_stopped; i++)
            {
                FontGlyph? g = fontGlyphs[i];
                if (g?.CharProc is null)
                {
                    continue;
                }

                PdfMatrix m = glyphToText.Then(PdfMatrix.Translation(glyphs[i].OffsetX, glyphs[i].OffsetY)).Then(tm).Then(ctm);
                if (!m.IsFinite)
                {
                    continue;
                }

                RunType3Glyph(fi, font, g, glyphs[i], m);
            }
        }
        finally
        {
            _type3Depth--;
            _glyphs = glyphs;
            _fontGlyphs = fontGlyphs;
        }
    }

    private void RunType3Glyph(FontInfo fi, Type3Font font, FontGlyph glyph, in PdfGlyph shown, in PdfMatrix matrix)
    {
        PdfStream proc = glyph.CharProc!;
        if (_formDepth >= _limits.MaxRecursionDepth)
        {
            Report(PdfSeverity.Error, "Type 3 glyphs are nested deeper than the recursion limit; one was skipped.");
            return;
        }

        if (_saved.Count >= MaxSaveDepth)
        {
            Report(PdfSeverity.Error, "Graphics state nesting is too deep to run a Type 3 glyph.");
            return;
        }

        // A glyph procedure that shows text in its own font would never end; the set shared with forms and patterns also
        // catches cycles that pass through them.
        int number = proc.Reference?.Number ?? -1;
        if (number >= 0 && !_activeForms.Add(number))
        {
            Report(PdfSeverity.Warning, "A Type 3 glyph procedure uses itself (directly or through others); the inner use was skipped.");
            return;
        }

        PdfDictionary? savedRes = _res;
        int savedQBase = _qBase, savedMcBase = _mcBase, savedCompat = _compat, savedSkipped = _skippedSaves;
        bool savedInText = _inText, savedTextClip = _textClipPending, savedPending = _pendingClip, savedEo = _clipEvenOdd;
        bool savedNoColor = _noColor, savedInCharProc = _inCharProc;
        PdfMatrix savedTm = _tm, savedTlm = _tlm, savedPatternBase = _patternBase;
        int baseSaved = _saved.Count, baseMc = _mc.Count;
        bool began = false;
        try
        {
            ReadOnlyMemory<byte> data;
            try
            {
                data = proc.GetDecodedBytes(_ct);
            }
            catch (PdfException e)
            {
                Report(PdfSeverity.Error, "A Type 3 glyph procedure could not be decoded and was skipped: " + e.Message);
                return;
            }

            _saved.Add(_g);
            _g = _g.Clone();
            _formDepth++;
            _path.Clear();
            _pendingClip = false;
            _inText = false;
            _textClipPending = false;
            _skippedSaves = 0;
            _g.Ctm = matrix;

            // Patterns chosen inside the procedure are anchored to glyph space, the default space of its content stream
            // (ISO 32000-2 8.7.3.1), not to the page.
            _patternBase = matrix;

            PdfBounds? bbox = font.FontBBox is { Length: 4 } bb && double.IsFinite(bb[0] + bb[1] + bb[2] + bb[3])
                ? PdfBounds.FromCorners(bb[0], bb[1], bb[2], bb[3]) : null;
            var info = new PdfType3GlyphInfo(fi.Name, fi.Dictionary ?? font.Dictionary, shown.Code, glyph.Name, shown.Unicode, glyph.Advance, bbox);
            PdfGraphicsState state = Snapshot();
            _device.BeginType3Glyph(in info, in state);
            began = true;

            _res = font.Resources ?? _res;
            _qBase = _saved.Count;
            _mcBase = _mc.Count;
            _compat = 0;
            _noColor = false;
            _inCharProc = true;
            _tapSuspended++;
            try
            {
                Execute(new ContentLexer(data, _limits.MaxNestingDepth));
            }
            finally
            {
                _tapSuspended--;
            }
        }
        finally
        {
            try
            {
                if (_saved.Count > baseSaved)
                {
                    UnwindTo(baseSaved + 1, baseMc, "a Type 3 glyph procedure");
                    Restore();
                    _formDepth--;
                }
            }
            finally
            {
                try
                {
                    if (began)
                    {
                        _device.EndType3Glyph();
                    }
                }
                finally
                {
                    if (number >= 0)
                    {
                        _activeForms.Remove(number);
                    }

                    _res = savedRes;
                    _qBase = savedQBase;
                    _mcBase = savedMcBase;
                    _compat = savedCompat;
                    _skippedSaves = savedSkipped;
                    _inText = savedInText;
                    _textClipPending = savedTextClip;
                    _pendingClip = savedPending;
                    _clipEvenOdd = savedEo;
                    _noColor = savedNoColor;
                    _inCharProc = savedInCharProc;
                    _tm = savedTm;
                    _tlm = savedTlm;
                    _patternBase = savedPatternBase;
                    _path.Clear();
                }
            }
        }
    }
}

/// <summary>Converts font outlines (quadratic and cubic segments) to the device's path form.</summary>
internal static class GlyphPaths
{
    /// <summary>
    /// Maps an outline through a glyph-space matrix (a b c d e f) to a path of move, line, cubic and close segments. A quadratic
    /// segment is raised to the cubic with the same curve (control points two thirds of the way to the quadratic control).
    /// </summary>
    public static PdfPath Convert(GlyphOutline outline, double[] m)
    {
        if (outline.IsEmpty)
        {
            return default;
        }

        byte[] ov = outline.Verbs;
        float[] oc = outline.Coords;
        int points = 0;
        foreach (byte v in ov)
        {
            points += v switch
            {
                GlyphOutline.Move or GlyphOutline.Line => 1,
                GlyphOutline.Quad or GlyphOutline.Cubic => 3,
                _ => 0,
            };
        }

        byte[] verbs = new byte[ov.Length];
        double[] pts = new double[points * 2];
        int vi = 0, pi = 0, ci = 0;
        double cx = 0, cy = 0;
        foreach (byte v in ov)
        {
            switch (v)
            {
                case GlyphOutline.Move:
                case GlyphOutline.Line:
                    {
                        if (ci + 2 > oc.Length)
                        {
                            return Finish(verbs, pts, vi, pi);
                        }

                        (cx, cy) = (oc[ci], oc[ci + 1]);
                        ci += 2;
                        verbs[vi++] = v == GlyphOutline.Move ? (byte)PdfPathVerb.Move : (byte)PdfPathVerb.Line;
                        Put(pts, ref pi, m, cx, cy);
                        break;
                    }

                case GlyphOutline.Quad:
                    {
                        if (ci + 4 > oc.Length)
                        {
                            return Finish(verbs, pts, vi, pi);
                        }

                        double qx = oc[ci], qy = oc[ci + 1], ex = oc[ci + 2], ey = oc[ci + 3];
                        ci += 4;
                        verbs[vi++] = (byte)PdfPathVerb.Curve;
                        Put(pts, ref pi, m, cx + ((2.0 / 3.0) * (qx - cx)), cy + ((2.0 / 3.0) * (qy - cy)));
                        Put(pts, ref pi, m, ex + ((2.0 / 3.0) * (qx - ex)), ey + ((2.0 / 3.0) * (qy - ey)));
                        Put(pts, ref pi, m, ex, ey);
                        (cx, cy) = (ex, ey);
                        break;
                    }

                case GlyphOutline.Cubic:
                    {
                        if (ci + 6 > oc.Length)
                        {
                            return Finish(verbs, pts, vi, pi);
                        }

                        verbs[vi++] = (byte)PdfPathVerb.Curve;
                        for (int k = 0; k < 6; k += 2)
                        {
                            Put(pts, ref pi, m, oc[ci + k], oc[ci + k + 1]);
                        }

                        cx = oc[ci + 4];
                        cy = oc[ci + 5];
                        ci += 6;
                        break;
                    }

                default:
                    verbs[vi++] = (byte)PdfPathVerb.Close;
                    break;
            }
        }

        return Finish(verbs, pts, vi, pi);
    }

    private static PdfPath Finish(byte[] verbs, double[] pts, int vi, int pi) =>
        vi == verbs.Length && pi == pts.Length ? new PdfPath(verbs, pts) : new PdfPath(verbs.AsMemory(0, vi), pts.AsMemory(0, pi));

    // Plain multiplications and additions in a fixed order, so the result is the same on every platform.
    private static void Put(double[] pts, ref int pi, double[] m, double x, double y)
    {
        pts[pi++] = (m[0] * x) + (m[2] * y) + m[4];
        pts[pi++] = (m[1] * x) + (m[3] * y) + m[5];
    }
}
