using System.Globalization;
using System.Text;
using OnePdf.FontPrograms;

namespace OnePdf.FontData;

/// <summary>
/// Parses CMap programs (Adobe Technical Note #5014/#5099 syntax, PostScript tokens): embedded Type 0 encodings and
/// <c>/ToUnicode</c> streams. Understands <c>begincodespacerange</c>, <c>begincidrange</c>, <c>begincidchar</c>, <c>beginbfrange</c>
/// (destination string or array), <c>beginbfchar</c> (destination string or glyph name), <c>usecmap</c>, and the <c>/WMode</c>,
/// <c>/CMapName</c>, <c>/Registry</c>, <c>/Ordering</c> and <c>/Supplement</c> definitions. Everything else, including
/// <c>notdefrange</c>, is skipped.
/// </summary>
/// <remarks>
/// Lenient: malformed entries are skipped and the rest of the stream still parses; truncated input yields what was read.
/// Bounds: <see cref="MaxEntries"/> mappings, 4-byte codes, destinations up to <see cref="MaxDestinationBytes"/> bytes, and
/// section and array element counts that are never trusted for allocation. The scanner never recurses.
/// </remarks>
internal static class CMapParser
{
    /// <summary>Mappings (all kinds) accepted from one CMap; further ones are dropped.</summary>
    public const int MaxEntries = 1 << 20;

    /// <summary>Longest bfchar/bfrange destination in bytes.</summary>
    public const int MaxDestinationBytes = 256;

    /// <summary>Maps a <c>usecmap</c> name to a CMap, or null when unknown. The default resolves predefined CMaps.</summary>
    public delegate CMap? UseCMapResolver(string name);

    private enum T
    {
        End,
        Hex,
        Name,
        Number,
        Word,
        Str,
        ArrayOpen,
        ArrayClose,
        DictOpen,
        DictClose,
    }

    private enum Section
    {
        None,
        Codespace,
        CidRange,
        CidChar,
        BfRange,
        BfChar,
        Skip,
    }

