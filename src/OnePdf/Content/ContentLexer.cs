using System.Text;

namespace OnePdf.Content;

internal enum ContentOp : byte
{
    Unknown,
    CloseFillStrokeEo, // b*
    CloseFillStroke, // b
    FillStrokeEo, // B*
    FillStroke, // B
    BeginMarkedProps, // BDC
    BeginInline, // BI
    BeginMarked, // BMC
    BeginText, // BT
    BeginCompat, // BX
    Curve, // c
    Concat, // cm
    StrokeSpaceName, // CS
    FillSpaceName, // cs
    Dash, // d
    CharProcWidth, // d0
    CharProcBox, // d1
    Do, // Do
    MarkPointProps, // DP
    EndInline, // EI
    EndMarked, // EMC
    EndText, // ET
    EndCompat, // EX
    FillNz, // f
    FillNzOld, // F
    FillEo, // f*
    StrokeGray, // G
    FillGray, // g
    SetExtGState, // gs
    Close, // h
    Flatness, // i
    InlineData, // ID
    LineJoin, // j
    LineCap, // J
    StrokeCmyk, // K
    FillCmyk, // k
    Line, // l
    Move, // m
    MiterLimit, // M
    MarkPoint, // MP
    EndPath, // n
    Save, // q
    Restore, // Q
    Rect, // re
    StrokeRgb, // RG
    FillRgb, // rg
    RenderingIntent, // ri
    CloseStroke, // s
    Stroke, // S
    StrokeColor, // SC
    FillColor, // sc
    StrokeColorN, // SCN
    FillColorN, // scn
    Shading, // sh
    NextLine, // T*
    CharSpace, // Tc
    TextMove, // Td
    TextMoveLeading, // TD
    TextFont, // Tf
    ShowText, // Tj
    ShowTextArray, // TJ
    Leading, // TL
    TextMatrix, // Tm
    TextRender, // Tr
    TextRise, // Ts
    WordSpace, // Tw
    HorizScale, // Tz
    CurveV, // v
    LineWidth, // w
    ClipNz, // W
    ClipEo, // W*
    CurveY, // y
    NextLineShow, // '
    SpacedNextLineShow, // "
}

internal enum OperandKind : byte
{
    Number,
    Name,
    String,
    Array,
    Bool,
    Null,
    Object,
}

/// <summary>One operand of a content-stream operator. Arrays of simple values refer to the lexer's item buffer.</summary>
internal struct Operand
{
    public OperandKind Kind;
    public bool IsInteger;
    public bool Bool;
    public double Num;
    public string? Name;
    public ReadOnlyMemory<byte> Str;
    public int ArrayStart;
    public int ArrayCount;
    public PdfObject? Obj;
}

internal enum ContentToken : byte
{
    End,
    Operand,
    Operator,
}

/// <summary>
/// Tokenizer for content streams. It never allocates for numbers or for strings without escapes, keeps arrays of simple
/// values in a reusable buffer (so <c>TJ</c> costs no allocation per element), and builds <see cref="PdfObject"/> trees
/// only for dictionaries and nested arrays (marked-content properties, inline image dictionaries), with the depth and
/// size bounded.
/// </summary>
internal sealed class ContentLexer
{
    /// <summary>Most simple elements kept for one flat array operand.</summary>
    public const int MaxArrayItems = 1 << 16;

    /// <summary>Most objects built for one dictionary or nested-array operand.</summary>
    private const int MaxObjectNodes = 1 << 18;

    private static readonly Dictionary<int, ContentOp> Ops = BuildOps();

    private readonly ReadOnlyMemory<byte> _mem;
    private readonly int _maxDepth;
    private int _pos;
    private Operand[] _items = new Operand[16];
    private int _itemCount;

    public ContentLexer(ReadOnlyMemory<byte> data, int maxDepth)
    {
        _mem = data;
        _maxDepth = Math.Max(1, maxDepth);
    }

    public int Position
    {
        get => _pos;
        set => _pos = Math.Clamp(value, 0, _mem.Length);
    }

    public ReadOnlyMemory<byte> Data => _mem;

