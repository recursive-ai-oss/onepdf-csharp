namespace OnePdf.FontPrograms;

/// <summary>
/// Runs one Type 2 charstring (Adobe Technical Note #5177) into an outline. State is per call, so concurrent glyph loads on one font
/// are independent. Bounds: operand stack, subroutine depth and total operator count.
/// </summary>
internal sealed class Type2Interpreter
{
    private readonly ReadOnlyMemory<byte> _fontData;
    private readonly CffIndex _globalSubrs;
    private readonly CffFont.FontDict _fd;
    private readonly bool _widthOnly;
    private readonly OutlineBuilder _out = new();
    private readonly float[] _stack = new float[FontLimits.MaxCharstringStack];
    private readonly float[] _transient = new float[32];
    private int _sp;
    private float _x;
    private float _y;
    private int _hints;
    private bool _widthDone;
    private float _width;
    private int _ops;
    private bool _stop;
    private bool _accented;
    private int _baseChar;
    private int _accentChar;
    private float _adx;
    private float _ady;

    public Type2Interpreter(ReadOnlyMemory<byte> fontData, CffIndex globalSubrs, CffFont.FontDict fd, bool widthOnly)
    {
        _fontData = fontData;
        _globalSubrs = globalSubrs;
        _fd = fd;
        _widthOnly = widthOnly;
        _width = fd.DefaultWidth;
    }

    private static int Bias(int count) => count < 1240 ? 107 : (count < 33900 ? 1131 : 32768);

    public CharstringResult Run(ReadOnlyMemory<byte> code)
    {
        var codes = new ReadOnlyMemory<byte>[FontLimits.MaxSubrDepth + 1];
        var pos = new int[FontLimits.MaxSubrDepth + 1];
        int depth = 0;
        codes[0] = code;
        ReadOnlySpan<byte> d = _fontData.Span;
        while (!_stop)
        {
            ReadOnlySpan<byte> c = codes[depth].Span;
            if (pos[depth] >= c.Length)
            {
                // Falling off the end of a subroutine is a return; off the end of the glyph ends it.
                if (depth == 0)
                {
                    break;
                }

                depth--;
                continue;
            }

            if (++_ops > FontLimits.MaxCharstringOperators)
            {
                throw new FontProgramException("A charstring runs too long.");
            }

            int p = pos[depth];
            int b0 = c[p++];
            if (b0 >= 32 || b0 == 28)
            {
                float v;
                if (b0 == 28)
                {
                    v = Be.I16(c, p);
                    p += 2;
                }
                else if (b0 <= 246)
                {
                    v = b0 - 139;
                }
                else if (b0 <= 250)
                {
                    v = ((b0 - 247) * 256) + Be.U8(c, p++) + 108;
                }
                else if (b0 <= 254)
                {
                    v = (-(b0 - 251) * 256) - Be.U8(c, p++) - 108;
                }
                else
                {
                    v = Be.I32(c, p) / 65536f;
                    p += 4;
                }

                Push(v);
                pos[depth] = p;
                continue;
            }

            pos[depth] = p;
            switch (b0)
            {
                case 1:
                case 3:
                case 18:
                case 23:
                    Stems();
                    break;
                case 19:
                case 20:
                    // hintmask/cntrmask: leftover operands are an implicit vstem; the mask bytes follow inline.
                    Stems();
                    int maskBytes = (_hints + 7) / 8;
                    if (pos[depth] + maskBytes > c.Length)
                    {
                        throw Be.Truncated();
                    }

                    pos[depth] += maskBytes;
                    break;
                case 21:
                    Width(_sp > 2);
                    RequireArgs(2);
                    Move(_stack[0], _stack[1]);
                    _sp = 0;
                    break;
                case 22:
                    Width(_sp > 1);
                    RequireArgs(1);
                    Move(_stack[0], 0);
                    _sp = 0;
                    break;
                case 4:
                    Width(_sp > 1);
                    RequireArgs(1);
                    Move(0, _stack[0]);
                    _sp = 0;
                    break;
                case 5:
                    RLineTo();
                    break;
                case 6:
                case 7:
                    HVLineTo(b0 == 6);
                    break;
                case 8:
                    RRCurveTo();
                    break;
                case 24:
                    RCurveLine();
                    break;
                case 25:
                    RLineCurve();
                    break;
                case 26:
                    VVCurveTo();
                    break;
                case 27:
                    HHCurveTo();
                    break;
                case 30:
                case 31:
                    HVCurveTo(b0 == 31);
                    break;
                case 10:
                case 29:
                {
                    if (_sp < 1)
                    {
                        throw new FontProgramException("Subroutine call without an index.");
                    }

                    CffIndex idx = b0 == 29 ? _globalSubrs : _fd.LocalSubrs;
                    int n = (int)_stack[--_sp] + Bias(idx.Count);
                    if (depth + 1 > FontLimits.MaxSubrDepth)
                    {
                        throw new FontProgramException("Charstring subroutines are nested too deeply.");
                    }

                    if ((uint)n >= (uint)idx.Count)
                    {
                        throw new FontProgramException("Subroutine index out of range.");
                    }

                    depth++;
                    codes[depth] = idx.GetMemory(d, _fontData, n);
                    pos[depth] = 0;
                    break;
                }

                case 11:
                    if (depth == 0)
                    {
                        _stop = true;
                    }
                    else
                    {
                        depth--;
                    }

                    break;
                case 14:
                    EndChar();
                    _stop = true;
                    break;
                case 12:
                    ExecuteEscape(Be.U8(c, pos[depth]++));
                    break;
                default:
                    // Reserved operators clear the stack, as the spec asks decoders to do.
                    _sp = 0;
                    break;
            }
        }

        if (_widthOnly)
        {
            return new CharstringResult(GlyphOutline.Empty, _width, false, 0, 0, 0, 0);
        }

        return new CharstringResult(_out.Build(), _width, _accented, _baseChar, _accentChar, _adx, _ady);
    }

