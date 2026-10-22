using System.Collections.Concurrent;
using OnePdf.FontData;
using OnePdf.FontPrograms;

namespace OnePdf.PdfFonts;

/// <summary>Per-CID horizontal widths (<c>/W</c>) or vertical metrics (<c>/W2</c>), stored as sorted ranges and searched by bisection.</summary>
internal sealed class CidMetricsTable
{
    /// <summary>Entries accepted from one array; further ones are dropped.</summary>
    public const int MaxEntries = 1 << 20;

    private readonly Range[] _ranges;

    private CidMetricsTable(Range[] ranges) => _ranges = ranges;

    private readonly record struct Range(int Lo, int Hi, int Order, double A, double B, double C);

    public bool IsEmpty => _ranges.Length == 0;

    /// <summary>Parses <c>/W</c>: <c>c [w1 w2 ...]</c> and <c>cfirst clast w</c> groups.</summary>
    public static CidMetricsTable ParseW(PdfArray? a) => Parse(a, vertical: false);

    /// <summary>Parses <c>/W2</c>: <c>c [w1y vx vy ...]</c> and <c>cfirst clast w1y vx vy</c> groups.</summary>
    public static CidMetricsTable ParseW2(PdfArray? a) => Parse(a, vertical: true);

    private static CidMetricsTable Parse(PdfArray? a, bool vertical)
    {
        var list = new List<Range>();
        if (a is null)
        {
            return new CidMetricsTable([]);
        }

        int per = vertical ? 3 : 1;
        int i = 0;
        while (i < a.Count && list.Count < MaxEntries)
        {
            if (a[i] is not (PdfInteger or PdfReal))
            {
                i++;
                continue;
            }

            int first = Clamp(a.GetDouble(i));
            if (i + 1 >= a.Count)
            {
                break;
            }

            if (a[i + 1] is PdfArray inner)
            {
                for (int k = 0; k + per <= inner.Count && list.Count < MaxEntries; k += per)
                {
                    long cid = (long)first + (k / per);
                    if (cid > int.MaxValue)
                    {
                        break;
                    }

                    list.Add(MakeRange((int)cid, (int)cid, list.Count, inner.GetDouble(k), vertical ? inner.GetDouble(k + 1) : 0, vertical ? inner.GetDouble(k + 2) : 0));
                }

                i += 2;
            }
            else if (i + 1 + per < a.Count)
            {
                int last = Clamp(a.GetDouble(i + 1));
                list.Add(MakeRange(first, last, list.Count, a.GetDouble(i + 2), vertical ? a.GetDouble(i + 3) : 0, vertical ? a.GetDouble(i + 4) : 0));
                i += 2 + per;
            }
            else
            {
                break;
            }
        }

        Range[] arr = [.. list];
        Array.Sort(arr, static (x, y) => x.Lo != y.Lo ? x.Lo.CompareTo(y.Lo) : x.Order.CompareTo(y.Order));
        return new CidMetricsTable(arr);
    }

    private static Range MakeRange(int lo, int hi, int order, double a, double b, double c) =>
        hi < lo ? new Range(hi, lo, order, a, b, c) : new Range(lo, hi, order, a, b, c);

    private static int Clamp(double v) => (int)Math.Clamp(v, 0, int.MaxValue);

    /// <summary>Looks a CID up. For <c>/W</c> only <paramref name="a"/> (the width) is meaningful; for <c>/W2</c> they are w1y, vx, vy.</summary>
    public bool TryGet(int cid, out double a, out double b, out double c)
    {
        Range[] r = _ranges;
        int lo = 0, hi = r.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (r[mid].Lo <= cid)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        // Overlaps are rare and wrong; the nearest enclosing earlier-starting range wins.
        for (int k = found; k >= 0 && k > found - 16; k--)
        {
            if (cid <= r[k].Hi)
            {
                a = r[k].A;
                b = r[k].B;
                c = r[k].C;
                return true;
            }
        }

        a = b = c = 0;
        return false;
    }
}

