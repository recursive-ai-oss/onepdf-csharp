using System.Text;
using OnePdf.Tests.Content;

namespace OnePdf.Tests.Writing;

public class WriterTests
{
    private static byte[] Base() => TestPdf.Build("BT /F1 12 Tf 10 100 Td (Hello) Tj ET", "/Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>");

    private static PdfDocument Edit(byte[] data, string? pw = null) => PdfDocument.Open(data, WriterSupport.Editable(pw));

    private static PdfDictionary Page(PdfDocument d) => d.Pages[0].Dictionary;

    [Fact]
    public void ReadOnlyDocumentRefusesChangesButSaves()
    {
        using PdfDocument doc = PdfDocument.Open(Base());
        Assert.False(doc.IsEditable);
        Assert.Throws<InvalidOperationException>(() => Page(doc).SetEntry("Rotate", new PdfInteger(90)));
        Assert.Throws<InvalidOperationException>(() => doc.AddObject(new PdfDictionary()));
        Assert.Throws<InvalidOperationException>(() => doc.SetInfo("Title", "x"));
        using PdfDocument again = PdfDocument.Open(WriterSupport.Save(doc));
        Assert.Equal(1, again.PageCount);
    }

    [Theory]
    [InlineData(PdfXrefFormat.Table)]
    [InlineData(PdfXrefFormat.Stream)]
    public void ChangesAndNewObjectsSurviveFullSave(PdfXrefFormat form)
    {
        using PdfDocument doc = Edit(Base());
        Page(doc).SetEntry("Rotate", new PdfInteger(90));
        PdfStream data = doc.CreateStream(Encoding.ASCII.GetBytes(new string('x', 500)), compress: true);
        var arr = new PdfArray();
        arr.Append(new PdfReal(1.5));
        arr.Append(new PdfName("A B#(x)"));
        arr.Append(new PdfString([0, 1, 2, 40, 41, 92, 255]));
        arr.Append(new PdfBoolean(true));
        arr.Append(data); // a loose stream joins the document
        PdfReference r = doc.AddObject(arr);
        doc.Catalog.SetEntry("Custom", r);
        doc.Catalog.SetEntry("Direct", new PdfDictionary());
        ((PdfDictionary)doc.Catalog.Get("Direct")!).SetEntry("K", new PdfInteger(7));
        doc.SetInfo("Title", "Caf\u00e9 \u4e2d\u6587");
        doc.SetXmp(Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"));

        byte[] bytes = WriterSupport.Save(doc, new PdfSaveOptions { XrefFormat = form });
        Assert.Null(Qpdf(bytes));
        using PdfDocument back = PdfDocument.Open(bytes);
        Assert.Equal(90, Page(back).GetInt("Rotate"));
        PdfArray a = back.Catalog.GetArray("Custom")!;
        Assert.Equal(1.5, a.GetDouble(0));
        Assert.Equal("A B#(x)", a.GetName(1));
        Assert.Equal(new byte[] { 0, 1, 2, 40, 41, 92, 255 }, ((PdfString)a[2]!).Bytes.ToArray());
        Assert.True(((PdfBoolean)a[3]!).Value);
        Assert.Equal(new string('x', 500), Encoding.ASCII.GetString(((PdfStream)a[4]!).GetDecodedBytes().Span));
        Assert.Equal(7, back.Catalog.GetDictionary("Direct")!.GetInt("K"));
        Assert.Equal("Caf\u00e9 \u4e2d\u6587", back.Info.Title);
        Assert.Contains("xmpmeta", Encoding.UTF8.GetString(back.Catalog.GetStream("Metadata")!.GetDecodedBytes().Span));
    }

    private static string? Qpdf(byte[] bytes, string? pw = null)
    {
        (int code, string text) = WriterSupport.QpdfRun(bytes, pw);
        return code == 0 ? null : text;
    }

    [Fact]
    public void UnreachableObjectsAreDroppedAndNumbersAreCompact()
    {
        using PdfDocument doc = Edit(Base());
        doc.AddObject(new PdfDictionary()); // never referenced
        byte[] bytes = WriterSupport.Save(doc, new PdfSaveOptions { XrefFormat = PdfXrefFormat.Table });
        using PdfDocument back = PdfDocument.Open(bytes);
        Assert.Equal(5, back.ObjectCount); // 0, catalog, pages, page, contents
    }

    [Fact]
    public void ObjectStreamsPackNonStreamObjectsAndKeepTheCatalogPlain()
    {
        using PdfDocument doc = PdfDocument.Open(PdfBuilder.Replace(Base(), "%PDF-1.7", "%PDF-1.3"));
        byte[] bytes = WriterSupport.Save(doc, new PdfSaveOptions { XrefFormat = PdfXrefFormat.Stream });
        string text = Encoding.Latin1.GetString(bytes);
        Assert.Contains("/Type/ObjStm", text);
        Assert.Contains("/Type/XRef", text);
        Assert.Contains("/Type/Catalog", text); // stored as an ordinary object, not inside the object stream
        using PdfDocument back = PdfDocument.Open(bytes);
        Assert.True(back.Revisions[0].IsXrefStream);
        Assert.StartsWith("1.5", back.Version);
    }

    [Fact]
    public void IncrementalSaveAppendsOnlyTheChangedObject()
    {
        byte[] original = Base();
        using PdfDocument doc = Edit(original);
        Page(doc).SetEntry("Rotate", new PdfInteger(180));
        byte[] bytes = WriterSupport.SaveIncremental(doc);

        Assert.True(bytes.AsSpan(0, original.Length).SequenceEqual(original));
        string appended = Encoding.Latin1.GetString(bytes, original.Length, bytes.Length - original.Length);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(appended, @"\d+ \d+ obj"));
        Assert.Contains("/Prev ", appended);
        Assert.Null(Qpdf(bytes));

        using PdfDocument back = PdfDocument.Open(bytes);
        Assert.Equal(2, back.Revisions.Count);
        Assert.Equal(180, Page(back).GetInt("Rotate"));
        Assert.Equal(0, back.Revisions[0].GetObject(3) is PdfDictionary d0 ? d0.GetInt("Rotate") : -1);
        Assert.Equal(180, ((PdfDictionary)back.Revisions[1].GetObject(3)!).GetInt("Rotate"));
        Assert.True(WriterSupport.AllPdfs().Any()); // corpus present
        Assert.True(CorpusRoundTripTests.PigOpens(bytes, null));
    }