    public bool AtEnd => _pos >= _mem.Length;

    private int _kwStart;
    private int _kwLength;

    /// <summary>The text of the last operator token, for diagnostics about unknown operators.</summary>
    public string LastKeyword => Encoding.Latin1.GetString(_mem.Span.Slice(_kwStart, Math.Min(_kwLength, 40)));

    public ReadOnlySpan<Operand> Items(in Operand array) => _items.AsSpan(array.ArrayStart, array.ArrayCount);

    public void ResetItems() => _itemCount = 0;

    private static Dictionary<int, ContentOp> BuildOps()
    {
        var d = new Dictionary<int, ContentOp>();
        void A(string s, ContentOp op) => d[Pack(s)] = op;
        A("b*", ContentOp.CloseFillStrokeEo);
        A("b", ContentOp.CloseFillStroke);
        A("B*", ContentOp.FillStrokeEo);
        A("B", ContentOp.FillStroke);
        A("BDC", ContentOp.BeginMarkedProps);
        A("BI", ContentOp.BeginInline);
        A("BMC", ContentOp.BeginMarked);
        A("BT", ContentOp.BeginText);
        A("BX", ContentOp.BeginCompat);
        A("c", ContentOp.Curve);
        A("cm", ContentOp.Concat);
        A("CS", ContentOp.StrokeSpaceName);
        A("cs", ContentOp.FillSpaceName);
        A("d", ContentOp.Dash);
        A("d0", ContentOp.CharProcWidth);
        A("d1", ContentOp.CharProcBox);
        A("Do", ContentOp.Do);
        A("DP", ContentOp.MarkPointProps);
        A("EI", ContentOp.EndInline);
        A("EMC", ContentOp.EndMarked);
        A("ET", ContentOp.EndText);
        A("EX", ContentOp.EndCompat);
        A("f", ContentOp.FillNz);
        A("F", ContentOp.FillNzOld);
        A("f*", ContentOp.FillEo);
        A("G", ContentOp.StrokeGray);
        A("g", ContentOp.FillGray);
        A("gs", ContentOp.SetExtGState);
        A("h", ContentOp.Close);
        A("i", ContentOp.Flatness);
        A("ID", ContentOp.InlineData);
        A("j", ContentOp.LineJoin);
        A("J", ContentOp.LineCap);
        A("K", ContentOp.StrokeCmyk);
        A("k", ContentOp.FillCmyk);
        A("l", ContentOp.Line);
        A("m", ContentOp.Move);
        A("M", ContentOp.MiterLimit);
        A("MP", ContentOp.MarkPoint);
        A("n", ContentOp.EndPath);
        A("q", ContentOp.Save);
        A("Q", ContentOp.Restore);
        A("re", ContentOp.Rect);
        A("RG", ContentOp.StrokeRgb);
        A("rg", ContentOp.FillRgb);
        A("ri", ContentOp.RenderingIntent);
        A("s", ContentOp.CloseStroke);
        A("S", ContentOp.Stroke);
        A("SC", ContentOp.StrokeColor);
        A("sc", ContentOp.FillColor);
        A("SCN", ContentOp.StrokeColorN);
        A("scn", ContentOp.FillColorN);
        A("sh", ContentOp.Shading);
        A("T*", ContentOp.NextLine);
        A("Tc", ContentOp.CharSpace);
        A("Td", ContentOp.TextMove);
        A("TD", ContentOp.TextMoveLeading);
        A("Tf", ContentOp.TextFont);
        A("Tj", ContentOp.ShowText);
        A("TJ", ContentOp.ShowTextArray);
        A("TL", ContentOp.Leading);
        A("Tm", ContentOp.TextMatrix);
        A("Tr", ContentOp.TextRender);
        A("Ts", ContentOp.TextRise);
        A("Tw", ContentOp.WordSpace);
        A("Tz", ContentOp.HorizScale);
        A("v", ContentOp.CurveV);
        A("w", ContentOp.LineWidth);
        A("W", ContentOp.ClipNz);
        A("W*", ContentOp.ClipEo);
        A("y", ContentOp.CurveY);
        A("'", ContentOp.NextLineShow);
        A("\"", ContentOp.SpacedNextLineShow);
        return d;
    }

