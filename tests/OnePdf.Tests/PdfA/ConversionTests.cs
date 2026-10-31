using System.Text;
using OnePdf.Tests.Creation;

namespace OnePdf.Tests.PdfA;

/// <summary>
/// Converting existing files to PDF/A: one hand-built file per repair, the refusal path, and a few committed corpus files validated by
/// veraPDF when ONEPDF_VERAPDF is set. A converted file must open, pass OnePDF's own check, and carry the change in the result.
/// </summary>
public class ConversionTests
{
    private static readonly PdfAConversionOptions s_options = new() { FontProvider = OnePdf.Fonts.FontsPackage.Provider };

    // One page (object 3) with the given page entries, resources and content; extra objects numbered from 10.
    private static byte[] Build(string content, string resources = "", string pageExtra = "", string catalogExtra = "", params (int Number, string Body)[] extra)
    {
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R " + catalogExtra + " >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        b.Obj(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R /Resources << {resources} >> {pageExtra} >>");
        b.Stream(4, string.Empty, Encoding.Latin1.GetBytes(content));
        int size = 5;
        foreach ((int number, string body) in extra)
        {
            b.Obj(number, body);
            size = Math.Max(size, number + 1);
        }

        long xref = b.XrefTable(size);
        return b.Trailer($"/Size {size} /Root 1 0 R", xref).ToArray();
    }

    private static (byte[] Output, PdfAConversionResult Result) Convert(byte[] input, PdfAConversionOptions? options = null)
    {
        using var inStream = new MemoryStream(input);
        using var outStream = new MemoryStream();
        PdfAConversionResult result = PdfAConverter.Convert(inStream, outStream, options ?? s_options);
        return (outStream.ToArray(), result);
    }

    private static void AssertConforms(byte[] output, string flavour = "2b")
    {
        using PdfDocument doc = PdfDocument.Open(output);
        Assert.Empty(doc.CheckPdfA(new PdfAOptions { Part = flavour[0] == '3' ? PdfAPart.Part3 : PdfAPart.Part2, Conformance = flavour[1] == 'u' ? PdfAConformance.U : PdfAConformance.B }));
        if (VeraPdf.Path is not null)
        {
            Assert.Empty(VeraPdf.Validate(output, flavour));
        }
    }

    private static bool Has(PdfAConversionResult r, PdfAChangeKind kind) => r.Changes.Any(c => c.Kind == kind);

    [Fact]
    public void JavaScriptAndAdditionalActionsAreRemoved()
    {
        byte[] input = Build("0 0 100 100 re f", pageExtra: "/AA << /O << /S /JavaScript /JS (app.alert(1)) >> >>",
            catalogExtra: "/OpenAction << /S /JavaScript /JS (app.alert(2)) >>");
        (byte[] output, PdfAConversionResult result) = Convert(input);
        Assert.True(Has(result, PdfAChangeKind.ActionsRemoved));
        using (PdfDocument doc = PdfDocument.Open(output))
        {
            Assert.False(doc.Catalog.ContainsKey("OpenAction"));
            Assert.Null(doc.Pages[0].Dictionary.Get("AA"));
        }

        AssertConforms(output);
    }

    [Fact]
    public void EncryptedFileNeedsItsPasswordAndComesOutPlain()
    {
        using var plain = PdfDocument.CreateEmpty();
        plain.InsertBlankPage(0, 200, 200);
        using var enc = new MemoryStream();
        plain.Save(enc, new PdfSaveOptions
        {
            Encryption = PdfEncryptionAction.Set,
            NewEncryption = new PdfEncryptionOptions { UserPassword = "secret", OwnerPassword = "boss", Cipher = PdfCipher.Aes128 },
        });
        byte[] input = enc.ToArray();
        Assert.Throws<PdfPasswordException>(() => Convert(input));

        (byte[] output, PdfAConversionResult result) = Convert(input, new PdfAConversionOptions { FontProvider = OnePdf.Fonts.FontsPackage.Provider, Password = "secret" });
        Assert.True(Has(result, PdfAChangeKind.EncryptionRemoved));
        using (PdfDocument doc = PdfDocument.Open(output))
        {
            Assert.False(doc.IsEncrypted);
        }

        AssertConforms(output);
    }

    [Fact]
    public void NonEmbeddedStandardFontGetsASubstituteProgram()
    {
        byte[] input = Build("BT /F1 18 Tf 20 200 Td (Hello, PDF/A) Tj ET",
            resources: "/Font << /F1 10 0 R >>",
            extra: (10, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));
        (byte[] output, PdfAConversionResult result) = Convert(input);
        Assert.True(Has(result, PdfAChangeKind.FontsEmbedded));
        using (PdfDocument doc = PdfDocument.Open(output))
        {
            PdfDictionary font = doc.Pages[0].Resources.GetDictionary("Font")!.GetDictionary("F1")!;
            Assert.NotNull(font.GetDictionary("FontDescriptor")!.GetStream("FontFile2"));
            Assert.Contains("Hello, PDF/A", string.Join(" ", doc.ExtractText().Select(p => p.Text)), StringComparison.Ordinal);
        }

        AssertConforms(output);
    }

    [Fact]
    public void NonEmbeddedStandardFontOfLevelUGetsAUnicodeMap()
    {
        byte[] input = Build("BT /F1 18 Tf 20 200 Td (Level U) Tj ET",
            resources: "/Font << /F1 10 0 R >>",
            extra: (10, "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman >>"));
        (byte[] output, _) = Convert(input, new PdfAConversionOptions { FontProvider = OnePdf.Fonts.FontsPackage.Provider, Conformance = PdfAConformance.U });
        AssertConforms(output, "2u");
    }

    [Theory]
    [InlineData("1 0 0 rg 20 20 100 100 re f")]
    [InlineData("0 0 0 1 k 20 20 100 100 re f")]
    [InlineData("0.5 g 20 20 100 100 re f 1 0 0 rg 150 20 50 50 re f 0 1 0 0 k 150 150 50 50 re f")]
    public void DeviceColoursComeUnderAnOutputIntent(string content)
    {
        (byte[] output, PdfAConversionResult result) = Convert(Build(content));
        Assert.True(Has(result, PdfAChangeKind.OutputIntentAdded));
        using (PdfDocument doc = PdfDocument.Open(output))
        {
            Assert.NotNull(doc.Catalog.GetArray("OutputIntents"));
        }

        AssertConforms(output);
    }

    [Fact]
    public void ForbiddenAnnotationIsRemoved()
    {
        byte[] input = Build("0 0 50 50 re f", pageExtra: "/Annots [10 0 R 11 0 R]",
            extra: [(10, "<< /Type /Annot /Subtype /Sound /Rect [10 10 50 50] /F 4 >>"),
                    (11, "<< /Type /Annot /Subtype /Link /Rect [60 60 90 90] /Border [0 0 0] /F 4 /A << /S /URI /URI (https://example.org/) >> >>")]);
        (byte[] output, PdfAConversionResult result) = Convert(input);
        Assert.True(Has(result, PdfAChangeKind.AnnotationsRemoved));
        using (PdfDocument doc = PdfDocument.Open(output))
        {
            Assert.Single(doc.Pages[0].Annotations);
        }

        AssertConforms(output);
    }

    [Fact]
    public void ExternalStreamReferenceIsDropped()
    {
        // A form whose data is meant to come from another file.
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R /Resources << /XObject << /Fm1 5 0 R >> >> >>");
        b.Stream(4, string.Empty, Encoding.Latin1.GetBytes("/Fm1 Do"));
        b.Stream(5, "/Type /XObject /Subtype /Form /BBox [0 0 100 100] /F (outside.dat)", Encoding.Latin1.GetBytes("0 0 40 40 re f"));
        long xref = b.XrefTable(6);
        byte[] file = b.Trailer("/Size 6 /Root 1 0 R", xref).ToArray();

        (byte[] output, PdfAConversionResult result) = Convert(file);
        Assert.True(Has(result, PdfAChangeKind.ExternalReferencesRemoved));
        using (PdfDocument doc = PdfDocument.Open(output))
        {
            PdfStream form = Assert.IsType<PdfStream>(doc.Pages[0].Resources.GetDictionary("XObject")!.Get("Fm1"));
            Assert.False(form.Dictionary.ContainsKey("F"));
        }

        AssertConforms(output);
    }

    [Fact]
    public void LzwStreamsAreStoredAgainWithFlate()
    {
        byte[] plain = Encoding.Latin1.GetBytes("0.2 0.4 0.6 rg 20 20 120 120 re f");
        byte[] lzw = LzwLiteral(plain);
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R >>");
        b.Stream(4, "/Filter /LZWDecode", lzw);
        long xref = b.XrefTable(5);
        byte[] input = b.Trailer("/Size 5 /Root 1 0 R", xref).ToArray();

        (byte[] output, PdfAConversionResult result) = Convert(input);
        Assert.True(Has(result, PdfAChangeKind.StreamsRecompressed));
        using (PdfDocument doc = PdfDocument.Open(output))
        {
            PdfStream content = Assert.IsType<PdfStream>(doc.Pages[0].Dictionary.Get("Contents"));
            Assert.DoesNotContain("LZWDecode", content.FilterNames);
            Assert.Equal(plain, content.GetDecodedBytes().ToArray());
        }

        AssertConforms(output);
    }

    // LZW with every byte as a literal 9-bit code between a clear code and the end code; valid for data shorter than the point where codes widen.
    private static byte[] LzwLiteral(byte[] data)
    {
        Assert.True(data.Length < 250);
        var bits = new List<bool>();
        void put(int code)
        {
            for (int i = 8; i >= 0; i--)
            {
                bits.Add(((code >> i) & 1) != 0);
            }
        }

        put(256);
        foreach (byte x in data)
        {
            put(x);
        }

        put(257);
        var bytes = new byte[(bits.Count + 7) / 8];
        for (int i = 0; i < bits.Count; i++)
        {
            if (bits[i])
            {
                bytes[i / 8] |= (byte)(0x80 >> (i % 8));
            }
        }

        return bytes;
    }

    [Fact]
    public void UnconvertibleFileIsRefusedWithReasonsAndNothingIsWritten()
    {
        // Graphics states nested deeper than PDF/A allows cannot be flattened without rewriting the page.
        byte[] input = Build(string.Concat(Enumerable.Repeat("q ", 40)) + "0 0 10 10 re f " + string.Concat(Enumerable.Repeat("Q ", 40)));
        using var inStream = new MemoryStream(input);
        using var outStream = new MemoryStream();
        PdfAException ex = Assert.Throws<PdfAException>(() => PdfAConverter.Convert(inStream, outStream, s_options));
        Assert.NotEmpty(ex.Violations);
        Assert.Equal(0, outStream.Length);

        string dir = Directory.CreateTempSubdirectory("onepdf-conv").FullName;
        try
        {
            string inPath = System.IO.Path.Combine(dir, "in.pdf");
            string outPath = System.IO.Path.Combine(dir, "out.pdf");
            File.WriteAllBytes(inPath, input);
            File.WriteAllText(outPath, "previous output");
            Assert.Throws<PdfAException>(() => PdfAConverter.Convert(inPath, outPath, s_options));
            Assert.Equal("previous output", File.ReadAllText(outPath));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LevelAIsAnArgumentError()
    {
        byte[] input = Build("0 0 10 10 re f");
        Assert.Throws<ArgumentException>(() => Convert(input, new PdfAConversionOptions { Conformance = PdfAConformance.A }));
    }

    [Fact]
    public void EmbeddedFilesAreRemovedInPart2AndKeptInPart3()
    {
        byte[] data = Encoding.Latin1.GetBytes("attachment bytes");
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(a.txt) 10 0 R] >> >> >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>");
        b.Obj(10, "<< /Type /Filespec /F (a.txt) /EF << /F 11 0 R >> >>");
        b.Stream(11, "/Type /EmbeddedFile", data);
        long xref = b.XrefTable(12);
        byte[] input = b.Trailer("/Size 12 /Root 1 0 R", xref).ToArray();

        (byte[] two, PdfAConversionResult r2) = Convert(input);
        Assert.True(Has(r2, PdfAChangeKind.EmbeddedFilesRemoved));
        AssertConforms(two);

        (byte[] three, PdfAConversionResult r3) = Convert(input, new PdfAConversionOptions { FontProvider = OnePdf.Fonts.FontsPackage.Provider, Part = PdfAPart.Part3 });
        Assert.True(Has(r3, PdfAChangeKind.EmbeddedFilesCompleted));
        AssertConforms(three, "3b");
    }

    public static TheoryData<string> CorpusFiles =>
    [
        "testdata/govdocs/000162.pdf",
        "testdata/govdocs/000594.pdf",
        "testdata/pdf20examples/PDF 2.0 UTF-8 string and annotation.pdf",
        "testdata/realworld/pdf/cjk-01-zen14166681.pdf",
        "testdata/realworld/pdf/multi-column-02-arx30132.pdf",
    ];

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void CorpusFilesConvertAndValidate(string relative)
    {
        VeraPdf.Require();
        string path = System.IO.Path.Combine(CreationSupport.Root(), relative);
        if (!File.Exists(path))
        {
            Assert.Skip("Corpus file not present: " + relative);
        }

        using var output = new MemoryStream();
        using var input = File.OpenRead(path);
        PdfAConversionResult result;
        try
        {
            result = PdfAConverter.Convert(input, output, s_options);
        }
        catch (PdfAException ex)
        {
            // A refusal is a valid outcome; it must say why and write nothing.
            Assert.NotEmpty(ex.Violations);
            Assert.Equal(0, output.Length);
            return;
        }

        Assert.StartsWith("PDF/A-2b", result.Profile, StringComparison.Ordinal);
        Assert.Empty(VeraPdf.Validate(output.ToArray(), "2b"));
    }
}
