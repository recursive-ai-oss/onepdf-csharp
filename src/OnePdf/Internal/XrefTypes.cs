namespace OnePdf.Internal;

/// <summary>One cross-reference entry: type 0 free, 1 in use at a file offset, 2 stored in an object stream.</summary>
/// <param name="Type">The entry type.</param>
/// <param name="A">Type 1: byte offset. Type 2: object number of the object stream. Type 0: next free object.</param>
/// <param name="B">Type 1 and 0: generation. Type 2: index within the object stream.</param>
internal readonly record struct XrefEntry(byte Type, long A, int B);

/// <summary>One cross-reference section: a revision's worth of entries and its trailer.</summary>
internal sealed class XrefSection
{
    public long Offset;
    public Dictionary<int, XrefEntry> Entries = [];
    public PdfDictionary Trailer = null!;
    public bool IsStream;
    public long Prev = -1;
}

/// <summary>Where a stream's stored bytes lie in the file, after any <c>/Length</c> repair.</summary>
internal sealed class StreamRange
{
    public StreamRange(long start, long length)
    {
        Start = start;
        Length = length;
    }

    public long Start { get; }

    public long Length { get; }
}

/// <summary>Cache keys: the document view (which revision's cross-reference data applies), what is cached, and the object number.</summary>
internal static class ObjectKeys
{
    public static long Object(int view, int number) => ((long)view << 32) | (uint)number;

    public static long Decoded(int view, int number) => (1L << 60) | Object(view, number);

    public static long ObjStm(int view, int number) => (2L << 60) | Object(view, number);
}

/// <summary>Resolves references found in containers read under one cross-reference view.</summary>
internal sealed class PdfResolver
{
    public PdfResolver(PdfDocument doc, int view)
    {
        Doc = doc;
        View = view;
    }

    public PdfDocument Doc { get; }

    public int View { get; }

    public PdfObject Resolve(PdfReference r) => Doc.ResolveReference(r, View);
}

/// <summary>A decoded object stream: its bytes and the (number, offset) header.</summary>
internal sealed class ObjStmData
{
    public byte[] Data = [];
    public int[] Numbers = [];
    public int[] Offsets = [];
    public int First;
}

/// <summary>An indirect object read at a file position.</summary>
internal sealed class ParsedObject
{
    public PdfObject Object = PdfNull.Instance;
    public int Number;
    public int Generation;
    public long EndPosition;
    public long Size;
}

/// <summary>Result of scanning a whole file for object headers (the last resort when cross-reference data fails).</summary>
internal sealed class ScanResult
{
    public Dictionary<int, (long Offset, int Generation)> Objects { get; } = [];

    public List<(long Position, int Number)> ObjectStreams { get; } = [];

    public List<PdfDictionary> Trailers { get; } = [];

    public int LastCatalog { get; set; } = -1;
}