    private static int Pack(string s)
    {
        int k = s.Length;
        for (int i = 0; i < s.Length; i++)
        {
            k = (k << 8) | s[i];
        }

        return k;
    }

    private static int Pack(ReadOnlySpan<byte> s)
    {
        int k = s.Length;
        for (int i = 0; i < s.Length; i++)
        {
            k = (k << 8) | s[i];
        }

        return k;
    }

    public static bool IsWhite(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    public static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    public static bool IsRegular(byte b) => !IsWhite(b) && !IsDelimiter(b);

    public void SkipWhiteAndComments()
    {
        ReadOnlySpan<byte> s = _mem.Span;
        while (_pos < s.Length)
        {
            byte b = s[_pos];
            if (IsWhite(b))
            {
                _pos++;
            }
            else if (b == '%')
            {
                while (_pos < s.Length && s[_pos] is not (10 or 13))
                {
                    _pos++;
                }
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>Reads the next token. Operands come back in <paramref name="operand"/>; operators in <paramref name="op"/>.</summary>
    public ContentToken Next(out Operand operand, out ContentOp op)
    {
        operand = default;
        op = ContentOp.Unknown;
        while (true)
        {
            SkipWhiteAndComments();
            ReadOnlySpan<byte> s = _mem.Span;
            if (_pos >= s.Length)
            {
                return ContentToken.End;
            }

            byte b = s[_pos];
            switch (b)
            {
                case (byte)'/':
                    _pos++;
                    operand.Kind = OperandKind.Name;
                    operand.Name = ReadName(s);
                    return ContentToken.Operand;
                case (byte)'(':
                    _pos++;
                    operand.Kind = OperandKind.String;
                    operand.Str = ReadLiteralString(s);
                    return ContentToken.Operand;
                case (byte)'<':
                    if (_pos + 1 < s.Length && s[_pos + 1] == '<')
                    {
                        operand = ReadComposite();
                        return ContentToken.Operand;
                    }

                    _pos++;
                    operand.Kind = OperandKind.String;
                    operand.Str = ReadHexString(s);
                    return ContentToken.Operand;
                case (byte)'[':
                    operand = ReadArrayOperand();
                    return ContentToken.Operand;
                case (byte)']':
                case (byte)')':
                case (byte)'>':
                case (byte)'{':
                case (byte)'}':
                    // Stray delimiter: not valid content, skip it.
                    _pos++;
                    continue;
                default:
                    break;
            }

            int start = _pos;
            while (_pos < s.Length && IsRegular(s[_pos]))
            {
                _pos++;
            }

            ReadOnlySpan<byte> word = s[start.._pos];
            if (b is >= (byte)'0' and <= (byte)'9' or (byte)'+' or (byte)'-' or (byte)'.')
            {
                operand.Kind = OperandKind.Number;
                operand.Num = ParseNumber(word, out bool isInt);
                operand.IsInteger = isInt;
                return ContentToken.Operand;
            }

            if (word.SequenceEqual("true"u8))
            {
                operand.Kind = OperandKind.Bool;
                operand.Bool = true;
                return ContentToken.Operand;
            }

            if (word.SequenceEqual("false"u8))
            {
                operand.Kind = OperandKind.Bool;
                return ContentToken.Operand;
            }

            if (word.SequenceEqual("null"u8))
            {
                operand.Kind = OperandKind.Null;
                return ContentToken.Operand;
            }

            _kwStart = start;
            _kwLength = word.Length;
            if (word.Length <= 3 && Ops.TryGetValue(Pack(word), out ContentOp known))
            {
                op = known;
                return ContentToken.Operator;
            }

            op = ContentOp.Unknown;
            return ContentToken.Operator;
        }
    }

    /// <summary>
    /// Parses a number leniently: an optional sign (repeated signs are read as one), digits with at most one point;
    /// anything after a second point, a sign inside the number or other characters is dropped. A token with no digits
    /// reads as 0. Digits beyond what a double holds exactly lose precision, never wrap.
    /// </summary>
    public static double ParseNumber(ReadOnlySpan<byte> w, out bool isInteger)
    {
        int i = 0;
        bool neg = false;
        while (i < w.Length && w[i] is (byte)'+' or (byte)'-')
        {
            if (w[i] == '-')
            {
                neg = true;
            }

            i++;
        }

        double whole = 0;
        while (i < w.Length && w[i] is >= (byte)'0' and <= (byte)'9')
        {
            whole = (whole * 10) + (w[i] - '0');
            i++;
        }

        isInteger = true;
        double result = whole;
        if (i < w.Length && w[i] == '.')
        {
            isInteger = false;
            i++;
            long frac = 0;
            int digits = 0;
            while (i < w.Length && w[i] is >= (byte)'0' and <= (byte)'9')
            {
                if (digits < 15)
                {
                    frac = (frac * 10) + (w[i] - '0');
                    digits++;
                }

                i++;
            }

            result += frac / Pow10(digits);
        }

        return result == 0 ? 0 : (neg ? -result : result);
    }

    private static double Pow10(int n)
    {
        double p = 1;
        for (int i = 0; i < n; i++)
        {
            p *= 10;
        }

        return p;
    }

    private string ReadName(ReadOnlySpan<byte> s)
    {
        int start = _pos;
        bool escapes = false;
        bool ascii = true;
        while (_pos < s.Length && IsRegular(s[_pos]))
        {
            if (s[_pos] == '#')
            {
                escapes = true;
            }

            if (s[_pos] >= 128)
            {
                ascii = false;
            }

            _pos++;
        }

        ReadOnlySpan<byte> raw = s[start.._pos];
        if (!escapes)
        {
            return ascii ? Encoding.Latin1.GetString(raw) : DecodeNameBytes(raw);
        }

        Span<byte> buf = raw.Length <= 256 ? stackalloc byte[raw.Length] : new byte[raw.Length];
        int n = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '#' && i + 2 < raw.Length && Hex(raw[i + 1]) >= 0 && Hex(raw[i + 2]) >= 0)
            {
                buf[n++] = (byte)((Hex(raw[i + 1]) << 4) | Hex(raw[i + 2]));
                i += 2;
            }
            else
            {
                buf[n++] = raw[i];
            }
        }

        return DecodeNameBytes(buf[..n]);
    }

    private static string DecodeNameBytes(ReadOnlySpan<byte> b)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(b);
        }
        catch (ArgumentException)
        {
            return Encoding.Latin1.GetString(b);
        }
    }

