using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace OnePdf.Text;

/// <summary>Options for <see cref="PdfExtractionJson"/>.</summary>
public sealed class PdfExtractionJsonOptions
{
    /// <summary>Write the per-glyph array of every page. Default true. Words refer to glyphs by index into it.</summary>
    public bool IncludeGlyphs { get; set; } = true;

    /// <summary>Indent the output. Default false.</summary>
    public bool Indented { get; set; }
}

/// <summary>
/// Writes extraction results in the versioned JSON form described in <c>docs/extraction-json.md</c> (format <c>onepdf.extraction</c>,
/// version <see cref="Version"/>). Numbers are rounded to three decimals and written in invariant form, so the same input gives the same bytes on
/// every platform.
/// </summary>
public static class PdfExtractionJson
{
    /// <summary>The version number written into the <c>version</c> member.</summary>
    public const int Version = 1;

    /// <summary>The value of the <c>format</c> member.</summary>
    public const string Format = "onepdf.extraction";

    // Property names are encoded once. They are plain ASCII, so the bytes are what writing the string would give.
    private static readonly JsonEncodedText NFormat = JsonEncodedText.Encode("format"), NVersion = JsonEncodedText.Encode("version"),
        NPageCount = JsonEncodedText.Encode("pageCount"), NPages = JsonEncodedText.Encode("pages"), NIndex = JsonEncodedText.Encode("index"),
        NWidth = JsonEncodedText.Encode("width"), NHeight = JsonEncodedText.Encode("height"), NUsedTree = JsonEncodedText.Encode("usedStructureTree"),
        NDiagnostics = JsonEncodedText.Encode("diagnostics"), NText = JsonEncodedText.Encode("text"), NBlocks = JsonEncodedText.Encode("blocks"),
        NRole = JsonEncodedText.Encode("role"), NTable = JsonEncodedText.Encode("table"), NBounds = JsonEncodedText.Encode("bounds"),
        NLines = JsonEncodedText.Encode("lines"), NDirection = JsonEncodedText.Encode("direction"), NVertical = JsonEncodedText.Encode("vertical"),
        NCells = JsonEncodedText.Encode("cells"), NWords = JsonEncodedText.Encode("words"), NVisible = JsonEncodedText.Encode("visible"),
        NGlyphs = JsonEncodedText.Encode("glyphs"), NFont = JsonEncodedText.Encode("font"), NFontId = JsonEncodedText.Encode("fontId"),
        NSize = JsonEncodedText.Encode("size"), NMatrix = JsonEncodedText.Encode("matrix"), NAdvance = JsonEncodedText.Encode("advance"),
        NColor = JsonEncodedText.Encode("color"), NRenderMode = JsonEncodedText.Encode("renderMode"), NMcid = JsonEncodedText.Encode("mcid");

    private static readonly JsonEncodedText VFormat = JsonEncodedText.Encode(Format), VHeader = JsonEncodedText.Encode("header"),
        VFooter = JsonEncodedText.Encode("footer"), VBody = JsonEncodedText.Encode("body"), VRtl = JsonEncodedText.Encode("rtl"), VLtr = JsonEncodedText.Encode("ltr");

    /// <summary>Serializes pages to a JSON string.</summary>
    /// <param name="pages">The pages, normally in page order.</param>
    /// <param name="options">Output options; null for the defaults.</param>
    public static string ToJson(IReadOnlyList<PdfPageText> pages, PdfExtractionJsonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pages);
        using PooledBytes buffer = new();
        WriteTo(buffer, pages, options ?? new PdfExtractionJsonOptions());
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Serializes pages as UTF-8 JSON to a stream (the stream stays open). The writer hands the bytes over page by page.</summary>
    /// <param name="output">The destination.</param>
    /// <param name="pages">The pages, normally in page order.</param>
    /// <param name="options">Output options; null for the defaults.</param>
    public static void Write(Stream output, IReadOnlyList<PdfPageText> pages, PdfExtractionJsonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(pages);
        using PooledBytes buffer = new();
        WriteTo(buffer, pages, options ?? new PdfExtractionJsonOptions(), output);
    }

