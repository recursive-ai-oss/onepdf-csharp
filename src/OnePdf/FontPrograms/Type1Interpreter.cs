namespace OnePdf.FontPrograms;

/// <summary>The result of running one Type 1 charstring.</summary>
internal readonly record struct Type1Result(
    GlyphOutline Outline,
    float Width,
    bool HasMetrics,
    float Sbx,
    bool IsAccented,
    float AccentAsb,
    float AccentDx,
    float AccentDy,
    int BaseChar,
    int AccentChar);

/// <summary>
/// Runs a Type 1 charstring (Adobe Type 1 Font Format, chapter 6) into an outline: path construction, <c>hsbw</c>/<c>sbw</c>,
/// subroutines, <c>div</c>, hint operators (skipped), the flex and hint-replacement OtherSubrs, <c>setcurrentpoint</c> and
/// <c>seac</c> (reported, composed by the font). State is per call. Bounds: operand stack, subroutine depth, operator count.
/// </summary>
internal sealed class Type1Interpreter
{
    private readonly byte[]?[] _subrs;
    private readonly bool _widthOnly;
    private readonly OutlineBuilder _out = new();
    private readonly float[] _stack = new float[FontLimits.MaxCharstringStack];
    private readonly float[] _ps = new float[32];
    private readonly float[] _flexX = new float[8];
    private readonly float[] _flexY = new float[8];
    private int _sp;
    private int _psp;
    private float _x;
    private float _y;
    private float _sbx;
    private float _sby;
    private float _wx;
    private bool _haveMetrics;
    private bool _inFlex;
    private int _flexN;
    private int _ops;
    private bool _stop;
    private bool _accented;
    private float _asb;
    private float _adx;
    private float _ady;
    private int _bchar;
    private int _achar;

    public Type1Interpreter(byte[]?[] subrs, bool widthOnly)
    {
        _subrs = subrs;
        _widthOnly = widthOnly;
    }

    public Type1Result Run(byte[] code)
    {
        var frames = new byte[FontLimits.MaxSubrDepth + 1][];
        var pos = new int[FontLimits.MaxSubrDepth + 1];
        int depth = 0;
        frames[0] = code;
        while (!_stop)
        {
            byte[] c = frames[depth];
            if (pos[depth] >= c.Length)
            {
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
            if (b0 >= 32)
            {
                float v;
                if (b0 <= 246)
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
                    v = Be.I32(c, p);
                    p += 4;
                }

                Push(v);
                pos[depth] = p;
                continue;
            }

            pos[depth] = p;
            switch (b0)
            {
                case 1: // hstem
                case 3: // vstem
                    _sp = 0;
                    break;
                case 4: // vmoveto
                    Need(1);
                    MoveBy(0, _stack[_sp - 1]);
                    _sp = 0;
                    break;
                case 5: // rlineto
                    Need(2);
                    LineBy(_stack[_sp - 2], _stack[_sp - 1]);
                    _sp = 0;
                    break;
                case 6: // hlineto
                    Need(1);
                    LineBy(_stack[_sp - 1], 0);
                    _sp = 0;
                    break;
                case 7: // vlineto
                    Need(1);
                    LineBy(0, _stack[_sp - 1]);
                    _sp = 0;
                    break;
                case 8: // rrcurveto
                    Need(6);
                    CurveBy(_stack[_sp - 6], _stack[_sp - 5], _stack[_sp - 4], _stack[_sp - 3], _stack[_sp - 2], _stack[_sp - 1]);
                    _sp = 0;
                    break;
                case 9: // closepath
                    _out.Close();
                    _sp = 0;
                    break;
                case 10: // callsubr
                {
                    Need(1);
                    int n = (int)_stack[--_sp];
                    if (depth + 1 > FontLimits.MaxSubrDepth)
                    {
                        throw new FontProgramException("Charstring subroutines are nested too deeply.");
                    }

                    if ((uint)n >= (uint)_subrs.Length || _subrs[n] is null)
                    {
                        throw new FontProgramException("Subroutine index out of range.");
                    }

                    depth++;
                    frames[depth] = _subrs[n]!;
                    pos[depth] = 0;
                    break;
                }

                case 11: // return
                    if (depth == 0)
                    {
                        _stop = true;
                    }
                    else
                    {
                        depth--;
                    }

                    break;
                case 12:
                    Escape(Be.U8(c, pos[depth]++));
                    break;
                case 13: // hsbw
                    Need(2);
                    _sbx = _stack[_sp - 2];
                    _wx = _stack[_sp - 1];
                    _sby = 0;
                    _x = _sbx;
                    _y = 0;
                    _haveMetrics = true;
                    _sp = 0;
                    if (_widthOnly)
                    {
                        _stop = true;
                    }

                    break;
                case 14: // endchar
                    _out.Close();
                    _stop = true;
                    break;
                case 21: // rmoveto
                    Need(2);
                    MoveBy(_stack[_sp - 2], _stack[_sp - 1]);
                    _sp = 0;
                    break;
                case 22: // hmoveto
                    Need(1);
                    MoveBy(_stack[_sp - 1], 0);
                    _sp = 0;
                    break;
                case 30: // vhcurveto
                    Need(4);
                    CurveBy(0, _stack[_sp - 4], _stack[_sp - 3], _stack[_sp - 2], _stack[_sp - 1], 0);
                    _sp = 0;
                    break;
                case 31: // hvcurveto
                    Need(4);
                    CurveBy(_stack[_sp - 4], 0, _stack[_sp - 3], _stack[_sp - 2], 0, _stack[_sp - 1]);
                    _sp = 0;
                    break;
                default:
                    _sp = 0;
                    break;
            }
        }

        if (_widthOnly)
        {
            return new Type1Result(GlyphOutline.Empty, _wx, _haveMetrics, _sbx, false, 0, 0, 0, 0, 0);
        }

        return new Type1Result(_out.Build(), _wx, _haveMetrics, _sbx, _accented, _asb, _adx, _ady, _bchar, _achar);
    }