    private static int Hex(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };

    private ReadOnlyMemory<byte> ReadLiteralString(ReadOnlySpan<byte> s)
    {
        int start = _pos;
        int depth = 1;
        bool plain = true;
        int p = _pos;
        while (p < s.Length)
        {
            byte b = s[p];
            if (b == '\\')
            {
                plain = false;
                p += 2;
                continue;
            }

            if (b == '\r')
            {
                plain = false;
            }
            else if (b == '(')
            {
                depth++;
            }
            else if (b == ')')
            {
                depth--;
                if (depth == 0)
                {
                    break;
                }
            }

            p++;
        }

        int end = Math.Min(p, s.Length);
        _pos = Math.Min(p + 1, s.Length);
        if (plain)
        {
            return _mem[start..end];
        }

        // Decode escapes (ISO 32000-2 7.3.4.2); an end-of-line inside the string reads as a single LF.
        var outp = new List<byte>(end - start);
        for (int i = start; i < end; i++)
        {
            byte b = s[i];
            if (b == '\\')
            {
                i++;
                if (i >= end)
                {
                    break;
                }

                byte e = s[i];
                switch (e)
                {
                    case (byte)'n': outp.Add(10); break;
                    case (byte)'r': outp.Add(13); break;
                    case (byte)'t': outp.Add(9); break;
                    case (byte)'b': outp.Add(8); break;
                    case (byte)'f': outp.Add(12); break;
                    case 13:
                        if (i + 1 < end && s[i + 1] == 10)
                        {
                            i++;
                        }

                        break;
                    case 10:
                        break;
                    case >= (byte)'0' and <= (byte)'7':
                        {
                            int v = e - '0';
                            for (int k = 0; k < 2 && i + 1 < end && s[i + 1] is >= (byte)'0' and <= (byte)'7'; k++)
                            {
                                i++;
                                v = (v * 8) + (s[i] - '0');
                            }

                            outp.Add((byte)(v & 0xFF));
                            break;
                        }

                    default:
                        outp.Add(e);
                        break;
                }
            }
            else if (b == 13)
            {
                if (i + 1 < end && s[i + 1] == 10)
                {
                    i++;
                }

                outp.Add(10);
            }
            else
            {
                outp.Add(b);
            }
        }

        return outp.ToArray();
    }

