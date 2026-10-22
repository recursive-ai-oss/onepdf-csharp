using System.Text;

namespace OnePdf.Tests;

public class StructureTests
{
    private static PdfDocument Open(byte[] data, PdfOpenOptions? o = null) => PdfDocument.Open(data, o);

    [Fact]
    public void ClassicTableFileOpens()
    {
        using PdfDocument doc = Open(PdfBuilder.Simple());
        Assert.Equal("1.7", doc.Version);
        Assert.Equal(1, doc.PageCount);
        Assert.Equal("Catalog", doc.Catalog.GetName("Type"));
        Assert.Equal(4, doc.ObjectCount);
        Assert.Single(doc.Revisions);
        Assert.Empty(doc.Diagnostics);
        PdfObject page = doc.GetObject(3)!;
        Assert.Equal(3, page.Reference!.Number);
        Assert.True(page.SourceOffset > 0);
    }

    [Fact]
    public void ObjectSyntaxIsParsed()
    {
        var b = new PdfBuilder();
        b.BasicPages();
        b.Obj(4, "<< /A 1 /B -2.5 /C (a\\(b\\)\\n\\101 x\\\ny) /D <48656c6C6f7> /E /Na#20me /F [1 2 R 3 0 R true null] /G .5 /H +3 /I << /J [ [ ] ] >> >>");
        long x = b.XrefTable(5);
        b.Trailer("/Size 5 /Root 1 0 R", x);
        using PdfDocument doc = Open(b.ToArray());
        var d = (PdfDictionary)doc.GetObject(4)!;
        Assert.Equal(1, d.GetInt("A"));
        Assert.Equal(-2.5, d.GetDouble("B"));
        Assert.Equal("a(b)\nA xy", d.GetString("C")!.GetText());
        Assert.Equal("Hellop", d.GetString("D")!.GetText());
        Assert.Equal("Na me", d.GetName("E"));
        PdfArray f = d.GetArray("F")!;
        Assert.Equal(4, f.Count);
        Assert.Equal(1, ((PdfReference)f.GetRaw(0)).Number);
        Assert.Equal(2, ((PdfReference)f.GetRaw(0)).Generation);
        Assert.IsType<PdfBoolean>(f[2]);
        Assert.Null(f[3]);
        Assert.Equal(0.5, d.GetDouble("G"));
        Assert.Equal(3, d.GetInt("H"));
        Assert.Equal(0, d.GetDictionary("I")!.GetArray("J")!.GetInt(0));
        Assert.Empty(doc.Diagnostics);
    }

    private static byte[] ObjStm(out int first, params (int Num, string Body)[] objs)
    {
        var header = new StringBuilder();
        var bodies = new StringBuilder();
        foreach ((int num, string body) in objs)
        {
            header.Append(num).Append(' ').Append(bodies.Length).Append(' ');
            bodies.Append(body).Append('\n');
        }

        first = header.Length;
        return Encoding.Latin1.GetBytes(header.ToString() + bodies);
    }

    /// <summary>Rows of (type, field2, field3) as W [1 4 2], Flate with the PNG Up predictor.</summary>
    private static byte[] XrefStreamData(params (int Type, long F2, int F3)[] rows)
    {
        const int Row = 7;
        var raw = new List<byte>();
        byte[] prev = new byte[Row];
        foreach ((int type, long f2, int f3) in rows)
        {
            byte[] cur = [(byte)type, (byte)(f2 >> 24), (byte)(f2 >> 16), (byte)(f2 >> 8), (byte)f2, (byte)(f3 >> 8), (byte)f3];
            raw.Add(2);
            for (int i = 0; i < Row; i++)
            {
                raw.Add((byte)(cur[i] - prev[i]));
            }

            prev = cur;
        }

        return PdfBuilder.Deflate([.. raw]);
    }

    private const string XrefDict = "/Type /XRef /W [1 4 2] /Filter /FlateDecode /DecodeParms << /Predictor 12 /Columns 7 >>";