    private void Escape(int op)
    {
        switch (op)
        {
            case 0: // dotsection
            case 1: // vstem3
            case 2: // hstem3
                _sp = 0;
                break;
            case 6: // seac: asb adx ady bchar achar
                Need(5);
                _asb = _stack[_sp - 5];
                _adx = _stack[_sp - 4];
                _ady = _stack[_sp - 3];
                _bchar = (int)_stack[_sp - 2];
                _achar = (int)_stack[_sp - 1];
                _accented = true;
                _sp = 0;
                _stop = true;
                break;
            case 7: // sbw: sbx sby wx wy
                Need(4);
                _sbx = _stack[_sp - 4];
                _sby = _stack[_sp - 3];
                _wx = _stack[_sp - 2];
                _x = _sbx;
                _y = _sby;
                _haveMetrics = true;
                _sp = 0;
                if (_widthOnly)
                {
                    _stop = true;
                }

                break;
            case 12: // div
                Need(2);
                _stack[_sp - 2] = _stack[_sp - 1] == 0 ? 0 : _stack[_sp - 2] / _stack[_sp - 1];
                _sp--;
                break;
            case 16: // callothersubr: arg1 ... argn n othersubr#
                CallOtherSubr();
                break;
            case 17: // pop: moves a value from the PostScript stack to the charstring stack
                Push(_psp > 0 ? _ps[--_psp] : 0);
                break;
            case 33: // setcurrentpoint
                Need(2);
                _x = _stack[_sp - 2];
                _y = _stack[_sp - 1];
                _sp = 0;
                break;
            default:
                _sp = 0;
                break;
        }
    }

    // OtherSubrs 0 to 3 are the ones every Type 1 font uses (Type 1 Font Format, chapter 8): 1 starts flex, 2 records a flex point,
    // 0 ends flex, 3 is hint replacement. Anything else just moves its arguments to the PostScript stack.
    private void CallOtherSubr()
    {
        Need(2);
        int which = (int)_stack[--_sp];
        int n = (int)_stack[--_sp];
        if (n < 0 || n > _sp)
        {
            throw new FontProgramException("Invalid argument count for callothersubr.");
        }

        switch (which)
        {
            case 1:
                _inFlex = true;
                _flexN = 0;
                _sp -= n;
                break;
            case 2:
                if (_inFlex && _flexN < _flexX.Length)
                {
                    _flexX[_flexN] = _x;
                    _flexY[_flexN] = _y;
                    _flexN++;
                }

                _sp -= n;
                break;
            case 0:
                EndFlex();
                _sp -= n;
                // The endpoint comes back through two pops, x first, then y.
                _psp = 0;
                _ps[_psp++] = _y;
                _ps[_psp++] = _x;
                break;
            default:
                // Hint replacement (3) pushes the subroutine number back for "pop callsubr". Arguments are pushed in reverse so
                // the first pop returns the first argument.
                _psp = 0;
                for (int i = 0; i < n && _psp < _ps.Length; i++)
                {
                    _ps[_psp++] = _stack[_sp - 1 - i];
                }

                _sp -= n;
                break;
        }
    }

    private void EndFlex()
    {
        _inFlex = false;
        if (_flexN >= 7)
        {
            // Point 0 is the reference point and is not on the curve; points 1 to 6 are two Bezier segments.
            if (!_out.IsOpen)
            {
                _out.MoveTo(_flexX[0], _flexY[0]);
            }

            _out.CubicTo(_flexX[1], _flexY[1], _flexX[2], _flexY[2], _flexX[3], _flexY[3]);
            _out.CubicTo(_flexX[4], _flexY[4], _flexX[5], _flexY[5], _flexX[6], _flexY[6]);
            _x = _flexX[6];
            _y = _flexY[6];
        }
    }

    private void Push(float v)
    {
        if (_sp >= FontLimits.MaxCharstringStack)
        {
            throw new FontProgramException("The charstring operand stack overflowed.");
        }

        _stack[_sp++] = v;
    }

    private void Need(int n)
    {
        if (_sp < n)
        {
            throw new FontProgramException("A charstring operator has too few operands.");
        }
    }

    private void MoveBy(float dx, float dy)
    {
        _x += dx;
        _y += dy;
        if (!_inFlex)
        {
            _out.MoveTo(_x, _y);
        }
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
}
