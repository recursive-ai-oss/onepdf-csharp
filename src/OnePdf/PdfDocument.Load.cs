using OnePdf.Internal;

namespace OnePdf;

public sealed partial class PdfDocument
{
    private XrefSection[] _sections = [];
    private PdfResolver[] _resolvers = [];
    private Dictionary<int, XrefEntry>? _merged;
    private long _bias;
    private long _headerOffset;
    private readonly Lazy<ScanResult> _scan;

    internal ReadOnlyMemory<byte> ReadRange(long start, int length)
    {
        ThrowIfDisposed();
        _source.EnsureUnchanged();
        if (_source.TryGetMemory(out ReadOnlyMemory<byte> all))
        {
            return all.Slice((int)start, length);
        }

        byte[] buf = new byte[length];
        if (_source.Read(start, buf) != length)
        {
            throw new PdfSourceChangedException("The source returned fewer bytes than it had at open.");
        }

        return buf;
    }

    /// <summary>Follows a reference (and references to references, up to the recursion limit) as seen by one revision's view.</summary>
    internal PdfObject ResolveReference(PdfReference r, int view) => ResolveChain(r.Number, view);

    internal PdfObject ResolveChain(int number, int view)
    {
        PdfObject cur = ResolveObject(number, view);
        int hops = 0;
        while (cur is PdfReference next)
        {
            if (++hops > Limits.MaxRecursionDepth)
            {
                _diag.Warn("A chain of references to references is too long (or loops); read as null.", number);
                return PdfNull.Instance;
            }

            cur = ResolveObject(next.Number, view);
        }

        return cur;
    }

    internal PdfObject ResolveObject(int number, int view)
    {
        ThrowIfDisposed();
        if (_merged is null || number < 0)
        {
            return PdfNull.Instance; // cross-reference data is still being read
        }

        return (PdfObject)Cache.GetOrLoad(ObjectKeys.Object(view, number), (this, number, view), static s => s.Item1.LoadObject(s.number, s.view));
    }

    private XrefEntry? LookupEntry(int view, int number)
    {
        if (view == 0)
        {
            return _merged!.TryGetValue(number, out XrefEntry e) ? e : null;
        }

        for (int k = view; k < _sections.Length; k++)
        {
            if (_sections[k].Entries.TryGetValue(number, out XrefEntry e))
            {
                return e;
            }
        }

        return null;
    }

    private (object, long) LoadObject(int number, int view)
    {
        XrefEntry? entry = LookupEntry(view, number);
        if (entry is not { } e || e.Type == 0)
        {
            return (PdfNull.Instance, 16);
        }

        PdfResolver resolver = _resolvers[Math.Min(view, _resolvers.Length - 1)];
        if (e.Type == 2)
        {
            return LoadCompressed((int)Math.Min(e.A, int.MaxValue), e.B, number, view, resolver);
        }

        _source.EnsureUnchanged();
        long pos = e.A + _bias;
        ParsedObject? p = pos >= 0 && pos < _source.Length ? ParseIndirectAt(pos, number, resolver, _diag) : null;
        if (p is null)
        {
            _diag.Warn($"The cross-reference offset {e.A} does not point at the object; searching the file for it.", number, e.A);
            if (_scan.Value.Objects.TryGetValue(number, out (long Offset, int Generation) loc)
                && ParseIndirectAt(loc.Offset, number, resolver, _diag) is { } found)
            {
                p = found;
            }
            else
            {
                _diag.Error("The object could not be found in the file; read as null.", number, e.A);
                return (PdfNull.Instance, 16);
            }
        }

        return (p.Object, p.Size + 64);
    }

