using OnePdf.Internal;

namespace OnePdf;

public sealed partial class PdfDocument
{
    private const int MaxSections = 65_536;
    private int _declaredEntries;

    private void Initialize()
    {
        long length = _source.Length;
        if (length == 0)
        {
            throw new PdfFormatException("The file is empty.");
        }

        FindHeader();

        string? reason = null;
        try
        {
            ReadXrefChain();
            if (Trailer.GetDictionary("Root") is null)
            {
                reason = "the trailer's /Root does not lead to a dictionary";
            }
        }
        catch (PdfFormatException e)
        {
            reason = e.Message;
        }

        if (reason is not null)
        {
            _diag.Warn($"The cross-reference data is unusable ({reason}); rebuilt by scanning the file for objects.");
            RebuildBySearching();
        }

        BuildRevisions();
        if (Trailer.ContainsKey("Encrypt"))
        {
            _diag.Info("The document is encrypted; decryption is not available, so strings and streams read as stored.");
        }
    }

    // ISO 32000-2 7.5.2: the header is the first line, but writers put junk before it; look in the first KiB.
    private void FindHeader()
    {
        Span<byte> head = stackalloc byte[1024];
        int n = _source.Read(0, head[..(int)Math.Min(head.Length, _source.Length)]);
        int idx = head[..n].IndexOf("%PDF-"u8);
        if (idx < 0)
        {
            _diag.Warn("The file has no %PDF- header.", offset: 0);
            return;
        }

        _headerOffset = idx;
        int end = idx + 5;
        int stop = end;
        while (stop < n && stop < end + 8 && (head[stop] is >= (byte)'0' and <= (byte)'9' || head[stop] == (byte)'.'))
        {
            stop++;
        }

        Version = System.Text.Encoding.ASCII.GetString(head[end..stop]);
        if (idx > 0)
        {
            _diag.Info($"The header starts at offset {idx}; bytes before it were ignored.", offset: 0);
        }
    }

    private long FindStartXref()
    {
        int tail = (int)Math.Min(_source.Length, 8192);
        byte[] buf = new byte[tail];
        int n = _source.Read(_source.Length - tail, buf);
        int idx = buf.AsSpan(0, n).LastIndexOf("startxref"u8);
        if (idx < 0)
        {
            throw new PdfFormatException("There is no startxref near the end of the file.");
        }

        int p = idx + 9;
        while (p < n && Lexer.IsWhite(buf[p]))
        {
            p++;
        }

        long v = 0;
        int digits = 0;
        while (p < n && buf[p] is >= (byte)'0' and <= (byte)'9' && digits < 18)
        {
            v = (v * 10) + (buf[p] - '0');
            p++;
            digits++;
        }

        if (digits == 0)
        {
            throw new PdfFormatException("startxref is not followed by an offset.");
        }

        return v;
    }

    /// <summary>
    /// Reads the cross-reference sections from the newest (named by startxref) back through /Prev, folding a hybrid file's
    /// /XRefStm into its section. Any inconsistency throws <see cref="PdfFormatException"/> and the caller rebuilds.
    /// </summary>
    private void ReadXrefChain()
    {
        long next = FindStartXref();
        var sections = new List<XrefSection>();
        var resolvers = new List<PdfResolver>();
        var visited = new HashSet<long>();
        _declaredEntries = 0;
        _merged = null;
        while (next >= 0)
        {
            if (!visited.Add(next))
            {
                throw new PdfFormatException($"The cross-reference /Prev chain loops back to offset {next}.");
            }

            if (sections.Count >= MaxSections)
            {
                throw new PdfFormatException("The cross-reference chain has too many sections.");
            }

            long pos = LocateSection(next);
            var resolver = new PdfResolver(this, sections.Count);
            resolvers.Add(resolver);
            XrefSection s = ReadSection(pos, resolver);
            sections.Add(s);
            next = s.Prev;
        }

        _sections = [.. sections];
        _resolvers = [.. resolvers];
        var merged = new Dictionary<int, XrefEntry>(Math.Min(_declaredEntries, 1 << 20));
        foreach (XrefSection s in _sections)
        {
            foreach (KeyValuePair<int, XrefEntry> kv in s.Entries)
            {
                merged.TryAdd(kv.Key, kv.Value);
            }
        }

        _merged = merged;
        Trailer = MergeTrailers();
    }