/// <summary>
/// A Type 0 font (ISO 32000-2 9.7): a CMap turns the string's bytes into CIDs, the CIDFont maps CIDs to glyphs (through the
/// charset of a CID-keyed CFF, a <c>CIDToGIDMap</c> for TrueType, or a substitute's Unicode cmap) and to metrics.
/// </summary>
internal sealed class CompositeFont : PdfFont
{
    private const int MaxCachedGlyphs = 1 << 18;
    private const int MaxCidToGidBytes = 1 << 17;

    private readonly PdfDictionary _cidFont;
    private readonly CMap _cmap;
    private readonly CMap? _toUnicode;
    private readonly CidToUnicode? _cidToUnicode;
    private readonly string? _ordering;
    private readonly bool _codeIsUnicode;
    private readonly byte[]? _cidToGid;
    private readonly ProgramUnicode? _programUnicode;
    private readonly CidMetricsTable _w;
    private readonly CidMetricsTable _w2;
    private readonly double _dw;
    private readonly double _dw2Y;
    private readonly double _dw2W;
    private readonly ConcurrentDictionary<long, FontGlyph> _cache = new();

    public CompositeFont(PdfDictionary dictionary, FontContext context)
        : base(dictionary, context)
    {
        PdfDictionary? cid = null;
        PdfObject? desc0 = dictionary.Get("DescendantFonts");
        if (desc0 is PdfArray arr && arr.Count > 0)
        {
            cid = arr[0] as PdfDictionary;
        }
        else if (desc0 is PdfDictionary d)
        {
            cid = d;
        }

        if (cid is null)
        {
            context.Warn($"Type 0 font '{Name}' has no usable DescendantFonts entry; glyphs map to CID 0.");
            cid = new PdfDictionary(null);
        }

        _cidFont = cid;
        if (Name.Length == 0)
        {
            Name = cid.GetName("BaseFont") ?? string.Empty;
        }

        DescriptorInfo desc = DescriptorInfo.Read(cid.GetDictionary("FontDescriptor"));
        Flags = desc.Flags;
        FontBBox = desc.BBox;

        _cmap = LoadEncoding(context, dictionary);
        WMode = _cmap.WMode;
        string? ordering = null;
        if (cid.GetDictionary("CIDSystemInfo") is { } info)
        {
            ordering = info.GetString("Ordering")?.GetText();
        }

        if (string.IsNullOrEmpty(ordering) || ordering == "Identity")
        {
            ordering = _cmap.Ordering ?? ordering;
        }

        _ordering = ordering;
        _cidToUnicode = CidToUnicode.Get(ordering);
        string cmapName = _cmap.Name ?? (dictionary.Get("Encoding") as PdfName)?.Value ?? string.Empty;
        _codeIsUnicode = cmapName.Contains("-UCS2-", StringComparison.Ordinal) || cmapName.Contains("-UTF16-", StringComparison.Ordinal) || cmapName.Contains("-UTF32-", StringComparison.Ordinal);

        Program = FontSupport.LoadEmbedded(context, desc, Name);
        IsEmbedded = Program is not null;
        EmbeddedFailed = Program is null && FontSupport.HasEmbeddedStream(desc);
        if (Program is null)
        {
            PdfFontRequest req = FontSupport.BuildRequest(Name, desc, ordering, WMode == 1);
            Program = FontSupport.LoadSubstitute(context, req);
            IsSubstituted = Program is not null;
            context.Document.ReportInfo(Program is null
                ? $"No font program for '{Name}': glyph outlines are unavailable (metrics and text still work)."
                : $"Font '{Name}' is not embedded; a substitute is used.", context.ObjectNumber, -1);
        }

        if (Program is not null && !IsSubstituted)
        {
            _programUnicode = new ProgramUnicode(Program);
        }

        if (Program is SfntFont { Cff: null } && cid.Get("CIDToGIDMap") is PdfStream map)
        {
            _cidToGid = FontSupport.ReadStream(context, map, MaxCidToGidBytes, "CIDToGIDMap");
        }

        _toUnicode = FontSupport.LoadToUnicode(context, dictionary);
        _dw = cid.ContainsKey("DW") ? cid.GetDouble("DW") : 1000;
        _w = CidMetricsTable.ParseW(cid.GetArray("W"));
        _dw2Y = 880;
        _dw2W = -1000;
        if (cid.GetArray("DW2") is { Count: >= 2 } dw2)
        {
            _dw2Y = dw2.GetDouble(0);
            _dw2W = dw2.GetDouble(1);
        }

        _w2 = WMode == 1 ? CidMetricsTable.ParseW2(cid.GetArray("W2")) : CidMetricsTable.ParseW2(null);
    }

