using System.Security.Cryptography;
using OnePdf.Internal;
using OnePdf.Security;

namespace OnePdf.Writing;

/// <summary>
/// An incremental update (ISO 32000-2 7.5.6): the original bytes are copied unchanged, then the objects that changed, a
/// new cross-reference section that lists only them, and a trailer whose /Prev points at the previous section. Object numbers
/// and generations are kept, so every reference in the untouched objects stays valid.
/// </summary>
internal sealed class IncrementalSaver : SaverBase
{
    public IncrementalSaver(PdfDocument doc, PdfSaveOptions options, CancellationToken ct)
        : base(doc, options, ct)
    {
    }

    public void Run(Stream output, IPdfSource source, long bias, int[] dirty, IncrementalContext lookup)
    {
        PdfRevision newest = Doc.Revisions[^1];
        bool stream = newest.IsXrefStream;
        SecurityDecryptor? enc = Doc.Decryptor;

        // Everything the document held at open stays as it was, byte for byte.
        Sink = new ByteSink(output);
        byte[] buf = new byte[1 << 16];
        long length = source.Length;
        for (long pos = 0; pos < length;)
        {
            Ct.ThrowIfCancellationRequested();
            int want = (int)Math.Min(buf.Length, length - pos);
            if (source.Read(pos, buf.AsSpan(0, want)) != want)
            {
                throw new PdfSourceChangedException("The source returned fewer bytes than it had at open.");
            }

            Sink.Write(buf.AsSpan(0, want));
            pos += want;
        }

        source.EnsureUnchanged();
        if (length == 0 || lookup.LastByte != (byte)'\n' && lookup.LastByte != (byte)'\r')
        {
            Sink.Byte((byte)'\n');
        }

        Writer = new ObjectWriter(Doc, IdentityMapper.Instance, Options);
        var entries = new List<(int Num, XrefEntry E)>(dirty.Length + 2);
        var freed = new List<int>();
        foreach (int n in dirty)
        {
            Ct.ThrowIfCancellationRequested();
            PdfObject obj = lookup.Get(n);
            if (obj is PdfNull)
            {
                freed.Add(n);
                continue;
            }

            int gen = obj.Reference?.Generation ?? 0;
            entries.Add((n, new XrefEntry(1, Sink.Position - bias, gen)));
            Writer.WriteIndirect(Sink, n, gen, obj, enc);
        }

        if (freed.Count > 0)
        {
            // The new free objects chain onto the free list the file already had (7.5.4: object 0 heads it).
            long oldHead = lookup.FreeHead;
            for (int i = 0; i < freed.Count; i++)
            {
                int gen = Math.Min(65535, lookup.GenerationOf(freed[i]) + 1);
                long next = i + 1 < freed.Count ? freed[i + 1] : oldHead;
                entries.Add((freed[i], new XrefEntry(0, next, gen)));
            }

            entries.Add((0, new XrefEntry(0, freed[0], 65535)));
            entries.Sort(static (a, b) => a.Num.CompareTo(b.Num));
        }

        int size = Math.Max(lookup.Size, dirty.Length > 0 ? dirty[^1] + 1 : 0);
        PdfDictionary old = Doc.Trailer;
        var core = new PdfDictionary(null);
        foreach (string key in (ReadOnlySpan<string>)["Root", "Info", "Encrypt"])
        {
            if (old.GetRaw(key) is { } v)
            {
                core.Set(key, v);
            }
        }

        // The first /ID string names the document and stays; the second says this revision is a different file.
        (byte[] First, byte[] Second)? id = ReadId(Doc);
        byte[] first = id?.First ?? RandomNumberGenerator.GetBytes(16);
        core.Set("ID", MakeId(first, RandomNumberGenerator.GetBytes(16)));
        core.Set("Prev", new PdfInteger(newest.XrefOffset - bias));

        int xrefNumber = size;
        WriteXref(entries, stream ? size + 1 : size, core, stream, xrefNumber, bias);
    }
}

/// <summary>What an incremental save needs to know about the document's objects, without exposing document internals to the saver.</summary>
internal sealed class IncrementalContext
{
    public required Func<int, PdfObject> Get { get; init; }

    public required Func<int, int> GenerationOf { get; init; }

    public required long FreeHead { get; init; }

    public required int Size { get; init; }

    public required byte LastByte { get; init; }
}