    /// <summary>
    /// Reads <c>n g obj ... endobj</c> at an absolute position. Returns null when no such header with the expected
    /// number is there. A stream's data is not read; only its start is recorded.
    /// </summary>
    internal ParsedObject? ParseIndirectAt(long position, int expectedNumber, PdfResolver resolver, DiagnosticBag? diag)
    {
        using var lx = new Lexer(_source, position);
        Token a = lx.NextToken();
        Token b = lx.NextToken();
        Token c = lx.NextToken();
        if (a.Kind != TokenKind.Integer || b.Kind != TokenKind.Integer || !c.IsKeyword("obj") || a.Int < 0 || a.Int > int.MaxValue
            || (expectedNumber >= 0 && a.Int != expectedNumber))
        {
            return null;
        }

        int num = (int)a.Int;
        int gen = (int)Math.Clamp(b.Int, 0, 65535);
        var parser = new ObjectParser(lx, resolver, Limits, diag, num, _decryptor is not null && num != _encryptObjNum ? this : null, gen);
        PdfObject? obj = parser.ReadObject(out string? kw);
        long size = parser.EstimatedBytes;
        if (obj is null)
        {
            obj = PdfNull.Instance;
            if (kw != "endobj")
            {
                diag?.Warn("The object has no value.", num, position);
            }
        }
        else
        {
            Token n = lx.NextToken();
            if (obj is PdfDictionary d && n.IsKeyword("stream"))
            {
                long p = lx.Position;
                while (lx.ByteAt(p) == ' ')
                {
                    p++;
                }

                if (lx.ByteAt(p) == 13)
                {
                    p++;
                    if (lx.ByteAt(p) == 10)
                    {
                        p++;
                    }
                }
                else if (lx.ByteAt(p) == 10)
                {
                    p++;
                }

                obj = new PdfStream(d, resolver, p);
                lx.Position = p;
                size += 128;
            }
            else if (!n.IsKeyword("endobj"))
            {
                diag?.Warn("The object is not closed by endobj.", num, n.Start);
                lx.Position = n.Start;
            }
        }

        if (obj is not PdfNull)
        {
            obj.Reference = new PdfReference(num, gen);
        }

        obj.SourceOffset = obj is PdfNull ? -1 : position;
        return new ParsedObject { Object = obj, Number = num, Generation = gen, EndPosition = lx.Position, Size = size };
    }

    private (object, long) LoadCompressed(int streamNumber, int index, int number, int view, PdfResolver resolver)
    {
        var d = (ObjStmData)Cache.GetOrLoad(ObjectKeys.ObjStm(view, streamNumber), (this, streamNumber, view), static s => s.Item1.LoadObjectStream(s.streamNumber, s.view));
        int i = index;
        if ((uint)i >= (uint)d.Numbers.Length || d.Numbers[i] != number)
        {
            i = Array.IndexOf(d.Numbers, number);
            if (i < 0)
            {
                _diag.Warn($"Object stream {streamNumber} does not contain the object; read as null.", number);
                return (PdfNull.Instance, 16);
            }

            _diag.Warn($"The cross-reference index {index} into object stream {streamNumber} is wrong; found at {i}.", number);
        }

        long at = (long)d.First + d.Offsets[i];
        if (at >= d.Data.Length)
        {
            _diag.Warn($"Object stream {streamNumber} has an offset beyond its data; read as null.", number);
            return (PdfNull.Instance, 16);
        }

        using var src = new MemorySource(d.Data);
        using var lx = new Lexer(src, at);
        var parser = new ObjectParser(lx, resolver, Limits, _diag, number);
        PdfObject? obj = parser.ReadObject(out _);
        if (obj is null or PdfNull)
        {
            return (PdfNull.Instance, 16);
        }

        obj.Reference = new PdfReference(number, 0);
        obj.ContainerObjectNumber = streamNumber;
        return (obj, parser.EstimatedBytes + 64);
    }

    private (object, long) LoadObjectStream(int streamNumber, int view)
    {
        XrefEntry? e = LookupEntry(view, streamNumber);
        if (e is not { Type: 1 } || ResolveObject(streamNumber, view) is not PdfStream s)
        {
            // Strict mode throws from Warn; otherwise the objects the cross-reference data placed here read as missing.
            _diag.Warn($"Object stream {streamNumber} is missing or is not a stream; its objects read as null.", streamNumber);
            return (EmptyObjStm, 64);
        }

        long n = s.Dictionary.GetInt("N", -1);
        long first = s.Dictionary.GetInt("First", -1);
        ReadOnlyMemory<byte> data = s.DecodeCore(CancellationToken.None);
        bool nOk = n >= 0 && n <= Limits.MaxObjects && n <= data.Length / 2;
        bool firstOk = first >= 0 && first <= data.Length;
        long max = nOk ? n : Math.Min(data.Length / 2, Limits.MaxObjects);
        var numbers = new List<int>();
        var offsets = new List<int>();
        long headerEnd = 0;
        using (var src = new MemorySource(data))
        using (var lx = new Lexer(src, 0))
        {
            for (long i = 0; i < max; i++)
            {
                Token a = lx.NextToken();
                if (a.Kind != TokenKind.Integer)
                {
                    break;
                }

                Token b = lx.NextToken();
                if (b.Kind != TokenKind.Integer || a.Int is < 0 or > int.MaxValue || b.Int is < 0 or > int.MaxValue)
                {
                    break;
                }

                numbers.Add((int)a.Int);
                offsets.Add((int)b.Int);
                headerEnd = lx.Position;
            }
        }

        // /First must lie at or after the end of the header it follows; one inside it cannot be right.
        if (firstOk && first < headerEnd)
        {
            firstOk = false;
        }

        if (!nOk || !firstOk)
        {
            // Salvage (docs/repair.md): the header is a run of 'number offset' integer pairs; they were read until they
            // stopped, and /First is taken to be where they stop.
            _diag.Warn($"Object stream {streamNumber} has an invalid /N or /First; its header was read by scanning.", streamNumber);
        }

        if (nOk && numbers.Count < n)
        {
            _diag.Warn($"The header of object stream {streamNumber} is damaged after {numbers.Count} of {n} entries; the rest are lost.", streamNumber);
        }

        var d = new ObjStmData { Data = data, First = firstOk ? (int)first : (int)headerEnd, Numbers = [.. numbers], Offsets = [.. offsets] };
        return (d, data.Length + (8L * numbers.Count) + 64);
    }

