using OnePdf.FontPrograms;
using OnePdf.PdfFonts;

namespace OnePdf.Content;

internal sealed partial class ContentInterpreter
{
    /// <summary>Glyphs delivered per run at most; longer strings are split (the text position carries on exactly).</summary>
    private const int MaxGlyphsPerRun = 4096;

    private bool DispatchText(ContentOp op)
    {
        switch (op)
        {
            case ContentOp.BeginText:
                if (_inText)
                {
                    Report(PdfSeverity.Warning, "BT inside a text object; the text matrices were reset.");
                }

                _inText = true;
                _tm = _tlm = PdfMatrix.Identity;
                return true;
            case ContentOp.EndText:
                if (!_inText)
                {
                    Report(PdfSeverity.Warning, "ET without BT was ignored.");
                    return true;
                }

                _inText = false;
                if (_textClipPending)
                {
                    PdfGraphicsState s = Snapshot();
                    _device.PushTextClip(in s);
                    _g.ClipCount++;
                    _textClipPending = false;
                }

                return true;
            case ContentOp.CharSpace:
                if (Nums(1))
                {
                    _g.CharSpace = _a[0];
                }
                else
                {
                    BadOperands(op);
                }

                return true;
            case ContentOp.WordSpace:
                if (Nums(1))
                {
                    _g.WordSpace = _a[0];
                }
                else
                {
                    BadOperands(op);
                }

                return true;
            case ContentOp.HorizScale:
                if (Nums(1))
                {
                    _g.HorizScale = _a[0] / 100.0;
                }
                else
                {
                    BadOperands(op);
                }

                return true;
            case ContentOp.Leading:
                if (Nums(1))
                {
                    _g.Leading = _a[0];
                }
                else
                {
                    BadOperands(op);
                }

                return true;
            case ContentOp.TextRise:
                if (Nums(1))
                {
                    _g.Rise = _a[0];
                }
                else
                {
                    BadOperands(op);
                }

                return true;
            case ContentOp.TextRender:
                if (Nums(1) && _a[0] is >= 0 and <= 7)
                {
                    _g.RenderMode = (int)_a[0];
                }
                else
                {
                    BadOperands(op);
                }

                return true;
            case ContentOp.TextFont:
                if (_nOps >= 2 && _ops[_nOps - 2].Kind == OperandKind.Name && _ops[_nOps - 1].Kind == OperandKind.Number && double.IsFinite(_ops[_nOps - 1].Num))
                {
                    SelectFont(_ops[_nOps - 2].Name!, _ops[_nOps - 1].Num);
                }
                else
                {
                    BadOperands(op);
                }

                return true;
            case ContentOp.TextMove:
                if (Nums(2))
                {
                    MoveText(_a[0], _a[1]);
                }
                else
                {
                    BadOperands(op);
                }

                return true;
            case ContentOp.TextMoveLeading:
                if (Nums(2))
                {
                    _g.Leading = -_a[1];
                    MoveText(_a[0], _a[1]);
                }
                else
                {
                    BadOperands(op);
                }

                return true;
            case ContentOp.TextMatrix:
                if (Nums(6))
                {
                    _tm = _tlm = new PdfMatrix(_a[0], _a[1], _a[2], _a[3], _a[4], _a[5]);
                }
                else
                {
                    BadOperands(op);
                }

                return true;
            case ContentOp.NextLine:
                MoveText(0, -_g.Leading);
                return true;
            case ContentOp.ShowText:
                if (_nOps >= 1 && _ops[_nOps - 1].Kind == OperandKind.String)
                {
                    ShowString(_ops[_nOps - 1].Str);
                }
                else
                {
                    BadOperands(op);
                }

                return true;
            case ContentOp.NextLineShow:
                if (_nOps >= 1 && _ops[_nOps - 1].Kind == OperandKind.String)
                {
                    MoveText(0, -_g.Leading);
                    ShowString(_ops[_nOps - 1].Str);
                }
                else
                {
                    BadOperands(op);
                }

                return true;
            case ContentOp.SpacedNextLineShow:
                if (_nOps >= 3 && _ops[_nOps - 1].Kind == OperandKind.String && _ops[_nOps - 2].Kind == OperandKind.Number
                    && _ops[_nOps - 3].Kind == OperandKind.Number && double.IsFinite(_ops[_nOps - 2].Num) && double.IsFinite(_ops[_nOps - 3].Num))
                {
                    _g.WordSpace = _ops[_nOps - 3].Num;
                    _g.CharSpace = _ops[_nOps - 2].Num;
                    MoveText(0, -_g.Leading);
                    ShowString(_ops[_nOps - 1].Str);
                }
                else
                {
                    BadOperands(op);
                }

                return true;
            default:
                return false;
        }
    }