    private void EndChar()
    {
        // The width is present when there are 1 operands (width only) or 5 (width + seac arguments).
        Width(_sp == 1 || _sp == 5);
        if (_sp >= 4)
        {
            // seac-style: adx ady bchar achar, the last four operands.
            _adx = _stack[_sp - 4];
            _ady = _stack[_sp - 3];
            _baseChar = (int)_stack[_sp - 2];
            _accentChar = (int)_stack[_sp - 1];
            _accented = true;
        }

        _sp = 0;
    }

    private void Push(float v)
    {
        if (_sp >= FontLimits.MaxCharstringStack)
        {
            throw new FontProgramException("The charstring operand stack overflowed.");
        }

        _stack[_sp++] = v;
    }

    private void RequireArgs(int n)
    {
        if (_sp < n)
        {
            throw new FontProgramException("A charstring operator has too few operands.");
        }

        if (_sp > n)
        {
            // Extra operands on a move are leftovers (the width was dropped already); keep the last ones.
            Array.Copy(_stack, _sp - n, _stack, 0, n);
            _sp = n;
        }
    }

    // The first stack-clearing operator may carry the advance width as an extra leading operand, relative to nominalWidthX.
    private void Width(bool present)
    {
        if (_widthDone)
        {
            return;
        }

        _widthDone = true;
        if (present && _sp > 0)
        {
            _width = _fd.NominalWidth + _stack[0];
            Array.Copy(_stack, 1, _stack, 0, _sp - 1);
            _sp--;
        }

        if (_widthOnly)
        {
            _stop = true;
        }
    }

    private void Stems()
    {
        Width((_sp & 1) != 0);
        _hints += _sp / 2;
        _sp = 0;
    }

    private void Move(float dx, float dy)
    {
        _x += dx;
        _y += dy;
        _out.MoveTo(_x, _y);
    }

    private void EnsureOpen()
    {
        if (!_out.IsOpen)
        {
            _out.MoveTo(_x, _y);
        }
    }

    private void LineBy(float dx, float dy)
    {
        EnsureOpen();
        _x += dx;
        _y += dy;
        _out.LineTo(_x, _y);
    }

    private void CurveBy(float d1x, float d1y, float d2x, float d2y, float d3x, float d3y)
    {
        EnsureOpen();
        float c1x = _x + d1x, c1y = _y + d1y;
        float c2x = c1x + d2x, c2y = c1y + d2y;
        _x = c2x + d3x;
        _y = c2y + d3y;
        _out.CubicTo(c1x, c1y, c2x, c2y, _x, _y);
    }