    private static readonly ObjStmData EmptyObjStm = new() { Data = ReadOnlyMemory<byte>.Empty, First = 0, Numbers = [], Offsets = [] };

    // ISO 32000-2 7.3.8.2: the stored length is /Length; when it is wrong the data ends at the 'endstream' keyword.
    internal StreamRange ComputeStreamRange(PdfStream s, long start)
    {
        ThrowIfDisposed();
        _source.EnsureUnchanged();
        long fileLen = _source.Length;
        start = Math.Min(start, fileLen);
        int objNum = s.Reference?.Number ?? -1;

        long declared = -1;
        PdfObject? raw = s.Dictionary.GetRaw("Length");
        if (raw is PdfInteger li)
        {
            declared = li.Value;
        }
        else if (raw is PdfReference lr)
        {
            declared = ReadIndirectLength(s, lr);
        }

        using var lx = new Lexer(_source, start);
        long end = FindStreamEnd(lx, start, declared, out bool ok);
        if (!ok)
        {
            _diag.Warn(
                declared < 0 ? $"The stream has no usable /Length; its data was found by scanning for endstream ({end - start} bytes)."
                             : $"The stream's /Length {declared} is wrong; its data was found by scanning for endstream ({end - start} bytes).",
                objNum,
                start);
        }

        return new StreamRange(start, end - start);
    }

    private long ReadIndirectLength(PdfStream s, PdfReference lr)
    {
        // The length of an object stream or xref stream is never taken from inside an object stream: reading it would
        // need the very stream being read.
        string? type = s.Dictionary.GetRaw("Type") is PdfName tn ? tn.Value : null;
        XrefEntry? e = _merged is null ? null : LookupEntry(s.Resolver.View, lr.Number);
        if (e is not { } entry || entry.Type == 0 || (entry.Type == 2 && type is "ObjStm" or "XRef"))
        {
            return -1;
        }

        return ResolveObject(lr.Number, s.Resolver.View) is PdfInteger i ? i.Value : -1;
    }

    /// <summary>
    /// Finds where stream data ends. A declared length is trusted only when <c>endstream</c> follows it (after at most
    /// 16 white-space bytes); otherwise the data runs up to the next <c>endstream</c>, or <c>endobj</c>, or the end of the file,
    /// less the end-of-line before that keyword.
    /// </summary>
    internal static long FindStreamEnd(Lexer lx, long start, long declared, out bool declaredOk)
    {
        long fileLen = lx.Length;
        if (declared >= 0 && declared <= fileLen - start)
        {
            long p = start + declared;
            for (int skipped = 0; skipped < 16 && Lexer.IsWhite(lx.ByteAt(p)); skipped++)
            {
                p++;
            }

            if (lx.MatchAt(p, "endstream"))
            {
                declaredOk = true;
                return start + declared;
            }
        }

        declaredOk = false;
        long idx = lx.IndexOf("endstream"u8, start, fileLen);
        if (idx < 0)
        {
            idx = lx.IndexOf("endobj"u8, start, fileLen);
        }

        if (idx < 0)
        {
            idx = fileLen;
        }

        long end = idx;
        if (end > start && lx.ByteAt(end - 1) == 10)
        {
            end--;
            if (end > start && lx.ByteAt(end - 1) == 13)
            {
                end--;
            }
        }
        else if (end > start && lx.ByteAt(end - 1) == 13)
        {
            end--;
        }

        return end;
    }
}
