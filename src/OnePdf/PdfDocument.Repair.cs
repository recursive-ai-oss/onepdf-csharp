using OnePdf.Internal;

namespace OnePdf;

public sealed partial class PdfDocument
{
    /// <summary>
    /// Last-resort scan: finds every <c>n g obj</c> header and every <c>trailer</c> keyword in the file, skipping over
    /// stream bodies so that bytes inside them are not mistaken for objects. Later definitions replace earlier ones.
    /// Nothing is reported here; callers record what they do with the result.
    /// </summary>
    private ScanResult ScanFile()
    {
        var result = new ScanResult();
        PdfResolver resolver = _resolvers.Length > 0 ? _resolvers[0] : new PdfResolver(this, 0);
        using var lx = new Lexer(_source, 0);
        long len = lx.Length;
        long pos = 0;
        while (pos < len)
        {
            int b = lx.ByteAt(pos);
            if (b == 'o' && lx.ByteAt(pos + 1) == 'b' && lx.ByteAt(pos + 2) == 'j' && !Lexer.IsRegular(lx.ByteAt(pos + 3))
                && !(pos >= 3 && lx.ByteAt(pos - 1) == 'd' && lx.ByteAt(pos - 2) == 'n' && lx.ByteAt(pos - 3) == 'e')
                && TryHeaderBefore(lx, pos, out long headerStart, out int num, out int gen))
            {
                if (!result.Objects.ContainsKey(num) && result.Objects.Count >= Limits.MaxObjects)
                {
                    throw new PdfLimitException($"The file holds more than {Limits.MaxObjects} objects.");
                }

                result.Objects[num] = (headerStart, gen);
                ParsedObject? p = ParseIndirectAt(headerStart, num, resolver, null);
                if (p is null)
                {
                    pos += 3;
                    continue;
                }

                pos = Math.Max(p.EndPosition, pos + 3);
                PdfDictionary? dict = p.Object as PdfDictionary ?? (p.Object as PdfStream)?.Dictionary;
                if (p.Object is PdfStream)
                {
                    // Skip the stream body: trust /Length only when endstream follows it.
                    long declared = dict!.GetRaw("Length") is PdfInteger li ? li.Value : -1;
                    pos = Math.Max(pos, FindStreamEnd(lx, p.EndPosition, declared, out _));
                }

                string? type = dict?.GetRaw("Type") is PdfName tn ? tn.Value : null;
                switch (type)
                {
                    case "XRef":
                        result.Trailers.Add(dict!);
                        break;
                    case "ObjStm":
                        result.ObjectStreams.Add((headerStart, num));
                        break;
                    case "Catalog":
                        result.LastCatalog = num;
                        break;
                }
            }
            else if (b == 't' && lx.MatchAt(pos, "trailer") && !Lexer.IsRegular(lx.ByteAt(pos + 7)))
            {
                lx.Position = pos + 7;
                var parser = new ObjectParser(lx, resolver, Limits, null);
                if (parser.ReadObject(out _) is PdfDictionary trailer)
                {
                    result.Trailers.Add(trailer);
                }

                pos = Math.Max(lx.Position, pos + 7);
            }
            else
            {
                pos++;
            }
        }

        return result;
    }

    // Walks back from the 'obj' keyword over "<number> <generation>" and the white space between.
    private static bool TryHeaderBefore(Lexer lx, long objPos, out long start, out int number, out int generation)
    {
        start = 0;
        number = 0;
        generation = 0;
        long p = objPos - 1;
        while (p >= 0 && Lexer.IsWhite(lx.ByteAt(p)))
        {
            p--;
        }

        long genEnd = p;
        while (p >= 0 && lx.ByteAt(p) is >= '0' and <= '9')
        {
            p--;
        }

        if (p == genEnd || genEnd - p > 5)
        {
            return false;
        }

        long genStart = p + 1;
        long gap = p;
        while (p >= 0 && Lexer.IsWhite(lx.ByteAt(p)))
        {
            p--;
        }

        if (p == gap)
        {
            return false;
        }

        long numEnd = p;
        while (p >= 0 && lx.ByteAt(p) is >= '0' and <= '9')
        {
            p--;
        }

        if (p == numEnd || numEnd - p > 10)
        {
            return false;
        }

        long numStart = p + 1;
        long n = 0;
        for (long i = numStart; i <= numEnd; i++)
        {
            n = (n * 10) + (lx.ByteAt(i) - '0');
        }

        long g = 0;
        for (long i = genStart; i <= genEnd; i++)
        {
            g = (g * 10) + (lx.ByteAt(i) - '0');
        }

        if (n > int.MaxValue || g > 65535)
        {
            return false;
        }

        start = numStart;
        number = (int)n;
        generation = (int)g;
        return true;
    }

