using System;
using System.Collections.Generic;
using System.Globalization;

namespace OnePdf.Functions;

/// <summary>
/// Type 4, PostScript calculator (ISO 32000-2 7.10.5): a small stack program compiled once to a flat instruction list.
/// </summary>
/// <remarks>
/// <para>Compilation turns <c>{...} if</c> and <c>{...}{...} ifelse</c> into forward jumps, so a program has no loops and
/// each instruction runs at most once. Evaluation still counts operations against <see cref="PdfLimits.MaxType4Operations"/>
/// (a straight-line program may simply be long) and stops there.</para>
/// <para>Evaluation never throws. A program that cannot be compiled, underflows its stack, overflows it, runs too long, or
/// leaves the wrong number of results still yields defined outputs (whatever is on the stack, missing results as 0, all
/// clipped to /Range) plus a <see cref="FunctionStatus"/> for a diagnostic.</para>
/// <para>Values are <c>double</c> tagged integer, real or boolean on a stack of at most 256 entries. Integers are 32-bit;
/// an integer result outside that range becomes a real, as in PostScript. Inputs arrive as reals. Operands of the wrong
/// type are converted and flagged (<see cref="FunctionStatus.TypeError"/>); arithmetic with no defined result (divide by
/// zero, square root or logarithm of a non-positive number, <c>0 0 atan</c>) yields 0 and <see cref="FunctionStatus.MathError"/>.</para>
/// <para><c>atan</c> returns degrees in [0, 360). <c>round</c> rounds halves up (floor(x + 0.5)). <c>idiv</c> and <c>mod</c>
/// truncate toward zero and <c>mod</c> takes the sign of the dividend. <c>bitshift</c> is a logical 32-bit shift
/// (positive left, negative right; a count of 32 or more gives 0).</para>
/// </remarks>
internal sealed class PostScriptFunction : PdfFunction
{
    private const int MaxStack = 256;
    private const int MaxInstructions = 1 << 20;

    private enum Op : byte
    {
        Nop,
        PushInt,
        PushReal,
        PushTrue,
        PushFalse,
        JumpIfFalse,
        Jump,
        Abs, Add, Atan, Ceiling, Cos, Cvi, Cvr, Div, Exp, Floor, Idiv, Ln, Log, Mod, Mul, Neg, Round, Sin, Sqrt, Sub, Truncate,
        And, Bitshift, Eq, Ge, Gt, Le, Lt, Ne, Not, Or, Xor,
        Copy, Dup, Exch, Index, Pop, Roll,
    }

    private enum Kind : byte
    {
        Int = 0,
        Real = 1,
        Bool = 2,
    }

    private struct Instr
    {
        public Op Op;
        public double Value;
        public int Target;
    }

    private static readonly Dictionary<string, Op> s_ops = new(StringComparer.Ordinal)
    {
        ["abs"] = Op.Abs, ["add"] = Op.Add, ["atan"] = Op.Atan, ["ceiling"] = Op.Ceiling, ["cos"] = Op.Cos, ["cvi"] = Op.Cvi,
        ["cvr"] = Op.Cvr, ["div"] = Op.Div, ["exp"] = Op.Exp, ["floor"] = Op.Floor, ["idiv"] = Op.Idiv, ["ln"] = Op.Ln,
        ["log"] = Op.Log, ["mod"] = Op.Mod, ["mul"] = Op.Mul, ["neg"] = Op.Neg, ["round"] = Op.Round, ["sin"] = Op.Sin,
        ["sqrt"] = Op.Sqrt, ["sub"] = Op.Sub, ["truncate"] = Op.Truncate, ["and"] = Op.And, ["bitshift"] = Op.Bitshift,
        ["eq"] = Op.Eq, ["ge"] = Op.Ge, ["gt"] = Op.Gt, ["le"] = Op.Le, ["lt"] = Op.Lt, ["ne"] = Op.Ne, ["not"] = Op.Not,
        ["or"] = Op.Or, ["xor"] = Op.Xor, ["copy"] = Op.Copy, ["dup"] = Op.Dup, ["exch"] = Op.Exch, ["index"] = Op.Index,
        ["pop"] = Op.Pop, ["roll"] = Op.Roll, ["true"] = Op.PushTrue, ["false"] = Op.PushFalse,
    };