    private PdfDictionary MergeTrailers()
    {
        PdfDictionary newest = _sections[0].Trailer;
        if (_sections.Length == 1)
        {
            return newest;
        }

        var result = new PdfDictionary(_resolvers[0]);
        foreach (KeyValuePair<string, PdfObject> kv in newest.Entries)
        {
            result.Set(kv.Key, kv.Value);
        }

        foreach (string key in (ReadOnlySpan<string>)["Root", "Info", "ID", "Encrypt"])
        {
            if (result.ContainsKey(key))
            {
                continue;
            }

            for (int k = 1; k < _sections.Length; k++)
            {
                if (_sections[k].Trailer.GetRaw(key) is { } v)
                {
                    result.Set(key, v);
                    break;
                }
            }
        }

        return result;
    }

    // startxref offsets count from the file start, but files with junk before the header are often written relative to the header.
    private long LocateSection(long offset)
    {
        Span<long> candidates = [offset + _bias, offset, offset + _headerOffset];
        for (int i = 0; i < candidates.Length; i++)
        {
            long c = candidates[i];
            if (c < 0 || c >= _source.Length || (i > 0 && (c == candidates[0] || (i == 2 && c == candidates[1]))))
            {
                continue;
            }

            if (LooksLikeSection(c))
            {
                if (c != offset + _bias)
                {
                    _bias = c - offset;
                    _diag.Info($"Offsets are relative to {(_bias == 0 ? "the start of the file" : $"the header at {_bias}")}.", offset: c);
                }

                return c;
            }
        }

        throw new PdfFormatException($"There is no cross-reference section at offset {offset}.") { Offset = offset };
    }

    private bool LooksLikeSection(long pos)
    {
        using var lx = new Lexer(_source, pos);
        Token t = lx.NextToken();
        if (t.IsKeyword("xref"))
        {
            return true;
        }

        if (t.Kind != TokenKind.Integer)
        {
            return false;
        }

        Token g = lx.NextToken();
        return g.Kind == TokenKind.Integer && lx.NextToken().IsKeyword("obj");
    }

    private XrefSection ReadSection(long pos, PdfResolver resolver)
    {
        using var lx = new Lexer(_source, pos);
        Token t = lx.NextToken();
        if (t.IsKeyword("xref"))
        {
            return ReadTableSection(lx, pos, resolver);
        }

        return ReadStreamSection(pos, resolver);
    }

