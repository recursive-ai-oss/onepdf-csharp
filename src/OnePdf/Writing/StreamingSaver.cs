using System.Security.Cryptography;
using System.Text;
using OnePdf.Internal;

namespace OnePdf.Writing;

/// <summary>
/// The output side of <see cref="PdfDocumentWriter"/>: writes objects one at a time as they become final, remembers their offsets
/// (eight bytes per object), and closes the file with a cross-reference section. Nothing but the offsets is kept.
/// With object streams on, dictionaries and arrays are collected in batches and written as compressed object streams (ISO 32000-2
/// 7.5.7); streams and the catalog stay plain objects. A page of tagged text has hundreds of tiny structure dictionaries, which
/// compress to a fraction of their size this way.
/// </summary>
internal sealed class StreamingSaver : SaverBase, IDisposable
{
    private const int ObjectsPerStream = 200;

    private long[] _offsets = new long[64];
    private int _highest;
    private readonly bool _objectStreams;
    private MemoryStream? _osBody;
    private ByteSink? _osSink;
    private readonly List<int> _osNumbers = [];
    private readonly List<int> _osOffsets = [];
    // Where an object lives when it is inside an object stream: (stream number, index). Offsets are 0 for such objects.
    private readonly List<(int Num, int Stream, int Index)> _packed = [];

    public StreamingSaver(PdfDocument workspace, PdfSaveOptions options, Stream output, string version, bool objectStreams)
        : base(workspace, options, CancellationToken.None)
    {
        _objectStreams = objectStreams;
        Sink = new ByteSink(output, 0, 1 << 16, pooled: true);
        Writer = new ObjectWriter(workspace, IdentityMapper.Instance, options);
        Sink.Ascii("%PDF-" + version + "\n%");
        Sink.Write([0xE2, 0xE3, 0xCF, 0xD3]);
        Sink.Byte((byte)'\n');
    }

    /// <summary>Gives the output buffer back to the pool. Call once writing is over (finished or abandoned).</summary>
    public void Release() => Sink.Release();

    public void Dispose() => _osBody?.Dispose();

    /// <summary>Bytes written so far.</summary>
    public long Position => Sink.Position;

    public bool IsWritten(int number) => number < _offsets.Length && _offsets[number] != 0;

    /// <summary>True when enough objects wait for an object stream that <see cref="WriteObjectStream"/> should be called.</summary>
    public bool ObjectStreamDue => _osNumbers.Count >= ObjectsPerStream;

    public bool HasPendingObjects => _osNumbers.Count > 0;

    public void Write(int number, PdfObject value, bool plain = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);

        if (number >= _offsets.Length)
        {
            Array.Resize(ref _offsets, Math.Max(number + 1, _offsets.Length * 2));
        }

        if (_offsets[number] != 0)
        {
            throw new InvalidOperationException($"Object {number} was already written.");
        }

        _highest = Math.Max(_highest, number);
        if (_objectStreams && !plain && value is PdfDictionary or PdfArray)
        {
            if (_osNumbers.Count == 0)
            {
                _osBody?.Dispose();
                _osBody = new MemoryStream();
                _osSink = new ByteSink(_osBody, 0, 4096);
            }

            _offsets[number] = -1; // taken; the real place is recorded when the stream is written
            _osNumbers.Add(number);
            _osOffsets.Add(checked((int)_osSink!.Position));
            Writer.WriteValue(_osSink, value, null, number, 0);
            _osSink.Byte((byte)'\n');
            return;
        }

        _offsets[number] = Sink.Position;
        Writer.WriteIndirect(Sink, number, 0, value, null);
    }

    /// <summary>Writes the collected objects as one object stream under <paramref name="streamNumber"/>, a number the caller took from the workspace.</summary>
    public void WriteObjectStream(int streamNumber)
    {
        if (_osNumbers.Count == 0)
        {
            return;
        }

        if (streamNumber >= _offsets.Length)
        {
            Array.Resize(ref _offsets, Math.Max(streamNumber + 1, _offsets.Length * 2));
        }

        _highest = Math.Max(_highest, streamNumber);
        _osSink!.Flush();
        // ISO 32000-2 7.5.7: N pairs 'number offset', then the objects; /First is where the objects start.
        var header = new StringBuilder(_osNumbers.Count * 14);
        for (int i = 0; i < _osNumbers.Count; i++)
        {
            header.Append(_osNumbers[i]).Append(' ').Append(_osOffsets[i]).Append('\n');
        }

        byte[] head = Encoding.ASCII.GetBytes(header.ToString());
        byte[] body = _osBody!.GetBuffer();
        int bodyLength = checked((int)_osBody.Length);
        byte[] data = new byte[head.Length + bodyLength];
        head.CopyTo(data, 0);
        Array.Copy(body, 0, data, head.Length, bodyLength);

        var dict = new PdfDictionary(null);
        dict.Set("Type", new PdfName("ObjStm"));
        dict.Set("N", new PdfInteger(_osNumbers.Count));
        dict.Set("First", new PdfInteger(head.Length));
        dict.Set("Filter", new PdfName("FlateDecode"));
        _offsets[streamNumber] = Sink.Position;
        WriteRawStream(streamNumber, dict, FlateWriter.Compress(data), null);
        for (int i = 0; i < _osNumbers.Count; i++)
        {
            _packed.Add((_osNumbers[i], streamNumber, i));
        }

        _osNumbers.Clear();
        _osOffsets.Clear();
    }

    // Its own method, so the cryptography assembly is loaded only by documents that ask for a random id.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static byte[] RandomId() => RandomNumberGenerator.GetBytes(16);

    public void Finish(PdfReference root, PdfReference? info, byte[]? documentId, bool xrefStream)
    {
        byte[] id = documentId ?? RandomId();
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

        foreach ((int num, int stream, int index) in _packed)
        {
            entries.Add((num, new XrefEntry(2, stream, index)));
        }

        entries.Sort(static (a, b) => a.Num.CompareTo(b.Num));

        int xrefNumber = _highest + 1;
        WriteXref(entries, xrefStream ? xrefNumber + 1 : xrefNumber, trailer, xrefStream, xrefNumber, 0);
    }
}