    private readonly Instr[]? _code;
    private readonly int _stackSize;
    private readonly long _maxOps;
    private readonly int _inputs;

    /// <summary>Compiles <paramref name="program"/>; check <see cref="IsValid"/> and <see cref="PdfFunction.Problem"/>.</summary>
    public PostScriptFunction(double[] domain, double[] range, ReadOnlySpan<byte> program, long maxOperations)
        : base(domain.Length / 2, range.Length / 2, domain, range)
    {
        _inputs = domain.Length / 2;
        _maxOps = maxOperations;
        _code = Compile(program, _inputs, out int stack, out string? problem);
        _stackSize = stack;
        Problem = problem;
    }

    /// <summary>False when the program failed to compile; every evaluation then reports <see cref="FunctionStatus.BadProgram"/>.</summary>
    public bool IsValid => _code is not null;

    public override FunctionStatus Evaluate(ReadOnlySpan<double> input, Span<double> output)
    {
        FunctionStatus status = CheckArguments(input, output);
        int n = OutputCount;
        Instr[]? code = _code;
        if (code is null)
        {
            output.Slice(0, n).Clear();
            ClipOutputs(output);
            return status | FunctionStatus.BadProgram;
        }

        int cap = _stackSize;
        Span<double> v = stackalloc double[cap];
        Span<byte> k = stackalloc byte[cap];
        int sp = 0;
        for (int i = 0; i < _inputs; i++)
        {
            v[sp] = ClipInput(i, i < input.Length ? input[i] : 0);
            k[sp] = (byte)Kind.Real;
            sp++;
        }

        long ops = 0;
        int pc = 0;
        while (pc < code.Length)
        {
            if (++ops > _maxOps)
            {
                status |= FunctionStatus.OperationLimit;
                break;
            }

            ref readonly Instr ins = ref code[pc++];
            switch (ins.Op)
            {
                case Op.Nop:
                    break;
                case Op.PushInt:
                case Op.PushReal:
                case Op.PushTrue:
                case Op.PushFalse:
                    if (sp >= cap)
                    {
                        status |= FunctionStatus.StackOverflow;
                        goto Done;
                    }

                    switch (ins.Op)
                    {
                        case Op.PushInt:
                            v[sp] = ins.Value;
                            k[sp] = (byte)Kind.Int;
                            break;
                        case Op.PushReal:
                            v[sp] = ins.Value;
                            k[sp] = (byte)Kind.Real;
                            break;
                        case Op.PushTrue:
                            v[sp] = 1;
                            k[sp] = (byte)Kind.Bool;
                            break;
                        default:
                            v[sp] = 0;
                            k[sp] = (byte)Kind.Bool;
                            break;
                    }

                    sp++;
                    break;
                case Op.JumpIfFalse:
                    if (sp < 1)
                    {
                        status |= FunctionStatus.StackUnderflow;
                        goto Done;
                    }

                    sp--;
                    if (k[sp] != (byte)Kind.Bool)
                    {
                        status |= FunctionStatus.TypeError;
                    }

                    if (v[sp] == 0)
                    {
                        pc = ins.Target;
                    }

                    break;
                case Op.Jump:
                    pc = ins.Target;
                    break;

                // One operand, same kind out.
                case Op.Abs:
                case Op.Neg:
                    if (sp < 1)
                    {
                        status |= FunctionStatus.StackUnderflow;
                        goto Done;
                    }

                    if (k[sp - 1] == (byte)Kind.Bool)
                    {
                        status |= FunctionStatus.TypeError;
                        k[sp - 1] = (byte)Kind.Int;
                    }

                    v[sp - 1] = ins.Op == Op.Abs ? Math.Abs(v[sp - 1]) : -v[sp - 1];
                    if (k[sp - 1] == (byte)Kind.Int && v[sp - 1] > int.MaxValue)
                    {
                        k[sp - 1] = (byte)Kind.Real; // -int.MinValue does not fit
                    }

                    break;
                case Op.Ceiling:
                case Op.Floor:
                case Op.Round:
                case Op.Truncate:
                    if (sp < 1)
                    {
                        status |= FunctionStatus.StackUnderflow;
                        goto Done;
                    }

                    if (k[sp - 1] == (byte)Kind.Bool)
                    {
                        status |= FunctionStatus.TypeError;
                        k[sp - 1] = (byte)Kind.Int;
                    }

                    if (k[sp - 1] == (byte)Kind.Real)
                    {
                        double x = v[sp - 1];
                        v[sp - 1] = ins.Op switch
                        {
                            Op.Ceiling => Math.Ceiling(x),
                            Op.Floor => Math.Floor(x),
                            Op.Round => Math.Floor(x + 0.5),
                            _ => Math.Truncate(x),
                        };
                    }

                    break;
                case Op.Cvi:
                    if (sp < 1)
                    {
                        status |= FunctionStatus.StackUnderflow;
                        goto Done;
                    }

                    if (k[sp - 1] == (byte)Kind.Bool)
                    {
                        status |= FunctionStatus.TypeError;
                    }

                    v[sp - 1] = ToIntValue(v[sp - 1], ref status);
                    k[sp - 1] = (byte)Kind.Int;
                    break;
                case Op.Cvr:
                    if (sp < 1)
                    {
                        status |= FunctionStatus.StackUnderflow;
                        goto Done;
                    }

                    if (k[sp - 1] == (byte)Kind.Bool)
                    {
                        status |= FunctionStatus.TypeError;
                    }

                    k[sp - 1] = (byte)Kind.Real;
                    break;
                case Op.Sqrt:
                case Op.Ln:
                case Op.Log:
                case Op.Sin:
                case Op.Cos:
                    if (sp < 1)
                    {
                        status |= FunctionStatus.StackUnderflow;
                        goto Done;
                    }

                    if (k[sp - 1] == (byte)Kind.Bool)
                    {
                        status |= FunctionStatus.TypeError;
                    }

                    {
                        double x = v[sp - 1];
                        double r;
                        switch (ins.Op)
                        {
                            case Op.Sin:
                                r = DetMath.SinDeg(x);
                                break;
                            case Op.Cos:
                                r = DetMath.CosDeg(x);
                                break;
                            case Op.Sqrt:
                                if (x < 0 || double.IsNaN(x))
                                {
                                    status |= FunctionStatus.MathError;
                                    r = 0;
                                }
                                else
                                {
                                    r = Math.Sqrt(x);
                                }

                                break;
                            default:
                                if (!(x > 0) || double.IsInfinity(x))
                                {
                                    status |= FunctionStatus.MathError;
                                    r = 0;
                                }
                                else
                                {
                                    r = ins.Op == Op.Ln ? DetMath.Ln(x) : DetMath.Log10(x);
                                }

                                break;
                        }

                        v[sp - 1] = r;
                        k[sp - 1] = (byte)Kind.Real;
                    }

                    break;
                case Op.Not:
                    if (sp < 1)
                    {
                        status |= FunctionStatus.StackUnderflow;
                        goto Done;
                    }

                    if (k[sp - 1] == (byte)Kind.Bool)
                    {
                        v[sp - 1] = v[sp - 1] == 0 ? 1 : 0;
                    }
                    else
                    {
                        if (k[sp - 1] == (byte)Kind.Real)
                        {
                            status |= FunctionStatus.TypeError;
                        }

                        v[sp - 1] = ~(int)ToIntValue(v[sp - 1], ref status);
                        k[sp - 1] = (byte)Kind.Int;
                    }

                    break;

                // Two operands.
                case Op.Add:
                case Op.Sub:
                case Op.Mul:
                    if (sp < 2)
                    {
                        status |= FunctionStatus.StackUnderflow;
                        goto Done;
                    }

                    {
                        byte ka = k[sp - 2];
                        byte kb = k[sp - 1];
                        if (ka == (byte)Kind.Bool || kb == (byte)Kind.Bool)
                        {
                            status |= FunctionStatus.TypeError;
                        }

                        double a = v[sp - 2];
                        double b = v[sp - 1];
                        double r = ins.Op == Op.Add ? a + b : ins.Op == Op.Sub ? a - b : a * b;
                        sp--;
                        bool bothInt = ka != (byte)Kind.Real && kb != (byte)Kind.Real;
                        v[sp - 1] = r;
                        k[sp - 1] = (byte)(bothInt && r >= int.MinValue && r <= int.MaxValue ? Kind.Int : Kind.Real);
                    }

                    break;
                case Op.Div:
                case Op.Exp:
                case Op.Atan:
                    if (sp < 2)
                    {
                        status |= FunctionStatus.StackUnderflow;
                        goto Done;
                    }

                    {
                        if (k[sp - 2] == (byte)Kind.Bool || k[sp - 1] == (byte)Kind.Bool)
                        {
                            status |= FunctionStatus.TypeError;
                        }

                        double a = v[sp - 2];
                        double b = v[sp - 1];
                        double r;
                        if (ins.Op == Op.Div)
                        {
                            r = b == 0 ? double.NaN : a / b;
                        }
                        else if (ins.Op == Op.Exp)
                        {
                            r = DetMath.Pow(a, b);
                        }
                        else
                        {
                            r = a == 0 && b == 0 ? double.NaN : DetMath.Atan2Deg(a, b);
                        }

                        if (!double.IsFinite(r))
                        {
                            status |= FunctionStatus.MathError;
                            r = 0;
                        }

                        sp--;
                        v[sp - 1] = r;
                        k[sp - 1] = (byte)Kind.Real;
                    }

                    break;
                case Op.Idiv:
                case Op.Mod:
                case Op.Bitshift:
                    if (sp < 2)
                    {
                        status |= FunctionStatus.StackUnderflow;
                        goto Done;
                    }

                    {
                        if (k[sp - 2] != (byte)Kind.Int || k[sp - 1] != (byte)Kind.Int)
                        {
                            status |= FunctionStatus.TypeError;
                        }

                        long a = (long)ToIntValue(v[sp - 2], ref status);
                        long b = (long)ToIntValue(v[sp - 1], ref status);
                        double r;
                        if (ins.Op == Op.Bitshift)
                        {
                            r = b >= 32 || b <= -32 ? 0 : b >= 0 ? (int)((uint)a << (int)b) : (int)((uint)a >> (int)-b);
                        }
                        else if (b == 0)
                        {
                            status |= FunctionStatus.MathError;
                            r = 0;
                        }
                        else
                        {
                            r = ins.Op == Op.Idiv ? a / b : a % b;
                        }

                        sp--;
                        v[sp - 1] = r;
                        k[sp - 1] = (byte)(r >= int.MinValue && r <= int.MaxValue ? Kind.Int : Kind.Real);
                    }

                    break;
                case Op.And:
                case Op.Or:
                case Op.Xor:
                    if (sp < 2)
                    {
                        status |= FunctionStatus.StackUnderflow;
                        goto Done;
                    }

                    {
                        byte ka = k[sp - 2];
                        byte kb = k[sp - 1];
                        if (ka == (byte)Kind.Bool && kb == (byte)Kind.Bool)
                        {
                            bool a = v[sp - 2] != 0;
                            bool b = v[sp - 1] != 0;
                            bool r = ins.Op == Op.And ? a & b : ins.Op == Op.Or ? a | b : a ^ b;
                            sp--;
                            v[sp - 1] = r ? 1 : 0;
                            k[sp - 1] = (byte)Kind.Bool;
                        }
                        else
                        {
                            if (ka != (byte)Kind.Int || kb != (byte)Kind.Int)
                            {
                                status |= FunctionStatus.TypeError;
                            }

                            int a = (int)ToIntValue(v[sp - 2], ref status);
                            int b = (int)ToIntValue(v[sp - 1], ref status);
                            int r = ins.Op == Op.And ? a & b : ins.Op == Op.Or ? a | b : a ^ b;
                            sp--;
                            v[sp - 1] = r;
                            k[sp - 1] = (byte)Kind.Int;
                        }
                    }

                    break;
                case Op.Eq:
                case Op.Ne:
                case Op.Ge:
                case Op.Gt:
                case Op.Le:
                case Op.Lt:
                    if (sp < 2)
                    {
                        status |= FunctionStatus.StackUnderflow;
                        goto Done;
                    }

                    {
                        bool boolA = k[sp - 2] == (byte)Kind.Bool;
                        bool boolB = k[sp - 1] == (byte)Kind.Bool;
                        double a = v[sp - 2];
                        double b = v[sp - 1];
                        bool r;
                        if (ins.Op is Op.Eq or Op.Ne)
                        {
                            // A boolean never equals a number.
                            bool eq = boolA == boolB && a == b;
                            r = ins.Op == Op.Eq ? eq : !eq;
                        }
                        else
                        {
                            if (boolA || boolB)
                            {
                                status |= FunctionStatus.TypeError;
                            }

                            r = ins.Op switch
                            {
                                Op.Ge => a >= b,
                                Op.Gt => a > b,
                                Op.Le => a <= b,
                                _ => a < b,
                            };
                        }

                        sp--;
                        v[sp - 1] = r ? 1 : 0;
                        k[sp - 1] = (byte)Kind.Bool;
                    }

                    break;

                // Stack manipulation.
                case Op.Pop:
                    if (sp < 1)
                    {
                        status |= FunctionStatus.StackUnderflow;
                        goto Done;
                    }

                    sp--;
                    break;
                case Op.Dup:
                    if (sp < 1)
                    {
                        status |= FunctionStatus.StackUnderflow;
                        goto Done;
                    }

                    if (sp >= cap)
                    {
                        status |= FunctionStatus.StackOverflow;
                        goto Done;
                    }

                    v[sp] = v[sp - 1];
                    k[sp] = k[sp - 1];
                    sp++;
                    break;
                case Op.Exch:
                    if (sp < 2)
                    {
                        status |= FunctionStatus.StackUnderflow;
                        goto Done;
                    }

                    (v[sp - 1], v[sp - 2]) = (v[sp - 2], v[sp - 1]);
                    (k[sp - 1], k[sp - 2]) = (k[sp - 2], k[sp - 1]);
                    break;
                case Op.Copy:
                    if (sp < 1)
                    {
                        status |= FunctionStatus.StackUnderflow;
                        goto Done;
                    }

                    {
                        double cnt = ToIntValue(v[--sp], ref status);
                        if (cnt < 0 || cnt > sp)
                        {
                            status |= FunctionStatus.StackUnderflow;
                            goto Done;
                        }

                        int c = (int)cnt;
                        if (sp + c > cap)
                        {
                            status |= FunctionStatus.StackOverflow;
                            goto Done;
                        }

                        v.Slice(sp - c, c).CopyTo(v.Slice(sp));
                        k.Slice(sp - c, c).CopyTo(k.Slice(sp));
                        sp += c;
                    }

                    break;
                case Op.Index:
                    if (sp < 1)
                    {
                        status |= FunctionStatus.StackUnderflow;
                        goto Done;
                    }

                    {
                        double idx = ToIntValue(v[--sp], ref status);
                        if (idx < 0 || idx >= sp)
                        {
                            status |= FunctionStatus.StackUnderflow;
                            goto Done;
                        }

                        v[sp] = v[sp - 1 - (int)idx];
                        k[sp] = k[sp - 1 - (int)idx];
                        sp++;
                    }

                    break;
                default: // Op.Roll
                    if (sp < 2)
                    {
                        status |= FunctionStatus.StackUnderflow;
                        goto Done;
                    }

                    {
                        long j = (long)ToIntValue(v[--sp], ref status);
                        double cnt = ToIntValue(v[--sp], ref status);
                        if (cnt < 0 || cnt > sp)
                        {
                            status |= FunctionStatus.StackUnderflow;
                            goto Done;
                        }

                        int c = (int)cnt;
                        if (c > 1)
                        {
                            int shift = (int)(((j % c) + c) % c);
                            if (shift != 0)
                            {
                                // Rotate the top c entries toward the top by `shift`: reverse all, then each part.
                                Reverse(v.Slice(sp - c, c), k.Slice(sp - c, c));
                                Reverse(v.Slice(sp - c, shift), k.Slice(sp - c, shift));
                                Reverse(v.Slice(sp - c + shift, c - shift), k.Slice(sp - c + shift, c - shift));
                            }
                        }
                    }

                    break;
            }
        }

    Done:
        if (sp != n)
        {
            status |= FunctionStatus.BadResultCount;
        }

        for (int j = 0; j < n; j++)
        {
            int at = sp - n + j;
            output[j] = at >= 0 ? v[at] : 0;
        }

        ClipOutputs(output);
        return status;
    }

