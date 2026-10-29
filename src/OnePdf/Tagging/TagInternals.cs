using System.Globalization;
using OnePdf.Content;
using OnePdf.Redaction;

namespace OnePdf.Tagging;

/// <summary>One thing a plan can give a role to: a content reference, an explicit artifact, or an annotation.</summary>
internal sealed class TagSlot
{
    public int Id;

    /// <summary>The element the slot is content of; null for an artifact.</summary>
    public PdfTagElement? Element;

    /// <summary>True for an artifact (explicit or suggested).</summary>
    public bool Artifact;

    public PdfTagContent Content = null!;

    public PdfArtifactKind Kind;

    /// <summary>True for an artifact the plan only suggested (headers and footers); an element's claim on the same content wins.</summary>
    public bool Soft;

    public int PageIndex = -1;

    /// <summary>The node of the element the slot belongs to (0 is the document element).</summary>
    public int NodeIndex;

    /// <summary>The marked-content tag written for the slot: the element's structure type.</summary>
    public string Tag = "Span";

    /// <summary>The marked-content sequences of the slot, in content order, once the pages are done.</summary>
    public List<ContentItem> Items = [];

    public bool IsArtifact => Artifact;

    public bool IsGraphics => Content.Kind == PdfTagContent.Kinds.Graphics;
}

/// <summary>A marked-content sequence of a slot: where it is, and where it sits in the reading of the page.</summary>
internal readonly record struct ContentItem(McScope Scope, int Mcid, int[] Order);

/// <summary>
/// The marked-content identifiers of one content stream that is a unit for the parent tree: a page's content, or a copy of a form
/// XObject that holds content of several elements. Identifiers count from 0 inside a scope (ISO 32000-2 14.7.4.2).
/// </summary>
internal sealed class McScope
{
    public int PageIndex;

    /// <summary>The form copy this scope belongs to; null for the page's content.</summary>
    public FormCopy? Copy;

    /// <summary>The slot of each identifier.</summary>
    public readonly List<int> Slots = [];

    /// <summary>The offset in the stream of the operator each identifier was inserted at, for ordering.</summary>
    public readonly List<int> Positions = [];

    /// <summary>The piece of the page's content each identifier was inserted in (0 for a form copy).</summary>
    public readonly List<int> Pieces = [];

    /// <summary>Where the stream sits in the reading of the page: empty for the first piece of the page's content, else the pieces and form uses leading to it.</summary>
    public int[] Prefix = [];

    public int ArtifactSequences;

    public int AddedOperators;

    public int StructParents = -1;

    public int Allocate(int slot, int position, int piece)
    {
        Slots.Add(slot);
        Positions.Add(position);
        Pieces.Add(piece);
        return Slots.Count - 1;
    }
}

/// <summary>A change to a content stream: the bytes in [Start, End) become Bytes (an insertion when Start equals End).</summary>
internal readonly record struct TagEdit(int Start, int End, byte[] Bytes, int Seq);

/// <summary>A form XObject copied for one use because its content belongs to more than one element (or to an element and to artifacts).</summary>
internal sealed class FormCopy
{
    public PdfStream Original = null!;
    public ReadOnlyMemory<byte> Data;
    public List<TagEdit> Edits = [];
    public McScope Scope = null!;
    public ResourceEdit Resources = null!;
    public ResourceEdit ParentResources = null!;
    public string NameInParent = string.Empty;
    public int PageIndex;
    public PdfReference? Reference;
    public PdfDictionary? Dictionary;

    /// <summary>The stream whose <c>Do</c> this copy replaces, and where that operator starts in it.</summary>
    public TagCtx ParentCtx = null!;
    public int DoPosition;
}

/// <summary>What one page produced.</summary>
internal sealed class PageOutcome
{
    public int PageIndex;
    public List<byte[]>? Streams;
    public ResourceEdit Resources = null!;
    public McScope Scope = null!;
    public List<McScope> PieceScopes = [];
    public List<FormCopy> Copies = [];
    public long ParsedOperators;
    public bool AnyEdit;
}

internal static class TagText
{
    private static readonly byte[] s_emc = "\nEMC\n"u8.ToArray();

    public static byte[] Emc => s_emc;

    public static byte[] Ascii(string s)
    {
        byte[] b = new byte[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            b[i] = (byte)s[i];
        }

        return b;
    }

    public static byte[] Mcid(string tag, int mcid) => Ascii("\n/" + tag + " <</MCID " + mcid.ToString(CultureInfo.InvariantCulture) + ">> BDC\n");

    public static byte[] Artifact(PdfArtifactKind kind) => kind switch
    {
        PdfArtifactKind.Header => Ascii("\n/Artifact <</Type /Pagination /Subtype /Header>> BDC\n"),
        PdfArtifactKind.Footer => Ascii("\n/Artifact <</Type /Pagination /Subtype /Footer>> BDC\n"),
        PdfArtifactKind.Watermark => Ascii("\n/Artifact <</Type /Pagination /Subtype /Watermark>> BDC\n"),
        PdfArtifactKind.Layout => Ascii("\n/Artifact <</Type /Layout>> BDC\n"),
        PdfArtifactKind.Page => Ascii("\n/Artifact <</Type /Page>> BDC\n"),
        _ => Ascii("\n/Artifact BMC\n"),
    };

    /// <summary>The structure type name of a role, as written in the file.</summary>
    public static string RoleName(PdfTagRole role) => s_roleNames[(int)role];

    private static readonly string[] s_roleNames =
    [
        "P", "H", "H1", "H2", "H3", "H4", "H5", "H6", "L", "LI", "Lbl", "LBody", "Table", "THead", "TBody", "TFoot", "TR", "TH", "TD",
        "Caption", "Figure", "Formula", "Link", "Span", "Div", "Part", "Art", "Sect", "BlockQuote", "Quote", "Note", "Reference", "Code",
        "Form", "TOC", "TOCI", "Annot",
    ];

    public static void WriteString(OutBuf o, List<byte> s)
    {
        o.Add((byte)'(');
        foreach (byte b in s)
        {
            if (b is (byte)'(' or (byte)')' or (byte)'\\')
            {
                o.Add((byte)'\\');
                o.Add(b);
            }
            else if (b is < 32 or > 126)
            {
                o.Add((byte)'\\');
                o.Add((byte)('0' + ((b >> 6) & 7)));
                o.Add((byte)('0' + ((b >> 3) & 7)));
                o.Add((byte)('0' + (b & 7)));
            }
            else
            {
                o.Add(b);
            }
        }

        o.Add((byte)')');
    }

    /// <summary>Applies edits to <paramref name="data"/>: the edits sorted by position (equal positions in the order made), the rest copied.</summary>
    public static byte[] Apply(ReadOnlyMemory<byte> data, List<TagEdit> edits, long limit)
    {
        TagEdit[] sorted = [.. edits];
        Array.Sort(sorted, static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Seq.CompareTo(b.Seq));
        var o = new OutBuf(limit);
        ReadOnlySpan<byte> src = data.Span;
        int pos = 0;
        foreach (TagEdit e in sorted)
        {
            if (e.Start < pos || e.End < e.Start || e.End > src.Length)
            {
                throw new PdfTaggingException(PdfTaggingFailure.ContentUnreadable, "Two changes to a content stream overlap; the page cannot be tagged safely.");
            }

            o.Add(src[pos..e.Start]);
            o.Add(e.Bytes);
            pos = e.End;
        }

        o.Add(src[pos..]);
        return o.ToArray();
    }
}