    private void MoveText(double tx, double ty)
    {
        _tlm = PdfMatrix.Translation(tx, ty).Then(_tlm);
        _tm = _tlm;
    }

    private void SelectFont(string name, double size)
    {
        _g.FontSize = size;
        PdfDictionary? fonts = _res?.GetDictionary("Font");
        PdfObject? raw = fonts?.GetRaw(name);
        PdfDictionary? dict = fonts?.GetDictionary(name);
        if (dict is null)
        {
            // The default font keeps the text decodable; its glyphs and widths are only approximate.
            Report(PdfSeverity.Warning, "Font /" + name + " is not in the resources; a default font is used.");
        }

        _g.Font = GetFont(name, dict, raw as PdfReference);
    }

    private FontInfo GetFont(string name, PdfDictionary? dict, PdfReference? reference)
    {
        if (dict is not null)
        {
            if (reference is not null && _fontsByRef.TryGetValue(reference.Number, out FontInfo? hit) && hit.Name == name)
            {
                return hit;
            }

            if (_fontsByDict.TryGetValue(dict, out FontInfo? hit2) && hit2.Name == name)
            {
                return hit2;
            }
        }
        else if (_defaultFont is { } known && known.Name == name)
        {
            return known;
        }

        var info = new FontInfo(name, dict, _doc.GetFont(dict));
        if (dict is null)
        {
            _defaultFont = info;
            return info;
        }

        if (reference is not null)
        {
            _fontsByRef[reference.Number] = info;
        }

        _fontsByDict[dict] = info;
        return info;
    }