    private static void Reverse(Span<double> v, Span<byte> k)
    {
        v.Reverse();
        k.Reverse();
    }

    // Truncates toward zero into the 32-bit integer range; out-of-range values and NaN are a math error.
    private static double ToIntValue(double x, ref FunctionStatus status)
    {
        if (double.IsNaN(x))
        {
            status |= FunctionStatus.MathError;
            return 0;
        }

        double t = Math.Truncate(x);
        if (t > int.MaxValue)
        {
            status |= FunctionStatus.MathError;
            return int.MaxValue;
        }

        if (t < int.MinValue)
        {
            status |= FunctionStatus.MathError;
            return int.MinValue;
        }

        return t;
    }

    private static bool IsSpace(byte c) => c is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool IsDelimiter(byte c) => c is (byte)'{' or (byte)'}' or (byte)'%' or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'/';

    private static Instr[]? Compile(ReadOnlySpan<byte> src, int inputs, out int stackSize, out string? problem)
    {
        stackSize = 0;
        problem = null;
        if (inputs > MaxStack)
        {
            problem = "A Type 4 function with more than 256 inputs is not supported.";
            return null;
        }

        var code = new List<Instr>();
        var openStart = new List<int>();
        var openBase = new List<int>();
        var closed = new List<(int Start, int End)>();
        int growth = inputs;
        bool sawCopy = false;
        bool started = false;
        bool finished = false;
        int pos = 0;

        while (!finished)
        {
            while (pos < src.Length && (IsSpace(src[pos]) || src[pos] == (byte)'%'))
            {
                if (src[pos] == (byte)'%')
                {
                    while (pos < src.Length && src[pos] is not (10 or 13))
                    {
                        pos++;
                    }
                }
                else
                {
                    pos++;
                }
            }

            if (pos >= src.Length)
            {
                break;
            }

            byte c = src[pos];
            int pendingBase = openBase.Count > 0 ? openBase[^1] : 0;
            int pending = closed.Count - pendingBase;
            if (c == (byte)'{')
            {
                pos++;
                if (!started)
                {
                    started = true;
                    continue;
                }

                // One block may precede another: the pair is the two branches of an ifelse.
                if (pending > 1 || (pending == 1 && closed[^1].End != code.Count - 1))
                {
                    problem = "A '{' block is not followed by if or ifelse.";
                    return null;
                }

                openStart.Add(code.Count);
                openBase.Add(closed.Count);
                code.Add(new Instr { Op = Op.Nop });
            }
            else if (c == (byte)'}')
            {
                pos++;
                if (!started)
                {
                    problem = "The program does not start with '{'.";
                    return null;
                }

                if (pending != 0)
                {
                    problem = "A '{' block is not followed by if or ifelse.";
                    return null;
                }

                if (openStart.Count == 0)
                {
                    finished = true;
                    continue;
                }

                int s = openStart[^1];
                openStart.RemoveAt(openStart.Count - 1);
                openBase.RemoveAt(openBase.Count - 1);
                code.Add(new Instr { Op = Op.Nop });
                closed.Add((s, code.Count - 1));
            }
            else
            {
                if (!started)
                {
                    problem = "The program does not start with '{'.";
                    return null;
                }

                int start = pos;
                while (pos < src.Length && !IsSpace(src[pos]) && !IsDelimiter(src[pos]))
                {
                    pos++;
                }

                if (pos == start)
                {
                    problem = "Unexpected delimiter in program.";
                    return null;
                }

                string tok = System.Text.Encoding.Latin1.GetString(src.Slice(start, pos - start));
                if (tok == "if")
                {
                    if (pending < 1)
                    {
                        problem = "'if' without a '{' block.";
                        return null;
                    }

                    (int s, int e) = closed[^1];
                    closed.RemoveAt(closed.Count - 1);
                    code[s] = new Instr { Op = Op.JumpIfFalse, Target = e + 1 };
                }
                else if (tok == "ifelse")
                {
                    if (pending < 2)
                    {
                        problem = "'ifelse' needs two '{' blocks.";
                        return null;
                    }

                    (int bs, int be) = closed[^1];
                    (int tsx, int te) = closed[^2];
                    closed.RemoveRange(closed.Count - 2, 2);
                    code[tsx] = new Instr { Op = Op.JumpIfFalse, Target = bs };
                    code[te] = new Instr { Op = Op.Jump, Target = be + 1 };
                }
                else if (pending != 0)
                {
                    problem = "A '{' block is not followed by if or ifelse.";
                    return null;
                }
                else if (s_ops.TryGetValue(tok, out Op op))
                {
                    code.Add(new Instr { Op = op });
                    if (op is Op.PushTrue or Op.PushFalse or Op.Dup or Op.Index)
                    {
                        growth++;
                    }
                    else if (op == Op.Copy)
                    {
                        sawCopy = true;
                    }
                }
                else if (TryNumber(tok, out double value, out bool isInt))
                {
                    code.Add(new Instr { Op = isInt ? Op.PushInt : Op.PushReal, Value = value });
                    growth++;
                }
                else
                {
                    problem = "Unknown operator '" + (tok.Length > 32 ? tok.Substring(0, 32) : tok) + "'.";
                    return null;
                }

                if (code.Count > MaxInstructions)
                {
                    problem = "The program is too long.";
                    return null;
                }
            }
        }

        if (!started)
        {
            problem = "The program is empty.";
            return null;
        }

        if (!finished || openStart.Count > 0)
        {
            // A truncated stream: the program is not trusted.
            problem = "The program ends before its closing '}'.";
            return null;
        }

        if (closed.Count != 0)
        {
            problem = "A '{' block is not followed by if or ifelse.";
            return null;
        }

        // Drop the no-op markers and re-aim the jumps.
        var map = new int[code.Count + 1];
        int n = 0;
        for (int i = 0; i < code.Count; i++)
        {
            map[i] = n;
            if (code[i].Op != Op.Nop)
            {
                n++;
            }
        }

        map[code.Count] = n;
        var result = new Instr[n];
        int w = 0;
        for (int i = 0; i < code.Count; i++)
        {
            Instr ins = code[i];
            if (ins.Op == Op.Nop)
            {
                continue;
            }

            if (ins.Op is Op.JumpIfFalse or Op.Jump)
            {
                ins.Target = map[ins.Target];
            }

            result[w++] = ins;
        }

        stackSize = sawCopy ? MaxStack : Math.Clamp(growth, 1, MaxStack);
        return result;
    }