    /// <summary>Parses CMap data. <paramref name="resolver"/> supplies <c>usecmap</c> parents; null uses the predefined CMaps.</summary>
    public static CMap Parse(ReadOnlySpan<byte> data, UseCMapResolver? resolver = null)
    {
        var lex = new Lexer(data);
        var spaces = new List<CodespaceRange>();
        var cid = new List<CidRange>[4];
        var bfr = new List<BfRange>[4];
        for (int i = 0; i < 4; i++)
        {
            cid[i] = [];
            bfr[i] = [];
        }

        var bfChars = new Dictionary<long, string>();
        int entries = 0;
        int order = 0;
        string? name = null, registry = null, ordering = null, useName = null;
        int wmode = 0, supplement = 0;
        Section sec = Section.None;

        // Operand history: the last few tokens, enough for "/Key value def", "/Name usecmap" and the section entries.
        Token a = default, b = default, c = default; // a = oldest of the three
        // A pending array destination of a bfrange: lo, hi and the collected strings.
        bool inArray = false;
        uint arrLo = 0, arrHi = 0;
        int arrLen = 0;
        List<string>? arrItems = null;

        while (true)
        {
            Token t = lex.Next();
            if (t.Type == T.End)
            {
                break;
            }

            if (inArray)
            {
                if (t.Type == T.ArrayClose)
                {
                    if (entries < MaxEntries && arrItems is not null && arrItems.Count > 0 && arrLen is >= 1 and <= 4 && arrHi >= arrLo)
                    {
                        long span = Math.Min((long)arrHi - arrLo + 1, arrItems.Count);
                        bfr[arrLen - 1].Add(new BfRange(arrLo, (uint)(arrLo + span - 1), string.Empty, [.. arrItems.GetRange(0, (int)span)], order++));
                        entries++;
                    }

                    inArray = false;
                    arrItems = null;
                }
                else if (t.Type == T.Hex && arrItems is not null && arrItems.Count < 65536)
                {
                    arrItems.Add(CMap.Utf16BeToString(t.Bytes));
                }

                continue;
            }

            if (t.Type == T.Word)
            {
                string w = t.Text;
                switch (w)
                {
                    case "begincodespacerange":
                        sec = Section.Codespace;
                        a = b = c = default;
                        continue;
                    case "begincidrange":
                        sec = Section.CidRange;
                        a = b = c = default;
                        continue;
                    case "begincidchar":
                        sec = Section.CidChar;
                        a = b = c = default;
                        continue;
                    case "beginbfrange":
                        sec = Section.BfRange;
                        a = b = c = default;
                        continue;
                    case "beginbfchar":
                        sec = Section.BfChar;
                        a = b = c = default;
                        continue;
                    case "beginnotdefrange":
                    case "beginnotdefchar":
                        sec = Section.Skip;
                        continue;
                    case "endcodespacerange":
                    case "endcidrange":
                    case "endcidchar":
                    case "endbfrange":
                    case "endbfchar":
                    case "endnotdefrange":
                    case "endnotdefchar":
                        sec = Section.None;
                        a = b = c = default;
                        continue;
                    case "usecmap":
                        if (sec == Section.None && c.Type == T.Name)
                        {
                            useName = c.Text;
                        }

                        a = b = c = default;
                        continue;
                    case "def":
                        if (sec == Section.None)
                        {
                            Definition(b, c, ref name, ref registry, ref ordering, ref wmode, ref supplement);
                        }

                        a = b = c = default;
                        continue;
                }

                if (sec == Section.None)
                {
                    // Other keywords (dict, begin, end, findresource...) end an operand group.
                    a = b = c = default;
                }

                continue;
            }

            if (sec == Section.None || sec == Section.Skip)
            {
                if (sec == Section.None && t.Type is T.Str or T.Number or T.Name)
                {
                    // Entries inside a << ... >> dictionary have no def of their own.
                    Definition(c, t, ref name, ref registry, ref ordering, ref wmode, ref supplement);
                }

                a = b;
                b = c;
                c = t;
                continue;
            }

            switch (sec)
            {
                case Section.Codespace:
                    // <lo> <hi>
                    if (t.Type == T.Hex)
                    {
                        if (b.Type == T.Hex)
                        {
                            AddCodespace(spaces, b.Bytes, t.Bytes);
                            b = default;
                        }
                        else
                        {
                            b = t;
                        }
                    }

                    break;
                case Section.CidRange:
                    // <lo> <hi> cid
                    if (t.Type == T.Number)
                    {
                        if (b.Type == T.Hex && a.Type == T.Hex && entries < MaxEntries)
                        {
                            AddCidRange(cid, a.Bytes, b.Bytes, t.Value, ref order, ref entries);
                        }

                        a = b = c = default;
                    }
                    else
                    {
                        a = b;
                        b = t;
                    }

                    break;
                case Section.CidChar:
                    // <code> cid
                    if (t.Type == T.Number)
                    {
                        if (b.Type == T.Hex && entries < MaxEntries)
                        {
                            AddCidRange(cid, b.Bytes, b.Bytes, t.Value, ref order, ref entries);
                        }

                        a = b = c = default;
                    }
                    else
                    {
                        a = b;
                        b = t;
                    }

                    break;
                case Section.BfChar:
                    // <src> <dst> | <src> /glyphname
                    if ((t.Type == T.Hex || t.Type == T.Name) && b.Type == T.Hex)
                    {
                        if (entries < MaxEntries && b.Bytes.Length is >= 1 and <= 4)
                        {
                            string? dst = t.Type == T.Hex ? CMap.Utf16BeToString(t.Bytes) : (GlyphList.TryGetUnicode(t.Text, out string u) ? u : null);
                            if (dst is not null)
                            {
                                bfChars[((long)b.Bytes.Length << 32) | CMap.ReadBigEndian(b.Bytes)] = dst;
                                entries++;
                            }
                        }

                        b = default;
                    }
                    else
                    {
                        b = t;
                    }

                    break;
                case Section.BfRange:
                    // <lo> <hi> <dst> | <lo> <hi> [ <dst> ... ]
                    if (t.Type == T.ArrayOpen && b.Type == T.Hex && a.Type == T.Hex)
                    {
                        arrLen = a.Bytes.Length;
                        arrLo = CMap.ReadBigEndian(a.Bytes);
                        arrHi = CMap.ReadBigEndian(b.Bytes);
                        arrItems = [];
                        inArray = true;
                        a = b = c = default;
                    }
                    else if (t.Type == T.Hex && b.Type == T.Hex && a.Type == T.Hex)
                    {
                        if (entries < MaxEntries && a.Bytes.Length is >= 1 and <= 4 && a.Bytes.Length == b.Bytes.Length)
                        {
                            uint lo = CMap.ReadBigEndian(a.Bytes), hi = CMap.ReadBigEndian(b.Bytes);
                            if (hi >= lo)
                            {
                                bfr[a.Bytes.Length - 1].Add(new BfRange(lo, hi, CMap.Utf16BeToString(t.Bytes), null, order++));
                                entries++;
                            }
                        }

                        a = b = c = default;
                    }
                    else
                    {
                        a = b;
                        b = t;
                    }

                    break;
            }
        }

        var cidArr = new CidRange[4][];
        var bfArr = new BfRange[4][];
        for (int i = 0; i < 4; i++)
        {
            CidRange[] cr = [.. cid[i]];
            Array.Sort(cr, static (x, y) => x.Lo != y.Lo ? x.Lo.CompareTo(y.Lo) : x.Order.CompareTo(y.Order));
            cidArr[i] = cr;
            BfRange[] br = [.. bfr[i]];
            Array.Sort(br, static (x, y) => x.Lo != y.Lo ? x.Lo.CompareTo(y.Lo) : x.Order.CompareTo(y.Order));
            bfArr[i] = br;
        }

        var map = new CMap(name, wmode, [.. spaces], cidArr, bfChars, bfArr)
        {
            Registry = registry,
            Ordering = ordering,
            Supplement = supplement,
        };
        if (useName is not null)
        {
            CMap? parent = resolver is not null ? resolver(useName) : PredefinedCMaps.TryGet(useName);
            if (parent is not null)
            {
                map.UseCMap = parent;
            }
            else
            {
                map.UnresolvedUseCMap = useName;
            }
        }

        return map;
    }

