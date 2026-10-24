using System.Globalization;
using System.Text;

namespace OnePdf;

/// <summary>The kind of an artifact: content that is not part of the document's real content (ISO 32000-2 14.8.2.2).</summary>
public enum PdfArtifactType
{
    /// <summary>No <c>/Type</c> entry: an artifact of unspecified kind.</summary>
    None = 0,

    /// <summary>Page-production material: running headers and footers, page numbers, watermarks.</summary>
    Pagination,

    /// <summary>Layout material: rules, borders, backgrounds, decorative shapes.</summary>
    Layout,

    /// <summary>Page material: print marks and the like.</summary>
    Page,

    /// <summary>Background material (PDF 2.0): full-page or section backgrounds.</summary>
    Background,
}

/// <summary>The subtype of a pagination artifact (ISO 32000-2 14.8.2.2.2).</summary>
public enum PdfArtifactSubtype
{
    /// <summary>No <c>/Subtype</c> entry.</summary>
    None = 0,

    /// <summary>A running header.</summary>
    Header,

    /// <summary>A running footer.</summary>
    Footer,

    /// <summary>A watermark.</summary>
    Watermark,
}

public abstract partial class PdfContentBuilder
{
    /// <summary>
    /// The number of marked-content sequences begun on this builder and not yet ended. <see cref="Save"/>/<see cref="Restore"/> pairs and
    /// marked-content sequences must nest properly, so <see cref="Restore"/> throws while a sequence begun after its <see cref="Save"/>
    /// is open. Commit closes whatever is still open.
    /// </summary>
    public int MarkedContentDepth { get; private set; }

    /// <summary>Begins a marked-content sequence with a tag and no properties (<c>BMC</c>, ISO 32000-2 14.6.2).</summary>
    /// <param name="tag">The tag, written as a PDF name (any characters are allowed; delimiters and non-ASCII are escaped).</param>
    /// <exception cref="ArgumentException">The tag is empty.</exception>
    public void BeginMarkedContent(string tag)
    {
        CheckOpen();
        EmitMark(Name(tag) + " BMC\n");
    }

    /// <summary>Begins a marked-content sequence whose property list is <c>&lt;&lt; /MCID <paramref name="mcid"/> &gt;&gt;</c> (<c>BDC</c>, ISO 32000-2 14.7.4.4): the content item that a structure element refers to.</summary>
    /// <param name="tag">The tag, normally the structure type of the element.</param>
    /// <param name="mcid">The marked-content identifier, unique on the page. Not negative.</param>
    public void BeginMarkedContent(string tag, int mcid)
    {
        CheckOpen();
        ArgumentOutOfRangeException.ThrowIfNegative(mcid);
        EmitMark(Name(tag) + " <</MCID " + mcid.ToString(CultureInfo.InvariantCulture) + ">> BDC\n");
    }

    /// <summary>
    /// Begins a <c>/Span</c> marked-content sequence with optional <c>/ActualText</c>, <c>/Alt</c>, <c>/Lang</c> and <c>/E</c> entries
    /// (ISO 32000-2 14.9.4, 14.9.3, 14.9.2, 14.9.5). Text extraction returns <paramref name="actualText"/> in place of the text drawn
    /// inside the sequence.
    /// </summary>
    /// <param name="actualText">The text the content stands for, or null for none. An empty string means "no text".</param>
    /// <param name="alternateText">Alternate description, or null.</param>
    /// <param name="language">A language tag such as <c>en-US</c>, or null.</param>
    /// <param name="expansion">The expansion of an abbreviation, or null.</param>
    public void BeginSpan(string? actualText = null, string? alternateText = null, string? language = null, string? expansion = null)
    {
        CheckOpen();
        var sb = new StringBuilder("/Span <<");
        AppendTextEntry(sb, "ActualText", actualText);
        AppendTextEntry(sb, "Alt", alternateText);
        AppendTextEntry(sb, "E", expansion);
        if (language is not null)
        {
            sb.Append("/Lang ");
            AppendTextString(sb, language);
        }

        sb.Append(">> BDC\n");
        EmitMark(sb.ToString());
    }

    /// <summary>
    /// Begins an artifact (ISO 32000-2 14.8.2.2): content a reader of a tagged document should skip, such as page numbers, rules and
    /// backgrounds. Without arguments it writes <c>/Artifact BMC</c>.
    /// </summary>
    /// <param name="type">The artifact kind.</param>
    /// <param name="subtype">The pagination subtype; only meaningful with <see cref="PdfArtifactType.Pagination"/>.</param>
    /// <param name="boundingBox">The artifact's bounding box in default user space, or null.</param>
    public void BeginArtifact(PdfArtifactType type = PdfArtifactType.None, PdfArtifactSubtype subtype = PdfArtifactSubtype.None, PdfRectangle? boundingBox = null)
    {
        CheckOpen();
        if (type == PdfArtifactType.None && subtype == PdfArtifactSubtype.None && boundingBox is null)
        {
            EmitMark("/Artifact BMC\n");
            return;
        }

        var sb = new StringBuilder("/Artifact <<");
        if (type != PdfArtifactType.None)
        {
            sb.Append("/Type /").Append(type.ToString());
        }

        if (subtype != PdfArtifactSubtype.None)
        {
            sb.Append("/Subtype /").Append(subtype.ToString());
        }

        if (boundingBox is { } b)
        {
            sb.Append("/BBox [").Append(Num(b.Left)).Append(' ').Append(Num(b.Bottom)).Append(' ').Append(Num(b.Right)).Append(' ').Append(Num(b.Top)).Append(']');
        }

        sb.Append(">> BDC\n");
        EmitMark(sb.ToString());
    }

    /// <summary>Ends the innermost marked-content sequence or artifact (<c>EMC</c>).</summary>
    /// <exception cref="InvalidOperationException">No sequence is open, or a <see cref="Save"/> made after it is still open (they must nest).</exception>
    public void EndMarkedContent()
    {
        CheckOpen();
        if (_open.Count == 0 || _open[^1] != 'M')
        {
            throw new InvalidOperationException(MarkedContentDepth == 0
                ? "EndMarkedContent without a matching begin."
                : "EndMarkedContent while a graphics state saved inside the sequence is still open; call Restore first.");
        }

        _open.RemoveAt(_open.Count - 1);
        MarkedContentDepth--;
        Emit("EMC\n");
    }

    private void EmitMark(string text)
    {
        if (_open.Count >= MaxDepth * 2)
        {
            throw new InvalidOperationException("Marked content is nested too deeply.");
        }

        _open.Add('M');
        MarkedContentDepth++;
        Emit(text);
    }

    private static void AppendTextEntry(StringBuilder sb, string key, string? value)
    {
        if (value is null)
        {
            return;
        }

        sb.Append('/').Append(key).Append(' ');
        AppendTextString(sb, value);
    }

    // A PDF text string (ISO 32000-2 7.9.2.2) as a hexadecimal string.
    internal static void AppendTextString(StringBuilder sb, string value)
    {
        sb.Append('<');
        foreach (byte b in PdfString.FromText(value).Bytes.Span)
        {
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        sb.Append('>');
    }

    private static string Name(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        if (tag.Length == 0)
        {
            throw new ArgumentException("A tag must not be empty.", nameof(tag));
        }

        var sb = new StringBuilder("/");
        foreach (byte b in Encoding.UTF8.GetBytes(tag))
        {
            // ISO 32000-2 7.3.5: regular characters other than '#' stand for themselves; everything else is written as #xx.
            if (b is >= 0x21 and < 0x7F && b is not ((byte)'#' or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%'))
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append('#').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }
}