    [Theory]
    [InlineData("testdata/generated/pdf/chrome-links-outline-objstm.pdf")]
    [InlineData("testdata/realworld/pdf/report-01-gov000331.pdf")]
    [InlineData("testdata/realworld/pdf/cjk-01-zen14166681.pdf")]
    [InlineData("testdata/realworld/pdf/form-06-gov882412.pdf")]
    public void IncrementalSaveOnXrefStreamAndHybridFiles(string relative)
    {
        byte[] original = File.ReadAllBytes(Path.Combine(WriterSupport.Root(), relative));
        using PdfDocument doc = PdfDocument.Open(original, WriterSupport.Editable(WriterSupport.PasswordFor(Path.Combine(WriterSupport.Root(), relative))));
        int before = doc.Revisions.Count;
        doc.SetInfo("Title", "Edited title");
        doc.Catalog.SetEntry("Lang", PdfString.FromText("fr"));
        byte[] bytes = WriterSupport.SaveIncremental(doc);
        Assert.True(bytes.AsSpan(0, original.Length).SequenceEqual(original));
        WriterSupport.AssertNoWorse(original, bytes, WriterSupport.PasswordFor(Path.Combine(WriterSupport.Root(), relative)), relative);
        using PdfDocument back = PdfDocument.Open(bytes, new PdfOpenOptions { Password = WriterSupport.PasswordFor(Path.Combine(WriterSupport.Root(), relative)) });
        Assert.Equal(before + 1, back.Revisions.Count);
        Assert.Equal(doc.Revisions[^1].IsXrefStream, back.Revisions[^1].IsXrefStream);
        Assert.Equal("Edited title", back.Info.Title);
        Assert.Equal("fr", back.Language);
        Assert.Equal(doc.PageCount, back.PageCount);
    }

    [Fact]
    public void IncrementalRemoveFreesTheObject()
    {
        byte[] original = Base();
        using PdfDocument doc = Edit(original);
        PdfDictionary page = Page(doc);
        int contents = page.GetRaw("Contents") is PdfReference cr ? cr.Number : throw new InvalidOperationException();
        doc.RemoveObject(contents);
        byte[] bytes = WriterSupport.SaveIncremental(doc);
        using PdfDocument back = PdfDocument.Open(bytes);
        Assert.Null(back.GetObject(contents));
        Assert.NotNull(back.Revisions[0].GetObject(contents));
        Assert.Null(Qpdf(bytes));
    }