    private static void Definition(Token key, Token value, ref string? name, ref string? registry, ref string? ordering, ref int wmode, ref int supplement)
    {
        if (key.Type != T.Name)
        {
            return;
        }

        switch (key.Text)
        {
            case "WMode" when value.Type == T.Number:
                wmode = value.Value == 1 ? 1 : 0;
                break;
            case "CMapName" when value.Type == T.Name:
                name = value.Text;
                break;
            case "Registry" when value.Type == T.Str:
                registry = value.Text;
                break;
            case "Ordering" when value.Type == T.Str:
                ordering = value.Text;
                break;
            case "Supplement" when value.Type == T.Number:
                supplement = (int)Math.Clamp(value.Value, 0, 100000);
                break;
        }
    }

    private static void AddCodespace(List<CodespaceRange> spaces, byte[] lo, byte[] hi)
    {
        if (lo.Length is < 1 or > 4 || lo.Length != hi.Length || spaces.Count >= 4096)
        {
            return;
        }

        spaces.Add(new CodespaceRange(lo.Length, CMap.ReadBigEndian(lo), CMap.ReadBigEndian(hi)));
    }

    private static void AddCidRange(List<CidRange>[] cid, byte[] lo, byte[] hi, double value, ref int order, ref int entries)
    {
        if (lo.Length is < 1 or > 4 || lo.Length != hi.Length || !(value >= 0 && value <= int.MaxValue))
        {
            return;
        }

        uint l = CMap.ReadBigEndian(lo), h = CMap.ReadBigEndian(hi);
        if (h < l)
        {
            return;
        }

        cid[lo.Length - 1].Add(new CidRange(l, h, (int)value, order++));
        entries++;
    }

    private struct Token
    {
        private string? _text;
        private byte[]? _bytes;

        public T Type;
        public double Value;

        public string Text
        {
            readonly get => _text ?? string.Empty;
            set => _text = value;
        }

        public byte[] Bytes
        {
            readonly get => _bytes ?? [];
            set => _bytes = value;
        }
    }

