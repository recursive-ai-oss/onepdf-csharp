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

    /// <summary>Serializes pages to a JSON string.</summary>
    /// <param name="pages">The pages, normally in page order.</param>
    /// <param name="options">Output options; null for the defaults.</param>
    public static string ToJson(IReadOnlyList<PdfPageText> pages, PdfExtractionJsonOptions? options = null)
    {
        using MemoryStream ms = new();
        Write(ms, pages, options);
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>Serializes pages as UTF-8 JSON to a stream (the stream stays open).</summary>
    /// <param name="output">The destination.</param>
    /// <param name="pages">The pages, normally in page order.</param>
    /// <param name="options">Output options; null for the defaults.</param>
    public static void Write(Stream output, IReadOnlyList<PdfPageText> pages, PdfExtractionJsonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(pages);
        options ??= new PdfExtractionJsonOptions();
        using Utf8JsonWriter w = new(output, new JsonWriterOptions { Indented = options.Indented, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        w.WriteStartObject();
        w.WriteString("format", Format);
        w.WriteNumber("version", Version);
        w.WriteNumber("pageCount", pages.Count);
        w.WriteStartArray("pages");
        foreach (PdfPageText p in pages)
        {
            WritePage(w, p, options);
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static string Num(double v)
    {
        if (!double.IsFinite(v))
        {
            return "0";
        }

        double r = Math.Round(v, 3, MidpointRounding.ToEven);
        return r == 0 ? "0" : r.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static void NumProp(Utf8JsonWriter w, string name, double v)
    {
        w.WritePropertyName(name);
        w.WriteRawValue(Num(v), skipInputValidation: true);
    }

    private static void Box(Utf8JsonWriter w, string name, PdfRectangle b)
    {
        w.WriteStartArray(name);
        foreach (double v in (ReadOnlySpan<double>)[b.Left, b.Bottom, b.Right, b.Top])
        {
            w.WriteRawValue(Num(v), skipInputValidation: true);
        }

        w.WriteEndArray();
    }

    private static void WritePage(Utf8JsonWriter w, PdfPageText p, PdfExtractionJsonOptions o)
    {
        w.WriteStartObject();
        w.WriteNumber("index", p.PageIndex);
        NumProp(w, "width", p.Width);
        NumProp(w, "height", p.Height);
        w.WriteBoolean("usedStructureTree", p.UsedStructureTree);
        w.WriteStartArray("diagnostics");
        foreach (PdfDiagnostic d in p.Diagnostics)
        {
            w.WriteStringValue(d.Message);
        }

        w.WriteEndArray();
        w.WriteString("text", p.GetText(PdfTextMode.ReadingOrder));
        w.WriteStartArray("blocks");
        foreach (PdfTextBlock b in p.Blocks)
        {
            w.WriteStartObject();
            w.WriteString("role", b.Role switch { PdfTextRole.Header => "header", PdfTextRole.Footer => "footer", _ => "body" });
            w.WriteBoolean("table", b.IsTable);
            Box(w, "bounds", b.Bounds);
            w.WriteStartArray("lines");
            foreach (PdfTextLine l in b.Lines)
            {
                w.WriteStartObject();
                w.WriteString("direction", l.Direction == PdfTextDirection.RightToLeft ? "rtl" : "ltr");
                w.WriteBoolean("vertical", l.IsVertical);
                Box(w, "bounds", l.Bounds);
                w.WriteStartArray("cells");
                foreach (int c in l.CellStarts)
                {
                    w.WriteNumberValue(c);
                }

                w.WriteEndArray();
                w.WriteString("text", l.Text);
                w.WriteStartArray("words");
                foreach (PdfTextWord word in l.Words)
                {
                    w.WriteStartObject();
                    w.WriteString("text", word.Text);
                    Box(w, "bounds", word.Bounds);
                    w.WriteBoolean("visible", word.IsVisible);
                    w.WriteStartArray("glyphs");
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
            w.WriteStartArray("glyphs");
            foreach (PdfTextGlyph g in p.Glyphs)
            {
                w.WriteStartObject();
                w.WriteString("text", g.Text);
                if (g.FontName is null)
                {
                    w.WriteNull("font");
                }
                else
                {
                    w.WriteString("font", GlyphPrep.Sanitize(g.FontName));
                }

                w.WriteNumber("fontId", g.FontId);
                NumProp(w, "size", g.Size);
                w.WriteStartArray("matrix");
                PdfTextMatrix m = g.Matrix;
                foreach (double v in (ReadOnlySpan<double>)[m.A, m.B, m.C, m.D, m.E, m.F])
                {
                    w.WriteRawValue(Num(v), skipInputValidation: true);
                }

                w.WriteEndArray();
                NumProp(w, "advance", g.Advance);
                Box(w, "bounds", g.Bounds);
                w.WriteBoolean("vertical", g.IsVertical);
                if (g.FillColor < 0)
                {
                    w.WriteNull("color");
                }
                else
                {
                    w.WriteString("color", "#" + (g.FillColor & 0xFFFFFF).ToString("x6", CultureInfo.InvariantCulture));
                }

                w.WriteNumber("renderMode", g.RenderMode);
                w.WriteBoolean("visible", g.IsVisible);
                w.WriteNumber("mcid", g.MarkedContentId);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        w.WriteEndObject();
    }
}
