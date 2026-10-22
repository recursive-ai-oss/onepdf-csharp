namespace OnePdf.Content;

internal sealed partial class ContentInterpreter
{
    /// <summary>Glyphs delivered per run at most; longer strings are split (the text position carries on exactly).</summary>
    private const int MaxGlyphsPerRun = 4096;

    private static readonly IPdfFontMetrics FallbackMetrics = DictionaryFontMetrics.Create(new PdfDictionary(null), static _ => { });

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
            Report(PdfSeverity.Warning, "Font /" + name + " is not in the resources; text is positioned with estimated widths.");
            _g.Font = new FontInfo(name, null, FallbackMetrics);
            return;
        }

        _g.Font = GetFont(name, dict, raw as PdfReference);
    }

    private FontInfo GetFont(string name, PdfDictionary dict, PdfReference? reference)
    {
        if (reference is not null && _fontsByRef.TryGetValue(reference.Number, out FontInfo? hit) && hit.Name == name)
        {
            return hit;
        }

        if (_fontsByDict.TryGetValue(dict, out FontInfo? hit2) && hit2.Name == name)
        {
            return hit2;
        }

        IPdfFontMetrics? metrics = _options.FontResolver?.Resolve(dict);
        metrics ??= DictionaryFontMetrics.Create(dict, m => Report(PdfSeverity.Info, m));
        var info = new FontInfo(name, dict, metrics);
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

                // A TJ number moves the text position by thousandths of the font size, against the writing direction.
                IPdfFontMetrics m = _g.Font?.Metrics ?? FallbackMetrics;
                double d = -it.Num / 1000.0 * _g.FontSize;
                _tm = m.IsVertical
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
    /// <c>Tw</c> applies only to a single-byte code 32.
    /// </summary>
    private void ShowString(ReadOnlyMemory<byte> str)
    {
        if (str.IsEmpty)
        {
            return;
        }

        ReadOnlySpan<byte> s = str.Span;
        FontInfo? fi = _g.Font;
        IPdfFontMetrics m = fi?.Metrics ?? FallbackMetrics;
        if (fi is null)
        {
            Report(PdfSeverity.Warning, "Text was shown before any font was selected; estimated widths are used.");
        }

        bool vertical = m.IsVertical;
        double fs = _g.FontSize, tc = _g.CharSpace, tw = _g.WordSpace, th = _g.HorizScale;
        double x = 0, y = 0;
        int count = 0;
        int chunkStart = 0;
        int i = 0;
        while (i < s.Length)
        {
            int n = m.ReadCode(s[i..], out int code);
            n = Math.Clamp(n, 1, s.Length - i);
            double w = m.GetWidth(code);
            if (!double.IsFinite(w))
            {
                w = 0;
            }

            if (count == _glyphs.Length)
            {
                Array.Resize(ref _glyphs, _glyphs.Length * 2);
            }

            _glyphs[count++] = new PdfGlyph(code, n, x, y, w);
            bool word = n == 1 && code == 32;
            if (vertical)
            {
                double v = m.GetVerticalAdvance(code);
                y += ((double.IsFinite(v) ? v : -1) * fs) + tc + (word ? tw : 0);
            }
            else
            {
                x += ((w * fs) + tc + (word ? tw : 0)) * th;
            }

            i += n;
            if (count == MaxGlyphsPerRun || i >= s.Length)
            {
                FlushRun(str.Slice(chunkStart, i - chunkStart), count, x, y, vertical, fi);
                x = 0;
                y = 0;
                count = 0;
                chunkStart = i;
            }
        }
    }

    private void FlushRun(ReadOnlyMemory<byte> codes, int count, double advX, double advY, bool vertical, FontInfo? fi)
    {
        if (_g.RenderMode >= 4)
        {
            _textClipPending = true;
        }

        if (_hidden == 0)
        {
            var run = new PdfGlyphRun(
                fi?.Name ?? string.Empty, fi?.Dictionary, _g.FontSize, (PdfTextRenderMode)_g.RenderMode,
                _g.CharSpace, _g.WordSpace, _g.HorizScale, _g.Rise, _tm, vertical, codes, _glyphs.AsMemory(0, count), advX, advY);
            PdfGraphicsState state = Snapshot();
            _device.ShowGlyphRun(in run, in state);
        }

        _tm = PdfMatrix.Translation(advX, advY).Then(_tm);
    }
}