    private ref struct Lexer
    {
        private readonly ReadOnlySpan<byte> _d;
        private int _p;

        public Lexer(ReadOnlySpan<byte> d)
        {
            _d = d;
            _p = 0;
        }

        private static bool IsWhite(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or 0;

        private static bool IsDelim(byte b) => b is (byte)'/' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'%';

        public Token Next()
        {
            while (_p < _d.Length)
            {
                byte b = _d[_p];
                if (IsWhite(b))
                {
                    _p++;
                }
                else if (b == (byte)'%')
                {
                    while (_p < _d.Length && _d[_p] is not ((byte)'\n' or (byte)'\r'))
                    {
                        _p++;
                    }
                }
                else
                {
                    break;
                }
            }

            if (_p >= _d.Length)
            {
                return new Token { Type = T.End, Text = string.Empty, Bytes = [] };
            }

            byte c = _d[_p];
            switch (c)
            {
                case (byte)'[':
                    _p++;
                    return new Token { Type = T.ArrayOpen, Text = "[", Bytes = [] };
                case (byte)']':
                    _p++;
                    return new Token { Type = T.ArrayClose, Text = "]", Bytes = [] };
                case (byte)'{':
                case (byte)'}':
                    _p++;
                    return new Token { Type = T.Word, Text = ((char)c).ToString(), Bytes = [] };
                case (byte)'<':
                    if (_p + 1 < _d.Length && _d[_p + 1] == (byte)'<')
                    {
                        _p += 2;
                        return new Token { Type = T.DictOpen, Text = "<<", Bytes = [] };
                    }

                    return ReadHex();
                case (byte)'>':
                    _p++;
                    if (_p < _d.Length && _d[_p] == (byte)'>')
                    {
                        _p++;
                    }

                    return new Token { Type = T.DictClose, Text = ">>", Bytes = [] };
                case (byte)'(':
                    return ReadString();
                case (byte)')':
                    _p++;
                    return Next();
            }

            bool literal = c == (byte)'/';
            if (literal)
            {
                _p++;
            }

            int s = _p;
            while (_p < _d.Length && !IsWhite(_d[_p]) && !IsDelim(_d[_p]))
            {
                _p++;
            }

            string text = Encoding.Latin1.GetString(_d.Slice(s, _p - s));
            if (literal)
            {
                return new Token { Type = T.Name, Text = text, Bytes = [] };
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v))
            {
                return new Token { Type = T.Number, Text = text, Value = v, Bytes = [] };
            }

            return new Token { Type = T.Word, Text = text, Bytes = [] };
        }

        private Token ReadHex()
        {
            _p++; // '<'
            var bytes = new List<byte>(8);
            int hi = -1;
            while (_p < _d.Length && _d[_p] != (byte)'>')
            {
                int v = _d[_p] switch
                {
                    >= (byte)'0' and <= (byte)'9' => _d[_p] - '0',
                    >= (byte)'a' and <= (byte)'f' => _d[_p] - 'a' + 10,
                    >= (byte)'A' and <= (byte)'F' => _d[_p] - 'A' + 10,
                    _ => -1,
                };
                _p++;
                if (v < 0)
                {
                    continue;
                }

                if (hi < 0)
                {
                    hi = v;
                }
                else
                {
                    if (bytes.Count < MaxDestinationBytes)
                    {
                        bytes.Add((byte)((hi << 4) | v));
                    }

                    hi = -1;
                }
            }

            if (hi >= 0 && bytes.Count < MaxDestinationBytes)
            {
                bytes.Add((byte)(hi << 4)); // an odd digit count is padded with a zero digit
            }

            if (_p < _d.Length)
            {
                _p++; // '>'
            }

            return new Token { Type = T.Hex, Text = string.Empty, Bytes = [.. bytes] };
        }

        private Token ReadString()
        {
            _p++; // '('
            int depth = 1;
            var sb = new StringBuilder();
            while (_p < _d.Length && depth > 0)
            {
                byte x = _d[_p++];
                if (x == (byte)'\\' && _p < _d.Length)
                {
                    sb.Append((char)_d[_p++]);
                }
                else if (x == (byte)'(')
                {
                    depth++;
                    sb.Append('(');
                }
                else if (x == (byte)')')
                {
                    if (--depth > 0)
                    {
                        sb.Append(')');
                    }
                }
                else if (sb.Length < 1024)
                {
                    sb.Append((char)x);
                }
            }

            return new Token { Type = T.Str, Text = sb.ToString(), Bytes = [] };
        }
    }
}
