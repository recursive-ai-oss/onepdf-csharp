namespace OnePdf.Content;

/// <summary>
/// The interpreter's own reading of a font dictionary for text positioning: <c>/Widths</c> and <c>/MissingWidth</c> of simple
/// fonts, <c>/DW</c> and <c>/W</c> of composite fonts, the font matrix of Type 3 fonts. A font layer replaces it through
/// <see cref="IPdfFontResolver"/>. Fonts that give no widths (the standard 14 without a <c>/Widths</c> array) advance by
/// 0.6 em for Courier and 0.5 em otherwise, until real metrics are attached.
/// </summary>
internal sealed class DictionaryFontMetrics : IPdfFontMetrics
{
    private const int MaxSimpleWidths = 1 << 16;
    private const int MaxRanges = 1 << 20;

    private readonly bool _composite;
    private readonly double[]? _simple;
    private readonly int _firstChar;
    private readonly double _missing;
    private readonly double _dw;
    private readonly double _dw2Advance = -1;
    private readonly (int First, int Last, double Width)[]? _ranges;

    private DictionaryFontMetrics(bool composite, double[]? simple, int firstChar, double missing, double dw, (int, int, double)[]? ranges, bool vertical, double w1)
    {
        _composite = composite;
        _simple = simple;
        _firstChar = firstChar;
        _missing = missing;
        _dw = dw;
        _ranges = ranges;
        IsVertical = vertical;
        _dw2Advance = w1;
    }

    public bool IsVertical { get; }

    public static DictionaryFontMetrics Create(PdfDictionary font, Action<string> note)
    {
        string subtype = font.GetName("Subtype") ?? string.Empty;
        if (subtype == "Type0")
        {
            return CreateComposite(font, note);
        }

        double scale = 0.001;
        if (subtype == "Type3" && font.GetArray("FontMatrix") is { Count: >= 4 } fm)
        {
            double a = fm.GetDouble(0);
            scale = a != 0 && double.IsFinite(a) ? Math.Abs(a) : 0.001;
        }

        int first = (int)Math.Clamp(font.GetInt("FirstChar", 0), 0, 255);
        PdfDictionary? desc = font.GetDictionary("FontDescriptor");
        double missing = desc is null ? 0 : desc.GetDouble("MissingWidth", 0) * scale;
        double[]? widths = null;
        if (font.GetArray("Widths") is { } wa)
        {
            int n = Math.Min(wa.Count, MaxSimpleWidths);
            widths = new double[n];
            for (int i = 0; i < n; i++)
            {
                widths[i] = wa.GetDouble(i) * scale;
            }
        }

        if (widths is null)
        {
            string baseFont = font.GetName("BaseFont") ?? string.Empty;
            missing = baseFont.Contains("Courier", StringComparison.OrdinalIgnoreCase) ? 0.6 : 0.5;
            if (subtype != "Type3")
            {
                note("A simple font has no /Widths; advances are estimated until font metrics are attached.");
            }
        }

        return new DictionaryFontMetrics(false, widths, first, missing, 0, null, false, 0);
    }

    private static DictionaryFontMetrics CreateComposite(PdfDictionary font, Action<string> note)
    {
        PdfDictionary? desc = font.GetArray("DescendantFonts") is { Count: > 0 } df ? df[0] as PdfDictionary : null;
        double dw = (desc?.GetDouble("DW", 1000) ?? 1000) / 1000.0;
        var ranges = new List<(int, int, double)>();
        if (desc?.GetArray("W") is { } w)
        {
            int i = 0;
            while (i < w.Count && ranges.Count < MaxRanges)
            {
                if (w[i] is not (PdfInteger or PdfReal))
                {
                    i++;
                    continue;
                }

                int c = (int)Math.Clamp(w.GetInt(i), 0, int.MaxValue / 2);
                if (i + 1 < w.Count && w[i + 1] is PdfArray list)
                {
                    for (int k = 0; k < list.Count && ranges.Count < MaxRanges; k++)
                    {
                        ranges.Add((c + k, c + k, list.GetDouble(k) / 1000.0));
                    }

                    i += 2;
                }
                else if (i + 2 < w.Count)
                {
                    int last = (int)Math.Clamp(w.GetInt(i + 1), 0, int.MaxValue / 2);
                    ranges.Add((c, Math.Max(c, last), w.GetDouble(i + 2) / 1000.0));
                    i += 3;
                }
                else
                {
                    break;
                }
            }
        }

        // Stable sort by first code: ties keep file order, and lookup prefers later entries.
        var arr = ranges.ToArray();
        var order = new int[arr.Length];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (x, y) => arr[x].Item1 != arr[y].Item1 ? arr[x].Item1.CompareTo(arr[y].Item1) : x.CompareTo(y));
        var sorted = new (int First, int Last, double Width)[arr.Length];
        for (int i = 0; i < order.Length; i++)
        {
            sorted[i] = arr[order[i]];
        }

        string enc = font.Get("Encoding") is PdfName en ? en.Value : string.Empty;
        bool vertical = enc.EndsWith("-V", StringComparison.Ordinal);
        if (font.Get("Encoding") is PdfStream es)
        {
            vertical = es.Dictionary.GetInt("WMode", 0) == 1;
        }

        double w1 = -1;
        if (desc?.GetArray("DW2") is { Count: >= 2 } dw2)
        {
            w1 = dw2.GetDouble(1, -1000) / 1000.0;
        }

        if (enc.Length > 0 && enc is not ("Identity-H" or "Identity-V"))
        {
            note("A composite font uses an encoding the interpreter does not read (" + enc + "); two-byte codes are assumed.");
        }
        else if (font.Get("Encoding") is PdfStream)
        {
            note("A composite font uses an embedded CMap the interpreter does not read; two-byte codes are assumed.");
        }

        return new DictionaryFontMetrics(true, null, 0, 0, dw, sorted, vertical, w1);
    }

    public int ReadCode(ReadOnlySpan<byte> bytes, out int code)
    {
        if (!_composite)
        {
            code = bytes[0];
            return 1;
        }

        if (bytes.Length >= 2)
        {
            code = (bytes[0] << 8) | bytes[1];
            return 2;
        }

        code = bytes[0];
        return 1;
    }

    public double GetWidth(int code)
    {
        if (!_composite)
        {
            int i = code - _firstChar;
            if (_simple is not null && i >= 0 && i < _simple.Length)
            {
                return _simple[i];
            }

            return _missing;
        }

        return LookupRange(code) ?? _dw;
    }

    public double GetVerticalAdvance(int code) => _dw2Advance;

    private double? LookupRange(int code)
    {
        (int First, int Last, double Width)[]? r = _ranges;
        if (r is null || r.Length == 0)
        {
            return null;
        }

        int lo = 0, hi = r.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (r[mid].First <= code)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        for (int k = found, steps = 0; k >= 0 && steps < 16; k--, steps++)
        {
            if (code <= r[k].Last)
            {
                return r[k].Width;
            }
        }

        return null;
    }
}