    /// <inheritdoc />
    public override bool IsComposite => true;

    internal override void ClearGlyphCache() => _cache.Clear();

    /// <summary>The CMap that splits strings into codes and maps them to CIDs.</summary>
    internal CMap Encoding => _cmap;

    private static CMap LoadEncoding(FontContext ctx, PdfDictionary font)
    {
        PdfObject? enc = font.Get("Encoding");
        if (enc is PdfName n)
        {
            CMap? m = n.Value is "Identity-H" ? CMap.CreateIdentity(0) : n.Value is "Identity-V" ? CMap.CreateIdentity(1) : PredefinedCMaps.TryGet(n.Value);
            if (m is not null)
            {
                return m;
            }

            ctx.Warn($"Unknown predefined CMap '{n.Value}'; Identity-H is assumed.");
        }
        else if (enc is PdfStream s)
        {
            byte[]? bytes = FontSupport.ReadStream(ctx, s, FontSupport.MaxCMapBytes, "embedded CMap");
            if (bytes is not null)
            {
                try
                {
                    CMap m = CMapParser.Parse(bytes);
                    if (m.UseCMap is null)
                    {
                        string? parent = s.Dictionary.GetName("UseCMap");
                        if (parent is not null)
                        {
                            m.UseCMap = PredefinedCMaps.TryGet(parent);
                        }
                    }

                    long wm = s.Dictionary.GetInt("WMode", -1);
                    if (wm is 0 or 1)
                    {
                        m.WMode = (int)wm;
                    }

                    return m;
                }
                catch (FontProgramException ex)
                {
                    ctx.Warn($"The embedded CMap could not be parsed ({ex.Message}); Identity-H is assumed.");
                }
            }
        }
        else
        {
            ctx.Warn("Type 0 font without a usable Encoding; Identity-H is assumed.");
        }

        return CMap.CreateIdentity(0);
    }

    /// <inheritdoc />
    public override int Next(ReadOnlySpan<byte> text, int position, out FontGlyph? glyph)
    {
        if ((uint)position >= (uint)text.Length || !_cmap.ReadCode(text[position..], out uint code, out int length, out bool valid) || length <= 0)
        {
            glyph = null;
            return position;
        }

        long key = ((long)length << 33) | ((long)(valid ? 1 : 0) << 32) | code;
        if (!_cache.TryGetValue(key, out glyph))
        {
            glyph = Build(code, length, valid);
            if (_cache.Count < MaxCachedGlyphs)
            {
                glyph = _cache.GetOrAdd(key, glyph);
            }
        }

        return position + length;
    }