    private static bool TryNumber(string tok, out double value, out bool isInt)
    {
        value = 0;
        isInt = false;
        char c0 = tok[0];
        if (!(c0 is >= '0' and <= '9' || c0 is '+' or '-' or '.'))
        {
            return false;
        }

        int hash = tok.IndexOf('#', StringComparison.Ordinal);
        if (hash > 0)
        {
            // Radix number, base#digits (PostScript language reference 3.3.1).
            if (!int.TryParse(tok.AsSpan(0, hash), NumberStyles.None, CultureInfo.InvariantCulture, out int radix) || radix < 2 || radix > 36 || hash == tok.Length - 1)
            {
                return false;
            }

            long acc = 0;
            for (int i = hash + 1; i < tok.Length; i++)
            {
                char ch = tok[i];
                int d = ch is >= '0' and <= '9' ? ch - '0' : ch is >= 'a' and <= 'z' ? ch - 'a' + 10 : ch is >= 'A' and <= 'Z' ? ch - 'A' + 10 : 99;
                if (d >= radix)
                {
                    return false;
                }

                acc = (acc * radix) + d;
                if (acc > uint.MaxValue)
                {
                    return false;
                }
            }

            // Radix integers are 32-bit patterns, so 16#FFFFFFFF is -1.
            value = (int)(uint)acc;
            isInt = true;
            return true;
        }

        foreach (char ch in tok)
        {
            if (!(ch is >= '0' and <= '9' || ch is '+' or '-' or '.' or 'e' or 'E'))
            {
                return false;
            }
        }

        if (long.TryParse(tok, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l))
        {
            if (l >= int.MinValue && l <= int.MaxValue)
            {
                value = l;
                isInt = true;
                return true;
            }

            value = l;
            return true;
        }

        if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out double d2) && double.IsFinite(d2))
        {
            value = d2;
            return true;
        }

        return false;
    }
}