    private void RLineTo()
    {
        for (int i = 0; i + 1 < _sp; i += 2)
        {
            LineBy(_stack[i], _stack[i + 1]);
        }

        _sp = 0;
    }

    private void HVLineTo(bool horizontal)
    {
        for (int i = 0; i < _sp; i++)
        {
            if (horizontal)
            {
                LineBy(_stack[i], 0);
            }
            else
            {
                LineBy(0, _stack[i]);
            }

            horizontal = !horizontal;
        }

        _sp = 0;
    }

    private void RRCurveTo()
    {
        for (int i = 0; i + 5 < _sp; i += 6)
        {
            CurveBy(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]);
        }

        _sp = 0;
    }

    private void RCurveLine()
    {
        int i = 0;
        for (; i + 7 < _sp; i += 6)
        {
            CurveBy(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]);
        }

        if (i + 1 < _sp)
        {
            LineBy(_stack[i], _stack[i + 1]);
        }

        _sp = 0;
    }

    private void RLineCurve()
    {
        int i = 0;
        for (; i + 7 < _sp; i += 2)
        {
            LineBy(_stack[i], _stack[i + 1]);
        }

        if (i + 5 < _sp)
        {
            CurveBy(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]);
        }

        _sp = 0;
    }

    private void VVCurveTo()
    {
        int i = 0;
        float dx1 = 0;
        if ((_sp & 1) != 0)
        {
            dx1 = _stack[i++];
        }

        for (; i + 3 < _sp; i += 4)
        {
            CurveBy(dx1, _stack[i], _stack[i + 1], _stack[i + 2], 0, _stack[i + 3]);
            dx1 = 0;
        }

        _sp = 0;
    }

    private void HHCurveTo()
    {
        int i = 0;
        float dy1 = 0;
        if ((_sp & 1) != 0)
        {
            dy1 = _stack[i++];
        }

        for (; i + 3 < _sp; i += 4)
        {
            CurveBy(_stack[i], dy1, _stack[i + 1], _stack[i + 2], _stack[i + 3], 0);
            dy1 = 0;
        }

        _sp = 0;
    }

    private void HVCurveTo(bool horizontal)
    {
        for (int i = 0; i + 3 < _sp; i += 4)
        {
            float extra = _sp - i == 5 ? _stack[i + 4] : 0;
            if (horizontal)
            {
                CurveBy(_stack[i], 0, _stack[i + 1], _stack[i + 2], extra, _stack[i + 3]);
            }
            else
            {
                CurveBy(0, _stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], extra);
            }

            horizontal = !horizontal;
        }

        _sp = 0;
    }

    private void ExecuteEscape(int op)
    {
        switch (op)
        {
            case 34: // hflex
                if (_sp >= 7)
                {
                    float y0 = _y;
                    CurveBy(_stack[0], 0, _stack[1], _stack[2], _stack[3], 0);
                    CurveBy(_stack[4], 0, _stack[5], y0 - _y, _stack[6], 0);
                }

                _sp = 0;
                break;
            case 35: // flex
                if (_sp >= 12)
                {
                    CurveBy(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], _stack[5]);
                    CurveBy(_stack[6], _stack[7], _stack[8], _stack[9], _stack[10], _stack[11]);
                }

                _sp = 0;
                break;
            case 36: // hflex1
                if (_sp >= 9)
                {
                    float y0 = _y;
                    CurveBy(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], 0);
                    CurveBy(_stack[5], 0, _stack[6], _stack[7], _stack[8], y0 - (_y + _stack[7]));
                }

                _sp = 0;
                break;
            case 37: // flex1
                if (_sp >= 11)
                {
                    float x0 = _x, y0 = _y;
                    float dx = _stack[0] + _stack[2] + _stack[4] + _stack[6] + _stack[8];
                    float dy = _stack[1] + _stack[3] + _stack[5] + _stack[7] + _stack[9];
                    CurveBy(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], _stack[5]);
                    if (Math.Abs(dx) > Math.Abs(dy))
                    {
                        CurveBy(_stack[6], _stack[7], _stack[8], _stack[9], _stack[10], y0 - (_y + _stack[7] + _stack[9]));
                    }
                    else
                    {
                        CurveBy(_stack[6], _stack[7], _stack[8], _stack[9], x0 - (_x + _stack[6] + _stack[8]), _stack[10]);
                    }
                }

                _sp = 0;
                break;
            default:
                Arithmetic(op);
                break;
        }
    }

    private void Arithmetic(int op)
    {
        switch (op)
        {
            case 3: // and
                Need(2);
                _stack[_sp - 2] = _stack[_sp - 2] != 0 && _stack[_sp - 1] != 0 ? 1 : 0;
                _sp--;
                break;
            case 4: // or
                Need(2);
                _stack[_sp - 2] = _stack[_sp - 2] != 0 || _stack[_sp - 1] != 0 ? 1 : 0;
                _sp--;
                break;
            case 5: // not
                Need(1);
                _stack[_sp - 1] = _stack[_sp - 1] == 0 ? 1 : 0;
                break;
            case 9: // abs
                Need(1);
                _stack[_sp - 1] = Math.Abs(_stack[_sp - 1]);
                break;
            case 10: // add
                Need(2);
                _stack[_sp - 2] += _stack[_sp - 1];
                _sp--;
                break;
            case 11: // sub
                Need(2);
                _stack[_sp - 2] -= _stack[_sp - 1];
                _sp--;
                break;
            case 12: // div
                Need(2);
                _stack[_sp - 2] = _stack[_sp - 1] == 0 ? 0 : _stack[_sp - 2] / _stack[_sp - 1];
                _sp--;
                break;
            case 14: // neg
                Need(1);
                _stack[_sp - 1] = -_stack[_sp - 1];
                break;
            case 15: // eq
                Need(2);
                _stack[_sp - 2] = _stack[_sp - 2] == _stack[_sp - 1] ? 1 : 0;
                _sp--;
                break;
            case 18: // drop
                Need(1);
                _sp--;
                break;
            case 20: // put
            {
                Need(2);
                int i = (int)_stack[_sp - 1];
                if ((uint)i < (uint)_transient.Length)
                {
                    _transient[i] = _stack[_sp - 2];
                }

                _sp -= 2;
                break;
            }

            case 21: // get
            {
                Need(1);
                int i = (int)_stack[_sp - 1];
                _stack[_sp - 1] = (uint)i < (uint)_transient.Length ? _transient[i] : 0;
                break;
            }

            case 22: // ifelse
                Need(4);
                _stack[_sp - 4] = _stack[_sp - 2] <= _stack[_sp - 1] ? _stack[_sp - 4] : _stack[_sp - 3];
                _sp -= 3;
                break;
            case 23: // random: deterministic by design
                Push(0.5f);
                break;
            case 24: // mul
                Need(2);
                _stack[_sp - 2] *= _stack[_sp - 1];
                _sp--;
                break;
            case 26: // sqrt
                Need(1);
                _stack[_sp - 1] = MathF.Sqrt(Math.Max(0, _stack[_sp - 1]));
                break;
            case 27: // dup
                Need(1);
                Push(_stack[_sp - 1]);
                break;
            case 28: // exch
                Need(2);
                (_stack[_sp - 1], _stack[_sp - 2]) = (_stack[_sp - 2], _stack[_sp - 1]);
                break;
            case 29: // index
            {
                Need(1);
                int i = (int)_stack[_sp - 1];
                _sp--;
                if (i < 0)
                {
                    i = 0;
                }

                Need(i + 1);
                Push(_stack[_sp - 1 - i]);
                break;
            }

            case 30: // roll
            {
                Need(2);
                int j = (int)_stack[_sp - 1];
                int n = (int)_stack[_sp - 2];
                _sp -= 2;
                if (n < 0 || n > _sp)
                {
                    throw new FontProgramException("Invalid roll in a charstring.");
                }

                if (n > 0)
                {
                    j = ((j % n) + n) % n;
                    Span<float> seg = _stack.AsSpan(_sp - n, n);
                    Span<float> tmp = stackalloc float[FontLimits.MaxCharstringStack];
                    tmp = tmp[..n];
                    for (int k = 0; k < n; k++)
                    {
                        tmp[(k + j) % n] = seg[k];
                    }

                    tmp.CopyTo(seg);
                }

                break;
            }

            default:
                _sp = 0;
                break;
        }
    }

    private void Need(int n)
    {
        if (_sp < n)
        {
            throw new FontProgramException("A charstring operator has too few operands.");
        }
    }
}
