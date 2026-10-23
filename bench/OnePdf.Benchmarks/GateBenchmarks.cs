using BenchmarkDotNet.Attributes;

namespace OnePdf.Benchmarks;

// Gate rows. Every class compares OnePDF with PdfPig on the same file in the same process. "Degree" is "1" or "default"
// (min(4, cores)); rows with a degree are reported separately per degree, never merged. Open and metadata work is single-threaded
// in both libraries, so those rows have no degree. Methods marked [Pending] are skipped until their prerequisite exists.

/// <summary>Open + page count: a small file, a 1,000-page file, and a file whose cross-reference data is gone and must be rebuilt.</summary>
[MemoryDiagnoser]
[BenchmarkCategory("gate", "open")]
public class OpenBenchmarks
{
    [Params("small", "pages-1000", "broken")]
    public string File { get; set; } = "";

    private string _path = "";

    [GlobalSetup]
    public void Setup() => _path = BenchData.Resolve(File);

    [Benchmark(Baseline = true)]
    public long OnePdf() => Ops.OnePdfOpen(_path);

    [Benchmark]
    public long PdfPig() => Ops.PdfPigOpen(_path);
}

/// <summary>Metadata: open, then the Info dictionary fields (and XMP where the library has it).</summary>
[MemoryDiagnoser]
[BenchmarkCategory("gate", "metadata")]
public class MetadataBenchmarks
{
    [Params("small-metadata", "born-300")]
    public string File { get; set; } = "";

    private string _path = "";

    [GlobalSetup]
    public void Setup() => _path = BenchData.Resolve(File);

    [Benchmark(Baseline = true)]
    public long OnePdf() => Ops.OnePdfMetadata(_path);

    [Benchmark]
    public long PdfPig() => Ops.PdfPigMetadata(_path);
}

/// <summary>Full plain-text extraction of a 300-page born-digital file, a scanned file with an OCR text layer and a CJK file.</summary>
[MemoryDiagnoser]
[BenchmarkCategory("gate", "text")]
public class TextBenchmarks
{
    [Params("born-300", "scan-ocr", "cjk")]
    public string File { get; set; } = "";

    /// <summary>"1", or "default" for min(4, cores). At "default" on born-300 this is the whole-document parallel row.</summary>
    [Params("1", "default")]
    public string Degree { get; set; } = "1";

    private string _path = "";
    private int _degree;

    [GlobalSetup]
    public void Setup()
    {
        _path = BenchData.Resolve(File);
        _degree = BenchData.Degree(Degree);
    }

    [Benchmark(Baseline = true)]
    [Pending("text")]
    public long OnePdf() => Ops.OnePdfText(_path, _degree);

    /// <summary>Information row: the same extraction with the glyphs kept in the result (the single-page default), as an application that reads them would run it.</summary>
    [Benchmark]
    [Pending("text")]
    public long OnePdfKeepingGlyphs() => Ops.OnePdfText(_path, _degree, null, retainGlyphs: true);

    [Benchmark]
    public long PdfPig() => Ops.PdfPigText(_path, _degree);
}

/// <summary>Words with positions.</summary>
[MemoryDiagnoser]
[BenchmarkCategory("gate", "words")]
public class WordsBenchmarks
{
    [Params("born-300", "text-page")]
    public string File { get; set; } = "";

    [Params("1", "default")]
    public string Degree { get; set; } = "1";

    private string _path = "";
    private int _degree;

    [GlobalSetup]
    public void Setup()
    {
        _path = BenchData.Resolve(File);
        _degree = BenchData.Degree(Degree);
    }

    [Benchmark(Baseline = true)]
    [Pending("text")]
    public long OnePdf() => Ops.OnePdfWords(_path, _degree);

    [Benchmark]
    public long PdfPig() => Ops.PdfPigWords(_path, _degree);
}

/// <summary>AES-256 (revision 6) encrypted file: open with the user password.</summary>
[MemoryDiagnoser]
[BenchmarkCategory("gate", "encrypted")]
public class EncryptedOpenBenchmarks
{
    private string _path = "";

    [GlobalSetup]
    public void Setup() => _path = BenchData.Resolve("aes256");

    [Benchmark(Baseline = true)]
    public long OnePdf() => Ops.OnePdfOpen(_path, BenchData.EncryptedPassword);

    [Benchmark]
    public long PdfPig() => Ops.PdfPigOpen(_path, BenchData.EncryptedPassword);
}

/// <summary>AES-256 encrypted file: full text extraction.</summary>
[MemoryDiagnoser]
[BenchmarkCategory("gate", "encrypted", "text")]
public class EncryptedTextBenchmarks
{
    [Params("1", "default")]
    public string Degree { get; set; } = "1";

    private string _path = "";
    private int _degree;

    [GlobalSetup]
    public void Setup()
    {
        _path = BenchData.Resolve("aes256");
        _degree = BenchData.Degree(Degree);
    }

    [Benchmark(Baseline = true)]
    [Pending("text")]
    public long OnePdf() => Ops.OnePdfText(_path, _degree, BenchData.EncryptedPassword);

    [Benchmark]
    public long PdfPig() => Ops.PdfPigText(_path, _degree, BenchData.EncryptedPassword);
}

/// <summary>Information only: interpretation of one page into a display list.</summary>
[MemoryDiagnoser]
[BenchmarkCategory("info", "interpret")]
public class InterpretBenchmarks
{
    [Params("vector-page", "text-page")]
    public string File { get; set; } = "";

    private string _path = "";

    [GlobalSetup]
    public void Setup() => _path = BenchData.Resolve(File);

    [Benchmark]
    public long OnePdfDisplayList() => Ops.OnePdfInterpret(_path);
}

/// <summary>Information only: PDFium (through Docnet.Core) and mutool text extraction, for the published speed goal. mutool times include starting the process.</summary>
[MemoryDiagnoser]
[BenchmarkCategory("info", "text")]
public class OtherEnginesTextBenchmarks
{
    [Params("born-300", "scan-ocr", "cjk")]
    public string File { get; set; } = "";

    private string _path = "";

    [GlobalSetup]
    public void Setup() => _path = BenchData.Resolve(File);

    [Benchmark]
    public long PdfiumDocnet() => Ops.PdfiumText(_path);

    [Benchmark]
    [Pending("mutool")]
    public long Mutool() => Ops.MutoolText(_path);
}