    private ReadOnlyMemory<byte> ReadHexString(ReadOnlySpan<byte> s)
    {
        var outp = new List<byte>();
        int hi = -1;
        while (_pos < s.Length)
        {
            byte b = s[_pos++];
            if (b == '>')
            {
                break;
            }

            int h = Hex(b);
            if (h < 0)
            {
                continue;
            }

            if (hi < 0)
            {
                hi = h;
            }
            else
            {
                outp.Add((byte)((hi << 4) | h));
                hi = -1;
            }
        }

        if (hi >= 0)
        {
            outp.Add((byte)(hi << 4));
        }

        return outp.ToArray();
    }

    // ---- arrays and composites ----

    private Operand ReadArrayOperand()
    {
        int save = _pos;
        int saveItems = _itemCount;
        _pos++; // '['
        int start = _itemCount;
        ReadOnlySpan<byte> s = _mem.Span;
        while (true)
        {
            SkipWhiteAndComments();
            if (_pos >= s.Length)
            {
                break;
            }

            byte b = s[_pos];
            if (b == ']')
            {
                _pos++;
                break;
            }

            if (b is (byte)'[' || (b == '<' && _pos + 1 < s.Length && s[_pos + 1] == '<'))
            {
                // Nested composites: take the general path from the opening bracket.
                _itemCount = saveItems;
                _pos = save;
                return ReadComposite();
            }

            if (b is (byte)')' or (byte)'>' or (byte)'{' or (byte)'}')
            {
                _pos++;
                continue;
            }

            Operand item = default;
            if (b == '/')
            {
                _pos++;
                item.Kind = OperandKind.Name;
                item.Name = ReadName(s);
            }
            else if (b == '(')
            {
                _pos++;
                item.Kind = OperandKind.String;
                item.Str = ReadLiteralString(s);
            }
            else if (b == '<')
            {
                _pos++;
                item.Kind = OperandKind.String;
                item.Str = ReadHexString(s);
            }
            else
            {
                int st = _pos;
                while (_pos < s.Length && IsRegular(s[_pos]))
                {
                    _pos++;
                }

                ReadOnlySpan<byte> w = s[st.._pos];
                if (b is >= (byte)'0' and <= (byte)'9' or (byte)'+' or (byte)'-' or (byte)'.')
                {
                    item.Kind = OperandKind.Number;
                    item.Num = ParseNumber(w, out bool isInt);
                    item.IsInteger = isInt;
                }
                else if (w.SequenceEqual("true"u8))
                {
                    item.Kind = OperandKind.Bool;
                    item.Bool = true;
                }
                else if (w.SequenceEqual("false"u8))
                {
                    item.Kind = OperandKind.Bool;
                }
                else if (w.SequenceEqual("null"u8))
                {
                    item.Kind = OperandKind.Null;
                }
                else
                {
                    // A keyword inside an array is not valid; if it is an operator the array was unterminated.
                    // Hand the keyword back to the main loop and close the array here.
                    _pos = st;
                    break;
                }
            }

            if (_itemCount - start < MaxArrayItems)
            {
                if (_itemCount == _items.Length)
                {
                    Array.Resize(ref _items, _items.Length * 2);
                }

                _items[_itemCount++] = item;
            }
        }

        return new Operand { Kind = OperandKind.Array, ArrayStart = start, ArrayCount = _itemCount - start };
    }