    /// <summary>
    /// Replaces the cross-reference data with what a scan finds: a single section with the last definition of every
    /// object, the compressed objects of every object stream, and a trailer taken from the file's last usable trailer
    /// (or xref stream dictionary), else built around the last catalog found. See docs/repair.md.
    /// </summary>
    private void RebuildBySearching()
    {
        Cache = new ObjectCache(Math.Max(0, _options.ObjectCacheBytes));
        _merged = null;
        _bias = 0;
        ScanResult scan = _scan.Value;

        var entries = new Dictionary<int, XrefEntry>(scan.Objects.Count);
        var definedAt = new Dictionary<int, long>(scan.Objects.Count);
        foreach (KeyValuePair<int, (long Offset, int Generation)> kv in scan.Objects)
        {
            entries[kv.Key] = new XrefEntry(1, kv.Value.Offset, kv.Value.Generation);
            definedAt[kv.Key] = kv.Value.Offset;
        }

        var resolver = new PdfResolver(this, 0);
        PdfDictionary? trailer = null;
        for (int i = scan.Trailers.Count - 1; i >= 0 && trailer is null; i--)
        {
            if (scan.Trailers[i].GetRaw("Root") is PdfReference)
            {
                trailer = new PdfDictionary(resolver);
                foreach (string key in (ReadOnlySpan<string>)["Root", "Info", "ID", "Encrypt"])
                {
                    for (int k = i; k >= 0; k--)
                    {
                        if (scan.Trailers[k].GetRaw(key) is { } v)
                        {
                            trailer.Set(key, v);
                            break;
                        }
                    }
                }
            }
        }

        if (trailer is null)
        {
            if (scan.LastCatalog < 0)
            {
                throw new PdfFormatException("The file has no usable cross-reference data, trailer or document catalog.");
            }

            _diag.Warn("No trailer with a /Root was found; the last /Type /Catalog object was used.", scan.LastCatalog);
            trailer = new PdfDictionary(resolver);
            trailer.Set("Root", new PdfReference(scan.LastCatalog, 0));
        }

        _sections = [new XrefSection { Offset = -1, Entries = entries, Trailer = trailer }];
        _resolvers = [resolver];
        _merged = entries;
        Trailer = trailer;

        // Object streams of an encrypted file are encrypted: the keys must be known before their headers are read.
        SetupEncryption();

        // Objects inside object streams: a stream's contents count as defined where the stream is, so a later
        // direct definition of the same number wins over them.
        foreach ((long streamPos, int streamNumber) in scan.ObjectStreams)
        {
            ObjStmData d;
            try
            {
                d = (ObjStmData)Cache.GetOrLoad(ObjectKeys.ObjStm(0, streamNumber), (this, streamNumber), static s => s.Item1.LoadObjectStream(s.streamNumber, 0));
            }
            catch (PdfFormatException e)
            {
                _diag.Warn($"Object stream {streamNumber} could not be read while rebuilding ({e.Message}); its objects are lost.", streamNumber);
                continue;
            }

            for (int i = 0; i < d.Numbers.Length; i++)
            {
                int n = d.Numbers[i];
                if (definedAt.TryGetValue(n, out long at) && at > streamPos)
                {
                    continue;
                }

                entries[n] = new XrefEntry(2, streamNumber, i);
                definedAt[n] = streamPos;
            }
        }
    }
}