    private static byte[] XrefStreamFile(bool breakStartxref = false)
    {
        var b = new PdfBuilder("1.5");
        byte[] os = ObjStm(
            out int first,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>"));
        b.Stream(4, $"/Type /ObjStm /N 3 /First {first} /Filter /FlateDecode", PdfBuilder.Deflate(os));
        long xrefAt = b.Position;
        byte[] xs = XrefStreamData((0, 0, 65535), (2, 4, 0), (2, 4, 1), (2, 4, 2), (1, b.Offsets[4], 0), (1, xrefAt, 0));
        b.Stream(5, $"{XrefDict} /Size 6 /Root 1 0 R", xs);
        b.Write($"startxref\n{(breakStartxref ? 3 : xrefAt)}\n%%EOF\n");
        return b.ToArray();
    }

    [Fact]
    public void XrefStreamAndObjectStreamOpen()
    {
        using PdfDocument doc = Open(XrefStreamFile());
        Assert.Equal(1, doc.PageCount);
        Assert.Empty(doc.Diagnostics);
        PdfObject page = doc.GetObject(3)!;
        Assert.Equal(4, page.ContainerObjectNumber);
        Assert.Equal(-1, page.SourceOffset);
        Assert.True(doc.Revisions[0].IsXrefStream);
    }

    [Fact]
    public void HybridFileFindsCompressedObjectsThroughXRefStm()
    {
        var b = new PdfBuilder("1.5");
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        byte[] os = ObjStm(out int first, (3, "<< /Type /Page /Parent 2 0 R >>"));
        b.Stream(4, $"/Type /ObjStm /N 1 /First {first}", os);
        long stmAt = b.Position;
        b.Stream(5, "/Type /XRef /W [1 4 2] /Size 6 /Index [3 1]", [2, 0, 0, 0, 4, 0, 0]);
        long x = b.XrefTable(6, only: [1, 2, 4]);
        b.Trailer($"/Size 6 /Root 1 0 R /XRefStm {stmAt}", x);
        using PdfDocument doc = Open(b.ToArray());
        Assert.Equal(1, doc.PageCount);
        Assert.Equal(4, doc.GetObject(3)!.ContainerObjectNumber);
        Assert.Empty(doc.Diagnostics);
    }

    private static byte[] Incremental()
    {
        var b = new PdfBuilder();
        b.BasicPages();
        long x1 = b.XrefTable(4);
        b.Trailer("/Size 4 /Root 1 0 R", x1);
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>");
        b.Obj(4, "<< /Type /Page /Parent 2 0 R >>");
        long x2 = b.Position;
        b.Write($"xref\n0 1\n0000000000 65535 f \n2 1\n{b.Offsets[2]:D10} 00000 n \n4 1\n{b.Offsets[4]:D10} 00000 n \n");
        b.Trailer($"/Size 5 /Root 1 0 R /Prev {x1}", x2);
        b.Obj(5, "<< /Type /Pages /Kids [3 0 R 4 0 R 4 0 R] >>");
        long x3 = b.Position;
        b.Write($"xref\n5 1\n{b.Offsets[5]:D10} 00000 n \n");
        b.Trailer($"/Size 6 /Prev {x2}", x3);
        return b.ToArray();
    }

    [Fact]
    public void IncrementalUpdatesKeepEarlierRevisionsReachable()
    {
        byte[] data = Incremental();
        using PdfDocument doc = Open(data);
        Assert.Equal(3, doc.Revisions.Count);
        Assert.Equal(2, doc.PageCount);
        Assert.Equal(1, doc.Revisions[0].PageCount);
        Assert.Equal(2, doc.Revisions[1].PageCount);
        Assert.Equal(2, doc.Revisions[2].PageCount);
        Assert.Null(doc.Revisions[0].GetObject(4));
        Assert.NotNull(doc.Revisions[1].GetObject(4));
        Assert.Equal(data.Length, doc.Revisions[2].EndOffset);
        Assert.True(doc.Revisions[0].EndOffset < doc.Revisions[1].EndOffset);
        Assert.Equal(1, ((PdfDictionary)doc.Revisions[0].GetObject(2)!).GetArray("Kids")!.Count);

        // The last trailer omits /Root; the newest value is inherited from the older section.
        Assert.NotNull(doc.Trailer.GetRaw("Root"));
        Assert.Empty(doc.Diagnostics);
    }

    [Fact]
    public void JunkBeforeTheHeaderIsToleratedWhetherOrNotOffsetsCountIt()
    {
        string junk = new('x', 100);
        foreach (bool relative in new[] { true, false })
        {
            var b = new PdfBuilder("1.4", junk);
            b.BasicPages();
            long bias = relative ? b.HeaderOffset : 0;
            long x = b.XrefTable(4, bias: bias);
            b.Trailer("/Size 4 /Root 1 0 R", x, bias);
            using PdfDocument doc = Open(b.ToArray());
            Assert.Equal(1, doc.PageCount);
            Assert.DoesNotContain(doc.Diagnostics, d => d.Severity >= PdfSeverity.Warning);
        }
    }

    // ----- repair -----

    private static void AssertRepairs(byte[] data, string messagePart, int pages = 1)
    {
        using PdfDocument doc = Open(data);
        Assert.Equal(pages, doc.PageCount);
        Assert.Contains(doc.Diagnostics, d => d.Severity >= PdfSeverity.Warning && d.Message.Contains(messagePart, StringComparison.OrdinalIgnoreCase));
        Assert.Throws<PdfFormatException>(() =>
        {
            using PdfDocument strict = Open(data, new PdfOpenOptions { Strict = true });
            _ = strict.PageCount;
            _ = strict.GetObject(3);
        });
    }

    [Fact]
    public void WrongStartxrefRebuilds()
    {
        var b = new PdfBuilder();
        b.BasicPages();
        long x = b.XrefTable(4);
        b.Trailer("/Size 4 /Root 1 0 R", x + 11);
        AssertRepairs(b.ToArray(), "rebuilt");
    }

    [Fact]
    public void WrongObjectOffsetsAreFoundByScanning()
    {
        var b = new PdfBuilder();
        b.BasicPages();
        long x = b.XrefTable(4, bias: 9);
        b.Trailer("/Size 4 /Root 1 0 R", x);
        byte[] data = b.ToArray();
        using PdfDocument doc = Open(data);
        Assert.Equal(1, doc.PageCount);
        Assert.Equal(3, doc.GetObject(3)!.Reference!.Number);
        Assert.Contains(doc.Diagnostics, d => d.ObjectNumber == 3 && d.Message.Contains("searching the file", StringComparison.Ordinal));
        Assert.Throws<PdfFormatException>(() =>
        {
            using PdfDocument strict = Open(data, new PdfOpenOptions { Strict = true });
            _ = strict.PageCount;
        });
    }

    [Fact]
    public void WrongStreamLengthIsRepairedByFindingEndstream()
    {
        var b = new PdfBuilder();
        b.BasicPages();
        byte[] content = Encoding.ASCII.GetBytes("0 0 m 100 100 l S 0 0 m 50 50 l S");
        b.Stream(4, "", content, lengthEntry: "5");
        long x = b.XrefTable(5);
        b.Trailer("/Size 5 /Root 1 0 R", x);
        byte[] data = b.ToArray();
        using PdfDocument doc = Open(data);
        var s = (PdfStream)doc.GetObject(4)!;
        Assert.Equal(content, s.GetRawBytes().ToArray());
        Assert.Equal(content, s.GetDecodedBytes().ToArray());
        Assert.Contains(doc.Diagnostics, d => d.ObjectNumber == 4 && d.Message.Contains("/Length", StringComparison.Ordinal));
        Assert.Throws<PdfFormatException>(() =>
        {
            using PdfDocument strict = Open(data, new PdfOpenOptions { Strict = true });
            _ = ((PdfStream)strict.GetObject(4)!).GetRawBytes();
        });
    }

    [Fact]
    public void MissingEndobjIsTolerated()
    {
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        b.Offsets[3] = b.Position;
        b.Write("3 0 obj\n<< /Type /Page /Parent 2 0 R >>\n");
        b.Obj(4, "<< >>");
        long x = b.XrefTable(5);
        b.Trailer("/Size 5 /Root 1 0 R", x);
        AssertRepairs(b.ToArray(), "endobj");
    }

    [Fact]
    public void TruncatedTrailerRebuilds()
    {
        var b = new PdfBuilder();
        b.BasicPages();
        long x = b.XrefTable(4);
        b.Write("trailer\n<< /Size 4 /Ro");
        AssertRepairs(b.ToArray(), "rebuilt");
    }

    [Fact]
    public void NoXrefAtAllRebuildsFromObjectHeaders()
    {
        var b = new PdfBuilder();
        b.BasicPages();
        AssertRepairs(b.ToArray(), "rebuilt");
    }

    [Fact]
    public void RebuildUsesTheLastDefinitionAndLastTrailer()
    {
        var b = new PdfBuilder();
        b.BasicPages();
        b.Write("trailer\n<< /Root 1 0 R >>\n");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R 3 0 R] /Count 2 >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R >>");
        using PdfDocument doc = Open(b.ToArray());
        PdfDictionary pages = doc.Catalog.GetDictionary("Pages")!;
        Assert.Equal(2, pages.GetArray("Kids")!.Count);
    }

