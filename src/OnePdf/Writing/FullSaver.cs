using System.Security.Cryptography;
using System.Text;
using OnePdf.Internal;
using OnePdf.Security;

namespace OnePdf.Writing;

/// <summary>
/// A full rewrite: walks the document from the catalog and Info, numbers the objects it meets 1, 2, 3 … in the order
/// it meets them, and writes each once, so unreachable objects are dropped and old revisions, linearization data and
/// free entries disappear. Objects are parsed once and written at once; only the queue of references not yet written is kept.
/// </summary>
internal sealed class FullSaver : SaverBase, IReferenceMapper, IDisposable
{
    private const int ObjectsPerStream = 100;
    private const int ObjectStreamBytes = 1 << 20;

    private readonly Dictionary<int, int> _map = [];
    private readonly Queue<(PdfObject Obj, int Num)> _queue = new();
    private readonly Dictionary<int, XrefEntry> _entries = [];
    private int _next = 1;
    private int _encryptNumber = -1;
    private SecurityDecryptor? _enc;
    private bool _objectStreams;

    private MemoryStream? _osBody;
    private ByteSink _osSink = null!;
    private readonly List<int> _osNumbers = [];
    private readonly List<int> _osOffsets = [];

    public FullSaver(PdfDocument doc, PdfSaveOptions options, CancellationToken ct)
        : base(doc, options, ct)
    {
    }

    public void Dispose() => _osBody?.Dispose();

    public PdfReference? Map(PdfReference reference)
    {
        PdfObject target = Doc.ResolveChain(reference.Number, 0);
        if (target is PdfNull)
        {
            return null;
        }

        int old = target.Reference?.Number ?? reference.Number;
        if (!_map.TryGetValue(old, out int n))
        {
            n = Allocate(target);
            _map[old] = n;
        }

        return new PdfReference(n, 0);
    }

    private int Allocate(PdfObject obj)
    {
        if (_next >= Doc.Limits.MaxObjects)
        {
            throw new PdfLimitException($"The document has more than {Doc.Limits.MaxObjects} objects.");
        }

        int n = _next++;
        _queue.Enqueue((obj, n));
        return n;
    }

    private PdfReference? MapTop(PdfObject? raw)
    {
        switch (raw)
        {
            case PdfReference r:
                return Map(r);
            case PdfDictionary or PdfArray:
                return new PdfReference(Allocate(raw), 0);
            default:
                return null;
        }
    }

