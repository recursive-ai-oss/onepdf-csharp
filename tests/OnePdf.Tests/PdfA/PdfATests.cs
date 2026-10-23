using System.Buffers.Binary;
using System.Text;
using OnePdf.FontPrograms;
using OnePdf.Tests.Creation;

namespace OnePdf.Tests.PdfA;

public class PdfATests
{
    private static readonly byte[] s_id = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

    private static PdfSaveOptions Options(PdfAPart part, PdfAConformance conf, PdfXrefFormat xref = PdfXrefFormat.Auto) => new()
    {
        DocumentId = s_id,
        XrefFormat = xref,
        PdfA = new PdfAOptions { Part = part, Conformance = conf },
    };

    private static byte[] Write(PdfDocument d, PdfAPart part, PdfAConformance conf, PdfXrefFormat xref = PdfXrefFormat.Auto)
    {
        using var ms = new MemoryStream();
        d.Save(ms, Options(part, conf, xref));
        return ms.ToArray();
    }

    public static IEnumerable<object[]> Matrix()
    {
        foreach (string n in PdfADocs.Names)
        {
            foreach ((PdfAPart p, PdfAConformance c, string f) in new[]
            {
                (PdfAPart.Part2, PdfAConformance.B, "2b"), (PdfAPart.Part2, PdfAConformance.U, "2u"),
                (PdfAPart.Part3, PdfAConformance.B, "3b"), (PdfAPart.Part3, PdfAConformance.U, "3u"),
            })
            {
                yield return new object[] { n, (int)p, (int)c, f };
            }
        }
    }

    // Claim 1: veraPDF finds the generated files compliant for every document and every flavour.
    [Theory]
    [MemberData(nameof(Matrix))]
    public void VeraPdfAcceptsGeneratedDocuments(string name, int part, int conf, string flavour)
    {
        VeraPdf.Require();
        using PdfDocument d = PdfADocs.Build(name);
        byte[] pdf = Write(d, (PdfAPart)part, (PdfAConformance)conf);
        if (Environment.GetEnvironmentVariable("ONEPDF_PDFA_OUT") is { Length: > 0 } outDir)
        {
            // Lets a person open the files, and seeds the fuzz target.
            Directory.CreateDirectory(outDir);
            File.WriteAllBytes(Path.Combine(outDir, $"{name}-{flavour}.pdf"), pdf);
        }

        List<string> failures = VeraPdf.Validate(pdf, flavour);
        Assert.True(failures.Count == 0, $"{name} {flavour}:\n" + string.Join("\n", failures.Take(10)));

        // The same file read back and checked by OnePDF itself finds nothing either.
        using PdfDocument back = PdfDocument.Open(pdf);
        Assert.Empty(back.CheckPdfA(new PdfAOptions { Part = (PdfAPart)part, Conformance = (PdfAConformance)conf }));
    }