    [Fact]
    public void RebuildRecoversObjectStreams()
    {
        byte[] data = XrefStreamFile(breakStartxref: true);
        using PdfDocument doc = Open(data);
        Assert.Equal(1, doc.PageCount);
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("rebuilt", StringComparison.Ordinal));
    }

    [Fact]
    public void StreamBodiesAreNotScannedForObjects()
    {
        var b = new PdfBuilder();
        b.BasicPages();
        b.Stream(4, "", Encoding.ASCII.GetBytes("1 0 obj << /Type /Catalog /Pages 99 0 R >> endobj"));
        using PdfDocument doc = Open(b.ToArray());
        Assert.Equal(1, doc.PageCount);
    }

    [Fact]
    public void GarbageIsNotAPdf()
    {
        Assert.False(PdfDocument.TryOpen(Encoding.ASCII.GetBytes("hello, this is not a pdf"), out PdfDocument? doc, out PdfException? err));
        Assert.Null(doc);
        Assert.IsType<PdfFormatException>(err);
        Assert.False(PdfDocument.TryOpen(ReadOnlyMemory<byte>.Empty, out _, out err));
    }

    // ----- cycles and odd references -----

    [Fact]
    public void PageTreeCycleTerminates()
    {
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [2 0 R 3 0 R 3 0 R] >>");
        b.Obj(3, "<< /Type /Page >>");
        long x = b.XrefTable(4);
        b.Trailer("/Size 4 /Root 1 0 R", x);
        using PdfDocument doc = Open(b.ToArray());
        Assert.Equal(1, doc.PageCount);
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("twice", StringComparison.Ordinal));
    }

    [Fact]
    public void ReferenceLoopsReadAsNull()
    {
        var b = new PdfBuilder();
        b.BasicPages();
        b.Obj(4, "5 0 R");
        b.Obj(5, "4 0 R");
        long x = b.XrefTable(6);
        b.Trailer("/Size 6 /Root 1 0 R", x);
        using PdfDocument doc = Open(b.ToArray());
        Assert.Null(doc.GetObject(4));
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("references", StringComparison.Ordinal));
    }

    [Fact]
    public void StreamLengthThatPointsAtItselfFallsBackToScanning()
    {
        var b = new PdfBuilder();
        b.BasicPages();
        b.Stream(4, "", Encoding.ASCII.GetBytes("abcdef"), lengthEntry: "4 0 R");
        long x = b.XrefTable(5);
        b.Trailer("/Size 5 /Root 1 0 R", x);
        using PdfDocument doc = Open(b.ToArray());
        Assert.Equal("abcdef", Encoding.ASCII.GetString(((PdfStream)doc.GetObject(4)!).GetRawBytes().Span));
    }

    [Fact]
    public void IndirectLengthIsResolved()
    {
        var b = new PdfBuilder();
        b.BasicPages();
        b.Stream(4, "", Encoding.ASCII.GetBytes("abcdef"), lengthEntry: "5 0 R");
        b.Obj(5, "6");
        long x = b.XrefTable(6);
        b.Trailer("/Size 6 /Root 1 0 R", x);
        using PdfDocument doc = Open(b.ToArray(), new PdfOpenOptions { Strict = true });
        Assert.Equal("abcdef", Encoding.ASCII.GetString(((PdfStream)doc.GetObject(4)!).GetRawBytes().Span));
    }

    [Fact]
    public void ExternalStreamDataIsEmptyWithDiagnostic()
    {
        var b = new PdfBuilder();
        b.BasicPages();
        b.Stream(4, "/F (other.bin)", [], lengthEntry: "0");
        long x = b.XrefTable(5);
        b.Trailer("/Size 5 /Root 1 0 R", x);
        using PdfDocument doc = Open(b.ToArray());
        var s = (PdfStream)doc.GetObject(4)!;
        Assert.True(s.IsExternal);
        Assert.Equal(0, s.GetRawBytes().Length);
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("external", StringComparison.Ordinal));
    }

    [Fact]
    public void EncryptedFilesOpenWithAnInfoNote()
    {
        var b = new PdfBuilder();
        b.BasicPages();
        b.Obj(4, "<< /Filter /Standard /V 1 /R 2 >>");
        long x = b.XrefTable(5);
        b.Trailer("/Size 5 /Root 1 0 R /Encrypt 4 0 R", x);
        using PdfDocument doc = Open(b.ToArray(), new PdfOpenOptions { Strict = true });
        Assert.True(doc.IsEncrypted);
        Assert.Contains(doc.Diagnostics, d => d.Severity == PdfSeverity.Info);
    }

    [Fact]
    public void FlateStreamsDecode()
    {
        byte[] plain = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("BT /F1 12 Tf (Hello) Tj ET\n", 50)));
        var b = new PdfBuilder();
        b.BasicPages();
        b.Stream(4, "/Filter /FlateDecode", PdfBuilder.Deflate(plain));
        long x = b.XrefTable(5);
        b.Trailer("/Size 5 /Root 1 0 R", x);
        using PdfDocument doc = Open(b.ToArray());
        var s = (PdfStream)doc.GetObject(4)!;
        Assert.Equal(["FlateDecode"], s.FilterNames);
        Assert.Null(s.UndecodedFilter);
        Assert.Equal(plain, s.GetDecodedBytes().ToArray());
        Assert.Equal(plain, s.GetDecodedBytes().ToArray());
    }

    // ----- sources -----

    [Fact]
    public async Task NonSeekableStreamsAreCopied()
    {
        byte[] data = PdfBuilder.Simple();
        using var pipe = new NonSeekable(data);
        using PdfDocument doc = await PdfDocument.OpenAsync(pipe);
        Assert.Equal(1, doc.PageCount);
        using var pipe2 = new NonSeekable(data);
        using PdfDocument doc2 = PdfDocument.Open(pipe2);
        Assert.Equal(1, doc2.PageCount);
    }

    [Fact]
    public void SeekableStreamStaysOpenWhenAsked()
    {
        using var ms = new MemoryStream(PdfBuilder.Simple());
        using (PdfDocument doc = PdfDocument.Open(ms, new PdfOpenOptions { LeaveOpen = true }))
        {
            Assert.Equal(1, doc.PageCount);
        }

        Assert.True(ms.CanRead);
        using (PdfDocument.Open(ms))
        {
        }

        Assert.False(ms.CanRead);
    }

    [Fact]
    public void ChangedFileRaisesSourceChanged()
    {
        string path = Path.Combine(Path.GetTempPath(), $"onepdf-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, PdfBuilder.Simple());
        try
        {
            using PdfDocument doc = PdfDocument.Open(path);
            File.AppendAllText(path, "\n% appended");
            Assert.Throws<PdfSourceChangedException>(() => doc.GetObject(0 + 3));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ChangedSeekableStreamRaisesSourceChanged()
    {
        var ms = new MemoryStream();
        ms.Write(PdfBuilder.Simple());
        using PdfDocument doc = PdfDocument.Open(ms, new PdfOpenOptions { LeaveOpen = true });
        ms.WriteByte(0);
        Assert.Throws<PdfSourceChangedException>(() => doc.GetObject(3));
    }

    [Fact]
    public void DisposedDocumentsThrow()
    {
        PdfDocument doc = Open(PdfBuilder.Simple());
        doc.Dispose();
        doc.Dispose();
        Assert.Throws<ObjectDisposedException>(() => doc.GetObject(3));
    }

    private sealed class NonSeekable(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