    public void Run(Stream output)
    {
        PdfDictionary trailer = Doc.Trailer;
        PdfObject? rootRaw = trailer.GetRaw("Root");
        if (Doc.Resolve(rootRaw) is not PdfDictionary)
        {
            throw new PdfFormatException("The document has no catalog to save.");
        }

        // Encryption: keep (the document's own key), remove, or set up new.
        (byte[] First, byte[] Second)? id = Options.DocumentId is { Length: > 0 } fixedId ? (fixedId, fixedId) : ReadId(Doc);
        PdfDictionary? encryptDict = null;
        string minVersion = "1.0";
        switch (Options.Encryption)
        {
            case PdfEncryptionAction.Keep when Doc.Decryptor is not null:
                _enc = Doc.Decryptor;
                encryptDict = trailer.GetDictionary("Encrypt");
                break;

            case PdfEncryptionAction.Set:
            {
                PdfEncryptionOptions eo = Options.NewEncryption ?? throw new ArgumentException("PdfEncryptionAction.Set needs PdfSaveOptions.NewEncryption.");
                id ??= (RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(16));
                try
                {
                    NewEncryption ne = StandardSecurityWriter.Create(eo, id.Value.First);
                    _enc = ne.Encryptor;
                    encryptDict = ne.Dictionary;
                    minVersion = ne.MinimumVersion;
                }
                catch (SecurityUnsupportedException e)
                {
                    throw new PdfEncryptionException(e.Message, e);
                }

                break;
            }
        }

        id ??= (RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(16));

        bool stream = Options.XrefFormat == PdfXrefFormat.Stream
            || (Options.XrefFormat == PdfXrefFormat.Auto && Doc.Revisions.Count > 0 && Doc.Revisions[^1].IsXrefStream);
        _objectStreams = stream && Options.UseObjectStreams;
        if (stream)
        {
            minVersion = Higher(minVersion, "1.5");
        }

        string version = Higher(Higher(Doc.Version ?? "1.4", minVersion), Options.MinimumVersion ?? "1.0");

        Sink = new ByteSink(output);
        Writer = new ObjectWriter(Doc, this, Options);
        Sink.Ascii("%PDF-" + version + "\n%");
        Sink.Write([0xE2, 0xE3, 0xCF, 0xD3]);
        Sink.Byte((byte)'\n');

        PdfReference root = MapTop(rootRaw)!;
        PdfReference? info = Doc.Resolve(trailer.GetRaw("Info")) is PdfDictionary ? MapTop(trailer.GetRaw("Info")) : null;
        PdfReference? encryptRef = null;
        if (encryptDict is not null)
        {
            _encryptNumber = Allocate(encryptDict);
            encryptRef = new PdfReference(_encryptNumber, 0);
        }

        // The catalog stays a plain object: readers that look for it before they have set up decryption (the key comes from
        // /Encrypt, which is never inside an object stream either) would otherwise have to open an object stream first.
        while (_queue.Count > 0)
        {
            Ct.ThrowIfCancellationRequested();
            (PdfObject obj, int num) = _queue.Dequeue();
            if (num == _encryptNumber)
            {
                _entries[num] = new XrefEntry(1, Sink.Position, 0);
                Writer.WriteIndirect(Sink, num, 0, obj, null);
            }
            else if (_objectStreams && obj is not PdfStream && num != root.Number)
            {
                AddToObjectStream(num, obj);
            }
            else
            {
                _entries[num] = new XrefEntry(1, Sink.Position, 0);
                Writer.WriteIndirect(Sink, num, 0, obj, _enc);
            }
        }

        FlushObjectStream();

        var core = new PdfDictionary(null);
        core.Set("Root", root);
        if (info is not null)
        {
            core.Set("Info", info);
        }

        if (encryptRef is not null)
        {
            core.Set("Encrypt", encryptRef);
        }

        core.Set("ID", MakeId(id.Value.First, id.Value.Second));

        int xrefNumber = stream ? _next++ : -1;
        var list = new List<(int Num, XrefEntry E)>(_entries.Count + 2) { (0, new XrefEntry(0, 0, 65535)) };
        foreach (KeyValuePair<int, XrefEntry> kv in _entries)
        {
            list.Add((kv.Key, kv.Value));
        }

        list.Sort(static (a, b) => a.Num.CompareTo(b.Num));
        WriteXref(list, _next, core, stream, xrefNumber, 0);
    }

    private void AddToObjectStream(int number, PdfObject obj)
    {
        if (_osNumbers.Count == 0)
        {
            _osBody?.Dispose();
            _osBody = new MemoryStream();
            _osSink = new ByteSink(_osBody, 0, 4096);
        }

        _osNumbers.Add(number);
        _osOffsets.Add(checked((int)_osSink.Position));
        Writer.WriteValue(_osSink, obj, null, number, 0);
        _osSink.Byte((byte)'\n');
        if (_osNumbers.Count >= ObjectsPerStream || _osSink.Position > ObjectStreamBytes)
        {
            FlushObjectStream();
        }
    }

    // ISO 32000-2 7.5.7: the stream holds N pairs 'number offset', then the objects; /First is where the objects start.
    private void FlushObjectStream()
    {
        if (_osNumbers.Count == 0)
        {
            return;
        }

        _osSink.Flush();
        byte[] body = _osBody!.ToArray();
        var header = new StringBuilder();
        for (int i = 0; i < _osNumbers.Count; i++)
        {
            header.Append(_osNumbers[i]).Append(' ').Append(_osOffsets[i]).Append('\n');
        }

        byte[] head = Encoding.ASCII.GetBytes(header.ToString());
        byte[] data = new byte[head.Length + body.Length];
        head.CopyTo(data, 0);
        body.CopyTo(data, head.Length);

        if (_next >= Doc.Limits.MaxObjects)
        {
            throw new PdfLimitException($"The document has more than {Doc.Limits.MaxObjects} objects.");
        }

        int streamNumber = _next++;
        var dict = new PdfDictionary(null);
        dict.Set("Type", new PdfName("ObjStm"));
        dict.Set("N", new PdfInteger(_osNumbers.Count));
        dict.Set("First", new PdfInteger(head.Length));
        dict.Set("Filter", new PdfName("FlateDecode"));
        _entries[streamNumber] = new XrefEntry(1, Sink.Position, 0);
        WriteRawStream(streamNumber, dict, FlateWriter.Compress(data), _enc);
        for (int i = 0; i < _osNumbers.Count; i++)
        {
            _entries[_osNumbers[i]] = new XrefEntry(2, streamNumber, i);
        }

        _osNumbers.Clear();
        _osOffsets.Clear();
    }
}
