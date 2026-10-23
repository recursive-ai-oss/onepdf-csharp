namespace OnePdf.Internal;

/// <summary>
/// Builds COS objects from tokens. Nesting is handled with an explicit stack, so input depth never becomes call depth,
/// and the depth is checked against <see cref="PdfLimits.MaxNestingDepth"/> before anything is allocated for a new level.
/// </summary>
internal sealed class ObjectParser
{
    private struct Frame
    {
        public PdfArray? Array;
        public PdfDictionary? Dict;
        public string? Key;
    }

    private readonly Lexer _lx;
    private readonly PdfResolver? _resolver;
    private readonly int _maxDepth;
    private readonly DiagnosticBag? _diag;
    private readonly int _objNum;
    private readonly PdfDocument? _decrypting;
    private readonly int _generation;
    private Frame[] _stack = [];

    public ObjectParser(Lexer lexer, PdfResolver? resolver, PdfLimits limits, DiagnosticBag? diag, int objectNumber = -1, PdfDocument? decrypting = null, int generation = 0)
    {
        _decrypting = decrypting;
        _generation = generation;
        _lx = lexer;
        _resolver = resolver;
        _maxDepth = limits.MaxNestingDepth;
        _diag = diag;
        _objNum = objectNumber;
    }

    /// <summary>Rough bytes of managed memory the objects built so far take, for the cache budget.</summary>
    public long EstimatedBytes { get; private set; }

    /// <summary>
    /// Reads one object. Returns null at the end of data, or when the next thing is a bare keyword (returned in
    /// <paramref name="keyword"/>, with the lexer left after it).
    /// </summary>
    public PdfObject? ReadObject(out string? keyword)
    {
        keyword = null;
        int depth = 0;
        while (true)
        {
            Token t = _lx.NextToken();
            PdfObject? value = null;
            switch (t.Kind)
            {
                case TokenKind.Eof:
                    if (depth == 0)
                    {
                        return null;
                    }

                    _diag?.Warn("Unexpected end of data inside an array or dictionary; closed it.", _objNum, t.Start);
                    value = Unwind(ref depth);
                    return value;
                case TokenKind.Junk:
                    continue;
                case TokenKind.ArrayOpen:
                    Push(ref depth, new Frame { Array = new PdfArray(_resolver) }, t.Start);
                    continue;
                case TokenKind.DictOpen:
                    Push(ref depth, new Frame { Dict = new PdfDictionary(_resolver) }, t.Start);
                    continue;
                case TokenKind.ArrayClose:
                    if (depth > 0 && _stack[depth - 1].Array is not null)
                    {
                        value = _stack[--depth].Array;
                        break;
                    }

                    _diag?.Warn("Stray ']' ignored.", _objNum, t.Start);
                    continue;
                case TokenKind.DictClose:
                    if (depth > 0 && _stack[depth - 1].Dict is not null)
                    {
                        if (_stack[depth - 1].Key is not null)
                        {
                            _diag?.Warn("Dictionary key without a value dropped.", _objNum, t.Start);
                        }

                        value = _stack[--depth].Dict;
                        break;
                    }

                    _diag?.Warn("Stray '>>' ignored.", _objNum, t.Start);
                    continue;
                case TokenKind.Integer:
                    value = ReadIntegerOrReference(t);
                    break;
                case TokenKind.Real:
                    value = Count(new PdfReal(t.Real), 24);
                    break;
                case TokenKind.String:
                    value = Count(new PdfString(_decrypting is null ? t.Bytes! : _decrypting.DecryptStringBytes(t.Bytes!, _objNum, _generation)), 32 + t.Bytes!.Length);
                    break;
                case TokenKind.Name:
                    if (depth > 0 && _stack[depth - 1].Dict is not null && _stack[depth - 1].Key is null)
                    {
                        // A dictionary key: no object is needed for it.
                        _stack[depth - 1].Key = t.Text;
                        EstimatedBytes += 16;
                        continue;
                    }

                    value = Count(new PdfName(t.Text!), 32 + (2 * t.Text!.Length));
                    break;
                case TokenKind.Keyword:
                    switch (t.Text)
                    {
                        case "true":
                            value = Count(new PdfBoolean(true), 24);
                            break;
                        case "false":
                            value = Count(new PdfBoolean(false), 24);
                            break;
                        case "null":
                            value = PdfNull.Instance;
                            break;
                        case "endobj" or "endstream" or "stream" or "obj" or "xref" or "trailer" or "startxref":
                            if (depth == 0)
                            {
                                keyword = t.Text;
                                return null;
                            }

                            _diag?.Warn($"Unterminated array or dictionary before '{t.Text}'; closed it.", _objNum, t.Start);
                            _lx.Position = t.Start;
                            return Unwind(ref depth);
                        default:
                            continue; // unknown bare keyword: skipped
                    }

                    break;
            }

            if (depth == 0)
            {
                return value;
            }

            Attach(ref _stack[depth - 1], value!, t.Start);
        }
    }

    private PdfObject? Unwind(ref int depth)
    {
        PdfObject? result = null;
        while (depth > 0)
        {
            ref Frame f = ref _stack[--depth];
            PdfObject done = (PdfObject?)f.Array ?? f.Dict!;
            if (depth == 0)
            {
                result = done;
            }
            else
            {
                Attach(ref _stack[depth - 1], done, -1);
            }
        }

        return result;
    }

    private void Push(ref int depth, Frame f, long at)
    {
        if (depth >= _maxDepth)
        {
            throw new PdfLimitException($"Arrays and dictionaries are nested deeper than {_maxDepth} levels.") { ObjectNumber = _objNum, Offset = at };
        }

        if (depth == _stack.Length)
        {
            Array.Resize(ref _stack, Math.Max(4, _stack.Length * 2));
        }

        EstimatedBytes += 64;
        _stack[depth++] = f;
    }

    private void Attach(ref Frame f, PdfObject value, long at)
    {
        if (f.Array is not null)
        {
            f.Array.Add(value);
            EstimatedBytes += 8;
            return;
        }

        if (f.Key is null)
        {
            if (value is PdfName n)
            {
                f.Key = n.Value;
            }
            else
            {
                _diag?.Warn("Dictionary key is not a name; entry skipped.", _objNum, at);
            }

            return;
        }

        if (value is not PdfNull)
        {
            f.Dict!.Set(f.Key, value);
            EstimatedBytes += 56;
        }

        f.Key = null;
    }

    private PdfObject ReadIntegerOrReference(Token t)
    {
        if (t.Int >= 0 && t.Int <= int.MaxValue)
        {
            long save = _lx.Position;
            Token g = _lx.NextToken();
            if (g.Kind == TokenKind.Integer && g.Int is >= 0 and <= 65535)
            {
                Token r = _lx.NextToken();
                if (r.IsKeyword("R"))
                {
                    _resolver?.Doc.NoteReference((int)t.Int);
                    return Count(new PdfReference((int)t.Int, (int)g.Int), 32);
                }
            }

            _lx.Position = save;
        }

        return Count(new PdfInteger(t.Int), 24);
    }

    private T Count<T>(T o, int bytes)
        where T : PdfObject
    {
        EstimatedBytes += bytes;
        return o;
    }
}