    private void ShowArray(ContentLexer lx)
    {
        if (_nOps < 1 || _ops[_nOps - 1].Kind != OperandKind.Array)
        {
            BadOperands(ContentOp.ShowTextArray);
            return;
        }

        Operand arr = _ops[_nOps - 1];
        int count = arr.ArrayCount;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<Operand> items = lx.Items(in arr);
            ref readonly Operand it = ref items[i];
            if (it.Kind == OperandKind.Number)
            {
                if (!double.IsFinite(it.Num))
                {
                    continue;
                }

                // A TJ number moves the text position by thousandths of the font size, against the writing direction
                // (ISO 32000-2 9.4.3): tx = -Tj/1000·Tfs·Th horizontally, ty = -Tj/1000·Tfs vertically.
                double d = -it.Num / 1000.0 * _g.FontSize;
                _tm = _g.Font?.Font.WMode == 1
                    ? PdfMatrix.Translation(0, d).Then(_tm)
                    : PdfMatrix.Translation(d * _g.HorizScale, 0).Then(_tm);
            }
            else if (it.Kind == OperandKind.String)
            {
                ShowString(it.Str);
            }
        }
    }

    /// <summary>
    /// Shows a string: one run per call (long strings in pieces), advancing the text matrix by the width of each code
    /// (ISO 32000-2 9.4.4): horizontal <c>tx = (w0·Tfs + Tc + Tw)·Th</c>, vertical <c>ty = w1·Tfs + Tc + Tw</c>, where
    /// <c>Tw</c> applies only to a single-byte code 32. In vertical mode the glyph is drawn at the text position minus the
    /// position vector v (9.7.4.3), so the run's offsets are those of the glyph origin and the vector travels with the glyph.
    /// A code the font has no glyph for draws nothing and still advances.
    /// </summary>
    private void ShowString(ReadOnlyMemory<byte> str)
    {
        if (str.IsEmpty)
        {
            return;
        }

        ReadOnlySpan<byte> s = str.Span;
        FontInfo? fi = _g.Font;
        if (fi is null)
        {
            Report(PdfSeverity.Warning, "Text was shown before any font was selected; a default font is used.");
            fi = _g.Font = GetFont(string.Empty, null, null);
        }

        PdfFont font = fi.Font;
        bool vertical = font.WMode == 1;
        bool type3 = font.IsType3;
        bool outlines = _options.GlyphOutlines && !type3 && _g.RenderMode != 3 && _hidden == 0;
        double fs = _g.FontSize, tc = _g.CharSpace, tw = _g.WordSpace, th = _g.HorizScale;
        double x = 0, y = 0;
        int count = 0;
        int chunkStart = 0;
        int pos = 0;
        while (pos < s.Length)
        {
            int next = font.Next(s, pos, out FontGlyph? g);
            if (g is null || next <= pos)
            {
                // A font that cannot split the remainder: skip a byte so interpretation always progresses.
                next = pos + 1;
                g = null;
            }

            double w = g?.Advance ?? 0;
            if (!double.IsFinite(w))
            {
                w = 0;
            }

            if (count == _glyphs.Length)
            {
                Array.Resize(ref _glyphs, _glyphs.Length * 2);
                Array.Resize(ref _fontGlyphs, _glyphs.Length);
            }

            bool word = g is not null && g.CodeLength == 1 && g.Code == 32;
            double spacing = tc + (word ? tw : 0);
            if (g is null)
            {
                _glyphs[count] = new PdfGlyph(0, next - pos, x, y, 0);
            }
            else if (vertical)
            {
                double vy = FiniteOr(g.VerticalAdvance, -1);
                double ox = FiniteOr(g.OriginX, 0), oy = FiniteOr(g.OriginY, 0);
                _glyphs[count] = new PdfGlyph(
                    unchecked((int)g.Code), g.CodeLength, x - (ox * fs * th), y - (oy * fs), w, g.Gid, g.HasUnicode ? g.Unicode : null,
                    outlines ? OutlineOf(g) : default, ox, oy, vy);
                y += (vy * fs) + spacing;
            }
            else
            {
                _glyphs[count] = new PdfGlyph(
                    unchecked((int)g.Code), g.CodeLength, x, y, w, g.Gid, g.HasUnicode ? g.Unicode : null,
                    outlines ? OutlineOf(g) : default);
                x += ((w * fs) + spacing) * th;
            }

            _fontGlyphs[count] = g;
            count++;
            pos = next;
            if (count == MaxGlyphsPerRun || pos >= s.Length)
            {
                FlushRun(str.Slice(chunkStart, pos - chunkStart), count, x, y, vertical, fi);
                x = 0;
                y = 0;
                count = 0;
                chunkStart = pos;
            }
        }
    }

    private static double FiniteOr(double v, double fallback) => double.IsFinite(v) ? v : fallback;

    private void FlushRun(ReadOnlyMemory<byte> codes, int count, double advX, double advY, bool vertical, FontInfo fi)
    {
        bool type3 = fi.Font.IsType3;

        // Type 3 glyphs are content, not outlines; the clipping modes have no effect on them (ISO 32000-2 9.3.6).
        if (_g.RenderMode >= 4 && !type3)
        {
            _textClipPending = true;
        }

        if (_hidden == 0)
        {
            var run = new PdfGlyphRun(
                fi.Name, fi.Dictionary, _g.FontSize, (PdfTextRenderMode)_g.RenderMode,
                _g.CharSpace, _g.WordSpace, _g.HorizScale, _g.Rise, _tm, vertical, codes, _glyphs.AsMemory(0, count), advX, advY);
            PdfGraphicsState state = Snapshot();
            _device.ShowGlyphRun(in run, in state);
            if (type3 && _options.GlyphOutlines && _g.RenderMode != 3)
            {
                ShowType3Glyphs(fi, count);
            }
        }

        _tm = PdfMatrix.Translation(advX, advY).Then(_tm);
    }
}