    // ISO 32000-2 7.5.4: subsections of 'first count' followed by count entries 'offset generation n|f', then the trailer.
    private XrefSection ReadTableSection(Lexer lx, long pos, PdfResolver resolver)
    {
        var section = new XrefSection { Offset = pos };
        while (true)
        {
            Token t = lx.NextToken();
            if (t.IsKeyword("trailer"))
            {
                break;
            }

            Token c = lx.NextToken();
            if (t.Kind != TokenKind.Integer || c.Kind != TokenKind.Integer || t.Int < 0 || c.Int < 0)
            {
                throw new PdfFormatException("A cross-reference subsection header is damaged or the trailer is missing.") { Offset = t.Start };
            }

            long first = t.Int;
            long count = c.Int;
            if (count > Limits.MaxObjects || _declaredEntries + count > Limits.MaxObjects)
            {
                throw new PdfLimitException($"The cross-reference data declares more than {Limits.MaxObjects} objects.") { Offset = t.Start };
            }

            // An entry takes at least six bytes ("0 0 n\n"); a count the file cannot hold is a lie.
            if (count * 6 > lx.Length - lx.Position)
            {
                throw new PdfFormatException($"A cross-reference subsection claims {count} entries, more than the file can hold.") { Offset = t.Start };
            }

            _declaredEntries += (int)count;
            for (long i = 0; i < count; i++)
            {
                Token o = lx.NextToken();
                Token g = lx.NextToken();
                Token k = lx.NextToken();
                if (o.Kind != TokenKind.Integer || g.Kind != TokenKind.Integer || !(k.IsKeyword("n") || k.IsKeyword("f")))
                {
                    throw new PdfFormatException("A cross-reference entry is damaged.") { Offset = o.Start };
                }

                bool free = k.IsKeyword("f");
                if (i == 0 && first == 1 && free && o.Int == 0 && g.Int == 65535)
                {
                    // Some writers number the first subsection from 1 although its first entry is the free head, object 0.
                    _diag.Warn("A cross-reference subsection starts at 1 but its first entry is object 0's free head; numbering shifted back.", offset: t.Start);
                    first = 0;
                }

                long num = first + i;
                if (num > int.MaxValue)
                {
                    throw new PdfFormatException("A cross-reference entry has an object number out of range.") { Offset = o.Start };
                }

                int gen = (int)Math.Clamp(g.Int, 0, 65535);
                section.Entries[(int)num] = free || o.Int == 0 ? new XrefEntry(0, 0, gen) : new XrefEntry(1, o.Int, gen);
            }
        }

        var parser = new ObjectParser(lx, resolver, Limits, _diag);
        if (parser.ReadObject(out _) is not PdfDictionary trailer)
        {
            throw new PdfFormatException("The trailer is missing or is not a dictionary.") { Offset = lx.Position };
        }

        section.Trailer = trailer;
        section.Prev = NumberOrMinusOne(trailer.Get("Prev"));
        if (NumberOrMinusOne(trailer.Get("XRefStm")) is >= 0 and var stmOffset)
        {
            // ISO 32000-2 7.5.8.4: a hybrid file looks in the table first, then the /XRefStm; the stream
            // supplies the compressed objects the table lists as free.
            long stmPos = LocateSection(stmOffset);
            XrefSection stm = ReadStreamSection(stmPos, resolver);
            foreach (KeyValuePair<int, XrefEntry> kv in stm.Entries)
            {
                if (!section.Entries.TryGetValue(kv.Key, out XrefEntry existing) || existing.Type == 0)
                {
                    section.Entries[kv.Key] = kv.Value;
                }
            }
        }

        return section;
    }

    private static long NumberOrMinusOne(PdfObject? o) => o switch
    {
        PdfInteger i when i.Value >= 0 => i.Value,
        PdfReal r when r.Value >= 0 && r.Value < 9e18 => (long)r.Value,
        _ => -1,
    };