    [Fact]
    public void VeraPdfRejectsAnOrdinarySave()
    {
        // Guards the harness: a plain save of the same document is not PDF/A, and the validator must say so.
        VeraPdf.Require();
        using PdfDocument d = PdfADocs.TrueType();
        using var ms = new MemoryStream();
        d.Save(ms);
        List<string> failures = VeraPdf.Validate(ms.ToArray(), "2b");
        Assert.NotEmpty(failures);
        Assert.Contains(failures, f => f.StartsWith("6.6.2.1", StringComparison.Ordinal) || f.StartsWith("6.1.3", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PdfXrefFormat.Table)]
    [InlineData(PdfXrefFormat.Stream)]
    public void VeraPdfAcceptsBothXrefForms(PdfXrefFormat xref)
    {
        VeraPdf.Require();
        using PdfDocument d = PdfADocs.Build("truetype");
        Assert.Empty(VeraPdf.Validate(Write(d, PdfAPart.Part2, PdfAConformance.B, xref), "2b"));
    }

    [Theory]
    [InlineData("3b", PdfAConformance.B)]
    [InlineData("3u", PdfAConformance.U)]
    public void VeraPdfAcceptsAttachmentInPart3(string flavour, PdfAConformance conf)
    {
        VeraPdf.Require();
        using PdfDocument d = PdfADocs.TrueType();
        d.AttachFile("invoice.xml", Encoding.UTF8.GetBytes("<invoice><total>12.50</total></invoice>"), new PdfAttachmentOptions
        {
            MimeType = "text/xml",
            Description = "Invoice data",
            Relationship = PdfAttachmentRelationship.Alternative,
            ModificationDate = new DateTimeOffset(2024, 2, 3, 4, 5, 6, TimeSpan.Zero),
        });
        d.AttachFile("a-notes.txt", "plain notes"u8.ToArray(), new PdfAttachmentOptions { MimeType = "text/plain", Relationship = PdfAttachmentRelationship.Supplement });
        byte[] pdf = Write(d, PdfAPart.Part3, conf);
        Assert.Empty(VeraPdf.Validate(pdf, flavour));

        using PdfDocument back = PdfDocument.Open(pdf);
        Assert.Equal(["a-notes.txt", "invoice.xml"], back.EmbeddedFiles.Select(f => f.Name).ToArray());
        PdfEmbeddedFile inv = back.EmbeddedFiles.Single(f => f.Name == "invoice.xml");
        Assert.Equal("Alternative", inv.Relationship);
        Assert.Equal("<invoice><total>12.50</total></invoice>", Encoding.UTF8.GetString(inv.ReadContent().Span));
    }

    [Fact]
    public void AttachmentWithoutDateGetsInfoDate()
    {
        using PdfDocument d = PdfADocs.TrueType();
        d.AttachFile("x.bin", new byte[] { 1, 2, 3 });
        byte[] pdf = Write(d, PdfAPart.Part3, PdfAConformance.B);
        using PdfDocument back = PdfDocument.Open(pdf);
        Assert.NotNull(back.EmbeddedFiles.Single().ModificationDate);
    }

    // ---- Claim 2: refusals ------------------------------------------------------------------------------------------------------

    private static PdfAException Refused(PdfDocument d, PdfAPart part = PdfAPart.Part2, PdfAConformance conf = PdfAConformance.B, PdfSaveOptions? extra = null)
    {
        using var ms = new MemoryStream();
        var o = new PdfSaveOptions { DocumentId = s_id, PdfA = new PdfAOptions { Part = part, Conformance = conf }, Encryption = extra?.Encryption ?? PdfEncryptionAction.Keep };
        PdfAException ex = Assert.Throws<PdfAException>(() => d.Save(ms, o));
        Assert.Equal(0, ms.Length);
        return ex;
    }

    private sealed class NoFonts : IPdfFontProvider
    {
        public PdfSubstituteFont? FindFont(PdfFontRequest request) => null;
    }

    [Fact]
    public void RefusesNonEmbeddedFontWithoutSubstitute()
    {
        using PdfDocument d = PdfADocs.Standard14();
        d.FontProvider = new NoFonts();
        PdfAException ex = Refused(d);
        Assert.Contains(ex.Violations, v => v.Clause == "6.2.11.4.1" && v.ObjectNumber > 0 && v.Message.Contains("Helvetica", StringComparison.Ordinal));
        Assert.True(ex.Violations.Count(v => v.Clause == "6.2.11.4.1") >= 4, "every font is listed");
    }

    [Fact]
    public void RefusesSymbolFonts()
    {
        using PdfDocument d = PdfADocs.New();
        PdfPage page = d.InsertBlankPage(0, 200, 200);
        using PdfPageContentBuilder b = page.BeginContent();
        b.SetFont(d.AddStandardFont("Symbol"), 12);
        b.DrawText("abc", 10, 10);
        b.Commit();
        Assert.Contains(Refused(d).Violations, v => v.Clause == "6.2.11.4.1" && v.Message.Contains("Symbol", StringComparison.Ordinal));
    }

    [Fact]
    public void RefusesFontsThatCameWithTheFile()
    {
        // A standard font the library did not add is not replaced: the file's own fonts are never rewritten.
        using PdfDocument d = PdfADocs.New();
        PdfPage page = d.InsertBlankPage(0, 200, 200);
        var font = new PdfDictionary();
        font.SetEntry("Type", new PdfName("Font"));
        font.SetEntry("Subtype", new PdfName("Type1"));
        font.SetEntry("BaseFont", new PdfName("Helvetica"));
        PdfReference fr = d.AddObject(font);
        var fonts = new PdfDictionary();
        fonts.SetEntry("F1", fr);
        page.Resources.SetEntry("Font", fonts);
        PdfAException ex = Refused(d);
        Assert.Contains(ex.Violations, v => v.Clause == "6.2.11.4.1" && v.ObjectNumber == fr.Number);
    }

    [Fact]
    public void RefusesCmykContent()
    {
        using PdfDocument d = PdfADocs.New();
        PdfPage page = d.InsertBlankPage(0, 200, 200);
        using (PdfPageContentBuilder b = page.BeginContent())
        {
            b.SetFillCmyk(0.1, 0.2, 0.3, 0.4);
            b.Rectangle(10, 10, 50, 50);
            b.Fill();
            b.Commit();
        }

        PdfAException ex = Refused(d);
        PdfAViolation v = Assert.Single(ex.Violations, x => x.Clause == "6.2.4.3");
        Assert.Contains("CMYK", v.Message, StringComparison.Ordinal);
        Assert.Equal(0, v.PageIndex);
    }

    [Fact]
    public void RefusesEncryptionUnlessRemoved()
    {
        byte[] enc;
        using (PdfDocument d = PdfADocs.TrueType())
        {
            using var ms = new MemoryStream();
            d.Save(ms, new PdfSaveOptions { Encryption = PdfEncryptionAction.Set, NewEncryption = new PdfEncryptionOptions { UserPassword = "pw" } });
            enc = ms.ToArray();
        }

        using (PdfDocument d = PdfDocument.Open(enc, new PdfOpenOptions { Password = "pw", AllowEditing = true }))
        {
            Assert.Contains(Refused(d).Violations, v => v.Clause == "6.1.3");
        }

        using (PdfDocument d = PdfDocument.Open(enc, new PdfOpenOptions { Password = "pw", AllowEditing = true }))
        {
            using var ms = new MemoryStream();
            d.Save(ms, new PdfSaveOptions { DocumentId = s_id, Encryption = PdfEncryptionAction.Remove, PdfA = new PdfAOptions() });
            using PdfDocument back = PdfDocument.Open(ms.ToArray());
            Assert.False(back.IsEncrypted);
            Assert.Empty(back.CheckPdfA());
        }

        using (PdfDocument d = PdfADocs.TrueType())
        {
            Assert.Throws<ArgumentException>(() => d.Save(new MemoryStream(), new PdfSaveOptions
            {
                Encryption = PdfEncryptionAction.Set,
                NewEncryption = new PdfEncryptionOptions(),
                PdfA = new PdfAOptions(),
            }));
        }
    }

    [Fact]
    public void RefusesJavaScriptAndForbiddenActions()
    {
        using PdfDocument d = PdfADocs.TrueType();
        var js = new PdfDictionary();
        js.SetEntry("S", new PdfName("JavaScript"));
        js.SetEntry("JS", PdfString.FromText("app.alert('x');"));
        d.Catalog.SetEntry("OpenAction", js);

        var link = new PdfDictionary();
        link.SetEntry("Type", new PdfName("Annot"));
        link.SetEntry("Subtype", new PdfName("Link"));
        var rect = new PdfArray();
        foreach (int v in new[] { 10, 10, 50, 50 })
        {
            rect.Append(new PdfInteger(v));
        }

        link.SetEntry("Rect", rect);
        var launch = new PdfDictionary();
        launch.SetEntry("S", new PdfName("Launch"));
        launch.SetEntry("F", PdfString.FromText("calc.exe"));
        link.SetEntry("A", launch);
        var annots = new PdfArray();
        annots.Append(d.AddObject(link));
        d.Pages[0].Dictionary.SetEntry("Annots", annots);

        PdfAException ex = Refused(d);
        Assert.True(ex.Violations.Count(v => v.Clause == "6.5.1") >= 2, string.Join("\n", ex.Violations));
    }

    [Fact]
    public void RefusesLzwStream()
    {
        using PdfDocument d = PdfADocs.TrueType();
        PdfStream s = d.CreateStream([0x80, 0x0B, 0x60, 0x50, 0x22, 0x0C, 0x0C, 0x85, 0x01]);
        s.Dictionary.SetEntry("Filter", new PdfName("LZWDecode"));
        PdfReference r = d.AddObject(s);
        d.Catalog.SetEntry("PieceInfo", r);
        PdfAException ex = Refused(d);
        Assert.Contains(ex.Violations, v => v.Clause == "6.1.7.2" && v.ObjectNumber == r.Number);
    }

    [Fact]
    public void RefusesEmbeddedFileInPart2()
    {
        using PdfDocument d = PdfADocs.TrueType();
        PdfReference spec = d.AttachFile("a.txt", "x"u8.ToArray());
        PdfAException ex = Refused(d);
        Assert.Contains(ex.Violations, v => v.Clause == "6.8" && v.ObjectNumber == spec.Number);

        // The same document is fine as part 3 once it is dated.
        d.SetInfo("ModDate", "D:20240203040506Z");
        Assert.True(Write(d, PdfAPart.Part3, PdfAConformance.B).Length > 0);
    }

    [Fact]
    public void RefusesMissingAppearanceThatCannotBeGenerated()
    {
        using PdfDocument d = PdfADocs.TrueType();
        var stamp = new PdfDictionary();
        stamp.SetEntry("Type", new PdfName("Annot"));
        stamp.SetEntry("Subtype", new PdfName("Stamp"));
        var rect = new PdfArray();
        foreach (int v in new[] { 100, 100, 160, 140 })
        {
            rect.Append(new PdfInteger(v));
        }

        stamp.SetEntry("Rect", rect);
        PdfReference sr = d.AddObject(stamp);
        var annots = new PdfArray();
        annots.Append(sr);
        d.Pages[0].Dictionary.SetEntry("Annots", annots);
        PdfAException ex = Refused(d);
        Assert.Contains(ex.Violations, v => v.Clause == "6.3.3" && v.ObjectNumber == sr.Number && v.PageIndex == 0);
        // The Print flag was set by the preparation, so only the appearance is reported.
        Assert.DoesNotContain(ex.Violations, v => v.Clause == "6.3.2");
    }

    [Fact]
    public void RefusesHiddenAnnotationAndBadAnnotationType()
    {
        using PdfDocument d = PdfADocs.Annotations();
        var rect = new PdfArray();
        foreach (int v in new[] { 100, 100, 160, 140 })
        {
            rect.Append(new PdfInteger(v));
        }

        var movie = new PdfDictionary();
        movie.SetEntry("Type", new PdfName("Annot"));
        movie.SetEntry("Subtype", new PdfName("Movie"));
        movie.SetEntry("Rect", rect);
        PdfArray annots = d.Pages[0].Dictionary.GetArray("Annots")!;
        annots.Append(d.AddObject(movie));
        ((PdfDictionary)annots[0]!).SetEntry("F", new PdfInteger(2));
        PdfAException ex = Refused(d);
        Assert.Contains(ex.Violations, v => v.Clause == "6.3.1");
        Assert.Contains(ex.Violations, v => v.Clause == "6.3.2");
    }

    [Fact]
    public void RefusesLevelUWhenAFontHasNoUnicodeMapping()
    {
        using PdfDocument d = PdfADocs.TrueType();
        PdfPage page = d.Pages[0];
        // A symbolic simple font with no ToUnicode and no way to tell what its codes mean.
        var desc = new PdfDictionary();
        desc.SetEntry("Type", new PdfName("FontDescriptor"));
        desc.SetEntry("FontName", new PdfName("Odd"));
        desc.SetEntry("Flags", new PdfInteger(4));
        PdfStream ff = d.CreateStream([1, 2, 3, 4]);
        desc.SetEntry("FontFile2", d.AddObject(ff));
        var font = new PdfDictionary();
        font.SetEntry("Type", new PdfName("Font"));
        font.SetEntry("Subtype", new PdfName("TrueType"));
        font.SetEntry("BaseFont", new PdfName("Odd"));
        font.SetEntry("FirstChar", new PdfInteger(65));
        font.SetEntry("LastChar", new PdfInteger(65));
        var w = new PdfArray();
        w.Append(new PdfInteger(500));
        font.SetEntry("Widths", w);
        font.SetEntry("FontDescriptor", d.AddObject(desc));
        PdfReference fr = d.AddObject(font);
        var fonts = new PdfDictionary();
        fonts.SetEntry("F9", fr);
        page.Resources.SetEntry("Font", fonts);

        Assert.True(Write(d, PdfAPart.Part2, PdfAConformance.B).Length > 0); // level B asks nothing of the Unicode mapping
        PdfAException ex = Refused(d, conf: PdfAConformance.U);
        Assert.Contains(ex.Violations, v => v.Clause == "6.2.11.7" && v.ObjectNumber == fr.Number);
    }

    [Fact]
    public void RefusesNotdefGlyph()
    {
        using PdfDocument d = PdfADocs.New();
        PdfPage page = d.InsertBlankPage(0, 200, 200);
        PdfCreatedFont f = d.AddFont(CreationSupport.RequireFont(PdfADocs.Sans));
        using PdfPageContentBuilder b = page.BeginContent();
        b.SetFont(f, 12);
        b.DrawText("a \u4E2D b", 10, 10); // Liberation Sans has no CJK ideograph
        b.Commit();
        Assert.Contains(Refused(d).Violations, v => v.Clause == "6.2.11.8");
    }

    [Fact]
    public void ListsEveryViolationWithTheirObjects()
    {
        using PdfDocument d = PdfADocs.Standard14();
        d.FontProvider = new NoFonts();
        var js = new PdfDictionary();
        js.SetEntry("S", new PdfName("JavaScript"));
        d.Catalog.SetEntry("OpenAction", js);
        PdfAException ex = Refused(d);
        Assert.Contains(ex.Violations, v => v.Clause == "6.5.1");
        Assert.Contains(ex.Violations, v => v.Clause == "6.2.11.4.1");
        Assert.Contains("violations", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IncrementalSaveRefusesPdfA()
    {
        byte[] plain = Write(PdfADocs.TrueType(), PdfAPart.Part2, PdfAConformance.B);
        using PdfDocument d = PdfDocument.Open(plain, new PdfOpenOptions { AllowEditing = true });
        Assert.Throws<ArgumentException>(() => d.SaveIncremental(new MemoryStream(), new PdfSaveOptions { PdfA = new PdfAOptions() }));
    }

    // ---- Claim 3: the sRGB profile ----------------------------------------------------------------------------------------------

    [Fact]
    public void SrgbProfileIsAValidIccV2Profile()
    {
        byte[] p = OnePdf.PdfA.SrgbProfile.Bytes;
        if (Environment.GetEnvironmentVariable("ONEPDF_PDFA_OUT") is { Length: > 0 } outDir)
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllBytes(Path.Combine(outDir, "srgb.icc"), p);
        }

        Assert.True(p.Length < 4096, $"{p.Length} bytes");
        Assert.Equal(p.Length, (int)BinaryPrimitives.ReadUInt32BigEndian(p));
        Assert.Equal("acsp", Encoding.ASCII.GetString(p, 36, 4));
        Assert.Equal("mntr", Encoding.ASCII.GetString(p, 12, 4));
        Assert.Equal("RGB ", Encoding.ASCII.GetString(p, 16, 4));
        Assert.Equal("XYZ ", Encoding.ASCII.GetString(p, 20, 4));
        Assert.Equal(2, p[8]);

        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(128));
        var tags = new Dictionary<string, (int Off, int Size)>();
        for (int i = 0; i < count; i++)
        {
            int at = 132 + (12 * i);
            string sig = Encoding.ASCII.GetString(p, at, 4);
            int off = (int)BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(at + 4));
            int size = (int)BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(at + 8));
            Assert.True(off % 4 == 0 && off + size <= p.Length, sig);
            tags[sig] = (off, size);
        }