    private FontGlyph Build(uint code, int length, bool valid)
    {
        int cid = valid ? _cmap.ToCid(code, length) : 0;
        if (cid < 0)
        {
            cid = 0;
        }

        // Unicode: ToUnicode, then a Unicode-coded CMap, then (below) the embedded program, then the character collection.
        bool hasUnicode = FontSupport.TryToUnicode(_toUnicode, code, length, out string text);
        if (!hasUnicode && _codeIsUnicode && valid)
        {
            hasUnicode = TryCodeAsUnicode(code, length, out text);
        }

        FontProgram? p = Program;
        int gid = -1;
        bool exists = false;
        if (p is not null)
        {
            if (IsSubstituted)
            {
                if (!hasUnicode && CollectionUnicode(cid) is int u and > 0)
                {
                    text = char.ConvertFromUtf32(u);
                    hasUnicode = true;
                }

                gid = FontSupport.FindGlyphByNameOrUnicode(p, null, hasUnicode ? text : null);
                exists = gid > 0;
                if (gid < 0)
                {
                    gid = 0;
                }
            }
            else
            {
                gid = EmbeddedGid(p, cid);
                exists = gid > 0 && gid < p.GlyphCount;
                if (gid < 0 || gid >= p.GlyphCount)
                {
                    gid = 0;
                }

                if (!hasUnicode && exists && _programUnicode is not null && _programUnicode.TryGet(gid, out string pu))
                {
                    text = pu;
                    hasUnicode = true;
                }
            }
        }

        if (!hasUnicode && CollectionUnicode(cid) is int cu and > 0 && cu <= 0x10FFFF && cu is not (>= 0xD800 and <= 0xDFFF))
        {
            text = char.ConvertFromUtf32(cu);
            hasUnicode = true;
        }

        if (!hasUnicode && valid && length == 1 && _cidToUnicode is not null && !_codeIsUnicode)
        {
            // The single-byte range of the legacy CJK encodings is ASCII (JIS Roman, GBK, Big5, KS C 5601) and, in Shift-JIS,
            // half-width katakana; their half-width CIDs have no entry in the character-collection table.
            if (code is >= 0x20 and <= 0x7E)
            {
                text = ((char)code).ToString();
                hasUnicode = true;
            }
            else if (code is >= 0xA1 and <= 0xDF && _cmap.Name is { } n && n.Contains("RKSJ", StringComparison.Ordinal))
            {
                text = ((char)(0xFF61 + (code - 0xA1))).ToString();
                hasUnicode = true;
            }
        }

        double w = _w.TryGet(cid, out double wv, out _, out _) ? wv : _dw;
        double vAdv = 0, ox = 0, oy = 0;
        if (WMode == 1)
        {
            if (_w2.TryGet(cid, out double w1, out double vx, out double vy))
            {
                vAdv = w1 / 1000.0;
                ox = vx / 1000.0;
                oy = vy / 1000.0;
            }
            else
            {
                vAdv = _dw2W / 1000.0;
                ox = w / 2000.0;
                oy = _dw2Y / 1000.0;
            }
        }

        double[]? matrix = p?.FontMatrix;
        if (IsSubstituted && p is not null && exists && w > 0)
        {
            matrix = SimpleFont.ScaleToWidth(p, gid, w);
        }

        return new FontGlyph(this)
        {
            Code = code,
            CodeLength = length,
            Cid = cid,
            Gid = p is null ? -1 : gid,
            Unicode = hasUnicode ? text : "\uFFFD",
            HasUnicode = hasUnicode,
            Exists = exists,
            Advance = w / 1000.0,
            VerticalAdvance = vAdv,
            OriginX = ox,
            OriginY = oy,
            OutlineMatrix = matrix,
        };
    }

    /// <summary>The character-collection table first, then the vertical and half-width variants it leaves out; 0 when unknown.</summary>
    private int CollectionUnicode(int cid)
    {
        if (_cidToUnicode is null)
        {
            return 0;
        }

        int u = _cidToUnicode.Lookup(cid);
        return u > 0 || _ordering is null ? u : CidUnicodeFallback.Lookup(_ordering, cid);
    }

    private int EmbeddedGid(FontProgram p, int cid)
    {
        if (p is SfntFont { Cff: null })
        {
            if (_cidToGid is null)
            {
                return cid;
            }

            long at = 2L * cid;
            return at + 1 < _cidToGid.Length ? (_cidToGid[at] << 8) | _cidToGid[at + 1] : 0;
        }

        return p.GlyphForCid(cid);
    }

    /// <summary>For the Uni*-UCS2/UTF16/UTF32 CMaps the code is the character itself (UTF-16 code units or a UTF-32 value).</summary>
    private static bool TryCodeAsUnicode(uint code, int length, out string text)
    {
        text = string.Empty;
        if (length == 2)
        {
            if (code is >= 0xD800 and <= 0xDFFF)
            {
                return false;
            }

            text = ((char)code).ToString();
            return code != 0;
        }

        if (length == 4)
        {
            char hi = (char)(code >> 16), lo = (char)(code & 0xFFFF);
            if (char.IsHighSurrogate(hi) && char.IsLowSurrogate(lo))
            {
                text = new string([hi, lo]);
                return true;
            }

            if (code is > 0 and <= 0x10FFFF && code is not (>= 0xD800 and <= 0xDFFF))
            {
                text = char.ConvertFromUtf32((int)code);
                return true;
            }
        }

        return false;
    }
}