    // ISO 32000-2 7.5.8: entries are fixed-width big-endian fields described by /W, covering the ranges in /Index.
    private XrefSection ReadStreamSection(long pos, PdfResolver resolver)
    {
        ParsedObject? p = ParseIndirectAt(pos, -1, resolver, _diag);
        if (p?.Object is not PdfStream s || s.Dictionary.GetName("Type") != "XRef")
        {
            throw new PdfFormatException("The cross-reference stream is missing or is not a stream.") { Offset = pos };
        }

        PdfDictionary d = s.Dictionary;
        PdfArray? w = d.GetArray("W");
        if (w is null || w.Count < 3)
        {
            throw new PdfFormatException("The cross-reference stream has no valid /W.") { Offset = pos };
        }

        Span<int> widths = stackalloc int[3];
        for (int i = 0; i < 3; i++)
        {
            long v = w.GetInt(i, -1);
            if (v is < 0 or > 8)
            {
                throw new PdfFormatException("The cross-reference stream has an invalid /W.") { Offset = pos };
            }

            widths[i] = (int)v;
        }

        int rowLen = widths[0] + widths[1] + widths[2];
        if (rowLen == 0)
        {
            throw new PdfFormatException("The cross-reference stream has empty rows.") { Offset = pos };
        }

        long size = d.GetInt("Size", -1);
        PdfArray? index = d.GetArray("Index");
        var ranges = new List<(long First, long Count)>();
        if (index is null)
        {
            if (size < 0)
            {
                throw new PdfFormatException("The cross-reference stream has neither /Index nor /Size.") { Offset = pos };
            }

            ranges.Add((0, size));
        }
        else
        {
            for (int i = 0; i + 1 < index.Count; i += 2)
            {
                ranges.Add((index.GetInt(i, -1), index.GetInt(i + 1, -1)));
            }
        }

        long total = 0;
        foreach ((long first, long count) in ranges)
        {
            if (first < 0 || count < 0)
            {
                throw new PdfFormatException("The cross-reference stream has a negative /Index value.") { Offset = pos };
            }

            total += count;
            if (total > Limits.MaxObjects || _declaredEntries + total > Limits.MaxObjects)
            {
                throw new PdfLimitException($"The cross-reference data declares more than {Limits.MaxObjects} objects.") { Offset = pos };
            }
        }

        if (total * rowLen > Limits.MaxDecodedStreamBytes)
        {
            throw new PdfLimitException("The cross-reference stream is larger than the stream size limit.") { Offset = pos };
        }

        byte[] data = s.DecodeCore(CancellationToken.None);
        if (total * rowLen > data.Length)
        {
            throw new PdfFormatException("The cross-reference stream holds fewer entries than it declares.") { Offset = pos };
        }

        _declaredEntries += (int)total;
        var section = new XrefSection { Offset = pos, IsStream = true, Trailer = d };
        int at = 0;
        foreach ((long first, long count) in ranges)
        {
            for (long i = 0; i < count; i++)
            {
                long type = widths[0] == 0 ? 1 : ReadField(data, ref at, widths[0]);
                long f2 = ReadField(data, ref at, widths[1]);
                long f3 = ReadField(data, ref at, widths[2]);
                long num = first + i;
                if (num > int.MaxValue)
                {
                    throw new PdfFormatException("A cross-reference entry has an object number out of range.") { Offset = pos };
                }

                section.Entries[(int)num] = type switch
                {
                    0 => new XrefEntry(0, f2, (int)Math.Min(f3, 65535)),
                    1 => f2 == 0 ? new XrefEntry(0, 0, 0) : new XrefEntry(1, f2, (int)Math.Min(f3, 65535)),
                    2 => new XrefEntry(2, f2, (int)Math.Min(f3, int.MaxValue)),
                    _ => new XrefEntry(0, 0, 0), // unknown types are references to the null object (7.5.8.3)
                };
            }
        }

        section.Prev = NumberOrMinusOne(d.Get("Prev"));
        return section;
    }

    private static long ReadField(byte[] data, ref int at, int width)
    {
        ulong v = 0;
        for (int i = 0; i < width; i++)
        {
            v = (v << 8) | data[at++];
        }

        return v > long.MaxValue ? long.MaxValue : (long)v;
    }

    private void BuildRevisions()
    {
        var list = new PdfRevision[_sections.Length];
        for (int i = 0; i < list.Length; i++)
        {
            int j = _sections.Length - 1 - i;
            list[i] = new PdfRevision(this, i, j, _sections[j]);
        }

        Revisions = list;
        int max = -1;
        foreach (int k in _merged!.Keys)
        {
            max = Math.Max(max, k);
        }

        ObjectCount = max + 1;
    }

    /// <summary>Absolute end of the revision that starts with the section at <paramref name="sectionPos"/>: just past its %%EOF.</summary>
    internal long FindRevisionEnd(long sectionPos)
    {
        using var lx = new Lexer(_source, sectionPos);
        long sx = lx.IndexOf("startxref"u8, sectionPos, _source.Length);
        if (sx < 0)
        {
            return _source.Length;
        }

        long eof = lx.IndexOf("%%EOF"u8, sx, Math.Min(_source.Length, sx + 64));
        if (eof < 0)
        {
            return _source.Length;
        }

        long end = eof + 5;
        if (lx.ByteAt(end) == 13)
        {
            end++;
        }

        if (lx.ByteAt(end) == 10)
        {
            end++;
        }

        return end;
    }
}