    private struct Frame
    {
        public PdfArray? Array;
        public PdfDictionary? Dict;
        public string? Key;
    }

    /// <summary>Reads a dictionary or nested array starting at the current '[' or '&lt;&lt;' into a COS object tree.</summary>
    private Operand ReadComposite()
    {
        PdfObject? obj = ReadObjectTree();
        return new Operand { Kind = OperandKind.Object, Obj = obj };
    }

    /// <summary>Reads one value (any type) at the current position as a COS object; null for null or on a stray token.</summary>
    public PdfObject? ReadValue(out bool isKeyword, out ContentOp keyword)
    {
        isKeyword = false;
        keyword = ContentOp.Unknown;
        SkipWhiteAndComments();
        ReadOnlySpan<byte> s = _mem.Span;
        if (_pos >= s.Length)
        {
            isKeyword = true;
            return null;
        }

        byte b = s[_pos];
        if (b == '[' || (b == '<' && _pos + 1 < s.Length && s[_pos + 1] == '<'))
        {
            return ReadObjectTree();
        }

        int save = _itemCount;
        ContentToken t = Next(out Operand o, out ContentOp kwOp);
        _itemCount = save;
        if (t == ContentToken.Operator)
        {
            isKeyword = true;
            keyword = kwOp;
            return null;
        }

        if (t == ContentToken.End)
        {
            isKeyword = true;
            return null;
        }

        return ToObject(in o);
    }

    public PdfObject? ToObject(in Operand o)
    {
        switch (o.Kind)
        {
            case OperandKind.Number:
                return o.IsInteger && Math.Abs(o.Num) < 9e15 ? new PdfInteger((long)o.Num) : new PdfReal(o.Num);
            case OperandKind.Name:
                return new PdfName(o.Name ?? string.Empty);
            case OperandKind.String:
                return new PdfString(o.Str.ToArray());
            case OperandKind.Bool:
                return new PdfBoolean(o.Bool);
            case OperandKind.Array:
                {
                    var a = new PdfArray(null);
                    ReadOnlySpan<Operand> items = Items(in o);
                    for (int i = 0; i < items.Length; i++)
                    {
                        if (ToObject(in items[i]) is { } x)
                        {
                            a.Add(x);
                        }
                        else
                        {
                            a.Add(PdfNull.Instance);
                        }
                    }

                    return a;
                }

            case OperandKind.Object:
                return o.Obj;
            default:
                return null;
        }
    }

    private PdfObject? ReadObjectTree()
    {
        ReadOnlySpan<byte> s = _mem.Span;
        var stack = new List<Frame>();
        PdfObject? result = null;
        int nodes = 0;
        bool overflow = false;
        int extra = 0; // nesting beyond the depth limit: skipped, not stored

        void Deliver(PdfObject? value)
        {
            if (stack.Count == 0)
            {
                result = value;
                return;
            }

            Frame f = stack[^1];
            if (f.Array is { } arr)
            {
                if (!overflow)
                {
                    arr.Add(value ?? PdfNull.Instance);
                }
            }
            else if (f.Dict is { } dict)
            {
                if (f.Key is null)
                {
                    // A dictionary key must be a name; a non-name value in key position is dropped.
                    if (value is PdfName n)
                    {
                        f.Key = n.Value;
                        stack[^1] = f;
                    }
                }
                else
                {
                    if (value is not null && !overflow)
                    {
                        dict.Set(f.Key, value);
                    }

                    f.Key = null;
                    stack[^1] = f;
                }
            }
        }

        do
        {
            SkipWhiteAndComments();
            if (_pos >= s.Length)
            {
                break;
            }

            byte b = s[_pos];
            if (b == '[' || (b == '<' && _pos + 1 < s.Length && s[_pos + 1] == '<'))
            {
                bool isArray = b == '[';
                _pos += isArray ? 1 : 2;
                if (extra > 0 || stack.Count >= _maxDepth || ++nodes > MaxObjectNodes)
                {
                    extra++;
                    continue;
                }

                stack.Add(isArray ? new Frame { Array = new PdfArray(null) } : new Frame { Dict = new PdfDictionary(null) });
                continue;
            }

            if (b == ']' || (b == '>' && _pos + 1 < s.Length && s[_pos + 1] == '>'))
            {
                _pos += b == ']' ? 1 : 2;
                if (extra > 0)
                {
                    extra--;
                    continue;
                }

                if (stack.Count == 0)
                {
                    continue;
                }

                Frame top = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                PdfObject done = (PdfObject?)top.Array ?? top.Dict!;
                Deliver(done);
                continue;
            }

            if (b is (byte)')' or (byte)'>' or (byte)'{' or (byte)'}')
            {
                _pos++;
                continue;
            }

            if (++nodes > MaxObjectNodes)
            {
                overflow = true;
            }

            int save = _itemCount;
            // A keyword here means the composite was never closed; give it back and close everything.
            int before = _pos;
            ContentToken t = Next(out Operand o, out _);
            _itemCount = save;
            if (t == ContentToken.Operator)
            {
                _pos = before;
                break;
            }

            if (t == ContentToken.End)
            {
                break;
            }

            if (extra == 0)
            {
                Deliver(ToObject(in o));
            }
        }
        while (stack.Count > 0);

        // Close anything left open (truncated input).
        while (stack.Count > 0)
        {
            Frame top = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            PdfObject done = (PdfObject?)top.Array ?? top.Dict!;
            Deliver(done);
        }

        return result;
    }