    private static void WriteTo(PooledBytes buffer, IReadOnlyList<PdfPageText> pages, PdfExtractionJsonOptions options, Stream? stream = null)
    {
        JavaScriptEncoder encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        using Utf8JsonWriter w = new(buffer, new JsonWriterOptions { Indented = options.Indented, Encoder = encoder });
        w.WriteStartObject();
        w.WriteString(NFormat, VFormat);
        w.WriteNumber(NVersion, Version);
        w.WriteNumber(NPageCount, pages.Count);
        w.WriteStartArray(NPages);
        foreach (PdfPageText p in pages)
        {
            WritePage(w, p, options, encoder);
            if (stream is not null)
            {
                w.Flush();
                stream.Write(buffer.WrittenSpan);
                buffer.Clear();
            }
        }

        w.WriteEndArray();
        w.WriteEndObject();
        w.Flush();
        if (stream is not null)
        {
            stream.Write(buffer.WrittenSpan);
            buffer.Clear();
        }
    }

    /// <summary>A growing byte buffer from the shared pool, written by <see cref="Utf8JsonWriter"/>.</summary>
    private sealed class PooledBytes : IBufferWriter<byte>, IDisposable
    {
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        private int _count;

        internal ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _count);

        internal void Clear() => _count = 0;

        public void Advance(int count) => _count += count;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Grow(sizeHint);
            return _buffer.AsMemory(_count);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Grow(sizeHint);
            return _buffer.AsSpan(_count);
        }

        private void Grow(int sizeHint)
        {
            int need = checked(_count + Math.Max(sizeHint, 1));
            if (need <= _buffer.Length)
            {
                return;
            }

            byte[] bigger = ArrayPool<byte>.Shared.Rent(Math.Max(need, checked(_buffer.Length * 2)));
            Buffer.BlockCopy(_buffer, 0, bigger, 0, _count);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = bigger;
        }

        public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer);
    }

    /// <summary>
    /// Writes <paramref name="v"/> rounded to three decimals (half to even) in the form <c>"0.###"</c> gives in the invariant culture:
    /// at most three decimals, no trailing zeros, "0" for zero and for non-finite values. Returns the byte count, or -1 when the 64-byte <paramref name="dst"/> is too small (only for magnitudes beyond about 1e60).
    /// </summary>
    internal static int FormatNum(double v, Span<byte> dst)
    {
        if (!double.IsFinite(v))
        {
            dst[0] = (byte)'0';
            return 1;
        }

        double r = Math.Round(v, 3, MidpointRounding.ToEven);
        if (r == 0)
        {
            dst[0] = (byte)'0';
            return 1;
        }

        if (Math.Abs(r) < 1e12)
        {
            // r is the double nearest to k / 1000, so k is what the format would print; no format-string parsing needed.
            long k = (long)Math.Round(r * 1000.0);
            return Finish(dst, k, (ulong)Math.Abs(k));
        }

        if (r.TryFormat(dst, out int n, "0.###", CultureInfo.InvariantCulture))
        {
            return n;
        }

        return -1; // magnitudes beyond about 1e60 need more room than the buffer has
    }

    private static int Finish(Span<byte> dst, long k, ulong a)
    {
        int pos = 0;
        if (k < 0)
        {
            dst[pos++] = (byte)'-';
        }

        (ulong whole, ulong frac) = (a / 1000, a % 1000);
        whole.TryFormat(dst.Slice(pos), out int w, default, CultureInfo.InvariantCulture);
        pos += w;
        if (frac != 0)
        {
            dst[pos++] = (byte)'.';
            dst[pos++] = (byte)('0' + frac / 100);
            ulong rest = frac % 100;
            if (rest != 0)
            {
                dst[pos++] = (byte)('0' + rest / 10);
                if (rest % 10 != 0)
                {
                    dst[pos++] = (byte)('0' + rest % 10);
                }
            }
        }

        return pos;
    }

    private static void RawNum(Utf8JsonWriter w, double v)
    {
        Span<byte> tmp = stackalloc byte[64];
        int n = FormatNum(v, tmp);
        if (n < 0)
        {
            w.WriteRawValue(Math.Round(v, 3, MidpointRounding.ToEven).ToString("0.###", CultureInfo.InvariantCulture), skipInputValidation: true);
            return;
        }

        w.WriteRawValue(tmp.Slice(0, n), skipInputValidation: true);
    }

    private static void NumProp(Utf8JsonWriter w, JsonEncodedText name, double v)
    {
        w.WritePropertyName(name);
        RawNum(w, v);
    }

    private static void Box(Utf8JsonWriter w, JsonEncodedText name, PdfRectangle b)
    {
        w.WriteStartArray(name);
        RawNum(w, b.Left);
        RawNum(w, b.Bottom);
        RawNum(w, b.Right);
        RawNum(w, b.Top);
        w.WriteEndArray();
    }

    private static void WritePage(Utf8JsonWriter w, PdfPageText p, PdfExtractionJsonOptions o, JavaScriptEncoder encoder)
    {
        w.WriteStartObject();
        w.WriteNumber(NIndex, p.PageIndex);
        NumProp(w, NWidth, p.Width);
        NumProp(w, NHeight, p.Height);
        w.WriteBoolean(NUsedTree, p.UsedStructureTree);
        w.WriteStartArray(NDiagnostics);
        foreach (PdfDiagnostic d in p.Diagnostics)
        {
            w.WriteStringValue(d.Message);
        }

        w.WriteEndArray();
        w.WriteString(NText, p.GetText(PdfTextMode.ReadingOrder));
        w.WriteStartArray(NBlocks);
        foreach (PdfTextBlock b in p.Blocks)
        {
            w.WriteStartObject();
            w.WriteString(NRole, b.Role switch { PdfTextRole.Header => VHeader, PdfTextRole.Footer => VFooter, _ => VBody });
            w.WriteBoolean(NTable, b.IsTable);
            Box(w, NBounds, b.Bounds);
            w.WriteStartArray(NLines);
            foreach (PdfTextLine l in b.Lines)
            {
                w.WriteStartObject();
                w.WriteString(NDirection, l.Direction == PdfTextDirection.RightToLeft ? VRtl : VLtr);
                w.WriteBoolean(NVertical, l.IsVertical);
                Box(w, NBounds, l.Bounds);
                w.WriteStartArray(NCells);
                foreach (int c in l.CellStarts)
                {
                    w.WriteNumberValue(c);
                }

                w.WriteEndArray();
                w.WriteString(NText, l.Text);
                w.WriteStartArray(NWords);
                foreach (PdfTextWord word in l.Words)
                {
                    w.WriteStartObject();
                    w.WriteString(NText, word.Text);
                    Box(w, NBounds, word.Bounds);
                    w.WriteBoolean(NVisible, word.IsVisible);
                    w.WriteStartArray(NGlyphs);
                    foreach (int gi in word.GlyphIndices)
                    {
                        w.WriteNumberValue(gi);
                    }

                    w.WriteEndArray();
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteEndArray();

        if (o.IncludeGlyphs)
        {
            w.WriteStartArray(NGlyphs);
            PdfTextGlyph[] glyphs = p.GlyphArray;
            string? lastFont = null;
            JsonEncodedText lastFontEncoded = default;
            Span<char> color = stackalloc char[7];
            for (int gi = 0; gi < glyphs.Length; gi++)
            {
                ref readonly PdfTextGlyph g = ref glyphs[gi];
                w.WriteStartObject();
                w.WriteString(NText, g.Text);
                if (g.FontName is null)
                {
                    w.WriteNull(NFont);
                }
                else
                {
                    // Glyphs of one font share the name string, so the encoded name is reused.
                    if (!ReferenceEquals(g.FontName, lastFont))
                    {
                        lastFont = g.FontName;
                        lastFontEncoded = JsonEncodedText.Encode(GlyphPrep.Sanitize(g.FontName), encoder);
                    }

                    w.WriteString(NFont, lastFontEncoded);
                }

                w.WriteNumber(NFontId, g.FontId);
                NumProp(w, NSize, g.Size);
                w.WriteStartArray(NMatrix);
                PdfTextMatrix m = g.Matrix;
                RawNum(w, m.A);
                RawNum(w, m.B);
                RawNum(w, m.C);
                RawNum(w, m.D);
                RawNum(w, m.E);
                RawNum(w, m.F);
                w.WriteEndArray();
                NumProp(w, NAdvance, g.Advance);
                Box(w, NBounds, g.Bounds);
                w.WriteBoolean(NVertical, g.IsVertical);
                if (g.FillColor < 0)
                {
                    w.WriteNull(NColor);
                }
                else
                {
                    color[0] = '#';
                    ((uint)(g.FillColor & 0xFFFFFF)).TryFormat(color.Slice(1), out _, "x6", CultureInfo.InvariantCulture);
                    w.WriteString(NColor, color);
                }

                w.WriteNumber(NRenderMode, g.RenderMode);
                w.WriteBoolean(NVisible, g.IsVisible);
                w.WriteNumber(NMcid, g.MarkedContentId);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        w.WriteEndObject();
    }
}