    [Fact]
    public void IncrementalSaveRefusesARebuiltFileButFullSaveCleansIt()
    {
        byte[] broken = PdfBuilder.Replace(Base(), "startxref\n", "startxref\n9");
        using PdfDocument doc = PdfDocument.Open(broken, WriterSupport.Editable());
        Assert.NotEmpty(doc.Diagnostics);
        Assert.Throws<PdfFormatException>(() => WriterSupport.SaveIncremental(doc));
        byte[] clean = WriterSupport.Save(doc);
        using PdfDocument back = PdfDocument.Open(clean);
        Assert.Equal(1, back.PageCount);
        Assert.DoesNotContain(back.Diagnostics, d => d.Severity >= PdfSeverity.Warning);
        Assert.Null(Qpdf(clean));
    }

    [Theory]
    [InlineData(PdfCipher.Aes128)]
    [InlineData(PdfCipher.Aes256)]
    public void NewEncryptionOpensWithUserAndOwnerPasswords(PdfCipher cipher)
    {
        using PdfDocument doc = PdfDocument.Open(Base());
        var enc = new PdfEncryptionOptions { UserPassword = "user pw", OwnerPassword = "owner pw", Cipher = cipher, Permissions = PdfPermissions.Print | PdfPermissions.CopyContent };
        foreach (PdfXrefFormat form in new[] { PdfXrefFormat.Table, PdfXrefFormat.Stream })
        {
            byte[] bytes = WriterSupport.Save(doc, new PdfSaveOptions { XrefFormat = form, Encryption = PdfEncryptionAction.Set, NewEncryption = enc });
            Assert.DoesNotContain("Hello", Encoding.Latin1.GetString(bytes));
            Assert.Null(Qpdf(bytes, "user pw"));
            Assert.Null(Qpdf(bytes, "owner pw"));
            Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(bytes));
            Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(bytes, new PdfOpenOptions { Password = "nope" }));
            using PdfDocument u = PdfDocument.Open(bytes, new PdfOpenOptions { Password = "user pw" });
            Assert.Equal(PdfAccessLevel.User, u.AccessLevel);
            Assert.Equal(PdfPermissions.Print | PdfPermissions.CopyContent, u.Permissions);
            Assert.Equal(cipher, u.Encryption!.Cipher);
            Assert.Contains("Hello", u.ExtractText()[0].GetText(OnePdf.Text.PdfTextMode.ReadingOrder));
            using PdfDocument o = PdfDocument.Open(bytes, new PdfOpenOptions { Password = "owner pw" });
            Assert.Equal(PdfAccessLevel.Owner, o.AccessLevel);
            Assert.True(CorpusRoundTripTests.PigOpens(bytes, "user pw"));
        }
    }

    [Fact]
    public void EncryptionCanBeKeptRemovedAndEditedIncrementally()
    {
        using PdfDocument plain = PdfDocument.Open(Base());
        byte[] enc = WriterSupport.Save(plain, new PdfSaveOptions { Encryption = PdfEncryptionAction.Set, NewEncryption = new PdfEncryptionOptions { UserPassword = "u", Cipher = PdfCipher.Aes256, EncryptMetadata = false } });

        using (PdfDocument doc = Edit(enc, "u"))
        {
            doc.SetInfo("Title", "Secret title");
            byte[] inc = WriterSupport.SaveIncremental(doc);
            Assert.True(inc.AsSpan(0, enc.Length).SequenceEqual(enc));
            Assert.DoesNotContain("Secret title", Encoding.Latin1.GetString(inc));
            Assert.Null(Qpdf(inc, "u"));
            using PdfDocument back = PdfDocument.Open(inc, new PdfOpenOptions { Password = "u" });
            Assert.True(back.IsEncrypted);
            Assert.Equal("Secret title", back.Info.Title);
            Assert.Contains("Hello", back.ExtractText()[0].GetText(OnePdf.Text.PdfTextMode.ReadingOrder));

            byte[] kept = WriterSupport.Save(back);
            using PdfDocument again = PdfDocument.Open(kept, new PdfOpenOptions { Password = "u" });
            Assert.True(again.IsEncrypted);
            Assert.Equal("Secret title", again.Info.Title);

            byte[] removed = WriterSupport.Save(back, new PdfSaveOptions { Encryption = PdfEncryptionAction.Remove });
            using PdfDocument open = PdfDocument.Open(removed);
            Assert.False(open.IsEncrypted);
            Assert.Equal("Secret title", open.Info.Title);
            Assert.Throws<ArgumentException>(() => WriterSupport.SaveIncremental(back, new PdfSaveOptions { Encryption = PdfEncryptionAction.Remove }));
        }
    }

    [Fact]
    public void DeepNestingIsRefusedWithoutRecursion()
    {
        using PdfDocument doc = Edit(Base());
        PdfArray inner = new();
        for (int i = 0; i < 50_000; i++)
        {
            var outer = new PdfArray();
            outer.Append(inner);
            inner = outer;
        }

        doc.Catalog.SetEntry("Deep", inner); // adopting 50,000 levels walks an explicit stack
        Assert.Throws<PdfLimitException>(() => WriterSupport.Save(doc));
        Assert.Throws<PdfLimitException>(() => WriterSupport.SaveIncremental(doc));
    }

    [Fact]
    public void ReferenceCyclesAndSharedObjectsSave()
    {
        using PdfDocument doc = Edit(Base());
        var a = new PdfDictionary();
        PdfReference ra = doc.AddObject(a);
        a.SetEntry("Self", ra);
        a.SetEntry("Page", Page(doc)); // an indirect object is stored by reference
        Assert.IsType<PdfReference>(a.GetRaw("Page"));
        doc.Catalog.SetEntry("Cyc", ra);
        using PdfDocument back = PdfDocument.Open(WriterSupport.Save(doc));
        PdfDictionary c = back.Catalog.GetDictionary("Cyc")!;
        Assert.Same(c, c.GetDictionary("Self"));
    }

    [Fact]
    public void ValuesOfAnotherDocumentAreRefused()
    {
        using PdfDocument one = Edit(Base());
        using PdfDocument two = Edit(Base());
        Assert.Throws<ArgumentException>(() => one.Catalog.SetEntry("X", Page(two)));
    }

    [Fact]
    public void ReadersKeepWorkingWhileTheCacheEvicts()
    {
        // A tiny cache would drop edited objects if they were not pinned.
        using PdfDocument doc = PdfDocument.Open(File.ReadAllBytes(WriterSupport.Generated("chrome-table-en.pdf")), new PdfOpenOptions { AllowEditing = true, ObjectCacheBytes = 1 });
        PdfDictionary page = Page(doc);
        page.SetEntry("Marker", new PdfInteger(42));
        for (int i = 1; i < doc.ObjectCount; i++)
        {
            _ = doc.GetObject(i);
        }

        Assert.Equal(42, Page(doc).GetInt("Marker"));
        using PdfDocument back = PdfDocument.Open(WriterSupport.Save(doc));
        Assert.Equal(42, Page(back).GetInt("Marker"));
    }

    [Fact]
    public void ParallelReadersOnAnEditableDocument()
    {
        using PdfDocument doc = PdfDocument.Open(File.ReadAllBytes(WriterSupport.Generated("chrome-table-en.pdf")), WriterSupport.Editable());
        doc.SetInfo("Title", "t");
        for (int round = 0; round < 5; round++)
        {
            Parallel.For(1, doc.ObjectCount, i =>
            {
                PdfObject? o = doc.GetObject(i);
                Assert.Same(o, doc.GetObject(i));
            });
        }
    }

    [Fact]
    public void RealNumbersAreWrittenWithoutExponents()
    {
        using PdfDocument doc = Edit(Base());
        var arr = new PdfArray();
        foreach (double v in new[] { 1e-7, 123456789012.5, -0.0, 0.1 + 0.2, 1e20 })
        {
            arr.Append(new PdfReal(v));
        }

        doc.Catalog.SetEntry("R", arr);
        byte[] bytes = WriterSupport.Save(doc, new PdfSaveOptions { XrefFormat = PdfXrefFormat.Table });
        Assert.DoesNotContain("E", Encoding.Latin1.GetString(bytes).Split("/R[")[1].Split(']')[0]);
        using PdfDocument back = PdfDocument.Open(bytes);
        PdfArray r = back.Catalog.GetArray("R")!;
        Assert.Equal(123456789012.5, r.GetDouble(1));
        Assert.Equal(0.3, r.GetDouble(3), 12);
        Assert.Equal(1e20, r.GetDouble(4));
    }

    [Fact]
    public void CompressStreamsShrinksUnfilteredStreamsOnly()
    {
        byte[] data = TestPdf.Build(new string('a', 4000));
        using PdfDocument doc = PdfDocument.Open(data);
        byte[] plain = WriterSupport.Save(doc);
        byte[] packed = WriterSupport.Save(doc, new PdfSaveOptions { CompressStreams = true });
        Assert.True(packed.Length < plain.Length - 3000);
        using PdfDocument back = PdfDocument.Open(packed);
        Assert.Equal(new string('a', 4000), Encoding.ASCII.GetString(((PdfStream)back.GetObject(4)!).GetDecodedBytes().Span));
    }

    [Fact]
    public void SaveTargetReprosStayFixed()
    {
        // The fuzz target body: edit, save in every form, reopen. A failure throws.
        foreach (string f in Directory.EnumerateFiles(Path.Combine(WriterSupport.Root(), "testdata", "repros"), "save-*.pdf"))
        {
            OnePdf.Fuzz.SaveTarget.Run(File.ReadAllBytes(f));
        }
    }

    [Fact]
    public void AddObjectNeverHandsOutObjectZero()
    {
        // A mutated test file whose cross-reference data lists no object above 0 (found by the save fuzz target). It opens but has no
        // catalog, so it is kept here rather than among the files every other test expects to read.
        byte[] broken = Convert.FromBase64String("JVBERi0xLjcKJeLjz9MKMSYgMCAOYmoKPDwgL1R5cGUgL0NhdGFsb2cgL1BhZ2VzIDIgMCBSID4+L1KUnIvfpM7P3zEwIDE5MCA2MF0gL1ZlcnRpnZqM36TOz+kxMCA5MCA1MCAxOTAgMjBdIF1MRSBbXSAvUCAzIDAgUiA+PgplbmRvYmoKeHJlZgowIDYKMDAwMDAwMDAwMCA2NTUzJSBmIAowMDAwMDAwMDE1IDAwMDAwIG4gCjAwMDAwMDAwNjQgMDAwMDAgbiAKMDAwMDAwMDEyMSAwMDAwMCBuIAowMDAwMDAwMjE0IDAwMDAwIG5wZSAvQW5ub3QgL1N1YnR5cGUgL0wKdHJhaWxlcgo8PCAvU2l6ZSA2IC9Sb290IDEgMCBSID4+CnN0YXJ0eHJlZgo0NTkKJSVFT0YK");
        using PdfDocument doc = Edit(broken);
        Assert.True(doc.AddObject(new PdfDictionary()).Number >= 1);
        OnePdf.Fuzz.SaveTarget.Run(broken);
    }

    [Fact]
    public void IncrementalSaveKeepsOffsetsRelativeToTheHeaderWhenJunkComesFirst()
    {
        byte[] original = File.ReadAllBytes(WriterSupport.Generated("raw-broken-junk-before-header.pdf"));
        using PdfDocument doc = Edit(original);
        doc.SetInfo("Title", "Edited");
        byte[] bytes = WriterSupport.SaveIncremental(doc);
        Assert.True(bytes.AsSpan(0, original.Length).SequenceEqual(original));
        using PdfDocument back = PdfDocument.Open(bytes);
        Assert.Equal(2, back.Revisions.Count);
        Assert.Equal("Edited", back.Info.Title);
        Assert.Equal(doc.PageCount, back.PageCount);
    }

    [Fact]
    public void GeneratedAppearanceCanBeStoredAsARealAppearanceAndSaved()
    {
        using PdfDocument doc = PdfDocument.Open(WriterSupport.Generated("raw-appearance-widgets.pdf"), WriterSupport.Editable());
        PdfAnnotation annot = doc.Pages[0].Annotations[0];
        PdfGeneratedAppearance g = annot.GenerateAppearance()!;
        PdfStream form = doc.CreateStream(g.Content.Span);
        foreach (KeyValuePair<string, PdfObject> kv in g.Dictionary.Entries)
        {
            if (kv.Key is not ("Length" or "Filter" or "DecodeParms"))
            {
                form.Dictionary.SetEntry(kv.Key, kv.Value);
            }
        }

        var ap = new PdfDictionary();
        ap.SetEntry("N", doc.AddObject(form));
        annot.Dictionary.SetEntry("AP", ap);
        using PdfDocument back = PdfDocument.Open(WriterSupport.Save(doc));
        PdfStream? stored = back.Pages[0].Annotations[0].GetAppearanceStream();
        Assert.NotNull(stored);
        Assert.Contains("(Hello World) Tj", Encoding.Latin1.GetString(stored!.GetDecodedBytes().Span));
    }
}