    // ---- inline images ----

    /// <summary>
    /// Reads the key/value pairs after <c>BI</c> up to <c>ID</c>. Keys and the colour-space and filter names are mapped from
    /// their abbreviations (ISO 32000-2 8.9.7). Returns null when the stream ends first.
    /// </summary>
    public PdfDictionary? ReadInlineImageDictionary()
    {
        var dict = new PdfDictionary(null);
        string? key = null;
        int guard = 0;
        while (guard++ < 4096)
        {
            SkipWhiteAndComments();
            if (_pos >= _mem.Length)
            {
                return null;
            }

            int before = _pos;
            PdfObject? v = ReadValue(out bool isKeyword, out ContentOp kw);
            if (isKeyword)
            {
                if (kw == ContentOp.InlineData)
                {
                    break;
                }

                if (_pos >= _mem.Length)
                {
                    return null;
                }

                // Unexpected keyword inside the dictionary: ignore it unless it is clearly the end of the image
                // (EI), in which case there is no data section.
                if (kw == ContentOp.EndInline)
                {
                    _pos = before;
                    return dict;
                }

                key = null;
                continue;
            }

            if (key is null)
            {
                if (v is PdfName n)
                {
                    key = ExpandKey(n.Value);
                }

                continue;
            }

            if (v is not null)
            {
                dict.Set(key, ExpandValue(key, v));
            }

            key = null;
        }

        return dict;
    }

    private static string ExpandKey(string k) => k switch
    {
        "BPC" => "BitsPerComponent",
        "CS" => "ColorSpace",
        "D" => "Decode",
        "DP" => "DecodeParms",
        "F" => "Filter",
        "H" => "Height",
        "IM" => "ImageMask",
        "I" => "Interpolate",
        "W" => "Width",
        "L" => "Length",
        _ => k,
    };

    private static string ExpandColorSpaceName(string n) => n switch
    {
        "G" => "DeviceGray",
        "RGB" => "DeviceRGB",
        "CMYK" => "DeviceCMYK",
        "I" => "Indexed",
        _ => n,
    };

    private static string ExpandFilterName(string n) => n switch
    {
        "AHx" => "ASCIIHexDecode",
        "A85" => "ASCII85Decode",
        "LZW" => "LZWDecode",
        "Fl" => "FlateDecode",
        "RL" => "RunLengthDecode",
        "CCF" => "CCITTFaxDecode",
        "DCT" => "DCTDecode",
        _ => n,
    };

