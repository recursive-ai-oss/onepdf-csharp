using System.Runtime.CompilerServices;

namespace OnePdf.Tests;

public class Pdf20ExampleTests
{
    // Build output lives outside the checkout, so the examples are found from this source file's location.
    private static string? Dir([CallerFilePath] string source = "")
    {
        string? env = Environment.GetEnvironmentVariable("ONEPDF_PDF20_DIR");
        if (env is not null && Directory.Exists(env))
        {
            return env;
        }

        for (string? d = Path.GetDirectoryName(source); d is not null; d = Path.GetDirectoryName(d))
        {
            string c = Path.Combine(d, "testdata", "pdf20examples");
            if (Directory.Exists(c))
            {
                return c;
            }
        }

        return null;
    }

    [Fact]
    public void ExamplesOpenCleanlyWithOnePage()
    {
        string? dir = Dir();
        Assert.SkipWhen(dir is null, "testdata/pdf20examples is not present");
        var files = Directory.GetFiles(dir!, "*.pdf");
        Assert.NotEmpty(files);
        foreach (string f in files)
        {
            using PdfDocument doc = PdfDocument.Open(f);
            Assert.True(doc.PageCount >= 1, f);
            Assert.DoesNotContain(doc.Diagnostics, d => d.Severity >= PdfSeverity.Warning);
        }
    }

    [Fact]
    public void IncrementalExampleExposesBothRevisions()
    {
        string? dir = Dir();
        Assert.SkipWhen(dir is null, "testdata/pdf20examples is not present");
        using PdfDocument doc = PdfDocument.Open(Path.Combine(dir!, "PDF 2.0 via incremental save.pdf"));
        Assert.Equal(2, doc.Revisions.Count);
        Assert.NotEqual(doc.Revisions[0].EndOffset, doc.Revisions[1].EndOffset);
        Assert.Equal(new FileInfo(Path.Combine(dir!, "PDF 2.0 via incremental save.pdf")).Length, doc.Revisions[1].EndOffset);
        Assert.Equal(1, doc.Revisions[1].PageCount);
    }

    [Fact]
    public void OffsetStartExampleOpens()
    {
        string? dir = Dir();
        Assert.SkipWhen(dir is null, "testdata/pdf20examples is not present");
        using PdfDocument doc = PdfDocument.Open(Path.Combine(dir!, "PDF 2.0 with offset start.pdf"));
        Assert.Equal(1, doc.PageCount);
        Assert.Equal("2.0", doc.Version);
    }
}