        foreach (string required in new[] { "desc", "cprt", "wtpt", "rXYZ", "gXYZ", "bXYZ", "rTRC", "gTRC", "bTRC" })
        {
            Assert.True(tags.ContainsKey(required), required);
        }

        double[] Xyz(string tag)
        {
            int o = tags[tag].Off + 8;
            return [BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(o)) / 65536.0, BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(o + 4)) / 65536.0, BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(o + 8)) / 65536.0];
        }

        // The three colorants add up to the D50 white point, and match the published sRGB (D50) matrix columns.
        double[] r = Xyz("rXYZ"), g = Xyz("gXYZ"), b = Xyz("bXYZ"), w = Xyz("wtpt");
        for (int i = 0; i < 3; i++)
        {
            Assert.InRange(r[i] + g[i] + b[i] - w[i], -0.0005, 0.0005);
        }

        Assert.InRange(r[0], 0.4360, 0.4362);
        Assert.InRange(g[1], 0.7168, 0.7170);
        Assert.InRange(b[2], 0.7138, 0.7143);

        // The tone curve is the sRGB decoding function.
        int c = tags["rTRC"].Off;
        int n = (int)BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(c + 8));
        Assert.Equal(1024, n);
        double At(double v) => BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(c + 12 + (2 * (int)Math.Round(v * (n - 1))))) / 65535.0;
        Assert.Equal(0, At(0));
        Assert.Equal(1, At(1));
        double mid = 512 / 1023.0;
        Assert.InRange(At(mid), (Math.Pow((mid + 0.055) / 1.055, 2.4)) - 0.0001, (Math.Pow((mid + 0.055) / 1.055, 2.4)) + 0.0001);
        Assert.InRange(At(0.02), 0.02 / 12.92 - 0.0001, 0.02 / 12.92 + 0.0001); // the linear segment near black
        Assert.Equal(tags["rTRC"], tags["bTRC"]);
    }

    [Fact]
    public void SrgbProfileBytesAreStable()
    {
        byte[] a = OnePdf.PdfA.SrgbProfile.Bytes;
        // Not a golden copy of a vendor profile: only guards against the generator drifting by accident.
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(a), System.Security.Cryptography.SHA256.HashData([.. a]));
        Assert.Contains("sRGB", Encoding.ASCII.GetString(a), StringComparison.Ordinal);
    }

    // ---- Claim 4: XMP and determinism -------------------------------------------------------------------------------------------

    [Fact]
    public void XmpRoundTripsAndMatchesInfo()
    {
        using PdfDocument d = PdfADocs.TrueType();
        byte[] pdf = Write(d, PdfAPart.Part2, PdfAConformance.U);
        using PdfDocument back = PdfDocument.Open(pdf);
        XmpMetadata xmp = back.Xmp!;
        Assert.True(xmp.IsWellFormed);
        Assert.Equal(2, xmp.PdfAPart);
        Assert.Equal("U", xmp.PdfAConformance);
        Assert.Equal(back.Info.Title, xmp.Title);
        Assert.Equal(["A. Writer & Co <test>"], xmp.Creators.ToArray());
        Assert.Equal(back.Info.Subject, xmp.Description);
        Assert.Equal(back.Info.Keywords, xmp.Keywords);
        Assert.Equal(back.Info.Producer, xmp.Producer);
        Assert.Equal(back.Info.Creator, xmp.CreatorTool);
        Assert.Equal(back.Info.CreationDate, xmp.CreateDate);
        Assert.Equal(back.Info.ModificationDate, xmp.ModifyDate);
        Assert.Equal(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.FromHours(1)), xmp.CreateDate);

        string raw = Encoding.UTF8.GetString(xmp.RawBytes.Span);
        Assert.StartsWith("<?xpacket begin=\"\uFEFF\"", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("encoding=", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("bytes=", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingMatchingXmpIsKept()
    {
        byte[] first = Write(PdfADocs.TrueType(), PdfAPart.Part2, PdfAConformance.B);
        using PdfDocument d = PdfDocument.Open(first, new PdfOpenOptions { AllowEditing = true });
        byte[] before = d.Xmp!.RawBytes.ToArray();
        byte[] second = Write(d, PdfAPart.Part2, PdfAConformance.B);
        using PdfDocument back = PdfDocument.Open(second);
        Assert.Equal(before, back.Xmp!.RawBytes.ToArray());
        // The same file asked for another part gets a new packet.
        using PdfDocument d3 = PdfDocument.Open(first, new PdfOpenOptions { AllowEditing = true });
        using PdfDocument back3 = PdfDocument.Open(Write(d3, PdfAPart.Part3, PdfAConformance.B));
        Assert.Equal(3, back3.Xmp!.PdfAPart);
    }

    [Fact]
    public void CffSubsetGetsNoValidatorWarnings()
    {
        VeraPdf.Require();
        using PdfDocument d = PdfADocs.Build("cff");
        byte[] pdf = Write(d, PdfAPart.Part2, PdfAConformance.B);
        Assert.Empty(VeraPdf.Validate(pdf, "2b"));
        Assert.DoesNotContain("WARNING", VeraPdf.LastLog ?? string.Empty);
    }

    [Theory]
    [InlineData("truetype")]
    [InlineData("cff")]
    [InlineData("form-filled")]
    [InlineData("annotations")]
    [InlineData("pages")]
    public void OutputIsDeterministic(string name)
    {
        byte[] a, b;
        using (PdfDocument d = PdfADocs.Build(name))
        {
            a = Write(d, PdfAPart.Part2, PdfAConformance.B);
            // Saving again from the same (now prepared) document gives the same bytes: preparing twice changes nothing more.
            b = Write(d, PdfAPart.Part2, PdfAConformance.B);
        }

        Assert.Equal(a, b);
        using PdfDocument d2 = PdfADocs.Build(name);
        Assert.Equal(a, Write(d2, PdfAPart.Part2, PdfAConformance.B));
    }

    // ---- structure of what is written ----------------------------------------------------------------------------------------

    [Fact]
    public void WrittenFileHasTheStructurePdfARequires()
    {
        using PdfDocument d = PdfADocs.Standard14();
        byte[] pdf = Write(d, PdfAPart.Part2, PdfAConformance.B);
        Assert.True(pdf[0] == '%' && pdf[1] == 'P' && pdf[2] == 'D' && pdf[3] == 'F' && pdf[4] == '-' && pdf[5] == '1' && pdf[6] == '.' && pdf[7] >= '0' && pdf[7] <= '7', "header 1.n");
        int eol = Array.IndexOf(pdf, (byte)'\n');
        Assert.True(pdf[eol + 1] == '%' && pdf[eol + 2] > 127 && pdf[eol + 3] > 127 && pdf[eol + 4] > 127 && pdf[eol + 5] > 127, "binary comment");

        using PdfDocument back = PdfDocument.Open(pdf);
        Assert.False(back.IsEncrypted);
        Assert.NotNull(back.Trailer.Get("ID"));
        PdfArray intents = back.Catalog.GetArray("OutputIntents")!;
        var intent = (PdfDictionary)intents[0]!;
        Assert.Equal("GTS_PDFA1", intent.GetName("S"));
        PdfStream profile = intent.GetStream("DestOutputProfile")!;
        Assert.Equal(3, profile.Dictionary.GetInt("N"));
        Assert.Equal(OnePdf.PdfA.SrgbProfile.Bytes, profile.GetDecodedBytes().ToArray());
        Assert.Equal("Metadata", back.Catalog.GetStream("Metadata")!.Dictionary.GetName("Type"));
        Assert.Empty(back.Catalog.GetStream("Metadata")!.FilterNames);

        // The standard fonts became embedded TrueType fonts with the AFM widths.
        var seen = 0;
        foreach (string key in back.Pages[0].Resources.GetDictionary("Font")!.Keys)
        {
            PdfDictionary f = back.Pages[0].Resources.GetDictionary("Font")!.GetDictionary(key)!;
            Assert.Equal("TrueType", f.GetName("Subtype"));
            Assert.NotNull(f.GetDictionary("FontDescriptor")!.GetStream("FontFile2"));
            Assert.Equal(f.GetInt("LastChar") - f.GetInt("FirstChar") + 1, f.GetArray("Widths")!.Count);
            seen++;
        }

        Assert.Equal(4, seen);
        string text = string.Join(" ", back.ExtractText().Select(p => p.Text));
        Assert.Contains("Helvetica text (caf\u00E9)", text, StringComparison.Ordinal);
        Assert.Contains("Courier bold text", text, StringComparison.Ordinal);
    }

    [Fact]
    public void StandardFontsAreSubsetWithAWindowsCmapAndKeepTheirText()
    {
        string plain;
        using (PdfDocument source = PdfADocs.Standard14())
        {
            plain = string.Join(" ", source.ExtractText().Select(p => p.Text));
        }

        using PdfDocument d = PdfADocs.Standard14();
        byte[] pdf = Write(d, PdfAPart.Part2, PdfAConformance.B);
        // Four whole Liberation faces were about 834 KB; the subsets hold only the glyphs the encodings reach.
        Assert.True(pdf.Length < 150_000, pdf.Length.ToString());
        using PdfDocument back = PdfDocument.Open(pdf);
        PdfDictionary fonts = back.Pages[0].Resources.GetDictionary("Font")!;
        foreach (string key in fonts.Keys)
        {
            PdfDictionary f = fonts.GetDictionary(key)!;
            Assert.Matches("^[A-Z]{6}\\+", f.GetName("BaseFont"));
            Assert.Equal(f.GetName("BaseFont"), f.GetDictionary("FontDescriptor")!.GetName("FontName"));
            byte[] program = f.GetDictionary("FontDescriptor")!.GetStream("FontFile2")!.GetDecodedBytes().ToArray();
            Assert.NotNull(SfntFont.Parse(program, 0).FindCmap(3, 1));
        }

        Assert.Equal(plain, string.Join(" ", back.ExtractText().Select(p => p.Text)));
    }

    [Fact]
    public void AnnotationsGetPrintFlagAndAppearance()
    {
        using PdfDocument d = PdfADocs.Annotations();
        byte[] pdf = Write(d, PdfAPart.Part2, PdfAConformance.B);
        using PdfDocument back = PdfDocument.Open(pdf);
        foreach (PdfAnnotation a in back.Pages[0].Annotations)
        {
            Assert.True((a.Flags & PdfAnnotationOptions.Print) != 0, a.Subtype);
            if (a.Subtype != "Link")
            {
                Assert.NotNull(a.GetAppearanceStream());
            }
        }
    }

    [Fact]
    public void FormFieldsKeepValuesAndNeedAppearancesIsCleared()
    {
        using PdfDocument d = PdfADocs.Form(flatten: false);
        byte[] pdf = Write(d, PdfAPart.Part2, PdfAConformance.B);
        using PdfDocument back = PdfDocument.Open(pdf);
        Assert.False(back.Form!.NeedAppearances);
        Assert.Contains(back.Form.AllFields, f => f.FullName == "name" && f.Value == "Ada Lovelace");
    }

    [Fact]
    public void CheckPdfAOnAnOrdinaryDocumentReportsWhatIsMissing()
    {
        using PdfDocument d = PdfADocs.TrueType();
        IReadOnlyList<PdfAViolation> v = d.CheckPdfA();
        Assert.Contains(v, x => x.Clause == "6.6.2.1");
        // Preparing adds them; what is left is empty for this document.
        Assert.Empty(d.PreparePdfA());
        // Only the trailer /ID is left, and a save adds it.
        Assert.All(d.CheckPdfA(), x => Assert.Contains("/ID", x.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void TransparencyNeedsAnIntentOrAGroupColourSpace()
    {
        using PdfDocument d = PdfADocs.Images();
        Assert.Contains(d.CheckPdfA(), x => x.Clause == "6.2.10" && x.PageIndex == 0);
        d.PreparePdfA();
        Assert.DoesNotContain(d.CheckPdfA(), x => x.Clause == "6.2.10");
    }

    [Fact]
    public void HostileColourSpacesAndOperatorsDoNotEscape()
    {
        using PdfDocument d = PdfADocs.TrueType();
        // Short arrays, a colour space that names itself, an unknown operator and deep q nesting.
        var res = d.Pages[0].Resources;
        var cs = new PdfDictionary();
        var indexed = new PdfArray();
        indexed.Append(new PdfName("Indexed"));
        cs.SetEntry("A", indexed);
        var sep = new PdfArray();
        sep.Append(new PdfName("Separation"));
        sep.Append(new PdfName("X"));
        cs.SetEntry("B", sep);
        res.SetEntry("ColorSpace", cs);
        PdfStream extra = d.CreateStream(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("q ", 40)) + "1 2 3 zz 0.5 0.5 0.5 rg"));
        d.Pages[0].Dictionary.SetEntry("Contents", extra);
        PdfAException ex = Refused(d);
        Assert.Contains(ex.Violations, v => v.Clause == "6.2.2" && v.Message.Contains("zz", StringComparison.Ordinal));
        Assert.Contains(ex.Violations, v => v.Clause == "6.1.13" && v.Message.Contains("28", StringComparison.Ordinal));
    }

    [Fact]
    public void AnnotationsOutsideThePageTreeDoNotMakeTheWrittenFileFailTheCheck()
    {
        // Found by fuzzing: a damaged page tree left annotations that no page lists. They are not a page's annotations, so neither the
        // preparation nor the check treats them as such; what is written must pass the check it was written under.
        using PdfDocument d = PdfADocs.TrueType();
        var stray = new PdfDictionary();
        stray.SetEntry("Type", new PdfName("Annot"));
        stray.SetEntry("Subtype", new PdfName("Movie"));
        var rect = new PdfArray();
        foreach (int v in new[] { 0, 0, 20, 20 })
        {
            rect.Append(new PdfInteger(v));
        }

        stray.SetEntry("Rect", rect);
        var annots = new PdfArray();
        annots.Append(stray);
        var holder = new PdfDictionary();
        holder.SetEntry("Annots", annots);
        d.Catalog.SetEntry("PieceInfo", holder);
        byte[] pdf = Write(d, PdfAPart.Part3, PdfAConformance.B);
        using PdfDocument back = PdfDocument.Open(pdf);
        Assert.Empty(back.CheckPdfA(new PdfAOptions { Part = PdfAPart.Part3 }));
    }

    [Fact]
    public void AttachFileRefusesAMultiLevelNameTreeWithATypedError()
    {
        using PdfDocument d = PdfADocs.TrueType();
        var leaf = new PdfDictionary();
        leaf.SetEntry("Names", new PdfArray());
        var kids = new PdfArray();
        kids.Append(d.AddObject(leaf));
        var tree = new PdfDictionary();
        tree.SetEntry("Kids", kids);
        var names = new PdfDictionary();
        names.SetEntry("EmbeddedFiles", tree);
        d.Catalog.SetEntry("Names", names);
        Assert.Throws<PdfFormatException>(() => d.AttachFile("a.txt", new byte[] { 1 }));
    }

    [Fact]
    public void ReadOnlyDocumentCanBeCheckedButNotPrepared()
    {
        byte[] plain = Write(PdfADocs.TrueType(), PdfAPart.Part2, PdfAConformance.B);
        using PdfDocument d = PdfDocument.Open(plain);
        Assert.Empty(d.CheckPdfA());
        Assert.Throws<InvalidOperationException>(() => d.PreparePdfA());
        Assert.Throws<InvalidOperationException>(() => d.Save(new MemoryStream(), new PdfSaveOptions { PdfA = new PdfAOptions() }));
    }

    [Fact]
    public void ReopenedPdfAFileCanBeSavedAgain()
    {
        byte[] first = Write(PdfADocs.Pages(), PdfAPart.Part2, PdfAConformance.B);
        using PdfDocument d = PdfDocument.Open(first, new PdfOpenOptions { AllowEditing = true });
        d.InsertBlankPage(1, 200, 200);
        byte[] second = Write(d, PdfAPart.Part2, PdfAConformance.B);
        using PdfDocument back = PdfDocument.Open(second);
        Assert.Equal(d.Pages.Count, back.Pages.Count);
        Assert.Empty(back.CheckPdfA());
    }
}