    private static PdfObject ExpandValue(string key, PdfObject v)
    {
        if (key == "ColorSpace")
        {
            if (v is PdfName n)
            {
                string e = ExpandColorSpaceName(n.Value);
                return ReferenceEquals(e, n.Value) ? v : new PdfName(e);
            }

            if (v is PdfArray a && a.Count > 0 && a.GetRaw(0) is PdfName first)
            {
                var copy = new PdfArray(null);
                copy.Add(new PdfName(ExpandColorSpaceName(first.Value)));
                for (int i = 1; i < a.Count; i++)
                {
                    PdfObject item = a.GetRaw(i);
                    // The base space of an Indexed space may itself be abbreviated.
                    copy.Add(i == 1 && item is PdfName bn ? new PdfName(ExpandColorSpaceName(bn.Value)) : item);
                }

                return copy;
            }
        }
        else if (key == "Filter")
        {
            if (v is PdfName n)
            {
                return new PdfName(ExpandFilterName(n.Value));
            }

            if (v is PdfArray a)
            {
                var copy = new PdfArray(null);
                for (int i = 0; i < a.Count; i++)
                {
                    PdfObject item = a.GetRaw(i);
                    copy.Add(item is PdfName fn ? new PdfName(ExpandFilterName(fn.Value)) : item);
                }

                return copy;
            }
        }

        return v;
    }

    /// <summary>
    /// Reads the data after <c>ID</c> and the closing <c>EI</c>. When <paramref name="expectedLength"/> is known (no filter)
    /// and an <c>EI</c> follows that many bytes, that is the end; otherwise the end is found by scanning for an <c>EI</c>
    /// that stands alone and is followed by plausible content (ISO 32000-2 8.9.7 says the length must be known from the
    /// dictionary, but writers disagree). The position ends after the <c>EI</c>.
    /// </summary>
    public ReadOnlyMemory<byte> ReadInlineImageData(long expectedLength, out bool endFound)
    {
        ReadOnlySpan<byte> s = _mem.Span;
        endFound = false;
        // One white-space byte after ID; a CR LF pair counts as one.
        if (_pos < s.Length && IsWhite(s[_pos]))
        {
            if (s[_pos] == 13 && _pos + 1 < s.Length && s[_pos + 1] == 10)
            {
                _pos += 2;
            }
            else
            {
                _pos++;
            }
        }

        int start = _pos;
        if (expectedLength >= 0 && start + expectedLength <= s.Length)
        {
            int p = start + (int)expectedLength;
            int q = p;
            while (q < s.Length && IsWhite(s[q]))
            {
                q++;
            }

            if (q + 1 < s.Length + 0 && s[q] == 'E' && s[q + 1] == 'I' && (q + 2 >= s.Length || !IsRegular(s[q + 2])))
            {
                _pos = q + 2;
                endFound = true;
                return _mem[start..p];
            }

            // Exact length without a trailing EI after one stray byte (CR LF vs LF).
            if (start > 0 && s[start - 1] == 10 && start - 2 >= 0 && s[start - 2] == 13)
            {
                // Data may begin one byte earlier than assumed; handled by the scan below.
            }
        }

        for (int i = start; i + 1 < s.Length; i++)
        {
            if (s[i] != 'E' || s[i + 1] != 'I')
            {
                continue;
            }

            bool before = i == start || IsWhite(s[i - 1]);
            bool after = i + 2 >= s.Length || IsWhite(s[i + 2]) || IsDelimiter(s[i + 2]);
            if (!before || !after || !LooksLikeContent(s, i + 2))
            {
                continue;
            }

            int end = i;
            if (end > start && IsWhite(s[end - 1]))
            {
                end--;
            }

            _pos = i + 2;
            endFound = true;
            return _mem[start..end];
        }

        _pos = s.Length;
        return _mem[start..];
    }

    /// <summary>After a candidate EI: the next few bytes must look like content-stream text, not binary data.</summary>
    private static bool LooksLikeContent(ReadOnlySpan<byte> s, int from)
    {
        int end = Math.Min(s.Length, from + 12);
        for (int i = from; i < end; i++)
        {
            byte b = s[i];
            if (b >= 128 || (b < 32 && !IsWhite(b)))
            {
                return false;
            }
        }

        return true;
    }
}
