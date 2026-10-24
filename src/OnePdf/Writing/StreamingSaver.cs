using System.Security.Cryptography;
using OnePdf.Internal;

namespace OnePdf.Writing;

/// <summary>
/// The output side of <see cref="PdfDocumentWriter"/>: writes objects one at a time as they become final, remembers their offsets
/// (eight bytes per object), and closes the file with a cross-reference section. Nothing but the offsets is kept.
/// </summary>
internal sealed class StreamingSaver : SaverBase
{
    private long[] _offsets = new long[64];
    private int _highest;

    public StreamingSaver(PdfDocument workspace, PdfSaveOptions options, Stream output, string version)
        : base(workspace, options, CancellationToken.None)
    {
        Sink = new ByteSink(output);
        Writer = new ObjectWriter(workspace, IdentityMapper.Instance, options);
        Sink.Ascii("%PDF-" + version + "\n%");
        Sink.Write([0xE2, 0xE3, 0xCF, 0xD3]);
        Sink.Byte((byte)'\n');
    }

    /// <summary>Bytes written so far.</summary>
    public long Position => Sink.Position;

    public bool IsWritten(int number) => number < _offsets.Length && _offsets[number] > 0;

    public void Write(int number, PdfObject value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);

        if (number >= _offsets.Length)
        {
            Array.Resize(ref _offsets, Math.Max(number + 1, _offsets.Length * 2));
        }

        if (_offsets[number] > 0)
        {
            throw new InvalidOperationException($"Object {number} was already written.");
        }

        _offsets[number] = Sink.Position;
        _highest = Math.Max(_highest, number);
        Writer.WriteIndirect(Sink, number, 0, value, null);
    }

    public void Finish(PdfReference root, PdfReference? info, byte[]? documentId, bool xrefStream)
    {
        byte[] id = documentId ?? RandomNumberGenerator.GetBytes(16);
        var trailer = new PdfDictionary(null);
        trailer.Set("Root", root);
        if (info is not null)
        {
            trailer.Set("Info", info);
        }

        trailer.Set("ID", MakeId(id, id));

        var entries = new List<(int Num, XrefEntry E)>(_highest + 2) { (0, new XrefEntry(0, 0, 65535)) };
        for (int n = 1; n <= _highest; n++)
        {
            if (_offsets[n] > 0)
            {
                entries.Add((n, new XrefEntry(1, _offsets[n], 0)));
            }
        }

        int xrefNumber = _highest + 1;
        WriteXref(entries, xrefStream ? xrefNumber + 1 : xrefNumber, trailer, xrefStream, xrefNumber, 0);
    }
}
